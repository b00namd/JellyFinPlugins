using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyTubbing.Services;

/// <summary>
/// Resolves a YouTube video ID to direct stream URL(s) via yt-dlp.
/// For 1080p+, yt-dlp returns separate video and audio URLs (DASH).
/// Resolved URLs are cached for 4 hours so repeated playback starts instantly.
/// </summary>
public class StreamResolverService
{
    // YouTube CDN URLs are valid for ~6 hours; cache for 4 h to be safe
    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(4);

    // Value: (videoUrl, audioUrl or null for combined streams, expiry)
    private static readonly ConcurrentDictionary<string, (string Video, string? Audio, DateTime Expiry)> _cache = new();

    private readonly ILogger<StreamResolverService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="StreamResolverService"/> class.
    /// </summary>
    public StreamResolverService(ILogger<StreamResolverService> logger)
    {
        _logger = logger;
    }

    /// <summary>Resolves a YouTube video ID to a <see cref="MediaSourceInfo"/> via yt-dlp.</summary>
    public async Task<IEnumerable<MediaSourceInfo>> ResolveAsync(string videoId, CancellationToken ct)
    {
        var (videoUrl, _) = await ResolveUrlsAsync(videoId, ct);
        if (!string.IsNullOrEmpty(videoUrl))
        {
            _logger.LogInformation("Resolved stream for {VideoId}.", videoId);
            return new[] { BuildSource(videoId, videoUrl) };
        }

        _logger.LogWarning("Could not resolve stream for {VideoId}.", videoId);
        return Array.Empty<MediaSourceInfo>();
    }

    /// <summary>
    /// Returns cached or freshly resolved stream URLs.
    /// For combined streams: Audio is null. For DASH (1080p+): both Video and Audio are set.
    /// </summary>
    public async Task<(string? Video, string? Audio)> ResolveUrlsAsync(string videoId, CancellationToken ct)
    {
        if (_cache.TryGetValue(videoId, out var cached) && DateTime.UtcNow < cached.Expiry)
        {
            _logger.LogDebug("Cache hit for {VideoId}.", videoId);
            return (cached.Video, cached.Audio);
        }

        var (video, audio) = await ResolveViaYtDlpAsync(videoId, ct);

        if (!string.IsNullOrEmpty(video))
        {
            _cache[videoId] = (video, audio, DateTime.UtcNow.Add(CacheTtl));
            PruneCache();
        }

        return (video, audio);
    }

    // -------------------------------------------------------------------------

    private async Task<(string? Video, string? Audio)> ResolveViaYtDlpAsync(string videoId, CancellationToken ct)
    {
        var config = Plugin.Instance!.Configuration;
        var binary = string.IsNullOrWhiteSpace(config.YtDlpBinaryPath) ? "yt-dlp" : config.YtDlpBinaryPath;
        var height = ParseHeight(config.PreferredQuality ?? "720p");

        // Try combined (muxed) stream first – one URL, no ffmpeg, seeking works natively.
        // Fall back to DASH (two URLs) → HlsTranscodeService merges with ffmpeg for seeking.
        // The "best[height<=N]" selector picks the best combined format up to N pixels tall
        // (YouTube offers combined up to ~360p; DASH covers 720p/1080p/4K).
        var format = $"best[height<={height}]/bestvideo[height<={height}]+bestaudio/best";

        var ytUrl = $"https://www.youtube.com/watch?v={videoId}";

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
            psi.ArgumentList.Add("-g");
            psi.ArgumentList.Add("--no-warnings");
            psi.ArgumentList.Add("--no-playlist");
            psi.ArgumentList.Add("--format");
            psi.ArgumentList.Add(format);
            psi.ArgumentList.Add("--");
            psi.ArgumentList.Add(ytUrl);

            using var proc = new Process { StartInfo = psi };
            proc.Start();

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(30));

            var stdoutTask = proc.StandardOutput.ReadToEndAsync(cts.Token);
            var stderrTask = proc.StandardError.ReadToEndAsync(cts.Token);
            await Task.WhenAll(stdoutTask, stderrTask);
            await proc.WaitForExitAsync(cts.Token);

            var lines = stdoutTask.Result
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(l => l.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                .ToArray();

            if (lines.Length == 0)
            {
                var stderr = stderrTask.Result.Trim();
                _logger.LogWarning("yt-dlp returned no URLs for {VideoId}. stderr: {Stderr}", videoId, stderr);
                return (null, null);
            }

            // Two lines = DASH (video URL + audio URL), one line = combined
            return lines.Length >= 2
                ? (lines[0], lines[1])
                : (lines[0], null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "yt-dlp stream resolution failed for {VideoId}", videoId);
            return (null, null);
        }
    }

    private static void PruneCache()
    {
        var now = DateTime.UtcNow;
        foreach (var key in _cache.Keys)
        {
            if (_cache.TryGetValue(key, out var entry) && now >= entry.Expiry)
                _cache.TryRemove(key, out _);
        }
    }

    private static MediaSourceInfo BuildSource(string videoId, string url) => new()
    {
        Id                   = videoId,
        Name                 = "yt-dlp",
        Path                 = url,
        Protocol             = MediaProtocol.Http,
        IsRemote             = true,
        Container            = "mp4",
        SupportsDirectPlay   = true,
        SupportsDirectStream = true,
        SupportsTranscoding  = true,
        RequiresOpening      = false,
        RequiresClosing      = false,
        IsInfiniteStream     = false,
        Type                 = MediaSourceType.Default,
    };

    private static int ParseHeight(string q) =>
        int.TryParse(q.TrimEnd('p', 'P'), out var h) ? h : 720;
}
