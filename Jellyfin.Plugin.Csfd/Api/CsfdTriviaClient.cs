using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Csfd.Api;

/// <summary>
/// Zaujímavosti k filmu/seriálu zo stránky csfd.sk/film/{id}/zaujimavosti/ (sidecar csfd-api ich nemá).
/// Držia sa v cache na disku – zaujímavosti pribúdajú zriedka.
/// </summary>
public sealed class CsfdTriviaClient
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromDays(30);

    /// <summary>
    /// Stránka sa načítala, ale zaujímavosti na ČSFD nie sú – skúsime znova o týždeň.
    /// (Predtým 1 deň: s rezervou prednačítania 3 dni sa takéto tituly sťahovali každú noc – ~360 zbytočných požiadaviek.)
    /// </summary>
    private static readonly TimeSpan EmptyCacheTtl = TimeSpan.FromDays(7);

    /// <summary>Po chybe sťahovania (ČSFD neodpovedá, Anubis) so starou cache sa skúsi znova o deň.</summary>
    private static readonly TimeSpan RetryAfterFailure = TimeSpan.FromDays(1);

    /// <summary>Najkratší rozostup medzi požiadavkami na ČSFD (aj medzi SK a CZ stránkou jedného filmu).</summary>
    internal static readonly TimeSpan MinRequestGap = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Jeden klient pre všetky filmy: poctivé bot UA (Anubis ho nezastaví) a ak by predsa prišla výzva,
    /// jej cookie (platí 7 dní) ostane v CookieContainer pre ďalšie filmy tej istej noci.
    /// </summary>
    private static readonly Lazy<HttpClient> SharedHttp = new(() => CsfdTvTipsClient.CreateHttpClient(asBot: true));

    private static DateTime _lastRequestAt = DateTime.MinValue;

    private static int _failures;

    /// <summary>Počet neúspešných stiahnutí od štartu (sieť, HTTP chyba, Anubis) – nočné prednačítanie z neho počíta rozdiel.</summary>
    public static int FailureCount => Volatile.Read(ref _failures);

    private static readonly Regex ItemRx = new(@"<li[^>]*>(.*?)</li>", RegexOptions.Compiled | RegexOptions.Singleline);
    private static readonly Regex AuthorRx = new(@"\(\s*<a[^>]*href=""/uzivatel/[^>]*>.*?</a>\s*\)", RegexOptions.Compiled | RegexOptions.Singleline);
    private static readonly Regex TagRx = new(@"<[^>]+>", RegexOptions.Compiled);
    private static readonly Regex SpaceRx = new(@"\s+", RegexOptions.Compiled);

    /// <summary>Strop pamäťovej cache – disková cache stačí, pamäť je len skratka pre častí opakované otvorenia.</summary>
    private const int MemoryLimit = 2000;

    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly ConcurrentDictionary<int, (DateTime At, List<string> Items)> Memory = new();

    private readonly ILogger<CsfdTriviaClient> _logger;

    public CsfdTriviaClient(ILogger<CsfdTriviaClient> logger)
    {
        _logger = logger;
    }

    /// <summary>Zaujímavosti v poradí z ČSFD (najlepšie hodnotené prvé); prázdny zoznam, ak žiadne nie sú alebo ČSFD neodpovedá.</summary>
    public Task<IReadOnlyList<string>> GetTriviaAsync(int csfdId, CancellationToken cancellationToken)
        => GetTriviaAsync(csfdId, TimeSpan.Zero, cancellationToken);

    /// <summary>
    /// Ako <see cref="GetTriviaAsync(int, CancellationToken)"/>, ale cache, ktorej platnosť skončí do <paramref name="refreshMargin"/>,
    /// sa stiahne znova už teraz (nočné prednačítanie).
    /// </summary>
    public async Task<IReadOnlyList<string>> GetTriviaAsync(int csfdId, TimeSpan refreshMargin, CancellationToken cancellationToken)
    {
        if (Memory.TryGetValue(csfdId, out var hit) && Fresh(hit.At, hit.Items, refreshMargin))
        {
            return hit.Items;
        }

        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var cacheFile = CacheFile(csfdId);
            List<string>? cached = null;
            if (cacheFile is not null && File.Exists(cacheFile))
            {
                var at = File.GetLastWriteTimeUtc(cacheFile);
                try
                {
                    cached = JsonSerializer.Deserialize<List<string>>(await File.ReadAllTextAsync(cacheFile, cancellationToken).ConfigureAwait(false)) ?? new List<string>();
                }
                catch (JsonException)
                {
                    // Poškodený súbor = ako keby nebol.
                    cached = null;
                }

                if (cached is not null && Fresh(at, cached, refreshMargin))
                {
                    Remember(csfdId, at, cached);
                    return cached;
                }
            }

            var items = await DownloadAsync(csfdId, cancellationToken).ConfigureAwait(false);
            if (items is null || (items.Count == 0 && cached is { Count: > 0 }))
            {
                // ČSFD neodpovedá alebo nič nenašlo – radšej staré zaujímavosti než žiadne (a neprepisujeme ich).
                if (cached is { Count: > 0 })
                {
                    // Ďalší pokus o stiahnutie až o deň.
                    Remember(csfdId, DateTime.UtcNow - CacheTtl + RetryAfterFailure, cached);
                    return cached;
                }

                return Memory.TryGetValue(csfdId, out var stale) ? stale.Items : Array.Empty<string>();
            }

            Remember(csfdId, DateTime.UtcNow, items);
            if (cacheFile is not null)
            {
                // Atomicky: súbor sa nikdy nedá prečítať napoly zapísaný.
                var tmp = cacheFile + ".tmp";
                await File.WriteAllTextAsync(tmp, JsonSerializer.Serialize(items), cancellationToken).ConfigureAwait(false);
                File.Move(tmp, cacheFile, overwrite: true);
            }

            return items;
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "ČSFD zaujímavosti {Id}: načítanie zlyhalo", csfdId);
            return Array.Empty<string>();
        }
        finally
        {
            Gate.Release();
        }
    }

    private static void Remember(int csfdId, DateTime at, List<string> items)
    {
        if (Memory.Count >= MemoryLimit)
        {
            Memory.Clear();
        }

        Memory[csfdId] = (at, items);
    }

    /// <summary>
    /// Či treba zaujímavosti stiahnuť: diskový súbor chýba, je poškodený alebo jeho platnosť skončí do <paramref name="refreshMargin"/>.
    /// Nič nesťahuje; bez dátového priečinka pluginu vráti true.
    /// </summary>
    public static bool NeedsRefresh(int csfdId, TimeSpan refreshMargin)
    {
        var cacheFile = CacheFile(csfdId);
        if (cacheFile is null || !File.Exists(cacheFile))
        {
            return true;
        }

        try
        {
            var items = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(cacheFile)) ?? new List<string>();
            return !Fresh(File.GetLastWriteTimeUtc(cacheFile), items, refreshMargin);
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return true;
        }
    }

    /// <summary>
    /// Platí cache z času <paramref name="at"/> ešte aspoň <paramref name="margin"/>?
    /// Rezerva platí len pre neprázdne zaujímavosti – „nič“ netreba obnovovať v predstihu.
    /// </summary>
    internal static bool Fresh(DateTime at, List<string> items, TimeSpan margin = default)
        => items.Count > 0
            ? DateTime.UtcNow - at + margin < CacheTtl
            : DateTime.UtcNow - at < EmptyCacheTtl;

    /// <summary>Vytiahne texty zaujímavostí zo stránky (bez autora a bez spoilerov).</summary>
    internal static List<string> ParseTrivia(string html)
    {
        var items = new List<string>();
        var start = html.IndexOf("article-trivia", StringComparison.Ordinal);
        if (start < 0)
        {
            return items;
        }

        // Zoznam končí koncom sekcie; stránkovanie a päta stránky už nie sú zaujímavosti.
        var end = html.IndexOf("</section>", start, StringComparison.Ordinal);
        var block = end < 0 ? html[start..] : html[start..end];
        foreach (Match m in ItemRx.Matches(block))
        {
            var raw = m.Groups[1].Value;
            if (raw.Contains("spoiler", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var text = AuthorRx.Replace(raw, string.Empty);
            text = TagRx.Replace(text, " ");
            text = SpaceRx.Replace(WebUtility.HtmlDecode(text), " ").Trim();
            if (text.Length > 0 && !items.Contains(text))
            {
                items.Add(text);
            }
        }

        return items;
    }

    private async Task<List<string>?> DownloadAsync(int csfdId, CancellationToken cancellationToken)
    {
        var http = SharedHttp.Value;
        try
        {
            // Slovenské zaujímavosti; ak ich SK stránka nemá, skúsime českú.
            var sk = await GetPoliteAsync(http, $"/film/{csfdId}/zaujimavosti/", cancellationToken).ConfigureAwait(false);
            var items = sk is null ? null : ParseTrivia(sk);
            if (items is null || items.Count == 0)
            {
                var cz = await GetPoliteAsync(http, $"https://www.csfd.cz/film/{csfdId}/zajimavosti/", cancellationToken).ConfigureAwait(false);
                if (cz is not null)
                {
                    items = ParseTrivia(cz);
                }
            }

            if (items is null)
            {
                // Výzvu Anubis sa nepodarilo prejsť ani na SK, ani na CZ (dôvod je v predošlom varovaní).
                Interlocked.Increment(ref _failures);
            }

            _logger.LogDebug("ČSFD zaujímavosti {Id}: {Count}", csfdId, items?.Count);
            return items;
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return new List<string>();
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(
                "ČSFD zaujímavosti {Id}: stránka sa nepodarila načítať ({Status}): {Error}",
                csfdId,
                ex is HttpRequestException { StatusCode: { } code } ? $"HTTP {(int)code}" : ex.GetType().Name,
                ex.Message);
            _logger.LogDebug(ex, "ČSFD zaujímavosti {Id}: detail chyby", csfdId);
            Interlocked.Increment(ref _failures);
            return null;
        }
    }

    /// <summary>
    /// Stiahne stránku zdieľaným klientom, najskôr <see cref="MinRequestGap"/> po predošlej požiadavke
    /// (aj medzi SK a CZ pokusom toho istého filmu). Volá sa len pod <see cref="Gate"/>.
    /// </summary>
    private async Task<string?> GetPoliteAsync(HttpClient http, string pathOrUrl, CancellationToken cancellationToken)
    {
        var wait = _lastRequestAt + MinRequestGap - DateTime.UtcNow;
        if (wait > TimeSpan.Zero)
        {
            await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
        }

        try
        {
            return await CsfdTvTipsClient.GetPageAsync(http, pathOrUrl, _logger, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lastRequestAt = DateTime.UtcNow;
        }
    }

    private static string? CacheFile(int csfdId)
    {
        var dataFolder = Plugin.Instance?.DataFolderPath;
        if (string.IsNullOrEmpty(dataFolder))
        {
            return null;
        }

        var dir = Path.Combine(dataFolder, "cache", "trivia");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, $"{csfdId}.json");
    }
}
