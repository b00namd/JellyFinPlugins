using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.JellyTubbing.Models;

/// <summary>YouTube Data API v3 – videos.list response.</summary>
public class YouTubeVideoListResponse
{
    [JsonPropertyName("items")] public YouTubeVideoItem[] Items { get; set; } = [];
    [JsonPropertyName("nextPageToken")] public string? NextPageToken { get; set; }
}

/// <summary>A single video item from the YouTube Data API.</summary>
public class YouTubeVideoItem
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("snippet")] public YouTubeVideoSnippet Snippet { get; set; } = new();
    [JsonPropertyName("contentDetails")] public YouTubeContentDetails? ContentDetails { get; set; }
}

/// <summary>Snippet block of a YouTube video.</summary>
public class YouTubeVideoSnippet
{
    [JsonPropertyName("title")] public string Title { get; set; } = string.Empty;
    [JsonPropertyName("description")] public string Description { get; set; } = string.Empty;
    [JsonPropertyName("publishedAt")] public string PublishedAt { get; set; } = string.Empty;
    [JsonPropertyName("channelTitle")] public string ChannelTitle { get; set; } = string.Empty;
    [JsonPropertyName("thumbnails")] public YouTubeThumbnails Thumbnails { get; set; } = new();
}

/// <summary>Content details block of a YouTube video.</summary>
public class YouTubeContentDetails
{
    /// <summary>ISO 8601 duration string, e.g. PT4M13S.</summary>
    [JsonPropertyName("duration")] public string Duration { get; set; } = string.Empty;
}

/// <summary>Thumbnail variants for a YouTube resource.</summary>
public class YouTubeThumbnails
{
    [JsonPropertyName("maxres")] public YouTubeThumbnail? MaxRes { get; set; }
    [JsonPropertyName("high")] public YouTubeThumbnail? High { get; set; }
    [JsonPropertyName("medium")] public YouTubeThumbnail? Medium { get; set; }
    [JsonPropertyName("default")] public YouTubeThumbnail? Default { get; set; }

    /// <summary>Best available thumbnail URL, highest resolution first.</summary>
    public string BestUrl => (MaxRes ?? High ?? Medium ?? Default)?.Url ?? string.Empty;
}

/// <summary>A single thumbnail entry.</summary>
public class YouTubeThumbnail
{
    [JsonPropertyName("url")] public string Url { get; set; } = string.Empty;
}

/// <summary>YouTube Data API v3 – subscriptions.list response.</summary>
public class YouTubeSubscriptionListResponse
{
    [JsonPropertyName("items")] public YouTubeSubscriptionItem[] Items { get; set; } = [];
    [JsonPropertyName("nextPageToken")] public string? NextPageToken { get; set; }
}

/// <summary>A single subscription item.</summary>
public class YouTubeSubscriptionItem
{
    [JsonPropertyName("snippet")] public YouTubeSubscriptionSnippet Snippet { get; set; } = new();
}

/// <summary>Snippet of a subscription.</summary>
public class YouTubeSubscriptionSnippet
{
    [JsonPropertyName("title")] public string Title { get; set; } = string.Empty;
    [JsonPropertyName("resourceId")] public YouTubeResourceId ResourceId { get; set; } = new();
    [JsonPropertyName("thumbnails")] public YouTubeThumbnails Thumbnails { get; set; } = new();
}

/// <summary>Resource identifier inside a subscription snippet.</summary>
public class YouTubeResourceId
{
    [JsonPropertyName("channelId")] public string ChannelId { get; set; } = string.Empty;
}

/// <summary>YouTube Data API v3 – search.list response.</summary>
public class YouTubeSearchListResponse
{
    [JsonPropertyName("items")] public YouTubeSearchItem[] Items { get; set; } = [];
    [JsonPropertyName("nextPageToken")] public string? NextPageToken { get; set; }
}

/// <summary>A single search result item.</summary>
public class YouTubeSearchItem
{
    [JsonPropertyName("id")] public YouTubeSearchItemId Id { get; set; } = new();
    [JsonPropertyName("snippet")] public YouTubeVideoSnippet Snippet { get; set; } = new();
}

/// <summary>The id block of a search result.</summary>
public class YouTubeSearchItemId
{
    [JsonPropertyName("videoId")] public string VideoId { get; set; } = string.Empty;
}

/// <summary>Google OAuth2 token response.</summary>
public class OAuthTokenResponse
{
    [JsonPropertyName("access_token")] public string AccessToken { get; set; } = string.Empty;
    [JsonPropertyName("refresh_token")] public string? RefreshToken { get; set; }
    [JsonPropertyName("expires_in")] public int ExpiresIn { get; set; }
    [JsonPropertyName("token_type")] public string TokenType { get; set; } = string.Empty;
}

/// <summary>Google Device Authorization response (RFC 8628).</summary>
public class DeviceCodeResponse
{
    [JsonPropertyName("device_code")] public string DeviceCode { get; set; } = string.Empty;
    [JsonPropertyName("user_code")] public string UserCode { get; set; } = string.Empty;
    [JsonPropertyName("verification_url")] public string VerificationUrl { get; set; } = string.Empty;
    [JsonPropertyName("expires_in")] public int ExpiresIn { get; set; }
    [JsonPropertyName("interval")] public int Interval { get; set; } = 5;
}

/// <summary>Error response from Google OAuth2 token endpoint.</summary>
public class OAuthErrorResponse
{
    [JsonPropertyName("error")] public string Error { get; set; } = string.Empty;
}
