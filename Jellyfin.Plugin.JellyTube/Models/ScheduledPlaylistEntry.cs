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
}
