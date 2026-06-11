using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyTube.Services;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyTube.ScheduledTasks;

/// <summary>
/// Jellyfin scheduled task that downloads the latest yt-dlp binary into the plugin's writable
/// configuration directory. Runs weekly by default.
/// </summary>
public class UpdateYtDlpTask : IScheduledTask
{
    private readonly ILogger<UpdateYtDlpTask> _logger;
    private readonly IApplicationPaths _appPaths;
    private readonly IHttpClientFactory _httpClientFactory;

    /// <summary>
    /// Initializes a new instance of the <see cref="UpdateYtDlpTask"/> class.
    /// </summary>
    public UpdateYtDlpTask(ILogger<UpdateYtDlpTask> logger, IApplicationPaths appPaths, IHttpClientFactory httpClientFactory)
    {
        _logger = logger;
        _appPaths = appPaths;
        _httpClientFactory = httpClientFactory;
    }

    /// <summary>
    /// Picks the standalone yt-dlp release asset for the current OS/architecture. The standalone
    /// builds bundle Python, so they run in minimal containers that don't ship python3 (unlike the
    /// plain "yt-dlp" zipapp asset, which needs a system Python).
    /// </summary>
    private static string GetReleaseAssetName()
    {
        if (OperatingSystem.IsWindows())
            return "yt-dlp.exe";
        if (OperatingSystem.IsMacOS())
            return "yt-dlp_macos";
        return RuntimeInformation.OSArchitecture == Architecture.Arm64
            ? "yt-dlp_linux_aarch64"
            : "yt-dlp_linux";
    }

    /// <inheritdoc />
    public string Name => "Update yt-dlp Binary";

    /// <inheritdoc />
    public string Key => "JellyTubeUpdateYtDlp";

    /// <inheritdoc />
    public string Description => "Downloads the latest yt-dlp binary so videos can always be fetched.";

    /// <inheritdoc />
    public string Category => "JellyTube";

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        return new[]
        {
            new TaskTriggerInfo
            {
                Type = TaskTriggerInfo.TriggerWeekly,
                DayOfWeek = DayOfWeek.Sunday,
                TimeOfDayTicks = TimeSpan.FromHours(3).Ticks
            }
        };
    }

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken ct)
    {
        // Respect an explicit user-provided binary path — don't touch a binary we don't manage.
        var config = Plugin.Instance!.Configuration;
        if (!string.IsNullOrWhiteSpace(config.YtDlpBinaryPath))
        {
            _logger.LogInformation(
                "Custom yt-dlp binary path is set ({Path}), skipping auto-update.",
                config.YtDlpBinaryPath);
            progress.Report(100);
            return;
        }

        // Download the standalone binary into the plugin's writable configuration directory rather
        // than the default working directory (often read-only in containers, which broke the old
        // update). We fetch the OS/arch-specific standalone asset directly so it works without a
        // system Python.
        var target = YtDlpService.GetManagedYtDlpPath(_appPaths);
        var targetDir = Path.GetDirectoryName(target)!;
        var url = $"https://github.com/yt-dlp/yt-dlp/releases/latest/download/{GetReleaseAssetName()}";
        _logger.LogInformation("Downloading latest yt-dlp ({Asset}) to {Path}…", GetReleaseAssetName(), target);

        try
        {
            Directory.CreateDirectory(targetDir);

            var http = _httpClientFactory.CreateClient();
            http.Timeout = TimeSpan.FromMinutes(5);
            using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();

            // Download to a temp file first, then atomically replace, so a partial download can't
            // leave a broken binary in place.
            var tmp = target + ".new";
            await using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await response.Content.CopyToAsync(fs, ct);
            }

            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(tmp,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                    UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                    UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            }

            File.Move(tmp, target, overwrite: true);
            _logger.LogInformation("yt-dlp binary updated successfully at {Path}.", target);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update yt-dlp binary.");
        }

        progress.Report(100);
    }
}
