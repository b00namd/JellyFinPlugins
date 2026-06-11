using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;
using Jellyfin.Plugin.JellyTube.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyTube.Services;

/// <summary>
/// Writes Kodi/Jellyfin-compatible <c>.nfo</c> metadata files for downloaded videos.
/// </summary>
public class NfoWriterService
{
    private readonly ILogger<NfoWriterService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="NfoWriterService"/> class.
    /// </summary>
    public NfoWriterService(ILogger<NfoWriterService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Writes an NFO file to <paramref name="nfoPath"/> using the provided <paramref name="meta"/>.
    /// </summary>
    public async Task WriteNfoAsync(VideoMetadata meta, string nfoPath)
    {
        try
        {
            var elements = new XElement("movie",
                new XElement("title", meta.Title),
                new XElement("originaltitle", meta.Title),
                new XElement("plot", meta.Description),
                new XElement("year", meta.UploadDate?.Year.ToString() ?? string.Empty),
                new XElement("premiered", meta.UploadDate?.ToString("yyyy-MM-dd") ?? string.Empty),
                new XElement("studio", meta.ChannelName),
                new XElement("runtime", ((int)((meta.DurationSeconds ?? 0) / 60)).ToString()),
                new XElement("uniqueid",
                    new XAttribute("type", "youtube"),
                    new XAttribute("default", "true"),
                    meta.VideoId),
                new XElement("source", meta.WebpageUrl)
            );

            // One <genre> per category
            foreach (var cat in meta.Categories)
            {
                elements.Add(new XElement("genre", cat));
            }

            // One <tag> per YouTube tag (limit to 20 to keep NFO reasonable)
            foreach (var tag in meta.Tags.Take(20))
            {
                elements.Add(new XElement("tag", tag));
            }

            // Thumbnail / poster
            if (!string.IsNullOrEmpty(meta.ThumbnailUrl))
            {
                elements.Add(new XElement("thumb",
                    new XAttribute("aspect", "poster"),
                    meta.ThumbnailUrl));

                elements.Add(new XElement("fanart",
                    new XElement("thumb", meta.ThumbnailUrl)));
            }

            var doc = new XDocument(elements);

            Directory.CreateDirectory(Path.GetDirectoryName(nfoPath)!);

            await using var writer = new StreamWriter(nfoPath, append: false, Encoding.UTF8);
            await writer.WriteLineAsync("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\" ?>");
            await writer.WriteAsync(doc.ToString());

            _logger.LogInformation("NFO written to {Path}", nfoPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to write NFO to {Path}", nfoPath);
        }
    }

    /// <summary>
    /// Writes an episode NFO (<c>episodedetails</c>) for series-organised downloads. Season is the
    /// upload year and episode the upload month/day (MMDD), so episodes sort chronologically.
    /// </summary>
    public async Task WriteEpisodeNfoAsync(VideoMetadata meta, string nfoPath)
    {
        try
        {
            var season = meta.UploadDate?.Year ?? 0;
            var episode = meta.UploadDate is { } d ? (d.Month * 100) + d.Day : 0;

            var root = new XElement("episodedetails",
                new XElement("title", meta.Title),
                new XElement("showtitle", meta.ChannelName),
                new XElement("season", season.ToString()),
                new XElement("episode", episode.ToString()),
                new XElement("aired", meta.UploadDate?.ToString("yyyy-MM-dd") ?? string.Empty),
                new XElement("premiered", meta.UploadDate?.ToString("yyyy-MM-dd") ?? string.Empty),
                new XElement("plot", meta.Description),
                new XElement("studio", meta.ChannelName),
                new XElement("runtime", ((int)((meta.DurationSeconds ?? 0) / 60)).ToString()),
                new XElement("uniqueid",
                    new XAttribute("type", "youtube"),
                    new XAttribute("default", "true"),
                    meta.VideoId),
                new XElement("source", meta.WebpageUrl));

            foreach (var cat in meta.Categories)
                root.Add(new XElement("genre", cat));
            foreach (var tag in meta.Tags.Take(20))
                root.Add(new XElement("tag", tag));
            if (!string.IsNullOrEmpty(meta.ThumbnailUrl))
                root.Add(new XElement("thumb", meta.ThumbnailUrl));

            await WriteDocumentAsync(new XDocument(root), nfoPath).ConfigureAwait(false);
            _logger.LogInformation("Episode NFO written to {Path}", nfoPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to write episode NFO to {Path}", nfoPath);
        }
    }

    /// <summary>
    /// Writes a <c>tvshow.nfo</c> for a channel-as-series folder, if one does not already exist.
    /// </summary>
    public async Task EnsureTvShowNfoAsync(string seriesDir, string channelName, string? channelId, string? thumbnailUrl)
    {
        try
        {
            var nfoPath = Path.Combine(seriesDir, "tvshow.nfo");
            if (File.Exists(nfoPath))
                return;

            var root = new XElement("tvshow",
                new XElement("title", channelName),
                new XElement("studio", channelName));

            if (!string.IsNullOrWhiteSpace(channelId))
                root.Add(new XElement("uniqueid",
                    new XAttribute("type", "youtube"),
                    new XAttribute("default", "true"),
                    channelId));

            if (!string.IsNullOrEmpty(thumbnailUrl))
            {
                root.Add(new XElement("thumb", new XAttribute("aspect", "poster"), thumbnailUrl));
                root.Add(new XElement("fanart", new XElement("thumb", thumbnailUrl)));
            }

            await WriteDocumentAsync(new XDocument(root), nfoPath).ConfigureAwait(false);
            _logger.LogInformation("tvshow.nfo written to {Path}", nfoPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to write tvshow.nfo in {Dir}", seriesDir);
        }
    }

    private static async Task WriteDocumentAsync(XDocument doc, string nfoPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(nfoPath)!);
        await using var writer = new StreamWriter(nfoPath, append: false, Encoding.UTF8);
        await writer.WriteLineAsync("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\" ?>");
        await writer.WriteAsync(doc.ToString());
    }
}
