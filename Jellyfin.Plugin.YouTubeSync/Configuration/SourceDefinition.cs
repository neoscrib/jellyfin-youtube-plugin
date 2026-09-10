using System;

namespace Jellyfin.Plugin.YouTubeSync.Configuration;

/// <summary>Defines the type of a YouTube source (channel or playlist).</summary>
public enum SourceType
{
    /// <summary>A YouTube channel.</summary>
    Channel,

    /// <summary>A YouTube playlist.</summary>
    Playlist
}

/// <summary>Determines how videos from a playlist are organised inside Jellyfin.</summary>
public enum SourceMode
{
    /// <summary>Videos are treated as TV-show episodes (tvshow.nfo parent).</summary>
    Series,

    /// <summary>Videos are treated as individual movies.</summary>
    Movies
}

/// <summary>Determines which channel tab is synced for channel sources.</summary>
public enum ChannelFeed
{
    /// <summary>Sync all uploads, including Shorts and streams.</summary>
    Videos,

    /// <summary>Sync channel-curated playlists from the /playlists tab.</summary>
    Playlists,

    /// <summary>Legacy Shorts-only selection; unsupported by YouTube Data API.</summary>
    Shorts,

    /// <summary>Sync live and stream archive content from the /streams tab.</summary>
    Streams
}

/// <summary>Describes a single YouTube source (channel or playlist) to sync.</summary>
public class SourceDefinition
{
    /// <summary>
    /// Gets or sets the YouTube channel / playlist URL or bare ID.
    /// Full URLs such as <c>https://www.youtube.com/@handle</c> or
    /// <c>https://www.youtube.com/playlist?list=PLxxxxxx</c> are accepted.
    /// For backward compatibility a bare channel ID (e.g. <c>UCxxxxxx</c>) or
    /// playlist ID (e.g. <c>PLxxxxxx</c>) is still supported.
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Gets or sets the source type.</summary>
    public SourceType Type { get; set; } = SourceType.Channel;

    /// <summary>Gets or sets how playlist content is structured inside Jellyfin.</summary>
    public SourceMode Mode { get; set; } = SourceMode.Series;

    /// <summary>Gets or sets the human-readable display name used as the folder name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets which channel feed tab to sync when <see cref="Type"/> is <see cref="SourceType.Channel"/>.</summary>
    public ChannelFeed Feed { get; set; } = ChannelFeed.Videos;

    /// <summary>Gets or sets an optional description written into the source .nfo file.</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>Gets or sets the thumbnail / channel-icon URL (auto-fetched from YouTube when empty).</summary>
    public string ThumbnailUrl { get; set; } = string.Empty;

    /// <summary>
    /// Gets the YouTube URL identifying this source and its selected feed.
    /// <para>
    /// For channels the selected feed suffix is appended. The Videos feed uses the API uploads playlist.
    /// </para>
    /// </summary>
    public string Url
    {
        get
        {
            // Accept full YouTube URLs entered directly by the user.
            if (Id.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                || Id.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                if (Type == SourceType.Channel)
                {
                    return EnsureChannelFeedTab(Id, Feed);
                }

                return Id;
            }

            // Legacy: bare ID → construct a standard URL.
            return Type == SourceType.Channel
                ? $"https://www.youtube.com/channel/{Id}{GetChannelFeedSuffix(Feed)}"
                : $"https://www.youtube.com/playlist?list={Id}";
        }
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    // Known YouTube channel tab suffixes — used by EnsureChannelVideosTab.
    private static readonly string[] ChannelTabSuffixes =
        ["/videos", "/shorts", "/streams", "/featured", "/about", "/community", "/playlists", "/channels"];

    /// <summary>
    /// Applies the selected channel tab by removing a known tab suffix and appending the selected feed suffix.
    /// </summary>
    private static string EnsureChannelFeedTab(string channelUrl, ChannelFeed feed)
    {
        var url = channelUrl.TrimEnd('/');

        foreach (var tab in ChannelTabSuffixes)
        {
            if (url.EndsWith(tab, StringComparison.OrdinalIgnoreCase))
            {
                url = url[..^tab.Length];
                break;
            }
        }

        return url + GetChannelFeedSuffix(feed);
    }

    private static string GetChannelFeedSuffix(ChannelFeed feed)
    {
        return feed switch
        {
            ChannelFeed.Playlists => "/playlists",
            ChannelFeed.Shorts => "/shorts",
            ChannelFeed.Streams => "/streams",
            _ => "/videos"
        };
    }
}
