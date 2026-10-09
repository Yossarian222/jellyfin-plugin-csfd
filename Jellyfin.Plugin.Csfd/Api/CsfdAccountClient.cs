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
    private static readonly Regex FlashErrorRx = new(@"flash-message-error"">([^<]+)<", RegexOptions.Compiled);
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
                var form = film is null ? null : FindRatingForm(film);
                if (form is null)
                {
                    // Bez formulára „form-stars-add“ nie sme prihlásení (relácia vypršala) – prihlásime sa znova.
                    _loggedIn = false;
                    continue;
                }

                if (FindStarHref(film!, stars * 20) == "#close-dropdown")
                {
                    // Film už má presne toto hodnotenie.
                    UpdateCache(csfdId, stars);
                    return (true, "OK");
                }

                // Rovnaké pole ako po kliknutí na hviezdu (CSFD.SecureHandle vloží kód hviezdy do _value_).
                await PostFormAsync(
                    http,
                    new Uri("https://www.csfd.sk" + form.Value.Action),
                    new Dictionary<string, string>
                    {
                        ["_token_"] = form.Value.Token,
                        ["_value_"] = EncodeValue(stars * 20),
                        ["_do"] = "starRating-addRating-form-submit"
                    },
                    cancellationToken).ConfigureAwait(false);

                UpdateCache(csfdId, stars);
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

    /// <summary>Kód hodnotenia ako CSFD.SimpleCrypt.encode: ROT13(base64url(JSON)), napr. 20 → „ZwN“.</summary>
    internal static string EncodeValue(int rating100)
    {
        var b64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(rating100.ToString(CultureInfo.InvariantCulture)))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');
        var chars = b64.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            var c = chars[i];
            if (char.IsAsciiLetter(c))
            {
                chars[i] = (char)(c + (char.ToLowerInvariant(c) < 'n' ? 13 : -13));
            }
        }

        return new string(chars);
    }

    /// <summary>Akcia a CSRF token formulára „form-stars-add“ (je len pre prihláseného).</summary>
    internal static (string Action, string Token)? FindRatingForm(string html)
    {
        var form = Regex.Match(html, @"<form[^>]*id=""form-stars-add""[^>]*>.*?</form>", RegexOptions.Singleline);
        if (!form.Success)
        {
            return null;
        }

        var action = Regex.Match(form.Value, @"action=""([^""]+)""");
        var token = Regex.Match(form.Value, @"name=""_token_""[^>]*value=""([^""]*)""|value=""([^""]*)""[^>]*name=""_token_""");
        if (!action.Success || !token.Success)
        {
            return null;
        }

        return (WebUtility.HtmlDecode(action.Groups[1].Value),
            WebUtility.HtmlDecode(token.Groups[1].Success ? token.Groups[1].Value : token.Groups[2].Value));
    }

    private static void UpdateCache(int csfdId, int stars)
    {
        if (_ratings is not null)
        {
            _ratings[csfdId] = stars;
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

    /// <summary>Odošle formulár ako prehliadač – CAS aj Nette odmietajú POST bez Origin/Referer z csfd.sk.</summary>
    private static async Task<string> PostFormAsync(HttpClient http, Uri url, Dictionary<string, string> fields, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new FormUrlEncodedContent(fields) };
        request.Headers.TryAddWithoutValidation("Origin", "https://www.csfd.sk");
        request.Headers.Referrer = new Uri("https://www.csfd.sk/prihlasenie/");
        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var html = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"ČSFD vrátilo HTTP {(int)response.StatusCode}: {html[..Math.Min(200, html.Length)]}");
        }

        return html;
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
        var fields = new Dictionary<string, string>
        {
            ["nick"] = config.CsfdNick.Trim(),
            ["password"] = config.CsfdPassword,
            ["permanent"] = "1",
            ["_do"] = "loginForm-submit"
        };
        var html = await PostFormAsync(_session, action, fields, cancellationToken).ConfigureAwait(false);
        if (html.Contains("anubis_challenge", StringComparison.Ordinal))
        {
            // Anubis aj na cas.csfd.cz – po vyriešení odošleme formulár znova.
            await CsfdTvTipsClient.PassAnubisAsync(_session, html, action, _logger, cancellationToken).ConfigureAwait(false);
            html = await PostFormAsync(_session, action, fields, cancellationToken).ConfigureAwait(false);
        }

        var error = FlashErrorRx.Match(html);
        if (error.Success)
        {
            return (false, "ČSFD: " + WebUtility.HtmlDecode(error.Groups[1].Value).Trim());
        }

        var check = await CsfdTvTipsClient.GetPageAsync(_session, "/", _logger, cancellationToken).ConfigureAwait(false) ?? html;
        _loggedIn = check.Contains("/odhlasit/", StringComparison.Ordinal)
            || check.Contains(config.CsfdNick.Trim(), StringComparison.OrdinalIgnoreCase)
            || check.Contains("odhlásiť", StringComparison.OrdinalIgnoreCase);
        _logger.LogInformation("ČSFD účet: prihlásenie {Result}", _loggedIn ? "OK" : "zlyhalo");
        return _loggedIn ? (true, "Prihlásenie na ČSFD je OK.") : (false, "Prihlásenie na ČSFD zlyhalo – skontroluj prezývku a heslo.");
    }
}
