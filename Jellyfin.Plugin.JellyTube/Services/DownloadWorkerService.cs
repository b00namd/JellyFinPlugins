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
/// Background service that processes download jobs immediately when they are enqueued.
/// </summary>
public class DownloadWorkerService : BackgroundService
{
    private readonly DownloadQueueService _queue;
    private readonly YtDlpService _ytDlp;
    private readonly NfoWriterService _nfo;
    private readonly ThumbnailService _thumbs;
    private readonly LibraryOrganizationService _library;
    private readonly DownloadArchiveService _archive;
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<DownloadWorkerService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="DownloadWorkerService"/> class.
    /// </summary>
    public DownloadWorkerService(
        DownloadQueueService queue,
        YtDlpService ytDlp,
        NfoWriterService nfo,
        ThumbnailService thumbs,
        LibraryOrganizationService library,
        DownloadArchiveService archive,
        ILibraryManager libraryManager,
        ILogger<DownloadWorkerService> logger)
    {
        _queue = queue;
        _ytDlp = ytDlp;
        _nfo = nfo;
        _thumbs = thumbs;
        _library = library;
        _archive = archive;
        _libraryManager = libraryManager;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Download worker service started.");

        var semaphore = new SemaphoreSlim(
            Math.Max(1, Plugin.Instance?.Configuration.MaxConcurrentDownloads ?? 1));

        await foreach (var jobId in _queue.Reader.ReadAllAsync(stoppingToken))
        {
            var job = _queue.GetJob(jobId);
            if (job is null || job.Status != DownloadJobStatus.Queued)
            {
                continue;
            }

            await semaphore.WaitAsync(stoppingToken);

            var capturedJob = job;
            _ = Task.Run(async () =>
            {
                try
                {
                    await ProcessJobAsync(capturedJob, stoppingToken);
                }
                finally
                {
                    semaphore.Release();
                }
            }, stoppingToken);
        }
    }

    internal async Task ProcessJobAsync(DownloadJob job, CancellationToken ct)
    {
        _logger.LogInformation("Processing job {Id}: {Url}", job.Id, job.Url);

        // Step 1 – fetch metadata
        job.Status = DownloadJobStatus.FetchingMetadata;
        var meta = await _ytDlp.FetchMetadataAsync(job.Url, ct);

        if (meta is null)
        {
            job.Status = DownloadJobStatus.Failed;
            job.ErrorMessage = "Metadaten konnten nicht abgerufen werden.";
            job.CompletedAt = DateTime.UtcNow;
            _logger.LogWarning("Job {Id} failed at metadata step.", job.Id);
            return;
        }

        job.Metadata = meta;

        // Step 2 – determine output directory and download
        var outputDir = _library.GetVideoDirectory(meta, job.OverrideDownloadPath);
        Directory.CreateDirectory(outputDir);

        job.Status = DownloadJobStatus.Downloading;

        var downloadProgress = new Progress<YoutubeDLSharp.DownloadProgress>(dp =>
        {
            job.ProgressPercent = dp.Progress;
            job.CurrentFile = dp.Data;
        });

        var archivePath = job.IsScheduled ? _archive.ArchivePath : null;

        bool success = job.IsPlaylist
            ? await _ytDlp.DownloadPlaylistAsync(job.Url, outputDir, downloadProgress, ct, archivePath)
            : await _ytDlp.DownloadVideoAsync(job.Url, outputDir, downloadProgress, ct, archivePath);

        if (!success)
        {
            job.Status = DownloadJobStatus.Failed;
            job.ErrorMessage = "yt-dlp hat einen Fehler gemeldet.";
            job.CompletedAt = DateTime.UtcNow;
            _logger.LogWarning("Job {Id} failed during download.", job.Id);
            return;
        }

        // Step 3 – write NFO and thumbnails
        job.Status = DownloadJobStatus.WritingMetadata;

        var config = Plugin.Instance!.Configuration;

        if (config.WriteNfoFiles || config.DownloadThumbnails)
        {
            var videoFile = LocateDownloadedFile(outputDir, meta.VideoId);

            if (videoFile is not null)
            {
                job.DownloadedFilePath = videoFile;

                if (config.WriteNfoFiles)
                {
                    var nfoPath = LibraryOrganizationService.GetNfoPath(videoFile);
                    await _nfo.WriteNfoAsync(meta, nfoPath);
                }

                if (config.DownloadThumbnails && !string.IsNullOrEmpty(meta.ThumbnailUrl))
                {
                    var thumbPath = LibraryOrganizationService.GetThumbnailPath(videoFile);
                    await _thumbs.DownloadThumbnailAsync(meta.ThumbnailUrl, thumbPath, ct);
                    await _thumbs.EnsureChannelPosterAsync(outputDir, meta.ThumbnailUrl, ct);
                }
            }
            else
            {
                _logger.LogWarning("Job {Id}: downloaded file not found in {Dir} for video {VideoId}.",
                    job.Id, outputDir, meta.VideoId);
            }
        }

        job.Status = DownloadJobStatus.Completed;
        job.ProgressPercent = 100;
        job.CompletedAt = DateTime.UtcNow;
        _logger.LogInformation("Job {Id} completed successfully.", job.Id);

        if (config.TriggerLibraryScanAfterDownload)
        {
            _libraryManager.QueueLibraryScan();
        }
    }

    private static string? LocateDownloadedFile(string dir, string videoId)
    {
        if (string.IsNullOrWhiteSpace(videoId))
        {
            return null;
        }

        return Directory.EnumerateFiles(dir, $"*{videoId}*")
            .FirstOrDefault(f =>
                f.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) ||
                f.EndsWith(".mkv", StringComparison.OrdinalIgnoreCase) ||
                f.EndsWith(".webm", StringComparison.OrdinalIgnoreCase) ||
                f.EndsWith(".avi", StringComparison.OrdinalIgnoreCase));
    }
}
