using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Csfd.Api;
using Jellyfin.Plugin.Csfd.Matching;

namespace Jellyfin.Plugin.Csfd.Providers;

/// <summary>
/// Štruktúra seriálu na ČSFD: seriál → sezóny → epizódy (alebo seriál → epizódy bez sezón).
/// </summary>
public sealed class CsfdSeriesStructure
{
    private readonly CsfdApiClient _client;

    public CsfdSeriesStructure(CsfdApiClient client)
    {
        _client = client;
    }

    /// <summary>
    /// Zoznam sezón/epizód. csfd-api rozpoznáva štruktúru (typ, nadpisy „Série“/„Epizody“, ID v odkazoch)
    /// len na CZ stránke, preto štruktúru čítame vždy z CZ. SK stránka slúži len na texty.
    /// </summary>
    public async Task<(List<CsfdSeriesChild>? Seasons, List<CsfdSeriesChild>? Episodes)> GetChildrenAsync(int id, CancellationToken cancellationToken)
    {
        var cs = await _client.GetMovieAsync(id, "cs", cancellationToken).ConfigureAwait(false);
        return (Clean(cs?.Seasons), Clean(cs?.Episodes));
    }

    /// <summary>
    /// Nájde ČSFD sezónu k číslu sezóny v Jellyfine. Vracia (null, true), ak seriál nemá sezóny
    /// a epizódy visia priamo pod ním (vtedy je to sezóna 1).
    /// </summary>
    public async Task<(CsfdSeriesChild? Season, bool FlatSeries)> ResolveSeasonAsync(int seriesId, int seasonNumber, CancellationToken cancellationToken)
    {
        if (seasonNumber <= 0)
        {
            return (null, false);
        }

        var (seasons, episodes) = await GetChildrenAsync(seriesId, cancellationToken).ConfigureAwait(false);
        if (seasons is null)
        {
            return (null, episodes is not null && seasonNumber == 1);
        }

        var byNumber = seasons.FirstOrDefault(s => CsfdText.FirstNumber(s.Title) == seasonNumber);
        if (byNumber is not null)
        {
            return (byNumber, false);
        }

        // Sezóny bez čísla v názve (napr. „Kniha prvá“) – podľa poradia.
        var anyNumbered = seasons.Any(s => CsfdText.FirstNumber(s.Title).HasValue);
        if (!anyNumbered && seasonNumber <= seasons.Count)
        {
            return (seasons[seasonNumber - 1], false);
        }

        return (null, false);
    }

    /// <summary>Položka epizódy zo zoznamu ČSFD.</summary>
    public async Task<CsfdSeriesChild?> ResolveEpisodeAsync(int seriesId, int seasonNumber, int episodeNumber, CancellationToken cancellationToken)
    {
        if (episodeNumber <= 0)
        {
            return null;
        }

        var (season, flat) = await ResolveSeasonAsync(seriesId, seasonNumber, cancellationToken).ConfigureAwait(false);

        List<CsfdSeriesChild>? list;
        if (season is not null)
        {
            (_, list) = await GetChildrenAsync(season.Id!.Value, cancellationToken).ConfigureAwait(false);
        }
        else if (flat)
        {
            (_, list) = await GetChildrenAsync(seriesId, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            return null;
        }

        if (list is null)
        {
            return null;
        }

        var coded = list
            .Select(e => (Child: e, Code: CsfdText.ParseEpisodeCode(e.Info)))
            .Where(x => x.Code.Episode.HasValue)
            .ToList();

        if (coded.Count > 0)
        {
            var hit = coded.FirstOrDefault(x => x.Code.Episode == episodeNumber && (x.Code.Season is null || x.Code.Season == seasonNumber));
            return hit.Child;
        }

        return episodeNumber <= list.Count ? list[episodeNumber - 1] : null;
    }

    private static List<CsfdSeriesChild>? Clean(List<CsfdSeriesChild>? list)
    {
        var valid = list?.Where(c => c.Id is > 0).ToList();
        return valid is { Count: > 0 } ? valid : null;
    }
}
