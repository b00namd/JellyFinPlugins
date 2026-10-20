using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyTubbing.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyTubbing.Services;

/// <summary>
/// Creates .strm, .nfo and thumbnail files for YouTube videos in the configured output folder.
/// </summary>
public class StrmService
{
    private readonly IHttpClientFactory _http;
    private readonly ILogger<StrmService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="StrmService"/> class.
    /// </summary>
    public StrmService(IHttpClientFactory http, ILogger<StrmService> logger)
    {
        _http   = http;
        _logger = logger;
    }

    /// <summary>
    /// Creates .strm / .nfo / thumbnail files for the channel's videos. Skips videos that already
    /// have a STRM file in the channel folder (matched by video ID, so renamed titles don't create
    /// duplicates) and videos in <paramref name="deletedIds"/>. Returns the number of new videos.
    /// </summary>
    public async Task<int> SyncChannelAsync(
        string channelName,
        string channelId,
        IReadOnlyList<(string VideoId, YouTubeVideoSnippet Snippet, TimeSpan Duration)> videos,
        ISet<string> deletedIds,
        CancellationToken ct)
    {
        var config = Plugin.Instance?.Configuration;
        if (config is null || string.IsNullOrWhiteSpace(config.StrmOutputPath))
        {
            _logger.LogWarning("STRM output path not configured – skipping {Channel}", channelName);
            return 0;
        }

        var channelDir = Path.Combine(config.StrmOutputPath, SanitizeName(channelName));
        Directory.CreateDirectory(channelDir);

        var existingIds = ReadExistingVideoIds(channelDir);
        var serverUrl   = (config.JellyfinServerUrl ?? "http://localhost:8096").TrimEnd('/');
        var created     = 0;

        foreach (var (videoId, snippet, duration) in videos)
        {
            ct.ThrowIfCancellationRequested();
            if (existingIds.Contains(videoId) || deletedIds.Contains(videoId))
                continue;

            var year     = DateTime.TryParse(snippet.PublishedAt, out var dt) ? dt.Year.ToString() : string.Empty;
            var safeName = SanitizeName(snippet.Title);
            // The ID keeps names unique when a channel reuses a title (e.g. "Livestream").
            var baseName = string.IsNullOrEmpty(year) ? $"{safeName} [{videoId}]" : $"{safeName} ({year}) [{videoId}]";

            await File.WriteAllTextAsync(
                Path.Combine(channelDir, baseName + ".nfo"),
                BuildNfo(snippet.Title, snippet.Description, snippet.PublishedAt, videoId, channelName, channelId,
                         snippet.Thumbnails.BestUrl, (int)duration.TotalSeconds),
                Encoding.UTF8, ct);

            var thumbnailUrl = snippet.Thumbnails.BestUrl;
            if (!string.IsNullOrEmpty(thumbnailUrl))
            {
                try
                {
                    var client = _http.CreateClient("jellytubbing");
                    var bytes  = await client.GetByteArrayAsync(thumbnailUrl, ct);
                    await File.WriteAllBytesAsync(Path.Combine(channelDir, baseName + "-thumb.jpg"), bytes, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Thumbnail download failed for {VideoId}", videoId);
                }
            }

            // STRM last, so Jellyfin never picks up a video before its NFO and thumbnail exist
            await File.WriteAllTextAsync(
                Path.Combine(channelDir, baseName + ".strm"),
                $"{serverUrl}/api/jellytubbing/stream/{videoId}",
                Encoding.UTF8, ct);

            existingIds.Add(videoId);
            created++;
        }

        return created;
    }

    /// <summary>Extracts the video ID from the content of a STRM file written by this plugin.</summary>
    public static string? VideoIdFromStrm(string strmContent)
    {
        const string Marker = "/api/jellytubbing/stream/";
        var idx = strmContent.LastIndexOf(Marker, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return null;
        var id = strmContent[(idx + Marker.Length)..].Trim();
        return VideoCacheService.IsValidVideoId(id) ? id : null;
    }

    // -------------------------------------------------------------------------

    private HashSet<string> ReadExistingVideoIds(string channelDir)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var strm in Directory.EnumerateFiles(channelDir, "*.strm"))
        {
            try
            {
                var id = VideoIdFromStrm(File.ReadAllText(strm));
                if (id is not null) ids.Add(id);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not read {Path}", strm);
            }
        }

        return ids;
    }

    private static string BuildNfo(
        string title, string description, string publishedAt, string videoId,
        string channelName, string channelId, string thumbnailUrl, int durationSeconds)
    {
        var hasDate  = DateTime.TryParse(publishedAt, out var dt);
        var year     = hasDate ? dt.Year.ToString() : string.Empty;
        var premiered = hasDate ? dt.ToString("yyyy-MM-dd") : string.Empty;
        var runtime  = durationSeconds > 0 ? ((int)(durationSeconds / 60.0 + 0.5)).ToString() : string.Empty;
        var ytUrl    = $"https://www.youtube.com/watch?v={videoId}";
        var chanUrl  = string.IsNullOrEmpty(channelId) ? string.Empty
                       : $"https://www.youtube.com/channel/{channelId}";

        var thumb = string.IsNullOrEmpty(thumbnailUrl)
            ? string.Empty
            : "\n  <thumb aspect=\"poster\">" + Xml(thumbnailUrl) + "</thumb>" +
              "\n  <fanart>\n    <thumb>" + Xml(thumbnailUrl) + "</thumb>\n  </fanart>";

        return $"""
<?xml version="1.0" encoding="UTF-8" standalone="yes" ?>
<movie>
  <title>{Xml(title)}</title>
  <originaltitle>{Xml(title)}</originaltitle>
  <plot>{Xml(description)}</plot>
  <year>{year}</year>
  <premiered>{premiered}</premiered>
  <studio>{Xml(channelName)}</studio>
  <runtime>{runtime}</runtime>
  <uniqueid type="youtube" default="true">{videoId}</uniqueid>
  <source>{Xml(ytUrl)}</source>
  <tag>{Xml(chanUrl)}</tag>{thumb}
</movie>
""";
    }

    private static string SanitizeName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(name.Length);
        foreach (var c in name)
            sb.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
        return sb.ToString().Trim().TrimEnd('.');
    }

    private static string Xml(string s) =>
        s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
}
