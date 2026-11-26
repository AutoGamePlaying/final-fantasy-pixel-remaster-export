namespace FfprExport.Gui;

internal enum OutputDirectoryState
{
    EmptyOrMissing,
    OwnBuildDbDirectory,
    ForeignNonEmptyDirectory,
}

internal enum KnownBuildVerdictKind
{
    Matched,
    NewGameVersion,
    HashMismatch,
    Unknown,
    Failed,
    Cancelled,
}

internal sealed record GuiGameEntry(
    GameSpec Spec,
    string DisplayName,
    string GameDirectory,
    string? SteamBuildId,
    string? AppManifestPath,
    bool IsManual)
{
    public string GameCode => Spec.Id.ToUpperInvariant();

    public string DisplayLabel => $"{DisplayName} ({GameCode})";

    public string BuildIdDisplay => string.IsNullOrWhiteSpace(SteamBuildId) ? L.Get("Common_Unknown") : SteamBuildId.Trim();
}

internal sealed record PlannedGameExport(
    GuiGameEntry Game,
    string OutputDirectory,
    OutputDirectoryState DirectoryState,
    bool UseForceFlag);

internal sealed record ProgressStepUpdate(
    int CurrentStep,
    int TotalSteps,
    string? Description);

internal sealed record GuiBuildReport(
    string ReportPath,
    string? Status,
    string? GameId,
    string? SteamBuildId,
    string SqlitePath,
    long? FileSize,
    string? FileSha256,
    string? ContentSha256,
    string? ComparisonStatus,
    string? ComparisonMessage,
    string? ComparisonSource,
    string? MatchedRegisteredAt,
    bool FileSha256MismatchOnly,
    int TableDiffCount,
    long? TotalElapsedMs,
    int? InventoryExitCode,
    int? ExportExitCode);

internal sealed record GuiGameExportResult(
    GuiGameEntry Game,
    string OutputDirectory,
    int ExitCode,
    bool Cancelled,
    GuiBuildReport? Report,
    KnownBuildVerdictKind Verdict,
    string VerdictSymbol,
    string VerdictShortText,
    string VerdictDetailText,
    string? ErrorMessage,
    string? RawReadError = null)
{
    public string SqlitePath => Report?.SqlitePath ?? Path.Combine(OutputDirectory, $"{Game.Spec.Id}.sqlite");

    public string EffectiveBuildId => !string.IsNullOrWhiteSpace(Report?.SteamBuildId)
        ? Report.SteamBuildId.Trim()
        : Game.BuildIdDisplay;

    public string VerdictDisplay => $"{VerdictSymbol} {VerdictShortText}";
}
