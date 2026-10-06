using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.Csfd.Api;
using Jellyfin.Plugin.Csfd.Configuration;

namespace Jellyfin.Plugin.Csfd.Matching;

/// <summary>Jazyk textu odhadnutý z diakritiky a typických slov.</summary>
public enum TextLanguage
{
    Unknown,
    Slovak,
    Czech,
    English
}

/// <summary>
/// Pomocné funkcie: rozpoznanie jazyka popisu, výber SK názvu, premiéry, normalizácia názvov.
/// </summary>
public static partial class CsfdText
{
    private static readonly string[] SlovakWords =
    {
        "sa", "sú", "ktorý", "ktorá", "ktoré", "ktorí", "alebo", "pretože", "ako", "keď", "ich", "svoj", "svoju", "nie",
        "byť", "bol", "bola", "boli", "iba", "ešte", "tiež", "príbeh", "sme", "ste", "som", "čo"
    };

    private static readonly string[] CzechWords =
    {
        "se", "jsou", "který", "která", "které", "kteří", "nebo", "protože", "jako", "když", "jejich", "svůj", "svou", "není",
        "být", "pouze", "ještě", "také", "příběh", "jsme", "jste", "jsem", "co"
    };

    private static readonly string[] EnglishWords =
    {
        "the", "and", "of", "to", "is", "in", "his", "her", "with", "for", "who", "when", "that", "their", "from"
    };

    private static readonly string[] SlovakiaNames = { "Slovensko", "Slovakia", "Slovenská republika" };
    private static readonly string[] CzechNames = { "Česko", "Česká republika", "Czech Republic", "Czechia" };
    private static readonly string[] CzechoslovakNames = { "Československo", "Czechoslovakia" };

    /// <summary>Odhadne jazyk textu.</summary>
    public static TextLanguage DetectLanguage(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return TextLanguage.Unknown;
        }

        var lower = text.ToLowerInvariant();
        double sk = 0, cs = 0, en = 0;

        foreach (var c in lower)
        {
            switch (c)
            {
                // len slovenské
                case 'ä': case 'ô': case 'ľ': case 'ĺ': case 'ŕ':
                    sk += 3;
                    break;
                // len české
                case 'ř': case 'ů': case 'ě':
                    cs += 3;
                    break;
            }
        }

        var words = WordRegex().Matches(lower).Select(m => m.Value).ToArray();
        var letters = lower.Count(char.IsLetter);
        var asciiLetters = lower.Count(c => c is >= 'a' and <= 'z');

        // Dlhší český text takmer vždy obsahuje ě/ř/ů. Dlhý text so stredoeurópskou diakritikou bez nich je skôr slovenský.
        var csSpecial = cs > 0;
        var hasCentralEuropean = lower.Any(c => "áéíóúýčšžďťň".Contains(c, StringComparison.Ordinal));
        if (letters >= 80 && !csSpecial && hasCentralEuropean)
        {
            sk += 4;
        }

        foreach (var w in words)
        {
            if (Array.IndexOf(SlovakWords, w) >= 0)
            {
                sk += 1;
            }

            if (Array.IndexOf(CzechWords, w) >= 0)
            {
                cs += 1;
            }

            if (Array.IndexOf(EnglishWords, w) >= 0)
            {
                en += 1.5;
            }
        }

        // Text takmer bez diakritiky s anglickými slovami = angličtina.
        if (letters > 0 && asciiLetters / (double)letters > 0.97 && en >= 2 && en > sk && en > cs)
        {
            return TextLanguage.English;
        }

        if (sk == 0 && cs == 0)
        {
            return en > 0 ? TextLanguage.English : TextLanguage.Unknown;
        }

        if (sk > cs * 1.2)
        {
            return TextLanguage.Slovak;
        }

        if (cs > sk * 1.2)
        {
            return TextLanguage.Czech;
        }

        return TextLanguage.Unknown;
    }

    /// <summary>
    /// Vyberie popis podľa poradia SK → EN → (CZ). Vracia null, ak nič nevyhovuje –
    /// vtedy popis doplní ďalší fetcher v poradí (TMDb / OMDb).
    /// </summary>
    public static string? PickOverview(IEnumerable<CsfdMovie?> sources, PluginConfiguration config)
        => PickOverviewWithLanguage(sources, config).Text;

    /// <summary>Ako <see cref="PickOverview"/>, plus ISO kód jazyka vybraného popisu (sk/en/cs).</summary>
    public static (string? Text, string? Language) PickOverviewWithLanguage(IEnumerable<CsfdMovie?> sources, PluginConfiguration config)
    {
        var all = sources
            .Where(s => s?.Descriptions is not null)
            .SelectMany(s => s!.Descriptions!)
            .Select(CleanPlot)
            .Where(d => d.Length > 0)
            .Distinct()
            .Select(d => (Text: d, Lang: DetectLanguage(d)))
            .ToList();

        var sk = all.FirstOrDefault(x => x.Lang == TextLanguage.Slovak).Text;
        if (sk is not null)
        {
            return (sk, "sk");
        }

        if (config.FallbackEnglish)
        {
            var en = all.FirstOrDefault(x => x.Lang == TextLanguage.English).Text;
            if (en is not null)
            {
                return (en, "en");
            }
        }

        if (config.FallbackCzech)
        {
            var cs = all.FirstOrDefault(x => x.Lang is TextLanguage.Czech or TextLanguage.Unknown).Text;
            if (cs is not null)
            {
                return (cs, "cs");
            }
        }

        return (null, null);
    }

    /// <summary>True, ak SK stránka neobsahuje slovenský popis (treba skúsiť EN).</summary>
    public static bool HasSlovakOverview(CsfdMovie? movie)
        => movie?.Descriptions?.Any(d => DetectLanguage(CleanPlot(d)) == TextLanguage.Slovak) == true;

    /// <summary>
    /// Slovenský názov z detailu stiahnutého so <c>language=sk</c>. Na SK stránke je lokalizovaný názov
    /// priamo v <see cref="CsfdMovie.Title"/> a český je medzi „ďalšími názvami“ (Česko).
    /// Vráti null, ak SK názov neexistuje (nechceme prepísať názov českým).
    /// </summary>
    public static string? PickSlovakTitle(CsfdMovie movie, bool allowCzech)
    {
        var altSk = movie.TitlesOther?.FirstOrDefault(t => IsOneOf(t.Country, SlovakiaNames) && !string.IsNullOrWhiteSpace(t.Title));
        if (altSk?.Title is not null)
        {
            return altSk.Title.Trim();
        }

        var title = movie.Title?.Trim();
        if (string.IsNullOrEmpty(title))
        {
            return null;
        }

        var origins = movie.Origins ?? new List<string>();
        if (origins.Any(o => IsOneOf(o, SlovakiaNames) || IsOneOf(o, CzechoslovakNames)))
        {
            return title;
        }

        var lang = DetectLanguage(title);
        var czech = movie.TitlesOther?.FirstOrDefault(t => IsOneOf(t.Country, CzechNames) && !string.IsNullOrWhiteSpace(t.Title))?.Title?.Trim();
        if (czech is not null)
        {
            // „Historky z podsvetia“ vs. „Historky z podsvětí“ → hlavný názov je slovenský.
            if (!string.Equals(Normalize(title), Normalize(czech), StringComparison.Ordinal) && lang != TextLanguage.Czech)
            {
                return title;
            }

            // Hlavný názov = český → SK preklad neexistuje.
            return allowCzech ? title : null;
        }

        // Bez českého alternatívneho názvu: rovnaký CZ/SK názov alebo originál – použijeme, ak nie je český.
        // Česká produkcia bez SK prekladu má na SK stránke český originál.
        if (lang == TextLanguage.Czech || origins.Any(o => IsOneOf(o, CzechNames)))
        {
            return allowCzech ? title : null;
        }

        return title;
    }

    /// <summary>
    /// Názov epizódy. <paramref name="czechListTitle"/> je zo zoznamu na CZ stránke, <paramref name="skDetailTitle"/>
    /// z nadpisu SK detailu. Český názov sa použije len ak je povolený – inak doplní TMDb.
    /// </summary>
    public static string? PickEpisodeTitle(string? czechListTitle, CsfdMovie? skDetail, string? skDetailTitle, bool allowCzech)
    {
        var alt = skDetail?.TitlesOther?.FirstOrDefault(t => IsOneOf(t.Country, SlovakiaNames) && !string.IsNullOrWhiteSpace(t.Title));
        if (alt?.Title is not null)
        {
            return alt.Title.Trim();
        }

        var cz = czechListTitle?.Trim();
        var sk = skDetailTitle?.Trim();

        // Rovnaké pravidlo ako pri filmoch: SK nadpis ≠ český alternatívny názov → je slovenský.
        var czAlt = skDetail?.TitlesOther?.FirstOrDefault(t => IsOneOf(t.Country, CzechNames) && !string.IsNullOrWhiteSpace(t.Title))?.Title?.Trim();
        if (string.IsNullOrEmpty(cz))
        {
            cz = czAlt;
        }

        // SK stránka ukázala iný názov ako CZ zoznam → je to lokalizovaný SK názov.
        if (!string.IsNullOrEmpty(sk) && !string.IsNullOrEmpty(cz)
            && !string.Equals(Normalize(sk), Normalize(cz), StringComparison.Ordinal)
            && DetectLanguage(sk) != TextLanguage.Czech)
        {
            return sk;
        }

        var candidate = !string.IsNullOrEmpty(sk) ? sk : cz;
        if (string.IsNullOrEmpty(candidate))
        {
            return null;
        }

        var lang = DetectLanguage(candidate);
        if (lang is TextLanguage.Slovak or TextLanguage.English)
        {
            return candidate;
        }

        return allowCzech ? candidate : null;
    }

    /// <summary>Premiéra: SK kino → SK čokoľvek → CZ kino → CZ → najskoršia.</summary>
    public static DateTime? PickPremiereDate(CsfdMovie movie)
    {
        var list = (movie.Premieres ?? new List<CsfdPremiere>())
            .Select(p => (P: p, Date: ParseDate(p.Date)))
            .Where(x => x.Date.HasValue)
            .ToList();
        if (list.Count == 0)
        {
            return null;
        }

        static bool IsCinema(CsfdPremiere p) => p.Format?.Contains("kin", StringComparison.OrdinalIgnoreCase) == true
                                               || p.Format?.Contains("cinema", StringComparison.OrdinalIgnoreCase) == true;

        DateTime? Earliest(Func<CsfdPremiere, bool> filter)
            => list.Where(x => filter(x.P)).Select(x => x.Date).Min();

        return Earliest(p => IsOneOf(p.Country, SlovakiaNames) && IsCinema(p))
            ?? Earliest(p => IsOneOf(p.Country, SlovakiaNames))
            ?? Earliest(p => IsOneOf(p.Country, CzechNames) && IsCinema(p))
            ?? Earliest(p => IsOneOf(p.Country, CzechNames))
            ?? list.Min(x => x.Date);
    }

    public static DateTime? ParseDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var formats = new[] { "dd.MM.yyyy", "d.M.yyyy", "yyyy-MM-dd" };
        return DateTime.TryParseExact(value.Trim(), formats, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var d)
            ? d
            : null;
    }

    /// <summary>Kód epizódy „S01E08“ / „E01“ → (sezóna, epizóda).</summary>
    public static (int? Season, int? Episode) ParseEpisodeCode(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return (null, null);
        }

        var m = EpisodeCodeRegex().Match(code);
        if (!m.Success)
        {
            return (null, null);
        }

        int? season = m.Groups["s"].Success ? int.Parse(m.Groups["s"].Value, CultureInfo.InvariantCulture) : null;
        int? episode = int.Parse(m.Groups["e"].Value, CultureInfo.InvariantCulture);
        return (season, episode);
    }

    /// <summary>Prvé číslo v názve sezóny („Séria 3“ → 3).</summary>
    public static int? FirstNumber(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        var m = NumberRegex().Match(text);
        return m.Success ? int.Parse(m.Value, CultureInfo.InvariantCulture) : null;
    }

    /// <summary>Normalizácia na porovnávanie: bez diakritiky, malé písmená, len písmená a čísla.</summary>
    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var formD = value.Replace("&", " and ", StringComparison.Ordinal).Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(formD.Length);
        foreach (var c in formD)
        {
            var cat = CharUnicodeInfo.GetUnicodeCategory(c);
            if (cat == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (char.IsLetterOrDigit(c))
            {
                sb.Append(char.ToLowerInvariant(c));
            }
            else if (sb.Length > 0 && sb[^1] != ' ')
            {
                sb.Append(' ');
            }
        }

        var normalized = sb.ToString().Trim();
        return LeadingArticleRegex().Replace(normalized, string.Empty);
    }

    public static bool IsOneOf(string? value, IEnumerable<string> options)
        => value is not null && options.Any(o => string.Equals(o, value.Trim(), StringComparison.OrdinalIgnoreCase));

    private static string CleanPlot(string plot)
    {
        var text = plot.Trim();
        // csfd-api niekedy nechá na konci zdroj v zátvorke, napr. "(HBO Europe)".
        text = TrailingSourceRegex().Replace(text, string.Empty).Trim();
        return WhitespaceRegex().Replace(text, " ");
    }

    [GeneratedRegex(@"[\p{L}]+")]
    private static partial Regex WordRegex();

    [GeneratedRegex(@"(?:S(?<s>\d{1,3}))?E(?<e>\d{1,4})", RegexOptions.IgnoreCase)]
    private static partial Regex EpisodeCodeRegex();

    [GeneratedRegex(@"\d+")]
    private static partial Regex NumberRegex();

    [GeneratedRegex(@"^(the|a|an) ")]
    private static partial Regex LeadingArticleRegex();

    // Zdroj popisu na konci: "(HBO Europe)", "(TV Prima)", "(Varan)" – krátke, bez interpunkcie vety.
    [GeneratedRegex(@"\s*\([^().,;:!?]{1,40}\)\s*$")]
    private static partial Regex TrailingSourceRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}
