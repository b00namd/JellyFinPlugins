using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyTubbing.Services;

/// <summary>
/// Resolves a YouTube video ID to a direct stream URL via yt-dlp.
/// Resolved URLs are cached for 4 hours so repeated playback starts instantly.
/// </summary>
public class StreamResolverService
{
    // YouTube CDN URLs are valid for ~6 hours; cache for 4 h to be safe
    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(4);

    private static readonly ConcurrentDictionary<string, (string Url, DateTime Expiry)> _cache = new();

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
        var streamUrl = await ResolveUrlAsync(videoId, ct);
        if (!string.IsNullOrEmpty(streamUrl))
        {
            _logger.LogInformation("Resolved stream for {VideoId}.", videoId);
            return new[] { BuildSource(videoId, streamUrl) };
        }

        _logger.LogWarning("Could not resolve stream for {VideoId}.", videoId);
        return Array.Empty<MediaSourceInfo>();
    }

    /// <summary>
    /// Returns a cached or freshly resolved direct stream URL for the given video ID.
    /// </summary>
    public async Task<string?> ResolveUrlAsync(string videoId, CancellationToken ct)
    {
        // Serve from cache if still valid
        if (_cache.TryGetValue(videoId, out var cached) && DateTime.UtcNow < cached.Expiry)
        {
            _logger.LogDebug("Cache hit for {VideoId}.", videoId);
            return cached.Url;
        }

        var url = await ResolveViaYtDlpAsync(videoId, ct);

        if (!string.IsNullOrEmpty(url))
        {
            _cache[videoId] = (url, DateTime.UtcNow.Add(CacheTtl));
            PruneCache();
        }

        return url;
    }

    // -------------------------------------------------------------------------

    private async Task<string?> ResolveViaYtDlpAsync(string videoId, CancellationToken ct)
    {
        var config = Plugin.Instance!.Configuration;
        var binary = string.IsNullOrWhiteSpace(config.YtDlpBinaryPath) ? "yt-dlp" : config.YtDlpBinaryPath;
        var height = ParseHeight(config.PreferredQuality ?? "720p");

        // Select the best combined (muxed) stream that contains both video and audio.
        // YouTube only offers combined streams up to ~720p; higher resolutions use
        // separate DASH streams (video+audio) which cannot be served via a single redirect.
        // Fallback chain: mp4 with audio → any container with audio → best available.
        var format = $"best[height<={height}][vcodec!=none][acodec!=none][ext=mp4]" +
                     $"/best[height<={height}][vcodec!=none][acodec!=none]" +
                     $"/best[vcodec!=none][acodec!=none][ext=mp4]" +
                     $"/best[vcodec!=none][acodec!=none]" +
                     $"/best";

        var ytUrl = $"https://www.youtube.com/watch?v={videoId}";

        try
        {
            using var proc = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName  = binary,
                    Arguments = $"-g --no-warnings --no-playlist" +
                                $" --format \"{format}\" -- {ytUrl}",
                    RedirectStandardOutput = true,
                    RedirectStandardError  = true,
                    UseShellExecute        = false,
                    CreateNoWindow         = true,
                }
            };
            proc.Start();

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(30));

            var output = await proc.StandardOutput.ReadToEndAsync(cts.Token);
            await proc.WaitForExitAsync(cts.Token);

            // yt-dlp -g returns one URL per line; take the first (video stream)
            var url = output.Split('\n')[0].Trim();
            return string.IsNullOrEmpty(url) ? null : url;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "yt-dlp stream resolution failed for {VideoId}", videoId);
            return null;
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
