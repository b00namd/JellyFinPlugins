using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyTube.Models;
using Jellyfin.Plugin.JellyTube.Services;
using MediaBrowser.Controller.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.JellyTube.Api;

/// <summary>
/// REST API endpoints for the YouTube Downloader plugin.
/// All endpoints require Jellyfin admin privileges.
/// </summary>
[ApiController]
[Route("api/jellytube")]
[Authorize(Policy = "RequiresElevation")]
public class JellyTubeController : ControllerBase
{
    private readonly DownloadQueueService _queue;
    private readonly YtDlpService _ytDlp;

    /// <summary>
    /// Initializes a new instance of the <see cref="JellyTubeController"/> class.
    /// </summary>
    public JellyTubeController(DownloadQueueService queue, YtDlpService ytDlp)
    {
        _queue = queue;
        _ytDlp = ytDlp;
    }

    /// <summary>
    /// Enqueues a new download job.
    /// </summary>
    /// <param name="request">The URL and playlist flag.</param>
    /// <returns>The created <see cref="DownloadJob"/>.</returns>
    [HttpPost("download")]
    [ProducesResponseType(typeof(DownloadJob), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult<DownloadJob> EnqueueDownload([FromBody] EnqueueRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Url))
        {
            return BadRequest("URL must not be empty.");
        }

        var job = _queue.Enqueue(request.Url, request.IsPlaylist, overrideDownloadPath: request.DownloadPath);
        return StatusCode(StatusCodes.Status201Created, job);
    }

    /// <summary>
    /// Returns all download jobs (active, pending, and completed).
    /// </summary>
    [HttpGet("jobs")]
    [ProducesResponseType(typeof(IReadOnlyList<DownloadJob>), StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyList<DownloadJob>> GetJobs()
    {
        return Ok(_queue.GetAllJobs());
    }

    /// <summary>
    /// Returns a single job by its ID.
    /// </summary>
    [HttpGet("jobs/{id:guid}")]
    [ProducesResponseType(typeof(DownloadJob), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<DownloadJob> GetJob(Guid id)
    {
        var job = _queue.GetJob(id);
        if (job is null)
        {
            return NotFound();
        }

        return Ok(job);
    }

    /// <summary>
    /// Cancels a queued job.
    /// </summary>
    [HttpDelete("jobs/{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult CancelJob(Guid id)
    {
        var job = _queue.GetJob(id);
        if (job is null)
        {
            return NotFound();
        }

        _queue.Cancel(id);
        return NoContent();
    }

    /// <summary>
    /// Fetches metadata for a YouTube URL without downloading it.
    /// </summary>
    [HttpPost("fetch-metadata")]
    [ProducesResponseType(typeof(VideoMetadata), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<VideoMetadata>> FetchMetadata(
        [FromBody] FetchMetadataRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Url))
        {
            return BadRequest("URL must not be empty.");
        }

        var meta = await _ytDlp.FetchMetadataAsync(request.Url, cancellationToken);
        if (meta is null)
        {
            return BadRequest("Could not fetch metadata. Check the URL and that yt-dlp is installed.");
        }

        return Ok(meta);
    }

    /// <summary>
    /// Serves the plugin UI JavaScript file.
    /// </summary>
    [HttpGet("ui")]
    [AllowAnonymous]
    [Produces("application/javascript")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult GetUiScript()
    {
        var assembly = Assembly.GetExecutingAssembly();
        const string ResourceName = "Jellyfin.Plugin.JellyTube.Configuration.configPage.js";
        var stream = assembly.GetManifestResourceStream(ResourceName);
        if (stream is null)
        {
            return NotFound();
        }

        using var reader = new StreamReader(stream);
        return Content(reader.ReadToEnd(), "application/javascript");
    }

    /// <summary>
    /// Checks whether yt-dlp and ffmpeg are available and returns their versions.
    /// </summary>
    [HttpGet("tools-check")]
    [ProducesResponseType(typeof(ToolsCheckResult), StatusCodes.Status200OK)]
    public async Task<ActionResult<ToolsCheckResult>> CheckTools()
    {
        var config = Plugin.Instance!.Configuration;
        var ytDlpBin = string.IsNullOrWhiteSpace(config.YtDlpBinaryPath) ? "yt-dlp" : config.YtDlpBinaryPath;

        var ffmpegBin = string.IsNullOrWhiteSpace(config.FfmpegBinaryPath) ? "ffmpeg" : config.FfmpegBinaryPath;

        var (ytAvailable, ytVersion, ytError) = await TryGetVersionAsync(ytDlpBin, "--version");
        var (ffAvailable, ffVersion, ffError) = await TryGetVersionAsync(ffmpegBin, "-version");

        return Ok(new ToolsCheckResult(ytAvailable, ytVersion, ytError, ffAvailable, ffVersion, ffError));
    }

    private static async Task<(bool Available, string? Version, string? Error)> TryGetVersionAsync(string binary, string args)
    {
        try
        {
            var psi = new ProcessStartInfo(binary, args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
                CreateNoWindow         = true
            };
            using var proc = Process.Start(psi);
            if (proc is null) return (false, null, "Process.Start returned null");

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var stdoutTask = proc.StandardOutput.ReadToEndAsync(cts.Token);
            var stderrTask = proc.StandardError.ReadToEndAsync(cts.Token);
            await Task.WhenAll(stdoutTask, stderrTask);
            await proc.WaitForExitAsync(cts.Token);

            var stdout = stdoutTask.Result;
            var stderr = stderrTask.Result;

            // yt-dlp writes version to stdout, ffmpeg writes to stderr first line
            var version = !string.IsNullOrWhiteSpace(stdout)
                ? stdout.Split('\n')[0].Trim()
                : stderr.Split('\n')[0].Trim();

            return (true, version, null);
        }
        catch (Exception ex)
        {
            return (false, null, ex.Message);
        }
    }

    /// <summary>
    /// Returns a summary of queue statistics.
    /// </summary>
    [HttpGet("status")]
    [ProducesResponseType(typeof(QueueStatus), StatusCodes.Status200OK)]
    public ActionResult<QueueStatus> GetStatus()
    {
        var jobs = _queue.GetAllJobs();
        int queued = 0, active = 0, completed = 0, failed = 0;

        foreach (var job in jobs)
        {
            switch (job.Status)
            {
                case DownloadJobStatus.Queued:
                    queued++;
                    break;
                case DownloadJobStatus.FetchingMetadata:
                case DownloadJobStatus.Downloading:
                case DownloadJobStatus.WritingMetadata:
                    active++;
                    break;
                case DownloadJobStatus.Completed:
                    completed++;
                    break;
                case DownloadJobStatus.Failed:
                case DownloadJobStatus.Cancelled:
                    failed++;
                    break;
            }
        }

        return Ok(new QueueStatus(queued, active, completed, failed));
    }
}

/// <summary>Request body for enqueuing a download.</summary>
public record EnqueueRequest(
    [Required] string Url,
    bool IsPlaylist = false,
    string? DownloadPath = null);

/// <summary>Request body for fetching metadata.</summary>
public record FetchMetadataRequest(
    [Required] string Url);

/// <summary>Summary of the download queue state.</summary>
public record QueueStatus(
    int Queued,
    int Active,
    int Completed,
    int Failed);

/// <summary>Availability and version info for required tools.</summary>
public record ToolsCheckResult(
    bool YtDlpAvailable,
    string? YtDlpVersion,
    string? YtDlpError,
    bool FfmpegAvailable,
    string? FfmpegVersion,
    string? FfmpegError);
