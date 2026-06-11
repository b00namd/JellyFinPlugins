using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyTube.Models;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
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
    private CancellationTokenSource? _cts;

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
        _libraryManager.ItemRemoved += OnItemRemoved;

        // Periodically delete videos whose post-watch grace period has expired (startup + every 6h).
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = _cts.Token;
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromMinutes(2), token).ConfigureAwait(false);
            while (!token.IsCancellationRequested)
            {
                try { await ProcessPendingDeletionsAsync(token).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
                catch (Exception ex) { _logger.LogWarning(ex, "Error while processing pending watched deletions."); }

                try { await Task.Delay(TimeSpan.FromHours(6), token).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }, token);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _userDataManager.UserDataSaved -= OnUserDataSaved;
        _libraryManager.ItemRemoved -= OnItemRemoved;
        _cts?.Cancel();
        return Task.CompletedTask;
    }

    /// <summary>
    /// When a downloaded JellyTube video is removed from the library — manually by the user or by
    /// the watched-cleanup — keep it in the download archive and mark it protected, so it is neither
    /// re-downloaded on the next scheduled run nor pulled back by an archive reconcile. In short:
    /// "deleted stays deleted".
    /// </summary>
    private void OnItemRemoved(object? sender, ItemChangeEventArgs e)
    {
        var config = Plugin.Instance?.Configuration;
        if (config is null)
            return;

        var filePath = e.Item?.Path;
        if (string.IsNullOrEmpty(filePath) || !IsVideoFile(filePath))
            return;

        if (!IsUnderDownloadPath(filePath, config))
            return;

        // Prefer the YouTube ID from the library item (set from the NFO's <uniqueid type="youtube">);
        // it survives even when the file and its sidecars are already gone.
        var videoId = ResolveVideoId(e.Item, filePath);

        if (string.IsNullOrWhiteSpace(videoId))
        {
            _logger.LogWarning("Removed JellyTube video '{Path}' but could not determine its ID; it may be re-downloaded.", filePath);
            return;
        }

        _archive.AddProtected(videoId);
        _logger.LogInformation("JellyTube video removed ('{Path}'); protected {VideoId} so it is not re-downloaded.", filePath, videoId);
    }

    private static bool IsUnderDownloadPath(string filePath, Configuration.PluginConfiguration config)
    {
        if (!string.IsNullOrWhiteSpace(config.DownloadPath) &&
            filePath.StartsWith(config.DownloadPath, StringComparison.OrdinalIgnoreCase))
            return true;

        return config.ScheduledEntries.Any(entry =>
            !string.IsNullOrWhiteSpace(entry.DownloadPath) &&
            filePath.StartsWith(entry.DownloadPath, StringComparison.OrdinalIgnoreCase));
    }

    private void OnUserDataSaved(object? sender, UserDataSaveEventArgs e)
    {
        var config = Plugin.Instance?.Configuration;
        if (config is null)
            return;

        // Manual only: act solely when the user explicitly toggles the watched state, NOT when
        // Jellyfin auto-marks a video played after it passes its playback threshold (~90%).
        if (e.SaveReason != UserDataSaveReason.TogglePlayed)
            return;

        var filePath = e.Item?.Path;
        if (string.IsNullOrEmpty(filePath) || !IsVideoFile(filePath))
            return;

        var pendingPath = Path.ChangeExtension(filePath, ".delete-pending");

        // Un-marked as watched again → cancel any queued deletion.
        if (!e.UserData.Played)
        {
            if (File.Exists(pendingPath))
            {
                TryDelete(pendingPath);
                _logger.LogInformation("'{Path}' marked unwatched again; cancelled queued deletion.", filePath);
            }
            return;
        }

        // Marked watched. Only queue if this video is a delete-watched candidate.
        if (!IsDeleteWatchedCandidate(filePath, config))
            return;

        var videoId = ResolveVideoId(e.Item, filePath);
        try
        {
            // Queue for deletion; the marker's timestamp starts the grace period.
            File.WriteAllText(pendingPath, videoId ?? string.Empty);
            var grace = Math.Max(0, config.DeleteWatchedGraceDays);
            _logger.LogInformation("'{Path}' marked watched; queued for deletion in {Days} day(s).", filePath, grace);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not write delete-pending marker for '{Path}'.", filePath);
        }
    }

    /// <summary>True if the video is eligible for delete-after-watched (download-time marker or path/config).</summary>
    private static bool IsDeleteWatchedCandidate(string filePath, Configuration.PluginConfiguration config)
    {
        var marker = Path.ChangeExtension(filePath, ".delete-watched");
        return File.Exists(marker) || ShouldDeleteBasedOnPath(filePath, config);
    }

    private string? ResolveVideoId(MediaBrowser.Controller.Entities.BaseItem? item, string filePath)
    {
        string? id = null;
        if (item?.ProviderIds is { } providerIds)
            providerIds.TryGetValue("youtube", out id);
        return string.IsNullOrWhiteSpace(id) ? TryExtractVideoId(filePath) : id;
    }

    /// <summary>
    /// Deletes videos whose post-watch grace period has expired. A ".delete-pending" marker is
    /// written when the user manually marks a delete-watched video as watched; the marker's file
    /// timestamp starts the grace period. Once older than DeleteWatchedGraceDays, the video and its
    /// sidecars are deleted and the video is protected from re-download.
    /// </summary>
    private async Task ProcessPendingDeletionsAsync(CancellationToken ct)
    {
        var config = Plugin.Instance?.Configuration;
        if (config is null)
            return;

        var graceDays = Math.Max(0, config.DeleteWatchedGraceDays);
        var cutoff = DateTime.UtcNow.AddDays(-graceDays);

        var basePaths = new List<string>();
        if (!string.IsNullOrWhiteSpace(config.DownloadPath))
            basePaths.Add(config.DownloadPath);
        foreach (var entry in config.ScheduledEntries)
            if (!string.IsNullOrWhiteSpace(entry.DownloadPath))
                basePaths.Add(entry.DownloadPath);
        basePaths = basePaths.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        int deleted = 0;
        int scanned = 0;

        foreach (var basePath in basePaths)
        {
            if (ct.IsCancellationRequested)
                return;
            if (!Directory.Exists(basePath))
                continue;

            IEnumerable<string> markers;
            try
            {
                markers = Directory.EnumerateFiles(basePath, "*.delete-pending", SearchOption.AllDirectories);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not enumerate pending deletions under '{Path}'.", basePath);
                continue;
            }

            foreach (var marker in markers.ToList())
            {
                if (ct.IsCancellationRequested)
                    return;
                if (++scanned % 200 == 0)
                    await Task.Yield();

                DateTime markedAt;
                try { markedAt = File.GetLastWriteTimeUtc(marker); }
                catch { continue; }

                if (markedAt > cutoff)
                    continue; // still within the grace period

                var dir = Path.GetDirectoryName(marker)!;
                var stem = Path.GetFileNameWithoutExtension(marker); // marker == "<video stem>.delete-pending"
                var videoFile = FindVideoFile(dir, stem);

                if (videoFile is null)
                {
                    TryDelete(marker); // video already gone; drop the stray marker
                    continue;
                }

                string? id = null;
                try { id = File.ReadAllText(marker).Trim(); }
                catch { /* fall back below */ }
                if (string.IsNullOrWhiteSpace(id))
                    id = TryExtractVideoId(videoFile);
                if (!string.IsNullOrWhiteSpace(id))
                    _archive.AddProtected(id);

                _logger.LogInformation("Grace period expired; deleting watched video '{Path}'.", videoFile);
                DeleteWithSidecars(videoFile);
                deleted++;
            }
        }

        if (deleted > 0)
        {
            _logger.LogInformation("Deleted {Count} watched video(s) past their grace period.", deleted);
            _libraryManager.QueueLibraryScan();
        }
    }

    private static string? FindVideoFile(string dir, string stem)
    {
        foreach (var ext in VideoExtensions)
        {
            var candidate = Path.Combine(dir, stem + ext);
            if (File.Exists(candidate))
                return candidate;
        }
        return null;
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
