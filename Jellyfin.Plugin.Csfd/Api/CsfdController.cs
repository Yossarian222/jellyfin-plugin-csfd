using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Csfd.Api;

/// <summary>Pomocné endpointy pre konfiguračnú stránku (len pre adminov).</summary>
[ApiController]
[Authorize(Policy = Policies.RequiresElevation)]
[Route("Plugins/Csfd")]
public class CsfdController : ControllerBase
{
    private readonly CsfdApiClient _client;

    public CsfdController(CsfdApiClient client)
    {
        _client = client;
    }

    /// <summary>Otestuje spojenie so sidecarom na ČSFD ID (predvolene Pulp Fiction).</summary>
    [HttpGet("Test")]
    public async Task<ActionResult<object>> Test([FromQuery] int id = 8852, CancellationToken cancellationToken = default)
    {
        var started = DateTime.UtcNow;
        var movie = await _client.GetMovieAsync(id, "sk", cancellationToken, bypassCache: true).ConfigureAwait(false);
        if (movie is null)
        {
            return Ok(new { ok = false, message = "csfd-api neodpovedá alebo vrátilo chybu – pozri log Jellyfinu a kontajnera csfd-api." });
        }

        return Ok(new
        {
            ok = true,
            title = movie.Title,
            year = movie.Year,
            rating = movie.Rating,
            ms = (int)(DateTime.UtcNow - started).TotalMilliseconds
        });
    }

    /// <summary>Vymaže diskovú cache pluginu.</summary>
    [HttpPost("ClearCache")]
    public ActionResult<object> ClearCache()
    {
        var dir = Plugin.Instance is null ? null : Path.Combine(Plugin.Instance.DataFolderPath, "cache");
        var deleted = 0;
        if (dir is not null && Directory.Exists(dir))
        {
            foreach (var file in Directory.EnumerateFiles(dir, "*.json"))
            {
                System.IO.File.Delete(file);
                deleted++;
            }
        }

        return Ok(new { ok = true, deleted });
    }
}
