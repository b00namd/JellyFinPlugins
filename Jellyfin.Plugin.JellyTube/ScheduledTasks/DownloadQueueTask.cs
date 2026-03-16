using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyTube.Services;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyTube.ScheduledTasks;

/// <summary>
/// Jellyfin scheduled task that processes the download queue.
/// Visible in the Jellyfin admin panel under Scheduled Tasks → YouTube Downloader.
/// </summary>
public class DownloadQueueTask : IScheduledTask
{
    private readonly DownloadQueueService _queue;
    private readonly ILogger<DownloadQueueTask> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="DownloadQueueTask"/> class.
    /// </summary>
    public DownloadQueueTask(
        DownloadQueueService queue,
        ILogger<DownloadQueueTask> logger)
    {
        _queue = queue;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "Process YouTube Download Queue";

    /// <inheritdoc />
    public string Key => "JellyTubeProcessQueue";

    /// <inheritdoc />
    public string Description => "Downloads queued YouTube videos and writes NFO metadata and thumbnails.";

    /// <inheritdoc />
    public string Category => "JellyTube";

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        return new[]
        {
            new TaskTriggerInfo
            {
                Type = TaskTriggerInfo.TriggerInterval,
                IntervalTicks = TimeSpan.FromHours(1).Ticks
            }
        };
    }

    /// <inheritdoc />
    public Task ExecuteAsync(IProgress<double> progress, CancellationToken ct)
    {
        var config = Plugin.Instance!.Configuration;

        // Enqueue scheduled playlist entries — the background worker will pick them up immediately
        if (config.EnableScheduledDownloads && config.ScheduledEntries.Count > 0)
        {
            foreach (var entry in config.ScheduledEntries)
            {
                var trimmed = entry.Url.Trim();
                if (!string.IsNullOrEmpty(trimmed))
                {
                    var overridePath = string.IsNullOrWhiteSpace(entry.DownloadPath) ? null : entry.DownloadPath;
                    _queue.Enqueue(trimmed, isPlaylist: true, isScheduled: true, overrideDownloadPath: overridePath, maxAgeDays: entry.MaxAgeDays, deleteWatched: entry.DeleteWatched);
                    _logger.LogInformation("Scheduled playlist enqueued: {Url} -> {Path}", trimmed, overridePath ?? "(global)");
                }
            }
        }

        _queue.PruneOldJobs(TimeSpan.FromDays(7));
        progress.Report(100);
        return Task.CompletedTask;
    }
}
