using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using Jellyfin.Plugin.YouTubeSync.Configuration;
using Jellyfin.Plugin.YouTubeSync.Metadata;

namespace Jellyfin.Plugin.YouTubeSync.Services;

/// <summary>Retrieves public source and video metadata exclusively from YouTube Data API v3.</summary>
public sealed class YouTubeDataApiService : IDisposable
{
    private readonly HttpClient _client;
    private readonly Func<string> _getApiKey;

    public YouTubeDataApiService(HttpClient client, Func<string> getApiKey)
    {
        _client = client;
        _getApiKey = getApiKey;
    }

    public async Task<SourceInfo> GetSourceInfoAsync(string url, CancellationToken cancellationToken)
    {
        var source = ParseSource(url);
        var resource = source.PlaylistId is not null
            ? (await ListAsync("playlists", new() { ["part"] = "snippet", ["id"] = source.PlaylistId }, 1, cancellationToken)).FirstOrDefault()
            : await GetChannelAsync(source, cancellationToken);
        if (resource is null) throw new InvalidOperationException("YouTube source was not found or is not public.");
        var snippet = resource["snippet"];
        var thumbnail = Thumbnail(snippet);
        return new SourceInfo
        {
            Title = Text(snippet, "title"),
            Description = Text(snippet, "description"),
            ThumbnailUrl = thumbnail,
            PosterUrl = Text(resource["brandingSettings"]?["image"], "bannerExternalUrl") is { Length: > 0 } banner ? banner : thumbnail,
            Type = source.PlaylistId is null ? SourceType.Channel : SourceType.Playlist
        };
    }

    public async Task<IReadOnlyList<JsonNode>> GetPlaylistEntriesAsync(
        string url, int maxEntryScanCount, CancellationToken cancellationToken,
        DateTime? retentionCutoffUtc = null, IReadOnlyDictionary<string, JsonNode>? cachedVideos = null)
    {
        var source = ParseSource(url);
        if (source.Feed == "shorts")
            throw new InvalidOperationException("YouTube Data API does not expose a Shorts-only feed. Select All uploads or use a playlist.");
        var playlistId = source.PlaylistId;
        if (playlistId is null)
        {
            var channel = await GetChannelAsync(source, cancellationToken);
            if (source.Feed == "playlists")
            {
                // playlists.list has no ordering parameter; sort the complete discovery result before limiting.
                var playlists = await ListAsync("playlists", new() { ["part"] = "snippet", ["channelId"] = Text(channel, "id") }, 0, cancellationToken);
                var ordered = playlists.OrderByDescending(p => Text(p["snippet"], "publishedAt"));
                return (maxEntryScanCount > 0 ? ordered.Take(maxEntryScanCount) : ordered)
                    .Select(p => (JsonNode)new JsonObject { ["id"] = Text(p, "id"), ["title"] = Text(p["snippet"], "title") }).ToList();
            }

            playlistId = Text(channel["contentDetails"]?["relatedPlaylists"], "uploads");
            if (string.IsNullOrWhiteSpace(playlistId)) throw new InvalidOperationException("YouTube channel has no accessible uploads playlist.");
        }

        var result = new List<JsonNode>();
        var query = new Dictionary<string, string> { ["part"] = "snippet,contentDetails", ["playlistId"] = playlistId };
        var tokens = new HashSet<string>(StringComparer.Ordinal);
        var scanned = 0;
        while (true)
        {
            query["maxResults"] = (maxEntryScanCount > 0 ? Math.Min(50, maxEntryScanCount - scanned) : 50).ToString(CultureInfo.InvariantCulture);
            var page = await RequestAsync("playlistItems", query, cancellationToken);
            var entries = Items(page).ToList();
            if (maxEntryScanCount > 0) entries = entries.Take(maxEntryScanCount - scanned).ToList();
            scanned += entries.Count;
            // The API exposes playlist position, not a guaranteed release-date ordering.
            // An old or known entry cannot terminate discovery: later entries may be new.
            var candidates = entries.Where(entry => !IsExpired(
                Text(entry["contentDetails"], "videoPublishedAt"), retentionCutoffUtc)).ToList();
            result.AddRange(await GetVideoPageAsync(candidates, source.Feed, retentionCutoffUtc, cachedVideos, cancellationToken));
            if (maxEntryScanCount > 0 && scanned >= maxEntryScanCount) break;
            var token = Text(page, "nextPageToken");
            if (token.Length == 0) break;
            if (!tokens.Add(token)) throw new InvalidOperationException("YouTube returned a repeated page token; sync stopped before cleanup.");
            query["pageToken"] = token;
        }
        return result;
    }

    private async Task<IReadOnlyList<JsonNode>> GetVideoPageAsync(
        IReadOnlyList<JsonNode> entries, string feed, DateTime? retentionCutoffUtc,
        IReadOnlyDictionary<string, JsonNode>? cachedVideos, CancellationToken cancellationToken)
    {
        var ids = entries.Select(e => Text(e["contentDetails"], "videoId")).Where(id => id.Length > 0 && !(cachedVideos?.ContainsKey(id) ?? false)).Distinct(StringComparer.Ordinal).ToArray();
        var videos = new Dictionary<string, JsonNode>(StringComparer.Ordinal);
        foreach (var batch in ids.Chunk(50))
        {
            var page = await RequestAsync("videos", new() { ["part"] = "snippet,contentDetails,liveStreamingDetails", ["id"] = string.Join(",", batch) }, cancellationToken);
            foreach (var video in Items(page)) videos[Text(video, "id")] = video;
        }

        var result = new List<JsonNode>();
        foreach (var entry in entries)
        {
            var id = Text(entry["contentDetails"], "videoId");
            if (cachedVideos?.TryGetValue(id, out var cached) == true)
            {
                if (!IsExpired(Text(cached, "published_at"), retentionCutoffUtc))
                {
                    var copy = cached.DeepClone();
                    copy["playlist_position"] = entry["snippet"]?["position"]?.GetValue<int>() + 1;
                    result.Add(copy);
                }
                continue;
            }
            // Deleted/private videos are omitted by videos.list. Never use playlist insertion dates as release dates.
            if (!videos.TryGetValue(id, out var video)) continue;
            if (feed == "streams" && video["liveStreamingDetails"] is null) continue;
            var snippet = video["snippet"];
            if (snippet is null || !DateTimeOffset.TryParse(Text(snippet, "publishedAt"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out _))
                throw new InvalidOperationException("YouTube returned video metadata without a valid publication date; sync stopped before cleanup.");
            if (IsExpired(Text(snippet, "publishedAt"), retentionCutoffUtc)) continue;
            var durationText = Text(video["contentDetails"], "duration");
            int? duration = string.IsNullOrEmpty(durationText) ? null : checked((int)XmlConvert.ToTimeSpan(durationText).TotalSeconds);
            result.Add(new JsonObject
            {
                ["id"] = id, ["title"] = Text(snippet, "title"), ["description"] = Text(snippet, "description"),
                ["thumbnail"] = Thumbnail(snippet), ["channel"] = Text(snippet, "channelTitle"),
                ["published_at"] = Text(snippet, "publishedAt"), ["duration"] = duration,
                ["metadata_fetched_at"] = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                ["refresh_always"] = Text(snippet, "liveBroadcastContent") is "live" or "upcoming",
                ["playlist_position"] = entry["snippet"]?["position"]?.GetValue<int>() + 1
            });
        }

        return result;
    }

    private static bool IsExpired(string dateText, DateTime? cutoff) => cutoff is DateTime value
        && DateTimeOffset.TryParse(dateText, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date)
        && date.UtcDateTime < value;

    private async Task<JsonNode> GetChannelAsync(SourceReference source, CancellationToken cancellationToken)
    {
        var query = new Dictionary<string, string> { ["part"] = "snippet,contentDetails,brandingSettings", [source.ChannelFilter] = source.ChannelValue };
        return (await ListAsync("channels", query, 1, cancellationToken)).FirstOrDefault()
            ?? throw new InvalidOperationException("YouTube channel was not found. Use its @handle, channel ID, or /user/ URL.");
    }

    private async Task<List<JsonNode>> ListAsync(string resource, Dictionary<string, string> query, int limit, CancellationToken cancellationToken)
    {
        var result = new List<JsonNode>();
        var tokens = new HashSet<string>(StringComparer.Ordinal);
        while (true)
        {
            query["maxResults"] = (limit > 0 ? Math.Min(50, limit - result.Count) : 50).ToString(CultureInfo.InvariantCulture);
            var page = await RequestAsync(resource, query, cancellationToken);
            result.AddRange(Items(page));
            if (limit > 0 && result.Count >= limit) return result.Take(limit).ToList();
            var token = Text(page, "nextPageToken");
            if (token.Length == 0) return result;
            if (!tokens.Add(token)) throw new InvalidOperationException("YouTube returned a repeated page token; sync stopped before cleanup.");
            query["pageToken"] = token;
        }
    }

    private async Task<JsonNode> RequestAsync(string resource, Dictionary<string, string> query, CancellationToken cancellationToken)
    {
        var key = _getApiKey().Trim();
        if (key.Length == 0) throw new InvalidOperationException("Configure a YouTube Data API v3 key in YouTubeSync settings before syncing.");
        var url = "https://www.googleapis.com/youtube/v3/" + resource + "?" + string.Join("&", query.Select(p => Uri.EscapeDataString(p.Key) + "=" + Uri.EscapeDataString(p.Value)));
        for (var attempt = 0; ; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            // Keep the API key out of URLs, HTTP URL logs, and exception messages.
            request.Headers.Add("X-Goog-Api-Key", key);
            using var response = await _client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if ((response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500) && attempt < 2)
            {
                await Task.Delay(TimeSpan.FromSeconds(1 << attempt), cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"YouTube Data API {resource} request failed (HTTP {(int)response.StatusCode}). Check the API key, API enablement, quota, and source access. Existing source files were not cleaned up.");
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return JsonNode.Parse(body) ?? throw new InvalidOperationException("YouTube returned an empty API response.");
        }
    }

    private static IEnumerable<JsonNode> Items(JsonNode page) =>
        page["items"] is JsonArray items ? items.OfType<JsonNode>() : throw new InvalidOperationException("YouTube returned an invalid list response; sync stopped before cleanup.");

    public static DateTime? ParsePublishedDate(JsonNode node) =>
        DateTimeOffset.TryParse(Text(node, "published_at"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date) ? date.UtcDateTime : null;

    private static string Text(JsonNode? node, string key) => node?[key]?.GetValue<string>() ?? string.Empty;

    private static string Thumbnail(JsonNode? snippet)
    {
        var thumbnails = snippet?["thumbnails"];
        foreach (var size in new[] { "maxres", "standard", "high", "medium", "default" })
        {
            var url = Text(thumbnails?[size], "url");
            if (url.Length > 0) return url;
        }
        return string.Empty;
    }

    public void Dispose() => _client.Dispose();

    private sealed record SourceReference(string? PlaylistId, string ChannelFilter, string ChannelValue, string Feed);

    private static SourceReference ParseSource(string input)
    {
        input = input.Trim();
        if (input.StartsWith('@')) return new(null, "forHandle", input, "videos");
        if (input.StartsWith("UC", StringComparison.Ordinal) && !input.Contains('/')) return new(null, "id", input, "videos");
        if (!input.Contains('/') && !input.Contains(':') && input.Length > 0) return new(input, "", "", "");
        if (!Uri.TryCreate(input, UriKind.Absolute, out var uri)
            || (uri.Scheme != "https" && uri.Scheme != "http")
            || (uri.Host != "youtube.com" && !uri.Host.EndsWith(".youtube.com", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Enter a YouTube channel or playlist URL, @handle, or channel ID.");
        foreach (var pair in uri.Query.TrimStart('?').Split('&'))
        {
            var parts = pair.Split('=', 2);
            if (parts.Length == 2 && parts[0] == "list" && parts[1].Length > 0) return new(Uri.UnescapeDataString(parts[1]), "", "", "");
        }
        var path = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.UnescapeDataString).ToArray();
        var feed = path.LastOrDefault() ?? "videos";
        if (path.Length > 0 && path[0].StartsWith('@')) return new(null, "forHandle", path[0], feed);
        if (path.Length > 1 && path[0] == "channel") return new(null, path[1].StartsWith('@') ? "forHandle" : "id", path[1], feed);
        if (path.Length > 1 && path[0] == "user") return new(null, "forUsername", path[1], feed);
        throw new InvalidOperationException("Use the channel's @handle or /channel/ ID URL instead of a custom /c/ URL.");
    }
}
