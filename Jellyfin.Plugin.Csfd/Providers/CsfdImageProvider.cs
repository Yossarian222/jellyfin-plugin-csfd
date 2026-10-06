using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Csfd.Api;
using Jellyfin.Plugin.Csfd.Matching;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;

namespace Jellyfin.Plugin.Csfd.Providers;

/// <summary>
/// Plagát a fotka z ČSFD. V hybridnom režime daj v knižnici TMDb nad ČSFD –
/// ČSFD obrázky sa potom použijú len tam, kde TMDb nič nemá.
/// </summary>
public class CsfdImageProvider : IRemoteImageProvider, IHasOrder
{
    private readonly CsfdApiClient _client;

    public CsfdImageProvider(CsfdApiClient client)
    {
        _client = client;
    }

    /// <inheritdoc />
    public string Name => Plugin.ProviderName;

    /// <inheritdoc />
    public int Order => 10;

    /// <inheritdoc />
    public bool Supports(BaseItem item) => item is Movie or Series or Season or Episode;

    /// <inheritdoc />
    public IEnumerable<ImageType> GetSupportedImages(BaseItem item)
    {
        yield return ImageType.Primary;
        if (item is not Episode)
        {
            yield return ImageType.Backdrop;
        }
    }

    /// <inheritdoc />
    public async Task<IEnumerable<RemoteImageInfo>> GetImages(BaseItem item, CancellationToken cancellationToken)
    {
        var list = new List<RemoteImageInfo>();
        if (!item.TryGetProviderId(Plugin.ProviderKey, out var raw) || !CsfdMatcher.TryParseId(raw, out var id))
        {
            return list;
        }

        var movie = await _client.GetMovieAsync(id, "sk", cancellationToken).ConfigureAwait(false);
        if (movie is null)
        {
            return list;
        }

        if (item is Episode)
        {
            // Epizódy na ČSFD nemávajú plagát; fotka z epizódy poslúži ako náhľad.
            if (CsfdMetadataMapper.IsRealImage(movie.Photo))
            {
                list.Add(Image(CsfdMetadataMapper.FixUrl(movie.Photo)!, ImageType.Primary));
            }

            return list;
        }

        if (CsfdMetadataMapper.IsRealImage(movie.Poster))
        {
            list.Add(Image(CsfdMetadataMapper.FixUrl(movie.Poster)!, ImageType.Primary));
        }

        if (CsfdMetadataMapper.IsRealImage(movie.Photo))
        {
            list.Add(Image(CsfdMetadataMapper.FixUrl(movie.Photo)!, ImageType.Backdrop));
        }

        return list;
    }

    /// <inheritdoc />
    public Task<HttpResponseMessage> GetImageResponse(string url, CancellationToken cancellationToken)
        => _client.GetImageResponseAsync(url, cancellationToken);

    private static RemoteImageInfo Image(string url, ImageType type) => new()
    {
        ProviderName = Plugin.ProviderName,
        Url = url,
        Type = type
        // Language zámerne null – ČSFD plagát nemá predbehnúť TMDb pri hybridnom poradí.
    };
}
