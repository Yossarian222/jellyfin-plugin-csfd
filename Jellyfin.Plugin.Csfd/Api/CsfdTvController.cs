using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Csfd.Api;

/// <summary>TV tip pre klienta, ktorý je zároveň položkou v knižnici (dá sa rovno spustiť).</summary>
public sealed class CsfdTvTipDto
{
    public int CsfdId { get; set; }

    public string Title { get; set; } = string.Empty;

    public int? Year { get; set; }

    public string? Time { get; set; }

    public string? Channel { get; set; }

    public Guid ItemId { get; set; }

    /// <summary>Hodnotenie ČSFD v percentách (CommunityRating × 10), ak ho položka má.</summary>
    public int? RatingPercent { get; set; }
}

/// <summary>Endpoint pre klientov (Wholphinix): „TV tipy dňa“ z ČSFD zúžené na to, čo je v knižnici používateľa.</summary>
[ApiController]
[Authorize]
[Route("Csfd")]
public class CsfdTvController : ControllerBase
{
    private const string UserIdClaim = "Jellyfin-UserId";

    private readonly CsfdTvTipsClient _tips;
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly ILogger<CsfdTvController> _logger;

    public CsfdTvController(CsfdTvTipsClient tips, ILibraryManager libraryManager, IUserManager userManager, ILogger<CsfdTvController> logger)
    {
        _tips = tips;
        _libraryManager = libraryManager;
        _userManager = userManager;
        _logger = logger;
    }

    /// <summary>Najlepšie hodnotené TV tipy dňa, ktoré má používateľ v knižnici.</summary>
    [HttpGet("TvTips")]
    public async Task<ActionResult<IReadOnlyList<CsfdTvTipDto>>> TvTips(
        [FromQuery] int day = 0,
        [FromQuery] int limit = 10,
        CancellationToken cancellationToken = default)
    {
        var claim = User.FindFirst(UserIdClaim)?.Value;
        if (!Guid.TryParse(claim, out var userId) || _userManager.GetUserById(userId) is not { } user)
        {
            return Unauthorized();
        }

        var tips = await _tips.GetTipsAsync(day, cancellationToken).ConfigureAwait(false);
        var result = new List<CsfdTvTipDto>();
        foreach (var tip in tips)
        {
            BaseItem? item;
            try
            {
                item = _libraryManager.GetItemList(new InternalItemsQuery(user)
                {
                    IncludeItemTypes = new[] { BaseItemKind.Movie, BaseItemKind.Series },
                    HasAnyProviderId = new Dictionary<string, string> { [Plugin.ProviderKey] = tip.CsfdId.ToString(CultureInfo.InvariantCulture) },
                    Recursive = true,
                    Limit = 1
                }).FirstOrDefault();

                // Položky, ktoré plugin ešte neidentifikoval, nemajú ČSFD ID → skúsime SK názov + rok.
                item ??= _libraryManager.GetItemList(new InternalItemsQuery(user)
                {
                    IncludeItemTypes = new[] { BaseItemKind.Movie, BaseItemKind.Series },
                    Name = tip.Title,
                    Years = tip.Year is { } year ? new[] { year } : Array.Empty<int>(),
                    Recursive = true,
                    Limit = 1
                }).FirstOrDefault();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "ČSFD TV: hľadanie {CsfdId} v knižnici zlyhalo", tip.CsfdId);
                continue;
            }

            if (item is null)
            {
                continue;
            }

            result.Add(new CsfdTvTipDto
            {
                CsfdId = tip.CsfdId,
                Title = item.Name,
                Year = tip.Year,
                Time = tip.Time,
                Channel = tip.Channel,
                ItemId = item.Id,
                RatingPercent = item.CommunityRating is { } r ? (int)Math.Round(r * 10) : null
            });
        }

        _logger.LogInformation(
            "ČSFD TV: {Tips} tipov z ČSFD, v knižnici {Matched}: {Titles}",
            tips.Count,
            result.Count,
            string.Join(", ", result.Select(r => r.Title)));

        return Ok(result
            .OrderByDescending(t => t.RatingPercent ?? -1)
            .Take(Math.Clamp(limit, 1, 30))
            .ToList());
    }
}
