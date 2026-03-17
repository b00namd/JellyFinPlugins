using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyTubbing.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.JellyTubbing.Api;

/// <summary>
/// REST API endpoints for the JellyTubbing plugin.
/// </summary>
[ApiController]
[Route("api/jellytubbing")]
[Authorize(Policy = "RequiresElevation")]
public class JellyTubbingController : ControllerBase
{
    private readonly InvidiousService _invidious;

    /// <summary>
    /// Initializes a new instance of the <see cref="JellyTubbingController"/> class.
    /// </summary>
    public JellyTubbingController(InvidiousService invidious)
    {
        _invidious = invidious;
    }

    /// <summary>
    /// Serves the embedded configuration page JavaScript.
    /// </summary>
    [HttpGet("ui")]
    [AllowAnonymous]
    [Produces("application/javascript")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult GetUiScript()
    {
        var stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream("Jellyfin.Plugin.JellyTubbing.Configuration.configPage.js");
        if (stream is null) return NotFound();
        using var reader = new StreamReader(stream);
        return Content(reader.ReadToEnd(), "application/javascript");
    }

    /// <summary>
    /// Tests whether the configured Invidious instance is reachable.
    /// </summary>
    [HttpGet("test-invidious")]
    [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
    public async Task<IActionResult> TestInvidious(CancellationToken ct)
    {
        var url = Plugin.Instance?.Configuration.InvidiousInstanceUrl ?? string.Empty;
        if (string.IsNullOrWhiteSpace(url))
            return Ok(new { reachable = false, message = "Keine Invidious-URL konfiguriert." });

        var ok = await _invidious.IsReachableAsync(ct);
        return Ok(new { reachable = ok, message = ok ? url : $"Nicht erreichbar: {url}" });
    }
}
