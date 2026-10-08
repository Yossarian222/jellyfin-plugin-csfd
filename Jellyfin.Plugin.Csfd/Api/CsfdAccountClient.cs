using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Csfd.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Csfd.Api;

/// <summary>
/// Používateľský účet na ČSFD: čítanie vlastných hodnotení z verejného profilu a hodnotenie filmov (0–5 hviezd)
/// cez prihlásenú reláciu – ČSFD nemá API, plugin robí to isté čo prehliadač (prihlásenie cez cas.csfd.cz, klik na hviezdu).
/// </summary>
public sealed class CsfdAccountClient
{
    private static readonly TimeSpan RatingsTtl = TimeSpan.FromMinutes(10);
    private static readonly Regex ProfileRx = new(@"/uzivatel/(\d+-[^/]+)/", RegexOptions.Compiled);
    private static readonly Regex RowFilmRx = new(@"href=""/film/(\d+)-", RegexOptions.Compiled);
    private static readonly Regex RowStarsRx = new(@"class=""stars (?:stars-(\d)|trash)", RegexOptions.Compiled);
    private static readonly Regex LoginFormRx = new(@"<form action=""([^""]+)""[^>]*id=""frm-loginForm""", RegexOptions.Compiled);
    private static readonly SemaphoreSlim Gate = new(1, 1);

    private static Dictionary<int, int>? _ratings;
    private static DateTime _ratingsAt = DateTime.MinValue;
    private static HttpClient? _session;
    private static bool _loggedIn;

    private readonly ILogger<CsfdAccountClient> _logger;

    public CsfdAccountClient(ILogger<CsfdAccountClient> logger)
    {
        _logger = logger;
    }

    private static PluginConfiguration Config => Plugin.Instance?.Configuration ?? new PluginConfiguration();

    /// <summary>ČSFD ID → hviezdy (0 = odpad, 1–5) z verejného profilu nastaveného v plugine.</summary>
    public async Task<IReadOnlyDictionary<int, int>> GetMyRatingsAsync(CancellationToken cancellationToken)
    {
        var profile = ProfileRx.Match(Config.CsfdProfileUrl ?? string.Empty);
        if (!profile.Success)
        {
            return new Dictionary<int, int>();
        }

        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_ratings is not null && DateTime.UtcNow - _ratingsAt < RatingsTtl)
            {
                return _ratings;
            }

            var ratings = new Dictionary<int, int>();
            using var http = CsfdTvTipsClient.CreateHttpClient();
            for (var page = 1; page <= 200; page++)
            {
                var path = $"/uzivatel/{profile.Groups[1].Value}/hodnotenia/" + (page > 1 ? $"?page={page}" : string.Empty);
                var html = await CsfdTvTipsClient.GetPageAsync(http, path, _logger, cancellationToken).ConfigureAwait(false);
                var before = ratings.Count;
                if (html is not null)
                {
                    ParseRatingsInto(html, ratings);
                }

                if (ratings.Count == before || html is null || !html.Contains($"page={page + 1}", StringComparison.Ordinal))
                {
                    break;
                }

                await Task.Delay(300, cancellationToken).ConfigureAwait(false);
            }

            _ratings = ratings;
            _ratingsAt = DateTime.UtcNow;
            return ratings;
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "ČSFD účet: hodnotenia sa nepodarilo načítať");
            return _ratings ?? new Dictionary<int, int>();
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>Ohodnotí film na ČSFD (0 = odpad, 1–5 hviezd). Vráti (ok, správa).</summary>
    public async Task<(bool Ok, string Message)> RateAsync(int csfdId, int stars, CancellationToken cancellationToken)
    {
        stars = Math.Clamp(stars, 0, 5);
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            for (var attempt = 1; attempt <= 2; attempt++)
            {
                if (!_loggedIn)
                {
                    var (ok, message) = await LoginCoreAsync(cancellationToken).ConfigureAwait(false);
                    if (!ok)
                    {
                        return (false, message);
                    }
                }

                var http = _session!;
                var film = await CsfdTvTipsClient.GetPageAsync(http, $"/film/{csfdId}/prehlad/", _logger, cancellationToken).ConfigureAwait(false);
                var href = film is null ? null : FindStarHref(film, stars * 20);
                if (href is null || href.Contains("registration-motivation", StringComparison.Ordinal))
                {
                    // Relácia vypršala – prihlásime sa znova.
                    _loggedIn = false;
                    continue;
                }

                var url = href.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? href : "https://www.csfd.sk" + href;
                using var response = await http.GetAsync(new Uri(url), cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    return (false, $"ČSFD vrátilo HTTP {(int)response.StatusCode}");
                }

                if (_ratings is not null)
                {
                    _ratings[csfdId] = stars;
                }

                _logger.LogInformation("ČSFD účet: film {CsfdId} ohodnotený {Stars}/5", csfdId, stars);
                return (true, "OK");
            }

            return (false, "Na ČSFD sa nepodarilo prihlásiť (hviezdy ostali neaktívne).");
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "ČSFD účet: hodnotenie {CsfdId} zlyhalo", csfdId);
            return (false, ex.Message);
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>Overí prihlásenie (pre tlačidlo v nastaveniach).</summary>
    public async Task<(bool Ok, string Message)> TestLoginAsync(CancellationToken cancellationToken)
    {
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _loggedIn = false;
            return await LoginCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Gate.Release();
        }
    }

    internal static void ParseRatingsInto(string html, IDictionary<int, int> ratings)
    {
        foreach (var row in html.Split("<tr", StringSplitOptions.None))
        {
            var film = RowFilmRx.Match(row);
            var stars = RowStarsRx.Match(row);
            if (film.Success && stars.Success)
            {
                var value = stars.Groups[1].Success ? int.Parse(stars.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
                ratings.TryAdd(int.Parse(film.Groups[1].Value, CultureInfo.InvariantCulture), value);
            }
        }
    }

    /// <summary>Odkaz hviezdy v bloku „Klikni a hodnoť“ (data-rating 0–100 po 20).</summary>
    internal static string? FindStarHref(string html, int rating)
    {
        foreach (Match a in Regex.Matches(html, @"<a [^>]*class=""star star-(\d+)[^""]*""[^>]*>"))
        {
            if (a.Groups[1].Value != rating.ToString(CultureInfo.InvariantCulture))
            {
                continue;
            }

            var href = Regex.Match(a.Value, @"href=""([^""]+)""");
            return href.Success ? WebUtility.HtmlDecode(href.Groups[1].Value) : null;
        }

        return null;
    }

    private async Task<(bool Ok, string Message)> LoginCoreAsync(CancellationToken cancellationToken)
    {
        var config = Config;
        if (string.IsNullOrWhiteSpace(config.CsfdNick) || string.IsNullOrEmpty(config.CsfdPassword))
        {
            return (false, "V nastaveniach pluginu chýba ČSFD prezývka alebo heslo.");
        }

        _session?.Dispose();
        _session = CsfdTvTipsClient.CreateHttpClient();
        var login = await CsfdTvTipsClient.GetPageAsync(_session, "/prihlasenie/", _logger, cancellationToken).ConfigureAwait(false);
        var form = login is null ? Match.Empty : LoginFormRx.Match(login);
        if (!form.Success)
        {
            return (false, "Prihlasovací formulár ČSFD sa nenašiel (zmenil sa web?).");
        }

        var action = new Uri(WebUtility.HtmlDecode(form.Groups[1].Value));
        using var body = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["nick"] = config.CsfdNick.Trim(),
            ["password"] = config.CsfdPassword,
            ["permanent"] = "1",
            ["_do"] = "loginForm-submit"
        });
        using var response = await _session.PostAsync(action, body, cancellationToken).ConfigureAwait(false);
        var html = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (html.Contains("anubis_challenge", StringComparison.Ordinal))
        {
            // Anubis aj na cas.csfd.cz – po vyriešení odošleme formulár znova.
            await CsfdTvTipsClient.PassAnubisAsync(_session, html, action, _logger, cancellationToken).ConfigureAwait(false);
            using var body2 = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["nick"] = config.CsfdNick.Trim(),
                ["password"] = config.CsfdPassword,
                ["permanent"] = "1",
                ["_do"] = "loginForm-submit"
            });
            using var retry = await _session.PostAsync(action, body2, cancellationToken).ConfigureAwait(false);
            html = await retry.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }

        var check = await CsfdTvTipsClient.GetPageAsync(_session, "/", _logger, cancellationToken).ConfigureAwait(false) ?? html;
        _loggedIn = check.Contains("/odhlasenie/", StringComparison.Ordinal)
            || check.Contains(config.CsfdNick.Trim(), StringComparison.OrdinalIgnoreCase)
            || check.Contains("odhlásiť", StringComparison.OrdinalIgnoreCase);
        _logger.LogInformation("ČSFD účet: prihlásenie {Result}", _loggedIn ? "OK" : "zlyhalo");
        return _loggedIn ? (true, "Prihlásenie na ČSFD je OK.") : (false, "Prihlásenie na ČSFD zlyhalo – skontroluj prezývku a heslo.");
    }
}
