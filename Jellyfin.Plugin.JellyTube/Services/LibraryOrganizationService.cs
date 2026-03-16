using System.IO;
using Jellyfin.Plugin.JellyTube.Helpers;
using Jellyfin.Plugin.JellyTube.Models;

namespace Jellyfin.Plugin.JellyTube.Services;

/// <summary>
/// Determines the on-disk folder layout for downloaded videos.
/// </summary>
public class LibraryOrganizationService
{
    /// <summary>
    /// Returns the directory path where a video should be saved.
    /// </summary>
    /// <param name="meta">Video metadata.</param>
    /// <param name="overrideBasePath">Optional base path override. Falls back to the global DownloadPath when null or empty.</param>
    public string GetVideoDirectory(VideoMetadata meta, string? overrideBasePath = null)
    {
        var config = Plugin.Instance!.Configuration;
        var basePath = string.IsNullOrWhiteSpace(overrideBasePath) ? config.DownloadPath : overrideBasePath;

        if (!config.OrganiseByChannel || string.IsNullOrWhiteSpace(meta.ChannelName))
        {
            return basePath;
        }

        var channelFolder = PathSanitizer.Sanitize(meta.ChannelName);

        if (!string.IsNullOrWhiteSpace(meta.PlaylistTitle))
        {
            var playlistFolder = PathSanitizer.Sanitize(meta.PlaylistTitle);
            return Path.Combine(basePath, channelFolder, playlistFolder);
        }

        return Path.Combine(basePath, channelFolder);
    }

    /// <summary>
    /// Returns the .nfo file path that corresponds to a given video file path.
    /// </summary>
    public static string GetNfoPath(string videoFilePath)
        => Path.ChangeExtension(videoFilePath, ".nfo");

    /// <summary>
    /// Returns the thumbnail path that corresponds to a given video file path.
    /// Jellyfin picks up <c>{name}-thumb.jpg</c> as an episode/item thumbnail.
    /// </summary>
    public static string GetThumbnailPath(string videoFilePath, string extension = "jpg")
    {
        var dir = Path.GetDirectoryName(videoFilePath)!;
        var stem = Path.GetFileNameWithoutExtension(videoFilePath);
        return Path.Combine(dir, $"{stem}-thumb.{extension}");
    }
}
