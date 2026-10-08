using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Csfd.Api;

/// <summary>
/// Poradie v ČSFD rebríčkoch „Najlepšie filmy“ a „Najlepšie seriály“ (top 1000, stránky po 100).
/// Sťahuje sa raz za týždeň a drží v cache na disku.
/// </summary>
public sealed class CsfdRankingsClient
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromDays(7);
    private static readonly string[] Lists = { "/rebricky/filmy/najlepsie/", "/rebricky/serialy/najlepsie/" };
    private static readonly Regex EntryRx = new(@"class=""position"">\s*(\d+)\.?\s*</span>\s*<a href=""/film/(\d+)-", RegexOptions.Compiled);
    private static readonly SemaphoreSlim Gate = new(1, 1);

    private static Dictionary<int, int>? _ranks;
    private static DateTime _loadedUtc = DateTime.MinValue;

    private readonly ILogger<CsfdRankingsClient> _logger;

    public CsfdRankingsClient(ILogger<CsfdRankingsClient> logger)
    {
        _logger = logger;
    }

    /// <summary>ČSFD ID → poradie v rebríčku (filmy aj seriály; ID sú na ČSFD jedinečné).</summary>
    public async Task<IReadOnlyDictionary<int, int>> GetRanksAsync(CancellationToken cancellationToken)
    {
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_ranks is not null && DateTime.UtcNow - _loadedUtc < CacheTtl)
            {
                return _ranks;
            }

            var cacheFile = CacheFile();
            if (cacheFile is not null && File.Exists(cacheFile) && DateTime.UtcNow - File.GetLastWriteTimeUtc(cacheFile) < CacheTtl)
            {
                _ranks = JsonSerializer.Deserialize<Dictionary<int, int>>(await File.ReadAllTextAsync(cacheFile, cancellationToken).ConfigureAwait(false));
                _loadedUtc = File.GetLastWriteTimeUtc(cacheFile);
                if (_ranks is { Count: > 0 })
                {
                    return _ranks;
                }
            }

            var fresh = await DownloadAsync(cancellationToken).ConfigureAwait(false);
            if (fresh.Count > 0)
            {
                _ranks = fresh;
                _loadedUtc = DateTime.UtcNow;
                if (cacheFile is not null)
                {
                    await File.WriteAllTextAsync(cacheFile, JsonSerializer.Serialize(fresh), cancellationToken).ConfigureAwait(false);
                }
            }

            // Pri výpadku ČSFD radšej staré poradie než žiadne.
            return _ranks ?? new Dictionary<int, int>();
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "ČSFD rebríčky: načítanie zlyhalo");
            return _ranks ?? new Dictionary<int, int>();
        }
        finally
        {
            Gate.Release();
        }
    }

    internal static void ParseInto(string html, IDictionary<int, int> ranks)
    {
        foreach (Match m in EntryRx.Matches(html))
        {
            var rank = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            var id = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
            ranks.TryAdd(id, rank);
        }
    }

    private async Task<Dictionary<int, int>> DownloadAsync(CancellationToken cancellationToken)
    {
        var ranks = new Dictionary<int, int>();
        using var http = CsfdTvTipsClient.CreateHttpClient();
        foreach (var list in Lists)
        {
            for (var from = 0; from < 1000; from += 100)
            {
                var path = from == 0 ? list : $"{list}?from={from}";
                var html = await CsfdTvTipsClient.GetPageAsync(http, path, _logger, cancellationToken).ConfigureAwait(false);
                var before = ranks.Count;
                if (html is not null)
                {
                    ParseInto(html, ranks);
                }

                if (ranks.Count == before)
                {
                    break; // koniec rebríčka (seriálov je menej než 1000)
                }

                await Task.Delay(500, cancellationToken).ConfigureAwait(false);
            }
        }

        _logger.LogInformation("ČSFD rebríčky: načítaných {Count} pozícií", ranks.Count);
        return ranks;
    }

    private static string? CacheFile()
    {
        var dataFolder = Plugin.Instance?.DataFolderPath;
        if (string.IsNullOrEmpty(dataFolder))
        {
            return null;
        }

        var dir = Path.Combine(dataFolder, "cache");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "rankings.json");
    }
}
