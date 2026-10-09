using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Csfd.Api;

/// <summary>Jeden „TV tip dňa“ zo stránky csfd.sk/televizia; <paramref name="Thumbnail"/> = malý plagát z tejto stránky (absolútna https URL).</summary>
public sealed record CsfdTvTip(int CsfdId, string Title, int? Year, string? Time, string? Channel, string? Thumbnail = null);

/// <summary>
/// Číta „TV tipy dňa“ priamo z csfd.sk/televizia/ (slovenský program; sidecar csfd-api na ne endpoint nemá).
/// Stránku chráni Anubis, tak jeho proof-of-work riešime sami (algoritmus fast/slow = SHA-256 s nulami na začiatku).
/// </summary>
public sealed class CsfdTvTipsClient
{
    private const string Host = "https://www.csfd.sk";

    /// <summary>Len ASCII – HTTP hlavička s „Č“ hodí FormatException.</summary>
    internal const string UserAgent = "Mozilla/5.0 (X11; Linux x86_64) Jellyfin-Csfd-plugin";
    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(3);

    /// <summary>Vyššiu obtiažnosť (počet núl v hex) by PoW riešil príliš dlho – vzdáme to.</summary>
    internal const int MaxDifficulty = 5;

    private static readonly Regex ArticleSplit = new("<article class=\"article article-poster-78", RegexOptions.Compiled);
    private static readonly Regex IdRx = new(@"href=""/film/(\d+)-", RegexOptions.Compiled);
    private static readonly Regex TitleRx = new(@"film-title-name"">([^<]+)<", RegexOptions.Compiled);
    private static readonly Regex YearRx = new(@"class=""info"">(\d{4})</span>", RegexOptions.Compiled);
    private static readonly Regex TimeRx = new(@"<strong>([^<]+)</strong>", RegexOptions.Compiled);
    private static readonly Regex ChannelRx = new(@"tv-label-btn"">.*?alt=""([^""]*)""", RegexOptions.Compiled | RegexOptions.Singleline);
    /// <summary>Obrázok v článku tipu je v &lt;figure&gt; (logo stanice v tv-label-btn tam nie je).</summary>
    private static readonly Regex FigureRx = new(@"<figure\b[^>]*>(.*?)</figure>", RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.IgnoreCase);
    private static readonly Regex ImgRx = new(@"<img\b[^>]*>", RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.IgnoreCase);
    private static readonly Regex PosterImgRx = new(@"<img\b[^>]*/film/posters/[^>]*>", RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.IgnoreCase);

    private static readonly Regex AttrRx = new(
        @"\s(?<name>data-srcset|data-src|srcset|src)\s*=\s*""(?<value>[^""]*)""",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex ChallengeRx = new(@"id=""anubis_challenge""[^>]*>(.*?)</script>", RegexOptions.Compiled | RegexOptions.Singleline);

    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly Dictionary<DateOnly, (DateTime At, List<CsfdTvTip> Tips)> Cache = new();
    private static readonly TimeZoneInfo Zone = FindZone();

    private readonly ILogger<CsfdTvTipsClient> _logger;

    public CsfdTvTipsClient(ILogger<CsfdTvTipsClient> logger)
    {
        _logger = logger;
    }

    /// <summary>Tipy pre deň <paramref name="day"/> (0 = dnes, 1 = zajtra, −1 = včera).</summary>
    public async Task<IReadOnlyList<CsfdTvTip>> GetTipsAsync(int day, CancellationToken cancellationToken)
    {
        // Cache podľa skutočného dátumu (slovenský čas) – po polnoci nesmú ostať včerajšie tipy pod „dnes“.
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, Zone));
        var date = today.AddDays(day);
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var old in Cache.Keys.Where(k => k < today.AddDays(-1)).ToList())
            {
                Cache.Remove(old);
            }

            Cache.TryGetValue(date, out var stale);
            if (stale.Tips is not null && DateTime.UtcNow - stale.At < CacheTtl)
            {
                return stale.Tips;
            }

            var html = await FetchAsync(day, cancellationToken).ConfigureAwait(false);
            var tips = html is null ? new List<CsfdTvTip>() : ParseTips(html);
            if (tips.Count == 0 && stale.Tips is { Count: > 0 })
            {
                // Pri výpadku (alebo zmenenej stránke) radšej servírujeme starú cache než nič – a neprepíšeme ju.
                return stale.Tips;
            }

            if (html is not null)
            {
                Cache[date] = (DateTime.UtcNow, tips);
            }

            return tips;
        }
        finally
        {
            Gate.Release();
        }
    }

    internal static List<CsfdTvTip> ParseTips(string html)
    {
        var tips = new List<CsfdTvTip>();
        var parts = ArticleSplit.Split(html);
        for (var i = 1; i < parts.Length; i++)
        {
            var part = parts[i];
            var id = IdRx.Match(part);
            var title = TitleRx.Match(part);
            if (!id.Success || !title.Success)
            {
                continue;
            }

            var csfdId = int.Parse(id.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            if (tips.Exists(t => t.CsfdId == csfdId))
            {
                // Rovnaký film beží na viacerých staniciach – stačí prvý výskyt.
                continue;
            }

            var year = YearRx.Match(part);
            var time = TimeRx.Match(part);
            var channel = ChannelRx.Match(part);
            var thumbnail = ParseThumbnail(part);
            tips.Add(new CsfdTvTip(
                csfdId,
                WebUtility.HtmlDecode(title.Groups[1].Value).Trim(),
                year.Success ? int.Parse(year.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : null,
                time.Success ? WebUtility.HtmlDecode(time.Groups[1].Value).Trim() : null,
                channel.Success ? WebUtility.HtmlDecode(channel.Groups[1].Value).Trim() : null,
                thumbnail));
        }

        return tips;
    }

    /// <summary>
    /// Plagát z článku tipu: najväčšia položka srcset (inak src), ako absolútna https URL.
    /// Null, ak obrázok chýba alebo je to len zástupný obrázok ČSFD.
    /// </summary>
    internal static string? ParseThumbnail(string article)
    {
        var figure = FigureRx.Match(article);
        var img = figure.Success ? ImgRx.Match(figure.Groups[1].Value) : PosterImgRx.Match(article);
        if (!img.Success)
        {
            return null;
        }

        string? src = null;
        string? srcset = null;
        foreach (Match a in AttrRx.Matches(img.Value))
        {
            var value = WebUtility.HtmlDecode(a.Groups["value"].Value).Trim();
            switch (a.Groups["name"].Value.ToLowerInvariant())
            {
                // data-* (lazy načítanie) má prednosť – v src býva vtedy len zástupný obrázok.
                case "data-src":
                    src = value;
                    break;
                case "src":
                    src ??= value;
                    break;
                case "data-srcset":
                    srcset = value;
                    break;
                case "srcset":
                    srcset ??= value;
                    break;
            }
        }

        var best = LargestFromSrcset(srcset) ?? src;
        if (string.IsNullOrWhiteSpace(best)
            || best.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
            || best.Contains("poster-free", StringComparison.OrdinalIgnoreCase)
            || best.Contains("placeholder", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (best.StartsWith("//", StringComparison.Ordinal))
        {
            return "https:" + best;
        }

        if (best.StartsWith('/'))
        {
            return Host + best;
        }

        return best.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ? "https://" + best[7..] : best;
    }

    /// <summary>Položka srcset s najväčším deskriptorom (2x, 3x alebo 156w); bez deskriptora sa berie ako 1x.</summary>
    private static string? LargestFromSrcset(string? srcset)
    {
        if (string.IsNullOrWhiteSpace(srcset))
        {
            return null;
        }

        string? best = null;
        var bestSize = double.MinValue;
        foreach (var entry in srcset.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var bits = entry.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (bits.Length == 0)
            {
                continue;
            }

            var size = 1d;
            if (bits.Length > 1 && bits[1].Length > 1
                && double.TryParse(bits[1][..^1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsed))
            {
                size = parsed;
            }

            if (size > bestSize)
            {
                bestSize = size;
                best = bits[0];
            }
        }

        return best;
    }

    /// <summary>Nájde v stránke výzvu Anubis a vráti parametre odpovede, alebo null.</summary>
    internal static (string Id, string Hash, long Nonce)? SolveChallenge(string html, ILogger? logger = null, CancellationToken cancellationToken = default)
    {
        var m = ChallengeRx.Match(html);
        if (!m.Success)
        {
            return null;
        }

        using var doc = JsonDocument.Parse(m.Groups[1].Value);
        var difficulty = doc.RootElement.GetProperty("rules").GetProperty("difficulty").GetInt32();
        var challenge = doc.RootElement.GetProperty("challenge");
        var id = challenge.GetProperty("id").GetString()!;
        var data = challenge.GetProperty("randomData").GetString()!;
        if (difficulty > MaxDifficulty)
        {
            logger?.LogWarning("ČSFD: Anubis obtiažnosť {Difficulty} je priveľká (max {Max}), výzvu neriešime", difficulty, MaxDifficulty);
            return null;
        }

        var prefix = new string('0', difficulty);

        for (long nonce = 0; nonce < 50_000_000; nonce++)
        {
            if ((nonce & 0xFFFF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(data + nonce))).ToLowerInvariant();
            if (hash.StartsWith(prefix, StringComparison.Ordinal))
            {
                return (id, hash, nonce);
            }
        }

        return null;
    }

    private async Task<string?> FetchAsync(int day, CancellationToken cancellationToken)
    {
        var path = day == 0 ? "/televizia/" : $"/televizia/?day={day}";
        using var http = CreateHttpClient();
        try
        {
            var html = await GetPageAsync(http, path, _logger, cancellationToken).ConfigureAwait(false);
            return html is null ? null : await SortedByRatingAsync(http, path, html, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            // Čokoľvek (sieť, zmenený formát výzvy…) – riadok tipov má byť radšej prázdny než 500.
            _logger.LogWarning(ex, "ČSFD TV: stránka {Path} sa nepodarila načítať", path);
            return null;
        }
    }

    /// <summary>HttpClient s vlastnými cookies – po vyriešení Anubis v ňom ostane priepustka pre ďalšie stránky.</summary>
    internal static HttpClient CreateHttpClient()
    {
        var handler = new HttpClientHandler { CookieContainer = new CookieContainer(), AllowAutoRedirect = true };
        var http = new HttpClient(handler, disposeHandler: true) { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        return http;
    }

    /// <summary>Stiahne stránku csfd.sk (cesta) alebo inú ČSFD adresu (celá URL); ak ju chráni Anubis, vyrieši výzvu. Null = výzvu sa nepodarilo prejsť.</summary>
    internal static async Task<string?> GetPageAsync(HttpClient http, string pathOrUrl, ILogger logger, CancellationToken cancellationToken)
    {
        var uri = pathOrUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? new Uri(pathOrUrl) : new Uri(Host + pathOrUrl);
        var html = await http.GetStringAsync(uri, cancellationToken).ConfigureAwait(false);
        return html.Contains("anubis_challenge", StringComparison.Ordinal)
            ? await PassAnubisAsync(http, html, uri, logger, cancellationToken).ConfigureAwait(false)
            : html;
    }

    /// <summary>Vyrieši výzvu Anubis v <paramref name="html"/> (stránka <paramref name="pageUri"/>) a vráti pôvodnú stránku, alebo null.</summary>
    internal static async Task<string?> PassAnubisAsync(HttpClient http, string html, Uri pageUri, ILogger logger, CancellationToken cancellationToken)
    {
        // PoW je čisto CPU práca – mimo vlákna požiadavky.
        var solved = await Task.Run(() => SolveChallenge(html, logger, cancellationToken), cancellationToken).ConfigureAwait(false);
        if (solved is null)
        {
            logger.LogWarning("ČSFD: výzvu Anubis sa nepodarilo vyriešiť");
            return null;
        }

        var pass = $"{pageUri.GetLeftPart(UriPartial.Authority)}/.within.website/x/cmd/anubis/api/pass-challenge?id={solved.Value.Id}"
            + $"&response={solved.Value.Hash}&nonce={solved.Value.Nonce}"
            + $"&redir={Uri.EscapeDataString(pageUri.ToString())}&elapsedTime=50";
        using var passResponse = await http.GetAsync(new Uri(pass), cancellationToken).ConfigureAwait(false);
        var result = await passResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!passResponse.IsSuccessStatusCode || result.Contains("anubis_challenge", StringComparison.Ordinal))
        {
            logger.LogWarning("ČSFD: Anubis odpoveď neprijal ({Status})", (int)passResponse.StatusCode);
            return null;
        }

        return result;
    }

    /// <summary>Časové pásmo ČSFD programu; bez tzdata v kontajneri ostane UTC.</summary>
    private static TimeZoneInfo FindZone()
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById("Europe/Bratislava");
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return TimeZoneInfo.Utc;
        }
    }

    /// <summary>
    /// Prepne stránku na „zoradiť podľa hodnotenia“ (formulár tvTipsOrder, sort=2) – poradie tipov je potom rebríček.
    /// Ak to nevyjde, ostane chronologické poradie z <paramref name="fallback"/>.
    /// </summary>
    private async Task<string> SortedByRatingAsync(HttpClient http, string path, string fallback, CancellationToken cancellationToken)
    {
        try
        {
            using var form = new FormUrlEncodedContent(new Dictionary<string, string> { ["sort"] = "2", ["_do"] = "tvTipsOrder-submit" });
            using var response = await http.PostAsync(new Uri(Host + path), form, cancellationToken).ConfigureAwait(false);
            var sorted = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (response.IsSuccessStatusCode && ParseTips(sorted).Count > 0)
            {
                return sorted;
            }
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogDebug(ex, "ČSFD TV: zoradenie podľa hodnotenia zlyhalo");
        }

        return fallback;
    }
}
