using System.Collections.Generic;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;

namespace Jellyfin.Plugin.Csfd.ExternalIds;

/// <summary>Pole „ČSFD ID“ v editore metadát – film.</summary>
public class CsfdMovieExternalId : IExternalId
{
    public string ProviderName => Plugin.ProviderName;

    public string Key => Plugin.ProviderKey;

    public ExternalIdMediaType? Type => ExternalIdMediaType.Movie;

    public bool Supports(IHasProviderIds item) => item is Movie;
}

/// <summary>Seriál.</summary>
public class CsfdSeriesExternalId : IExternalId
{
    public string ProviderName => Plugin.ProviderName;

    public string Key => Plugin.ProviderKey;

    public ExternalIdMediaType? Type => ExternalIdMediaType.Series;

    public bool Supports(IHasProviderIds item) => item is Series;
}

/// <summary>Sezóna.</summary>
public class CsfdSeasonExternalId : IExternalId
{
    public string ProviderName => Plugin.ProviderName;

    public string Key => Plugin.ProviderKey;

    public ExternalIdMediaType? Type => ExternalIdMediaType.Season;

    public bool Supports(IHasProviderIds item) => item is Season;
}

/// <summary>Epizóda.</summary>
public class CsfdEpisodeExternalId : IExternalId
{
    public string ProviderName => Plugin.ProviderName;

    public string Key => Plugin.ProviderKey;

    public ExternalIdMediaType? Type => ExternalIdMediaType.Episode;

    public bool Supports(IHasProviderIds item) => item is Episode;
}

/// <summary>Klikateľný odkaz „ČSFD“ na detaile položky.</summary>
public class CsfdExternalUrlProvider : IExternalUrlProvider
{
    public string Name => Plugin.ProviderName;

    public IEnumerable<string> GetExternalUrls(BaseItem item)
    {
        if (item is Movie or Series or Season or Episode
            && item.TryGetProviderId(Plugin.ProviderKey, out var id)
            && !string.IsNullOrWhiteSpace(id))
        {
            // ČSFD presmeruje /film/{id}/ na správny slug (aj pri sezónach a epizódach).
            yield return $"https://www.csfd.sk/film/{id}/prehlad/";
        }
    }
}
