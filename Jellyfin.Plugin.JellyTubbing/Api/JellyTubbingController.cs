using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyTubbing.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.JellyTubbing.Api;

/// <summary>Request body for device poll endpoint.</summary>
public class DevicePollRequest
{
    /// <summary>Gets or sets the device code returned by oauth-device-start.</summary>
    public string DeviceCode { get; set; } = string.Empty;
}

/// <summary>
/// REST API endpoints for the JellyTubbing plugin.
/// </summary>
[ApiController]
[Route("api/jellytubbing")]
[Authorize(Policy = "RequiresElevation")]
public class JellyTubbingController : ControllerBase
{
    private readonly OAuthService _oauth;
    private readonly YouTubeApiService _youtube;
    private readonly StreamResolverService _resolver;
    private readonly SyncBackgroundService _sync;

    /// <summary>
    /// Initializes a new instance of the <see cref="JellyTubbingController"/> class.
    /// </summary>
    public JellyTubbingController(
        OAuthService oauth,
        YouTubeApiService youtube,
        StreamResolverService resolver,
        SyncBackgroundService sync)
    {
        _oauth    = oauth;
        _youtube  = youtube;
        _resolver = resolver;
        _sync     = sync;
    }

    // -----------------------------------------------------------------------
    // Config page UI
    // -----------------------------------------------------------------------

    /// <summary>Serves the embedded configuration page JavaScript.</summary>
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

    // -----------------------------------------------------------------------
    // yt-dlp check
    // -----------------------------------------------------------------------

    /// <summary>Checks whether yt-dlp is available on the server.</summary>
    [HttpGet("check-tools")]
    [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
    public async Task<IActionResult> CheckTools()
    {
        var bin = Plugin.Instance?.Configuration.YtDlpBinaryPath;
        if (string.IsNullOrWhiteSpace(bin)) bin = "yt-dlp";

        var (available, version, error) = await TryGetVersionAsync(bin, "--version");
        return Ok(new { ytDlpAvailable = available, ytDlpVersion = version, ytDlpError = error });
    }

    // -----------------------------------------------------------------------
    // OAuth2
    // -----------------------------------------------------------------------

    /// <summary>Starts the device authorization flow. Returns user_code and verification_url.</summary>
    [HttpPost("oauth-device-start")]
    [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
    public async Task<IActionResult> StartDeviceAuth(CancellationToken ct)
    {
        var config = Plugin.Instance?.Configuration;
        if (string.IsNullOrWhiteSpace(config?.OAuthClientId))
            return Ok(new { success = false, message = "OAuth-Client-ID nicht konfiguriert." });

        var result = await _oauth.StartDeviceAuthAsync(ct);
        if (result is null)
            return Ok(new { success = false, message = "Fehler beim Starten der Geraete-Authorisierung." });

        return Ok(new
        {
            success          = true,
            userCode         = result.UserCode,
            verificationUrl  = result.VerificationUrl,
            deviceCode       = result.DeviceCode,
            interval         = result.Interval,
            expiresIn        = result.ExpiresIn,
        });
    }

    /// <summary>Polls once for device authorization completion.</summary>
    [HttpPost("oauth-device-poll")]
    [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
    public async Task<IActionResult> PollDeviceAuth([FromBody] DevicePollRequest request, CancellationToken ct)
    {
        var status = await _oauth.PollDeviceAsync(request.DeviceCode, ct);
        return Ok(new { status });
    }

    /// <summary>Returns the current OAuth authorization status.</summary>
    [HttpGet("oauth-status")]
    [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
    public IActionResult GetOAuthStatus()
    {
        return Ok(new { authorized = _oauth.IsAuthorized });
    }

    /// <summary>Revokes the stored OAuth tokens.</summary>
    [HttpPost("oauth-revoke")]
    [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
    public IActionResult RevokeOAuth()
    {
        _oauth.Revoke();
        return Ok(new { success = true });
    }

    // -----------------------------------------------------------------------
    // Subscriptions
    // -----------------------------------------------------------------------

    /// <summary>Returns the user's YouTube subscriptions (requires OAuth).</summary>
    [HttpGet("subscriptions")]
    [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetSubscriptions(CancellationToken ct)
    {
        if (!_oauth.IsAuthorized)
            return Ok(new { success = false, message = "Nicht mit Google verbunden." });

        var subs = await _youtube.GetSubscriptionsAsync(ct);
        var synced = Plugin.Instance?.Configuration.SyncedChannelIds ?? [];

        var result = subs.Select(s => new
        {
            channelId = s.Snippet.ResourceId.ChannelId,
            title     = s.Snippet.Title,
            thumbnail = s.Snippet.Thumbnails.BestUrl,
            synced    = synced.Contains(s.Snippet.ResourceId.ChannelId),
        });

        return Ok(new { success = true, subscriptions = result });
    }

    // -----------------------------------------------------------------------
    // Sync
    // -----------------------------------------------------------------------

    /// <summary>Triggers an immediate sync of all configured channels.</summary>
    [HttpPost("sync")]
    [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
    public IActionResult TriggerSync(CancellationToken ct)
    {
        _ = _sync.TriggerSyncAsync(ct);
        return Ok(new { success = true, message = "Synchronisation gestartet." });
    }

    // -----------------------------------------------------------------------
    // Stream redirect (STRM playback)
    // -----------------------------------------------------------------------

    /// <summary>
    /// Resolves a YouTube video ID to a direct stream URL and returns a 302 redirect.
    /// Used by the .strm files generated for subscribed channels.
    /// </summary>
    [HttpGet("stream/{videoId}")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status302Found)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> StreamVideo(string videoId, CancellationToken ct)
    {
        var url = await _resolver.ResolveUrlAsync(videoId, ct);
        if (string.IsNullOrEmpty(url))
            return NotFound(new { message = $"Stream-URL fuer {videoId} konnte nicht aufgeloest werden." });

        return Redirect(url);
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static async Task<(bool Available, string? Version, string? Error)> TryGetVersionAsync(string binary, string args)
    {
        try
        {
            var psi = new ProcessStartInfo(binary, args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
                CreateNoWindow         = true,
            };
            using var proc = Process.Start(psi);
            if (proc is null) return (false, null, "Process.Start returned null");

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var stdout = await proc.StandardOutput.ReadToEndAsync(cts.Token);
            await proc.WaitForExitAsync(cts.Token);
            return (true, stdout.Split('\n')[0].Trim(), null);
        }
        catch (Exception ex)
        {
            return (false, null, ex.Message);
        }
    }
}
