using System.Text.Json.Serialization;

namespace FfprExport.Db;

internal sealed record DbBuildOptions(
    string ExportDirectory,
    string OutputPath,
    int Jobs);

internal sealed record TableHash(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("rows")] long Rows,
    [property: JsonPropertyName("sha256")] string Sha256);

internal sealed record DbHashReport(
    [property: JsonPropertyName("format")] int Format,
    [property: JsonPropertyName("file_sha256")] string FileSha256,
    [property: JsonPropertyName("content_sha256")] string ContentSha256,
    [property: JsonPropertyName("schema_version")] int SchemaVersion,
    [property: JsonPropertyName("sqlite_version")] string SqliteVersion,
    [property: JsonPropertyName("tables")] IReadOnlyList<TableHash> Tables);

internal sealed record DbContentHashReport(
    string ContentSha256,
    int SchemaVersion,
    string SqliteVersion,
    IReadOnlyList<TableHash> Tables);

internal sealed record DbStageReport(
    [property: JsonPropertyName("stage")] string Stage,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("rows")] long Rows,
    [property: JsonPropertyName("millis")] long Millis,
    [property: JsonPropertyName("note")] string Note);

internal sealed record DbSourceRecord(
    [property: JsonPropertyName("stage")] string Stage,
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("sha256")] string Sha256,
    [property: JsonPropertyName("file_count")] long FileCount);

internal sealed record DbBuildIssue(
    string Stage,
    string Kind,
    string Subject,
    string Detail);

internal sealed record DbIssueSummary(
    [property: JsonPropertyName("stage")] string Stage,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("count")] long Count);

internal sealed record DbManifestToolInfo(
    [property: JsonPropertyName("tool")] string Tool,
    [property: JsonPropertyName("tool_version")] string ToolVersion,
    [property: JsonPropertyName("schema_version")] int SchemaVersion,
    [property: JsonPropertyName("sqlite_version")] string SqliteVersion,
    [property: JsonPropertyName("jobs")] int Jobs,
    [property: JsonPropertyName("total_millis")] long TotalMillis);

internal sealed record DbBuildManifest(
    [property: JsonPropertyName("format")] int Format,
    [property: JsonPropertyName("built_at")] string BuiltAt,
    [property: JsonPropertyName("game_id")] string GameId,
    [property: JsonPropertyName("steam_build_id")] string? SteamBuildId,
    [property: JsonPropertyName("unity_version")] string? UnityVersion,
    [property: JsonPropertyName("tool")] DbManifestToolInfo Tool,
    [property: JsonPropertyName("sources")] IReadOnlyList<DbSourceRecord> Sources,
    [property: JsonPropertyName("stages")] IReadOnlyList<DbStageReport> Stages,
    [property: JsonPropertyName("issues")] IReadOnlyList<DbIssueSummary> Issues,
    [property: JsonPropertyName("total_issues")] long TotalIssues,
    [property: JsonPropertyName("hash")] DbHashReport Hash);

internal sealed record DbBuildReport(
    string OutputPath,
    string ManifestPath,
    string GameId,
    int SchemaVersion,
    string SqliteVersion,
    long FileSizeBytes,
    string FileSha256,
    string ContentSha256,
    long TotalMillis,
    IReadOnlyList<DbStageReport> Stages,
    IReadOnlyList<DbIssueSummary> Issues,
    long TotalIssues);

internal sealed record DbTableCount(
    string Name,
    string Area,
    long Rows);

internal sealed record DbStatsReport(
    string Path,
    long FileSizeBytes,
    string SqliteVersion,
    IReadOnlyDictionary<string, string> Meta,
    IReadOnlyList<DbStageReport> Stages,
    IReadOnlyList<DbIssueSummary> Issues,
    long TotalIssues,
    IReadOnlyList<DbTableCount> Tables);

internal sealed record QueryPlanStep(
    int Id,
    int Parent,
    string Detail);

internal sealed record MapGridCellResult(
    int SubmapId,
    string SubmapName,
    string LayerKind,
    string LayerName,
    int X,
    int Y,
    int Value);

internal sealed record TextSearchHit(
    string Scope,
    string Key,
    string Lang,
    string ValuePlain);
