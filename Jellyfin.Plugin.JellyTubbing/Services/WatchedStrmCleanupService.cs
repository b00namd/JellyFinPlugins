using System;
using System.IO;
using System.Linq;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyTubbing.Services;

/// <summary>
/// Listens for Jellyfin playback events and deletes watched STRM files
/// (plus their .nfo and thumbnail sidecars) when DeleteWatchedStrm is enabled.
/// </summary>
public class WatchedStrmCleanupService : IHostedService
{
    private readonly IUserDataManager _userDataManager;
    private readonly ILibraryManager _libraryManager;
    private readonly DeletedVideoList _deleted;
    private readonly VideoCacheService _cache;
    private readonly ILogger<WatchedStrmCleanupService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="WatchedStrmCleanupService"/> class.
    /// </summary>
    public WatchedStrmCleanupService(
        IUserDataManager userDataManager,
        ILibraryManager libraryManager,
        DeletedVideoList deleted,
        VideoCacheService cache,
        ILogger<WatchedStrmCleanupService> logger)
    {
        _userDataManager = userDataManager;
        _libraryManager  = libraryManager;
        _deleted         = deleted;
        _cache           = cache;
        _logger          = logger;
    }

    /// <inheritdoc />
    public System.Threading.Tasks.Task StartAsync(System.Threading.CancellationToken cancellationToken)
    {
        _userDataManager.UserDataSaved += OnUserDataSaved;
        return System.Threading.Tasks.Task.CompletedTask;
    }

    /// <inheritdoc />
    public System.Threading.Tasks.Task StopAsync(System.Threading.CancellationToken cancellationToken)
    {
        _userDataManager.UserDataSaved -= OnUserDataSaved;
        return System.Threading.Tasks.Task.CompletedTask;
    }

    private void OnUserDataSaved(object? sender, UserDataSaveEventArgs e)
    {
        var config = Plugin.Instance?.Configuration;
        if (config is null || !config.DeleteWatchedStrm)
            return;

        if (!e.UserData.Played)
            return;

        var filePath = e.Item?.Path;
        if (string.IsNullOrEmpty(filePath))
            return;

        // Only handle .strm files inside the configured STRM output path
        if (!string.Equals(Path.GetExtension(filePath), ".strm", StringComparison.OrdinalIgnoreCase))
            return;

        if (!string.IsNullOrWhiteSpace(config.StrmOutputPath) &&
            !filePath.StartsWith(config.StrmOutputPath, StringComparison.OrdinalIgnoreCase))
            return;

        if (!File.Exists(filePath))
            return;

        _logger.LogInformation("Watched STRM '{Path}', deleting with sidecars.", filePath);

        string? videoId = null;
        try { videoId = StrmService.VideoIdFromStrm(File.ReadAllText(filePath)); }
        catch (Exception ex) { _logger.LogDebug(ex, "Could not read '{Path}'.", filePath); }

        DeleteWithSidecars(filePath);

        if (videoId is not null)
        {
            // Otherwise the next sync recreates it while it is still among the channel's newest videos
            _deleted.Add(videoId);
            _cache.Delete(videoId);
        }

        _libraryManager.QueueLibraryScan();
    }

    private void DeleteWithSidecars(string strmPath)
    {
        var dir      = Path.GetDirectoryName(strmPath);
        var baseName = Path.GetFileNameWithoutExtension(strmPath);

        if (dir is null) return;

        TryDelete(strmPath);

        // Prefix match instead of a search pattern, and including the "-thumb.jpg" the sync writes
        var sidecars = Directory.EnumerateFiles(dir)
            .Where(f =>
            {
                var name = Path.GetFileName(f);
                return name.StartsWith(baseName + ".", StringComparison.Ordinal)
                    || name.StartsWith(baseName + "-thumb.", StringComparison.Ordinal);
            })
            .ToList();

        foreach (var sidecar in sidecars)
        {
            if (!string.Equals(sidecar, strmPath, StringComparison.OrdinalIgnoreCase))
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
