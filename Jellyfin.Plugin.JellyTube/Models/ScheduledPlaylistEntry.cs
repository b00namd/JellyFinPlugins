namespace Jellyfin.Plugin.JellyTube.Models;

/// <summary>
/// A single scheduled playlist/channel URL with an optional custom download path.
/// </summary>
public class ScheduledPlaylistEntry
{
    /// <summary>Gets or sets the playlist or channel URL.</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>Gets or sets the download path override. Empty means use the global DownloadPath.</summary>
    public string DownloadPath { get; set; } = string.Empty;

    /// <summary>Gets or sets the maximum video age in days (0 = unlimited, falls back to global PlaylistMaxAgeDays).</summary>
    public int MaxAgeDays { get; set; } = 0;

    /// <summary>Gets or sets a value indicating whether watched videos from this entry are automatically deleted.</summary>
    public bool DeleteWatched { get; set; } = false;
}
