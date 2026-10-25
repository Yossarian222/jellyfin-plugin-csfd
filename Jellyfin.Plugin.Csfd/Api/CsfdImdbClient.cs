using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Csfd.Api;

/// <summary>Hodnotenie z IMDb: priemer (1–10) a počet hlasov.</summary>
public sealed record ImdbRating(
    [property: JsonPropertyName("rating")] double Rating,
    [property: JsonPropertyName("votes")] int Votes);

/// <summary>
/// IMDb hodnotenia titulov v knižnici z oficiálneho datasetu title.ratings.tsv.gz (datasets.imdbws.com, bez kľúča).
/// Dataset sťahuje denná úloha, z neho sa uložia len tituly z knižnice (súbor mimo cache, ClearCache ho nezmaže).
/// </summary>
public sealed class CsfdImdbClient
{
    private const string DatasetUrl = "https://datasets.imdbws.com/title.ratings.tsv.gz";

    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static Dictionary<string, ImdbRating>? _ratings;

    private readonly ILogger<CsfdImdbClient> _logger;

    public CsfdImdbClient(ILogger<CsfdImdbClient> logger)
    {
        _logger = logger;
    }

    /// <summary>IMDb ID (tt…) → hodnotenie; prázdne, kým úloha dataset raz nestiahne.</summary>
    public IReadOnlyDictionary<string, ImdbRating> GetRatings()
    {
        if (_ratings is not null)
        {
            return _ratings;
        }

        var file = DataFile();
        try
        {
            if (file is not null && File.Exists(file))
            {
                _ratings = JsonSerializer.Deserialize<Dictionary<string, ImdbRating>>(File.ReadAllText(file));
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            _logger.LogWarning(ex, "IMDb hodnotenia: uložený súbor sa nedá prečítať");
        }

        return _ratings ??= new Dictionary<string, ImdbRating>();
    }

    /// <summary>Stiahne dataset a uloží hodnotenia titulov s IMDb ID z <paramref name="ids"/>; vráti ich počet.</summary>
    public async Task<int> RefreshAsync(ISet<string> ids, CancellationToken cancellationToken)
    {
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Jellyfin-Plugin-Csfd");
            await using var stream = await http.GetStreamAsync(DatasetUrl, cancellationToken).ConfigureAwait(false);
            await using var gzip = new GZipStream(stream, CompressionMode.Decompress);
            using var reader = new StreamReader(gzip);
            var fresh = await ParseAsync(reader, ids, cancellationToken).ConfigureAwait(false);

            _ratings = fresh;
            var file = DataFile();
            if (file is not null)
            {
                await File.WriteAllTextAsync(file, JsonSerializer.Serialize(fresh), cancellationToken).ConfigureAwait(false);
            }

            return fresh.Count;
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>Riadky „tconst\taverageRating\tnumVotes“ (prvý je hlavička); necháva len <paramref name="ids"/>.</summary>
    internal static async Task<Dictionary<string, ImdbRating>> ParseAsync(TextReader reader, ISet<string> ids, CancellationToken cancellationToken)
    {
        var ratings = new Dictionary<string, ImdbRating>();
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            var parts = line.Split('\t');
            if (parts.Length == 3
                && ids.Contains(parts[0])
                && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var rating)
                && int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var votes))
            {
                ratings[parts[0]] = new ImdbRating(rating, votes);
            }
        }

        return ratings;
    }

    private static string? DataFile()
    {
        var dataFolder = Plugin.Instance?.DataFolderPath;
        if (string.IsNullOrEmpty(dataFolder))
        {
            return null;
        }

        Directory.CreateDirectory(dataFolder);
        return Path.Combine(dataFolder, "imdb-ratings.json");
    }
}
