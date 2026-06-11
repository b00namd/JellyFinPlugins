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

        // Force=true bypasses the archive so an already-downloaded video can be fetched again.
        var archivePath = (job.IsScheduled && !job.Force) ? _archive.ArchivePath : null;
        if (job.Force && job.Metadata?.VideoId is { Length: > 0 } forceId)
        {
            _archive.Remove(forceId);
        }
        // Retries are per-video. For a whole-playlist/channel job a "failure" usually means a few
        // individual videos were unavailable (members-only, deleted) — --ignore-errors already let
        // the rest through, so retrying would just re-scan the entire channel for no gain. The next
        // scheduled run picks up anything still missing anyway.
        var maxAttempts = job.IsPlaylist ? 1 : Math.Max(1, 1 + Plugin.Instance!.Configuration.DownloadRetryCount);
        bool success = false;

        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            if (attempt > 1)
            {
                var backoff = TimeSpan.FromSeconds(Math.Min(30, 5 * (attempt - 1)));
                _logger.LogInformation("Job {Id}: retry attempt {Attempt}/{Max} after {Delay}s.",
                    job.Id, attempt, maxAttempts, backoff.TotalSeconds);
                try { await Task.Delay(backoff, ct); }
                catch (OperationCanceledException) { break; }
            }

            success = job.IsPlaylist
                ? await _ytDlp.DownloadPlaylistAsync(job.Url, outputDir, downloadProgress, ct, archivePath, job.MaxAgeDays, job.IsScheduled)
                : await _ytDlp.DownloadVideoAsync(job.Url, outputDir, downloadProgress, ct, archivePath);

            if (success || ct.IsCancellationRequested)
                break;
        }

        if (!success && !job.IsPlaylist)
        {
            job.Status = DownloadJobStatus.Failed;
            job.ErrorMessage = $"yt-dlp hat einen Fehler gemeldet (nach {maxAttempts} Versuch(en)).";
            job.CompletedAt = DateTime.UtcNow;
            _logger.LogWarning("Job {Id} failed during download after {Attempts} attempt(s).", job.Id, maxAttempts);
            return;
        }

        if (!success)
        {
            _logger.LogWarning("Job {Id}: playlist download reported errors (some videos may be unavailable). Writing metadata for successful downloads.", job.Id);
        }

        CleanupJobIntermediateFiles(job, outputDir);

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
            // meta is guaranteed non-null here: the non-playlist branch above fetched it
            // and returned early on null. Capture into a non-nullable local.
            var videoMeta = meta!;
            var videoFile = LocateDownloadedFile(outputDir, videoMeta.VideoId);

            if (videoFile is not null)
            {
                if (shouldWriteDeleteMarker)
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
                    await _thumbs.DownloadThumbnailAsync(videoMeta.ThumbnailUrl, thumbPath, ct);
                    await _thumbs.EnsureChannelPosterAsync(outputDir, videoMeta.ThumbnailUrl, ct);
                }

                videoFile = RenameToCleanTitle(videoFile, videoMeta.VideoId) ?? videoFile;
                job.DownloadedFilePath = videoFile;
            }
            else
            {
                _logger.LogWarning("Job {Id}: downloaded file not found in {Dir} for video {VideoId}.",
                    job.Id, outputDir, videoMeta.VideoId);
            }
        }

        job.Status = success ? DownloadJobStatus.Completed : DownloadJobStatus.CompletedWithErrors;
        job.ProgressPercent = 100;
        job.CompletedAt = DateTime.UtcNow;
        if (success)
            _logger.LogInformation("Job {Id} completed successfully.", job.Id);
        else
            _logger.LogInformation("Job {Id} completed with errors (some videos failed).", job.Id);

        // Always trigger a scan: file rename invalidates Jellyfin's cached media info,
        // so without a scan the UI shows stale resolution/codec/etc.
        _libraryManager.QueueLibraryScan();
    }

    private async Task WritePlaylistMetadataAsync(string outputDir, bool writeDeleteMarker, CancellationToken ct)
    {
        var config = Plugin.Instance!.Configuration;
        string? lastThumbnailUrl = null;

        var organiseInSubfolders = config.OrganiseByChannel || config.OrganiseAsSeries;
        var searchOption = organiseInSubfolders ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        var infoJsonFiles = Directory.EnumerateFiles(outputDir, "*.info.json", searchOption).ToList();

        // For series mode, remember a channel's folder + URL so real channel artwork can be fetched once.
        (string Dir, string ChannelUrl)? seriesArtwork = null;
        foreach (var jsonPath in infoJsonFiles)
        {
            var videoMeta = await ParseInfoJsonAsync(jsonPath, ct);
            if (videoMeta is null || string.IsNullOrEmpty(videoMeta.VideoId))
                continue;

            var videoDir = Path.GetDirectoryName(jsonPath) ?? outputDir;

            // Channel-level series metadata (tvshow.nfo + poster/banner) depends only on the
            // info.json, so handle it even for videos already renamed to a clean title — their files
            // no longer contain the id and so can't be re-located below, which would otherwise skip
            // them entirely on re-runs.
            if (config.OrganiseAsSeries && config.WriteNfoFiles)
            {
                var seriesDir = Directory.GetParent(videoDir)?.FullName ?? videoDir;
                await _nfo.EnsureTvShowNfoAsync(seriesDir, videoMeta.ChannelName, videoMeta.ChannelId, videoMeta.ThumbnailUrl);

                if (config.DownloadThumbnails)
                {
                    var channelUrl = !string.IsNullOrWhiteSpace(videoMeta.UploaderUrl)
                        ? videoMeta.UploaderUrl
                        : (!string.IsNullOrWhiteSpace(videoMeta.ChannelId) ? $"https://www.youtube.com/channel/{videoMeta.ChannelId}" : null);
                    if (!string.IsNullOrWhiteSpace(channelUrl))
                        seriesArtwork = (seriesDir, channelUrl);
                }
            }

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
                if (config.OrganiseAsSeries)
                    await _nfo.WriteEpisodeNfoAsync(videoMeta, nfoPath);
                else
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

        // Non-series channel poster (series posters are written per channel folder above).
        if (!config.OrganiseAsSeries && config.DownloadThumbnails && lastThumbnailUrl is not null)
            await _thumbs.EnsureChannelPosterAsync(outputDir, lastThumbnailUrl, ct);

        // Series: real channel logo as poster + banner as backdrop.
        if (config.OrganiseAsSeries && config.DownloadThumbnails && seriesArtwork is { } art)
            await EnsureSeriesArtworkAsync(art.Dir, art.ChannelUrl, ct);
    }

    /// <summary>
    /// Writes the channel's logo as <c>poster.jpg</c> and its banner as <c>backdrop.jpg</c> in the
    /// series folder, refreshed at most every 30 days (channel art rarely changes).
    /// </summary>
    private async Task EnsureSeriesArtworkAsync(string seriesDir, string channelUrl, CancellationToken ct)
    {
        var posterPath = Path.Combine(seriesDir, "poster.jpg");
        if (File.Exists(posterPath) && (DateTime.UtcNow - File.GetLastWriteTimeUtc(posterPath)) < TimeSpan.FromDays(30))
            return;

        var (avatar, banner) = await _ytDlp.FetchChannelArtworkAsync(channelUrl, ct);
        if (!string.IsNullOrWhiteSpace(avatar))
            await _thumbs.DownloadThumbnailAsync(avatar, posterPath, ct);
        if (!string.IsNullOrWhiteSpace(banner))
            await _thumbs.DownloadThumbnailAsync(banner, Path.Combine(seriesDir, "backdrop.jpg"), ct);
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

    /// <summary>
    /// Cleans yt-dlp intermediates for a finished job, scoped so concurrent channel downloads
    /// never touch each other's in-progress files.
    /// </summary>
    private void CleanupJobIntermediateFiles(DownloadJob job, string outputDir)
    {
        var config = Plugin.Instance!.Configuration;

        // Playlist + per-channel folders: videos land in subfolders. Only clean the subfolders THIS
        // job actually wrote to (something modified since it started), so we never reach into a
        // channel another concurrent job is still downloading. Single-video and flat-playlist jobs
        // already point at one specific folder, so cleaning it directly (non-recursive) is safe.
        if (job.IsPlaylist && config.OrganiseByChannel && Directory.Exists(outputDir))
        {
            var since = job.StartedAt ?? DateTime.MinValue;
            foreach (var subdir in Directory.EnumerateDirectories(outputDir))
            {
                try
                {
                    if (Directory.EnumerateFiles(subdir).Any(f => File.GetLastWriteTimeUtc(f) >= since))
                        CleanupIntermediateFiles(subdir, recursive: false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Could not scan '{Dir}' for intermediate cleanup.", subdir);
                }
            }
        }
        else
        {
            CleanupIntermediateFiles(outputDir, recursive: false);
        }
    }

    private void CleanupIntermediateFiles(string dir, bool recursive)
    {
        if (!Directory.Exists(dir))
            return;

        // yt-dlp leaves these behind:
        //   *.f<formatId>.<ext>      stream-specific intermediates (audio/video only)
        //   *.f<formatId>.<ext>.part interrupted-download partial files
        //   *.part                   yt-dlp partial downloads
        //   *.temp.<ext>             intermediate during merge/postprocessing
        // This runs recursively over the whole download tree, so when several channels download
        // concurrently (MaxConcurrentDownloads > 1) one job's cleanup can see another job's
        // in-progress fragments. Skip anything modified very recently so we don't delete a file
        // another yt-dlp process is still writing (which caused "Unable to rename .part" errors).
        var inProgressCutoff = DateTime.UtcNow.AddMinutes(-15);
        var searchOption = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        int deleted = 0;
        foreach (var file in Directory.EnumerateFiles(dir, "*.*", searchOption).ToList())
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
                // Leave recently-touched fragments alone — a concurrent download likely owns them.
                if (File.GetLastWriteTimeUtc(file) > inProgressCutoff)
                    continue;

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
                        !f.EndsWith(".part", StringComparison.OrdinalIgnoreCase) &&
                        !IsStreamFragment(f))
            .FirstOrDefault(f =>
                f.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) ||
                f.EndsWith(".mkv", StringComparison.OrdinalIgnoreCase) ||
                f.EndsWith(".webm", StringComparison.OrdinalIgnoreCase) ||
                f.EndsWith(".avi", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// True if the file is a yt-dlp stream-specific fragment like "name.f400.mp4" (audio- or
    /// video-only, before merge). These must never be mistaken for the finished video — doing so
    /// makes the plugin write metadata and mark a video as downloaded when the merged file failed,
    /// leaving a stale download-archive entry that blocks re-downloading.
    /// </summary>
    private static bool IsStreamFragment(string path)
    {
        var name = Path.GetFileName(path);
        var parts = name.Split('.');
        return parts.Length >= 3 && parts[^2].StartsWith('f') && int.TryParse(parts[^2][1..], out _);
    }
}
