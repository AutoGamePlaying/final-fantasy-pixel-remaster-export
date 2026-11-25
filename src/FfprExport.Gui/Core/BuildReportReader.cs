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
    public const string ShortMatched = "与登记的版本一致";
    public const string ShortNewVersion = "新版本，未登记";
    public const string ShortHashMismatch = "哈希不同";

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
            throw new FileNotFoundException($"找不到构建报告文件：{reportPath}", reportPath);
        }

        string json;
        try
        {
            json = File.ReadAllText(ioPath, Encoding.UTF8);
        }
        catch (Exception ex)
        {
            throw new IOException($"读取构建报告失败（{reportPath}）：{ex.Message}", ex);
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException($"构建报告根节点必须是 JSON 对象：{reportPath}");
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
            throw new InvalidDataException($"解析构建报告 JSON 失败（{reportPath}）：{ex.Message}", ex);
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
            errorMessage = $"输出目录中未找到 *.build.json 构建报告：{outputDirectory}";
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
                "已取消",
                "✗ 已取消：导出任务被用户中断",
                "用户已取消导出");
        }

        if (exitCode >= 2)
        {
            var reason = string.IsNullOrWhiteSpace(readError)
                ? string.Create(CultureInfo.InvariantCulture, $"命令行导出失败（退出码 {exitCode}），请查看上方日志")
                : string.Create(CultureInfo.InvariantCulture, $"命令行导出失败（退出码 {exitCode}）：{readError}");
            return new GuiGameExportResult(
                game,
                outputDirectory,
                exitCode,
                Cancelled: false,
                report,
                KnownBuildVerdictKind.Failed,
                "✗",
                "导出失败",
                $"✗ 导出失败：{reason}",
                reason);
        }

        if (report is null)
        {
            var reason = string.IsNullOrWhiteSpace(readError)
                ? $"未在输出目录读取到 *.build.json：{outputDirectory}"
                : readError;
            return new GuiGameExportResult(
                game,
                outputDirectory,
                exitCode,
                Cancelled: false,
                null,
                KnownBuildVerdictKind.Failed,
                "✗",
                "导出失败",
                $"✗ 导出失败：{reason}",
                reason);
        }

        if (!string.Equals(report.Status, "completed", StringComparison.OrdinalIgnoreCase))
        {
            var reason = $"构建报告状态不是 completed（当前为 {report.Status ?? "空"}）：{report.ReportPath}";
            return new GuiGameExportResult(
                game,
                outputDirectory,
                exitCode,
                Cancelled: false,
                report,
                KnownBuildVerdictKind.Failed,
                "✗",
                "导出失败",
                $"✗ 导出失败：{reason}",
                reason);
        }

        var partialSuffix = exitCode == 1 ? "（存在部分资源告警）" : string.Empty;
        var sourceSuffix = string.IsNullOrWhiteSpace(report.ComparisonSource)
            ? string.Empty
            : $"，来源：{report.ComparisonSource}";

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
                FormatMatchedDetail(report, partialSuffix, sourceSuffix),
                null),
            "new_game_version" => new GuiGameExportResult(
                game,
                outputDirectory,
                exitCode,
                Cancelled: false,
                report,
                KnownBuildVerdictKind.NewGameVersion,
                "✓",
                ShortNewVersion + partialSuffix,
                $"✓ {ShortNewVersion}{partialSuffix}" + (string.IsNullOrEmpty(sourceSuffix) ? string.Empty : $"（{sourceSuffix.TrimStart('，')}）"),
                null),
            "hash_mismatch" => new GuiGameExportResult(
                game,
                outputDirectory,
                exitCode,
                Cancelled: false,
                report,
                KnownBuildVerdictKind.HashMismatch,
                "✗",
                ShortHashMismatch + partialSuffix,
                FormatMismatchDetail(report, partialSuffix, sourceSuffix),
                null),
            _ => new GuiGameExportResult(
                game,
                outputDirectory,
                exitCode,
                Cancelled: false,
                report,
                KnownBuildVerdictKind.Unknown,
                "✗",
                "未知比对结论" + partialSuffix,
                $"✗ 未知比对结论（status={report.ComparisonStatus ?? "null"}）{partialSuffix}",
                null),
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
            sb.AppendLine($"[{item.Game.GameCode}] {item.Game.DisplayName} | 构建号: {item.EffectiveBuildId} | 结论: {item.VerdictDisplay}");
            sb.AppendLine($"数据库路径: {item.SqlitePath}");
            sb.AppendLine($"file_sha256:    {item.Report?.FileSha256 ?? "(无)"}");
            sb.Append($"content_sha256: {item.Report?.ContentSha256 ?? "(无)"}");
            blocks.Add(sb.ToString());
        }

        return string.Join(Environment.NewLine + Environment.NewLine, blocks);
    }

    private static string FormatMatchedDetail(GuiBuildReport report, string partialSuffix, string sourceSuffix)
    {
        var notes = new List<string>();
        if (!string.IsNullOrWhiteSpace(report.MatchedRegisteredAt))
        {
            notes.Add($"登记日期：{report.MatchedRegisteredAt}");
        }

        if (!string.IsNullOrEmpty(sourceSuffix))
        {
            notes.Add(sourceSuffix.TrimStart('，'));
        }

        return notes.Count == 0
            ? $"✓ {ShortMatched}{partialSuffix}"
            : $"✓ {ShortMatched}{partialSuffix}（{string.Join("，", notes)}）";
    }

    private static string FormatMismatchDetail(GuiBuildReport report, string partialSuffix, string sourceSuffix)
    {
        var notes = new List<string>();
        if (report.FileSha256MismatchOnly)
        {
            notes.Add("仅文件哈希不同，内容哈希一致");
        }
        else if (report.TableDiffCount > 0)
        {
            notes.Add(string.Create(CultureInfo.InvariantCulture, $"差异表 {report.TableDiffCount} 张"));
        }

        if (!string.IsNullOrWhiteSpace(report.MatchedRegisteredAt))
        {
            notes.Add($"对照登记：{report.MatchedRegisteredAt}");
        }

        if (!string.IsNullOrEmpty(sourceSuffix))
        {
            notes.Add(sourceSuffix.TrimStart('，'));
        }

        return notes.Count == 0
            ? $"✗ {ShortHashMismatch}{partialSuffix}"
            : $"✗ {ShortHashMismatch}{partialSuffix}（{string.Join("，", notes)}）";
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
