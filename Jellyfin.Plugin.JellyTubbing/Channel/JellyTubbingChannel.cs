using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyTubbing.Services;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Channels;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;

namespace Jellyfin.Plugin.JellyTubbing.Channel;

/// <summary>
/// Jellyfin channel that shows YouTube trending videos via the YouTube Data API.
/// Subscribed channels are synced separately as STRM files via <see cref="ChannelSyncTask"/>.
/// </summary>
public class JellyTubbingChannel : IChannel, IRequiresMediaInfoCallback
{
    private readonly YouTubeApiService _youtube;

    /// <summary>
    /// Initializes a new instance of the <see cref="JellyTubbingChannel"/> class.
    /// </summary>
    public JellyTubbingChannel(YouTubeApiService youtube)
    {
        _youtube = youtube;
    }

    /// <inheritdoc />
    public string Name => "JellyTrending";

    /// <inheritdoc />
    public string Description => "YouTube-Trending direkt in Jellyfin.";

    /// <inheritdoc />
    public string DataVersion => "4";

    /// <inheritdoc />
    public string HomePageUrl => string.Empty;

    /// <inheritdoc />
    public ChannelParentalRating ParentalRating => ChannelParentalRating.GeneralAudience;

    /// <inheritdoc />
    public InternalChannelFeatures GetChannelFeatures() => new()
    {
        ContentTypes             = new List<ChannelMediaContentType> { ChannelMediaContentType.Movie },
        MediaTypes               = new List<ChannelMediaType> { ChannelMediaType.Video },
        SupportsContentDownloading = false,
    };

    /// <inheritdoc />
    public bool IsEnabledFor(string userId) => true;

    /// <inheritdoc />
    public IEnumerable<ImageType> GetSupportedChannelImages() => new[] { ImageType.Primary, ImageType.Thumb };

    /// <inheritdoc />
    public Task<DynamicImageResponse> GetChannelImage(ImageType type, CancellationToken ct)
    {
        var stream = GetType().Assembly.GetManifestResourceStream("Jellyfin.Plugin.JellyTubbing.thumb.png");
        return Task.FromResult(stream is null
            ? new DynamicImageResponse { HasImage = false }
            : new DynamicImageResponse { HasImage = true, Stream = stream, Format = ImageFormat.Png });
    }

    // -----------------------------------------------------------------------
    // Category folders
    // -----------------------------------------------------------------------

    // (Id, Display name, YouTube video category ID or null for all)
    private static readonly (string Id, string Label, string? CategoryId)[] Categories =
    {
        ("trending",        "Trending",     null),
        ("trending_music",  "Musik",        "10"),
        ("trending_gaming", "Gaming",       "20"),
        ("trending_news",   "Nachrichten",  "25"),
        ("trending_movies", "Filme",        "1"),
    };

    /// <inheritdoc />
    public async Task<ChannelItemResult> GetChannelItems(InternalChannelItemQuery query, CancellationToken ct)
    {
        var region = Plugin.Instance?.Configuration.TrendingRegion ?? "DE";

        // Root – show category folders, each with the thumbnail of its current top video
        if (string.IsNullOrEmpty(query.FolderId))
        {
            var topVideos = await Task.WhenAll(Array.ConvertAll(Categories, c => _youtube.GetTrendingAsync(c.CategoryId, ct)));
            var folders = new ChannelItemInfo[Categories.Length];
            for (var i = 0; i < Categories.Length; i++)
            {
                folders[i] = new ChannelItemInfo
                {
                    Id       = Categories[i].Id,
                    Name     = $"{Categories[i].Label} ({region})",
                    Type     = ChannelItemType.Folder,
                    ImageUrl = topVideos[i].Count > 0 ? topVideos[i][0].ImageUrl : null,
                };
            }

            return new ChannelItemResult { Items = folders, TotalRecordCount = folders.Length };
        }

        // Category folder – fetch trending videos
        foreach (var (id, _, categoryId) in Categories)
        {
            if (query.FolderId == id)
            {
                var items = await _youtube.GetTrendingAsync(categoryId, ct);
                return new ChannelItemResult { Items = items, TotalRecordCount = items.Count };
            }
        }

        return new ChannelItemResult { Items = Array.Empty<ChannelItemInfo>(), TotalRecordCount = 0 };
    }

    /// <inheritdoc />
    public Task<IEnumerable<MediaSourceInfo>> GetChannelItemMediaInfo(string id, CancellationToken ct)
    {
        var videoId = id.StartsWith("YT:", StringComparison.Ordinal) ? id[3..] : id;
        if (!VideoCacheService.IsValidVideoId(videoId))
            return Task.FromResult<IEnumerable<MediaSourceInfo>>(Array.Empty<MediaSourceInfo>());

        // Same endpoint as the STRM files: downloads into the cache on first play
        var serverUrl = (Plugin.Instance?.Configuration.JellyfinServerUrl ?? "http://localhost:8096").TrimEnd('/');
        var source = new MediaSourceInfo
        {
            // Jellyfin parses the media source ID as a GUID (HLS playlist), so derive a stable one
            Id                   = new Guid(MD5.HashData(Encoding.UTF8.GetBytes("jellytubbing:" + videoId))).ToString("N"),
            Name                 = "YouTube",
            Path                 = $"{serverUrl}/api/jellytubbing/stream/{videoId}",
            Protocol             = MediaProtocol.Http,
            IsRemote             = true,
            Container            = "mp4",
            SupportsDirectPlay   = true,
            SupportsDirectStream = true,
            SupportsTranscoding  = true,
            Type                 = MediaSourceType.Default,
        };
        return Task.FromResult<IEnumerable<MediaSourceInfo>>(new[] { source });
    }
}
