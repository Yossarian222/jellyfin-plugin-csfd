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

    /// <summary>Po neúspešnom prihlásení chvíľu neskúšame znova (ČSFD by účet mohlo zablokovať).</summary>
    private static readonly TimeSpan LoginCooldown = TimeSpan.FromMinutes(10);

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
    private static DateTime _loginFailedAt = DateTime.MinValue;
    private static volatile bool _resetRequested;

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
            ApplyReset();
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
            ApplyReset();
            for (var attempt = 1; attempt <= 2; attempt++)
            {
                if (!_loggedIn)
                {
                    var (ok, message) = await LoginAsync(cancellationToken).ConfigureAwait(false);
                    if (!ok)
                    {
                        return (false, message);
                    }
                }

                var http = _session!;
                var film = await CsfdTvTipsClient.GetPageAsync(http, $"/film/{csfdId}/prehlad/", _logger, cancellationToken).ConfigureAwait(false);
                if (film is null)
                {
                    return (false, "Stránku filmu na ČSFD sa nepodarilo načítať.");
                }

                var form = FindRatingForm(film);
                if (form is null)
                {
                    if (IsLoggedInPage(film, Config.CsfdNick))
                    {
                        // Relácia platí, len formulár chýba (iný typ titulu, zmenený web) – nové prihlásenie by nepomohlo.
                        return (false, "Formulár hodnotenia sa na stránke filmu nenašiel.");
                    }

                    // Bez formulára „form-stars-add“ a bez odhlásenia nie sme prihlásení (relácia vypršala) – prihlásime sa znova.
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
                var filmUrl = $"https://www.csfd.sk/film/{csfdId}/prehlad/";
                var after = await PostFormAsync(
                    http,
                    new Uri("https://www.csfd.sk" + form.Value.Action),
                    new Dictionary<string, string>
                    {
                        ["_token_"] = form.Value.Token,
                        ["_value_"] = EncodeValue(stars * 20),
                        ["_do"] = "starRating-addRating-form-submit"
                    },
                    cancellationToken,
                    filmUrl).ConfigureAwait(false);

                // Overenie: po uložení má zvolená hviezda odkaz „#close-dropdown“ (rovnako ako pri už ohodnotenom filme).
                if (FindStarHref(after, stars * 20) != "#close-dropdown")
                {
                    after = await CsfdTvTipsClient.GetPageAsync(http, $"/film/{csfdId}/prehlad/", _logger, cancellationToken).ConfigureAwait(false) ?? string.Empty;
                    if (FindStarHref(after, stars * 20) != "#close-dropdown")
                    {
                        _logger.LogWarning(
                            "ČSFD účet: hodnotenie {CsfdId} sa neuložilo, odpoveď: {Snippet}",
                            csfdId,
                            after[..Math.Min(300, after.Length)]);
                        return (false, "ČSFD hodnotenie neprijalo.");
                    }
                }

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
            ApplyReset();
            _loggedIn = false;
            return await LoginAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "ČSFD účet: test prihlásenia zlyhal");
            return (false, ex.Message);
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>Po zmene nastavení zahodí reláciu, cooldown aj cache hodnotení (uplatní sa pri ďalšom volaní pod zámkom).</summary>
    internal static void RequestReset() => _resetRequested = true;

    /// <summary>Je stránka zobrazená prihlásenému používateľovi? Odkaz na odhlásenie alebo na vlastný profil.</summary>
    internal static bool IsLoggedInPage(string html, string? nick)
    {
        if (html.Contains("/odhlasit/", StringComparison.Ordinal))
        {
            return true;
        }

        nick = nick?.Trim();
        return !string.IsNullOrEmpty(nick)
            && Regex.IsMatch(html, $@"href=""(?:https://www\.csfd\.(?:sk|cz))?/uzivatel/\d+-{Regex.Escape(nick)}/", RegexOptions.IgnoreCase);
    }

    /// <summary>Cieľ prihlasovacieho formulára: absolútna https URL na ČSFD (relatívnu rozrieši voči stránke), inak null.</summary>
    internal static Uri? ResolveLoginAction(string action, Uri page)
    {
        if (!Uri.TryCreate(page, WebUtility.HtmlDecode(action), out var uri))
        {
            return null;
        }

        var host = uri.Host;
        return uri.Scheme == Uri.UriSchemeHttps
            && (host.EndsWith(".csfd.cz", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".csfd.sk", StringComparison.OrdinalIgnoreCase))
            ? uri
            : null;
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
        // Nový slovník namiesto úpravy – volajúci GetMyRatingsAsync ho môžu práve serializovať.
        if (_ratings is not null)
        {
            _ratings = new Dictionary<int, int>(_ratings) { [csfdId] = stars };
        }
    }

    private static void ApplyReset()
    {
        if (!_resetRequested)
        {
            return;
        }

        _resetRequested = false;
        _session?.Dispose();
        _session = null;
        _loggedIn = false;
        _loginFailedAt = DateTime.MinValue;
        _ratings = null;
        _ratingsAt = DateTime.MinValue;
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
    private static async Task<string> PostFormAsync(HttpClient http, Uri url, Dictionary<string, string> fields, CancellationToken cancellationToken, string referer = "https://www.csfd.sk/prihlasenie/")
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new FormUrlEncodedContent(fields) };
        request.Headers.TryAddWithoutValidation("Origin", "https://www.csfd.sk");
        request.Headers.Referrer = new Uri(referer);
        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var html = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"ČSFD vrátilo HTTP {(int)response.StatusCode}: {html[..Math.Min(200, html.Length)]}");
        }

        return html;
    }

    /// <summary>Prihlásenie s ochranou: po neúspechu ďalší pokus až po <see cref="LoginCooldown"/>.</summary>
    private async Task<(bool Ok, string Message)> LoginAsync(CancellationToken cancellationToken)
    {
        var wait = _loginFailedAt + LoginCooldown - DateTime.UtcNow;
        if (wait > TimeSpan.Zero)
        {
            return (false, $"Prihlásenie na ČSFD nedávno zlyhalo – ďalší pokus o {Math.Ceiling(wait.TotalMinutes)} min (alebo po uložení nastavení).");
        }

        try
        {
            return await LoginCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            _loginFailedAt = DateTime.UtcNow;
            throw;
        }
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
            _loginFailedAt = DateTime.UtcNow;
            return (false, "Prihlasovací formulár ČSFD sa nenašiel (zmenil sa web?).");
        }

        // Heslo posielame len na ČSFD cez https.
        var action = ResolveLoginAction(form.Groups[1].Value, new Uri("https://www.csfd.sk/prihlasenie/"));
        if (action is null)
        {
            _loginFailedAt = DateTime.UtcNow;
            _logger.LogWarning("ČSFD účet: prihlasovací formulár mieri mimo ČSFD ({Action}), heslo neposielam", form.Groups[1].Value);
            return (false, "Prihlasovací formulár ČSFD mieri na neočakávanú adresu – heslo sa neposlalo.");
        }

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
            _loginFailedAt = DateTime.UtcNow;
            return (false, "ČSFD: " + WebUtility.HtmlDecode(error.Groups[1].Value).Trim());
        }

        var check = await CsfdTvTipsClient.GetPageAsync(_session, "/", _logger, cancellationToken).ConfigureAwait(false) ?? html;
        _loggedIn = IsLoggedInPage(check, config.CsfdNick);
        _loginFailedAt = _loggedIn ? DateTime.MinValue : DateTime.UtcNow;
        _logger.LogInformation("ČSFD účet: prihlásenie {Result}", _loggedIn ? "OK" : "zlyhalo");
        return _loggedIn ? (true, "Prihlásenie na ČSFD je OK.") : (false, "Prihlásenie na ČSFD zlyhalo – skontroluj prezývku a heslo.");
    }
}
