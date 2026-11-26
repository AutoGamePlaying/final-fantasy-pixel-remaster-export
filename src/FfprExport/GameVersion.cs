using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace FfprExport;

internal sealed record SteamBuildInfo(
    [property: JsonPropertyName("app_id")] int AppId,
    [property: JsonPropertyName("build_id")] string? BuildId,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("last_updated")] string? LastUpdated,
    [property: JsonPropertyName("source")] string? Source);

internal sealed record ExeVersionInfo(
    [property: JsonPropertyName("relative_path")] string RelativePath,
    [property: JsonPropertyName("size")] long Size,
    [property: JsonPropertyName("sha256")] string Sha256,
    [property: JsonPropertyName("file_version")] string? FileVersion,
    [property: JsonPropertyName("product_version")] string? ProductVersion,
    [property: JsonPropertyName("fixed_file_version")] string? FixedFileVersion,
    [property: JsonPropertyName("fixed_product_version")] string? FixedProductVersion);

internal sealed record BinaryFileHashInfo(
    [property: JsonPropertyName("relative_path")] string RelativePath,
    [property: JsonPropertyName("size")] long Size,
    [property: JsonPropertyName("sha256")] string Sha256);

internal sealed record BundleFileFingerprint(
    [property: JsonPropertyName("relative_path")] string RelativePath,
    [property: JsonPropertyName("size")] long Size,
    [property: JsonPropertyName("sha256")] string Sha256);

internal sealed record ResourceFingerprintResult(
    string CatalogSha256,
    long CatalogSize,
    string Fingerprint,
    long ElapsedMs,
    long HashedBytes,
    BundleFileFingerprint[] Bundles);

internal sealed record GameVersionInfo(
    [property: JsonPropertyName("game_id")] string GameId,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("unity_version")] string UnityVersion,
    [property: JsonPropertyName("steam")] SteamBuildInfo Steam,
    [property: JsonPropertyName("exe")] ExeVersionInfo? Exe,
    [property: JsonPropertyName("game_assembly")] BinaryFileHashInfo? GameAssembly,
    [property: JsonPropertyName("global_metadata")] BinaryFileHashInfo? GlobalMetadata,
    [property: JsonPropertyName("catalog_sha256")] string CatalogSha256,
    [property: JsonPropertyName("catalog_size")] long CatalogSize,
    [property: JsonPropertyName("bundle_count")] int BundleCount,
    [property: JsonPropertyName("bundle_total_bytes")] long BundleTotalBytes,
    [property: JsonPropertyName("resource_fingerprint")] string ResourceFingerprint);

internal sealed record PeParsedVersion(
    string? FileVersion,
    string? ProductVersion,
    string? FixedFileVersion,
    string? FixedProductVersion);

/// <summary>
/// Reads Steam build metadata, IL2CPP binary hashes, cross-platform PE version resources,
/// and deterministic Addressables/bundle resource fingerprints for FF1-FF6 Pixel Remaster.
/// </summary>
internal static class GameVersionDetector
{
    private const int StreamBufferSize = 1024 * 1024;
    private static readonly UTF8Encoding Utf8WithoutBom = new(false);

    internal static (GameVersionInfo Info, ResourceFingerprintResult Fingerprint) Inspect(
        DetectedGame game,
        string? manualSteamBuildId,
        int jobs,
        TextWriter? warningOutput = null)
    {
        var warn = warningOutput ?? Console.Error;
        var steam = ReadSteamBuildInfo(game.GameDirectory, game.Spec.AppId, manualSteamBuildId, warn);
        var exe = ReadExeVersion(game.GameDirectory, game.Spec.ExeName, warn);
        var gameAssembly = TryComputeBinaryHash(game.GameDirectory, game.GameAssemblyPath, "GameAssembly.dll", warn);
        var globalMetadata = TryComputeBinaryHash(
            game.GameDirectory,
            game.GlobalMetadataPath,
            $"{game.Spec.DataDirName}/il2cpp_data/Metadata/global-metadata.dat",
            warn);
        var fingerprint = ComputeResourceFingerprint(game.DataDirectory, game.CatalogPath, jobs);

        var bundleTotalBytes = fingerprint.Bundles.Sum(b => b.Size);
        var info = new GameVersionInfo(
            game.Spec.Id,
            game.Spec.Title,
            game.UnityVersion,
            steam,
            exe,
            gameAssembly,
            globalMetadata,
            fingerprint.CatalogSha256,
            fingerprint.CatalogSize,
            fingerprint.Bundles.Length,
            bundleTotalBytes,
            fingerprint.Fingerprint);

        return (info, fingerprint);
    }

    internal static SteamBuildInfo ReadSteamBuildInfo(
        string gameRoot,
        int appId,
        string? manualBuildId,
        TextWriter? warningOutput = null)
    {
        var warn = warningOutput ?? Console.Error;
        var trimmedManual = string.IsNullOrWhiteSpace(manualBuildId) ? null : manualBuildId.Trim();
        var manifestFileName = $"appmanifest_{appId}.acf";

        var fullRoot = PathSupport.TrimTrailingSeparators(Path.GetFullPath(gameRoot));
        var steamappsDir = Directory.GetParent(fullRoot)?.Parent?.FullName;
        var manifestPath = steamappsDir is not null
            ? FindFileCaseInsensitive(steamappsDir, manifestFileName)
            : null;

        if (manifestPath is not null)
        {
            string content;
            try
            {
                content = File.ReadAllText(PathSupport.ToIoPath(manifestPath), Encoding.UTF8);
            }
            catch (Exception exception)
            {
                throw new IOException(L.Format("GameVersion_ReadManifestFailed", manifestPath, exception.Message), exception);
            }

            var fields = ParseAppManifestVdf(content);
            fields.TryGetValue("buildid", out var buildId);
            fields.TryGetValue("name", out var name);
            fields.TryGetValue("LastUpdated", out var lastUpdated);

            if (!string.IsNullOrWhiteSpace(buildId))
            {
                var trimmedBuild = buildId.Trim();
                if (trimmedManual is not null
                    && !string.Equals(trimmedBuild, trimmedManual, StringComparison.Ordinal))
                {
                    warn.WriteLine(L.Format("GameVersion_IgnoreManualBuildId", trimmedBuild, manifestFileName, trimmedManual));
                }

                return new SteamBuildInfo(
                    appId,
                    trimmedBuild,
                    string.IsNullOrWhiteSpace(name) ? null : name,
                    string.IsNullOrWhiteSpace(lastUpdated) ? null : lastUpdated.Trim(),
                    "appmanifest");
            }

            if (trimmedManual is not null)
            {
                return new SteamBuildInfo(
                    appId,
                    trimmedManual,
                    string.IsNullOrWhiteSpace(name) ? null : name,
                    string.IsNullOrWhiteSpace(lastUpdated) ? null : lastUpdated.Trim(),
                    "manual");
            }

            warn.WriteLine(L.Format("GameVersion_ManifestMissingBuildId", manifestPath));
            return new SteamBuildInfo(
                appId,
                null,
                string.IsNullOrWhiteSpace(name) ? null : name,
                string.IsNullOrWhiteSpace(lastUpdated) ? null : lastUpdated.Trim(),
                null);
        }

        if (trimmedManual is not null)
        {
            return new SteamBuildInfo(appId, trimmedManual, null, null, "manual");
        }

        warn.WriteLine(L.Format("GameVersion_NoManifestNoManualBuildId", manifestFileName));
        return new SteamBuildInfo(appId, null, null, null, null);
    }

    /// <summary>
    /// Parses a Valve KeyValues (VDF/ACF) file and returns top-level key/value pairs inside AppState (depth 1).
    /// </summary>
    internal static Dictionary<string, string> ParseAppManifestVdf(string content)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var depth = 0;
        var index = 0;
        var inAppState = false;
        string? pendingKey = null;

        while (index < content.Length)
        {
            SkipWhitespaceAndComments(content, ref index);
            if (index >= content.Length)
            {
                break;
            }

            var ch = content[index];
            if (ch == '{')
            {
                if (depth == 0 && string.Equals(pendingKey, "AppState", StringComparison.OrdinalIgnoreCase))
                {
                    inAppState = true;
                }

                depth++;
                pendingKey = null;
                index++;
                continue;
            }

            if (ch == '}')
            {
                if (depth == 1)
                {
                    inAppState = false;
                }

                if (depth > 0)
                {
                    depth--;
                }

                pendingKey = null;
                index++;
                continue;
            }

            var token = ReadVdfToken(content, ref index);
            if (depth == 0)
            {
                pendingKey = token;
            }
            else if (inAppState && depth == 1)
            {
                if (pendingKey is null)
                {
                    pendingKey = token;
                }
                else
                {
                    result[pendingKey] = token;
                    pendingKey = null;
                }
            }
            else
            {
                pendingKey = null;
            }
        }

        return result;
    }

    internal static ExeVersionInfo? ReadExeVersion(
        string gameRoot,
        string exeFileName,
        TextWriter? warningOutput = null)
    {
        var warn = warningOutput ?? Console.Error;
        var exePath = FindFileCaseInsensitive(gameRoot, exeFileName);
        if (exePath is null)
        {
            warn.WriteLine(L.Format("GameVersion_ExeNotFound", exeFileName));
            return null;
        }

        var ioPath = PathSupport.ToIoPath(exePath);
        long size;
        PeParsedVersion parsed;
        using (var stream = new FileStream(ioPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            size = stream.Length;
            parsed = PeVersionReader.ReadFromStream(stream, exePath);
        }

        var sha256 = ComputeFileSha256Lower(exePath);
        var relativePath = PathSupport.NormalizeRelativePath(gameRoot, exePath);
        return new ExeVersionInfo(
            relativePath,
            size,
            sha256,
            parsed.FileVersion,
            parsed.ProductVersion,
            parsed.FixedFileVersion,
            parsed.FixedProductVersion);
    }

    internal static BinaryFileHashInfo? TryComputeBinaryHash(
        string gameRoot,
        string? filePath,
        string expectedRelativeName,
        TextWriter? warningOutput = null)
    {
        var warn = warningOutput ?? Console.Error;
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(PathSupport.ToIoPath(filePath)))
        {
            warn.WriteLine(L.Format("GameVersion_BinaryNotFound", expectedRelativeName));
            return null;
        }

        var ioPath = PathSupport.ToIoPath(filePath);
        var size = new FileInfo(ioPath).Length;
        var sha256 = ComputeFileSha256Lower(filePath);
        var relativePath = PathSupport.NormalizeRelativePath(gameRoot, filePath);
        return new BinaryFileHashInfo(relativePath, size, sha256);
    }

    /// <summary>
    /// Computes the resource fingerprint from StreamingAssets/aa/catalog.json and all *.bundle files
    /// under the game's *_Data directory sorted by relative path.
    /// </summary>
    internal static ResourceFingerprintResult ComputeResourceFingerprint(
        string dataDirectory,
        string catalogPath,
        int jobs = 1)
    {
        var ioDataDir = PathSupport.ToIoPath(dataDirectory);
        if (!Directory.Exists(ioDataDir))
        {
            throw new DirectoryNotFoundException(L.Format("GameVersion_FingerprintMissingDataDir", dataDirectory));
        }

        var ioCatalogPath = PathSupport.ToIoPath(catalogPath);
        if (!File.Exists(ioCatalogPath))
        {
            throw new FileNotFoundException(L.Format("GameVersion_FingerprintMissingCatalog", catalogPath));
        }

        var stopwatch = Stopwatch.StartNew();

        var catalogSize = new FileInfo(ioCatalogPath).Length;
        var catalogSha256 = ComputeFileSha256Lower(catalogPath);

        var candidates = new List<(string RelativePath, string FullPath)>();
        foreach (var filePath in Directory.EnumerateFiles(ioDataDir, "*", SearchOption.AllDirectories))
        {
            if (!Path.GetExtension(filePath).Equals(".bundle", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var relative = PathSupport.NormalizeRelativePath(ioDataDir, filePath);
            candidates.Add((relative, filePath));
        }

        candidates.Sort((a, b) => StringComparer.Ordinal.Compare(a.RelativePath, b.RelativePath));

        var entries = new BundleFileFingerprint[candidates.Count];
        long hashedBytes = catalogSize;
        var degree = Math.Max(1, jobs);

        if (degree == 1 || candidates.Count <= 4)
        {
            for (var i = 0; i < candidates.Count; i++)
            {
                var (relativePath, fullPath) = candidates[i];
                var size = new FileInfo(fullPath).Length;
                var sha = ComputeFileSha256Lower(fullPath);
                entries[i] = new BundleFileFingerprint(relativePath, size, sha);
                hashedBytes += size;
            }
        }
        else
        {
            Parallel.For(
                0,
                candidates.Count,
                new ParallelOptions { MaxDegreeOfParallelism = degree },
                i =>
                {
                    var (relativePath, fullPath) = candidates[i];
                    var size = new FileInfo(fullPath).Length;
                    var sha = ComputeFileSha256Lower(fullPath);
                    entries[i] = new BundleFileFingerprint(relativePath, size, sha);
                    Interlocked.Add(ref hashedBytes, size);
                });
        }

        var combined = CombineResourceFingerprint(catalogSha256, entries);
        stopwatch.Stop();

        return new ResourceFingerprintResult(
            catalogSha256,
            catalogSize,
            combined,
            stopwatch.ElapsedMilliseconds,
            hashedBytes,
            entries);
    }

    /// <summary>
    /// Combines catalog.json SHA-256 and bundle metadata into a deterministic SHA-256 fingerprint
    /// independent of input ordering.
    /// </summary>
    internal static string CombineResourceFingerprint(
        string catalogSha256,
        IReadOnlyList<BundleFileFingerprint> bundles)
    {
        var ordered = bundles
            .OrderBy(entry => entry.RelativePath, StringComparer.Ordinal)
            .ToArray();

        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendUtf8(hasher, "ffpr-resource-fingerprint-v1\n");
        AppendUtf8(hasher, $"catalog\t{catalogSha256.ToLowerInvariant()}\n");
        foreach (var entry in ordered)
        {
            var line = string.Create(
                CultureInfo.InvariantCulture,
                $"bundle\t{entry.RelativePath}\t{entry.Size}\t{entry.Sha256.ToLowerInvariant()}\n");
            AppendUtf8(hasher, line);
        }

        return Convert.ToHexStringLower(hasher.GetHashAndReset());
    }

    internal static string ComputeFileSha256Lower(string path)
    {
        var ioPath = PathSupport.ToIoPath(path);
        using var stream = new FileStream(
            ioPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            StreamBufferSize,
            FileOptions.SequentialScan);
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[StreamBufferSize];
        while (true)
        {
            var bytesRead = stream.Read(buffer, 0, buffer.Length);
            if (bytesRead == 0)
            {
                break;
            }

            hasher.AppendData(buffer, 0, bytesRead);
        }

        return Convert.ToHexStringLower(hasher.GetHashAndReset());
    }

    private static string? FindFileCaseInsensitive(string directory, string fileName)
    {
        var ioDir = PathSupport.ToIoPath(directory);
        if (!Directory.Exists(ioDir))
        {
            return null;
        }

        var direct = Path.Combine(directory, fileName);
        if (File.Exists(PathSupport.ToIoPath(direct)))
        {
            return direct;
        }

        foreach (var candidate in Directory.EnumerateFiles(ioDir))
        {
            if (string.Equals(Path.GetFileName(candidate), fileName, StringComparison.OrdinalIgnoreCase))
            {
                return Path.Combine(directory, Path.GetFileName(candidate));
            }
        }

        return null;
    }

    private static void SkipWhitespaceAndComments(string content, ref int index)
    {
        while (index < content.Length)
        {
            var ch = content[index];
            if (char.IsWhiteSpace(ch))
            {
                index++;
                continue;
            }

            if (ch == '/' && index + 1 < content.Length && content[index + 1] == '/')
            {
                index += 2;
                while (index < content.Length && content[index] != '\n')
                {
                    index++;
                }

                continue;
            }

            break;
        }
    }

    private static string ReadVdfToken(string content, ref int index)
    {
        if (content[index] == '"')
        {
            index++;
            var sb = new StringBuilder();
            while (index < content.Length)
            {
                var ch = content[index++];
                if (ch == '"')
                {
                    break;
                }

                if (ch == '\\' && index < content.Length)
                {
                    var escaped = content[index++];
                    sb.Append(escaped switch
                    {
                        'n' => '\n',
                        'r' => '\r',
                        't' => '\t',
                        '"' => '"',
                        '\\' => '\\',
                        _ => escaped,
                    });
                }
                else
                {
                    sb.Append(ch);
                }
            }

            return sb.ToString();
        }

        var start = index;
        while (index < content.Length
            && !char.IsWhiteSpace(content[index])
            && content[index] != '{'
            && content[index] != '}'
            && content[index] != '"')
        {
            index++;
        }

        return content[start..index];
    }

    private static void AppendUtf8(IncrementalHash hasher, string text)
    {
        var bytes = Utf8WithoutBom.GetBytes(text);
        hasher.AppendData(bytes);
    }
}

/// <summary>
/// Cross-platform pure-C# PE resource reader that extracts FileVersion and ProductVersion
/// from the RT_VERSION (VS_VERSIONINFO) resource without relying on Windows-only FileVersionInfo.
/// </summary>
internal static class PeVersionReader
{
    private const ushort DosMagic = 0x5A4D; // "MZ"
    private const uint PeSignature = 0x00004550; // "PE\0\0"
    private const ushort Pe32Magic = 0x010B;
    private const ushort Pe32PlusMagic = 0x020B;
    private const uint ResourceTypeVersion = 16; // RT_VERSION
    private const uint FixedFileInfoSignature = 0xFEEF04BD;
    private const int MaxVersionResourceBytes = 16 * 1024 * 1024;

    private readonly record struct PeSection(
        uint VirtualAddress,
        uint VirtualSize,
        uint RawPointer,
        uint RawSize);

    private readonly record struct ResourceDirEntry(
        uint NameOrId,
        uint OffsetToData)
    {
        public bool IsNamed => (NameOrId & 0x80000000u) != 0;
        public bool IsDirectory => (OffsetToData & 0x80000000u) != 0;
        public uint SubOffset => OffsetToData & 0x7FFFFFFFu;
    }

    internal static PeParsedVersion ReadFromFile(string exePath)
    {
        var ioPath = PathSupport.ToIoPath(exePath);
        using var stream = new FileStream(ioPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        return ReadFromStream(stream, exePath);
    }

    internal static PeParsedVersion ReadFromStream(Stream stream, string sourceName)
    {
        if (!stream.CanSeek)
        {
            throw new InvalidDataException(L.Format("PeVersion_UnseekableStream", sourceName));
        }

        if (stream.Length < 0x40)
        {
            throw new InvalidDataException(L.Format("PeVersion_FileTooSmall", sourceName));
        }

        var dosHeader = ReadExact(stream, 0, 0x40, sourceName);
        if (BinaryPrimitives.ReadUInt16LittleEndian(dosHeader.AsSpan(0, 2)) != DosMagic)
        {
            throw new InvalidDataException(L.Format("PeVersion_MissingMz", sourceName));
        }

        var peOffset = (long)BinaryPrimitives.ReadUInt32LittleEndian(dosHeader.AsSpan(0x3C, 4));
        if (peOffset < 0 || peOffset + 24 > stream.Length)
        {
            throw new InvalidDataException(L.Format("PeVersion_PeOffsetOutOfRange", sourceName));
        }

        var peAndCoff = ReadExact(stream, peOffset, 24, sourceName);
        if (BinaryPrimitives.ReadUInt32LittleEndian(peAndCoff.AsSpan(0, 4)) != PeSignature)
        {
            throw new InvalidDataException(L.Format("PeVersion_MissingPeSignature", sourceName));
        }

        var numberOfSections = BinaryPrimitives.ReadUInt16LittleEndian(peAndCoff.AsSpan(6, 2));
        var sizeOfOptionalHeader = BinaryPrimitives.ReadUInt16LittleEndian(peAndCoff.AsSpan(20, 2));
        var optionalHeaderOffset = peOffset + 24;
        if (sizeOfOptionalHeader < 96 || optionalHeaderOffset + sizeOfOptionalHeader > stream.Length)
        {
            throw new InvalidDataException(L.Format("PeVersion_InvalidOptionalHeaderSize", sourceName));
        }

        var optionalHeader = ReadExact(stream, optionalHeaderOffset, sizeOfOptionalHeader, sourceName);
        var optMagic = BinaryPrimitives.ReadUInt16LittleEndian(optionalHeader.AsSpan(0, 2));
        int numberOfRvaOffset;
        int dataDirectoriesOffset;
        if (optMagic == Pe32Magic)
        {
            numberOfRvaOffset = 92;
            dataDirectoriesOffset = 96;
        }
        else if (optMagic == Pe32PlusMagic)
        {
            numberOfRvaOffset = 108;
            dataDirectoriesOffset = 112;
        }
        else
        {
            throw new InvalidDataException(L.Format("PeVersion_UnsupportedMagic", sourceName, optMagic));
        }

        if (sizeOfOptionalHeader < dataDirectoriesOffset + 24)
        {
            return new PeParsedVersion(null, null, null, null);
        }

        var numberOfRvaAndSizes = BinaryPrimitives.ReadUInt32LittleEndian(optionalHeader.AsSpan(numberOfRvaOffset, 4));
        if (numberOfRvaAndSizes <= 2)
        {
            return new PeParsedVersion(null, null, null, null);
        }

        // DataDirectory[2] is the Resource Table.
        var resourceRva = BinaryPrimitives.ReadUInt32LittleEndian(optionalHeader.AsSpan(dataDirectoriesOffset + 16, 4));
        var resourceSize = BinaryPrimitives.ReadUInt32LittleEndian(optionalHeader.AsSpan(dataDirectoriesOffset + 20, 4));
        if (resourceRva == 0 || resourceSize == 0)
        {
            return new PeParsedVersion(null, null, null, null);
        }

        var sectionTableOffset = optionalHeaderOffset + sizeOfOptionalHeader;
        var sectionTableBytes = checked(numberOfSections * 40);
        if (sectionTableOffset + sectionTableBytes > stream.Length)
        {
            throw new InvalidDataException(L.Format("PeVersion_SectionTableOutOfRange", sourceName));
        }

        var sectionData = ReadExact(stream, sectionTableOffset, sectionTableBytes, sourceName);
        var sections = new PeSection[numberOfSections];
        for (var i = 0; i < numberOfSections; i++)
        {
            var span = sectionData.AsSpan(i * 40, 40);
            var virtualSize = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(8, 4));
            var virtualAddress = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(12, 4));
            var rawSize = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(16, 4));
            var rawPointer = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(20, 4));
            sections[i] = new PeSection(virtualAddress, virtualSize, rawPointer, rawSize);
        }

        if (!TryRvaToFileOffset(sections, resourceRva, out var resourceBaseOffset))
        {
            throw new InvalidDataException(L.Format("PeVersion_ResourceRvaUnmapped", sourceName, resourceRva));
        }

        var rootEntries = ReadResourceDirectory(stream, resourceBaseOffset, 0, sourceName);
        ResourceDirEntry? versionTypeEntry = null;
        foreach (var entry in rootEntries)
        {
            if (!entry.IsNamed && entry.NameOrId == ResourceTypeVersion && entry.IsDirectory)
            {
                versionTypeEntry = entry;
                break;
            }
        }

        if (versionTypeEntry is null)
        {
            return new PeParsedVersion(null, null, null, null);
        }

        var nameEntries = ReadResourceDirectory(stream, resourceBaseOffset, versionTypeEntry.Value.SubOffset, sourceName);
        ResourceDirEntry? chosenNameEntry = null;
        foreach (var entry in nameEntries)
        {
            if (!entry.IsDirectory)
            {
                continue;
            }

            if (chosenNameEntry is null || (!entry.IsNamed && entry.NameOrId == 1))
            {
                chosenNameEntry = entry;
                if (!entry.IsNamed && entry.NameOrId == 1)
                {
                    break;
                }
            }
        }

        if (chosenNameEntry is null)
        {
            return new PeParsedVersion(null, null, null, null);
        }

        var langEntries = ReadResourceDirectory(stream, resourceBaseOffset, chosenNameEntry.Value.SubOffset, sourceName);
        ResourceDirEntry? chosenLangEntry = null;
        foreach (var entry in langEntries)
        {
            if (entry.IsDirectory)
            {
                continue;
            }

            if (chosenLangEntry is null || (!entry.IsNamed && entry.NameOrId == 0x0409))
            {
                chosenLangEntry = entry;
                if (!entry.IsNamed && entry.NameOrId == 0x0409)
                {
                    break;
                }
            }
        }

        if (chosenLangEntry is null)
        {
            return new PeParsedVersion(null, null, null, null);
        }

        var dataEntryOffset = resourceBaseOffset + chosenLangEntry.Value.SubOffset;
        var dataEntry = ReadExact(stream, dataEntryOffset, 16, sourceName);
        var dataRva = BinaryPrimitives.ReadUInt32LittleEndian(dataEntry.AsSpan(0, 4));
        var dataSize = BinaryPrimitives.ReadUInt32LittleEndian(dataEntry.AsSpan(4, 4));
        if (dataSize == 0 || dataSize > MaxVersionResourceBytes)
        {
            throw new InvalidDataException(L.Format("PeVersion_InvalidVersionLength", sourceName, dataSize));
        }

        if (!TryRvaToFileOffset(sections, dataRva, out var dataFileOffset))
        {
            throw new InvalidDataException(L.Format("PeVersion_DataRvaUnmapped", sourceName, dataRva));
        }

        var blob = ReadExact(stream, dataFileOffset, (int)dataSize, sourceName);
        return ParseVersionResourceBlob(blob);
    }

    internal static PeParsedVersion ParseVersionResourceBlob(ReadOnlySpan<byte> blob)
    {
        if (blob.Length < 6)
        {
            return new PeParsedVersion(null, null, null, null);
        }

        if (!TryReadVersionNodeHeader(blob, 0, blob.Length, out var rootEnd, out var rootValueLen, out _, out _, out var valueOffset))
        {
            return new PeParsedVersion(null, null, null, null);
        }

        string? fixedFileVersion = null;
        string? fixedProductVersion = null;

        if (rootValueLen >= 52 && valueOffset + 52 <= rootEnd)
        {
            var fixedSpan = blob.Slice(valueOffset, 52);
            var sig = BinaryPrimitives.ReadUInt32LittleEndian(fixedSpan.Slice(0, 4));
            if (sig == FixedFileInfoSignature)
            {
                var fileMs = BinaryPrimitives.ReadUInt32LittleEndian(fixedSpan.Slice(8, 4));
                var fileLs = BinaryPrimitives.ReadUInt32LittleEndian(fixedSpan.Slice(12, 4));
                var prodMs = BinaryPrimitives.ReadUInt32LittleEndian(fixedSpan.Slice(16, 4));
                var prodLs = BinaryPrimitives.ReadUInt32LittleEndian(fixedSpan.Slice(20, 4));
                fixedFileVersion = FormatFixedVersion(fileMs, fileLs);
                fixedProductVersion = FormatFixedVersion(prodMs, prodLs);
            }
        }

        string? stringFileVersion = null;
        string? stringProductVersion = null;
        var preferredTableMatched = false;

        var childPos = Align4(valueOffset + rootValueLen);
        while (childPos + 6 <= rootEnd)
        {
            if (!TryReadVersionNodeHeader(blob, childPos, rootEnd, out var childEnd, out var childValLen, out _, out var childKey, out var childValOff))
            {
                break;
            }

            if (string.Equals(childKey, "StringFileInfo", StringComparison.Ordinal))
            {
                var tablePos = Align4(childValOff + childValLen);
                while (tablePos + 6 <= childEnd)
                {
                    if (!TryReadVersionNodeHeader(blob, tablePos, childEnd, out var tableEnd, out var tableValLen, out _, out var tableKey, out var tableValOff))
                    {
                        break;
                    }

                    var isEnUsTable = tableKey.StartsWith("0409", StringComparison.OrdinalIgnoreCase);
                    var tableStrings = ReadStringTable(blob, Align4(tableValOff + tableValLen), tableEnd);
                    tableStrings.TryGetValue("FileVersion", out var fv);
                    tableStrings.TryGetValue("ProductVersion", out var pv);

                    if (!preferredTableMatched || isEnUsTable)
                    {
                        if (!string.IsNullOrWhiteSpace(fv) && (isEnUsTable || stringFileVersion is null))
                        {
                            stringFileVersion = fv;
                        }

                        if (!string.IsNullOrWhiteSpace(pv) && (isEnUsTable || stringProductVersion is null))
                        {
                            stringProductVersion = pv;
                        }

                        if (isEnUsTable)
                        {
                            preferredTableMatched = true;
                        }
                    }

                    var nextTable = Align4(tableEnd);
                    if (nextTable <= tablePos)
                    {
                        break;
                    }

                    tablePos = nextTable;
                }
            }

            var nextChild = Align4(childEnd);
            if (nextChild <= childPos)
            {
                break;
            }

            childPos = nextChild;
        }

        return new PeParsedVersion(
            stringFileVersion ?? fixedFileVersion,
            stringProductVersion ?? fixedProductVersion,
            fixedFileVersion,
            fixedProductVersion);
    }

    private static Dictionary<string, string> ReadStringTable(ReadOnlySpan<byte> blob, int start, int tableEnd)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var pos = start;
        while (pos + 6 <= tableEnd)
        {
            if (!TryReadVersionNodeHeader(blob, pos, tableEnd, out var entryEnd, out var valLen, out var wType, out var key, out var valOff))
            {
                break;
            }

            if (!string.IsNullOrEmpty(key) && valLen > 0 && valOff < entryEnd)
            {
                var byteLen = wType == 1
                    ? Math.Min(entryEnd - valOff, valLen * 2)
                    : Math.Min(entryEnd - valOff, valLen);
                byteLen -= byteLen % 2;
                if (byteLen > 0)
                {
                    var raw = Encoding.Unicode.GetString(blob.Slice(valOff, byteLen));
                    var nulIdx = raw.IndexOf('\0');
                    var cleaned = (nulIdx >= 0 ? raw[..nulIdx] : raw).Trim();
                    if (!string.IsNullOrEmpty(cleaned) && !values.ContainsKey(key))
                    {
                        values[key] = cleaned;
                    }
                }
            }

            var next = Align4(entryEnd);
            if (next <= pos)
            {
                break;
            }

            pos = next;
        }

        return values;
    }

    private static bool TryReadVersionNodeHeader(
        ReadOnlySpan<byte> blob,
        int offset,
        int limit,
        out int nodeEnd,
        out int valueLength,
        out ushort valueType,
        out string key,
        out int valueOffset)
    {
        nodeEnd = offset;
        valueLength = 0;
        valueType = 0;
        key = string.Empty;
        valueOffset = offset;

        if (offset + 6 > limit)
        {
            return false;
        }

        var wLength = BinaryPrimitives.ReadUInt16LittleEndian(blob.Slice(offset, 2));
        valueLength = BinaryPrimitives.ReadUInt16LittleEndian(blob.Slice(offset + 2, 2));
        valueType = BinaryPrimitives.ReadUInt16LittleEndian(blob.Slice(offset + 4, 2));
        if (wLength < 6)
        {
            return false;
        }

        nodeEnd = Math.Min(limit, offset + wLength);
        var keyStart = offset + 6;
        var keyPos = keyStart;
        while (keyPos + 2 <= nodeEnd)
        {
            var codeUnit = BinaryPrimitives.ReadUInt16LittleEndian(blob.Slice(keyPos, 2));
            keyPos += 2;
            if (codeUnit == 0)
            {
                key = Encoding.Unicode.GetString(blob.Slice(keyStart, keyPos - keyStart - 2));
                valueOffset = Align4(keyPos);
                return true;
            }
        }

        return false;
    }

    private static string FormatFixedVersion(uint ms, uint ls)
    {
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{ms >> 16}.{ms & 0xFFFFu}.{ls >> 16}.{ls & 0xFFFFu}");
    }

    private static int Align4(int offset) => (offset + 3) & ~3;

    private static ResourceDirEntry[] ReadResourceDirectory(
        Stream stream,
        long resourceBaseOffset,
        uint relativeOffset,
        string sourceName)
    {
        var dirOffset = resourceBaseOffset + relativeOffset;
        var header = ReadExact(stream, dirOffset, 16, sourceName);
        var namedCount = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(12, 2));
        var idCount = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(14, 2));
        var total = namedCount + idCount;
        if (total == 0)
        {
            return [];
        }

        var rawEntries = ReadExact(stream, dirOffset + 16, checked(total * 8), sourceName);
        var entries = new ResourceDirEntry[total];
        for (var i = 0; i < total; i++)
        {
            var span = rawEntries.AsSpan(i * 8, 8);
            var nameOrId = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(0, 4));
            var offsetToData = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(4, 4));
            entries[i] = new ResourceDirEntry(nameOrId, offsetToData);
        }

        return entries;
    }

    private static bool TryRvaToFileOffset(PeSection[] sections, uint rva, out long fileOffset)
    {
        foreach (var section in sections)
        {
            var span = Math.Max(section.VirtualSize, section.RawSize);
            if (rva >= section.VirtualAddress && rva < section.VirtualAddress + span)
            {
                var delta = rva - section.VirtualAddress;
                if (delta < section.RawSize)
                {
                    fileOffset = (long)section.RawPointer + delta;
                    return true;
                }
            }
        }

        fileOffset = 0;
        return false;
    }

    private static byte[] ReadExact(Stream stream, long offset, int count, string sourceName)
    {
        if (offset < 0 || count < 0 || offset + count > stream.Length)
        {
            throw new InvalidDataException(L.Format("PeVersion_ReadOutOfRange", sourceName, offset, count));
        }

        stream.Seek(offset, SeekOrigin.Begin);
        var buffer = new byte[count];
        var readTotal = 0;
        while (readTotal < count)
        {
            var n = stream.Read(buffer, readTotal, count - readTotal);
            if (n == 0)
            {
                throw new InvalidDataException(L.Format("PeVersion_UnexpectedEof", sourceName));
            }

            readTotal += n;
        }

        return buffer;
    }
}
