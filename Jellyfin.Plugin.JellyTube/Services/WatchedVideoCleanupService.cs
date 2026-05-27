using System;
using System.IO;
using System.Linq;
using System.Text.Json;
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
    private static readonly string[] VideoExtensions = { ".mp4", ".mkv", ".webm", ".avi", ".mov" };

    private readonly DownloadQueueService _queue;
    private readonly DownloadArchiveService _archive;
    private readonly IUserDataManager _userDataManager;
    private readonly IUserManager _userManager;
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<WatchedVideoCleanupService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="WatchedVideoCleanupService"/> class.
    /// </summary>
    public WatchedVideoCleanupService(
        DownloadQueueService queue,
        DownloadArchiveService archive,
        IUserDataManager userDataManager,
        IUserManager userManager,
        ILibraryManager libraryManager,
        ILogger<WatchedVideoCleanupService> logger)
    {
        _queue = queue;
        _archive = archive;
        _userDataManager = userDataManager;
        _userManager = userManager;
        _libraryManager = libraryManager;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _userDataManager.UserDataSaved += OnUserDataSaved;

        // Run startup scan after a short delay to let Jellyfin finish initializing
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromMinutes(2), cancellationToken).ConfigureAwait(false);
            await ScanAndDeleteWatchedAsync(cancellationToken).ConfigureAwait(false);
        }, cancellationToken);

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

        // Find a matching completed scheduled download job still in memory
        var job = _queue.GetAllJobs().FirstOrDefault(j =>
            j.IsScheduled &&
            j.Status == DownloadJobStatus.Completed &&
            j.DownloadedFilePath != null &&
            string.Equals(j.DownloadedFilePath, filePath, StringComparison.OrdinalIgnoreCase));

        // Marker file written at download time survives restarts
        var markerPath = Path.ChangeExtension(filePath, ".delete-watched");
        var hasMarker = File.Exists(markerPath);

        // Fallback: check if the file lives under a scheduled entry's download path with DeleteWatched=true
        bool isUnderScheduledEntry = false;
        if (job is null && !hasMarker)
        {
            isUnderScheduledEntry = ShouldDeleteBasedOnPath(filePath, config);
            if (!isUnderScheduledEntry)
                return;
        }

        var shouldDelete = hasMarker || isUnderScheduledEntry || (job?.DeleteWatched ?? false) || config.DeleteWatchedScheduledVideos;
        if (!shouldDelete)
            return;

        DeleteWatchedFile(filePath, job?.Metadata?.VideoId);
    }

    private async Task ScanAndDeleteWatchedAsync(CancellationToken ct)
    {
        var config = Plugin.Instance?.Configuration;
        if (config is null)
            return;

        var users = _userManager.Users.ToList();
        if (users.Count == 0)
            return;

        // Collect all paths that should have delete-watched enabled
        var pathsToScan = config.ScheduledEntries
            .Where(e => e.DeleteWatched)
            .Select(e => string.IsNullOrWhiteSpace(e.DownloadPath) ? config.DownloadPath : e.DownloadPath)
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (config.DeleteWatchedScheduledVideos && !string.IsNullOrWhiteSpace(config.DownloadPath))
            pathsToScan.Add(config.DownloadPath);

        if (config.DeleteWatchedManualVideos && !string.IsNullOrWhiteSpace(config.DownloadPath))
            pathsToScan.Add(config.DownloadPath);

        pathsToScan = pathsToScan.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        if (pathsToScan.Count == 0)
            return;

        _logger.LogInformation("Starting watched-video startup scan across {Count} path(s).", pathsToScan.Count);
        int deleted = 0;

        foreach (var basePath in pathsToScan)
        {
            if (!Directory.Exists(basePath))
                continue;

            foreach (var filePath in Directory.EnumerateFiles(basePath, "*.*", SearchOption.AllDirectories))
            {
                if (ct.IsCancellationRequested)
                    return;

                if (!IsVideoFile(filePath))
                    continue;

                var item = _libraryManager.FindByPath(filePath, false);
                if (item is null)
                    continue;

                var isPlayed = users.Any(user =>
                    _userDataManager.GetUserData(user, item).Played);

                if (!isPlayed)
                    continue;

                _logger.LogInformation("Startup scan: deleting already-watched '{Path}'.", filePath);
                DeleteWatchedFile(filePath, videoId: null);
                deleted++;
            }
        }

        if (deleted > 0)
        {
            _logger.LogInformation("Startup scan complete: deleted {Count} watched video(s).", deleted);
            _libraryManager.QueueLibraryScan();
        }
        else
        {
            _logger.LogInformation("Startup scan complete: no watched videos to delete.");
        }
    }

    private void DeleteWatchedFile(string filePath, string? videoId)
    {
        _logger.LogInformation("Watched video '{Path}', deleting file.", filePath);

        if (string.IsNullOrWhiteSpace(videoId))
            videoId = TryExtractVideoId(filePath);

        if (!string.IsNullOrWhiteSpace(videoId))
        {
            _archive.Add(videoId);
        }
        else
        {
            _logger.LogWarning("Could not extract video ID from '{Path}'. Video will not be added to archive.", filePath);
        }

        DeleteWithSidecars(filePath);
        _libraryManager.QueueLibraryScan();
    }

    private string? TryExtractVideoId(string filePath)
    {
        var dir = Path.GetDirectoryName(filePath);
        var stem = Path.GetFileNameWithoutExtension(filePath);

        if (dir is not null)
        {
            // Try .delete-watched marker file (contains video ID)
            var markerPath = Path.Combine(dir, stem + ".delete-watched");
            if (File.Exists(markerPath))
            {
                try
                {
                    var content = File.ReadAllText(markerPath).Trim();
                    if (IsPlausibleVideoId(content))
                        return content;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to read video ID from marker '{Path}'.", markerPath);
                }
            }

            // Try .info.json sidecar (playlists)
            var infoJsonPath = Path.Combine(dir, stem + ".info.json");
            if (File.Exists(infoJsonPath))
            {
                try
                {
                    using var stream = File.OpenRead(infoJsonPath);
                    using var doc = JsonDocument.Parse(stream);
                    if (doc.RootElement.TryGetProperty("id", out var idProp) &&
                        idProp.ValueKind == JsonValueKind.String)
                    {
                        var id = idProp.GetString();
                        if (!string.IsNullOrWhiteSpace(id))
                            return id;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to read video ID from '{Path}'.", infoJsonPath);
                }
            }
        }

        // Fall back to filename pattern: "Title - <videoId>.ext"
        var dashIdx = stem.LastIndexOf(" - ", StringComparison.Ordinal);
        if (dashIdx >= 0)
        {
            var extractedId = stem[(dashIdx + 3)..];
            if (IsPlausibleVideoId(extractedId))
                return extractedId;
        }

        return null;
    }

    private static bool IsPlausibleVideoId(string id)
    {
        return id.Length >= 6 && id.Length <= 16 &&
               id.All(c => char.IsLetterOrDigit(c) || c is '-' or '_');
    }

    private static bool ShouldDeleteBasedOnPath(string filePath, Configuration.PluginConfiguration config)
    {
        if (config.DeleteWatchedScheduledVideos)
            return true;

        if (config.DeleteWatchedManualVideos &&
            !string.IsNullOrWhiteSpace(config.DownloadPath) &&
            filePath.StartsWith(config.DownloadPath, StringComparison.OrdinalIgnoreCase))
            return true;

        return config.ScheduledEntries.Any(entry =>
        {
            var effectivePath = string.IsNullOrWhiteSpace(entry.DownloadPath)
                ? config.DownloadPath
                : entry.DownloadPath;
            return entry.DeleteWatched &&
                   !string.IsNullOrWhiteSpace(effectivePath) &&
                   filePath.StartsWith(effectivePath, StringComparison.OrdinalIgnoreCase);
        });
    }

    private static bool IsVideoFile(string path)
    {
        var ext = Path.GetExtension(path);
        return VideoExtensions.Any(e => string.Equals(e, ext, StringComparison.OrdinalIgnoreCase));
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
