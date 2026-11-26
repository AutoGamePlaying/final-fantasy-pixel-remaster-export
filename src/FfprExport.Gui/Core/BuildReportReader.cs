using System.Globalization;
using System.Text;
using System.Text.Json;

namespace FfprExport.Gui;

/// <summary>
/// Reads structured build results from *.build.json files produced by ffpr-export build-db
/// and determines known-build verdicts without scraping CLI console output.
/// </summary>
internal static class BuildReportReader
{
    public static string ShortMatched => L.Get("Gui_VerdictShortMatched");
    public static string ShortNewVersion => L.Get("Gui_VerdictShortNewVersion");
    public static string ShortHashMismatch => L.Get("Gui_VerdictShortHashMismatch");

    public static string? FindBuildReportPath(string outputDirectory, string? preferredGameId = null)
    {
        if (string.IsNullOrWhiteSpace(outputDirectory))
        {
            return null;
        }

        var ioDir = PathSupport.ToIoPath(outputDirectory);
        if (!Directory.Exists(ioDir))
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(preferredGameId))
        {
            var preferred = Path.Combine(outputDirectory, $"{preferredGameId.Trim().ToLowerInvariant()}.sqlite.build.json");
            if (File.Exists(PathSupport.ToIoPath(preferred)))
            {
                return preferred;
            }
        }

        var candidates = Directory.EnumerateFiles(ioDir, "*.build.json", SearchOption.TopDirectoryOnly)
            .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (candidates.Length == 0)
        {
            return null;
        }

        return Path.Combine(outputDirectory, Path.GetFileName(candidates[0]));
    }

    public static GuiBuildReport ReadReport(string reportPath)
    {
        var ioPath = PathSupport.ToIoPath(reportPath);
        if (!File.Exists(ioPath))
        {
            throw new FileNotFoundException(L.Format("Gui_ReportNotFound", reportPath), reportPath);
        }

        string json;
        try
        {
            json = File.ReadAllText(ioPath, Encoding.UTF8);
        }
        catch (Exception ex)
        {
            throw new IOException(L.Format("Gui_ReportReadFailed", reportPath, ex.Message), ex);
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException(L.Format("Gui_ReportRootNotObject", reportPath));
            }

            var status = TryGetString(root, "status");
            var gameId = TryGetString(root, "game_id");

            string? steamBuildId = null;
            if (TryGetObject(root, "game_version", out var gvEl)
                && TryGetObject(gvEl, "steam", out var steamEl))
            {
                steamBuildId = TryGetString(steamEl, "build_id");
            }

            var outputDir = Path.GetDirectoryName(reportPath) ?? string.Empty;
            var sqliteFileName = !string.IsNullOrWhiteSpace(gameId)
                ? $"{gameId}.sqlite"
                : Path.GetFileName(reportPath).Replace(".build.json", string.Empty, StringComparison.OrdinalIgnoreCase);

            long? fileSize = null;
            string? fileSha256 = null;
            string? contentSha256 = null;
            if (TryGetObject(root, "database", out var dbEl))
            {
                var parsedFileName = TryGetString(dbEl, "file_name");
                if (!string.IsNullOrWhiteSpace(parsedFileName))
                {
                    sqliteFileName = parsedFileName;
                }

                fileSize = TryGetInt64(dbEl, "size");
                fileSha256 = TryGetString(dbEl, "file_sha256");
                contentSha256 = TryGetString(dbEl, "content_sha256");
            }

            string? comparisonStatus = null;
            string? comparisonMessage = null;
            string? comparisonSource = null;
            string? matchedRegisteredAt = null;
            var fileSha256MismatchOnly = false;
            var tableDiffCount = 0;

            if (TryGetObject(root, "known_build_check", out var checkEl))
            {
                comparisonStatus = TryGetString(checkEl, "status");
                comparisonMessage = TryGetString(checkEl, "message");
                comparisonSource = TryGetString(checkEl, "source");
                matchedRegisteredAt = TryGetString(checkEl, "matched_registered_at");
                if (checkEl.TryGetProperty("file_sha256_mismatch_only", out var mismatchOnlyEl)
                    && mismatchOnlyEl.ValueKind is JsonValueKind.True or JsonValueKind.False)
                {
                    fileSha256MismatchOnly = mismatchOnlyEl.GetBoolean();
                }

                if (checkEl.TryGetProperty("table_diffs", out var diffsEl) && diffsEl.ValueKind == JsonValueKind.Array)
                {
                    tableDiffCount = diffsEl.GetArrayLength();
                }
            }

            long? totalElapsedMs = null;
            if (TryGetObject(root, "timings_ms", out var timingsEl))
            {
                totalElapsedMs = TryGetInt64(timingsEl, "total");
            }

            int? inventoryExit = null;
            int? exportExit = null;
            if (TryGetObject(root, "export_exit_codes", out var codesEl))
            {
                inventoryExit = TryGetInt32(codesEl, "inventory");
                exportExit = TryGetInt32(codesEl, "export");
            }

            var sqlitePath = string.IsNullOrEmpty(outputDir)
                ? sqliteFileName
                : Path.Combine(outputDir, sqliteFileName);

            return new GuiBuildReport(
                reportPath,
                status,
                gameId,
                steamBuildId,
                sqlitePath,
                fileSize,
                fileSha256,
                contentSha256,
                comparisonStatus,
                comparisonMessage,
                comparisonSource,
                matchedRegisteredAt,
                fileSha256MismatchOnly,
                tableDiffCount,
                totalElapsedMs,
                inventoryExit,
                exportExit);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException(L.Format("Gui_ReportJsonParseFailed", reportPath, ex.Message), ex);
        }
    }

    public static bool TryReadReportFromDirectory(
        string outputDirectory,
        string? preferredGameId,
        out GuiBuildReport? report,
        out string? errorMessage)
    {
        report = null;
        errorMessage = null;

        var reportPath = FindBuildReportPath(outputDirectory, preferredGameId);
        if (reportPath is null)
        {
            errorMessage = L.Format("Gui_ReportMissingInDir", outputDirectory);
            return false;
        }

        try
        {
            report = ReadReport(reportPath);
            return true;
        }
        catch (Exception ex)
        {
            errorMessage = ex.Message;
            return false;
        }
    }

    public static GuiGameExportResult ReevaluateForCurrentLanguage(GuiGameExportResult existing)
    {
        return EvaluateOutcome(
            existing.Game,
            existing.OutputDirectory,
            existing.ExitCode,
            existing.Cancelled,
            existing.Report,
            existing.RawReadError);
    }

    public static GuiGameExportResult EvaluateOutcome(
        GuiGameEntry game,
        string outputDirectory,
        int exitCode,
        bool cancelled,
        GuiBuildReport? report,
        string? readError = null)
    {
        if (cancelled)
        {
            return new GuiGameExportResult(
                game,
                outputDirectory,
                exitCode,
                Cancelled: true,
                report,
                KnownBuildVerdictKind.Cancelled,
                "✗",
                L.Get("Gui_VerdictShortCancelled"),
                L.Get("Gui_VerdictDetailCancelled"),
                L.Get("Gui_VerdictErrorUserCancelled"),
                RawReadError: readError);
        }

        if (exitCode >= 2)
        {
            var reason = string.IsNullOrWhiteSpace(readError)
                ? L.Format("Gui_VerdictCliFailed", exitCode)
                : L.Format("Gui_VerdictCliFailedWithReason", exitCode, readError);
            return new GuiGameExportResult(
                game,
                outputDirectory,
                exitCode,
                Cancelled: false,
                report,
                KnownBuildVerdictKind.Failed,
                "✗",
                L.Get("Gui_VerdictShortFailed"),
                L.Format("Gui_VerdictDetailFailed", reason),
                reason,
                RawReadError: readError);
        }

        if (report is null)
        {
            var reason = string.IsNullOrWhiteSpace(readError)
                ? L.Format("Gui_VerdictMissingReport", outputDirectory)
                : readError;
            return new GuiGameExportResult(
                game,
                outputDirectory,
                exitCode,
                Cancelled: false,
                null,
                KnownBuildVerdictKind.Failed,
                "✗",
                L.Get("Gui_VerdictShortFailed"),
                L.Format("Gui_VerdictDetailFailed", reason),
                reason,
                RawReadError: readError);
        }

        if (!string.Equals(report.Status, "completed", StringComparison.OrdinalIgnoreCase))
        {
            var reason = L.Format("Gui_VerdictStatusNotCompleted", report.Status ?? L.Get("Common_Null"), report.ReportPath);
            return new GuiGameExportResult(
                game,
                outputDirectory,
                exitCode,
                Cancelled: false,
                report,
                KnownBuildVerdictKind.Failed,
                "✗",
                L.Get("Gui_VerdictShortFailed"),
                L.Format("Gui_VerdictDetailFailed", reason),
                reason,
                RawReadError: readError);
        }

        var partialSuffix = exitCode == 1 ? L.Get("Gui_VerdictPartialSuffix") : string.Empty;
        var sourceNote = string.IsNullOrWhiteSpace(report.ComparisonSource)
            ? null
            : L.Format("Gui_VerdictSourcePart", report.ComparisonSource);

        var normalizedStatus = (report.ComparisonStatus ?? string.Empty).Trim().ToLowerInvariant();
        return normalizedStatus switch
        {
            "matched" => new GuiGameExportResult(
                game,
                outputDirectory,
                exitCode,
                Cancelled: false,
                report,
                KnownBuildVerdictKind.Matched,
                "✓",
                ShortMatched + partialSuffix,
                FormatMatchedDetail(report, partialSuffix, sourceNote),
                null,
                RawReadError: readError),
            "new_game_version" => new GuiGameExportResult(
                game,
                outputDirectory,
                exitCode,
                Cancelled: false,
                report,
                KnownBuildVerdictKind.NewGameVersion,
                "✓",
                ShortNewVersion + partialSuffix,
                sourceNote is null
                    ? $"✓ {ShortNewVersion}{partialSuffix}"
                    : L.Format("Gui_VerdictDetailWithNotes", "✓", ShortNewVersion, partialSuffix, sourceNote),
                null,
                RawReadError: readError),
            "hash_mismatch" => new GuiGameExportResult(
                game,
                outputDirectory,
                exitCode,
                Cancelled: false,
                report,
                KnownBuildVerdictKind.HashMismatch,
                "✗",
                ShortHashMismatch + partialSuffix,
                FormatMismatchDetail(report, partialSuffix, sourceNote),
                null,
                RawReadError: readError),
            _ => new GuiGameExportResult(
                game,
                outputDirectory,
                exitCode,
                Cancelled: false,
                report,
                KnownBuildVerdictKind.Unknown,
                "✗",
                L.Get("Gui_VerdictShortUnknown") + partialSuffix,
                L.Format("Gui_VerdictDetailUnknown", report.ComparisonStatus ?? "null", partialSuffix),
                null,
                RawReadError: readError),
        };
    }

    public static string FormatClipboardHashText(IReadOnlyList<GuiGameExportResult> results)
    {
        var blocks = new List<string>();
        foreach (var item in results)
        {
            if (item.Report is null
                && string.IsNullOrWhiteSpace(item.Report?.FileSha256)
                && string.IsNullOrWhiteSpace(item.Report?.ContentSha256))
            {
                continue;
            }

            var sb = new StringBuilder();
            sb.AppendLine(L.Format(
                "Gui_ClipboardHeader",
                item.Game.GameCode,
                item.Game.DisplayName,
                item.EffectiveBuildId,
                item.VerdictDisplay));
            sb.AppendLine(L.Format("Gui_ClipboardDbPath", item.SqlitePath));
            sb.AppendLine($"file_sha256:    {item.Report?.FileSha256 ?? L.Get("Common_NoneParen")}");
            sb.Append($"content_sha256: {item.Report?.ContentSha256 ?? L.Get("Common_NoneParen")}");
            blocks.Add(sb.ToString());
        }

        return string.Join(Environment.NewLine + Environment.NewLine, blocks);
    }

    private static string FormatMatchedDetail(GuiBuildReport report, string partialSuffix, string? sourceNote)
    {
        var notes = new List<string>();
        if (!string.IsNullOrWhiteSpace(report.MatchedRegisteredAt))
        {
            notes.Add(L.Format("Gui_VerdictRegisteredDatePart", report.MatchedRegisteredAt));
        }

        if (!string.IsNullOrEmpty(sourceNote))
        {
            notes.Add(sourceNote);
        }

        return notes.Count == 0
            ? $"✓ {ShortMatched}{partialSuffix}"
            : L.Format("Gui_VerdictDetailWithNotes", "✓", ShortMatched, partialSuffix, string.Join(L.Get("Gui_DetailListSeparator"), notes));
    }

    private static string FormatMismatchDetail(GuiBuildReport report, string partialSuffix, string? sourceNote)
    {
        var notes = new List<string>();
        if (report.FileSha256MismatchOnly)
        {
            notes.Add(L.Get("Gui_VerdictFileHashOnlyNote"));
        }
        else if (report.TableDiffCount > 0)
        {
            notes.Add(L.Format("Gui_VerdictTableDiffCountNote", report.TableDiffCount));
        }

        if (!string.IsNullOrWhiteSpace(report.MatchedRegisteredAt))
        {
            notes.Add(L.Format("Gui_VerdictReferenceDatePart", report.MatchedRegisteredAt));
        }

        if (!string.IsNullOrEmpty(sourceNote))
        {
            notes.Add(sourceNote);
        }

        return notes.Count == 0
            ? $"✗ {ShortHashMismatch}{partialSuffix}"
            : L.Format("Gui_VerdictDetailWithNotes", "✗", ShortHashMismatch, partialSuffix, string.Join(L.Get("Gui_DetailListSeparator"), notes));
    }

    private static bool TryGetObject(JsonElement element, string propertyName, out JsonElement value)
    {
        if (element.TryGetProperty(propertyName, out value) && value.ValueKind == JsonValueKind.Object)
        {
            return true;
        }

        value = default;
        return false;
    }

    private static string? TryGetString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.String
            ? prop.GetString()
            : null;
    }

    private static long? TryGetInt64(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var prop) && prop.TryGetInt64(out var val)
            ? val
            : null;
    }

    private static int? TryGetInt32(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var prop) && prop.TryGetInt32(out var val)
            ? val
            : null;
    }
}
