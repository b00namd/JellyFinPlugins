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
    public string VideoFormat { get; set; } = "bestvideo[height<=1080][vcodec^=avc1]+bestaudio[ext=m4a]/bestvideo[height<=1080][vcodec^=avc1]+bestaudio/best[height<=1080]";

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
    /// Gets or sets how many of the most-recent videos to check per channel on each scheduled run.
    /// Bounds how deep yt-dlp scans a channel's listing (it stops looking past this many entries).
    /// Must be large enough to cover the configured max age window. 0 = scan the entire channel (slow).
    /// </summary>
    public int PlaylistScanLimit { get; set; } = 50;

    /// <summary>
    /// Gets or sets a value indicating whether YouTube Shorts are excluded from channel downloads.
    /// When enabled, a bare channel URL is scanned via its "/videos" tab (long-form uploads only)
    /// instead of the channel root, which also pulls in the separate Shorts tab.
    /// </summary>
    public bool ExcludeShorts { get; set; } = true;

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

    /// <summary>
    /// Gets or sets a value indicating whether watched videos from manual downloads are automatically deleted.
    /// </summary>
    public bool DeleteWatchedManualVideos { get; set; } = false;

    /// <summary>
    /// Gets or sets the default audio language tag to embed in downloaded files (e.g. "deu", "eng", "fra").
    /// Leave empty to not set a language tag.
    /// </summary>
    public string DefaultAudioLanguage { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the path to a Netscape-format cookies file for authenticated YouTube downloads.
    /// Leave empty to download without cookies.
    /// </summary>
    public string CookiesFilePath { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets how many times a failed download should be retried before giving up.
    /// Set to 0 to disable retries. Default 2 (3 total attempts).
    /// </summary>
    public int DownloadRetryCount { get; set; } = 2;
}
