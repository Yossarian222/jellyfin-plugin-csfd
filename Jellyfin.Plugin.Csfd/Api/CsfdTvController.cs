using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.Csfd.Configuration;
using Jellyfin.Plugin.Csfd.Matching;
using Jellyfin.Plugin.Csfd.Providers;
using Jellyfin.Plugin.Csfd.Seasonal;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
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

    /// <summary>Pri chýbajúcom tipe: plagát z ČSFD (zo sidecaru, inak <see cref="Thumbnail"/>).</summary>
    public string? Poster { get; set; }

    /// <summary>Malý plagát priamo zo stránky TV tipov (absolútna https URL); aj keď sidecar plagát nemá.</summary>
    public string? Thumbnail { get; set; }

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
    private const string IsApiKeyClaim = "Jellyfin-IsApiKey";
    private const string AdministratorRole = "Administrator";

    /// <summary>Najkratší rozostup medzi hodnoteniami (všetci používatelia spolu – hodnotí sa jedným ČSFD účtom).</summary>
    private static readonly TimeSpan RateInterval = TimeSpan.FromSeconds(1);
    private static readonly object RateLock = new();
    private static DateTime _lastRateAt = DateTime.MinValue;

    private static readonly BaseItemKind[] MovieAndSeries = { BaseItemKind.Movie, BaseItemKind.Series };

    private readonly CsfdTvTipsClient _tips;
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly ILogger<CsfdTvController> _logger;
    private readonly CsfdApiClient _client;
    private readonly CsfdRankingsClient _rankings;
    private readonly CsfdAccountClient _account;
    private readonly CsfdTriviaClient _trivia;
    private readonly CsfdWatchlistClient _watchlist;
    private readonly CsfdSeasonalClient _seasonal;

    public CsfdTvController(
        CsfdTvTipsClient tips,
        ILibraryManager libraryManager,
        IUserManager userManager,
        ILogger<CsfdTvController> logger,
        CsfdApiClient client,
        CsfdRankingsClient rankings,
        CsfdAccountClient account,
        CsfdTriviaClient trivia,
        CsfdWatchlistClient watchlist,
        CsfdSeasonalClient seasonal)
    {
        _seasonal = seasonal;
        _watchlist = watchlist;
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
    public async Task<ActionResult<object>> Rate([FromRoute] int csfdId, [FromQuery] int? stars, CancellationToken cancellationToken = default)
    {
        if (CurrentUser() is not { } user)
        {
            return Unauthorized();
        }

        if (Plugin.Instance?.Configuration.AllowRatingForAllUsers == false && !user.HasPermission(PermissionKind.IsAdministrator))
        {
            return Forbid();
        }

        if (csfdId <= 0 || stars is not (>= 0 and <= 5))
        {
            return BadRequest(new { ok = false, message = "Neplatné ČSFD ID alebo počet hviezd (0–5)." });
        }

        lock (RateLock)
        {
            var now = DateTime.UtcNow;
            if (now - _lastRateAt < RateInterval)
            {
                return StatusCode(StatusCodes.Status429TooManyRequests, new { ok = false, message = "Príliš rýchlo za sebou – skús o chvíľu." });
            }

            _lastRateAt = now;
        }

        var (ok, message) = await _account.RateAsync(csfdId, stars.Value, cancellationToken).ConfigureAwait(false);
        return Ok(new { ok, message });
    }

    /// <summary>Zaujímavosti k filmu/seriálu z ČSFD (najviac <paramref name="limit"/>, bez spoilerov).</summary>
    [HttpGet("Trivia/{csfdId:int}")]
    public async Task<ActionResult<IReadOnlyList<string>>> Trivia([FromRoute] int csfdId, [FromQuery] int limit = 4, CancellationToken cancellationToken = default)
    {
        if (csfdId <= 0)
        {
            return BadRequest();
        }

        var items = await _trivia.GetTriviaAsync(csfdId, cancellationToken).ConfigureAwait(false);
        return Ok(items.Take(Math.Clamp(limit, 1, 50)).ToList());
    }

    /// <summary>Poradie v ČSFD rebríčkoch najlepších filmov a seriálov: ČSFD ID → pozícia.</summary>
    [HttpGet("Ranks")]
    public async Task<ActionResult<IReadOnlyDictionary<int, int>>> Ranks(CancellationToken cancellationToken = default)
        => Ok(await _rankings.GetRanksAsync(cancellationToken).ConfigureAwait(false));

    /// <summary>Najlepšie hodnotené TV tipy dňa, ktoré má používateľ v knižnici.</summary>
    /// <param name="userId">Voliteľne iný používateľ – len pre admina alebo API kľúč (ten používateľa nemá), napr. MCP server pre Clauda.</param>
    [HttpGet("TvTips")]
    public async Task<ActionResult<IReadOnlyList<CsfdTvTipDto>>> TvTips(
        [FromQuery] int day = 0,
        [FromQuery] int limit = 10,
        [FromQuery] int missing = 0,
        [FromQuery] Guid? userId = null,
        CancellationToken cancellationToken = default)
    {
        var (resolvedId, forbidden) = ResolveUserId(User, userId);
        if (forbidden)
        {
            return Forbid();
        }

        if (resolvedId is not { } id || _userManager.GetUserById(id) is not { } user)
        {
            return Unauthorized();
        }

        var tips = await _tips.GetTipsAsync(Math.Clamp(day, -1, 7), cancellationToken).ConfigureAwait(false);
        var result = new List<CsfdTvTipDto>();
        var notInLibrary = new List<CsfdTvTip>();
        foreach (var tip in tips)
        {
            BaseItem? item;
            try
            {
                item = FindInLibrary(user, tip.CsfdId, tip.Title, tip.Year);
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
                Thumbnail = tip.Thumbnail,
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

    /// <summary>
    /// „Chcem vidieť“ z ČSFD profilu (nastavený v plugine): najprv tituly v knižnici používateľa (v poradí z ČSFD),
    /// potom najviac <paramref name="missing"/> chýbajúcich s detailmi pre Seerr.
    /// </summary>
    [HttpGet("Watchlist")]
    public async Task<ActionResult<IReadOnlyList<CsfdTvTipDto>>> Watchlist(
        [FromQuery] int limit = 20,
        [FromQuery] int missing = 10,
        CancellationToken cancellationToken = default)
    {
        if (CurrentUser() is not { } user)
        {
            return Unauthorized();
        }

        var entries = await _watchlist.GetWatchlistAsync(cancellationToken).ConfigureAwait(false);
        var maxInLibrary = Math.Clamp(limit, 1, 50);
        var maxMissing = Math.Clamp(missing, 0, 20);
        var result = new List<CsfdTvTipDto>();
        var notInLibrary = new List<CsfdTvTip>();
        var seen = new HashSet<Guid>();
        foreach (var entry in entries)
        {
            if (result.Count >= maxInLibrary && notInLibrary.Count >= maxMissing)
            {
                break;
            }

            BaseItem? item;
            try
            {
                item = FindInLibrary(user, entry.CsfdId, entry.Title, entry.Year);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "ČSFD Chcem vidieť: hľadanie {CsfdId} v knižnici zlyhalo", entry.CsfdId);
                continue;
            }

            if (item is null)
            {
                if (notInLibrary.Count < maxMissing)
                {
                    notInLibrary.Add(new CsfdTvTip(entry.CsfdId, entry.Title, entry.Year, null, null));
                }

                continue;
            }

            if (result.Count >= maxInLibrary || !seen.Add(item.Id))
            {
                continue;
            }

            result.Add(new CsfdTvTipDto
            {
                CsfdId = entry.CsfdId,
                Title = item.Name,
                Year = entry.Year ?? item.ProductionYear,
                ItemId = item.Id,
                InLibrary = true,
                RatingPercent = item.CommunityRating is { } r ? (int)Math.Round(r * 10) : null
            });
        }

        _logger.LogInformation(
            "ČSFD Chcem vidieť: {Count} položiek z ČSFD, v knižnici {Matched}, chýbajúcich {Missing}",
            entries.Count,
            result.Count,
            notInLibrary.Count);

        foreach (var entry in notInLibrary)
        {
            result.Add(await ToMissingDtoAsync(entry, cancellationToken).ConfigureAwait(false));
        }

        return Ok(result);
    }

    /// <summary>
    /// Sviatočné odporúčania (<paramref name="event"/>: newyear, valentine, easter, halloween, nicholas, christmas) z kurátorovaného
    /// zoznamu: najprv tituly v knižnici používateľa, potom najviac <paramref name="missing"/> chýbajúcich s detailmi pre Seerr;
    /// v oboch skupinách podľa hodnotenia ČSFD zostupne, spolu najviac <paramref name="limit"/>. Formát ako TvTips (Time/Channel sú null).
    /// </summary>
    /// <param name="userId">Voliteľne iný používateľ – len pre admina alebo API kľúč (ako pri TvTips).</param>
    [HttpGet("Seasonal")]
    public async Task<ActionResult<IReadOnlyList<CsfdTvTipDto>>> Seasonal(
        [FromQuery(Name = "event")] string? @event,
        [FromQuery] int limit = 20,
        [FromQuery] int missing = 20,
        [FromQuery] Guid? userId = null,
        CancellationToken cancellationToken = default)
    {
        if (SeasonalCatalog.NormalizeEvent(@event) is not { } eventKey)
        {
            return BadRequest(new { message = "Neznámy sviatok – povolené: " + string.Join(", ", SeasonalCatalog.EventKeys) });
        }

        var (resolvedId, forbidden) = ResolveUserId(User, userId);
        if (forbidden)
        {
            return Forbid();
        }

        if (resolvedId is not { } id || _userManager.GetUserById(id) is not { } user)
        {
            return Unauthorized();
        }

        var maxTotal = Math.Clamp(limit, 1, 50);
        var maxMissing = Math.Clamp(missing, 0, 20);
        var entries = SeasonalCatalog.Get(eventKey);
        var inLibrary = new List<CsfdTvTipDto>();
        var notInLibrary = new List<SeasonalEntry>();
        var seen = new HashSet<Guid>();
        foreach (var entry in entries)
        {
            BaseItem? item;
            try
            {
                item = FindInLibrary(user, entry.CsfdId, entry.TmdbId, entry.MediaType, entry.Year, entry.LocalTitle, entry.Title);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "ČSFD sviatky: hľadanie {Title} v knižnici zlyhalo", entry.Title);
                continue;
            }

            if (item is null)
            {
                notInLibrary.Add(entry);
                continue;
            }

            if (!seen.Add(item.Id))
            {
                continue;
            }

            inLibrary.Add(new CsfdTvTipDto
            {
                CsfdId = entry.CsfdId ?? CsfdIdOf(item) ?? 0,
                Title = item.Name,
                Year = entry.Year ?? item.ProductionYear,
                ItemId = item.Id,
                InLibrary = true,
                RatingPercent = item.CommunityRating is { } r ? (int)Math.Round(r * 10) : null
            });
        }

        var result = inLibrary
            .OrderByDescending(t => t.RatingPercent ?? -1)
            .Take(maxTotal)
            .ToList();

        var missingSlots = Math.Min(maxMissing, maxTotal - result.Count);
        if (missingSlots > 0)
        {
            var missingDtos = new List<CsfdTvTipDto>();
            foreach (var entry in notInLibrary)
            {
                var detail = entry.CsfdId is { } csfdId
                    ? await _seasonal.GetDetailAsync(csfdId, cancellationToken).ConfigureAwait(false)
                    : null;
                missingDtos.Add(ToSeasonalMissingDto(entry, detail));
            }

            result.AddRange(missingDtos.OrderByDescending(t => t.RatingPercent ?? -1).Take(missingSlots));
        }

        _logger.LogInformation(
            "ČSFD sviatky {Event}: {Count} titulov, v knižnici {Matched}, chýbajúcich vrátených {Missing}",
            eventKey,
            entries.Count,
            inLibrary.Count,
            result.Count(r => !r.InLibrary));

        return Ok(result);
    }

    /// <summary>Používateľ z tokenu (claim Jellyfin-UserId), alebo null (aj pri API kľúči, ten má prázdne ID).</summary>
    private Jellyfin.Database.Implementations.Entities.User? CurrentUser()
        => ClaimUserId(User) is { } userId ? _userManager.GetUserById(userId) : null;

    /// <summary>ID používateľa z tokenu; null, ak chýba alebo je prázdne (API kľúč).</summary>
    internal static Guid? ClaimUserId(ClaimsPrincipal principal)
        => Guid.TryParse(principal.FindFirst(UserIdClaim)?.Value, out var id) && id != Guid.Empty ? id : null;

    /// <summary>
    /// Za koho sa pýta: bez <paramref name="requested"/> za seba; iného používateľa smie zadať len admin alebo API kľúč.
    /// </summary>
    internal static (Guid? UserId, bool Forbidden) ResolveUserId(ClaimsPrincipal principal, Guid? requested)
    {
        var own = ClaimUserId(principal);
        if (requested is not { } wanted || wanted == Guid.Empty || wanted == own)
        {
            return (own, false);
        }

        var isApiKey = bool.TryParse(principal.FindFirst(IsApiKeyClaim)?.Value, out var apiKey) && apiKey;
        return isApiKey || principal.IsInRole(AdministratorRole) ? (wanted, false) : (null, true);
    }

    /// <summary>Titul v knižnici používateľa: podľa ČSFD ID, inak (ešte neidentifikované pluginom) podľa názvu a roku.</summary>
    private BaseItem? FindInLibrary(Jellyfin.Database.Implementations.Entities.User user, int csfdId, string title, int? year)
    {
        var item = FindByProviderId(user, Plugin.ProviderKey, csfdId, MovieAndSeries);
        return item ?? FindByName(user, title, year is { } y ? new[] { y } : Array.Empty<int>(), MovieAndSeries);
    }

    /// <summary>
    /// Titul zo sviatočného výberu v knižnici: podľa ČSFD ID, potom TMDb ID (len správny typ – TMDb má pre filmy a seriály
    /// samostatné číslovanie), nakoniec podľa názvov s tolerantným rokom (±1, ČSFD a TMDb sa občas o rok líšia).
    /// </summary>
    private BaseItem? FindInLibrary(
        Jellyfin.Database.Implementations.Entities.User user,
        int? csfdId,
        int? tmdbId,
        string? mediaType,
        int? year,
        params string?[] titles)
    {
        var kinds = string.Equals(mediaType, "tv", StringComparison.OrdinalIgnoreCase)
            ? new[] { BaseItemKind.Series }
            : new[] { BaseItemKind.Movie };

        if (csfdId is { } c && FindByProviderId(user, Plugin.ProviderKey, c, MovieAndSeries) is { } byCsfd)
        {
            return byCsfd;
        }

        if (tmdbId is { } t && FindByProviderId(user, MediaBrowser.Model.Entities.MetadataProvider.Tmdb.ToString(), t, kinds) is { } byTmdb)
        {
            return byTmdb;
        }

        var years = year is { } y ? new[] { y - 1, y, y + 1 } : Array.Empty<int>();
        foreach (var title in titles.Where(t => !string.IsNullOrWhiteSpace(t)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (FindByName(user, title!, years, kinds) is { } byName)
            {
                return byName;
            }
        }

        return null;
    }

    private BaseItem? FindByProviderId(Jellyfin.Database.Implementations.Entities.User user, string provider, int id, BaseItemKind[] kinds)
        => _libraryManager.GetItemList(new InternalItemsQuery(user)
        {
            IncludeItemTypes = kinds,
            HasAnyProviderId = new Dictionary<string, string> { [provider] = id.ToString(CultureInfo.InvariantCulture) },
            Recursive = true,
            Limit = 1
        }).FirstOrDefault();

    private BaseItem? FindByName(Jellyfin.Database.Implementations.Entities.User user, string title, int[] years, BaseItemKind[] kinds)
        => _libraryManager.GetItemList(new InternalItemsQuery(user)
        {
            IncludeItemTypes = kinds,
            Name = title,
            Years = years,
            Recursive = true,
            Limit = 1
        }).FirstOrDefault();

    /// <summary>ČSFD ID z položky knižnice (ak ju už plugin identifikoval).</summary>
    private static int? CsfdIdOf(BaseItem item)
        => item.ProviderIds.TryGetValue(Plugin.ProviderKey, out var v) && int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) ? id : null;

    /// <summary>
    /// Chýbajúci sviatočný titul pre klienta: názvy na vyhľadanie v Seerr (CZ/SK, originál, ČSFD), typ z kurátorovaného zoznamu,
    /// hodnotenie a plagát z ČSFD (ak je ČSFD ID známe). Bez ČSFD ID: CsfdId = 0, bez hodnotenia a plagátu.
    /// </summary>
    internal static CsfdTvTipDto ToSeasonalMissingDto(SeasonalEntry entry, CsfdMovie? detail)
    {
        var titles = new List<string?> { detail?.Title, entry.LocalTitle, entry.Title };
        titles.AddRange(detail?.TitlesOther?.Select(t => t.Title) ?? Enumerable.Empty<string?>());
        var poster = CsfdMetadataMapper.FixUrl(detail?.Poster);

        return new CsfdTvTipDto
        {
            CsfdId = entry.CsfdId ?? 0,
            Title = FirstNonEmpty(detail?.Title, entry.LocalTitle, entry.Title),
            Year = entry.Year ?? detail?.Year,
            InLibrary = false,
            RatingPercent = detail?.Rating,
            MediaType = string.Equals(entry.MediaType, "tv", StringComparison.OrdinalIgnoreCase) ? "tv" : "movie",
            Titles = titles
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .Select(t => t!.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList(),
            Poster = poster,
            Thumbnail = poster,
            Photo = CsfdMetadataMapper.FixUrl(detail?.Photo),
            Overview = detail is null ? null : CsfdText.PickOverview(new[] { detail }, new PluginConfiguration { FallbackCzech = true }),
            Genres = detail?.Genres,
            DurationMinutes = detail?.Duration
        };
    }

    private static string FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim() ?? string.Empty;

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
            Poster = CsfdMetadataMapper.FixUrl(detail?.Poster) ?? tip.Thumbnail,
            Thumbnail = tip.Thumbnail,
            Photo = CsfdMetadataMapper.FixUrl(detail?.Photo),
            Overview = detail is null ? null : CsfdText.PickOverview(new[] { detail }, new PluginConfiguration { FallbackCzech = true }),
            Genres = detail?.Genres,
            DurationMinutes = detail?.Duration
        };
    }
}
