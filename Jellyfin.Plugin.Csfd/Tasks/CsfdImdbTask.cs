using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Csfd.Api;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Csfd.Tasks;

/// <summary>Denne obnoví IMDb hodnotenia filmov, seriálov a epizód v knižnici (pre Wholphinix, vedľa ČSFD).</summary>
public sealed class CsfdImdbTask : IScheduledTask
{
    private readonly ILibraryManager _libraryManager;
    private readonly CsfdImdbClient _imdb;
    private readonly ILogger<CsfdImdbTask> _logger;

    public CsfdImdbTask(ILibraryManager libraryManager, CsfdImdbClient imdb, ILogger<CsfdImdbTask> logger)
    {
        _libraryManager = libraryManager;
        _imdb = imdb;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "ČSFD: IMDb hodnotenia";

    /// <inheritdoc />
    public string Key => "CsfdImdb";

    /// <inheritdoc />
    public string Description => "Stiahne oficiálny IMDb dataset hodnotení a uloží hodnotenia titulov z knižnice (zobrazuje ich Wholphinix vedľa ČSFD).";

    /// <inheritdoc />
    public string Category => "ČSFD";

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        yield return new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.DailyTrigger,
            TimeOfDayTicks = TimeSpan.FromHours(4).Ticks
        };
    }

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var items = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.Movie, BaseItemKind.Series, BaseItemKind.Episode },
            IsVirtualItem = false,
            Recursive = true
        });
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            if (item.TryGetProviderId(MetadataProvider.Imdb, out var id) && id.StartsWith("tt", StringComparison.Ordinal))
            {
                ids.Add(id);
            }
        }

        progress.Report(10);
        var count = await _imdb.RefreshAsync(ids, cancellationToken).ConfigureAwait(false);
        progress.Report(100);
        _logger.LogInformation("IMDb hodnotenia: {Count} z {Total} titulov s IMDb ID", count, ids.Count);
    }
}
