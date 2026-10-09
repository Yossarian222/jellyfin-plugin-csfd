using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Csfd.Configuration;
using Jellyfin.Plugin.Csfd.Matching;
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

    /// <summary>Položka v knižnici; null pri tipe, ktorý používateľ nemá (pozri <see cref="InLibrary"/>).</summary>
    public Guid? ItemId { get; set; }

    public bool InLibrary { get; set; }

    /// <summary>Hodnotenie ČSFD v percentách.</summary>
    public int? RatingPercent { get; set; }

    /// <summary>Pri chýbajúcom tipe: „movie“ alebo „tv“ (typ pre Seerr).</summary>
    public string? MediaType { get; set; }

    /// <summary>Pri chýbajúcom tipe: SK, ČSFD a ďalšie názvy na vyhľadanie v Seerr/TMDb.</summary>
    public List<string>? Titles { get; set; }

    /// <summary>Pri chýbajúcom tipe: plagát z ČSFD.</summary>
    public string? Poster { get; set; }

    /// <summary>Pri chýbajúcom tipe: fotka z ČSFD (záloha pozadia).</summary>
    public string? Photo { get; set; }

    /// <summary>Pri chýbajúcom tipe: popis z ČSFD (SK, inak CZ).</summary>
    public string? Overview { get; set; }

    public List<string>? Genres { get; set; }

    public int? DurationMinutes { get; set; }
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
    private readonly CsfdApiClient _client;
    private readonly CsfdRankingsClient _rankings;
    private readonly CsfdAccountClient _account;
    private readonly CsfdTriviaClient _trivia;

    public CsfdTvController(
        CsfdTvTipsClient tips,
        ILibraryManager libraryManager,
        IUserManager userManager,
        ILogger<CsfdTvController> logger,
        CsfdApiClient client,
        CsfdRankingsClient rankings,
        CsfdAccountClient account,
        CsfdTriviaClient trivia)
    {
        _account = account;
        _trivia = trivia;
        _tips = tips;
        _client = client;
        _rankings = rankings;
        _libraryManager = libraryManager;
        _userManager = userManager;
        _logger = logger;
    }

    /// <summary>Moje hodnotenia z ČSFD profilu (nastavený v plugine): ČSFD ID → hviezdy (0 = odpad, 1–5).</summary>
    [HttpGet("MyRatings")]
    public async Task<ActionResult<IReadOnlyDictionary<int, int>>> MyRatings(CancellationToken cancellationToken = default)
        => Ok(await _account.GetMyRatingsAsync(cancellationToken).ConfigureAwait(false));

    /// <summary>Ohodnotí film na ČSFD účtom z nastavení pluginu.</summary>
    [HttpPost("MyRatings/{csfdId:int}")]
    public async Task<ActionResult<object>> Rate([FromRoute] int csfdId, [FromQuery] int stars, CancellationToken cancellationToken = default)
    {
        var (ok, message) = await _account.RateAsync(csfdId, stars, cancellationToken).ConfigureAwait(false);
        return Ok(new { ok, message });
    }

    /// <summary>Zaujímavosti k filmu/seriálu z ČSFD (najviac <paramref name="limit"/>, bez spoilerov).</summary>
    [HttpGet("Trivia/{csfdId:int}")]
    public async Task<ActionResult<IReadOnlyList<string>>> Trivia([FromRoute] int csfdId, [FromQuery] int limit = 4, CancellationToken cancellationToken = default)
    {
        var items = await _trivia.GetTriviaAsync(csfdId, cancellationToken).ConfigureAwait(false);
        return Ok(items.Take(Math.Clamp(limit, 1, 50)).ToList());
    }

    /// <summary>Poradie v ČSFD rebríčkoch najlepších filmov a seriálov: ČSFD ID → pozícia.</summary>
    [HttpGet("Ranks")]
    public async Task<ActionResult<IReadOnlyDictionary<int, int>>> Ranks(CancellationToken cancellationToken = default)
        => Ok(await _rankings.GetRanksAsync(cancellationToken).ConfigureAwait(false));

    /// <summary>Najlepšie hodnotené TV tipy dňa, ktoré má používateľ v knižnici.</summary>
    [HttpGet("TvTips")]
    public async Task<ActionResult<IReadOnlyList<CsfdTvTipDto>>> TvTips(
        [FromQuery] int day = 0,
        [FromQuery] int limit = 10,
        [FromQuery] int missing = 0,
        CancellationToken cancellationToken = default)
    {
        var claim = User.FindFirst(UserIdClaim)?.Value;
        if (!Guid.TryParse(claim, out var userId) || _userManager.GetUserById(userId) is not { } user)
        {
            return Unauthorized();
        }

        var tips = await _tips.GetTipsAsync(day, cancellationToken).ConfigureAwait(false);
        var result = new List<CsfdTvTipDto>();
        var notInLibrary = new List<CsfdTvTip>();
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
                // Tipy sú zoradené podľa hodnotenia → prvé chýbajúce sú tie najlepšie.
                if (notInLibrary.Count < Math.Clamp(missing, 0, 10))
                {
                    notInLibrary.Add(tip);
                }

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
                InLibrary = true,
                RatingPercent = item.CommunityRating is { } r ? (int)Math.Round(r * 10) : null
            });
        }

        _logger.LogInformation(
            "ČSFD TV: {Tips} tipov z ČSFD, v knižnici {Matched}: {Titles}",
            tips.Count,
            result.Count,
            string.Join(", ", result.Select(r => r.Title)));

        var missingDtos = new List<CsfdTvTipDto>();
        foreach (var tip in notInLibrary)
        {
            missingDtos.Add(await ToMissingDtoAsync(tip, cancellationToken).ConfigureAwait(false));
        }

        return Ok(result
            .OrderByDescending(t => t.RatingPercent ?? -1)
            .Take(Math.Clamp(limit, 1, 30))
            .Concat(missingDtos)
            .ToList());
    }

    /// <summary>Doplní detail zo sidecaru (názvy, hodnotenie, plagát); bez sidecaru ostane len SK názov.</summary>
    private async Task<CsfdTvTipDto> ToMissingDtoAsync(CsfdTvTip tip, CancellationToken cancellationToken)
    {
        CsfdMovie? detail = null;
        try
        {
            detail = await _client.GetMovieAsync(tip.CsfdId, "sk", cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "ČSFD TV: detail {CsfdId} sa nepodarilo načítať", tip.CsfdId);
        }

        var titles = new List<string?> { tip.Title, detail?.Title };
        titles.AddRange(detail?.TitlesOther?.Select(t => t.Title) ?? Enumerable.Empty<string?>());
        static string? FixUrl(string? url) => url is not null && url.StartsWith("//", StringComparison.Ordinal) ? "https:" + url : url;

        return new CsfdTvTipDto
        {
            CsfdId = tip.CsfdId,
            Title = tip.Title,
            Year = tip.Year ?? detail?.Year,
            Time = tip.Time,
            Channel = tip.Channel,
            InLibrary = false,
            RatingPercent = detail?.Rating,
            MediaType = detail?.Type?.Contains("seri", StringComparison.OrdinalIgnoreCase) == true ? "tv" : "movie",
            Titles = titles
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .Select(t => t!.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList(),
            Poster = FixUrl(detail?.Poster),
            Photo = FixUrl(detail?.Photo),
            Overview = detail is null ? null : CsfdText.PickOverview(new[] { detail }, new PluginConfiguration { FallbackCzech = true }),
            Genres = detail?.Genres,
            DurationMinutes = detail?.Duration
        };
    }
}
