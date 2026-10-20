using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyTubbing.Services;
using MediaBrowser.Model.Tasks;
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
    private readonly VideoCacheService _cache;
    private readonly ITaskManager _taskManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="JellyTubbingController"/> class.
    /// </summary>
    public JellyTubbingController(
        OAuthService oauth,
        YouTubeApiService youtube,
        VideoCacheService cache,
        ITaskManager taskManager)
    {
        _oauth       = oauth;
        _youtube     = youtube;
        _cache       = cache;
        _taskManager = taskManager;
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
        var config = Plugin.Instance?.Configuration;

        var ytdlpBin = VideoCacheService.ResolveYtDlpBinary();
        var ffmpegBin = string.IsNullOrWhiteSpace(config?.FfmpegBinaryPath) ? "ffmpeg" : config.FfmpegBinaryPath;

        var (ytDlpOk, ytDlpVer, ytDlpErr)   = await TryGetVersionAsync(ytdlpBin,  "--version");
        var (ffmpegOk, ffmpegVer, ffmpegErr) = await TryGetVersionAsync(ffmpegBin, "-version");

        return Ok(new
        {
            ytDlpAvailable  = ytDlpOk,  ytDlpVersion  = ytDlpVer,  ytDlpError  = ytDlpErr,
            ffmpegAvailable = ffmpegOk, ffmpegVersion = ffmpegVer,  ffmpegError = ffmpegErr,
        });
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
    public IActionResult TriggerSync()
    {
        // Runs as the scheduled task, so it is not tied to this request and shows progress in the dashboard
        _taskManager.QueueScheduledTask<ChannelSyncTask>();
        return Ok(new { success = true, message = "Synchronisation gestartet." });
    }

    // -----------------------------------------------------------------------
    // Stream (STRM playback)
    // -----------------------------------------------------------------------

    /// <summary>
    /// Delivers a YouTube video for STRM playback. The first request downloads the video completely
    /// into the cache (it waits until the download is done); later requests are served from disk
    /// with range support, so seeking and resuming work.
    /// </summary>
    [HttpGet("stream/{videoId}")]
    [HttpHead("stream/{videoId}")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status206PartialContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status502BadGateway)]
    public async Task<IActionResult> StreamVideo(string videoId, CancellationToken ct)
    {
        if (!VideoCacheService.IsValidVideoId(videoId))
            return BadRequest(new { message = "Ungueltige Video-ID." });

        var path = await _cache.GetOrDownloadAsync(videoId, ct);
        if (path is null)
            return StatusCode(StatusCodes.Status502BadGateway,
                new { message = $"Video {videoId} konnte nicht geladen werden (siehe Jellyfin-Log)." });

        return PhysicalFile(path, "video/mp4", enableRangeProcessing: true);
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
            var stdoutTask = proc.StandardOutput.ReadToEndAsync(cts.Token);
            var stderrTask = proc.StandardError.ReadToEndAsync(cts.Token);
            await Task.WhenAll(stdoutTask, stderrTask);
            await proc.WaitForExitAsync(cts.Token);

            // yt-dlp writes version to stdout; ffmpeg writes to stderr
            var version = !string.IsNullOrWhiteSpace(stdoutTask.Result)
                ? stdoutTask.Result.Split('\n')[0].Trim()
                : stderrTask.Result.Split('\n')[0].Trim();

            return (true, version, null);
        }
        catch (Exception ex)
        {
            return (false, null, ex.Message);
        }
    }
}
