using System.Collections.Generic;
using Jellyfin.Plugin.JellyTube.Models;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.JellyTube.Configuration;

/// <summary>
/// Plugin configuration.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Gets or sets the base directory where downloaded videos are stored.
    /// </summary>
    public string DownloadPath { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the path to the yt-dlp binary. Leave empty to use the one on PATH.
    /// </summary>
    public string YtDlpBinaryPath { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the path to the ffmpeg binary. Leave empty to use the one on PATH.
    /// </summary>
    public string FfmpegBinaryPath { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the yt-dlp format string for video quality selection.
    /// </summary>
    public string VideoFormat { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the preferred output container: mp4, mkv, or webm.
    /// </summary>
    public string PreferredContainer { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the maximum number of simultaneous downloads.
    /// </summary>
    public int MaxConcurrentDownloads { get; set; } = 1;

    /// <summary>
    /// Gets or sets a value indicating whether to organise videos into channel subfolders.
    /// </summary>
    public bool OrganiseByChannel { get; set; } = false;

    /// <summary>
    /// Gets or sets a value indicating whether to download subtitles.
    /// </summary>
    public bool DownloadSubtitles { get; set; } = false;

    /// <summary>
    /// Gets or sets the subtitle language codes (comma-separated, e.g. "en,de").
    /// </summary>
    public string SubtitleLanguages { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether to trigger a Jellyfin library scan after downloads complete.
    /// </summary>
    public bool TriggerLibraryScanAfterDownload { get; set; } = false;

    /// <summary>
    /// Gets or sets a value indicating whether scheduled playlist downloads are enabled.
    /// </summary>
    public bool EnableScheduledDownloads { get; set; } = false;

    /// <summary>
    /// Gets or sets the list of scheduled playlist/channel entries, each with its own optional download path.
    /// </summary>
    public List<ScheduledPlaylistEntry> ScheduledEntries { get; set; } = new();

    /// <summary>
    /// Gets or sets the maximum age in days for playlist items (0 = unlimited).
    /// </summary>
    public int PlaylistMaxAgeDays { get; set; } = 0;

    /// <summary>
    /// Gets or sets a value indicating whether to write .nfo metadata files.
    /// </summary>
    public bool WriteNfoFiles { get; set; } = false;

    /// <summary>
    /// Gets or sets a value indicating whether to download video thumbnails.
    /// </summary>
    public bool DownloadThumbnails { get; set; } = false;

    /// <summary>
    /// Gets or sets a value indicating whether watched videos from scheduled downloads are automatically deleted.
    /// </summary>
    public bool DeleteWatchedScheduledVideos { get; set; } = false;
}
