using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Csfd.Configuration;
using MediaBrowser.Common.Net;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Csfd.Api;

/// <summary>
/// Klient na csfd-api sidecar. Všetky požiadavky idú sériovo s minimálnym rozostupom
/// (šetrí ČSFD aj Anubis proof-of-work) a výsledky sa ukladajú do diskovej cache.
/// </summary>
public sealed class CsfdApiClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString
    };

    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static DateTime _lastRequestUtc = DateTime.MinValue;

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<CsfdApiClient> _logger;

    public CsfdApiClient(IHttpClientFactory httpClientFactory, ILogger<CsfdApiClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    private static PluginConfiguration Config => Plugin.Instance?.Configuration ?? new PluginConfiguration();

    /// <summary>Detail titulu. <paramref name="language"/> je sk, cs alebo en.</summary>
    public Task<CsfdMovie?> GetMovieAsync(int id, string language, CancellationToken cancellationToken, bool bypassCache = false)
    {
        var path = $"/movie/{id}?language={language}";
        var ttl = bypassCache ? TimeSpan.Zero : TimeSpan.FromDays(Math.Max(1, Config.CacheDays));
        return GetAsync<CsfdMovie>(path, $"movie-{language}-{id}", ttl, cancellationToken);
    }

    /// <summary>Vyhľadávanie filmov a seriálov.</summary>
    public Task<CsfdSearchResult?> SearchAsync(string query, CancellationToken cancellationToken)
    {
        // Typ (film/seriál) parsuje csfd-api len z českých štítkov → hľadáme na CZ stránke.
        var path = $"/search/{Uri.EscapeDataString(query)}?language=cs";
        return GetAsync<CsfdSearchResult>(path, "search-" + Hash(query.ToLowerInvariant()), TimeSpan.FromDays(7), cancellationToken);
    }

    /// <summary>Stiahne obrázok (plagát / fotku) – obrázky idú z image.pmgstatic.com, nie cez sidecar.</summary>
    public Task<HttpResponseMessage> GetImageResponseAsync(string url, CancellationToken cancellationToken)
        => _httpClientFactory.CreateClient(NamedClient.Default).GetAsync(new Uri(url), cancellationToken);

    private async Task<T?> GetAsync<T>(string path, string cacheKey, TimeSpan ttl, CancellationToken cancellationToken)
        where T : class
    {
        var cacheFile = GetCacheFile(cacheKey);
        if (cacheFile is not null && File.Exists(cacheFile) && DateTime.UtcNow - File.GetLastWriteTimeUtc(cacheFile) < ttl)
        {
            try
            {
                await using var fs = File.OpenRead(cacheFile);
                var cached = await JsonSerializer.DeserializeAsync<T>(fs, JsonOptions, cancellationToken).ConfigureAwait(false);
                if (cached is not null)
                {
                    return cached;
                }
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                _logger.LogDebug(ex, "ČSFD cache {File} je poškodená, sťahujem znova", cacheFile);
            }
        }

        var json = await FetchAsync(path, cancellationToken).ConfigureAwait(false);
        if (json is null)
        {
            return null;
        }

        T? result;
        try
        {
            result = JsonSerializer.Deserialize<T>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "ČSFD: neplatná odpoveď pre {Path}", path);
            return null;
        }

        if (result is not null && cacheFile is not null)
        {
            try
            {
                var tmp = cacheFile + ".tmp";
                await File.WriteAllTextAsync(tmp, json, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
                File.Move(tmp, cacheFile, true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogDebug(ex, "ČSFD: nepodarilo sa zapísať cache {File}", cacheFile);
            }
        }

        return result;
    }

    private async Task<string?> FetchAsync(string path, CancellationToken cancellationToken)
    {
        var config = Config;
        if (string.IsNullOrWhiteSpace(config.ApiUrl))
        {
            _logger.LogWarning("ČSFD: v nastaveniach pluginu chýba URL csfd-api");
            return null;
        }

        var uri = new Uri(config.ApiUrl.TrimEnd('/') + path);

        for (var attempt = 1; attempt <= 2; attempt++)
        {
            await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var wait = _lastRequestUtc.AddMilliseconds(Math.Max(500, config.RequestDelayMs)) - DateTime.UtcNow;
                if (wait > TimeSpan.Zero)
                {
                    await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
                }

                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                if (!string.IsNullOrWhiteSpace(config.ApiKey))
                {
                    request.Headers.TryAddWithoutValidation("x-api-key", config.ApiKey.Trim());
                }

                var client = _httpClientFactory.CreateClient(NamedClient.Default);
                using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
                _lastRequestUtc = DateTime.UtcNow;

                if (response.IsSuccessStatusCode)
                {
                    return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                }

                if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Unauthorized or HttpStatusCode.BadRequest)
                {
                    _logger.LogWarning("ČSFD: {Status} pre {Path}", (int)response.StatusCode, path);
                    return null;
                }

                _logger.LogInformation("ČSFD: {Status} pre {Path} (pokus {Attempt}/2)", (int)response.StatusCode, path, attempt);
            }
            catch (HttpRequestException ex)
            {
                _lastRequestUtc = DateTime.UtcNow;
                _logger.LogWarning(ex, "ČSFD: csfd-api nedostupné ({Uri})", config.ApiUrl);
                return null;
            }
            catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                // HttpClient timeout – nesmie zhodiť refresh celej položky.
                _lastRequestUtc = DateTime.UtcNow;
                _logger.LogWarning(ex, "ČSFD: timeout pre {Path}", path);
                return null;
            }
            finally
            {
                Gate.Release();
            }

            if (attempt < 2)
            {
                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
            }
        }

        return null;
    }

    private string? GetCacheFile(string key)
    {
        var dataFolder = Plugin.Instance?.DataFolderPath;
        if (string.IsNullOrEmpty(dataFolder))
        {
            return null;
        }

        try
        {
            var dir = Path.Combine(dataFolder, "cache");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, key + ".json");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "ČSFD: cache priečinok nedostupný");
            return null;
        }
    }

    private static string Hash(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..16].ToLowerInvariant();
}
