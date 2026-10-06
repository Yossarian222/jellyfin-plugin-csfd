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

/// <summary>Popis, hodnotenie a premiéra sezóny z ČSFD.</summary>
public class CsfdSeasonProvider : IRemoteMetadataProvider<Season, SeasonInfo>, IHasOrder
{
    private readonly CsfdApiClient _client;
    private readonly CsfdMetadataMapper _mapper;
    private readonly CsfdSeriesStructure _structure;

    public CsfdSeasonProvider(CsfdApiClient client, CsfdMetadataMapper mapper, CsfdSeriesStructure structure)
    {
        _client = client;
        _mapper = mapper;
        _structure = structure;
    }

    /// <inheritdoc />
    public string Name => Plugin.ProviderName;

    /// <inheritdoc />
    public int Order => 0;

    /// <inheritdoc />
    public Task<IEnumerable<RemoteSearchResult>> GetSearchResults(SeasonInfo searchInfo, CancellationToken cancellationToken)
        => Task.FromResult(Enumerable.Empty<RemoteSearchResult>());

    /// <inheritdoc />
    public async Task<MetadataResult<Season>> GetMetadata(SeasonInfo info, CancellationToken cancellationToken)
    {
        var result = new MetadataResult<Season>();
        var config = CsfdMetadataMapper.Config;
        if (!config.UseEpisodes
            || !info.IndexNumber.HasValue
            || !info.SeriesProviderIds.TryGetValue(Plugin.ProviderKey, out var raw)
            || !CsfdMatcher.TryParseId(raw, out var seriesId))
        {
            return result;
        }

        int seasonId;
        if (info.ProviderIds.TryGetValue(Plugin.ProviderKey, out var own) && CsfdMatcher.TryParseId(own, out var ownId) && ownId != seriesId)
        {
            seasonId = ownId;
        }
        else
        {
            var (season, _) = await _structure.ResolveSeasonAsync(seriesId, info.IndexNumber.Value, cancellationToken).ConfigureAwait(false);
            if (season?.Id is not int sid || sid <= 0)
            {
                return result;
            }

            seasonId = sid;
        }

        var (sk, en) = await _mapper.LoadAsync(seasonId, cancellationToken, fetchEnglish: false).ConfigureAwait(false);
        if (sk is null)
        {
            return result;
        }

        result.Item = new Season { IndexNumber = info.IndexNumber };
        CsfdMetadataMapper.Apply(result, seasonId, sk, en, isTitleLevel: false);
        return result;
    }

    /// <inheritdoc />
    public Task<HttpResponseMessage> GetImageResponse(string url, CancellationToken cancellationToken)
        => _client.GetImageResponseAsync(url, cancellationToken);
}
