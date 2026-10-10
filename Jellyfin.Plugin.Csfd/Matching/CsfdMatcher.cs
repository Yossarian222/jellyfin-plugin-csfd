using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Csfd.Api;
using MediaBrowser.Controller.Providers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Csfd.Matching;

/// <summary>Kandidát z ČSFD so skóre zhody 0–100.</summary>
public sealed record CsfdCandidate(CsfdSearchItem Item, int Score);

/// <summary>
/// Nájde ČSFD záznam k položke Jellyfinu: ČSFD ID → URL v názve → vyhľadávanie podľa názvu
/// a originálneho názvu so skórovaním (názov, rok, typ). Neistú zhodu nepriradí.
/// </summary>
public sealed partial class CsfdMatcher
{
    private static readonly string[] MovieTypes = { "film", "tv-film", "student-film", "amateur-film", "theatrical", "concert", "video-compilation" };
    private static readonly string[] SeriesTypes = { "series", "tv-show" };

    private readonly CsfdApiClient _client;
    private readonly ILogger<CsfdMatcher> _logger;

    public CsfdMatcher(CsfdApiClient client, ILogger<CsfdMatcher> logger)
    {
        _client = client;
        _logger = logger;
    }

    /// <summary>ČSFD ID z ProviderIds alebo z URL/ID vloženého do názvu (Identify dialóg).</summary>
    public static int? GetExplicitId(ItemLookupInfo info)
    {
        if (info.ProviderIds.TryGetValue(Plugin.ProviderKey, out var raw) && TryParseId(raw, out var id))
        {
            return id;
        }

        // Z názvu len ČSFD URL alebo "csfd:ID" – holé číslo nikdy (film "1917" nie je ČSFD ID 1917;
        // Jellyfin pri ručnom refreshi nastavuje IsAutomated=false, takže sa na to spoliehať nedá).
        return TryParseId(info.Name, out var fromName, allowBareNumber: false) ? fromName : null;
    }

    /// <summary>
    /// Holé číslo v názve v Identify dialógu – môže to byť ČSFD ID aj názov filmu („1917“),
    /// preto ho provider ponúkne ako ďalší výsledok popri bežnom vyhľadávaní.
    /// </summary>
    public static int? GetBareNumberFromName(ItemLookupInfo info)
        => info.Name is not null && info.Name.Trim().All(char.IsDigit) && TryParseId(info.Name, out var id) ? id : null;

    /// <summary>Akceptuje "8852", "csfd:8852" aj celú URL ČSFD.</summary>
    public static bool TryParseId(string? value, out int id, bool allowBareNumber = true)
    {
        id = 0;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var v = value.Trim();
        var m = UrlIdRegex().Match(v);
        if (m.Success)
        {
            // Pri URL /film/{seriál}/{epizóda}/ berieme posledné ID.
            var ids = m.Groups["id"].Captures;
            return int.TryParse(ids[^1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out id);
        }

        if (v.StartsWith("csfd:", StringComparison.OrdinalIgnoreCase))
        {
            v = v[5..].Trim();
        }
        else if (!allowBareNumber)
        {
            return false;
        }

        return v.All(char.IsDigit) && int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out id) && id > 0;
    }

    /// <summary>Zoradení kandidáti pre film alebo seriál.</summary>
    public async Task<IReadOnlyList<CsfdCandidate>> FindCandidatesAsync(ItemLookupInfo info, bool series, CancellationToken cancellationToken)
    {
        var queries = new List<string>();
        foreach (var q in new[] { info.Name, info.OriginalTitle })
        {
            var clean = CleanQuery(q);
            if (!string.IsNullOrEmpty(clean) && !queries.Contains(clean, StringComparer.OrdinalIgnoreCase))
            {
                queries.Add(clean);
            }
        }

        var year = info.Year ?? info.PremiereDate?.Year;
        var all = new Dictionary<int, CsfdCandidate>();

        foreach (var query in queries)
        {
            var result = await _client.SearchAsync(query, cancellationToken).ConfigureAwait(false);
            if (result is null)
            {
                continue;
            }

            var items = series
                ? (result.TvSeries ?? new List<CsfdSearchItem>()).Concat(result.Movies ?? new List<CsfdSearchItem>())
                : (result.Movies ?? new List<CsfdSearchItem>()).Concat(result.TvSeries ?? new List<CsfdSearchItem>());

            foreach (var item in items)
            {
                if (item.Id is not > 0)
                {
                    continue;
                }

                var itemId = item.Id.Value;
                var score = Score(item.Title, new[] { info.Name, info.OriginalTitle }, year, item.Year, item.Type, series);
                if (!all.TryGetValue(itemId, out var existing) || existing.Score < score)
                {
                    all[itemId] = new CsfdCandidate(item, score);
                }
            }

            // Jasná zhoda – druhé vyhľadávanie netreba.
            if (all.Values.Any(c => c.Score >= 95))
            {
                break;
            }
        }

        return all.Values.OrderByDescending(c => c.Score).ToList();
    }

    /// <summary>
    /// Najlepšie ČSFD ID nad prahom. Ak je najlepší kandidát neistý, overí top 3 cez detail
    /// (porovná aj s „ďalšími názvami“ – originál, US, SK…).
    /// </summary>
    public async Task<int?> FindBestIdAsync(ItemLookupInfo info, bool series, int minScore, CancellationToken cancellationToken)
    {
        var explicitId = GetExplicitId(info);
        if (explicitId.HasValue)
        {
            if (!await LooksLikeTitleMistakenForIdAsync(info, explicitId.Value, cancellationToken).ConfigureAwait(false))
            {
                return explicitId;
            }

            _logger.LogInformation("ČSFD: uložené ID {Id} pre {Name} ({Year}) je zjavne omyl (názov = číslo), hľadám znova", explicitId, info.Name, info.Year);
        }

        var candidates = await FindCandidatesAsync(info, series, cancellationToken).ConfigureAwait(false);
        if (candidates.Count == 0)
        {
            _logger.LogInformation("ČSFD: nič sa nenašlo pre {Name} ({Year})", info.Name, info.Year);
            return null;
        }

        var best = candidates[0];
        if (best.Score >= minScore && (candidates.Count == 1 || best.Score - candidates[1].Score >= 5))
        {
            return best.Item.Id;
        }

        // Neistá alebo nejednoznačná zhoda → overíme top 3 aj cez „ďalšie názvy“ z detailu.
        var year = info.Year ?? info.PremiereDate?.Year;
        var verified = new List<CsfdCandidate>();
        foreach (var candidate in candidates.Where(c => c.Score >= 25).Take(3))
        {
            var detail = await _client.GetMovieAsync(candidate.Item.Id!.Value, "sk", cancellationToken).ConfigureAwait(false);
            if (detail is null)
            {
                continue;
            }

            var titles = new List<string?> { detail.Title };
            titles.AddRange(detail.TitlesOther?.Select(t => t.Title) ?? Enumerable.Empty<string?>());
            // Typ berieme z vyhľadávania (CZ stránka) – na SK stránke ho csfd-api nerozpozná.
            var score = titles
                .Select(t => Score(t, new[] { info.Name, info.OriginalTitle }, year, detail.Year ?? candidate.Item.Year, candidate.Item.Type, series))
                .DefaultIfEmpty(0)
                .Max();
            verified.Add(new CsfdCandidate(candidate.Item, score));
        }

        var ranked = verified.OrderByDescending(c => c.Score).ToList();
        if (ranked.Count > 0 && ranked[0].Score >= minScore && (ranked.Count == 1 || ranked[0].Score - ranked[1].Score >= 5))
        {
            return ranked[0].Item.Id;
        }

        var bestVerified = ranked.Count > 0 ? ranked[0].Score : 0;

        _logger.LogInformation(
            "ČSFD: neistá zhoda pre {Name} ({Year}), najlepší {Title} ({CsfdYear}) skóre {Score} – nepriraďujem",
            info.Name,
            year,
            best.Item.Title,
            best.Item.Year,
            Math.Max(best.Score, bestVerified));
        return null;
    }

    /// <summary>
    /// Oprava starej chyby: film „1917“ dostal ČSFD ID 1917. Ak sa uložené ID zhoduje s číselným názvom
    /// a rok ČSFD záznamu nesedí, ID ignorujeme a hľadáme normálne (Replace all metadata ho prepíše).
    /// </summary>
    private async Task<bool> LooksLikeTitleMistakenForIdAsync(ItemLookupInfo info, int id, CancellationToken cancellationToken)
    {
        var name = info.Name?.Trim();
        if (string.IsNullOrEmpty(name) || !name.All(char.IsDigit) || name != id.ToString(CultureInfo.InvariantCulture))
        {
            return false;
        }

        var year = info.Year ?? info.PremiereDate?.Year;
        if (!year.HasValue)
        {
            return false;
        }

        var detail = await _client.GetMovieAsync(id, "sk", cancellationToken).ConfigureAwait(false);
        return detail?.Year is not int csfdYear || Math.Abs(csfdYear - year.Value) > 1;
    }

    /// <summary>
    /// Najnižšia povolená hranica automatickej zhody: samotný zhodný rok (+30) a typ bez podobnosti názvu
    /// dá 30, preto musí k hranici prispieť aj názov (50 ≈ aspoň 30 % podobnosť pri zhodnom roku).
    /// </summary>
    public const int MinScoreFloor = 50;

    /// <summary>Skóre 0–100: podobnosť názvu (max 70) + rok (±30) + typ (−15 pri nezhode).</summary>
    public static int Score(string? candidateTitle, IEnumerable<string?> wanted, int? wantedYear, int? candidateYear, string? candidateType, bool series)
    {
        var cand = CsfdText.Normalize(candidateTitle);
        if (cand.Length == 0)
        {
            return 0;
        }

        double sim = 0;
        foreach (var w in wanted)
        {
            var n = CsfdText.Normalize(CleanQuery(w));
            if (n.Length == 0)
            {
                continue;
            }

            // "Pulp Fiction" vs "Pulp Fiction: Historky z podsvetí"
            sim = Math.Max(sim, TitleSimilarity(cand, n));
        }

        double score = sim * 70;

        if (wantedYear.HasValue && candidateYear.HasValue)
        {
            var diff = Math.Abs(wantedYear.Value - candidateYear.Value);
            score += diff switch
            {
                0 => 30,
                1 => 18,
                2 => 5,
                _ => -30
            };
        }
        else
        {
            score += 10;
        }

        if (!string.IsNullOrEmpty(candidateType))
        {
            var typeOk = series ? SeriesTypes.Contains(candidateType) : MovieTypes.Contains(candidateType);
            if (!typeOk)
            {
                score -= 15;
            }
        }

        return (int)Math.Clamp(Math.Round(score), 0, 100);
    }

    /// <summary>Pod touto podobnosťou názvu (0–1) je spárovanie podozrivé.</summary>
    public const double SuspiciousSimilarity = 0.5;

    /// <summary>
    /// Dôvod, prečo spárovanie položky (<paramref name="name"/>, <paramref name="originalTitle"/>,
    /// <paramref name="year"/>) s ČSFD záznamom <paramref name="movie"/> vyzerá zle: niektorý zo zadaných názvov sa
    /// nepodobá na žiadny ČSFD názov (hlavný ani „ďalšie názvy“), alebo sa rok líši o viac ako 1. Null = v poriadku.
    /// </summary>
    public static string? DescribeSuspiciousMatch(string? name, string? originalTitle, int? year, CsfdMovie movie)
    {
        var csfdTitles = new List<string?> { movie.Title };
        csfdTitles.AddRange(movie.TitlesOther?.Select(t => t.Title) ?? Enumerable.Empty<string?>());
        var normalized = csfdTitles.Select(CsfdText.Normalize).Where(t => t.Length > 0).Distinct().ToList();

        var reasons = new List<string>();
        if (normalized.Count > 0)
        {
            foreach (var wanted in new[] { name, originalTitle }.Distinct())
            {
                var n = CsfdText.Normalize(CleanQuery(wanted));
                if (n.Length > 0 && normalized.All(t => TitleSimilarity(t, n) < SuspiciousSimilarity))
                {
                    reasons.Add($"názov „{wanted}“ sa nepodobá na ČSFD názvy");
                }
            }
        }

        if (year.HasValue && movie.Year.HasValue && Math.Abs(year.Value - movie.Year.Value) > 1)
        {
            reasons.Add($"rok {year} vs. {movie.Year} na ČSFD");
        }

        return reasons.Count > 0 ? string.Join(", ", reasons) : null;
    }

    /// <summary>Podobnosť normalizovaných názvov; názov s podtitulom („Pulp Fiction: Historky…“) sa počíta ako 0.9.</summary>
    private static double TitleSimilarity(string cand, string n)
    {
        var s = Similarity(cand, n);
        if (s < 0.9 && n.Length >= 4 && (cand.StartsWith(n + " ", StringComparison.Ordinal) || n.StartsWith(cand + " ", StringComparison.Ordinal)))
        {
            s = 0.9;
        }

        return s;
    }

    /// <summary>1 − normalizovaná Levenshteinova vzdialenosť.</summary>
    public static double Similarity(string a, string b)
    {
        if (a == b)
        {
            return 1;
        }

        if (a.Length == 0 || b.Length == 0)
        {
            return 0;
        }

        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++)
        {
            prev[j] = j;
        }

        for (var i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
            }

            (prev, cur) = (cur, prev);
        }

        return 1.0 - (prev[b.Length] / (double)Math.Max(a.Length, b.Length));
    }

    /// <summary>Odstráni z názvu rok v zátvorke a tagy vydania („Film (2019) [1080p]“).</summary>
    public static string CleanQuery(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var v = BracketRegex().Replace(value, " ");
        v = YearInParensRegex().Replace(v, " ");
        return SpacesRegex().Replace(v, " ").Trim();
    }

    [GeneratedRegex(@"csfd\.(?:cz|sk)(?:/(?:sk|en))?/film/(?:(?<id>\d+)[^/?#]*(?:/|$))+", RegexOptions.IgnoreCase)]
    private static partial Regex UrlIdRegex();

    [GeneratedRegex(@"\[[^\]]*\]")]
    private static partial Regex BracketRegex();

    [GeneratedRegex(@"\((?:19|20)\d{2}\)")]
    private static partial Regex YearInParensRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex SpacesRegex();
}
