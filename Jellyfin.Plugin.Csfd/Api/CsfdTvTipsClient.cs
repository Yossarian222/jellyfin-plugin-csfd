using System;
using System.Collections.Generic;
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

/// <summary>Jeden „TV tip dňa“ zo stránky csfd.sk/televizia.</summary>
public sealed record CsfdTvTip(int CsfdId, string Title, int? Year, string? Time, string? Channel);

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

    private static readonly Regex ArticleSplit = new("<article class=\"article article-poster-78", RegexOptions.Compiled);
    private static readonly Regex IdRx = new(@"href=""/film/(\d+)-", RegexOptions.Compiled);
    private static readonly Regex TitleRx = new(@"film-title-name"">([^<]+)<", RegexOptions.Compiled);
    private static readonly Regex YearRx = new(@"class=""info"">(\d{4})</span>", RegexOptions.Compiled);
    private static readonly Regex TimeRx = new(@"<strong>([^<]+)</strong>", RegexOptions.Compiled);
    private static readonly Regex ChannelRx = new(@"tv-label-btn"">.*?alt=""([^""]*)""", RegexOptions.Compiled | RegexOptions.Singleline);
    private static readonly Regex ChallengeRx = new(@"id=""anubis_challenge""[^>]*>(.*?)</script>", RegexOptions.Compiled | RegexOptions.Singleline);

    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly Dictionary<int, (DateTime At, List<CsfdTvTip> Tips)> Cache = new();

    private readonly ILogger<CsfdTvTipsClient> _logger;

    public CsfdTvTipsClient(ILogger<CsfdTvTipsClient> logger)
    {
        _logger = logger;
    }

    /// <summary>Tipy pre deň <paramref name="day"/> (0 = dnes, 1 = zajtra, −1 = včera).</summary>
    public async Task<IReadOnlyList<CsfdTvTip>> GetTipsAsync(int day, CancellationToken cancellationToken)
    {
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Cache.TryGetValue(day, out var hit) && DateTime.UtcNow - hit.At < CacheTtl)
            {
                return hit.Tips;
            }

            var html = await FetchAsync(day, cancellationToken).ConfigureAwait(false);
            if (html is null)
            {
                // Pri výpadku radšej servírujeme starú cache než nič.
                return Cache.TryGetValue(day, out var stale) ? stale.Tips : Array.Empty<CsfdTvTip>();
            }

            var tips = ParseTips(html);
            Cache[day] = (DateTime.UtcNow, tips);
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
            tips.Add(new CsfdTvTip(
                csfdId,
                WebUtility.HtmlDecode(title.Groups[1].Value).Trim(),
                year.Success ? int.Parse(year.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : null,
                time.Success ? WebUtility.HtmlDecode(time.Groups[1].Value).Trim() : null,
                channel.Success ? WebUtility.HtmlDecode(channel.Groups[1].Value).Trim() : null));
        }

        return tips;
    }

    /// <summary>Nájde v stránke výzvu Anubis a vráti parametre odpovede, alebo null.</summary>
    internal static (string Id, string Hash, long Nonce)? SolveChallenge(string html)
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
        var prefix = new string('0', difficulty);

        for (long nonce = 0; nonce < 50_000_000; nonce++)
        {
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
        using var handler = new HttpClientHandler { CookieContainer = new CookieContainer(), AllowAutoRedirect = true };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);

        try
        {
            var html = await http.GetStringAsync(new Uri(Host + path), cancellationToken).ConfigureAwait(false);
            if (!html.Contains("anubis_challenge", StringComparison.Ordinal))
            {
                return html;
            }

            var solved = SolveChallenge(html);
            if (solved is null)
            {
                _logger.LogWarning("ČSFD TV: výzvu Anubis sa nepodarilo vyriešiť");
                return null;
            }

            var pass = $"{Host}/.within.website/x/cmd/anubis/api/pass-challenge?id={solved.Value.Id}"
                + $"&response={solved.Value.Hash}&nonce={solved.Value.Nonce}"
                + $"&redir={Uri.EscapeDataString(Host + path)}&elapsedTime=50";
            using var passResponse = await http.GetAsync(new Uri(pass), cancellationToken).ConfigureAwait(false);
            var result = await passResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (result.Contains("anubis_challenge", StringComparison.Ordinal))
            {
                _logger.LogWarning("ČSFD TV: Anubis odpoveď neprijal ({Status})", (int)passResponse.StatusCode);
                return null;
            }

            return result;
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            // Čokoľvek (sieť, zmenený formát výzvy…) – riadok tipov má byť radšej prázdny než 500.
            _logger.LogWarning(ex, "ČSFD TV: stránka {Path} sa nepodarila načítať", path);
            return null;
        }
    }
}
