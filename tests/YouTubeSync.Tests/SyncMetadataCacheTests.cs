using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.YouTubeSync.Sync;
using Xunit;

public sealed class SyncMetadataCacheTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("youtube-cache-tests-").FullName;
    private readonly DateTime _now = new(2026, 9, 10, 12, 0, 0, DateTimeKind.Utc);

    private JsonNode Entry()
    {
        var strm = Path.Combine(_directory, "video.strm");
        var nfo = Path.Combine(_directory, "video.nfo");
        File.WriteAllText(strm, "resolver");
        File.WriteAllText(nfo, "metadata");
        return new JsonObject
        {
            ["id"] = "video", ["published_at"] = "2026-09-05T12:00:00Z",
            ["metadata_fetched_at"] = _now.ToString("O"),
            ["__strm_path"] = strm, ["__nfo_path"] = nfo
        };
    }

    [Fact]
    public async Task CompletedSnapshotSurvivesReloadAndExpiresAfterADay()
    {
        await SyncMetadataCache.SaveAsync(_directory, "source", new[] { Entry() }, default);
        Assert.Single(SyncMetadataCache.Load(_directory, "source", _now.AddHours(23)));
        Assert.Empty(SyncMetadataCache.Load(_directory, "source", _now.AddHours(24)));
        Assert.Empty(SyncMetadataCache.Load(_directory, "different-source", _now));
    }

    [Theory]
    [InlineData("video.strm")]
    [InlineData("video.nfo")]
    public async Task MissingFilesForceMetadataRefetch(string file)
    {
        await SyncMetadataCache.SaveAsync(_directory, "source", new[] { Entry() }, default);
        File.Delete(Path.Combine(_directory, file));
        Assert.Empty(SyncMetadataCache.Load(_directory, "source", _now));
    }

    [Fact]
    public async Task CancelledSaveKeepsPreviousCompletedSnapshot()
    {
        await SyncMetadataCache.SaveAsync(_directory, "source", new[] { Entry() }, default);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SyncMetadataCache.SaveAsync(_directory, "source", Array.Empty<JsonNode>(), cancellation.Token));
        Assert.Single(SyncMetadataCache.Load(_directory, "source", _now));
        Assert.False(File.Exists(Path.Combine(_directory, SyncMetadataCache.FileName + ".tmp")));
    }

    [Fact]
    public void MissingOrCorruptSnapshotIsACacheMiss()
    {
        Assert.Empty(SyncMetadataCache.Load(_directory, "source", _now));
        File.WriteAllText(Path.Combine(_directory, SyncMetadataCache.FileName), "broken json");
        Assert.Empty(SyncMetadataCache.Load(_directory, "source", _now));
    }

    [Fact]
    public async Task LiveAndUpcomingMetadataAlwaysRefreshes()
    {
        var entry = Entry();
        entry["refresh_always"] = true;
        await SyncMetadataCache.SaveAsync(_directory, "source", new[] { entry }, default);
        Assert.Empty(SyncMetadataCache.Load(_directory, "source", _now));
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
