using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.YouTubeSync.Sync;

/// <summary>Reuses metadata only after a completed sync, while periodically refreshing edits.</summary>
public static class SyncMetadataCache
{
    public const string FileName = ".youtube-sync-metadata.json";

    public static IReadOnlyDictionary<string, JsonNode> Load(string directory, string sourceUrl, DateTime nowUtc)
    {
        var result = new Dictionary<string, JsonNode>(StringComparer.Ordinal);
        try
        {
            var snapshot = JsonNode.Parse(File.ReadAllText(Path.Combine(directory, FileName)));
            if (snapshot?["source"]?.GetValue<string>() != sourceUrl || snapshot["entries"] is not JsonArray entries)
                return result;
            foreach (var entry in entries)
            {
                if (entry is null || entry["refresh_always"]?.GetValue<bool>() == true) continue;
                if (!DateTimeOffset.TryParse(entry["metadata_fetched_at"]?.GetValue<string>(), CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal, out var fetched) || nowUtc - fetched.UtcDateTime >= TimeSpan.FromHours(24)
                    || fetched.UtcDateTime > nowUtc) continue;
                if (!File.Exists(entry["__strm_path"]?.GetValue<string>()) || !File.Exists(entry["__nfo_path"]?.GetValue<string>())) continue;
                var id = entry["id"]?.GetValue<string>();
                if (!string.IsNullOrEmpty(id)) result[id] = entry.DeepClone();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException or FormatException)
        {
            // Missing/corrupt state is a cache miss, never evidence that source files should be removed.
            result.Clear();
        }
        return result;
    }

    public static async Task SaveAsync(string directory, string sourceUrl, IReadOnlyList<JsonNode> entries, CancellationToken cancellationToken)
    {
        var list = new JsonArray();
        foreach (var entry in entries) list.Add(entry.DeepClone());
        var snapshot = new JsonObject { ["source"] = sourceUrl, ["entries"] = list };
        var path = Path.Combine(directory, FileName);
        var temporary = path + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, snapshot.ToJsonString(), cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
