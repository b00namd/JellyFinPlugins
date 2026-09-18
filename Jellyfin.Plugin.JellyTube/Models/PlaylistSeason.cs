namespace Jellyfin.Plugin.JellyTube.Models;

/// <summary>
/// Target season for a manually downloaded playlist in series mode: every playlist becomes its own
/// "Season N" folder inside the series folder.
/// </summary>
/// <param name="Directory">Absolute path of the "Season N" folder.</param>
/// <param name="Number">The season number.</param>
public record PlaylistSeason(string Directory, int Number)
{
    /// <summary>
    /// Name of the per-season yt-dlp download archive (a dotfile, so Jellyfin ignores it).
    /// </summary>
    public const string ArchiveFileName = ".jellytube-archive.txt";
}

/// <summary>
/// Basic playlist information fetched before a download.
/// </summary>
/// <param name="Id">YouTube playlist ID.</param>
/// <param name="Title">Playlist title.</param>
/// <param name="Channel">Name of the channel that owns the playlist, if known.</param>
public record PlaylistInfo(string Id, string Title, string? Channel);
