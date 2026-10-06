using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Csfd.Api;
using Jellyfin.Plugin.Csfd.Matching;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Csfd.Providers;

/// <summary>Metadáta filmov z ČSFD.</summary>
public class CsfdMovieProvider : IRemoteMetadataProvider<Movie, MovieInfo>, IHasOrder
{
    private readonly CsfdApiClient _client;
    private readonly CsfdMatcher _matcher;
    private readonly CsfdMetadataMapper _mapper;
    private readonly ILogger<CsfdMovieProvider> _logger;

    public CsfdMovieProvider(CsfdApiClient client, CsfdMatcher matcher, CsfdMetadataMapper mapper, ILogger<CsfdMovieProvider> logger)
    {
        _client = client;
        _matcher = matcher;
        _mapper = mapper;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => Plugin.ProviderName;

    /// <inheritdoc />
    public int Order => 0;

    /// <inheritdoc />
    public async Task<IEnumerable<RemoteSearchResult>> GetSearchResults(MovieInfo searchInfo, CancellationToken cancellationToken)
    {
        var id = CsfdMatcher.GetIdForIdentify(searchInfo);
        if (id.HasValue)
        {
            var movie = await _client.GetMovieAsync(id.Value, "sk", cancellationToken).ConfigureAwait(false);
            return movie is null ? Enumerable.Empty<RemoteSearchResult>() : new[] { CsfdMetadataMapper.ToSearchResult(id.Value, movie) };
        }

        var candidates = await _matcher.FindCandidatesAsync(searchInfo, false, cancellationToken).ConfigureAwait(false);
        return candidates.Take(10).Select(CsfdMetadataMapper.ToSearchResult).ToList();
    }

    /// <inheritdoc />
    public async Task<MetadataResult<Movie>> GetMetadata(MovieInfo info, CancellationToken cancellationToken)
    {
        var result = new MetadataResult<Movie>();
        var config = CsfdMetadataMapper.Config;

        var id = await _matcher.FindBestIdAsync(info, false, config.MinMatchScore, cancellationToken).ConfigureAwait(false);
        if (!id.HasValue)
        {
            return result;
        }

        var (sk, en) = await _mapper.LoadAsync(id.Value, cancellationToken).ConfigureAwait(false);
        if (sk is null)
        {
            return result;
        }

        result.Item = new Movie();
        result.QueriedById = info.ProviderIds.ContainsKey(Plugin.ProviderKey);
        CsfdMetadataMapper.Apply(result, id.Value, sk, en, isTitleLevel: true);
        _logger.LogDebug("ČSFD: {Name} → {Id} ({Rating} %)", info.Name, id.Value, sk.Rating);
        return result;
    }

    /// <inheritdoc />
    public Task<HttpResponseMessage> GetImageResponse(string url, CancellationToken cancellationToken)
        => _client.GetImageResponseAsync(url, cancellationToken);
}
