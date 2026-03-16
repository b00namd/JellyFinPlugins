using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyTube.Models;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyTube.Services;

/// <summary>
/// Listens for Jellyfin playback events and deletes watched videos
/// that originated from scheduled downloads, then adds them to the
/// download archive so they are not re-downloaded.
/// </summary>
public class WatchedVideoCleanupService : IHostedService
{
    private readonly DownloadQueueService _queue;
    private readonly DownloadArchiveService _archive;
    private readonly IUserDataManager _userDataManager;
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<WatchedVideoCleanupService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="WatchedVideoCleanupService"/> class.
    /// </summary>
    public WatchedVideoCleanupService(
        DownloadQueueService queue,
        DownloadArchiveService archive,
        IUserDataManager userDataManager,
        ILibraryManager libraryManager,
        ILogger<WatchedVideoCleanupService> logger)
    {
        _queue = queue;
        _archive = archive;
        _userDataManager = userDataManager;
        _libraryManager = libraryManager;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _userDataManager.UserDataSaved += OnUserDataSaved;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _userDataManager.UserDataSaved -= OnUserDataSaved;
        return Task.CompletedTask;
    }

    private void OnUserDataSaved(object? sender, UserDataSaveEventArgs e)
    {
        var config = Plugin.Instance?.Configuration;
        if (config is null)
            return;

        if (!e.UserData.Played)
            return;

        var filePath = e.Item?.Path;
        if (string.IsNullOrEmpty(filePath))
            return;

        // Find a matching completed scheduled download job
        var job = _queue.GetAllJobs().FirstOrDefault(j =>
            j.IsScheduled &&
            j.Status == DownloadJobStatus.Completed &&
            j.DownloadedFilePath != null &&
            string.Equals(j.DownloadedFilePath, filePath, StringComparison.OrdinalIgnoreCase));

        if (job is null)
            return;

        // Per-job setting takes priority; fall back to global DeleteWatchedScheduledVideos
        if (!job.DeleteWatched && !config.DeleteWatchedScheduledVideos)
            return;

        _logger.LogInformation("Watched scheduled video '{Path}', deleting file.", filePath);

        // Add to archive so yt-dlp won't re-download it
        if (job.Metadata?.VideoId is { Length: > 0 } videoId)
        {
            _archive.Add(videoId);
        }

        // Delete video file and all related sidecar files (.nfo, thumbnail, subtitles)
        DeleteWithSidecars(filePath);

        // Trigger library scan so Jellyfin removes the item from its database
        _libraryManager.QueueLibraryScan();
    }

    private void DeleteWithSidecars(string videoPath)
    {
        var dir = Path.GetDirectoryName(videoPath);
        var baseName = Path.GetFileNameWithoutExtension(videoPath);

        if (dir is null)
            return;

        TryDelete(videoPath);

        foreach (var sidecar in Directory.EnumerateFiles(dir, $"{baseName}.*"))
        {
            if (!string.Equals(sidecar, videoPath, StringComparison.OrdinalIgnoreCase))
                TryDelete(sidecar);
        }
    }

    private void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
            _logger.LogInformation("Deleted '{Path}'.", path);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to delete '{Path}'.", path);
        }
    }
}
