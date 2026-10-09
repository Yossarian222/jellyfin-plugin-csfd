using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Csfd.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Csfd.Api;

/// <summary>Jedna položka zoznamu „Chcem vidieť“ z ČSFD profilu.</summary>
public sealed record CsfdWatchlistItem(int CsfdId, string Title, int? Year);

/// <summary>
/// Zoznam „Chcem vidieť“ (cz „Chci vidět“) z ČSFD profilu nastaveného v plugine.
/// Najprv csfd.sk/…/chcem-vidiet/, ak nič, csfd.cz/…/chci-videt/; súkromný zoznam cez prihlásenú reláciu účtu.
/// Cache 6 h v pamäti aj na disku; pri výpadku ostáva stará.
/// </summary>
public sealed class CsfdWatchlistClient
{
    internal const int MaxPages = 5;

    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(6);

    private static readonly Regex ProfileRx = new(@"/uzivatel/(\d+-[^/?#]+)", RegexOptions.Compiled);
    /// <summary>Navigácia, bočný panel a skripty – nikdy nie sú súčasťou zoznamu (header nie: ČSFD ním obaľuje aj názov v článku).</summary>
    private static readonly Regex ChromeRx = new(
        @"<(nav|aside|script|style|noscript)\b[^>]*>.*?</\1>",
        RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.IgnoreCase);

    private static readonly Regex PageFooterRx = new(@"<footer\b[^>]*>.*?</footer>", RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.IgnoreCase);

    /// <summary>Nadpis sekcie so zoznamom („Chcem vidieť“, „Chci vidět“).</summary>
    private static readonly Regex ListHeadingRx = new(
        @"<h[1-3]\b[^>]*>(?:(?!</h[1-3]>).)*?(?:Chcem\s+vidie(?:ť|&#357;)|Chci\s+vid(?:ě|&#283;)t)",
        RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.IgnoreCase);

    private static readonly Regex NextHeadingRx = new(@"<h[12]\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex ContainerRx = new(@"<(?:article|tr|li)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex FilmLinkRx = new(
        @"<a\b(?<attrs>[^>]*?)href=""(?:https?://www\.csfd\.(?:sk|cz))?/film/(?<id>\d+)-[^""]*""(?<attrs2>[^>]*)>(?<text>.*?)</a>",
        RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.IgnoreCase);

    private static readonly Regex TitleClassRx = new(
        @"<a\b[^>]*href=""(?:https?://www\.csfd\.(?:sk|cz))?/film/(?<id>\d+)-[^""]*""[^>]*class=""[^""]*film-title-name[^""]*""[^>]*>(?<text>.*?)</a>"
        + @"|<a\b[^>]*class=""[^""]*film-title-name[^""]*""[^>]*href=""(?:https?://www\.csfd\.(?:sk|cz))?/film/(?<id>\d+)-[^""]*""[^>]*>(?<text>.*?)</a>",
        RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.IgnoreCase);

    private static readonly Regex InfoYearRx = new(@"class=""[^""]*\binfo\b[^""]*""[^>]*>\s*\(?\s*(\d{4})\s*\)?", RegexOptions.Compiled);
    private static readonly Regex ParenYearRx = new(@"\((\d{4})\)", RegexOptions.Compiled);
    private static readonly Regex TagRx = new(@"<[^>]+>", RegexOptions.Compiled);
    private static readonly Regex SpaceRx = new(@"\s+", RegexOptions.Compiled);

    private static readonly SemaphoreSlim Gate = new(1, 1);

    private static string? _profile;
    private static List<CsfdWatchlistItem>? _items;
    private static DateTime _at = DateTime.MinValue;

    private readonly ILogger<CsfdWatchlistClient> _logger;
    private readonly CsfdAccountClient _account;

    public CsfdWatchlistClient(ILogger<CsfdWatchlistClient> logger, CsfdAccountClient account)
    {
        _logger = logger;
        _account = account;
    }

    private static PluginConfiguration Config => Plugin.Instance?.Configuration ?? new PluginConfiguration();

    /// <summary>Položky v poradí z ČSFD; prázdny zoznam, ak profil nie je nastavený alebo ČSFD neodpovedá (a nie je cache).</summary>
    public async Task<IReadOnlyList<CsfdWatchlistItem>> GetWatchlistAsync(CancellationToken cancellationToken)
    {
        var match = ProfileRx.Match(Config.CsfdProfileUrl ?? string.Empty);
        if (!match.Success)
        {
            return Array.Empty<CsfdWatchlistItem>();
        }

        var profile = match.Groups[1].Value;
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_profile != profile)
            {
                _profile = profile;
                _items = null;
                _at = DateTime.MinValue;
            }

            if (_items is not null && DateTime.UtcNow - _at < CacheTtl)
            {
                return _items;
            }

            var cacheFile = CacheFile();
            if (_items is null && cacheFile is not null && File.Exists(cacheFile))
            {
                try
                {
                    var stored = JsonSerializer.Deserialize<StoredWatchlist>(await File.ReadAllTextAsync(cacheFile, cancellationToken).ConfigureAwait(false));
                    if (stored?.Profile == profile && stored.Items is not null)
                    {
                        _items = stored.Items;
                        _at = File.GetLastWriteTimeUtc(cacheFile);
                        if (DateTime.UtcNow - _at < CacheTtl)
                        {
                            return _items;
                        }
                    }
                }
                catch (JsonException)
                {
                    // Poškodený súbor = ako keby nebol.
                }
            }

            var fresh = await DownloadAsync(profile, cancellationToken).ConfigureAwait(false);
            if (fresh is null || (fresh.Count == 0 && _items is { Count: > 0 }))
            {
                // Výpadok alebo zmenená stránka – stará cache je lepšia než nič a neprepíšeme ju.
                return _items ?? (IReadOnlyList<CsfdWatchlistItem>)Array.Empty<CsfdWatchlistItem>();
            }

            _items = fresh;
            _at = DateTime.UtcNow;
            if (cacheFile is not null)
            {
                var tmp = cacheFile + ".tmp";
                await File.WriteAllTextAsync(tmp, JsonSerializer.Serialize(new StoredWatchlist { Profile = profile, Items = fresh }), cancellationToken).ConfigureAwait(false);
                File.Move(tmp, cacheFile, overwrite: true);
            }

            return fresh;
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "ČSFD Chcem vidieť: načítanie zlyhalo");
            return _items ?? (IReadOnlyList<CsfdWatchlistItem>)Array.Empty<CsfdWatchlistItem>();
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>
    /// Vytiahne položky zoznamu zo stránky: kontajnery article/tr/li s odkazom na /film/{id}-…,
    /// bez hlavičky, navigácie, päty a bočného panelu; deduplikované podľa ID, v poradí stránky.
    /// </summary>
    internal static List<CsfdWatchlistItem> ParseWatchlist(string html)
    {
        var items = new List<CsfdWatchlistItem>();
        var main = MainContent(html);
        var starts = ContainerRx.Matches(main).Select(m => m.Index).ToList();
        for (var i = 0; i < starts.Count; i++)
        {
            var end = i + 1 < starts.Count ? starts[i + 1] : main.Length;
            var part = main[starts[i]..end];

            var title = TitleClassRx.Match(part);
            var link = title.Success ? title : FilmLinkRx.Match(part);
            if (!link.Success)
            {
                continue;
            }

            var id = int.Parse(link.Groups["id"].Value, CultureInfo.InvariantCulture);
            var name = CleanText(link.Groups["text"].Value);
            if (id <= 0 || name.Length == 0 || items.Exists(x => x.CsfdId == id))
            {
                continue;
            }

            items.Add(new CsfdWatchlistItem(id, name, FindYear(part[(link.Index + link.Length)..]) ?? FindYear(part)));
        }

        return items;
    }

    /// <summary>
    /// Hlavný obsah stránky: &lt;main&gt; (ak je), bez navigácie, bočného panelu a skriptov;
    /// ak je v ňom nadpis „Chcem vidieť“/„Chci vidět“, len úsek od neho po ďalší nadpis h1/h2.
    /// </summary>
    internal static string MainContent(string html)
    {
        var start = html.IndexOf("<main", StringComparison.OrdinalIgnoreCase);
        if (start >= 0)
        {
            var end = html.IndexOf("</main>", start, StringComparison.OrdinalIgnoreCase);
            html = end < 0 ? html[start..] : html[start..end];
        }
        else
        {
            // Bez <main> je päta stránky súčasťou textu – odkazy v nej nie sú zoznam.
            html = PageFooterRx.Replace(html, string.Empty);
        }

        // Vnorené bloky rovnakého typu sú zriedkavé; opakujeme, kým je čo odstrániť.
        string previous;
        do
        {
            previous = html;
            html = ChromeRx.Replace(html, string.Empty);
        }
        while (html.Length != previous.Length);

        var heading = ListHeadingRx.Match(html);
        if (heading.Success)
        {
            var from = heading.Index + heading.Length;
            var next = NextHeadingRx.Match(html, from);
            var section = next.Success ? html[heading.Index..next.Index] : html[heading.Index..];
            if (ContainerRx.IsMatch(section) && FilmLinkRx.IsMatch(section))
            {
                return section;
            }
        }

        return html;
    }

    /// <summary>Ďalšia strana zoznamu (?page=n+1) je na stránke odkazovaná?</summary>
    internal static bool HasNextPage(string html, int page)
        => html.Contains($"page={page + 1}", StringComparison.Ordinal);

    /// <summary>Kandidáti URL zoznamu: SK, potom CZ.</summary>
    internal static IReadOnlyList<string> WatchlistUrls(string profile) => new[]
    {
        $"https://www.csfd.sk/uzivatel/{profile}/chcem-vidiet/",
        $"https://www.csfd.cz/uzivatel/{profile}/chci-videt/"
    };

    private static int? FindYear(string text)
    {
        var m = InfoYearRx.Match(text);
        if (!m.Success)
        {
            m = ParenYearRx.Match(text);
        }

        if (!m.Success)
        {
            return null;
        }

        var year = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        return year is >= 1870 and <= 2100 ? year : null;
    }

    private static string CleanText(string html)
        => SpaceRx.Replace(WebUtility.HtmlDecode(TagRx.Replace(html, " ")), " ").Trim();

    /// <summary>Null = ČSFD sa nepodarilo načítať vôbec (ani anonymne, ani prihlásene).</summary>
    private async Task<List<CsfdWatchlistItem>?> DownloadAsync(string profile, CancellationToken cancellationToken)
    {
        var anyPage = false;
        using var http = CsfdTvTipsClient.CreateHttpClient();
        foreach (var url in WatchlistUrls(profile))
        {
            var (items, loaded) = await DownloadListAsync(url, u => CsfdTvTipsClient.GetPageAsync(http, u, _logger, cancellationToken), cancellationToken).ConfigureAwait(false);
            anyPage |= loaded;
            if (items.Count > 0)
            {
                _logger.LogInformation("ČSFD Chcem vidieť: {Count} položiek z {Url}", items.Count, url);
                return items;
            }
        }

        if (CsfdAccountClient.HasCredentials)
        {
            // Súkromný zoznam vidí len prihlásený vlastník.
            foreach (var url in WatchlistUrls(profile))
            {
                var (items, loaded) = await DownloadListAsync(url, u => _account.GetPageLoggedInAsync(u, cancellationToken), cancellationToken).ConfigureAwait(false);
                anyPage |= loaded;
                if (items.Count > 0)
                {
                    _logger.LogInformation("ČSFD Chcem vidieť: {Count} položiek z {Url} (prihlásený)", items.Count, url);
                    return items;
                }
            }
        }

        _logger.LogInformation("ČSFD Chcem vidieť: zoznam profilu {Profile} je prázdny alebo nedostupný", profile);
        return anyPage ? new List<CsfdWatchlistItem>() : null;
    }

    /// <summary>Načíta najviac <see cref="MaxPages"/> strán zoznamu; 404 = taký zoznam nie je.</summary>
    private async Task<(List<CsfdWatchlistItem> Items, bool Loaded)> DownloadListAsync(
        string url,
        Func<string, Task<string?>> getPage,
        CancellationToken cancellationToken)
    {
        var items = new List<CsfdWatchlistItem>();
        var loaded = false;
        for (var page = 1; page <= MaxPages; page++)
        {
            string? html;
            try
            {
                html = await getPage(page > 1 ? $"{url}?page={page}" : url).ConfigureAwait(false);
            }
            catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                loaded = true;
                break;
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogDebug(ex, "ČSFD Chcem vidieť: {Url} strana {Page} zlyhala", url, page);
                break;
            }

            if (html is null)
            {
                break;
            }

            loaded = true;
            var before = items.Count;
            foreach (var item in ParseWatchlist(html))
            {
                if (!items.Exists(x => x.CsfdId == item.CsfdId))
                {
                    items.Add(item);
                }
            }

            if (items.Count == before || !HasNextPage(html, page))
            {
                break;
            }

            await Task.Delay(300, cancellationToken).ConfigureAwait(false);
        }

        return (items, loaded);
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
        return Path.Combine(dir, "watchlist.json");
    }

    private sealed class StoredWatchlist
    {
        public string? Profile { get; set; }

        public List<CsfdWatchlistItem>? Items { get; set; }
    }
}
