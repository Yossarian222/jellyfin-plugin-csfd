using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Csfd.Api;
using Jellyfin.Plugin.Csfd.Matching;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Providers;

namespace Jellyfin.Plugin.Csfd.Providers;

/// <summary>Metadáta seriálov z ČSFD.</summary>
public class CsfdSeriesProvider : IRemoteMetadataProvider<Series, SeriesInfo>, IHasOrder
{
    private readonly CsfdApiClient _client;
    private readonly CsfdMatcher _matcher;
    private readonly CsfdMetadataMapper _mapper;

    public CsfdSeriesProvider(CsfdApiClient client, CsfdMatcher matcher, CsfdMetadataMapper mapper)
    {
        _client = client;
        _matcher = matcher;
        _mapper = mapper;
    }

    /// <inheritdoc />
    public string Name => Plugin.ProviderName;

    /// <inheritdoc />
    public int Order => 0;

    /// <inheritdoc />
    public async Task<IEnumerable<RemoteSearchResult>> GetSearchResults(SeriesInfo searchInfo, CancellationToken cancellationToken)
    {
        // ČSFD URL, "csfd:ID" alebo už uložené ID → priamo ten záznam.
        var id = CsfdMatcher.GetExplicitId(searchInfo);
        if (id.HasValue)
        {
            var movie = await _client.GetMovieAsync(id.Value, "sk", cancellationToken).ConfigureAwait(false);
            return movie is null ? Enumerable.Empty<RemoteSearchResult>() : new[] { CsfdMetadataMapper.ToSearchResult(id.Value, movie) };
        }

        var results = new List<RemoteSearchResult>();
        var candidates = await _matcher.FindCandidatesAsync(searchInfo, true, cancellationToken).ConfigureAwait(false);
        results.AddRange(candidates.Take(10).Select(CsfdMetadataMapper.ToSearchResult));

        // Holé číslo môže byť aj ČSFD ID – ponúkneme ho na konci zoznamu.
        var bare = CsfdMatcher.GetBareNumberFromName(searchInfo);
        if (bare.HasValue && candidates.All(c => c.Item.Id != bare.Value))
        {
            var byId = await _client.GetMovieAsync(bare.Value, "sk", cancellationToken).ConfigureAwait(false);
            if (byId is not null)
            {
                results.Add(CsfdMetadataMapper.ToSearchResult(bare.Value, byId));
            }
        }

        return results;
    }

    /// <inheritdoc />
    public async Task<MetadataResult<Series>> GetMetadata(SeriesInfo info, CancellationToken cancellationToken)
    {
        var result = new MetadataResult<Series>();
        var config = CsfdMetadataMapper.Config;

        var id = await _matcher.FindBestIdAsync(info, true, Math.Clamp(config.MinMatchScore, CsfdMatcher.MinScoreFloor, 100), cancellationToken).ConfigureAwait(false);
        if (!id.HasValue)
        {
            return result;
        }

        var (sk, en) = await _mapper.LoadAsync(id.Value, cancellationToken).ConfigureAwait(false);
        if (sk is null)
        {
            return result;
        }

        // Ak niekto ručne zadal ID sezóny/epizódy, prejdeme na nadradený seriál.
        // (Rodiča csfd-api spoľahlivo vráti len z CZ stránky.)
        var seriesId = id.Value;
        var cs = await _client.GetMovieAsync(seriesId, "cs", cancellationToken).ConfigureAwait(false);
        if (cs?.Parent?.Series?.Id is int parentId && parentId > 0 && parentId != seriesId)
        {
            seriesId = parentId;
            (sk, en) = await _mapper.LoadAsync(seriesId, cancellationToken).ConfigureAwait(false);
            if (sk is null)
            {
                return result;
            }
        }

        result.Item = new Series();
        result.QueriedById = info.ProviderIds.ContainsKey(Plugin.ProviderKey);
        CsfdMetadataMapper.Apply(result, seriesId, sk, en, isTitleLevel: true);
        return result;
    }

    /// <inheritdoc />
    public Task<HttpResponseMessage> GetImageResponse(string url, CancellationToken cancellationToken)
        => _client.GetImageResponseAsync(url, cancellationToken);
}
