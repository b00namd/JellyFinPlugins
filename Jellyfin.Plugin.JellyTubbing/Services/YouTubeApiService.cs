using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyTubbing.Models;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Model.Channels;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyTubbing.Services;

/// <summary>
/// Wraps the YouTube Data API v3 for trending, subscriptions and channel video listing.
/// </summary>
public class YouTubeApiService
{
    private const string Base = "https://www.googleapis.com/youtube/v3";

    private readonly IHttpClientFactory _http;
    private readonly OAuthService _oauth;
    private readonly ILogger<YouTubeApiService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="YouTubeApiService"/> class.
    /// </summary>
    public YouTubeApiService(IHttpClientFactory http, OAuthService oauth, ILogger<YouTubeApiService> logger)
    {
        _http   = http;
        _oauth  = oauth;
        _logger = logger;
    }

    private string ApiKey => Plugin.Instance?.Configuration.YouTubeApiKey ?? string.Empty;

    // -----------------------------------------------------------------------
    // Trending (public – API key only)
    // -----------------------------------------------------------------------

    /// <summary>
    /// Returns trending videos for the configured region.
    /// Pass a YouTube video category ID (e.g. "10" for Music) or null for all categories.
    /// </summary>
    public async Task<List<ChannelItemInfo>> GetTrendingAsync(string? categoryId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(ApiKey))
        {
            _logger.LogWarning("YouTube API key not configured – cannot fetch trending.");
            return new();
        }

        var region = Plugin.Instance?.Configuration.TrendingRegion ?? "DE";
        var url = $"{Base}/videos?part=snippet,contentDetails&chart=mostPopular" +
                  $"&regionCode={Uri.EscapeDataString(region)}&maxResults=50&key={Uri.EscapeDataString(ApiKey)}";
        if (!string.IsNullOrEmpty(categoryId))
            url += $"&videoCategoryId={categoryId}";

        try
        {
            var client = _http.CreateClient("jellytubbing");
            var resp = await client.GetFromJsonAsync<YouTubeVideoListResponse>(url, ct);
            return resp?.Items.Select(MapVideoToChannelItem).ToList() ?? new();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch trending videos (category={Category})", categoryId);
            return new();
        }
    }

    // -----------------------------------------------------------------------
    // Subscriptions (OAuth required)
    // -----------------------------------------------------------------------

    /// <summary>Returns all subscriptions of the authenticated user (paginates automatically).</summary>
    public async Task<List<YouTubeSubscriptionItem>> GetSubscriptionsAsync(CancellationToken ct)
    {
        var token = await _oauth.GetValidAccessTokenAsync(ct);
        if (string.IsNullOrEmpty(token))
        {
            _logger.LogWarning("No valid OAuth token – cannot fetch subscriptions.");
            return new();
        }

        var result = new List<YouTubeSubscriptionItem>();
        string? pageToken = null;

        do
        {
            var url = $"{Base}/subscriptions?part=snippet&mine=true&maxResults=50&order=alphabetical";
            if (pageToken is not null) url += $"&pageToken={Uri.EscapeDataString(pageToken)}";

            try
            {
                var client = _http.CreateClient("jellytubbing");
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
                var resp = await client.GetFromJsonAsync<YouTubeSubscriptionListResponse>(url, ct);
                if (resp?.Items is null) break;
                result.AddRange(resp.Items);
                pageToken = resp.NextPageToken;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to fetch subscriptions page");
                break;
            }
        }
        while (pageToken is not null);

        return result;
    }

    // -----------------------------------------------------------------------
    // Channel videos (public – API key only)
    // -----------------------------------------------------------------------

    /// <summary>Returns the most recent videos from a YouTube channel, optionally excluding Shorts.</summary>
    public async Task<List<(string VideoId, YouTubeVideoSnippet Snippet, TimeSpan Duration)>> GetChannelVideosAsync(
        string channelId, int maxResults, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(ApiKey)) return new();

        var includeShorts = Plugin.Instance?.Configuration.IncludeShorts ?? false;
        var result = new List<(string, YouTubeVideoSnippet, TimeSpan)>();
        string? pageToken = null;
        var remaining = maxResults;

        do
        {
            var batch = Math.Min(remaining, 50);
            var url = $"{Base}/search?part=snippet&channelId={Uri.EscapeDataString(channelId)}" +
                      $"&type=video&order=date&maxResults={batch}&key={Uri.EscapeDataString(ApiKey)}";
            if (pageToken is not null) url += $"&pageToken={Uri.EscapeDataString(pageToken)}";

            try
            {
                var client = _http.CreateClient("jellytubbing");
                var resp = await client.GetFromJsonAsync<YouTubeSearchListResponse>(url, ct);
                if (resp?.Items is null) break;

                // Always fetch contentDetails for duration (NFO metadata + Shorts filter)
                var ids = string.Join(",", resp.Items.Select(i => Uri.EscapeDataString(i.Id.VideoId)));
                var durations = new Dictionary<string, TimeSpan>();
                var detailUrl = $"{Base}/videos?part=contentDetails&id={ids}&key={Uri.EscapeDataString(ApiKey)}";
                var details = await client.GetFromJsonAsync<YouTubeVideoListResponse>(detailUrl, ct);
                if (details?.Items is not null)
                {
                    foreach (var v in details.Items)
                    {
                        if (!string.IsNullOrEmpty(v.ContentDetails?.Duration))
                            durations[v.Id] = ParseDuration(v.ContentDetails.Duration);
                    }
                }

                foreach (var item in resp.Items)
                {
                    var dur = durations.TryGetValue(item.Id.VideoId, out var d) ? d : TimeSpan.Zero;
                    if (!includeShorts && dur.TotalSeconds > 0 && dur.TotalSeconds <= 60)
                        continue;
                    result.Add((item.Id.VideoId, item.Snippet, dur));
                }

                pageToken  = resp.NextPageToken;
                remaining -= resp.Items.Length;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to fetch videos for channel {ChannelId}", channelId);
                break;
            }
        }
        while (pageToken is not null && remaining > 0);

        return result;
    }

    private static TimeSpan ParseDuration(string iso)
    {
        try { return System.Xml.XmlConvert.ToTimeSpan(iso); }
        catch { return TimeSpan.Zero; }
    }

    // -----------------------------------------------------------------------

    private static ChannelItemInfo MapVideoToChannelItem(YouTubeVideoItem v) => new()
    {
        Id           = "YT:" + v.Id,
        Name         = v.Snippet.Title,
        Overview     = v.Snippet.Description,
        Type         = ChannelItemType.Media,
        ContentType  = ChannelMediaContentType.Movie,
        MediaType    = ChannelMediaType.Video,
        ImageUrl     = v.Snippet.Thumbnails.BestUrl,
        PremiereDate = DateTime.TryParse(v.Snippet.PublishedAt, out var dt) ? dt : null,
        RunTimeTicks = ParseIsoDuration(v.ContentDetails?.Duration),
    };

    private static long? ParseIsoDuration(string? iso)
    {
        if (string.IsNullOrEmpty(iso)) return null;
        try { return (long)System.Xml.XmlConvert.ToTimeSpan(iso).Ticks; }
        catch { return null; }
    }
}
