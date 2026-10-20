using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyTubbing.Services;

/// <summary>
/// Remembers the IDs of videos whose STRM files were deleted as watched, so the channel sync
/// does not create them again while they are still among a channel's newest videos.
/// Stored one ID per line in <c>jellytubbing-deleted.txt</c> in the plugin configuration folder.
/// </summary>
public class DeletedVideoList
{
    private readonly object _lock = new();
    private readonly ILogger<DeletedVideoList> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="DeletedVideoList"/> class.
    /// </summary>
    public DeletedVideoList(ILogger<DeletedVideoList> logger)
    {
        _logger = logger;
    }

    private static string FilePath =>
        Path.Combine(Plugin.Instance!.Paths.PluginConfigurationsPath, "jellytubbing-deleted.txt");

    /// <summary>Returns all remembered video IDs.</summary>
    public HashSet<string> Load()
    {
        lock (_lock)
        {
            try
            {
                return File.Exists(FilePath)
                    ? new HashSet<string>(File.ReadAllLines(FilePath), StringComparer.Ordinal)
                    : new HashSet<string>(StringComparer.Ordinal);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not read {Path}.", FilePath);
                return new HashSet<string>(StringComparer.Ordinal);
            }
        }
    }

    /// <summary>Adds a video ID to the list.</summary>
    public void Add(string videoId)
    {
        lock (_lock)
        {
            try
            {
                File.AppendAllLines(FilePath, new[] { videoId });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not add {VideoId} to {Path}.", videoId, FilePath);
            }
        }
    }
}
