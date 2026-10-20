using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyTubbing.Services;

/// <summary>
/// Downloads a YouTube video completely into a local cache the first time it is played and serves
/// it from there afterwards. A finished file supports HTTP range requests, so seeking and resuming
/// work in every client. Files not played for <c>CacheRetentionHours</c> are deleted again.
/// </summary>
public partial class VideoCacheService
{
    // Bounds a single download (long videos at 1080p over a slow line).
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(45);

    // Files touched less recently than this get their timestamp refreshed when served.
    private static readonly TimeSpan TouchInterval = TimeSpan.FromMinutes(10);

    private readonly ConcurrentDictionary<string, Lazy<Task<string?>>> _downloads = new();

    // At most two downloads at once, so scanning clients can't flood YouTube or the disk.
    private readonly SemaphoreSlim _downloadSlots = new(2, 2);

    private readonly ILogger<VideoCacheService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="VideoCacheService"/> class.
    /// </summary>
    public VideoCacheService(ILogger<VideoCacheService> logger)
    {
        _logger = logger;
    }

    /// <summary>Returns true when the string looks like a YouTube video ID (11 URL-safe characters).</summary>
    public static bool IsValidVideoId(string videoId) => VideoIdRegex().IsMatch(videoId);

    /// <summary>
    /// Returns the path of the cached video, downloading it first if needed.
    /// Concurrent requests for the same video share one download. Cancelling <paramref name="ct"/>
    /// only stops waiting; the download itself continues so the next request finds the file.
    /// </summary>
    public async Task<string?> GetOrDownloadAsync(string videoId, CancellationToken ct)
    {
        if (!IsValidVideoId(videoId))
        {
            return null;
        }

        var path = CachedFilePath(videoId);
        if (File.Exists(path))
        {
            Touch(path);
            return path;
        }

        PruneCache();

        var download = _downloads.GetOrAdd(
            videoId,
            id => new Lazy<Task<string?>>(() => DownloadAsync(id)));

        return await download.Value.WaitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Deletes a cached video, e.g. after its STRM file was removed as watched.</summary>
    public void Delete(string videoId)
    {
        if (!IsValidVideoId(videoId))
        {
            return;
        }

        var path = CachedFilePath(videoId);
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
                _logger.LogInformation("Deleted cached video {Path}.", path);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not delete cached video {Path}.", path);
        }
    }

    /// <summary>
    /// Resolves the yt-dlp binary: configured path, then the copy JellyTube keeps up to date in the
    /// plugin configuration folder, then yt-dlp from PATH.
    /// </summary>
    public static string ResolveYtDlpBinary()
    {
        var plugin = Plugin.Instance;
        if (!string.IsNullOrWhiteSpace(plugin?.Configuration.YtDlpBinaryPath))
        {
            return plugin.Configuration.YtDlpBinaryPath;
        }

        if (plugin is not null)
        {
            var managed = Path.Combine(
                plugin.Paths.PluginConfigurationsPath,
                OperatingSystem.IsWindows() ? "yt-dlp.exe" : "yt-dlp");
            if (File.Exists(managed))
            {
                return managed;
            }
        }

        return "yt-dlp";
    }

    // -------------------------------------------------------------------------

    private static string CacheDirectory
    {
        get
        {
            var plugin = Plugin.Instance!;
            return string.IsNullOrWhiteSpace(plugin.Configuration.CachePath)
                ? Path.Combine(plugin.Paths.CachePath, "jellytubbing")
                : plugin.Configuration.CachePath;
        }
    }

    private static string CachedFilePath(string videoId) => Path.Combine(CacheDirectory, videoId + ".mp4");

    private async Task<string?> DownloadAsync(string videoId)
    {
        try
        {
            await _downloadSlots.WaitAsync().ConfigureAwait(false);
            try
            {
                return await RunYtDlpAsync(videoId).ConfigureAwait(false);
            }
            finally
            {
                _downloadSlots.Release();
            }
        }
        finally
        {
            _downloads.TryRemove(videoId, out _);
        }
    }

    private async Task<string?> RunYtDlpAsync(string videoId)
    {
        var config = Plugin.Instance!.Configuration;
        var finalPath = CachedFilePath(videoId);
        var tempDir = Path.Combine(CacheDirectory, "tmp", videoId);
        Directory.CreateDirectory(tempDir);

        // H.264 + AAC up to 1080p plays everywhere without transcoding. YouTube has no H.264 above
        // 1080p; the VP9/AV1 fallbacks only kick in for videos without any H.264 format.
        var height = ParseHeight(config.PreferredQuality);
        var format = $"bv*[height<={height}][vcodec^=avc1]+ba[ext=m4a]" +
                     $"/b[height<={height}][vcodec^=avc1]" +
                     $"/bv*[height<={height}]+ba" +
                     $"/b[height<={height}]/b";

        var psi = new ProcessStartInfo
        {
            FileName               = ResolveYtDlpBinary(),
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true,
        };
        psi.ArgumentList.Add("--no-playlist");
        psi.ArgumentList.Add("--no-warnings");
        psi.ArgumentList.Add("--format");
        psi.ArgumentList.Add(format);
        psi.ArgumentList.Add("--merge-output-format");
        psi.ArgumentList.Add("mp4");
        // moov atom at the front, so players can start without fetching the end of the file first
        psi.ArgumentList.Add("--postprocessor-args");
        psi.ArgumentList.Add("Merger+ffmpeg_o:-movflags +faststart");
        if (!string.IsNullOrWhiteSpace(config.FfmpegBinaryPath))
        {
            psi.ArgumentList.Add("--ffmpeg-location");
            psi.ArgumentList.Add(config.FfmpegBinaryPath);
        }

        psi.ArgumentList.Add("--output");
        psi.ArgumentList.Add(Path.Combine(tempDir, "video.%(ext)s"));
        psi.ArgumentList.Add("--print");
        psi.ArgumentList.Add("after_move:filepath");
        psi.ArgumentList.Add("--");
        psi.ArgumentList.Add($"https://www.youtube.com/watch?v={videoId}");

        _logger.LogInformation("Caching video {VideoId} ({Height}p max)...", videoId, height);
        var started = DateTime.UtcNow;

        try
        {
            using var proc = new Process { StartInfo = psi };
            proc.Start();

            using var cts = new CancellationTokenSource(DownloadTimeout);
            try
            {
                var stdoutTask = proc.StandardOutput.ReadToEndAsync(cts.Token);
                var stderrTask = proc.StandardError.ReadToEndAsync(cts.Token);
                await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
                await proc.WaitForExitAsync(cts.Token).ConfigureAwait(false);

                var downloaded = stdoutTask.Result
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .LastOrDefault(File.Exists);

                if (proc.ExitCode != 0 || downloaded is null)
                {
                    _logger.LogWarning(
                        "yt-dlp could not cache {VideoId} (exit code {Code}): {Stderr}",
                        videoId, proc.ExitCode, stderrTask.Result.Trim());
                    return null;
                }

                File.Move(downloaded, finalPath, overwrite: true);
                _logger.LogInformation(
                    "Cached video {VideoId} in {Seconds:F0} s ({SizeMb:F0} MB).",
                    videoId, (DateTime.UtcNow - started).TotalSeconds, new FileInfo(finalPath).Length / 1048576.0);
                return finalPath;
            }
            catch (OperationCanceledException)
            {
                try { proc.Kill(entireProcessTree: true); } catch { /* already exited */ }
                _logger.LogWarning("Caching {VideoId} timed out after {Minutes} min.", videoId, DownloadTimeout.TotalMinutes);
                return null;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "yt-dlp failed for {VideoId}.", videoId);
            return null;
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { /* ignored */ }
        }
    }

    private void Touch(string path)
    {
        try
        {
            if (DateTime.UtcNow - File.GetLastWriteTimeUtc(path) > TouchInterval)
            {
                File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not refresh timestamp of {Path}.", path);
        }
    }

    private void PruneCache()
    {
        var dir = CacheDirectory;
        if (!Directory.Exists(dir))
        {
            return;
        }

        var retention = TimeSpan.FromHours(Math.Max(1, Plugin.Instance!.Configuration.CacheRetentionHours));
        var cutoff = DateTime.UtcNow - retention;

        foreach (var file in Directory.EnumerateFiles(dir, "*.mp4"))
        {
            if (File.GetLastWriteTimeUtc(file) >= cutoff)
            {
                continue;
            }

            try
            {
                File.Delete(file);
                _logger.LogInformation("Removed {File} from the cache (not played for {Hours} h).", Path.GetFileName(file), retention.TotalHours);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not remove {File} from the cache.", file);
            }
        }

        // Leftovers of downloads interrupted by a server restart
        var tmp = Path.Combine(dir, "tmp");
        if (Directory.Exists(tmp))
        {
            foreach (var sub in Directory.EnumerateDirectories(tmp))
            {
                if (!_downloads.ContainsKey(Path.GetFileName(sub)) && Directory.GetLastWriteTimeUtc(sub) < DateTime.UtcNow - DownloadTimeout)
                {
                    try { Directory.Delete(sub, recursive: true); } catch { /* ignored */ }
                }
            }
        }
    }

    // Capped at 1080p: above that YouTube only offers VP9/AV1, which most clients can't direct-play.
    private static int ParseHeight(string? quality) =>
        int.TryParse(quality?.TrimEnd('p', 'P'), out var h) && h > 0 ? Math.Min(h, 1080) : 720;

    [GeneratedRegex("^[A-Za-z0-9_-]{11}$")]
    private static partial Regex VideoIdRegex();
}
