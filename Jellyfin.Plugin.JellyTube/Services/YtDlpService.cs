using System;
using System.Diagnostics;
using System.Text.Json;
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
    private readonly DownloadArchiveService _archive;
    private readonly MediaBrowser.Common.Configuration.IApplicationPaths _appPaths;

    // Matches yt-dlp's members-only error, e.g.:
    //   ERROR: [youtube] mL-np8L4NiY: Join this channel to get access to members-only content…
    private static readonly System.Text.RegularExpressions.Regex _membersOnlyRegex = new(
        @"\[youtube\]\s+([A-Za-z0-9_-]{11}):\s+Join this channel",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// Initializes a new instance of the <see cref="YtDlpService"/> class.
    /// </summary>
    public YtDlpService(ILogger<YtDlpService> logger, DownloadArchiveService archive, MediaBrowser.Common.Configuration.IApplicationPaths appPaths)
    {
        _logger = logger;
        _archive = archive;
        _appPaths = appPaths;
    }

    /// <summary>
    /// Path to the plugin-managed yt-dlp binary in the (writable) plugin configuration directory.
    /// Used so the auto-update task can keep yt-dlp current without needing write access to a
    /// system path like /usr/local/bin (which is read-only in many container images).
    /// </summary>
    internal static string GetManagedYtDlpPath(MediaBrowser.Common.Configuration.IApplicationPaths appPaths) =>
        System.IO.Path.Combine(appPaths.PluginConfigurationsPath,
            OperatingSystem.IsWindows() ? "yt-dlp.exe" : "yt-dlp");

    /// <summary>
    /// Resolves the yt-dlp binary to invoke: an explicit configured path wins; otherwise the
    /// plugin-managed binary if it has been downloaded; otherwise "yt-dlp" from PATH.
    /// </summary>
    private string ResolveYtDlpBinary()
    {
        var configured = Plugin.Instance!.Configuration.YtDlpBinaryPath;
        if (!string.IsNullOrWhiteSpace(configured))
            return configured;

        var managed = GetManagedYtDlpPath(_appPaths);
        return System.IO.File.Exists(managed) ? managed : "yt-dlp";
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
        var binary = ResolveYtDlpBinary();
        // Scan the channel's "/videos" tab instead of its root so YouTube Shorts (which live in a
        // separate tab) are not pulled in. Shorts are normalised to plain watch URLs, so a URL or
        // duration filter can't reliably exclude them — targeting the Videos tab is the clean way.
        var effectiveUrl = NormalizeChannelUrl(url, config.ExcludeShorts);
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
            // Don't litter the media folders with channel/tab-level playlist .info.json files
            // (e.g. "<Channel> - Videos - <id>.info.json"); we only need the per-video ones.
            psi.ArgumentList.Add("--no-write-playlist-metafiles");
            psi.ArgumentList.Add("-o");
            string outputTemplate;
            if (config.OrganiseAsSeries)
            {
                // channel = series, "Season <year>" = season, "S<year>E<mmdd>" so Jellyfin recognises
                // each video as a dated episode (episode/season are also written authoritatively to NFO).
                outputTemplate = System.IO.Path.Combine(outputDir, "%(channel)s", "Season %(upload_date>%Y)s",
                    "%(channel)s - S%(upload_date>%Y)sE%(upload_date>%m%d)s - %(title)s - %(id)s.%(ext)s");
            }
            else if (config.OrganiseByChannel)
            {
                outputTemplate = System.IO.Path.Combine(outputDir, "%(channel)s", "%(title)s - %(id)s.%(ext)s");
            }
            else
            {
                outputTemplate = System.IO.Path.Combine(outputDir, "%(title)s - %(id)s.%(ext)s");
            }
            psi.ArgumentList.Add(outputTemplate);

            if (!string.IsNullOrWhiteSpace(config.VideoFormat))
            {
                psi.ArgumentList.Add("--format");
                psi.ArgumentList.Add(config.VideoFormat);
            }

            psi.ArgumentList.Add("--merge-output-format");
            psi.ArgumentList.Add(mergeFormat);

            if (config.EmbedChapters)
                psi.ArgumentList.Add("--embed-chapters");

            if (!string.IsNullOrWhiteSpace(config.FfmpegBinaryPath))
            {
                psi.ArgumentList.Add("--ffmpeg-location");
                psi.ArgumentList.Add(config.FfmpegBinaryPath);
            }

            if (!string.IsNullOrWhiteSpace(config.CookiesFilePath))
            {
                psi.ArgumentList.Add("--cookies");
                psi.ArgumentList.Add(config.CookiesFilePath);
            }

            // NOTE: no --write-thumbnail. yt-dlp would write a redundant .webp next to each video;
            // ThumbnailService already downloads a Jellyfin-friendly "-thumb.jpg" from the metadata.

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

            // Bound how deep we scan the channel listing. Because we deliberately do NOT use the
            // early-exit "--break-on-*" flags below (see notes), yt-dlp would otherwise extract
            // metadata for the channel's ENTIRE back-catalogue on every run. --playlist-end caps it
            // to the most-recent N entries, and --lazy-playlist streams them instead of buffering
            // the whole list first.
            psi.ArgumentList.Add("--lazy-playlist");
            if (config.PlaylistScanLimit > 0)
            {
                psi.ArgumentList.Add("--playlist-end");
                psi.ArgumentList.Add(config.PlaylistScanLimit.ToString());
            }

            var effectiveMaxAge = maxAgeDays > 0 ? maxAgeDays : (isScheduled ? config.PlaylistMaxAgeDays : 0);
            if (effectiveMaxAge > 0)
            {
                // NOTE: deliberately NO --break-on-reject. A channel listing is NOT strictly
                // newest-first: pinned videos can be old, and the Shorts/Live tabs restart the
                // date order (so the list jumps back to "newest" partway through). Breaking on the
                // first out-of-window video would skip newer matching videos further down the list.
                // --dateafter filters each entry individually and keeps scanning instead.
                psi.ArgumentList.Add("--dateafter");
                psi.ArgumentList.Add(DateTime.UtcNow.AddDays(-effectiveMaxAge).ToString("yyyyMMdd"));
            }

            if (!string.IsNullOrEmpty(archivePath))
            {
                // NOTE: deliberately NO --break-on-existing. If the newest video is already archived
                // but an older in-window video was never downloaded (it failed, was members-only, or
                // was published out of order), breaking at the first archived entry would skip that
                // gap forever. The archive file still prevents already-downloaded videos from being
                // fetched again — it just no longer halts the scan.
                psi.ArgumentList.Add("--download-archive");
                psi.ArgumentList.Add(archivePath);
            }

            psi.ArgumentList.Add("--");
            psi.ArgumentList.Add(effectiveUrl);

            _logger.LogInformation("yt-dlp playlist download starting: {Url} → {Dir}", effectiveUrl, outputDir);

            using var proc = new Process { StartInfo = psi };
            proc.Start();

            // 6-hour timeout to prevent indefinitely hanging downloads
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromHours(6));
            var linkedToken = timeoutCts.Token;

            try
            {
                var stdoutTask = proc.StandardOutput.ReadToEndAsync(linkedToken);
                var stderrTask = proc.StandardError.ReadToEndAsync(linkedToken);
                await Task.WhenAll(stdoutTask, stderrTask);
                await proc.WaitForExitAsync(linkedToken);

                var stderr = stderrTask.Result.Trim();
                if (!string.IsNullOrEmpty(stderr))
                {
                    _logger.LogInformation("yt-dlp stderr: {Stderr}", stderr);

                    // Members-only videos can't be fetched without a membership and error on every
                    // run (yt-dlp only learns the status during extraction, so a match-filter can't
                    // pre-skip them). Record their IDs in the archive so the next scheduled run skips
                    // them before extraction — no repeated error, no spurious "completed with errors".
                    if (!string.IsNullOrEmpty(archivePath))
                    {
                        foreach (System.Text.RegularExpressions.Match m in _membersOnlyRegex.Matches(stderr))
                        {
                            var id = m.Groups[1].Value;
                            if (!_archive.Contains(id))
                            {
                                _archive.AddProtected(id);
                                _logger.LogInformation("Archived members-only video {VideoId} so it is skipped on future runs.", id);
                            }
                        }
                    }
                }

                _logger.LogInformation("yt-dlp playlist download finished, exit code {Code}", proc.ExitCode);

                // Exit code 0 = full success. 101 = a "--break-on-*" stop (we no longer pass those
                // flags, but accept it for safety). Any other non-zero code means at least one video
                // failed; with --ignore-errors the rest still downloaded, so the caller treats this
                // as "completed with errors" rather than retrying the whole channel scan.
                return proc.ExitCode == 0 || proc.ExitCode == 101;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                _logger.LogWarning("yt-dlp playlist download timed out after 6 hours: {Url}", url);
                try { proc.Kill(entireProcessTree: true); } catch { }
                return false;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception while downloading playlist {Url}", url);
            return false;
        }
    }

    /// <summary>
    /// Fetches a channel's artwork URLs (square avatar/logo and wide banner) without listing any
    /// videos. Returns (null, null) on failure.
    /// </summary>
    public async Task<(string? Avatar, string? Banner)> FetchChannelArtworkAsync(string channelUrl, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(channelUrl))
            return (null, null);

        try
        {
            var config = Plugin.Instance!.Configuration;
            var psi = new ProcessStartInfo
            {
                FileName               = ResolveYtDlpBinary(),
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
                CreateNoWindow         = true,
            };
            psi.ArgumentList.Add("--playlist-items");
            psi.ArgumentList.Add("0"); // channel metadata only, don't enumerate videos
            psi.ArgumentList.Add("-J");
            if (!string.IsNullOrWhiteSpace(config.CookiesFilePath))
            {
                psi.ArgumentList.Add("--cookies");
                psi.ArgumentList.Add(config.CookiesFilePath);
            }
            psi.ArgumentList.Add("--");
            psi.ArgumentList.Add(channelUrl);

            using var proc = new Process { StartInfo = psi };
            proc.Start();
            var json = await proc.StandardOutput.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(ct);

            if (string.IsNullOrWhiteSpace(json))
                return (null, null);

            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("thumbnails", out var thumbs) || thumbs.ValueKind != JsonValueKind.Array)
                return (null, null);

            string? avatar = null, banner = null;
            foreach (var t in thumbs.EnumerateArray())
            {
                var id = t.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
                var url = t.TryGetProperty("url", out var urlEl) ? urlEl.GetString() : null;
                if (string.IsNullOrEmpty(url))
                    continue;
                if (string.Equals(id, "avatar_uncropped", StringComparison.OrdinalIgnoreCase))
                    avatar = url;
                else if (string.Equals(id, "banner_uncropped", StringComparison.OrdinalIgnoreCase))
                    banner = url;
            }

            return (avatar, banner);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not fetch channel artwork for {Url}", channelUrl);
            return (null, null);
        }
    }

    // -------------------------------------------------------------------------
    // Private helpers
    // -------------------------------------------------------------------------

    // Channel tabs that already target specific content — if the URL ends in one of these we leave
    // it alone (e.g. an explicit "/shorts" or "/streams" the user deliberately added).
    private static readonly string[] _channelTabs =
    {
        "videos", "shorts", "streams", "live", "playlists", "featured",
        "community", "about", "releases", "podcasts", "courses", "store"
    };

    /// <summary>
    /// If <paramref name="excludeShorts"/> is set and the URL is a bare YouTube channel root
    /// (e.g. "/@handle", "/channel/UC…", "/c/Name", "/user/Name") with no tab already specified,
    /// returns the URL pointing at the "/videos" tab so Shorts/Live are not enumerated. Playlist,
    /// watch, and already-tab-qualified URLs are returned unchanged.
    /// </summary>
    internal static string NormalizeChannelUrl(string url, bool excludeShorts)
    {
        if (!excludeShorts || string.IsNullOrWhiteSpace(url))
            return url;

        // Leave playlists, single videos, and explicit shorts URLs untouched.
        if (url.Contains("list=", StringComparison.OrdinalIgnoreCase) ||
            url.Contains("/watch", StringComparison.OrdinalIgnoreCase) ||
            url.Contains("/shorts/", StringComparison.OrdinalIgnoreCase))
            return url;

        var trimmed = url.TrimEnd('/');

        // Already pointing at a specific channel tab?
        var lastSegment = trimmed[(trimmed.LastIndexOf('/') + 1)..];
        var queryStart = lastSegment.IndexOf('?', StringComparison.Ordinal);
        if (queryStart >= 0)
            lastSegment = lastSegment[..queryStart];
        if (Array.Exists(_channelTabs, t => string.Equals(t, lastSegment, StringComparison.OrdinalIgnoreCase)))
            return url;

        // Bare channel root → append the Videos tab.
        if (trimmed.Contains("/@", StringComparison.Ordinal) ||
            trimmed.Contains("/channel/", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("/c/", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("/user/", StringComparison.OrdinalIgnoreCase))
            return trimmed + "/videos";

        return url;
    }

    private YoutubeDL CreateClient(string? outputDir = null)
    {
        var config = Plugin.Instance!.Configuration;

        return new YoutubeDL
        {
            YoutubeDLPath = ResolveYtDlpBinary(),
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
            // No WriteThumbnail: ThumbnailService downloads a "-thumb.jpg" itself; yt-dlp's .webp
            // would just be redundant clutter next to the video.
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

        // Use cookies file for authenticated downloads (e.g. age-restricted or PO Token required)
        if (!string.IsNullOrWhiteSpace(config.CookiesFilePath))
        {
            opts.Cookies = config.CookiesFilePath;
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
