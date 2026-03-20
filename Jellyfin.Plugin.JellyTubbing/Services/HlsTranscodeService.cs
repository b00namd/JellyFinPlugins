using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyTubbing.Services;

/// <summary>
/// Manages per-video HLS transcoding sessions for DASH streams (1080p+).
/// ffmpeg merges separate video+audio streams into HLS segments on disk,
/// enabling seeking within the transcoded range.
/// </summary>
public sealed class HlsTranscodeService : IDisposable
{
    private static readonly string TempBase =
        Path.Combine(Path.GetTempPath(), "jellytubbing_hls");

    private readonly ConcurrentDictionary<string, HlsSession> _sessions = new();
    private readonly ILogger<HlsTranscodeService> _logger;

    /// <summary>Initializes a new instance of the <see cref="HlsTranscodeService"/> class.</summary>
    public HlsTranscodeService(ILogger<HlsTranscodeService> logger)
    {
        _logger = logger;
        try { Directory.CreateDirectory(TempBase); }
        catch (Exception ex) { _logger.LogWarning(ex, "Could not create HLS temp dir {Dir}", TempBase); }
    }

    /// <summary>
    /// Ensures a transcoding session exists and has produced at least one segment.
    /// Returns the path to the M3U8 playlist file, or null on timeout/error.
    /// </summary>
    public async Task<string?> EnsureReadyAsync(
        string videoId, string videoUrl, string audioUrl, CancellationToken ct)
    {
        PruneOldSessions();

        var config  = Plugin.Instance!.Configuration;
        var ffmpeg  = string.IsNullOrWhiteSpace(config.FfmpegBinaryPath) ? "ffmpeg" : config.FfmpegBinaryPath;

        var session = _sessions.GetOrAdd(
            videoId,
            id => new HlsSession(id, videoUrl, audioUrl, ffmpeg, TempBase, _logger));

        return await session.WaitReadyAsync(ct);
    }

    /// <summary>Returns the full path to a playlist or segment file, or null if not found.</summary>
    public string? GetFilePath(string videoId, string fileName)
    {
        if (!_sessions.TryGetValue(videoId, out var session)) return null;
        var path = Path.Combine(session.TempDir, SanitizeFileName(fileName));
        return File.Exists(path) ? path : null;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        foreach (var s in _sessions.Values) s.Dispose();
        _sessions.Clear();
    }

    // -------------------------------------------------------------------------

    private void PruneOldSessions()
    {
        var cutoff = DateTime.UtcNow.AddHours(-4);
        foreach (var kv in _sessions)
        {
            if (kv.Value.StartedAt < cutoff && _sessions.TryRemove(kv.Key, out var old))
                old.Dispose();
        }
    }

    private static string SanitizeFileName(string name)
    {
        // Allow only safe characters to prevent path traversal
        return Path.GetFileName(name) ?? string.Empty;
    }
}

// ---------------------------------------------------------------------------

internal sealed class HlsSession : IDisposable
{
    public string   TempDir    { get; }
    public DateTime StartedAt  { get; } = DateTime.UtcNow;

    private readonly string  _videoId;
    private readonly string  _videoUrl;
    private readonly string  _audioUrl;
    private readonly string  _ffmpegBin;
    private readonly string  _playlistPath;
    private readonly ILogger _logger;

    private Process? _proc;
    private int      _startFlag; // 0 = not started, 1 = started

    public HlsSession(
        string videoId, string videoUrl, string audioUrl,
        string ffmpegBin, string tempBase, ILogger logger)
    {
        _videoId      = videoId;
        _videoUrl     = videoUrl;
        _audioUrl     = audioUrl;
        _ffmpegBin    = ffmpegBin;
        _logger       = logger;
        TempDir       = Path.Combine(tempBase, videoId);
        _playlistPath = Path.Combine(TempDir, "index.m3u8");
        Directory.CreateDirectory(TempDir);
    }

    /// <summary>Starts ffmpeg (once) and waits until the first segment is on disk.</summary>
    public async Task<string?> WaitReadyAsync(CancellationToken ct)
    {
        if (Interlocked.Exchange(ref _startFlag, 1) == 0)
            StartFfmpeg();

        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            if (File.Exists(_playlistPath) &&
                Directory.GetFiles(TempDir, "*.ts").Length >= 1)
                return _playlistPath;

            await Task.Delay(300, ct);
        }

        return File.Exists(_playlistPath) ? _playlistPath : null;
    }

    private void StartFfmpeg()
    {
        _logger.LogInformation("HLS: starting ffmpeg for {VideoId} → {Dir}", _videoId, TempDir);

        var psi = new ProcessStartInfo
        {
            FileName               = _ffmpegBin,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true,
        };

        psi.ArgumentList.Add("-hide_banner");
        psi.ArgumentList.Add("-loglevel");           psi.ArgumentList.Add("error");
        psi.ArgumentList.Add("-i");                  psi.ArgumentList.Add(_videoUrl);
        psi.ArgumentList.Add("-i");                  psi.ArgumentList.Add(_audioUrl);
        psi.ArgumentList.Add("-c:v");                psi.ArgumentList.Add("copy");
        psi.ArgumentList.Add("-c:a");                psi.ArgumentList.Add("copy");
        psi.ArgumentList.Add("-f");                  psi.ArgumentList.Add("hls");
        psi.ArgumentList.Add("-hls_time");           psi.ArgumentList.Add("6");
        psi.ArgumentList.Add("-hls_list_size");      psi.ArgumentList.Add("0");
        psi.ArgumentList.Add("-hls_flags");          psi.ArgumentList.Add("independent_segments");
        psi.ArgumentList.Add("-hls_segment_filename");
        psi.ArgumentList.Add(Path.Combine(TempDir, "%05d.ts"));
        psi.ArgumentList.Add(_playlistPath);

        _proc = Process.Start(psi);
    }

    public void Dispose()
    {
        try { if (_proc is { HasExited: false }) _proc.Kill(); } catch { /* ignored */ }
        _proc?.Dispose();
        try { Directory.Delete(TempDir, recursive: true); } catch { /* ignored */ }
    }
}
