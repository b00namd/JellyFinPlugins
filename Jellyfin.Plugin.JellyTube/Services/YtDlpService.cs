using System;
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
        int maxAgeDays = 0)
    {
        var ytdl = CreateClient(outputDir);
        var config = Plugin.Instance!.Configuration;
        var opts = BuildSubtitleOptions(playlist: true, archivePath: archivePath, maxAgeDays: maxAgeDays);

        try
        {
            var result = await ytdl.RunVideoPlaylistDownload(
                url,
                format: config.VideoFormat,
                ct: ct,
                progress: progress,
                overrideOptions: opts);
            if (!result.Success)
            {
                _logger.LogWarning("Playlist download failed for {Url}: {Error}",
                    url, string.Join("; ", result.ErrorOutput));
            }

            return result.Success;
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

    private static OptionSet BuildSubtitleOptions(bool playlist, string? archivePath = null, int maxAgeDays = 0)
    {
        var config = Plugin.Instance!.Configuration;

        var opts = new OptionSet
        {
            WriteThumbnail = config.DownloadThumbnails,
            WriteAutoSubs = config.DownloadSubtitles,
            WriteSubs = config.DownloadSubtitles,
            SubLangs = config.DownloadSubtitles ? config.SubtitleLanguages : null,
            RestrictFilenames = true,
            NoPlaylist = !playlist
        };

        // Per-entry maxAgeDays takes priority; fall back to global setting
        var effectiveMaxAge = maxAgeDays > 0 ? maxAgeDays : config.PlaylistMaxAgeDays;
        if (playlist && effectiveMaxAge > 0)
        {
            opts.DateAfter = DateTime.UtcNow.AddDays(-effectiveMaxAge);
        }

        // Use archive file to skip already-downloaded (or deleted) videos
        if (!string.IsNullOrEmpty(archivePath))
        {
            opts.DownloadArchive = archivePath;
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
