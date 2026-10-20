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
    private readonly DeletedVideoList _deleted;
    private readonly ILibraryManager _library;
    private readonly ILogger<ChannelSyncTask> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ChannelSyncTask"/> class.
    /// </summary>
    public ChannelSyncTask(
        YouTubeApiService youtube,
        StrmService strm,
        DeletedVideoList deleted,
        ILibraryManager library,
        ILogger<ChannelSyncTask> logger)
    {
        _youtube = youtube;
        _strm    = strm;
        _deleted = deleted;
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

        // Channel names come from the API key lookup, not the subscriptions: the OAuth token of a
        // Google app in testing mode expires after 7 days, which used to rename folders to channel IDs.
        var deletedIds = _deleted.Load();
        var total      = config.SyncedChannelIds.Length;
        var done       = 0;
        var created    = 0;

        foreach (var channelId in config.SyncedChannelIds)
        {
            if (ct.IsCancellationRequested) break;

            try
            {
                var (channelName, videos) = await _youtube.GetChannelVideosAsync(channelId, config.MaxVideosPerChannel, ct);
                if (string.IsNullOrWhiteSpace(channelName))
                {
                    _logger.LogWarning("Skipping channel {ChannelId}: channel could not be looked up (API key or quota?).", channelId);
                }
                else
                {
                    var added = await _strm.SyncChannelAsync(channelName, channelId, videos, deletedIds, ct);
                    created += added;
                    _logger.LogInformation("{Name}: {Added} new of {Count} videos.", channelName, added, videos.Count);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Sync failed for channel {ChannelId}", channelId);
            }

            progress.Report(++done * 100.0 / total);
        }

        _logger.LogInformation("JellyTubbing sync finished: {Count} new video(s).", created);

        // Trigger library scan so Jellyfin picks up the new STRM files
        if (created > 0)
            _library.QueueLibraryScan();
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
                // Both would read every video and so download all of them into the cache
                EnableTrickplayImageExtraction         = false,
                ExtractTrickplayImagesDuringLibraryScan = false,
                EnableChapterImageExtraction           = false,
                ExtractChapterImagesDuringLibraryScan  = false,
                TypeOptions =
                [
                    new TypeOptions
                    {
                        Type                  = "Movie",
                        MetadataFetchers      = ["Nfo"],
                        MetadataFetcherOrder  = ["Nfo"],
                        // No "Image Extractor": grabbing a frame would download every video
                        // into the cache. The sync writes a -thumb.jpg instead.
                        ImageFetchers         = [],
                        ImageFetcherOrder     = [],
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
