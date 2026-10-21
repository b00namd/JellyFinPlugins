using System;
using System.Collections.Concurrent;
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

    private static readonly TimeSpan TrendingCacheTime = TimeSpan.FromMinutes(30);

    private readonly ConcurrentDictionary<string, (DateTime Fetched, List<ChannelItemInfo> Items)> _trendingCache = new();

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

        // The channel overview fetches every category for its folder images, so keep results a while
        var cacheKey = $"{region}:{categoryId}";
        if (_trendingCache.TryGetValue(cacheKey, out var cached) && DateTime.UtcNow - cached.Fetched < TrendingCacheTime)
            return cached.Items;

        var url = $"{Base}/videos?part=snippet,contentDetails&chart=mostPopular" +
                  $"&regionCode={Uri.EscapeDataString(region)}&maxResults=50&key={Uri.EscapeDataString(ApiKey)}";
        if (!string.IsNullOrEmpty(categoryId))
            url += $"&videoCategoryId={categoryId}";

        try
        {
            var client = _http.CreateClient("jellytubbing");
            var resp = await client.GetFromJsonAsync<YouTubeVideoListResponse>(url, ct);
            var items = resp?.Items.Select(MapVideoToChannelItem).ToList() ?? new();
            if (items.Count > 0)
                _trendingCache[cacheKey] = (DateTime.UtcNow, items);
            return items;
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

    /// <summary>
    /// Returns the channel's name and its most recent videos, optionally excluding Shorts.
    /// Reads the channel's uploads playlist (1 quota unit per page) instead of search.list
    /// (100 units, and its date ordering skips videos). The name is null if the channel lookup failed.
    /// </summary>
    public async Task<(string? ChannelTitle, List<(string VideoId, YouTubeVideoSnippet Snippet, TimeSpan Duration)> Videos)> GetChannelVideosAsync(
        string channelId, int maxResults, CancellationToken ct)
    {
        var result = new List<(string, YouTubeVideoSnippet, TimeSpan)>();
        if (string.IsNullOrWhiteSpace(ApiKey)) return (null, result);

        var includeShorts = Plugin.Instance?.Configuration.IncludeShorts ?? false;
        var key = Uri.EscapeDataString(ApiKey);
        var client = _http.CreateClient("jellytubbing");

        try
        {
            var channels = await client.GetFromJsonAsync<YouTubeChannelListResponse>(
                $"{Base}/channels?part=snippet,contentDetails&id={Uri.EscapeDataString(channelId)}&key={key}", ct);
            var channel = channels?.Items.FirstOrDefault();
            if (channel is null || string.IsNullOrEmpty(channel.ContentDetails.RelatedPlaylists.Uploads))
            {
                _logger.LogWarning("Channel {ChannelId} not found.", channelId);
                return (null, result);
            }

            string? pageToken = null;
            var pages = 0;

            // Shorts, live streams and premieres are filtered out after fetching, so keep paging
            // until enough regular videos are found (bounded so a Shorts-only channel can't page forever).
            do
            {
                var url = $"{Base}/playlistItems?part=contentDetails&maxResults=50" +
                          $"&playlistId={Uri.EscapeDataString(channel.ContentDetails.RelatedPlaylists.Uploads)}&key={key}";
                if (pageToken is not null) url += $"&pageToken={Uri.EscapeDataString(pageToken)}";

                var page = await client.GetFromJsonAsync<YouTubePlaylistItemListResponse>(url, ct);
                if (page is null || page.Items.Length == 0) break;

                var ids = string.Join(",", page.Items.Select(i => Uri.EscapeDataString(i.ContentDetails.VideoId)));
                var details = await client.GetFromJsonAsync<YouTubeVideoListResponse>(
                    $"{Base}/videos?part=snippet,contentDetails&id={ids}&key={key}", ct);

                foreach (var v in details?.Items ?? [])
                {
                    if (!string.Equals(v.Snippet.LiveBroadcastContent, "none", StringComparison.OrdinalIgnoreCase))
                        continue;

                    var dur = string.IsNullOrEmpty(v.ContentDetails?.Duration) ? TimeSpan.Zero : ParseDuration(v.ContentDetails.Duration);
                    if (!includeShorts && dur.TotalSeconds > 0 && dur.TotalSeconds <= 180)
                        continue;

                    result.Add((v.Id, v.Snippet, dur));
                    if (result.Count >= maxResults) break;
                }

                pageToken = page.NextPageToken;
            }
            while (pageToken is not null && result.Count < maxResults && ++pages < 10);

            return (channel.Snippet.Title, result);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch videos for channel {ChannelId}", channelId);
            return (null, result);
        }
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
