using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyTube.Services;

/// <summary>
/// Downloads video thumbnails and channel poster images to disk.
/// </summary>
public class ThumbnailService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<ThumbnailService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ThumbnailService"/> class.
    /// </summary>
    public ThumbnailService(IHttpClientFactory httpClientFactory, ILogger<ThumbnailService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <summary>
    /// Downloads an image from <paramref name="url"/> and saves it to <paramref name="destPath"/>.
    /// Failures are logged as warnings and do not throw.
    /// </summary>
    public async Task DownloadThumbnailAsync(string url, string destPath, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        var client = _httpClientFactory.CreateClient("thumbnail");

        foreach (var candidate in BuildThumbnailCandidates(url))
        {
            try
            {
                var bytes = await client.GetByteArrayAsync(candidate, ct);
                Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
                await File.WriteAllBytesAsync(destPath, bytes, ct);
                _logger.LogInformation("Thumbnail saved to {Path}", destPath);
                return;
            }
            catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                continue;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to download thumbnail from {Url}", candidate);
                return;
            }
        }

        _logger.LogWarning("No thumbnail variant could be downloaded for {Url}", url);
    }

    private static System.Collections.Generic.IEnumerable<string> BuildThumbnailCandidates(string url)
    {
        yield return url;

        // YouTube localized thumbnails like ".../maxresdefault_en-US.jpg" often 404.
        // Fall back to the non-localized variant.
        var localizedMatch = System.Text.RegularExpressions.Regex.Match(
            url, @"^(.*?)(_[a-z]{2}-[A-Z]{2})(\.\w+)$");
        if (localizedMatch.Success)
            yield return localizedMatch.Groups[1].Value + localizedMatch.Groups[3].Value;

        // Final fallback: hqdefault is always available on YouTube
        var ytIdMatch = System.Text.RegularExpressions.Regex.Match(
            url, @"i\.ytimg\.com/vi(?:_lc)?/([a-zA-Z0-9_-]+)/");
        if (ytIdMatch.Success)
            yield return $"https://i.ytimg.com/vi/{ytIdMatch.Groups[1].Value}/hqdefault.jpg";
    }

    /// <summary>
    /// Downloads <paramref name="thumbnailUrl"/> as <c>poster.jpg</c> inside <paramref name="channelDir"/>
    /// only if no poster already exists.
    /// </summary>
    public async Task EnsureChannelPosterAsync(string channelDir, string thumbnailUrl, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(thumbnailUrl))
        {
            return;
        }

        var posterPath = Path.Combine(channelDir, "poster.jpg");
        if (!File.Exists(posterPath))
        {
            await DownloadThumbnailAsync(thumbnailUrl, posterPath, ct);
        }
    }
}
