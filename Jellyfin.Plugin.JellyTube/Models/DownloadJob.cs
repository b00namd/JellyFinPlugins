using System;

namespace Jellyfin.Plugin.JellyTube.Models;

/// <summary>
/// Represents a single download job in the queue.
/// </summary>
public class DownloadJob
{
    /// <summary>Gets the unique identifier of this job.</summary>
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>Gets or sets the YouTube URL (video or playlist).</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether this URL points to a playlist.</summary>
    public bool IsPlaylist { get; set; }

    /// <summary>Gets or sets the current job status.</summary>
    public DownloadJobStatus Status { get; set; } = DownloadJobStatus.Queued;

    /// <summary>Gets or sets the download progress (0–100).</summary>
    public double ProgressPercent { get; set; }

    /// <summary>Gets or sets the name of the file currently being downloaded.</summary>
    public string? CurrentFile { get; set; }

    /// <summary>Gets or sets the error message if the job failed.</summary>
    public string? ErrorMessage { get; set; }

    /// <summary>Gets the time this job was created.</summary>
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;

    /// <summary>Gets or sets the time this job completed (or failed).</summary>
    public DateTime? CompletedAt { get; set; }

    /// <summary>Gets or sets the fetched video metadata.</summary>
    public VideoMetadata? Metadata { get; set; }

    /// <summary>Gets or sets a value indicating whether this job was started by the scheduled task.</summary>
    public bool IsScheduled { get; set; }

    /// <summary>Gets or sets the full path to the downloaded video file (set after download completes).</summary>
    public string? DownloadedFilePath { get; set; }

    /// <summary>Gets or sets an optional download directory override. Overrides the global DownloadPath for this job.</summary>
    public string? OverrideDownloadPath { get; set; }

    /// <summary>Gets or sets the maximum video age in days for playlist downloads (0 = use global/unlimited).</summary>
    public int MaxAgeDays { get; set; } = 0;

    /// <summary>Gets or sets a value indicating whether watched files should be deleted for this job.</summary>
    public bool DeleteWatched { get; set; } = false;
}
