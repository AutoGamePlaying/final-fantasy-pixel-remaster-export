using System.Buffers.Binary;
using System.Text;
using System.Text.Json;

namespace FfprExport;

internal sealed record CatalogResourceEntry(
    int EntryIndex,
    string InternalId,
    string ProviderId,
    string ResourceType,
    int DependencyKeyIndex,
    string? PrimaryKey);

internal sealed record CatalogBundleRecord(
    int EntryIndex,
    string InternalId,
    string RelativePath,
    string FullPath,
    bool ExistsOnDisk,
    long FileSize,
    string? PrimaryKey,
    IReadOnlyList<CatalogResourceEntry> CatalogAssets);

internal sealed record AddressablesCatalogSnapshot(
    string CatalogPath,
    string LocatorId,
    int TotalInternalIds,
    int TotalEntries,
    IReadOnlyList<CatalogBundleRecord> Bundles,
    IReadOnlyList<CatalogResourceEntry> NonBundleEntries,
    IReadOnlyList<string> UnindexedBundlesOnDisk,
    IReadOnlyList<string> EncryptedFontBundles);

internal static class AddressablesCatalog
{
    private const string RuntimePathToken = "{UnityEngine.AddressableAssets.Addressables.RuntimePath}";
    private const string AssetBundleProviderId = "UnityEngine.ResourceManagement.ResourceProviders.AssetBundleProvider";

    public static AddressablesCatalogSnapshot Load(DetectedGame game)
    {
        if (!File.Exists(game.CatalogPath))
        {
            throw new FileNotFoundException($"找不到 Addressables 目录文件：{game.CatalogPath}");
        }

        using var stream = File.OpenRead(game.CatalogPath);
        using var doc = JsonDocument.Parse(stream);
        var root = doc.RootElement;

        var locatorId = root.TryGetProperty("m_LocatorId", out var locEl)
            ? locEl.GetString() ?? string.Empty
            : string.Empty;

        var providerIds = ReadStringArray(root, "m_ProviderIds");
        var internalIds = ReadStringArray(root, "m_InternalIds");
        var internalIdPrefixes = ReadStringArray(root, "m_InternalIdPrefixes");
        var resourceTypes = ReadResourceTypes(root);

        // Expand internalId prefixes (used in newer Addressables formats, empty in 1.16.x).
        for (var i = 0; i < internalIds.Length; i++)
        {
            internalIds[i] = ExpandInternalIdPrefix(internalIds[i], internalIdPrefixes);
        }

        var keyBytes = ReadBase64(root, "m_KeyDataString");
        var bucketBytes = ReadBase64(root, "m_BucketDataString");
        var entryBytes = ReadBase64(root, "m_EntryDataString");

        var buckets = ParseBuckets(bucketBytes, keyBytes);
        var rawEntries = ParseEntries(entryBytes);

        // Map bucket index -> resolved bundle entry indices (entries using AssetBundleProvider).
        var bundleEntryByIndex = new Dictionary<int, (CatalogResourceEntry Entry, string RelPath, string FullPath, bool Exists, long Size)>();
        var entries = new List<CatalogResourceEntry>(rawEntries.Count);

        for (var i = 0; i < rawEntries.Count; i++)
        {
            var raw = rawEntries[i];
            var internalId = raw.InternalIdIndex >= 0 && raw.InternalIdIndex < internalIds.Length
                ? internalIds[raw.InternalIdIndex]
                : string.Empty;
            var providerId = raw.ProviderIndex >= 0 && raw.ProviderIndex < providerIds.Length
                ? providerIds[raw.ProviderIndex]
                : string.Empty;
            var resourceType = raw.ResourceTypeIndex >= 0 && raw.ResourceTypeIndex < resourceTypes.Length
                ? resourceTypes[raw.ResourceTypeIndex]
                : string.Empty;
            var primaryKey = raw.PrimaryKeyIndex >= 0 && raw.PrimaryKeyIndex < buckets.Count
                ? buckets[raw.PrimaryKeyIndex].Key
                : null;

            var entry = new CatalogResourceEntry(
                i,
                internalId,
                providerId,
                resourceType,
                raw.DependencyKeyIndex,
                primaryKey);
            entries.Add(entry);

            if (IsBundleEntry(providerId, internalId))
            {
                var (relPath, fullPath) = ResolveBundlePath(game.AddressablesDirectory, internalId);
                var exists = File.Exists(fullPath);
                var size = exists ? new FileInfo(fullPath).Length : 0L;
                bundleEntryByIndex[i] = (entry, relPath, fullPath, exists, size);
            }
        }

        // Associate bundled asset entries with their owning bundle(s) via dependencyKeyIndex.
        var assetsByBundleEntry = new Dictionary<int, List<CatalogResourceEntry>>();
        foreach (var bundleIdx in bundleEntryByIndex.Keys)
        {
            assetsByBundleEntry[bundleIdx] = [];
        }

        var nonBundleEntries = new List<CatalogResourceEntry>();
        foreach (var entry in entries)
        {
            if (bundleEntryByIndex.ContainsKey(entry.EntryIndex))
            {
                continue;
            }

            var linkedToBundle = false;
            if (entry.DependencyKeyIndex >= 0 && entry.DependencyKeyIndex < buckets.Count)
            {
                foreach (var depEntryIdx in buckets[entry.DependencyKeyIndex].EntryIndices)
                {
                    if (assetsByBundleEntry.TryGetValue(depEntryIdx, out var list))
                    {
                        list.Add(entry);
                        linkedToBundle = true;
                        break;
                    }
                }
            }

            if (!linkedToBundle)
            {
                nonBundleEntries.Add(entry);
            }
        }

        var bundleRecords = bundleEntryByIndex.Values
            .OrderBy(b => b.RelPath, StringComparer.Ordinal)
            .Select(b => new CatalogBundleRecord(
                b.Entry.EntryIndex,
                b.Entry.InternalId,
                b.RelPath,
                b.FullPath,
                b.Exists,
                b.Size,
                b.Entry.PrimaryKey,
                assetsByBundleEntry[b.Entry.EntryIndex]))
            .ToArray();

        var knownBundlePaths = new HashSet<string>(
            bundleRecords.Select(b => Path.GetFullPath(b.FullPath)),
            StringComparer.OrdinalIgnoreCase);

        var unindexed = new List<string>();
        var standaloneDir = Path.Combine(game.AddressablesDirectory, "StandaloneWindows64");
        if (Directory.Exists(standaloneDir))
        {
            foreach (var file in Directory.EnumerateFiles(standaloneDir, "*.bundle").OrderBy(p => p, StringComparer.Ordinal))
            {
                var full = Path.GetFullPath(file);
                if (!knownBundlePaths.Contains(full))
                {
                    unindexed.Add(Path.GetRelativePath(game.DataDirectory, full).Replace('\\', '/'));
                }
            }
        }

        var streamingAssetsDir = Path.Combine(game.DataDirectory, "StreamingAssets");
        var fontBundles = new List<string>();
        if (Directory.Exists(streamingAssetsDir))
        {
            foreach (var file in Directory.EnumerateFiles(streamingAssetsDir, "*.bundle", SearchOption.TopDirectoryOnly)
                .OrderBy(p => p, StringComparer.Ordinal))
            {
                fontBundles.Add(Path.GetRelativePath(game.DataDirectory, file).Replace('\\', '/'));
            }
        }

        return new AddressablesCatalogSnapshot(
            game.CatalogPath,
            locatorId,
            internalIds.Length,
            entries.Count,
            bundleRecords,
            nonBundleEntries,
            unindexed,
            fontBundles);
    }

    private static bool IsBundleEntry(string providerId, string internalId)
    {
        return string.Equals(providerId, AssetBundleProviderId, StringComparison.Ordinal)
            || internalId.EndsWith(".bundle", StringComparison.OrdinalIgnoreCase);
    }

    internal static (string RelativePath, string FullPath) ResolveBundlePath(string aaDirectory, string internalId)
    {
        var normalized = internalId.Replace('\\', '/');
        string relFromAa;
        if (normalized.StartsWith(RuntimePathToken + "/", StringComparison.Ordinal))
        {
            relFromAa = normalized[(RuntimePathToken.Length + 1)..];
        }
        else if (normalized.StartsWith(RuntimePathToken, StringComparison.Ordinal))
        {
            relFromAa = normalized[RuntimePathToken.Length..].TrimStart('/');
        }
        else
        {
            relFromAa = normalized.TrimStart('/');
        }

        var fullPath = Path.GetFullPath(Path.Combine(aaDirectory, relFromAa.Replace('/', Path.DirectorySeparatorChar)));
        var relFromData = $"StreamingAssets/aa/{relFromAa}";
        return (relFromData, fullPath);
    }

    private static string ExpandInternalIdPrefix(string id, string[] prefixes)
    {
        if (prefixes.Length == 0)
        {
            return id;
        }

        var idx = id.LastIndexOf('#');
        if (idx <= 0)
        {
            return id;
        }

        if (int.TryParse(id[..idx], out var prefixIdx) && prefixIdx >= 0 && prefixIdx < prefixes.Length)
        {
            return prefixes[prefixIdx] + id[(idx + 1)..];
        }

        return id;
    }

    private static string[] ReadStringArray(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var arr) || arr.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var result = new string[arr.GetArrayLength()];
        var i = 0;
        foreach (var item in arr.EnumerateArray())
        {
            result[i++] = item.GetString() ?? string.Empty;
        }

        return result;
    }

    private static string[] ReadResourceTypes(JsonElement root)
    {
        if (!root.TryGetProperty("m_resourceTypes", out var arr) || arr.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var result = new string[arr.GetArrayLength()];
        var i = 0;
        foreach (var item in arr.EnumerateArray())
        {
            result[i++] = item.TryGetProperty("m_ClassName", out var cls)
                ? cls.GetString() ?? string.Empty
                : string.Empty;
        }

        return result;
    }

    private static byte[] ReadBase64(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var el) || el.ValueKind != JsonValueKind.String)
        {
            return [];
        }

        var str = el.GetString();
        return string.IsNullOrEmpty(str) ? [] : Convert.FromBase64String(str);
    }

    private sealed record BucketRecord(string? Key, int[] EntryIndices);

    private static List<BucketRecord> ParseBuckets(byte[] bucketBytes, byte[] keyBytes)
    {
        var result = new List<BucketRecord>();
        if (bucketBytes.Length < 4)
        {
            return result;
        }

        var bucketCount = BinaryPrimitives.ReadInt32LittleEndian(bucketBytes.AsSpan(0, 4));
        var offset = 4;
        for (var i = 0; i < bucketCount && offset + 8 <= bucketBytes.Length; i++)
        {
            var dataOffset = BinaryPrimitives.ReadInt32LittleEndian(bucketBytes.AsSpan(offset, 4));
            offset += 4;
            var entryCount = BinaryPrimitives.ReadInt32LittleEndian(bucketBytes.AsSpan(offset, 4));
            offset += 4;

            var entryIndices = new int[entryCount];
            for (var j = 0; j < entryCount && offset + 4 <= bucketBytes.Length; j++)
            {
                entryIndices[j] = BinaryPrimitives.ReadInt32LittleEndian(bucketBytes.AsSpan(offset, 4));
                offset += 4;
            }

            var key = TryReadKeyObject(keyBytes, dataOffset);
            result.Add(new BucketRecord(key, entryIndices));
        }

        return result;
    }

    private static string? TryReadKeyObject(byte[] keyBytes, int offset)
    {
        if (offset < 0 || offset >= keyBytes.Length)
        {
            return null;
        }

        // KeyDataString starts with a 4-byte count header; bucket dataOffset is relative to payload after that header.
        var pos = 4 + offset;
        if (pos >= keyBytes.Length)
        {
            return null;
        }

        var objectType = keyBytes[pos++];
        switch (objectType)
        {
            case 0: // AsciiString
                if (pos + 4 > keyBytes.Length) return null;
                var asciiLen = BinaryPrimitives.ReadInt32LittleEndian(keyBytes.AsSpan(pos, 4));
                pos += 4;
                if (asciiLen < 0 || pos + asciiLen > keyBytes.Length) return null;
                return Encoding.ASCII.GetString(keyBytes, pos, asciiLen);

            case 1: // UnicodeString (UTF-16LE)
                if (pos + 4 > keyBytes.Length) return null;
                var utf16Len = BinaryPrimitives.ReadInt32LittleEndian(keyBytes.AsSpan(pos, 4));
                pos += 4;
                if (utf16Len < 0 || pos + utf16Len > keyBytes.Length) return null;
                return Encoding.Unicode.GetString(keyBytes, pos, utf16Len);

            case 2: // UInt16
                if (pos + 2 > keyBytes.Length) return null;
                return BinaryPrimitives.ReadUInt16LittleEndian(keyBytes.AsSpan(pos, 2)).ToString();

            case 3: // UInt32
                if (pos + 4 > keyBytes.Length) return null;
                return BinaryPrimitives.ReadUInt32LittleEndian(keyBytes.AsSpan(pos, 4)).ToString();

            case 4: // Int32
                if (pos + 4 > keyBytes.Length) return null;
                return BinaryPrimitives.ReadInt32LittleEndian(keyBytes.AsSpan(pos, 4)).ToString();

            case 5: // Hash128 (encoded as length-prefixed ASCII string in Unity Addressables)
                if (pos + 4 > keyBytes.Length) return null;
                var hashLen = BinaryPrimitives.ReadInt32LittleEndian(keyBytes.AsSpan(pos, 4));
                pos += 4;
                if (hashLen < 0 || pos + hashLen > keyBytes.Length) return null;
                return Encoding.ASCII.GetString(keyBytes, pos, hashLen);

            default:
                return null;
        }
    }

    private sealed record RawEntry(
        int InternalIdIndex,
        int ProviderIndex,
        int DependencyKeyIndex,
        int DepHash,
        int DataIndex,
        int PrimaryKeyIndex,
        int ResourceTypeIndex);

    private static List<RawEntry> ParseEntries(byte[] entryBytes)
    {
        var result = new List<RawEntry>();
        if (entryBytes.Length < 4)
        {
            return result;
        }

        var count = BinaryPrimitives.ReadInt32LittleEndian(entryBytes.AsSpan(0, 4));
        var offset = 4;
        for (var i = 0; i < count && offset + 28 <= entryBytes.Length; i++)
        {
            result.Add(new RawEntry(
                BinaryPrimitives.ReadInt32LittleEndian(entryBytes.AsSpan(offset, 4)),
                BinaryPrimitives.ReadInt32LittleEndian(entryBytes.AsSpan(offset + 4, 4)),
                BinaryPrimitives.ReadInt32LittleEndian(entryBytes.AsSpan(offset + 8, 4)),
                BinaryPrimitives.ReadInt32LittleEndian(entryBytes.AsSpan(offset + 12, 4)),
                BinaryPrimitives.ReadInt32LittleEndian(entryBytes.AsSpan(offset + 16, 4)),
                BinaryPrimitives.ReadInt32LittleEndian(entryBytes.AsSpan(offset + 20, 4)),
                BinaryPrimitives.ReadInt32LittleEndian(entryBytes.AsSpan(offset + 24, 4))));
            offset += 28;
        }

        return result;
    }
}
