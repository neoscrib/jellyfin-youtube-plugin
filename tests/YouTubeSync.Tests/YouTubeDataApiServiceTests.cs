using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.YouTubeSync.Services;
using Xunit;

public sealed class YouTubeDataApiServiceTests
{
    private sealed class Handler : HttpMessageHandler
    {
        public List<string> Urls { get; } = new();
        public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } = _ => Json("{\"items\":[]}");
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Urls.Add(request.RequestUri!.ToString());
            Assert.DoesNotContain("test-secret", request.RequestUri.ToString());
            Assert.Equal("test-secret", request.Headers.GetValues("X-Goog-Api-Key").Single());
            return Task.FromResult(Respond(request));
        }
    }

    private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK) { Content = new StringContent(value, Encoding.UTF8, "application/json") };
    private static YouTubeDataApiService Service(Handler handler, string key = "test-secret") => new(new HttpClient(handler), () => key);
    private static string Video(string id) => "{\"id\":\"" + id + "\",\"snippet\":{\"title\":\"Pets\",\"publishedAt\":\"2026-09-05T12:00:00Z\",\"channelTitle\":\"TerraGreen\",\"thumbnails\":{\"high\":{\"url\":\"https://example.com/high.jpg\"}}},\"contentDetails\":{\"duration\":\"PT1H2M3S\"}}";

    [Theory]
    [InlineData("@TerraGreen1", "forHandle=%40TerraGreen1")]
    [InlineData("https://www.youtube.com/@TerraGreen1/videos", "forHandle=%40TerraGreen1")]
    [InlineData("https://youtube.com/channel/UC123", "id=UC123")]
    [InlineData("https://youtube.com/user/legacy", "forUsername=legacy")]
    public async Task ResolvesChannelIdentifiers(string source, string expected)
    {
        var handler = new Handler { Respond = _ => Json("{\"items\":[{\"id\":\"UC123\",\"snippet\":{\"title\":\"TerraGreen\"}}]}") };
        var info = await Service(handler).GetSourceInfoAsync(source, default);
        Assert.Equal("TerraGreen", info.Title);
        Assert.Contains(expected, handler.Urls.Single());
    }

    [Fact]
    public async Task PagesPlaylistAndUsesVideoPublicationDateAndDuration()
    {
        var handler = new Handler();
        handler.Respond = request =>
        {
            var url = request.RequestUri!.ToString();
            if (url.Contains("/videos?")) return Json("{\"items\":[" + Video("a") + "," + Video("b") + "]}");
            if (url.Contains("pageToken=next")) return Json("{\"items\":[{\"contentDetails\":{\"videoId\":\"b\"},\"snippet\":{\"position\":1}}]}");
            return Json("{\"nextPageToken\":\"next\",\"items\":[{\"contentDetails\":{\"videoId\":\"a\"},\"snippet\":{\"position\":0,\"publishedAt\":\"2026-09-10T00:00:00Z\"}}]}");
        };
        var entries = await Service(handler).GetPlaylistEntriesAsync("https://youtube.com/playlist?list=PL123", 0, default);
        Assert.Equal(new[] { "a", "b" }, entries.Select(e => e["id"]!.GetValue<string>()));
        Assert.Equal(3723, entries[0]["duration"]!.GetValue<int>());
        Assert.Equal(2, entries[1]["playlist_position"]!.GetValue<int>());
        Assert.Equal(new DateTime(2026, 9, 5, 12, 0, 0, DateTimeKind.Utc), YouTubeDataApiService.ParsePublishedDate(entries[0]));
        Assert.Equal(3, handler.Urls.Count);
    }

    [Fact]
    public async Task BatchesAtFiftyAndSkipsUnavailableVideos()
    {
        var handler = new Handler();
        handler.Respond = request =>
        {
            var url = request.RequestUri!.ToString();
            if (url.Contains("/videos?")) return Json("{\"items\":[]}");
            var next = url.Contains("pageToken=next");
            var entries = Enumerable.Range(next ? 50 : 0, next ? 1 : 50).Select(i => new { contentDetails = new { videoId = "v" + i } });
            return Json(System.Text.Json.JsonSerializer.Serialize(new { items = entries, nextPageToken = next ? "" : "next" }));
        };
        Assert.Empty(await Service(handler).GetPlaylistEntriesAsync("PL123", 0, default));
        Assert.Equal(2, handler.Urls.Count(u => u.Contains("/videos?")));
        Assert.Contains("id=v50", handler.Urls.Last());
    }

    [Fact]
    public async Task ScanLimitStopsBeforeNextPage()
    {
        var handler = new Handler { Respond = request => request.RequestUri!.ToString().Contains("/videos?")
            ? Json("{\"items\":[" + Video("a") + "]}")
            : Json("{\"nextPageToken\":\"next\",\"items\":[{\"contentDetails\":{\"videoId\":\"a\"}}]}") };
        Assert.Single(await Service(handler).GetPlaylistEntriesAsync("PL123", 1, default));
        Assert.Equal(2, handler.Urls.Count);
        Assert.Contains("maxResults=1", handler.Urls[0]);
    }

    [Fact]
    public async Task MissingKeyDoesNotSendRequest()
    {
        var handler = new Handler();
        await Assert.ThrowsAsync<InvalidOperationException>(() => Service(handler, "").GetSourceInfoAsync("@test", default));
        Assert.Empty(handler.Urls);
    }

    [Fact]
    public async Task QuotaErrorFailsInsteadOfReturningEmptyListAndDoesNotExposeKey()
    {
        var handler = new Handler { Respond = _ => new(HttpStatusCode.Forbidden) { Content = new StringContent("test-secret quotaExceeded") } };
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Service(handler).GetPlaylistEntriesAsync("PL123", 0, default));
        Assert.Contains("403", error.Message);
        Assert.DoesNotContain("test-secret", error.ToString());
    }

    [Theory]
    [InlineData("https://youtube.com/@test/shorts")]
    [InlineData("https://youtube.com/c/custom")]
    [InlineData("https://example.com/@test")]
    public async Task UnsupportedSourcesDoNotSendRequests(string source)
    {
        var handler = new Handler();
        await Assert.ThrowsAsync<InvalidOperationException>(() => Service(handler).GetPlaylistEntriesAsync(source, 0, default));
        Assert.Empty(handler.Urls);
    }

    [Fact]
    public async Task MalformedResponseFailsInsteadOfReturningEmptyList()
    {
        var handler = new Handler { Respond = _ => Json("{}") };
        await Assert.ThrowsAsync<InvalidOperationException>(() => Service(handler).GetPlaylistEntriesAsync("PL123", 0, default));
    }


    [Fact]
    public async Task StreamsFilterKeepsArchivesAndAllUploadsKeepsBoth()
    {
        var handler = new Handler();
        handler.Respond = request =>
        {
            var url = request.RequestUri!.ToString();
            if (url.Contains("/channels?")) return Json("""{"items":[{"id":"UC123","contentDetails":{"relatedPlaylists":{"uploads":"UU123"}}}]}""");
            if (url.Contains("/playlistItems?")) return Json("""{"items":[{"contentDetails":{"videoId":"a"}},{"contentDetails":{"videoId":"b"}}]}""");
            var archive = JsonNode.Parse(Video("b"))!;
            archive["liveStreamingDetails"] = new JsonObject { ["actualEndTime"] = "2026-09-05T13:00:00Z" };
            return Json("{\"items\":[" + Video("a") + "," + archive.ToJsonString() + "]}");
        };
        var service = Service(handler);
        Assert.Equal(2, (await service.GetPlaylistEntriesAsync("https://youtube.com/@test/videos", 0, default)).Count);
        var streams = await service.GetPlaylistEntriesAsync("https://youtube.com/@test/streams", 0, default);
        Assert.Equal("b", Assert.Single(streams)["id"]!.GetValue<string>());
    }

    [Fact]
    public async Task PlaylistDiscoverySortsBeforeApplyingLimit()
    {
        var handler = new Handler();
        handler.Respond = request =>
        {
            var url = request.RequestUri!.ToString();
            if (url.Contains("/channels?")) return Json("""{"items":[{"id":"UC123"}]}""");
            if (url.Contains("pageToken=next")) return Json("""{"items":[{"id":"new","snippet":{"title":"Newest","publishedAt":"2026-09-05T00:00:00Z"}}]}""");
            return Json("""{"nextPageToken":"next","items":[{"id":"old","snippet":{"publishedAt":"2020-01-01T00:00:00Z"}}]}""");
        };
        var entries = await Service(handler).GetPlaylistEntriesAsync("https://youtube.com/@test/playlists", 1, default);
        Assert.Equal("new", Assert.Single(entries)["id"]!.GetValue<string>());
        Assert.Equal(3, handler.Urls.Count);
    }

    [Fact]
    public async Task FailedSecondPageDoesNotReturnPartialResults()
    {
        var handler = new Handler { Respond = request => request.RequestUri!.ToString().Contains("pageToken=next")
            ? new HttpResponseMessage(HttpStatusCode.Forbidden)
            : Json("""{"nextPageToken":"next","items":[{"contentDetails":{"videoId":"a"}}]}""") };
        await Assert.ThrowsAsync<InvalidOperationException>(() => Service(handler).GetPlaylistEntriesAsync("PL123", 0, default));
        Assert.Equal(2, handler.Urls.Count);
    }

    [Fact]
    public async Task MissingVideoPublicationDateFailsBeforeReturningEntries()
    {
        var handler = new Handler { Respond = request => request.RequestUri!.ToString().Contains("/videos?")
            ? Json("""{"items":[{"id":"a","snippet":{"title":"Undated"}}]}""")
            : Json("""{"items":[{"contentDetails":{"videoId":"a"}}]}""") };
        await Assert.ThrowsAsync<InvalidOperationException>(() => Service(handler).GetPlaylistEntriesAsync("PL123", 0, default));
    }

    [Fact]
    public async Task CancellationPropagates()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Service(new Handler()).GetSourceInfoAsync("@test", cancellation.Token));
    }
}
