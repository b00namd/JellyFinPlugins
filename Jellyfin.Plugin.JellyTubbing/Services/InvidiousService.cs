using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyTubbing.Models;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Model.Channels;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyTubbing.Services;

/// <summary>
/// Communicates with an Invidious instance to search videos, fetch trending and resolve stream URLs.
/// </summary>
public class InvidiousService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<InvidiousService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="InvidiousService"/> class.
    /// </summary>
    public InvidiousService(IHttpClientFactory httpClientFactory, ILogger<InvidiousService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    private string BaseUrl =>
        (Plugin.Instance?.Configuration.InvidiousInstanceUrl ?? string.Empty).TrimEnd('/');

    /// <summary>Checks whether the configured Invidious instance is reachable.</summary>
    public async Task<bool> IsReachableAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(BaseUrl))
            return false;

        try
        {
            var client = _httpClientFactory.CreateClient("jellytubbing");
            client.Timeout = TimeSpan.FromSeconds(10);
            // /api/v1/stats is disabled on many instances – use trending instead
            var resp = await client.GetAsync(BaseUrl + "/api/v1/trending?fields=videoId&limit=1", ct);
            return resp.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Searches YouTube via Invidious and returns channel items.</summary>
    public async Task<List<ChannelItemInfo>> SearchAsync(string query, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(BaseUrl))
            return new List<ChannelItemInfo>();

        try
        {
            var url = $"{BaseUrl}/api/v1/search?q={Uri.EscapeDataString(query)}&type=video" +
                      "&fields=videoId,title,author,published,lengthSeconds,description,videoThumbnails";
            var client = _httpClientFactory.CreateClient("jellytubbing");
            var items = await client.GetFromJsonAsync<InvidiousVideoItem[]>(url, ct);
            return items is null ? new List<ChannelItemInfo>() : items.Select(MapToChannelItem).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Invidious search failed for query: {Query}", query);
            return new List<ChannelItemInfo>();
        }
    }

    /// <summary>Returns trending videos for the configured region.</summary>
    public async Task<List<ChannelItemInfo>> GetTrendingAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(BaseUrl))
            return new List<ChannelItemInfo>();

        var region = Plugin.Instance?.Configuration.TrendingRegion ?? "DE";
        try
        {
            var url = $"{BaseUrl}/api/v1/trending?region={Uri.EscapeDataString(region)}" +
                      "&fields=videoId,title,author,published,lengthSeconds,description,videoThumbnails";
            var client = _httpClientFactory.CreateClient("jellytubbing");
            var items = await client.GetFromJsonAsync<InvidiousVideoItem[]>(url, ct);
            return items is null ? new List<ChannelItemInfo>() : items.Select(MapToChannelItem).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Invidious trending fetch failed.");
            return new List<ChannelItemInfo>();
        }
    }

    /// <summary>
    /// Fetches the best progressive MP4 stream URL for a video from Invidious.
    /// Returns <c>null</c> if unavailable.
    /// </summary>
    public async Task<string?> GetBestStreamUrlAsync(string videoId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(BaseUrl))
            return null;

        var preferredHeight = ParseHeight(Plugin.Instance?.Configuration.PreferredQuality ?? "720p");
        try
        {
            var url = $"{BaseUrl}/api/v1/videos/{videoId}?fields=formatStreams";
            var client = _httpClientFactory.CreateClient("jellytubbing");
            var detail = await client.GetFromJsonAsync<InvidiousVideoDetail>(url, ct);
            if (detail?.FormatStreams is null || detail.FormatStreams.Length == 0)
                return null;

            // Progressive MP4 streams, sorted best-first
            var mp4 = detail.FormatStreams
                .Where(s => string.Equals(s.Container, "mp4", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(s => ParseHeight(s.QualityLabel))
                .ToArray();

            if (mp4.Length == 0)
                return null;

            // Best stream at or below preferred quality
            var best = mp4.FirstOrDefault(s => ParseHeight(s.QualityLabel) <= preferredHeight)
                       ?? mp4.Last();

            return best.Url;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Invidious video detail fetch failed for {VideoId}", videoId);
            return null;
        }
    }

    // -------------------------------------------------------------------------

    private static int ParseHeight(string qualityLabel)
    {
        var s = qualityLabel.TrimEnd('p', 'P', 'k', 'K');
        return int.TryParse(s, out var h) ? h : 0;
    }

    private static ChannelItemInfo MapToChannelItem(InvidiousVideoItem v)
    {
        var thumb = v.VideoThumbnails
            .OrderByDescending(t => t.Width)
            .FirstOrDefault();

        return new ChannelItemInfo
        {
            Id          = "YT:" + v.VideoId,
            Name        = v.Title,
            Overview    = v.Description,
            Type        = ChannelItemType.Media,
            ContentType = ChannelMediaContentType.Movie,
            MediaType   = ChannelMediaType.Video,
            ImageUrl    = thumb?.Url,
            PremiereDate = v.Published > 0
                ? DateTimeOffset.FromUnixTimeSeconds(v.Published).DateTime
                : null,
            RunTimeTicks = v.LengthSeconds > 0
                ? TimeSpan.FromSeconds(v.LengthSeconds).Ticks
                : null,
        };
    }
}
