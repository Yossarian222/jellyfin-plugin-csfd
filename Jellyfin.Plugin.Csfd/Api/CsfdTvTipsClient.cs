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
/// Stránky chráni Anubis. Anonymné čítanie sa predstaví poctivým bot UA (<see cref="BotUserAgent"/>) s hlavičkou Accept –
/// to Anubis na ČSFD púšťa bez výzvy. Ak výzva predsa príde, riešime ju sami: fast/slow = SHA-256 s nulami na začiatku,
/// metarefresh = počkať a otvoriť URL z &lt;meta refresh&gt;.
/// </summary>
public sealed class CsfdTvTipsClient
{
    private const string Host = "https://www.csfd.sk";

    /// <summary>Len ASCII – HTTP hlavička s „Č“ hodí FormatException. Prehliadačové UA – Anubis mu dá výzvu (prihlásená relácia).</summary>
    internal const string UserAgent = "Mozilla/5.0 (X11; Linux x86_64) Jellyfin-Csfd-plugin";

    /// <summary>
    /// Poctivo identifikovaný bot (bez „Mozilla“) pre anonymné čítanie verejných stránok.
    /// Anubis na ČSFD (v1.28) výzvu dáva len prehliadačovým UA, takže tento prejde bez proof-of-work.
    /// </summary>
    internal static readonly string BotUserAgent = $"jellyfin-plugin-csfd/{PluginVersion()} (+https://github.com/Yossarian222/jellyfin-plugin-csfd)";
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
    private static readonly Regex AnubisVersionRx = new(@"id=""anubis_version""[^>]*>\s*""?([^""<]*)""?\s*</script>", RegexOptions.Compiled | RegexOptions.Singleline);
    private static readonly Regex MetaRefreshRx = new(@"<meta\s+http-equiv=""refresh""\s+content=""([^""]+)""", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex RefreshValueRx = new(@"^\s*(\d+)\s*;\s*url\s*=\s*(\S+)\s*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Dlhšie než toto na metarefresh nečakáme (Anubis dáva 1–2 s).</summary>
    private static readonly TimeSpan MaxRefreshDelay = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Výzva Anubis „metarefresh“: „N; url=/.within.website/…/pass-challenge?…“ – Anubis ju náhodne pošle buď
    /// v HTTP hlavičke Refresh (<paramref name="refreshHeader"/>), alebo v &lt;meta http-equiv="refresh"&gt; v stránke.
    /// Vráti čakanie a absolútnu URL – len na rovnaký host ako stránka a len na pass-challenge.
    /// </summary>
    internal static bool TryParseMetaRefresh(string html, Uri pageUri, out TimeSpan delay, out Uri url, string? refreshHeader = null)
    {
        delay = TimeSpan.Zero;
        url = pageUri;
        if (!html.Contains("anubis_challenge", StringComparison.Ordinal))
        {
            return false;
        }

        var meta = MetaRefreshRx.Match(html);
        var value = !string.IsNullOrWhiteSpace(refreshHeader) ? refreshHeader : meta.Success ? WebUtility.HtmlDecode(meta.Groups[1].Value) : null;
        var m = value is null ? Match.Empty : RefreshValueRx.Match(value);
        if (!m.Success
            || !int.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var seconds)
            || !Uri.TryCreate(pageUri, m.Groups[2].Value, out var target)
            || !string.Equals(target.Host, pageUri.Host, StringComparison.OrdinalIgnoreCase)
            || target.Scheme != Uri.UriSchemeHttps
            || !target.AbsolutePath.EndsWith("/.within.website/x/cmd/anubis/api/pass-challenge", StringComparison.Ordinal))
        {
            return false;
        }

        // Pre istotu o chlp dlhšie, než žiada stránka.
        var wanted = TimeSpan.FromSeconds(seconds) + TimeSpan.FromMilliseconds(250);
        delay = wanted < MaxRefreshDelay ? wanted : MaxRefreshDelay;
        url = target;
        return true;
    }

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

    /// <summary>Verzia Anubisu zo stránky výzvy (napr. „v1.28.2“), alebo „?“.</summary>
    internal static string AnubisVersion(string html)
    {
        var m = AnubisVersionRx.Match(html);
        return m.Success && m.Groups[1].Value.Length > 0 ? m.Groups[1].Value.Trim() : "?";
    }

    /// <summary>Nájde v stránke výzvu Anubis a vráti parametre odpovede, alebo null.</summary>
    internal static (string Id, string Hash, long Nonce)? SolveChallenge(string html, ILogger? logger = null, CancellationToken cancellationToken = default)
    {
        var solved = TrySolveChallenge(html, out var reason, cancellationToken);
        if (solved is null)
        {
            logger?.LogWarning("ČSFD: výzvu Anubis {Version} neriešime – {Reason}", AnubisVersion(html), reason);
        }

        return solved;
    }

    /// <summary>
    /// Ako <see cref="SolveChallenge"/>, ale bez logovania: pri neúspechu vráti v <paramref name="reason"/> dôvod.
    /// Formát (Anubis v1.2x): {"rules":{"algorithm":"fast","difficulty":N},"challenge":{"id":…,"randomData":…}};
    /// fast/slow = SHA-256(randomData + nonce) s N nulami na začiatku hexu.
    /// </summary>
    internal static (string Id, string Hash, long Nonce)? TrySolveChallenge(string html, out string reason, CancellationToken cancellationToken = default)
    {
        var m = ChallengeRx.Match(html);
        if (!m.Success)
        {
            reason = "stránka neobsahuje anubis_challenge";
            return null;
        }

        string id;
        string data;
        int difficulty;
        try
        {
            using var doc = JsonDocument.Parse(m.Groups[1].Value);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("rules", out var rules) || rules.ValueKind != JsonValueKind.Object)
            {
                reason = "JSON výzvy nemá objekt rules";
                return null;
            }

            var algorithm = rules.TryGetProperty("algorithm", out var alg) && alg.ValueKind == JsonValueKind.String ? alg.GetString() : null;
            if (algorithm is not ("fast" or "slow"))
            {
                reason = algorithm == "metarefresh"
                    ? "metarefresh bez použiteľnej URL (ani hlavička Refresh, ani <meta refresh>)"
                    : $"algoritmus „{algorithm ?? "null"}“ nepodporujeme (vieme fast/slow/metarefresh)";
                return null;
            }

            if (!rules.TryGetProperty("difficulty", out var diff) || !diff.TryGetInt32(out difficulty) || difficulty < 0)
            {
                reason = "chýba alebo je neplatná difficulty";
                return null;
            }

            if (!root.TryGetProperty("challenge", out var challenge) || challenge.ValueKind != JsonValueKind.Object)
            {
                // Videné v noci 2026-11-26/27: Anubis občas pošle "challenge": null (výzva nevydaná / chybová stránka).
                reason = $"challenge je {(root.TryGetProperty("challenge", out var c) ? c.ValueKind.ToString() : "chýba")} (výzva nebola vydaná)";
                return null;
            }

            if (!challenge.TryGetProperty("id", out var idEl) || idEl.ValueKind != JsonValueKind.String
                || !challenge.TryGetProperty("randomData", out var dataEl) || dataEl.ValueKind != JsonValueKind.String)
            {
                reason = "challenge nemá id alebo randomData";
                return null;
            }

            id = idEl.GetString()!;
            data = dataEl.GetString()!;
        }
        catch (JsonException ex)
        {
            reason = "JSON výzvy sa nedá prečítať: " + ex.Message;
            return null;
        }

        if (difficulty > MaxDifficulty)
        {
            reason = $"obtiažnosť {difficulty} je priveľká (max {MaxDifficulty})";
            return null;
        }

        reason = "nenašiel sa nonce";
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
        using var http = CreateHttpClient(asBot: true);
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

    /// <summary>
    /// HttpClient s vlastnými cookies – po vyriešení Anubis v ňom ostane priepustka pre ďalšie stránky.
    /// <paramref name="asBot"/> = poctivé UA <see cref="BotUserAgent"/> (anonymné čítanie), inak prehliadačové <see cref="UserAgent"/>.
    /// </summary>
    internal static HttpClient CreateHttpClient(bool asBot = false)
    {
        var handler = new SocketsHttpHandler
        {
            CookieContainer = new CookieContainer(),
            UseCookies = true,
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.All,
            // Klient môže žiť celú noc (zdieľaná priepustka) – DNS zmeny sa prejavia aspoň po 15 min.
            PooledConnectionLifetime = TimeSpan.FromMinutes(15),
        };
        var http = new HttpClient(handler, disposeHandler: true) { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd(asBot ? BotUserAgent : UserAgent);

        // HttpClient sám Accept neposiela – Anubis (v1.28) potom aj botovi dá výzvu „metarefresh“ (overené 2026-11-27).
        http.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml;q=0.9,*/*;q=0.8");
        http.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "sk,cs;q=0.9,en;q=0.5");
        return http;
    }

    /// <summary>
    /// Stiahne stránku csfd.sk (cesta) alebo inú ČSFD adresu (celá URL); ak ju chráni Anubis, vyrieši výzvu. Null = výzvu sa nepodarilo prejsť.
    /// Chybový HTTP stav (404…) hodí <see cref="HttpRequestException"/> so <see cref="HttpRequestException.StatusCode"/>.
    /// </summary>
    internal static async Task<string?> GetPageAsync(HttpClient http, string pathOrUrl, ILogger logger, CancellationToken cancellationToken)
    {
        var uri = pathOrUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? new Uri(pathOrUrl) : new Uri(Host + pathOrUrl);
        using var response = await http.GetAsync(uri, cancellationToken).ConfigureAwait(false);
        var html = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            if (html.Contains("anubis", StringComparison.OrdinalIgnoreCase))
            {
                // Anubis zamietol (DENY/rate limit) – tu výzvu riešiť nemá zmysel.
                logger.LogWarning(
                    "ČSFD: Anubis {Version} odmietol {Url} (HTTP {Status}, UA {Agent})",
                    AnubisVersion(html),
                    uri,
                    (int)response.StatusCode,
                    http.DefaultRequestHeaders.UserAgent.ToString());
            }

            throw new HttpRequestException($"ČSFD {uri}: HTTP {(int)response.StatusCode}", null, response.StatusCode);
        }

        if (!html.Contains("anubis_challenge", StringComparison.Ordinal))
        {
            return html;
        }

        var refresh = response.Headers.TryGetValues("Refresh", out var values) ? string.Join(",", values) : null;
        return await PassAnubisAsync(http, html, uri, logger, cancellationToken, refresh).ConfigureAwait(false);
    }

    /// <summary>Vyrieši výzvu Anubis v <paramref name="html"/> (stránka <paramref name="pageUri"/>) a vráti pôvodnú stránku, alebo null.</summary>
    internal static async Task<string?> PassAnubisAsync(HttpClient http, string html, Uri pageUri, ILogger logger, CancellationToken cancellationToken, string? refreshHeader = null)
    {
        var version = AnubisVersion(html);
        var started = DateTime.UtcNow;

        if (TryParseMetaRefresh(html, pageUri, out var refreshDelay, out var refreshUrl, refreshHeader))
        {
            // Výzva „metarefresh“: prehliadač len počká a otvorí URL z <meta refresh> – urobíme to isté.
            await Task.Delay(refreshDelay, cancellationToken).ConfigureAwait(false);
            using var refreshResponse = await http.GetAsync(refreshUrl, cancellationToken).ConfigureAwait(false);
            var refreshed = await refreshResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!refreshResponse.IsSuccessStatusCode || refreshed.Contains("anubis_challenge", StringComparison.Ordinal))
            {
                logger.LogWarning(
                    "ČSFD: Anubis {Version} metarefresh pre {Url} neprešiel (HTTP {Status}{Again})",
                    version,
                    pageUri,
                    (int)refreshResponse.StatusCode,
                    refreshed.Contains("anubis_challenge", StringComparison.Ordinal) ? ", znova výzva" : string.Empty);
                return null;
            }

            logger.LogDebug("ČSFD: Anubis {Version} metarefresh prejdený pre {Url}", version, pageUri);
            return refreshed;
        }

        // PoW je čisto CPU práca – mimo vlákna požiadavky.
        string reason = string.Empty;
        var solved = await Task.Run(() => TrySolveChallenge(html, out reason, cancellationToken), cancellationToken).ConfigureAwait(false);
        if (solved is null)
        {
            logger.LogWarning("ČSFD: výzvu Anubis {Version} pre {Url} sa nepodarilo vyriešiť – {Reason}", version, pageUri, reason);
            return null;
        }

        var elapsed = Math.Max(1, (int)(DateTime.UtcNow - started).TotalMilliseconds);
        var pass = $"{pageUri.GetLeftPart(UriPartial.Authority)}/.within.website/x/cmd/anubis/api/pass-challenge?id={Uri.EscapeDataString(solved.Value.Id)}"
            + $"&response={solved.Value.Hash}&nonce={solved.Value.Nonce}"
            + $"&redir={Uri.EscapeDataString(pageUri.ToString())}&elapsedTime={elapsed}";
        using var passResponse = await http.GetAsync(new Uri(pass), cancellationToken).ConfigureAwait(false);
        var result = await passResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (result.Contains("anubis_challenge", StringComparison.Ordinal))
        {
            logger.LogWarning(
                "ČSFD: Anubis {Version} odpoveď pre {Url} neprijal (HTTP {Status}) – po pass-challenge prišla znova výzva",
                version,
                pageUri,
                (int)passResponse.StatusCode);
            return null;
        }

        if (!passResponse.IsSuccessStatusCode)
        {
            // Výzva prešla, ale cieľová stránka sama vrátila chybu (napr. 404 neexistujúci profil) – to nie je chyba Anubisu.
            logger.LogWarning(
                "ČSFD: Anubis {Version} prejdený, ale {Url} vrátila HTTP {Status}{Detail}",
                version,
                passResponse.RequestMessage?.RequestUri ?? pageUri,
                (int)passResponse.StatusCode,
                result.Contains("anubis", StringComparison.OrdinalIgnoreCase) ? " (stránka Anubisu – odpoveď zamietnutá)" : string.Empty);
            return null;
        }

        logger.LogDebug("ČSFD: Anubis {Version} prejdený pre {Url} (nonce {Nonce})", version, pageUri, solved.Value.Nonce);
        return result;
    }

    /// <summary>Verzia pluginu na 3 miesta (1.12.2), pre User-Agent.</summary>
    private static string PluginVersion()
    {
        var v = typeof(CsfdTvTipsClient).Assembly.GetName().Version;
        return v is null ? "0.0.0" : $"{v.Major}.{v.Minor}.{Math.Max(0, v.Build)}";
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
