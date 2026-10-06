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

/// <summary>SK názov a popis epizódy z ČSFD (TMDb doplní zvyšok).</summary>
public class CsfdEpisodeProvider : IRemoteMetadataProvider<Episode, EpisodeInfo>, IHasOrder
{
    private readonly CsfdApiClient _client;
    private readonly CsfdMetadataMapper _mapper;
    private readonly CsfdSeriesStructure _structure;

    public CsfdEpisodeProvider(CsfdApiClient client, CsfdMetadataMapper mapper, CsfdSeriesStructure structure)
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
    public Task<IEnumerable<RemoteSearchResult>> GetSearchResults(EpisodeInfo searchInfo, CancellationToken cancellationToken)
        => Task.FromResult(Enumerable.Empty<RemoteSearchResult>());

    /// <inheritdoc />
    public async Task<MetadataResult<Episode>> GetMetadata(EpisodeInfo info, CancellationToken cancellationToken)
    {
        var result = new MetadataResult<Episode>();
        var config = CsfdMetadataMapper.Config;
        if (!config.UseEpisodes
            || info.IsMissingEpisode
            || !info.IndexNumber.HasValue
            || !info.SeriesProviderIds.TryGetValue(Plugin.ProviderKey, out var raw)
            || !CsfdMatcher.TryParseId(raw, out var seriesId))
        {
            return result;
        }

        var seasonNumber = info.ParentIndexNumber ?? 1;
        string? listTitle = null;
        int episodeId;

        if (info.ProviderIds.TryGetValue(Plugin.ProviderKey, out var own) && CsfdMatcher.TryParseId(own, out var ownId) && ownId != seriesId)
        {
            episodeId = ownId;
        }
        else
        {
            var child = await _structure.ResolveEpisodeAsync(seriesId, seasonNumber, info.IndexNumber.Value, cancellationToken).ConfigureAwait(false);
            if (child?.Id is not int eid || eid <= 0)
            {
                return result;
            }

            episodeId = eid;
            listTitle = child.Title;
        }

        var (sk, en) = await _mapper.LoadAsync(episodeId, cancellationToken, fetchEnglish: false).ConfigureAwait(false);
        if (sk is null)
        {
            return result;
        }

        result.Item = new Episode
        {
            IndexNumber = info.IndexNumber,
            ParentIndexNumber = info.ParentIndexNumber
        };
        CsfdMetadataMapper.Apply(result, episodeId, sk, en, isTitleLevel: false);

        // Nadpis detailu epizódy môže byť „Seriál - Séria 1 - Názov“ – berieme poslednú časť.
        var detailTitle = sk.Title;
        if (detailTitle is not null && detailTitle.Contains(" - ", System.StringComparison.Ordinal))
        {
            detailTitle = detailTitle[(detailTitle.LastIndexOf(" - ", System.StringComparison.Ordinal) + 3)..];
        }

        var title = CsfdText.PickEpisodeTitle(listTitle, sk, detailTitle, config.FallbackCzech);
        if (config.UseSlovakTitle && !string.IsNullOrWhiteSpace(title))
        {
            result.Item.Name = title;
        }

        return result;
    }

    /// <inheritdoc />
    public Task<HttpResponseMessage> GetImageResponse(string url, CancellationToken cancellationToken)
        => _client.GetImageResponseAsync(url, cancellationToken);
}
