using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyTube.Models;
using Microsoft.Extensions.Logging;
using YoutubeDLSharp;
using YoutubeDLSharp.Metadata;
using YoutubeDLSharp.Options;

namespace Jellyfin.Plugin.JellyTube.Services;

/// <summary>
/// Wraps YoutubeDLSharp to fetch metadata and download videos via yt-dlp.
/// </summary>
public class YtDlpService
{
    private readonly ILogger<YtDlpService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="YtDlpService"/> class.
    /// </summary>
    public YtDlpService(ILogger<YtDlpService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Fetches video metadata without downloading the video.
    /// Returns <c>null</c> if the fetch fails.
    /// </summary>
    public async Task<VideoMetadata?> FetchMetadataAsync(string url, CancellationToken ct)
    {
        var ytdl = CreateClient();

        try
        {
            var result = await ytdl.RunVideoDataFetch(url, ct: ct);
            if (!result.Success)
            {
                _logger.LogWarning("Metadata fetch failed for {Url}: {Error}",
                    url, string.Join("; ", result.ErrorOutput));
                return null;
            }

            return MapToMetadata(result.Data);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception while fetching metadata for {Url}", url);
            return null;
        }
    }

    /// <summary>
    /// Downloads a single video to the specified output directory.
    /// </summary>
    public async Task<bool> DownloadVideoAsync(
        string url,
        string outputDir,
        IProgress<DownloadProgress>? progress,
        CancellationToken ct,
        string? archivePath = null)
    {
        var ytdl = CreateClient(outputDir);
        var config = Plugin.Instance!.Configuration;
        var mergeFormat = GetMergeFormat(config.PreferredContainer);
        var opts = BuildSubtitleOptions(playlist: false, archivePath: archivePath);

        try
        {
            var result = await ytdl.RunVideoDownload(
                url,
                format: config.VideoFormat,
                mergeFormat: mergeFormat,
                ct: ct,
                progress: progress,
                overrideOptions: opts);
            if (!result.Success)
            {
                _logger.LogWarning("Download failed for {Url}: {Error}",
                    url, string.Join("; ", result.ErrorOutput));
            }

            return result.Success;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception while downloading {Url}", url);
            return false;
        }
    }

    /// <summary>
    /// Downloads all videos in a playlist to the specified output directory.
    /// </summary>
    public async Task<bool> DownloadPlaylistAsync(
        string url,
        string outputDir,
        IProgress<DownloadProgress>? progress,
        CancellationToken ct,
        string? archivePath = null,
        int maxAgeDays = 0,
        bool isScheduled = false)
    {
        var config = Plugin.Instance!.Configuration;
        var binary = string.IsNullOrWhiteSpace(config.YtDlpBinaryPath) ? "yt-dlp" : config.YtDlpBinaryPath;
        var mergeFormat = config.PreferredContainer?.ToLowerInvariant() switch
        {
            "mkv"  => "mkv",
            "webm" => "webm",
            _      => "mp4",
        };

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName               = binary,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
                CreateNoWindow         = true,
            };

            psi.ArgumentList.Add("--yes-playlist");
            psi.ArgumentList.Add("--ignore-errors");
            psi.ArgumentList.Add("--no-overwrites");
            psi.ArgumentList.Add("--write-info-json");
            psi.ArgumentList.Add("-o");
            var outputTemplate = config.OrganiseByChannel
                ? System.IO.Path.Combine(outputDir, "%(channel)s", "%(title)s - %(id)s.%(ext)s")
                : System.IO.Path.Combine(outputDir, "%(title)s - %(id)s.%(ext)s");
            psi.ArgumentList.Add(outputTemplate);

            if (!string.IsNullOrWhiteSpace(config.VideoFormat))
            {
                psi.ArgumentList.Add("--format");
                psi.ArgumentList.Add(config.VideoFormat);
            }

            psi.ArgumentList.Add("--merge-output-format");
            psi.ArgumentList.Add(mergeFormat);

            if (!string.IsNullOrWhiteSpace(config.FfmpegBinaryPath))
            {
                psi.ArgumentList.Add("--ffmpeg-location");
                psi.ArgumentList.Add(config.FfmpegBinaryPath);
            }

            if (config.DownloadThumbnails)
                psi.ArgumentList.Add("--write-thumbnail");

            if (config.DownloadSubtitles)
            {
                psi.ArgumentList.Add("--write-auto-subs");
                psi.ArgumentList.Add("--write-subs");
                if (!string.IsNullOrWhiteSpace(config.SubtitleLanguages))
                {
                    psi.ArgumentList.Add("--sub-langs");
                    psi.ArgumentList.Add(config.SubtitleLanguages);
                }
            }

            if (!string.IsNullOrWhiteSpace(config.DefaultAudioLanguage))
            {
                psi.ArgumentList.Add("--postprocessor-args");
                psi.ArgumentList.Add($"ffmpeg:-metadata:s:a:0 language={config.DefaultAudioLanguage.Trim()}");
            }

            var effectiveMaxAge = maxAgeDays > 0 ? maxAgeDays : (isScheduled ? config.PlaylistMaxAgeDays : 0);
            if (effectiveMaxAge > 0)
            {
                psi.ArgumentList.Add("--dateafter");
                psi.ArgumentList.Add(DateTime.UtcNow.AddDays(-effectiveMaxAge).ToString("yyyyMMdd"));
                psi.ArgumentList.Add("--break-on-reject");
            }

            if (!string.IsNullOrEmpty(archivePath))
            {
                psi.ArgumentList.Add("--download-archive");
                psi.ArgumentList.Add(archivePath);
                psi.ArgumentList.Add("--break-on-existing");
            }

            psi.ArgumentList.Add("--");
            psi.ArgumentList.Add(url);

            _logger.LogInformation("yt-dlp playlist download starting: {Url} → {Dir}", url, outputDir);

            using var proc = new Process { StartInfo = psi };
            proc.Start();

            var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = proc.StandardError.ReadToEndAsync(ct);
            await Task.WhenAll(stdoutTask, stderrTask);
            await proc.WaitForExitAsync(ct);

            var stderr = stderrTask.Result.Trim();
            if (!string.IsNullOrEmpty(stderr))
                _logger.LogInformation("yt-dlp stderr: {Stderr}", stderr);

            _logger.LogInformation("yt-dlp playlist download finished, exit code {Code}", proc.ExitCode);
            return proc.ExitCode == 0;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception while downloading playlist {Url}", url);
            return false;
        }
    }

    // -------------------------------------------------------------------------
    // Private helpers
    // -------------------------------------------------------------------------

    private static YoutubeDL CreateClient(string? outputDir = null)
    {
        var config = Plugin.Instance!.Configuration;

        return new YoutubeDL
        {
            YoutubeDLPath = string.IsNullOrWhiteSpace(config.YtDlpBinaryPath)
                ? "yt-dlp"
                : config.YtDlpBinaryPath,
            FFmpegPath = string.IsNullOrWhiteSpace(config.FfmpegBinaryPath)
                ? "ffmpeg"
                : config.FfmpegBinaryPath,
            OutputFolder = outputDir ?? config.DownloadPath,
            OverwriteFiles = false,
            IgnoreDownloadErrors = false,
            OutputFileTemplate = "%(title)s - %(id)s.%(ext)s"
        };
    }

    private static DownloadMergeFormat GetMergeFormat(string? container) =>
        container?.ToLowerInvariant() switch
        {
            "mkv" => DownloadMergeFormat.Mkv,
            "webm" => DownloadMergeFormat.Webm,
            _ => DownloadMergeFormat.Mp4
        };

    private static OptionSet BuildSubtitleOptions(bool playlist, string? archivePath = null, int maxAgeDays = 0, bool isScheduled = false)
    {
        var config = Plugin.Instance!.Configuration;

        var opts = new OptionSet
        {
            WriteThumbnail  = config.DownloadThumbnails,
            WriteAutoSubs   = config.DownloadSubtitles,
            WriteSubs       = config.DownloadSubtitles,
            SubLangs        = config.DownloadSubtitles ? config.SubtitleLanguages : null,
            NoPlaylist      = !playlist,
            WriteInfoJson   = playlist,  // write per-video .info.json so metadata can be read back for all items
            IgnoreErrors    = playlist,  // skip unavailable/deleted videos instead of aborting the whole playlist
        };

        // Per-entry maxAgeDays takes priority; global fallback only for scheduled runs (not manual downloads)
        var effectiveMaxAge = maxAgeDays > 0 ? maxAgeDays : (isScheduled ? config.PlaylistMaxAgeDays : 0);
        if (playlist && effectiveMaxAge > 0)
        {
            opts.DateAfter = DateTime.UtcNow.AddDays(-effectiveMaxAge);
#pragma warning disable CS0618 // BreakOnReject: deprecated in favour of --break-match-filter; no relative-date support in match-filter syntax
            opts.BreakOnReject = true; // stop at first video older than the date limit (channel is newest-first)
#pragma warning restore CS0618
        }

        // Embed audio language tag via ffmpeg post-processor
        if (!string.IsNullOrWhiteSpace(config.DefaultAudioLanguage))
        {
            opts.PostprocessorArgs = $"ffmpeg:-metadata:s:a:0 language={config.DefaultAudioLanguage.Trim()}";
        }

        // Use archive file to skip already-downloaded (or deleted) videos
        if (!string.IsNullOrEmpty(archivePath))
        {
            opts.DownloadArchive = archivePath;
            opts.BreakOnExisting = true; // stop at first archived video (channel is sorted newest-first)
        }

        return opts;
    }

    private static VideoMetadata MapToMetadata(VideoData d)
    {
        return new VideoMetadata
        {
            VideoId = d.ID ?? string.Empty,
            Title = d.Title ?? string.Empty,
            Description = d.Description ?? string.Empty,
            ChannelName = d.Channel ?? d.Uploader ?? string.Empty,
            ChannelId = d.ChannelID ?? string.Empty,
            UploaderUrl = d.UploaderUrl ?? string.Empty,
            UploadDate = d.UploadDate,
            DurationSeconds = d.Duration,
            ViewCount = d.ViewCount,
            LikeCount = d.LikeCount,
            ThumbnailUrl = d.Thumbnail ?? string.Empty,
            WebpageUrl = d.WebpageUrl ?? string.Empty,
            Tags = d.Tags ?? Array.Empty<string>(),
            Categories = d.Categories ?? Array.Empty<string>()
        };
    }
}
