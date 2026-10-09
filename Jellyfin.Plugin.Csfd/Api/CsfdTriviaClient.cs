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

    /// <summary>Prázdny výsledok (alebo chyba) sa skúsi znova skôr.</summary>
    private static readonly TimeSpan EmptyCacheTtl = TimeSpan.FromDays(1);

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
    public async Task<IReadOnlyList<string>> GetTriviaAsync(int csfdId, CancellationToken cancellationToken)
    {
        if (Memory.TryGetValue(csfdId, out var hit) && Fresh(hit.At, hit.Items))
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

                if (cached is not null && Fresh(at, cached))
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
                    Remember(csfdId, DateTime.UtcNow - CacheTtl + EmptyCacheTtl, cached);
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

    private static bool Fresh(DateTime at, List<string> items)
        => DateTime.UtcNow - at < (items.Count > 0 ? CacheTtl : EmptyCacheTtl);

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
        using var http = CsfdTvTipsClient.CreateHttpClient();
        try
        {
            // Slovenské zaujímavosti; ak ich SK stránka nemá, skúsime českú.
            var html = await CsfdTvTipsClient.GetPageAsync(http, $"/film/{csfdId}/zaujimavosti/", _logger, cancellationToken).ConfigureAwait(false);
            var items = html is null ? null : ParseTrivia(html);
            if (items is null || items.Count == 0)
            {
                var cz = await CsfdTvTipsClient.GetPageAsync(http, $"https://www.csfd.cz/film/{csfdId}/zajimavosti/", _logger, cancellationToken).ConfigureAwait(false);
                if (cz is not null)
                {
                    items = ParseTrivia(cz);
                }
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
            _logger.LogWarning(ex, "ČSFD zaujímavosti {Id}: stránka sa nepodarila načítať", csfdId);
            return null;
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
