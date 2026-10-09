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
    private readonly CsfdAccountClient _account;

    public CsfdController(CsfdApiClient client, CsfdAccountClient account)
    {
        _client = client;
        _account = account;
    }

    /// <summary>Overí prihlásenie na ČSFD účet z nastavení.</summary>
    [HttpPost("TestLogin")]
    public async Task<ActionResult<object>> TestLogin(CancellationToken cancellationToken = default)
    {
        var (ok, message) = await _account.TestLoginAsync(cancellationToken).ConfigureAwait(false);
        return Ok(new { ok, message });
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
            // Aj podpriečinky (trivia/); súbor, ktorý sa práve používa, preskočíme.
            foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                try
                {
                    System.IO.File.Delete(file);
                    deleted++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }
        }

        return Ok(new { ok = true, deleted });
    }
}
