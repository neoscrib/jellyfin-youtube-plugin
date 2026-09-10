using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Providers;

namespace Jellyfin.Plugin.YouTubeSync.Metadata;

/// <summary>Gives Home Videos folders the release date of the video they contain.</summary>
public sealed class VideoFolderMetadataProvider : ILocalMetadataProvider<Folder>, IHasItemChangeMonitor
{
    public const string MetadataFileName = ".youtube-sync-folder.xml";

    public string Name => "YouTubeSync video folders";

    public Task<MetadataResult<Folder>> GetMetadata(
        ItemInfo info,
        IDirectoryService directoryService,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = new MetadataResult<Folder>();
        // Do not override Movie, Series, Season, or library metadata.
        if (info.ItemType == typeof(Folder) && ReadReleaseDate(info.Path) is DateTime date)
        {
            result.HasMetadata = true;
            result.Item = new Folder
            {
                Name = Path.GetFileName(info.Path),
                PremiereDate = date,
                ProductionYear = date.Year
            };
        }

        return Task.FromResult(result);
    }

    public bool HasChanged(BaseItem item, IDirectoryService directoryService)
    {
        return item.GetType() == typeof(Folder)
            && ReadReleaseDate(item.Path) is DateTime date
            && (item.PremiereDate != date || item.ProductionYear != date.Year);
    }

    private static DateTime? ReadReleaseDate(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            using var reader = XmlReader.Create(Path.Combine(path, MetadataFileName), new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null
            });
            var root = XDocument.Load(reader).Root;
            return root?.Name == "youtubeSyncFolder"
                && DateTime.TryParseExact(root.Element("premiered")?.Value, "yyyy-MM-dd",
                    CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out var date)
                ? date
                : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
        catch (XmlException)
        {
            return null;
        }
    }
}
