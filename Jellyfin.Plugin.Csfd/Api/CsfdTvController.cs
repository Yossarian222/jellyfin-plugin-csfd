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

    public CsfdTvController(CsfdTvTipsClient tips, ILibraryManager libraryManager, IUserManager userManager)
    {
        _tips = tips;
        _libraryManager = libraryManager;
        _userManager = userManager;
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
            var item = _libraryManager.GetItemList(new InternalItemsQuery(user)
            {
                IncludeItemTypes = new[] { BaseItemKind.Movie, BaseItemKind.Series },
                HasAnyProviderId = new Dictionary<string, string> { [Plugin.ProviderKey] = tip.CsfdId.ToString(CultureInfo.InvariantCulture) },
                Recursive = true,
                Limit = 1
            }).FirstOrDefault();
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

        return Ok(result
            .OrderByDescending(t => t.RatingPercent ?? -1)
            .Take(Math.Clamp(limit, 1, 30))
            .ToList());
    }
}
