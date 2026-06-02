namespace Jellyfin.Plugin.JellyTube.Models;

/// <summary>
/// Represents the current state of a download job.
/// </summary>
public enum DownloadJobStatus
{
    /// <summary>Job is waiting in the queue.</summary>
    Queued,

    /// <summary>Fetching video metadata from YouTube.</summary>
    FetchingMetadata,

    /// <summary>Actively downloading the video file.</summary>
    Downloading,

    /// <summary>Writing NFO and thumbnail files.</summary>
    WritingMetadata,

    /// <summary>Download and metadata writing completed successfully.</summary>
    Completed,

    /// <summary>Playlist download finished but some videos failed.</summary>
    CompletedWithErrors,

    /// <summary>Download failed due to an error.</summary>
    Failed,

    /// <summary>Download was cancelled by the user.</summary>
    Cancelled
}
