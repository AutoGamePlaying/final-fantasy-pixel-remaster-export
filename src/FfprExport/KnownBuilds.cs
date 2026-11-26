using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FfprExport;

internal sealed record FfprDbTableHash(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("rows")] long Rows,
    [property: JsonPropertyName("sha256")] string Sha256);

internal sealed record FfprDbHashReport(
    [property: JsonPropertyName("format")] int Format,
    [property: JsonPropertyName("file_sha256")] string FileSha256,
    [property: JsonPropertyName("content_sha256")] string ContentSha256,
    [property: JsonPropertyName("schema_version")] int SchemaVersion,
    [property: JsonPropertyName("sqlite_version")] string SqliteVersion,
    [property: JsonPropertyName("tables")] FfprDbTableHash[] Tables);

internal sealed record FfprExportToolInfo(
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("git_commit")] string GitCommit,
    [property: JsonPropertyName("assets_tools_version")] string AssetsToolsVersion,
    [property: JsonPropertyName("assets_tools_cpp2il_version")] string AssetsToolsCpp2IlVersion);

internal sealed record BuildToolsInfo(
    [property: JsonPropertyName("ffpr_export")] FfprExportToolInfo FfprExport);

internal sealed record BuildDatabaseInfo(
    [property: JsonPropertyName("file_name")] string FileName,
    [property: JsonPropertyName("size")] long Size,
    [property: JsonPropertyName("schema_version")] int? SchemaVersion,
    [property: JsonPropertyName("sqlite_version")] string? SqliteVersion,
    [property: JsonPropertyName("file_sha256")] string FileSha256,
    [property: JsonPropertyName("content_sha256")] string? ContentSha256,
    [property: JsonPropertyName("hash_format")] int? HashFormat,
    [property: JsonPropertyName("tables")] FfprDbTableHash[] Tables);

internal sealed record KnownBuildEntry(
    [property: JsonPropertyName("registered_at")] string RegisteredAt,
    [property: JsonPropertyName("note")] string? Note,
    [property: JsonPropertyName("game_version")] GameVersionInfo GameVersion,
    [property: JsonPropertyName("tools")] BuildToolsInfo? Tools,
    [property: JsonPropertyName("database")] BuildDatabaseInfo Database);

internal sealed record TableDiffInfo(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("expected_rows")] long? ExpectedRows,
    [property: JsonPropertyName("actual_rows")] long? ActualRows,
    [property: JsonPropertyName("expected_sha256")] string? ExpectedSha256,
    [property: JsonPropertyName("actual_sha256")] string? ActualSha256);

internal enum KnownBuildComparisonStatus
{
    Matched,
    NewGameVersion,
    HashMismatch,
}

internal sealed record KnownBuildComparisonResult(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("matched_registered_at")] string? MatchedRegisteredAt,
    [property: JsonPropertyName("file_sha256_mismatch_only")] bool FileSha256MismatchOnly,
    [property: JsonPropertyName("table_diffs")] TableDiffInfo[] TableDiffs)
{
    [JsonIgnore]
    internal KnownBuildComparisonStatus StatusKind { get; init; }
}

/// <summary>
/// Loads known-builds.json grouped by game (ff1..ff6) -- preferring the file next to the executable
/// and falling back to the embedded resource -- and compares a newly built SQLite database against it.
/// </summary>
internal static class KnownBuildsRegistry
{
    internal const string FileName = "known-builds.json";
    internal const string EmbeddedResourceName = "FfprExport.known-builds.json";

    internal static string MessageMatched => L.Get("KnownBuilds_Matched");
    internal static string MessageNewGameVersion => L.Get("KnownBuilds_NewGameVersion");
    internal static string MessageHashMismatch => L.Get("KnownBuilds_HashMismatch");

    private static readonly JsonSerializerOptions ParseOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    internal static (Dictionary<string, KnownBuildEntry[]> ByGame, string Source) Load(
        string? executableDirectoryOverride = null)
    {
        var baseDir = executableDirectoryOverride ?? AppContext.BaseDirectory;
        var adjacentPath = Path.Combine(baseDir, FileName);
        if (File.Exists(PathSupport.ToIoPath(adjacentPath)))
        {
            var json = File.ReadAllText(PathSupport.ToIoPath(adjacentPath), Encoding.UTF8);
            return (ParseByGame(json, adjacentPath), "file");
        }

        var assembly = typeof(KnownBuildsRegistry).Assembly;
        using var stream = assembly.GetManifestResourceStream(EmbeddedResourceName);
        if (stream is null)
        {
            throw new InvalidOperationException(L.Format("KnownBuilds_MissingEmbeddedResource", EmbeddedResourceName));
        }

        using var reader = new StreamReader(stream, Encoding.UTF8);
        var embeddedJson = reader.ReadToEnd();
        return (ParseByGame(embeddedJson, EmbeddedResourceName), "embedded");
    }

    internal static Dictionary<string, KnownBuildEntry[]> ParseByGame(string json, string sourceName)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException(L.Format("KnownBuilds_InvalidFormatRoot", sourceName));
            }

            var targetObj = doc.RootElement;
            if (targetObj.TryGetProperty("games", out var gamesEl) && gamesEl.ValueKind == JsonValueKind.Object)
            {
                targetObj = gamesEl;
            }

            var result = new Dictionary<string, KnownBuildEntry[]>(StringComparer.OrdinalIgnoreCase);
            foreach (var prop in targetObj.EnumerateObject())
            {
                if (prop.Value.ValueKind != JsonValueKind.Array)
                {
                    throw new InvalidDataException(L.Format("KnownBuilds_InvalidFormatGameArray", prop.Name, sourceName));
                }

                var entries = prop.Value.Deserialize<KnownBuildEntry[]>(ParseOptions)
                    ?? throw new InvalidDataException(L.Format("KnownBuilds_NullGameEntries", prop.Name, sourceName));
                result[prop.Name] = entries;
            }

            return result;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(L.Format("KnownBuilds_ParseFailed", sourceName, exception.Message), exception);
        }
    }

    internal static KnownBuildComparisonResult Compare(
        IReadOnlyDictionary<string, KnownBuildEntry[]> registryByGame,
        string source,
        string gameId,
        GameVersionInfo actualGameVersion,
        BuildDatabaseInfo actualDb)
    {
        var entries = registryByGame.TryGetValue(gameId, out var list) ? list : [];
        return Compare(entries, source, actualGameVersion, actualDb);
    }

    internal static KnownBuildComparisonResult Compare(
        IReadOnlyList<KnownBuildEntry> entries,
        string source,
        GameVersionInfo actualGameVersion,
        BuildDatabaseInfo actualDb)
    {
        var sameGameEntries = entries
            .Where(entry => entry.GameVersion is not null && IsSameGameVersion(entry.GameVersion, actualGameVersion))
            .ToArray();

        if (sameGameEntries.Length == 0)
        {
            return new KnownBuildComparisonResult(
                "new_game_version",
                MessageNewGameVersion,
                source,
                null,
                false,
                [])
            {
                StatusKind = KnownBuildComparisonStatus.NewGameVersion,
            };
        }

        foreach (var entry in sameGameEntries)
        {
            if (IsExactDatabaseMatch(entry.Database, actualDb))
            {
                return new KnownBuildComparisonResult(
                    "matched",
                    MessageMatched,
                    source,
                    entry.RegisteredAt,
                    false,
                    [])
                {
                    StatusKind = KnownBuildComparisonStatus.Matched,
                };
            }
        }

        // Pick the closest registered entry for diff reporting: prefer matching schema_version, then latest.
        var reference = sameGameEntries
            .LastOrDefault(entry => entry.Database.SchemaVersion == actualDb.SchemaVersion)
            ?? sameGameEntries[^1];

        var diffs = DiffTables(reference.Database.Tables, actualDb.Tables);
        var contentMatched = !string.IsNullOrEmpty(actualDb.ContentSha256)
            && !string.IsNullOrEmpty(reference.Database.ContentSha256)
            && string.Equals(reference.Database.ContentSha256, actualDb.ContentSha256, StringComparison.OrdinalIgnoreCase);

        return new KnownBuildComparisonResult(
            "hash_mismatch",
            MessageHashMismatch,
            source,
            reference.RegisteredAt,
            contentMatched && diffs.Length == 0,
            diffs)
        {
            StatusKind = KnownBuildComparisonStatus.HashMismatch,
        };
    }

    internal static void PrintComparison(KnownBuildComparisonResult result, TextWriter output)
    {
        output.WriteLine(L.Format("KnownBuilds_ComparisonHeader", result.Source, result.Message));
        if (result.StatusKind == KnownBuildComparisonStatus.Matched && !string.IsNullOrEmpty(result.MatchedRegisteredAt))
        {
            output.WriteLine(L.Format("KnownBuilds_MatchedRegisteredAt", result.MatchedRegisteredAt));
            return;
        }

        if (result.StatusKind != KnownBuildComparisonStatus.HashMismatch)
        {
            return;
        }

        if (!string.IsNullOrEmpty(result.MatchedRegisteredAt))
        {
            output.WriteLine(L.Format("KnownBuilds_ReferenceRegisteredAt", result.MatchedRegisteredAt));
        }

        if (result.FileSha256MismatchOnly)
        {
            output.WriteLine(L.Get("KnownBuilds_FileShaMismatchOnly"));
            return;
        }

        if (result.TableDiffs.Length == 0)
        {
            output.WriteLine(L.Get("KnownBuilds_NoTableHashes"));
            return;
        }

        output.WriteLine(L.Format("KnownBuilds_DiffTableCount", result.TableDiffs.Length));
        foreach (var diff in result.TableDiffs)
        {
            var line = diff.Kind switch
            {
                "added" => L.Format("KnownBuilds_DiffAdded", diff.Name, diff.ActualRows),
                "removed" => L.Format("KnownBuilds_DiffRemoved", diff.Name, diff.ExpectedRows),
                _ => diff.ExpectedRows == diff.ActualRows
                    ? L.Format("KnownBuilds_DiffChangedSameRows", diff.Name, diff.ActualRows)
                    : L.Format("KnownBuilds_DiffChangedDiffRows", diff.Name, diff.ExpectedRows, diff.ActualRows),
            };
            output.WriteLine(line);
        }
    }

    private static bool IsSameGameVersion(GameVersionInfo registered, GameVersionInfo actual)
    {
        if (!string.Equals(registered.ResourceFingerprint, actual.ResourceFingerprint, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.IsNullOrEmpty(registered.GameAssembly?.Sha256)
            && !string.IsNullOrEmpty(actual.GameAssembly?.Sha256)
            && !string.Equals(registered.GameAssembly.Sha256, actual.GameAssembly.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.IsNullOrEmpty(registered.GlobalMetadata?.Sha256)
            && !string.IsNullOrEmpty(actual.GlobalMetadata?.Sha256)
            && !string.Equals(registered.GlobalMetadata.Sha256, actual.GlobalMetadata.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }

    private static bool IsExactDatabaseMatch(BuildDatabaseInfo registered, BuildDatabaseInfo actual)
    {
        var fileMatches = string.IsNullOrEmpty(registered.FileSha256)
            || string.Equals(registered.FileSha256, actual.FileSha256, StringComparison.OrdinalIgnoreCase);

        if (string.IsNullOrEmpty(actual.ContentSha256))
        {
            return !string.IsNullOrEmpty(registered.FileSha256) && fileMatches;
        }

        var contentMatches = !string.IsNullOrEmpty(registered.ContentSha256)
            && string.Equals(registered.ContentSha256, actual.ContentSha256, StringComparison.OrdinalIgnoreCase);

        return contentMatches && fileMatches;
    }

    private static TableDiffInfo[] DiffTables(
        IReadOnlyList<FfprDbTableHash>? expectedTables,
        IReadOnlyList<FfprDbTableHash>? actualTables)
    {
        var expectedMap = (expectedTables ?? [])
            .ToDictionary(table => table.Name, StringComparer.Ordinal);
        var actualMap = (actualTables ?? [])
            .ToDictionary(table => table.Name, StringComparer.Ordinal);

        var allNames = new SortedSet<string>(expectedMap.Keys, StringComparer.Ordinal);
        allNames.UnionWith(actualMap.Keys);

        var diffs = new List<TableDiffInfo>();
        foreach (var name in allNames)
        {
            var hasExpected = expectedMap.TryGetValue(name, out var expected);
            var hasActual = actualMap.TryGetValue(name, out var actual);

            if (!hasExpected && hasActual && actual is not null)
            {
                diffs.Add(new TableDiffInfo(name, "added", null, actual.Rows, null, actual.Sha256));
                continue;
            }

            if (hasExpected && expected is not null && !hasActual)
            {
                diffs.Add(new TableDiffInfo(name, "removed", expected.Rows, null, expected.Sha256, null));
                continue;
            }

            if (expected is not null
                && actual is not null
                && (expected.Rows != actual.Rows
                    || !string.Equals(expected.Sha256, actual.Sha256, StringComparison.OrdinalIgnoreCase)))
            {
                diffs.Add(new TableDiffInfo(
                    name,
                    "changed",
                    expected.Rows,
                    actual.Rows,
                    expected.Sha256,
                    actual.Sha256));
            }
        }

        return [.. diffs];
    }

    internal static (string Version, string GitCommit) ReadBuildMetadata()
    {
        var assembly = typeof(KnownBuildsRegistry).Assembly;
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        var version = !string.IsNullOrWhiteSpace(informational)
            ? informational.Split('+')[0]
            : assembly.GetName().Version?.ToString(3) ?? Program.ToolVersion;

        var gitCommit = assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attr => string.Equals(attr.Key, "GitCommit", StringComparison.Ordinal))
            ?.Value;

        return (version, string.IsNullOrWhiteSpace(gitCommit) ? "unknown" : gitCommit);
    }
}
