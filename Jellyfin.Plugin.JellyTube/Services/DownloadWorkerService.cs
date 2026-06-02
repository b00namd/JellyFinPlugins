using System;
using System.Diagnostics;
using System.Globalization;
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
    public override Task StartAsync(CancellationToken cancellationToken)
    {
        KillOrphanedYtDlpProcesses();
        return base.StartAsync(cancellationToken);
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

        job.StartedAt = DateTime.UtcNow;

        // Step 1 – fetch metadata (single videos only; playlists write per-video .info.json during download)
        VideoMetadata? meta = null;
        string outputDir;

        if (job.IsPlaylist)
        {
            job.Status = DownloadJobStatus.Downloading;
            outputDir = _library.GetPlaylistDirectory(job.OverrideDownloadPath);
        }
        else
        {
            job.Status = DownloadJobStatus.FetchingMetadata;
            meta = await _ytDlp.FetchMetadataAsync(job.Url, ct);

            if (meta is null)
            {
                job.Status = DownloadJobStatus.Failed;
                job.ErrorMessage = "Metadaten konnten nicht abgerufen werden.";
                job.CompletedAt = DateTime.UtcNow;
                _logger.LogWarning("Job {Id} failed at metadata step.", job.Id);
                return;
            }

            job.Metadata = meta;
            outputDir = _library.GetVideoDirectory(meta, job.OverrideDownloadPath);
        }
        Directory.CreateDirectory(outputDir);

        job.Status = DownloadJobStatus.Downloading;
        var downloadProgress = new Progress<YoutubeDLSharp.DownloadProgress>(dp =>
        {
            job.ProgressPercent = dp.Progress;
            job.CurrentFile = dp.Data;
        });

        var archivePath = job.IsScheduled ? _archive.ArchivePath : null;

        bool success = job.IsPlaylist
            ? await _ytDlp.DownloadPlaylistAsync(job.Url, outputDir, downloadProgress, ct, archivePath, job.MaxAgeDays, job.IsScheduled)
            : await _ytDlp.DownloadVideoAsync(job.Url, outputDir, downloadProgress, ct, archivePath);

        if (!success && !job.IsPlaylist)
        {
            job.Status = DownloadJobStatus.Failed;
            job.ErrorMessage = "yt-dlp hat einen Fehler gemeldet.";
            job.CompletedAt = DateTime.UtcNow;
            _logger.LogWarning("Job {Id} failed during download.", job.Id);
            return;
        }

        if (!success)
        {
            _logger.LogWarning("Job {Id}: playlist download reported errors (some videos may be unavailable). Writing metadata for successful downloads.", job.Id);
        }

        CleanupIntermediateFiles(outputDir);

        // Step 3 – write NFO and thumbnails
        job.Status = DownloadJobStatus.WritingMetadata;

        var config = Plugin.Instance!.Configuration;

        var shouldWriteDeleteMarker = job.IsScheduled
            ? (job.DeleteWatched || config.DeleteWatchedScheduledVideos)
            : config.DeleteWatchedManualVideos;

        if (job.IsPlaylist)
        {
            await WritePlaylistMetadataAsync(outputDir, shouldWriteDeleteMarker, ct);
        }
        else
        {
            var videoFile = LocateDownloadedFile(outputDir, meta.VideoId);

            if (videoFile is not null)
            {
                if (shouldWriteDeleteMarker)
                {
                    var markerPath = Path.ChangeExtension(videoFile, ".delete-watched");
                    try { await File.WriteAllTextAsync(markerPath, meta.VideoId, ct); }
                    catch (Exception ex) { _logger.LogWarning(ex, "Could not write delete-watched marker for '{Path}'.", videoFile); }
                }

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

                videoFile = RenameToCleanTitle(videoFile, meta.VideoId) ?? videoFile;
                job.DownloadedFilePath = videoFile;
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

        // Always trigger a scan: file rename invalidates Jellyfin's cached media info,
        // so without a scan the UI shows stale resolution/codec/etc.
        _libraryManager.QueueLibraryScan();
    }

    private async Task WritePlaylistMetadataAsync(string outputDir, bool writeDeleteMarker, CancellationToken ct)
    {
        var config = Plugin.Instance!.Configuration;
        string? lastThumbnailUrl = null;

        var searchOption = config.OrganiseByChannel ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        var infoJsonFiles = Directory.EnumerateFiles(outputDir, "*.info.json", searchOption).ToList();
        foreach (var jsonPath in infoJsonFiles)
        {
            var videoMeta = await ParseInfoJsonAsync(jsonPath, ct);
            if (videoMeta is null || string.IsNullOrEmpty(videoMeta.VideoId))
                continue;

            var videoDir = Path.GetDirectoryName(jsonPath) ?? outputDir;
            var videoFile = LocateDownloadedFile(videoDir, videoMeta.VideoId);
            if (videoFile is null)
                continue;

            if (writeDeleteMarker)
            {
                var markerPath = Path.ChangeExtension(videoFile, ".delete-watched");
                try { await File.WriteAllTextAsync(markerPath, videoMeta.VideoId, ct); }
                catch (Exception ex) { _logger.LogWarning(ex, "Could not write delete-watched marker for '{Path}'.", videoFile); }
            }

            if (config.WriteNfoFiles)
            {
                var nfoPath = LibraryOrganizationService.GetNfoPath(videoFile);
                await _nfo.WriteNfoAsync(videoMeta, nfoPath);
            }

            if (config.DownloadThumbnails && !string.IsNullOrEmpty(videoMeta.ThumbnailUrl))
            {
                var thumbPath = LibraryOrganizationService.GetThumbnailPath(videoFile);
                if (!File.Exists(thumbPath))
                    await _thumbs.DownloadThumbnailAsync(videoMeta.ThumbnailUrl, thumbPath, ct);
                lastThumbnailUrl = videoMeta.ThumbnailUrl;
            }

            RenameToCleanTitle(videoFile, videoMeta.VideoId);
        }

        if (config.DownloadThumbnails && lastThumbnailUrl is not null)
            await _thumbs.EnsureChannelPosterAsync(outputDir, lastThumbnailUrl, ct);
    }

    private async Task<VideoMetadata?> ParseInfoJsonAsync(string jsonPath, CancellationToken ct)
    {
        try
        {
            await using var stream = File.OpenRead(jsonPath);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            var root = doc.RootElement;

            string? Str(string key) =>
                root.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

            DateTime? uploadDate = null;
            var udStr = Str("upload_date");
            if (udStr?.Length == 8 &&
                DateTime.TryParseExact(udStr, "yyyyMMdd", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var ud))
                uploadDate = ud;

            return new VideoMetadata
            {
                VideoId         = Str("id") ?? string.Empty,
                Title           = Str("title") ?? string.Empty,
                Description     = Str("description") ?? string.Empty,
                ChannelName     = Str("channel") ?? Str("uploader") ?? string.Empty,
                ChannelId       = Str("channel_id") ?? string.Empty,
                UploaderUrl     = Str("uploader_url") ?? string.Empty,
                UploadDate      = uploadDate,
                DurationSeconds = root.TryGetProperty("duration", out var dur) && dur.ValueKind == JsonValueKind.Number ? dur.GetDouble() : null,
                ViewCount       = root.TryGetProperty("view_count", out var vc) && vc.ValueKind == JsonValueKind.Number ? vc.GetInt64() : null,
                LikeCount       = root.TryGetProperty("like_count", out var lc) && lc.ValueKind == JsonValueKind.Number ? lc.GetInt64() : null,
                ThumbnailUrl    = Str("thumbnail") ?? string.Empty,
                WebpageUrl      = Str("webpage_url") ?? string.Empty,
                Tags            = root.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Array
                    ? tags.EnumerateArray().Select(t => t.GetString() ?? string.Empty).Where(s => s.Length > 0).ToArray()
                    : Array.Empty<string>(),
                Categories      = root.TryGetProperty("categories", out var cats) && cats.ValueKind == JsonValueKind.Array
                    ? cats.EnumerateArray().Select(c => c.GetString() ?? string.Empty).Where(s => s.Length > 0).ToArray()
                    : Array.Empty<string>()
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse info JSON '{Path}'.", jsonPath);
            return null;
        }
    }

    private void KillOrphanedYtDlpProcesses()
    {
        try
        {
            var binaryPath = Plugin.Instance?.Configuration.YtDlpBinaryPath;
            var processName = string.IsNullOrWhiteSpace(binaryPath)
                ? "yt-dlp"
                : Path.GetFileNameWithoutExtension(binaryPath);

            var procs = Process.GetProcessesByName(processName);
            foreach (var proc in procs)
            {
                try
                {
                    proc.Kill(entireProcessTree: true);
                    _logger.LogInformation("Killed orphaned yt-dlp process {Pid}.", proc.Id);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Could not kill yt-dlp process {Pid}.", proc.Id);
                }
                finally
                {
                    proc.Dispose();
                }
            }

            if (procs.Length > 0)
                _logger.LogInformation("Killed {Count} orphaned yt-dlp process(es) on startup.", procs.Length);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error while killing orphaned yt-dlp processes on startup.");
        }
    }

    private void CleanupIntermediateFiles(string dir)
    {
        if (!Directory.Exists(dir))
            return;

        // yt-dlp leaves these behind:
        //   *.f<formatId>.<ext>      stream-specific intermediates (audio/video only)
        //   *.f<formatId>.<ext>.part interrupted-download partial files
        //   *.part                   yt-dlp partial downloads
        //   *.temp.<ext>             intermediate during merge/postprocessing
        int deleted = 0;
        foreach (var file in Directory.EnumerateFiles(dir, "*.*", SearchOption.AllDirectories).ToList())
        {
            var name = Path.GetFileName(file);

            bool shouldDelete = false;

            if (name.EndsWith(".part", StringComparison.OrdinalIgnoreCase))
            {
                shouldDelete = true;
            }
            else if (name.Contains(".temp.", StringComparison.OrdinalIgnoreCase))
            {
                shouldDelete = true;
            }
            else
            {
                var parts = name.Split('.');
                if (parts.Length >= 3 && parts[^2].StartsWith('f') && int.TryParse(parts[^2][1..], out _))
                    shouldDelete = true;
            }

            if (!shouldDelete)
                continue;

            try
            {
                File.Delete(file);
                deleted++;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not delete intermediate file '{Path}'.", file);
            }
        }

        if (deleted > 0)
            _logger.LogInformation("Cleaned up {Count} intermediate file(s) in '{Dir}'.", deleted, dir);
    }

    private string? RenameToCleanTitle(string videoFile, string videoId)
    {
        var dir = Path.GetDirectoryName(videoFile);
        if (dir is null)
            return null;

        var oldStem = Path.GetFileNameWithoutExtension(videoFile);
        var suffix = $" - {videoId}";

        if (!oldStem.EndsWith(suffix, StringComparison.Ordinal))
            return null;

        var cleanStem = oldStem[..^suffix.Length];
        if (string.IsNullOrWhiteSpace(cleanStem))
            return null;

        var ext = Path.GetExtension(videoFile);
        var newVideoPath = Path.Combine(dir, cleanStem + ext);

        if (File.Exists(newVideoPath))
        {
            int counter = 2;
            while (File.Exists(Path.Combine(dir, $"{cleanStem} ({counter}){ext}")))
                counter++;
            cleanStem = $"{cleanStem} ({counter})";
            newVideoPath = Path.Combine(dir, cleanStem + ext);
        }

        try
        {
            foreach (var file in Directory.EnumerateFiles(dir, $"{oldStem}*").ToList())
            {
                var fileName = Path.GetFileName(file);
                var newFileName = cleanStem + fileName[oldStem.Length..];
                File.Move(file, Path.Combine(dir, newFileName));
            }

            _logger.LogInformation("Renamed '{OldStem}' → '{NewStem}'.", oldStem, cleanStem);
            return newVideoPath;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not rename '{Path}' to clean title.", videoFile);
            return null;
        }
    }

    private static string? LocateDownloadedFile(string dir, string videoId)
    {
        if (string.IsNullOrWhiteSpace(videoId))
        {
            return null;
        }

        return Directory.EnumerateFiles(dir, $"*{videoId}*")
            .Where(f => !f.Contains(".temp.", StringComparison.OrdinalIgnoreCase) &&
                        !f.EndsWith(".part", StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault(f =>
                f.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) ||
                f.EndsWith(".mkv", StringComparison.OrdinalIgnoreCase) ||
                f.EndsWith(".webm", StringComparison.OrdinalIgnoreCase) ||
                f.EndsWith(".avi", StringComparison.OrdinalIgnoreCase));
    }
}
