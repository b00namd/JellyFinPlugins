using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Channels;
using Jellyfin.Plugin.JellyTube.Models;

namespace Jellyfin.Plugin.JellyTube.Services;

/// <summary>
/// Thread-safe in-memory queue that manages download jobs.
/// </summary>
public class DownloadQueueService
{
    private readonly ConcurrentDictionary<Guid, DownloadJob> _jobs = new();
    private readonly Channel<Guid> _workChannel =
        Channel.CreateUnbounded<Guid>(new UnboundedChannelOptions { SingleReader = false });

    /// <summary>
    /// Gets the channel reader used by worker tasks to receive job IDs.
    /// </summary>
    public System.Threading.Channels.ChannelReader<Guid> Reader => _workChannel.Reader;

    /// <summary>
    /// Enqueues a new download job and returns it.
    /// </summary>
    public DownloadJob Enqueue(string url, bool isPlaylist = false, bool isScheduled = false, string? overrideDownloadPath = null)
    {
        var job = new DownloadJob
        {
            Url = url,
            IsPlaylist = isPlaylist,
            IsScheduled = isScheduled,
            OverrideDownloadPath = string.IsNullOrWhiteSpace(overrideDownloadPath) ? null : overrideDownloadPath
        };
        _jobs[job.Id] = job;
        _workChannel.Writer.TryWrite(job.Id);
        return job;
    }

    /// <summary>
    /// Returns a job by its ID, or <c>null</c> if not found.
    /// </summary>
    public DownloadJob? GetJob(Guid id) =>
        _jobs.TryGetValue(id, out var job) ? job : null;

    /// <summary>
    /// Returns all jobs ordered by creation time (newest first).
    /// </summary>
    public IReadOnlyList<DownloadJob> GetAllJobs() =>
        _jobs.Values.OrderByDescending(j => j.CreatedAt).ToList();

    /// <summary>
    /// Marks a queued or downloading job as cancelled.
    /// </summary>
    public void Cancel(Guid id)
    {
        if (_jobs.TryGetValue(id, out var job)
            && job.Status is DownloadJobStatus.Queued or DownloadJobStatus.FetchingMetadata)
        {
            job.Status = DownloadJobStatus.Cancelled;
        }
    }

    /// <summary>
    /// Removes completed, failed, or cancelled jobs older than the given age from the in-memory list.
    /// </summary>
    public void PruneOldJobs(TimeSpan maxAge)
    {
        var cutoff = DateTime.UtcNow - maxAge;
        foreach (var kvp in _jobs)
        {
            var job = kvp.Value;
            if (job.Status is DownloadJobStatus.Completed or DownloadJobStatus.Failed or DownloadJobStatus.Cancelled
                && job.CompletedAt.HasValue
                && job.CompletedAt.Value < cutoff)
            {
                _jobs.TryRemove(kvp.Key, out _);
            }
        }
    }
}
