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
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Csfd.Tasks;

/// <summary>
/// Nočné prednačítanie: pomaly obnoví zaujímavosti titulov v knižnici (chýbajúce alebo s končiacou platnosťou),
/// TV tipy na dnes a zajtra a „Chcem vidieť“ – cez deň sa potom servírujú z cache.
/// Jellyfin úlohu nájde sám (typ implementuje <see cref="IScheduledTask"/>).
/// </summary>
public sealed class CsfdPrefetchTask : IScheduledTask
{
    /// <summary>Zaujímavosti, ktorým platnosť skončí do tejto doby, sa obnovia už teraz.</summary>
    internal static readonly TimeSpan TriviaRefreshMargin = TimeSpan.FromDays(3);

    /// <summary>Najkratší dovolený rozostup medzi titulmi – šetrí ČSFD aj pri zlom nastavení.</summary>
    internal const int MinDelayMs = 1000;

    /// <summary>Podiel priebehu pre TV tipy a „Chcem vidieť“; zvyšok sú zaujímavosti.</summary>
    private const double ListsShare = 5;

    private readonly ILibraryManager _libraryManager;
    private readonly CsfdTriviaClient _trivia;
    private readonly CsfdTvTipsClient _tips;
    private readonly CsfdWatchlistClient _watchlist;
    private readonly ILogger<CsfdPrefetchTask> _logger;

    public CsfdPrefetchTask(
        ILibraryManager libraryManager,
        CsfdTriviaClient trivia,
        CsfdTvTipsClient tips,
        CsfdWatchlistClient watchlist,
        ILogger<CsfdPrefetchTask> logger)
    {
        _libraryManager = libraryManager;
        _trivia = trivia;
        _tips = tips;
        _watchlist = watchlist;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "ČSFD: nočné prednačítanie";

    /// <inheritdoc />
    public string Key => "CsfdPrefetch";

    /// <inheritdoc />
    public string Description => "Pomaly prednačíta zaujímavosti titulov v knižnici, TV tipy na dnes a zajtra a zoznam „Chcem vidieť“ z ČSFD.";

    /// <inheritdoc />
    public string Category => "ČSFD";

    private static PluginConfiguration Config => Plugin.Instance?.Configuration ?? new PluginConfiguration();

    /// <summary>Rozostup z nastavení, najmenej <see cref="MinDelayMs"/>.</summary>
    internal static int EffectiveDelayMs(int configured) => Math.Max(MinDelayMs, configured);

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        yield return new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.DailyTrigger,
            TimeOfDayTicks = TimeSpan.FromHours(3).Ticks
        };
    }

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        if (!Config.PrefetchEnabled)
        {
            _logger.LogInformation("ČSFD prednačítanie: vypnuté v nastaveniach pluginu");
            progress.Report(100);
            return;
        }

        var delay = TimeSpan.FromMilliseconds(EffectiveDelayMs(Config.PrefetchDelayMs));
        var started = DateTime.UtcNow;

        // 1) TV tipy: dnes a zajtra (klient vie ľubovoľný deň; cache je len v pamäti, 3 h).
        var tipCounts = new List<int>();
        foreach (var day in new[] { 0, 1 })
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                tipCounts.Add((await _tips.GetTipsAsync(day, cancellationToken).ConfigureAwait(false)).Count);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "ČSFD prednačítanie: TV tipy (deň {Day}) zlyhali", day);
                tipCounts.Add(0);
            }

            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }

        progress.Report(ListsShare / 2);

        // 2) „Chcem vidieť“: jeden zoznam z profilu v nastaveniach (nie je per používateľ).
        var watchlistCount = 0;
        try
        {
            watchlistCount = (await _watchlist.GetWatchlistAsync(cancellationToken).ConfigureAwait(false)).Count;
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "ČSFD prednačítanie: „Chcem vidieť“ zlyhalo");
        }

        progress.Report(ListsShare);

        // 3) Zaujímavosti titulov s ČSFD ID, ktorým cache chýba alebo čoskoro vyprší.
        var ids = CsfdIdsInLibrary();
        var due = ids.Where(id => CsfdTriviaClient.NeedsRefresh(id, TriviaRefreshMargin)).ToList();
        _logger.LogInformation(
            "ČSFD prednačítanie: {Total} titulov s ČSFD ID, zaujímavosti treba obnoviť pri {Due} (rozostup {Delay} ms)",
            ids.Count,
            due.Count,
            (int)delay.TotalMilliseconds);

        var refreshed = 0;
        var withTrivia = 0;
        for (var i = 0; i < due.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var items = await _trivia.GetTriviaAsync(due[i], TriviaRefreshMargin, cancellationToken).ConfigureAwait(false);
            refreshed++;
            if (items.Count > 0)
            {
                withTrivia++;
            }

            progress.Report(ListsShare + ((100 - ListsShare) * (i + 1) / due.Count));
            if (i < due.Count - 1)
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
        }

        progress.Report(100);
        _logger.LogInformation(
            "ČSFD prednačítanie hotové za {Elapsed:hh\\:mm\\:ss}: TV tipy dnes/zajtra {Tips}, „Chcem vidieť“ {Watchlist}, zaujímavosti {Refreshed}/{Due} (s textom {WithTrivia}), aktuálnych {Fresh}",
            DateTime.UtcNow - started,
            string.Join("/", tipCounts),
            watchlistCount,
            refreshed,
            due.Count,
            withTrivia,
            ids.Count - due.Count);
    }

    /// <summary>Rôzne ČSFD ID filmov a seriálov v knižnici.</summary>
    private List<int> CsfdIdsInLibrary()
    {
        var items = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.Movie, BaseItemKind.Series },
            IsVirtualItem = false,
            Recursive = true
        });

        var ids = new List<int>();
        var seen = new HashSet<int>();
        foreach (var item in items)
        {
            if (item.TryGetProviderId(Plugin.ProviderKey, out var raw)
                && CsfdMatcher.TryParseId(raw, out var id)
                && id > 0
                && seen.Add(id))
            {
                ids.Add(id);
            }
        }

        return ids;
    }
}
