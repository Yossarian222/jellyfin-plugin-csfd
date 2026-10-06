using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Csfd.Api;
using Jellyfin.Plugin.Csfd.Configuration;
using Jellyfin.Plugin.Csfd.Matching;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;

namespace Jellyfin.Plugin.Csfd.Providers;

/// <summary>Prenos dát z ČSFD detailu do položky Jellyfinu.</summary>
public sealed class CsfdMetadataMapper
{
    private readonly CsfdApiClient _client;

    public CsfdMetadataMapper(CsfdApiClient client)
    {
        _client = client;
    }

    internal static PluginConfiguration Config => Plugin.Instance?.Configuration ?? new PluginConfiguration();

    /// <summary>
    /// Načíta SK detail a podľa potreby EN detail (len keď na SK stránke chýba slovenský popis).
    /// Pri sezónach a epizódach EN stránku nesťahujeme – ČSFD tam anglické popisy prakticky nemá
    /// a anglický popis doplní TMDb.
    /// </summary>
    public async Task<(CsfdMovie? Sk, CsfdMovie? En)> LoadAsync(int id, CancellationToken cancellationToken, bool fetchEnglish = true)
    {
        var sk = await _client.GetMovieAsync(id, "sk", cancellationToken).ConfigureAwait(false);
        if (sk is null)
        {
            return (null, null);
        }

        CsfdMovie? en = null;
        var config = Config;
        if (fetchEnglish && config.UseOverview && config.FallbackEnglish && !CsfdText.HasSlovakOverview(sk))
        {
            en = await _client.GetMovieAsync(id, "en", cancellationToken).ConfigureAwait(false);
        }

        return (sk, en);
    }

    /// <summary>Zapíše ČSFD dáta do položky. Prázdne polia nechá na ďalší fetcher (TMDb/OMDb).</summary>
    public static void Apply<T>(MetadataResult<T> result, int csfdId, CsfdMovie sk, CsfdMovie? en, bool isTitleLevel)
        where T : BaseItem
    {
        var config = Config;
        var item = result.Item;
        string? resultLanguage = null;

        item.SetProviderId(Plugin.ProviderKey, csfdId.ToString(System.Globalization.CultureInfo.InvariantCulture));

        if (isTitleLevel && config.UseSlovakTitle)
        {
            var title = CsfdText.PickSlovakTitle(sk, config.FallbackCzech);
            if (!string.IsNullOrWhiteSpace(title))
            {
                item.Name = title;
            }
        }

        if (config.UseOverview)
        {
            var (overview, language) = CsfdText.PickOverviewWithLanguage(new[] { sk, en }, config);
            if (!string.IsNullOrWhiteSpace(overview))
            {
                item.Overview = overview;
                resultLanguage = language;
            }
        }

        if (config.UseRating && sk.Rating is >= 0 and <= 100)
        {
            // Hlavné ČSFD hodnotenie (napr. 90 %) → CommunityRating 9.0
            item.CommunityRating = (float)Math.Round(sk.Rating.Value / 10f, 1);
            if (config.AlsoCriticRating)
            {
                item.CriticRating = sk.Rating.Value;
            }
        }

        if (isTitleLevel && config.UseGenres && sk.Genres is { Count: > 0 })
        {
            item.Genres = sk.Genres.Where(g => !string.IsNullOrWhiteSpace(g)).Select(g => g.Trim()).Distinct().ToArray();
        }

        if (isTitleLevel && config.UseTags && sk.Tags is { Count: > 0 })
        {
            foreach (var tag in sk.Tags.Where(t => !string.IsNullOrWhiteSpace(t)))
            {
                item.AddTag(tag.Trim());
            }
        }

        if (isTitleLevel && sk.Origins is { Count: > 0 })
        {
            item.ProductionLocations = sk.Origins.ToArray();
        }

        if (config.UsePremiereDate)
        {
            var premiere = CsfdText.PickPremiereDate(sk);
            if (premiere.HasValue)
            {
                item.PremiereDate = premiere;
                item.ProductionYear = premiere.Value.Year;
            }
        }

        if (sk.Year.HasValue && sk.Year.Value > 1870)
        {
            // Rok výroby podľa ČSFD má prednosť pred rokom SK premiéry.
            item.ProductionYear = sk.Year;
        }

        if (config.UsePeople && sk.Creators is not null)
        {
            AddPeople(result, sk.Creators.Directors, PersonKind.Director, int.MaxValue);
            AddPeople(result, sk.Creators.Writers, PersonKind.Writer, int.MaxValue);
            AddPeople(result, sk.Creators.Music, PersonKind.Composer, int.MaxValue);
            AddPeople(result, sk.Creators.Actors, PersonKind.Actor, Math.Max(0, config.MaxActors));
        }

        result.HasMetadata = true;
        // Jazyk podľa skutočne použitého popisu – Jellyfin podľa toho rozhoduje o náhrade popisu z ďalších fetcherov.
        result.ResultLanguage = resultLanguage ?? "sk";
        result.Provider = Plugin.ProviderName;
    }

    private static void AddPeople<T>(MetadataResult<T> result, List<CsfdPerson>? people, PersonKind kind, int max)
        where T : BaseItem
    {
        if (people is null)
        {
            return;
        }

        var order = 0;
        foreach (var p in people.Where(p => !string.IsNullOrWhiteSpace(p.Name)).Take(max))
        {
            var person = new PersonInfo
            {
                Name = p.Name!.Trim(),
                Type = kind,
                SortOrder = order++
            };
            if (p.Id is > 0)
            {
                person.SetProviderId(Plugin.ProviderKey, p.Id.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }

            result.AddPerson(person);
        }
    }

    /// <summary>
    /// Výsledok pre Identify dialóg. Názov dávame originálny – Jellyfin ho po výbere použije ako vyhľadávací
    /// názov aj pre TMDb, ktorý by s lokalizovaným názvom často netrafil.
    /// </summary>
    public static RemoteSearchResult ToSearchResult(int csfdId, CsfdMovie movie)
    {
        var r = new RemoteSearchResult
        {
            Name = PickOriginalTitle(movie),
            ProductionYear = movie.Year,
            ImageUrl = FixUrl(movie.Poster),
            Overview = CsfdText.PickOverview(new[] { movie }, new PluginConfiguration { FallbackCzech = true }),
            SearchProviderName = Plugin.ProviderName,
            PremiereDate = CsfdText.PickPremiereDate(movie)
        };
        r.SetProviderId(Plugin.ProviderKey, csfdId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return r;
    }

    /// <summary>Originálny názov: „ďalší názov“ krajiny pôvodu → USA/UK → hlavný ČSFD názov.</summary>
    public static string? PickOriginalTitle(CsfdMovie movie)
    {
        var others = movie.TitlesOther ?? new List<CsfdTitleOther>();
        var origin = movie.Origins?.FirstOrDefault();
        var hit = others.FirstOrDefault(t => origin is not null && string.Equals(t.Country, origin, StringComparison.OrdinalIgnoreCase))
                  ?? others.FirstOrDefault(t => t.Country is "USA" or "Velká Británie" or "Veľká Británia" or "UK");
        return string.IsNullOrWhiteSpace(hit?.Title) ? movie.Title : hit!.Title!.Trim();
    }

    public static RemoteSearchResult ToSearchResult(CsfdCandidate candidate)
    {
        var r = new RemoteSearchResult
        {
            Name = candidate.Item.Title,
            ProductionYear = candidate.Item.Year,
            ImageUrl = FixUrl(candidate.Item.Poster),
            SearchProviderName = Plugin.ProviderName,
            Overview = $"ČSFD · zhoda {candidate.Score} %"
        };
        r.SetProviderId(Plugin.ProviderKey, candidate.Item.Id!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return r;
    }

    public static bool IsRealImage(string? url)
        => FixUrl(url) is not null;

    /// <summary>Doplní protokol (csfd-api vracia aj "//image.pmgstatic.com/..."); data: a prázdne vráti ako null.</summary>
    public static string? FixUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        var u = url.Trim();
        if (u.StartsWith("//", StringComparison.Ordinal))
        {
            u = "https:" + u;
        }

        return u.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? u : null;
    }
}
