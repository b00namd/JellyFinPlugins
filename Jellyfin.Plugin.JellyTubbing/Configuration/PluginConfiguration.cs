using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.JellyTubbing.Configuration;

/// <summary>
/// Configuration for JellyTubbing plugin.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    // -----------------------------------------------------------------------
    // YouTube Data API
    // -----------------------------------------------------------------------

    /// <summary>Gets or sets the YouTube Data API v3 key (required for trending and channel videos).</summary>
    public string YouTubeApiKey { get; set; } = string.Empty;

    // -----------------------------------------------------------------------
    // Google OAuth2 (for accessing subscriptions)
    // -----------------------------------------------------------------------

    /// <summary>Gets or sets the OAuth2 client ID from Google Cloud Console.</summary>
    public string OAuthClientId { get; set; } = string.Empty;

    /// <summary>Gets or sets the OAuth2 client secret from Google Cloud Console.</summary>
    public string OAuthClientSecret { get; set; } = string.Empty;

    /// <summary>Gets or sets the stored OAuth2 access token.</summary>
    public string OAuthAccessToken { get; set; } = string.Empty;

    /// <summary>Gets or sets the stored OAuth2 refresh token.</summary>
    public string OAuthRefreshToken { get; set; } = string.Empty;

    /// <summary>Gets or sets the expiry of the access token as Unix timestamp (seconds).</summary>
    public long OAuthTokenExpiryUnix { get; set; } = 0;

    // -----------------------------------------------------------------------
    // STRM library sync
    // -----------------------------------------------------------------------

    /// <summary>Gets or sets the output folder for STRM/NFO/thumbnail files.</summary>
    public string StrmOutputPath { get; set; } = string.Empty;

    /// <summary>Gets or sets the Jellyfin server base URL used inside STRM files.</summary>
    public string JellyfinServerUrl { get; set; } = "http://localhost:8096";

    /// <summary>Gets or sets the channel IDs to synchronise as STRM files.</summary>
    public string[] SyncedChannelIds { get; set; } = [];

    /// <summary>Gets or sets maximum number of videos to sync per channel.</summary>
    public int MaxVideosPerChannel { get; set; } = 25;

    /// <summary>Gets or sets whether YouTube Shorts (≤ 60 s) are included in the sync.</summary>
    public bool IncludeShorts { get; set; } = false;

    // -----------------------------------------------------------------------
    // Streaming / yt-dlp
    // -----------------------------------------------------------------------

    /// <summary>Gets or sets the optional path to the yt-dlp binary.</summary>
    public string YtDlpBinaryPath { get; set; } = string.Empty;

    /// <summary>Gets or sets the preferred stream quality (360p, 480p, 720p, 1080p).</summary>
    public string PreferredQuality { get; set; } = "720p";

    // -----------------------------------------------------------------------
    // Trending channel view
    // -----------------------------------------------------------------------

    /// <summary>Gets or sets the ISO 3166-1 alpha-2 region code for trending videos (e.g. DE, US).</summary>
    public string TrendingRegion { get; set; } = "DE";
}
