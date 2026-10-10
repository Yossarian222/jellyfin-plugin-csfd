using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Csfd.Api;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Csfd.Seasonal;

/// <summary>Titul zo sviatočného výberu (Seasonal/seasonal.json).</summary>
public sealed class SeasonalEntry
{
    /// <summary>ČSFD ID; null, ak nie je overené (vtedy bez hodnotenia a plagátu z ČSFD).</summary>
    public int? CsfdId { get; set; }

    /// <summary>TMDb ID (podľa <see cref="MediaType"/> filmu alebo seriálu); páruje sa s knižnicou cez provider „Tmdb“.</summary>
    public int? TmdbId { get; set; }

    /// <summary>„movie“ alebo „tv“ (typ pre Seerr).</summary>
    public string MediaType { get; set; } = "movie";

    /// <summary>Originálny názov.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Český/slovenský názov (ak sa líši od originálu).</summary>
    public string? LocalTitle { get; set; }

    public int? Year { get; set; }
}

/// <summary>Kurátorovaný zoznam sviatočných filmov a seriálov – vložený do DLL ako EmbeddedResource.</summary>
public static class SeasonalCatalog
{
    /// <summary>Podporované sviatky (kľúč parametra <c>event</c>).</summary>
    public static readonly IReadOnlyList<string> EventKeys = new[] { "newyear", "valentine", "easter", "halloween", "nicholas", "christmas" };

    private const string ResourceName = "Jellyfin.Plugin.Csfd.Seasonal.seasonal.json";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private static readonly Lazy<IReadOnlyDictionary<string, IReadOnlyList<SeasonalEntry>>> Data = new(Load);

    /// <summary>Všetky sviatky so zoznamami titulov.</summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<SeasonalEntry>> Events => Data.Value;

    /// <summary>Normalizovaný kľúč sviatku, alebo null pri neznámom.</summary>
    public static string? NormalizeEvent(string? key)
    {
        var k = key?.Trim().ToLowerInvariant();
        return k is not null && EventKeys.Contains(k) ? k : null;
    }

    /// <summary>Tituly pre sviatok (prázdne pri neznámom kľúči).</summary>
    public static IReadOnlyList<SeasonalEntry> Get(string key)
        => NormalizeEvent(key) is { } k && Events.TryGetValue(k, out var list) ? list : Array.Empty<SeasonalEntry>();

    internal static IReadOnlyDictionary<string, IReadOnlyList<SeasonalEntry>> Parse(Stream json)
    {
        var root = JsonSerializer.Deserialize<SeasonalFile>(json, JsonOptions)
            ?? throw new InvalidDataException("seasonal.json je prázdny");
        return (root.Events ?? new Dictionary<string, List<SeasonalEntry>>())
            .ToDictionary(e => e.Key.ToLowerInvariant(), e => (IReadOnlyList<SeasonalEntry>)e.Value, StringComparer.OrdinalIgnoreCase);
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<SeasonalEntry>> Load()
    {
        using var stream = typeof(SeasonalCatalog).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException("Chýba vložený zdroj " + ResourceName);
        return Parse(stream);
    }

    private sealed class SeasonalFile
    {
        public Dictionary<string, List<SeasonalEntry>>? Events { get; set; }
    }
}

/// <summary>
/// Detaily sviatočných titulov z csfd-api (hodnotenie, plagát, názvy). Nad diskovou cache <see cref="CsfdApiClient"/>
/// drží ešte pamäťovú cache na 7 dní – sviatočné odporúčania sa pýtajú opakovane na tých istých 20 titulov.
/// </summary>
public sealed class CsfdSeasonalClient
{
    private static readonly TimeSpan Ttl = TimeSpan.FromDays(7);

    /// <summary>Neúspech (sidecar nedostupný) sa pamätá kratšie, aby sa ČSFD nezahlcovalo, ale rýchlo sa zotavilo.</summary>
    private static readonly TimeSpan FailureTtl = TimeSpan.FromHours(1);

    private static readonly ConcurrentDictionary<int, (DateTime At, CsfdMovie? Movie)> Memory = new();

    private readonly CsfdApiClient _client;
    private readonly ILogger<CsfdSeasonalClient> _logger;

    public CsfdSeasonalClient(CsfdApiClient client, ILogger<CsfdSeasonalClient> logger)
    {
        _client = client;
        _logger = logger;
    }

    /// <summary>Detail titulu (SK) podľa ČSFD ID; null, ak ho csfd-api nevráti.</summary>
    public async Task<CsfdMovie?> GetDetailAsync(int csfdId, CancellationToken cancellationToken)
    {
        if (Memory.TryGetValue(csfdId, out var hit) && DateTime.UtcNow - hit.At < (hit.Movie is null ? FailureTtl : Ttl))
        {
            return hit.Movie;
        }

        CsfdMovie? movie = null;
        try
        {
            movie = await _client.GetMovieAsync(csfdId, "sk", cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "ČSFD sviatky: detail {CsfdId} sa nepodarilo načítať", csfdId);
        }

        Memory[csfdId] = (DateTime.UtcNow, movie);
        return movie;
    }
}
