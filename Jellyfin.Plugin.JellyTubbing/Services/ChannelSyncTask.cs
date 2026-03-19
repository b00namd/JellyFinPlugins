using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyTubbing.Services;

/// <summary>
/// Jellyfin scheduled task that syncs subscribed YouTube channels to STRM files.
/// Appears under Administration → Scheduled Tasks → JellyTubbing.
/// </summary>
public class ChannelSyncTask : IScheduledTask
{
    private readonly YouTubeApiService _youtube;
    private readonly StrmService _strm;
    private readonly ILibraryManager _library;
    private readonly ILogger<ChannelSyncTask> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ChannelSyncTask"/> class.
    /// </summary>
    public ChannelSyncTask(
        YouTubeApiService youtube,
        StrmService strm,
        ILibraryManager library,
        ILogger<ChannelSyncTask> logger)
    {
        _youtube = youtube;
        _strm    = strm;
        _library = library;
        _logger  = logger;
    }

    /// <inheritdoc />
    public string Name => "Kanal-Synchronisation";

    /// <inheritdoc />
    public string Key => "JellyTubbingChannelSync";

    /// <inheritdoc />
    public string Description => "Synchronisiert abonnierte YouTube-Kanaele als STRM-Dateien in die Jellyfin-Bibliothek.";

    /// <inheritdoc />
    public string Category => "JellyTubbing";

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        yield return new TaskTriggerInfo
        {
            Type          = TaskTriggerInfo.TriggerInterval,
            IntervalTicks = TimeSpan.FromHours(24).Ticks,
        };
    }

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken ct)
    {
        var config = Plugin.Instance?.Configuration;
        if (config is null || config.SyncedChannelIds.Length == 0)
        {
            _logger.LogDebug("JellyTubbing sync: no channels configured.");
            progress.Report(100);
            return;
        }

        if (string.IsNullOrWhiteSpace(config.StrmOutputPath))
        {
            _logger.LogWarning("JellyTubbing sync: STRM output path not configured.");
            progress.Report(100);
            return;
        }

        EnsureLibraryExists(config.StrmOutputPath);

        _logger.LogInformation("JellyTubbing sync started for {Count} channel(s).", config.SyncedChannelIds.Length);

        var subs   = await _youtube.GetSubscriptionsAsync(ct);
        var subMap = subs.ToDictionary(
            s => s.Snippet.ResourceId.ChannelId,
            s => s.Snippet.Title);

        var total = config.SyncedChannelIds.Length;
        var done  = 0;

        foreach (var channelId in config.SyncedChannelIds)
        {
            if (ct.IsCancellationRequested) break;

            var channelName = subMap.TryGetValue(channelId, out var n) ? n : channelId;
            try
            {
                var videos = await _youtube.GetChannelVideosAsync(channelId, config.MaxVideosPerChannel, ct);
                foreach (var (videoId, snippet, duration) in videos)
                {
                    await _strm.CreateVideoFilesAsync(
                        channelName,
                        channelId,
                        videoId,
                        snippet.Title,
                        snippet.Description,
                        snippet.PublishedAt,
                        snippet.Thumbnails.BestUrl,
                        (int)duration.TotalSeconds,
                        ct);
                }

                _logger.LogInformation("Synced {Count} videos for {Name}.", videos.Count, channelName);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Sync failed for channel {ChannelId}", channelId);
            }

            progress.Report(++done * 100.0 / total);
        }

        _logger.LogInformation("JellyTubbing sync finished.");

        // Trigger library scan so Jellyfin picks up the new STRM files
        _ = _library.ValidateMediaLibrary(new Progress<double>(), CancellationToken.None);
    }

    private void EnsureLibraryExists(string strmPath)
    {
        try
        {
            var folders = _library.GetVirtualFolders();
            var alreadyLinked = folders.Any(f =>
                f.Locations != null &&
                f.Locations.Any(l => string.Equals(l, strmPath, StringComparison.OrdinalIgnoreCase)));

            if (alreadyLinked)
            {
                _logger.LogDebug("JellyTubbing: Library for '{Path}' already exists.", strmPath);
                return;
            }

            _logger.LogInformation("JellyTubbing: Creating library 'JellyTubbing' at '{Path}'.", strmPath);
            var opts = new LibraryOptions
            {
                PathInfos = [new MediaPathInfo { Path = strmPath }],
                SaveLocalMetadata = true,
                TypeOptions =
                [
                    new TypeOptions
                    {
                        Type                  = "Movie",
                        MetadataFetchers      = ["Nfo"],
                        MetadataFetcherOrder  = ["Nfo"],
                        ImageFetchers         = ["Image Extractor"],
                        ImageFetcherOrder     = ["Image Extractor"],
                    }
                ]
            };
            _library.AddVirtualFolder("JellyTubbing", CollectionTypeOptions.homevideos, opts, refreshLibrary: false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "JellyTubbing: Could not auto-create library – add '{Path}' manually.", strmPath);
        }
    }
}
