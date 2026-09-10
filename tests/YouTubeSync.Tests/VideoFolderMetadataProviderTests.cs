using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.YouTubeSync.Metadata;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using Xunit;

public sealed class VideoFolderMetadataProviderTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("youtube-folder-tests-").FullName;
    private readonly VideoFolderMetadataProvider _provider = new();

    [Fact]
    public async Task ImportsReleaseDateForFolderSorting()
    {
        WriteMarker("<youtubeSyncFolder><premiered>2026-09-05</premiered></youtubeSyncFolder>");
        var folder = new Folder { Path = _directory };
        Assert.True(_provider.HasChanged(folder, null!));
        var result = await _provider.GetMetadata(new ItemInfo(folder), null!, CancellationToken.None);
        Assert.True(result.HasMetadata);
        Assert.Equal(new DateTime(2026, 9, 5, 0, 0, 0, DateTimeKind.Utc), result.Item.PremiereDate);
        Assert.Equal(2026, result.Item.ProductionYear);
        folder.PremiereDate = result.Item.PremiereDate;
        folder.ProductionYear = result.Item.ProductionYear;
        Assert.False(_provider.HasChanged(folder, null!));
        WriteMarker("<youtubeSyncFolder><premiered>2026-09-06</premiered></youtubeSyncFolder>");
        Assert.True(_provider.HasChanged(folder, null!));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("<broken")]
    [InlineData("<youtubeSyncFolder><premiered>invalid</premiered></youtubeSyncFolder>")]
    [InlineData("<movie><premiered>2026-09-05</premiered></movie>")]
    public async Task IgnoresMissingOrInvalidMarkers(string? xml)
    {
        if (xml is not null) WriteMarker(xml);
        var folder = new Folder { Path = _directory };
        Assert.False(_provider.HasChanged(folder, null!));
        Assert.False((await _provider.GetMetadata(new ItemInfo(folder), null!, CancellationToken.None)).HasMetadata);
    }

    [Fact]
    public async Task DoesNotOverrideSeasonMetadata()
    {
        WriteMarker("<youtubeSyncFolder><premiered>2026-09-05</premiered></youtubeSyncFolder>");
        var season = new Season { Path = _directory };
        Assert.False(_provider.HasChanged(season, null!));
        Assert.False((await _provider.GetMetadata(new ItemInfo(season), null!, CancellationToken.None)).HasMetadata);
    }

    private void WriteMarker(string xml) => File.WriteAllText(Path.Combine(_directory, VideoFolderMetadataProvider.MetadataFileName), xml);

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
