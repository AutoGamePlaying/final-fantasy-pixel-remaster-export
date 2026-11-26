using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace FfprExport;

internal sealed record WizardEnvironment(
    TextReader Input,
    TextWriter Output,
    TextWriter Error,
    string BaseDirectory,
    Func<IReadOnlyList<DiscoveredSteamGame>> DiscoverGames,
    Func<string, DiscoveredSteamGame> InspectManualGameDirectory,
    Func<string[], Task<int>> RunBuildDb,
    Func<string>? ResolveDefaultOutputRoot = null);

/// <summary>
/// Interactive Chinese-language wizard launched when ffpr-export is started with no arguments
/// (for example by double-clicking ffpr-export.exe in Windows Explorer).
/// Delegates database building to the exact same BuildDbCommand entry point used by CLI mode.
/// </summary>
internal static class InteractiveWizard
{
    private enum OutputDirectoryState
    {
        EmptyOrMissing,
        OwnBuildDbDirectory,
        ForeignNonEmptyDirectory,
    }

    private sealed record PlannedBuild(
        DiscoveredSteamGame Game,
        string OutputDirectory,
        bool ForceOverwrite);

    private sealed record ParsedBuildReport(
        string SqlitePath,
        string? FileSha256,
        string? ContentSha256,
        string? ComparisonStatus,
        string? ComparisonMessage,
        string? ComparisonSource,
        string? MatchedRegisteredAt,
        int TableDiffCount,
        string? StageTimingsSummary);

    private sealed record GameRunOutcome(
        PlannedBuild Plan,
        int ExitCode,
        double ElapsedSeconds,
        ParsedBuildReport? Report,
        string? ErrorMessage,
        string? NextStepAdvice);

    public static Task<int> RunAsync()
    {
        var defaultEnv = new WizardEnvironment(
            Input: Console.In,
            Output: Console.Out,
            Error: Console.Error,
            BaseDirectory: AppContext.BaseDirectory,
            DiscoverGames: SteamLocator.DiscoverGames,
            InspectManualGameDirectory: InspectManualGameDirectoryDefault,
            RunBuildDb: BuildDbCommand.RunAsync);

        return RunAsync(defaultEnv);
    }

    internal static async Task<int> RunAsync(WizardEnvironment env)
    {
        try
        {
            return await RunCoreAsync(env);
        }
        catch (Exception ex)
        {
            env.Error.WriteLine($"错误：向导运行发生意外异常：{ex.Message}");
            env.Output.WriteLine();
            env.Output.WriteLine($"错误：{ex.Message}");
            env.Output.WriteLine($"下一步建议：{SuggestNextStep(ex)}");
            WaitForExitPrompt(env);
            return 2;
        }
    }

    /// <summary>
    /// Strips leading/trailing whitespace and matching outer quote pairs (ASCII or typographic quotes)
    /// that users often include when copying paths from File Explorer or terminals.
    /// </summary>
    internal static string NormalizeUserPath(string? rawInput)
    {
        if (string.IsNullOrWhiteSpace(rawInput))
        {
            return string.Empty;
        }

        var text = rawInput.Trim();
        while (text.Length >= 2)
        {
            var first = text[0];
            var last = text[^1];
            var isMatchingQuotePair = (first == '"' && last == '"')
                || (first == '\'' && last == '\'')
                || (first == '“' && last == '”')
                || (first == '‘' && last == '’');
            if (!isMatchingQuotePair)
            {
                break;
            }

            text = text[1..^1].Trim();
        }

        return text;
    }

    internal static string BuildDefaultGameFolderName(string gameId, string? steamBuildId)
    {
        return AgpPaths.BuildGameFolderName(gameId, steamBuildId);
    }

    private static async Task<int> RunCoreAsync(WizardEnvironment env)
    {
        var output = env.Output;
        output.WriteLine("============================================================");
        output.WriteLine($"{OutputGuard.ToolName} {Program.ToolVersion} — FINAL FANTASY I–VI Pixel Remaster 导出与建库向导");
        output.WriteLine("============================================================");
        output.WriteLine("提示：按提示完成选择即可生成 SQLite 数据库；命令行用法请运行 ffpr-export --help。");
        output.WriteLine();

        // Step 1: Discover Steam games or prompt user for a manual game directory.
        output.WriteLine("[步骤 1/4] 正在自动定位 Steam 与已安装的 FF1–FF6 游戏……");
        IReadOnlyList<DiscoveredSteamGame> discovered;
        try
        {
            discovered = env.DiscoverGames();
        }
        catch (Exception ex)
        {
            output.WriteLine($"提示：自动扫描 Steam 目录时出现问题（{ex.Message}），将改为手动输入目录。");
            discovered = [];
        }

        var selectedGames = SelectGames(env, discovered);
        if (selectedGames is null || selectedGames.Count == 0)
        {
            output.WriteLine("已取消操作。");
            WaitForExitPrompt(env);
            return 0;
        }

        output.WriteLine();

        // Step 2: Select output directory (default: <agpHome>/ffpr-export/output/<gameId>-<steamBuildId>/).
        var plannedBuilds = SelectOutputDirectories(env, selectedGames);
        if (plannedBuilds is null || plannedBuilds.Count == 0)
        {
            output.WriteLine("已取消操作。");
            WaitForExitPrompt(env);
            return 0;
        }

        output.WriteLine();

        // Step 3: Confirm before starting.
        if (!ConfirmStart(env, plannedBuilds))
        {
            output.WriteLine("已取消操作。");
            WaitForExitPrompt(env);
            return 0;
        }

        output.WriteLine();

        // Step 4: Run build-db for each selected game using the shared BuildDbCommand entry point.
        output.WriteLine("[步骤 4/4] 开始导出并构建数据库……");
        var outcomes = new List<GameRunOutcome>(plannedBuilds.Count);
        for (var i = 0; i < plannedBuilds.Count; i++)
        {
            var plan = plannedBuilds[i];
            if (plannedBuilds.Count > 1)
            {
                output.WriteLine();
                output.WriteLine("------------------------------------------------------------");
                output.WriteLine(
                    $"[{i + 1}/{plannedBuilds.Count}] 开始构建：{plan.Game.DisplayName}（{plan.Game.Spec.Id}）");
                output.WriteLine("------------------------------------------------------------");
            }

            var outcome = await ExecuteSingleBuildAsync(env, plan);
            outcomes.Add(outcome);
        }

        PrintFinalSummary(output, outcomes);
        WaitForExitPrompt(env);

        if (outcomes.Any(o => o.ErrorMessage is not null || o.ExitCode >= 2))
        {
            return 2;
        }

        return outcomes.Any(o => o.ExitCode != 0) ? 1 : 0;
    }

    private static List<DiscoveredSteamGame>? SelectGames(
        WizardEnvironment env,
        IReadOnlyList<DiscoveredSteamGame> discovered)
    {
        var output = env.Output;
        if (discovered.Count == 0)
        {
            output.WriteLine("未在本地 Steam 游戏库中自动找到 FINAL FANTASY I–VI Pixel Remaster。");
            var manualGame = PromptManualGameDirectory(env);
            return manualGame is null ? null : [manualGame];
        }

        output.WriteLine($"找到 {discovered.Count} 个支持的游戏：");
        for (var i = 0; i < discovered.Count; i++)
        {
            var game = discovered[i];
            var buildDisplay = string.IsNullOrWhiteSpace(game.SteamBuildId) ? "未知" : game.SteamBuildId;
            output.WriteLine($"  [{i + 1}] {game.DisplayName}（{game.Spec.Id}） | Steam 构建号：{buildDisplay}");
            output.WriteLine($"      目录：{game.GameDirectory}");
        }

        var allOptionNumber = discovered.Count + 1;
        output.WriteLine($"  [{allOptionNumber}] 全部（依次构建以上 {discovered.Count} 个游戏）");
        output.WriteLine("  [0] 手动输入其他游戏目录");
        output.WriteLine();

        while (true)
        {
            var singleChoiceHint = discovered.Count == 1
                ? "1 选择该游戏"
                : $"1–{discovered.Count} 单个游戏";
            output.Write(
                $"请输入编号选择游戏（{singleChoiceHint}，{allOptionNumber} 全部，0 手动输入，q 退出）：");
            var line = env.Input.ReadLine();
            if (line is null)
            {
                return null;
            }

            var trimmed = line.Trim();
            if (IsCancelToken(trimmed))
            {
                return null;
            }

            if (string.Equals(trimmed, "all", StringComparison.OrdinalIgnoreCase)
                || string.Equals(trimmed, "全部", StringComparison.Ordinal))
            {
                return [.. discovered];
            }

            if (int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var choice))
            {
                if (choice == 0)
                {
                    var manualGame = PromptManualGameDirectory(env);
                    return manualGame is null ? null : [manualGame];
                }

                if (choice >= 1 && choice <= discovered.Count)
                {
                    return [discovered[choice - 1]];
                }

                if (choice == allOptionNumber)
                {
                    return [.. discovered];
                }
            }

            output.WriteLine($"输入无效，请输入 0 到 {allOptionNumber} 之间的编号（或输入 q 退出）。");
        }
    }

    private static DiscoveredSteamGame? PromptManualGameDirectory(WizardEnvironment env)
    {
        var output = env.Output;
        while (true)
        {
            output.WriteLine("请粘贴 Steam 游戏目录路径（支持带空格或两端带引号，直接回车退出）：");
            output.Write("> ");
            var line = env.Input.ReadLine();
            if (line is null)
            {
                return null;
            }

            var normalized = NormalizeUserPath(line);
            if (string.IsNullOrEmpty(normalized) || IsCancelToken(normalized))
            {
                return null;
            }

            string fullPath;
            try
            {
                fullPath = PathSupport.TrimTrailingSeparators(Path.GetFullPath(normalized));
            }
            catch (Exception ex)
            {
                output.WriteLine($"错误：路径格式无效（{ex.Message}），请重新输入。");
                continue;
            }

            if (!Directory.Exists(PathSupport.ToIoPath(fullPath)))
            {
                output.WriteLine($"错误：目录不存在：{fullPath}，请检查路径后重新输入。");
                continue;
            }

            try
            {
                var inspected = env.InspectManualGameDirectory(fullPath);
                var buildDisplay = string.IsNullOrWhiteSpace(inspected.SteamBuildId) ? "未知" : inspected.SteamBuildId;
                output.WriteLine($"已识别游戏：{inspected.DisplayName}（{inspected.Spec.Id}，Steam 构建号：{buildDisplay}）");
                return inspected;
            }
            catch (Exception ex)
            {
                output.WriteLine($"错误：{ex.Message}");
                output.WriteLine("请确认目录是 FINAL FANTASY I–VI Pixel Remaster 的安装根目录（含 *_Data 文件夹），然后重新输入。");
            }
        }
    }

    private static List<PlannedBuild>? SelectOutputDirectories(
        WizardEnvironment env,
        IReadOnlyList<DiscoveredSteamGame> selectedGames)
    {
        var output = env.Output;
        var defaultOutputRoot = env.ResolveDefaultOutputRoot is not null
            ? PathSupport.TrimTrailingSeparators(Path.GetFullPath(env.ResolveDefaultOutputRoot()))
            : AgpPaths.GetOutputRoot(
                isWindowsOverride: null,
                getBaseDirectory: () => env.BaseDirectory);

        if (selectedGames.Count == 1)
        {
            var game = selectedGames[0];
            var defaultDir = Path.Combine(
                defaultOutputRoot,
                BuildDefaultGameFolderName(game.Spec.Id, game.SteamBuildId));

            output.WriteLine("[步骤 2/4] 设置输出目录");
            output.WriteLine($"默认输出目录：{defaultDir}");

            while (true)
            {
                output.Write("直接回车使用默认目录，或粘贴其他输出目录路径（可带引号）：");
                var line = env.Input.ReadLine();
                if (line is null)
                {
                    return null;
                }

                var trimmed = line.Trim();
                if (IsCancelToken(trimmed))
                {
                    return null;
                }

                var normalized = NormalizeUserPath(line);
                string chosenDir;
                try
                {
                    chosenDir = string.IsNullOrEmpty(normalized)
                        ? defaultDir
                        : PathSupport.TrimTrailingSeparators(Path.GetFullPath(normalized));
                    OutputGuard.ValidateOutputPath(chosenDir, game.GameDirectory);
                }
                catch (Exception ex)
                {
                    output.WriteLine($"错误：输出目录无效：{ex.Message}，请重新输入。");
                    continue;
                }

                var state = InspectOutputDirectoryState(chosenDir);
                if (state == OutputDirectoryState.ForeignNonEmptyDirectory)
                {
                    output.WriteLine($"错误：输出目录非空且不是 {OutputGuard.ToolName} 的建库目录：{chosenDir}");
                    output.WriteLine("请换一个空目录或新的目录路径。");
                    continue;
                }

                var force = state == OutputDirectoryState.OwnBuildDbDirectory;
                if (force)
                {
                    output.WriteLine("提示：该目录已包含上次建库结果，确认后将自动清空并重新构建。");
                }

                return [new PlannedBuild(game, chosenDir, force)];
            }
        }

        output.WriteLine("[步骤 2/4] 设置批量输出根目录");
        output.WriteLine($"默认输出根目录：{defaultOutputRoot}");
        output.WriteLine("各游戏将分别输出到对应的子目录：");
        foreach (var game in selectedGames)
        {
            var subDir = Path.Combine(
                defaultOutputRoot,
                BuildDefaultGameFolderName(game.Spec.Id, game.SteamBuildId));
            output.WriteLine($"  - {game.DisplayName}（{game.Spec.Id}） -> {subDir}");
        }

        while (true)
        {
            output.Write("直接回车使用默认根目录，或粘贴其他输出根目录路径（可带引号）：");
            var line = env.Input.ReadLine();
            if (line is null)
            {
                return null;
            }

            var trimmed = line.Trim();
            if (IsCancelToken(trimmed))
            {
                return null;
            }

            var normalized = NormalizeUserPath(line);
            string chosenRoot;
            try
            {
                chosenRoot = string.IsNullOrEmpty(normalized)
                    ? defaultOutputRoot
                    : PathSupport.TrimTrailingSeparators(Path.GetFullPath(normalized));
            }
            catch (Exception ex)
            {
                output.WriteLine($"错误：输出根目录格式无效：{ex.Message}，请重新输入。");
                continue;
            }

            var plans = new List<PlannedBuild>(selectedGames.Count);
            var valid = true;
            foreach (var game in selectedGames)
            {
                var targetDir = Path.Combine(
                    chosenRoot,
                    BuildDefaultGameFolderName(game.Spec.Id, game.SteamBuildId));
                try
                {
                    OutputGuard.ValidateOutputPath(targetDir, game.GameDirectory);
                }
                catch (Exception ex)
                {
                    output.WriteLine($"错误：{game.Spec.Id} 输出目录无效：{ex.Message}，请重新选择输出根目录。");
                    valid = false;
                    break;
                }

                var state = InspectOutputDirectoryState(targetDir);
                if (state == OutputDirectoryState.ForeignNonEmptyDirectory)
                {
                    output.WriteLine($"错误：子目录非空且不是 {OutputGuard.ToolName} 的建库目录：{targetDir}");
                    output.WriteLine("请换一个空目录或新的输出根目录。");
                    valid = false;
                    break;
                }

                plans.Add(new PlannedBuild(game, targetDir, state == OutputDirectoryState.OwnBuildDbDirectory));
            }

            if (!valid)
            {
                continue;
            }

            if (plans.Any(p => p.ForceOverwrite))
            {
                output.WriteLine("提示：部分游戏输出子目录已存在上次建库结果，确认后将自动覆盖重建。");
            }

            return plans;
        }
    }

    private static bool ConfirmStart(WizardEnvironment env, IReadOnlyList<PlannedBuild> plannedBuilds)
    {
        var output = env.Output;
        output.WriteLine("[步骤 3/4] 确认建库信息");
        if (plannedBuilds.Count == 1)
        {
            var plan = plannedBuilds[0];
            var buildDisplay = string.IsNullOrWhiteSpace(plan.Game.SteamBuildId) ? "未知" : plan.Game.SteamBuildId;
            var overwriteNote = plan.ForceOverwrite ? "（将覆盖上次建库结果）" : string.Empty;
            output.WriteLine($"  游戏：{plan.Game.DisplayName}（{plan.Game.Spec.Id}）");
            output.WriteLine($"  Steam 构建号：{buildDisplay}");
            output.WriteLine($"  游戏目录：{plan.Game.GameDirectory}");
            output.WriteLine($"  输出目录：{plan.OutputDirectory}{overwriteNote}");
        }
        else
        {
            output.WriteLine($"  共计划依次构建 {plannedBuilds.Count} 个游戏：");
            for (var i = 0; i < plannedBuilds.Count; i++)
            {
                var plan = plannedBuilds[i];
                var buildDisplay = string.IsNullOrWhiteSpace(plan.Game.SteamBuildId) ? "未知" : plan.Game.SteamBuildId;
                var overwriteNote = plan.ForceOverwrite ? "（覆盖旧构建）" : string.Empty;
                output.WriteLine(
                    $"  {i + 1}. {plan.Game.DisplayName}（{plan.Game.Spec.Id}，构建号：{buildDisplay}）");
                output.WriteLine($"     游戏目录：{plan.Game.GameDirectory}");
                output.WriteLine($"     输出目录：{plan.OutputDirectory}{overwriteNote}");
            }
        }

        while (true)
        {
            output.Write("按回车开始建库（输入 n 取消）：");
            var line = env.Input.ReadLine();
            if (line is null)
            {
                return false;
            }

            var trimmed = line.Trim().ToLowerInvariant();
            if (trimmed is "" or "y" or "yes")
            {
                return true;
            }

            if (trimmed is "n" or "no" or "q" or "quit" or "exit")
            {
                return false;
            }

            output.WriteLine("输入无效：直接按回车开始，或输入 n 取消。");
        }
    }

    private static async Task<GameRunOutcome> ExecuteSingleBuildAsync(WizardEnvironment env, PlannedBuild plan)
    {
        string[] buildArgs = plan.ForceOverwrite
            ? ["--game", plan.Game.GameDirectory, "--out", plan.OutputDirectory, "--force"]
            : ["--game", plan.Game.GameDirectory, "--out", plan.OutputDirectory];

        var sw = Stopwatch.StartNew();
        try
        {
            var exitCode = await env.RunBuildDb(buildArgs);
            sw.Stop();

            var report = TryReadBuildReport(plan.OutputDirectory, plan.Game.Spec.Id);
            if (exitCode >= 2)
            {
                return new GameRunOutcome(
                    plan,
                    exitCode,
                    sw.Elapsed.TotalSeconds,
                    report,
                    $"建库命令返回错误退出码 {exitCode}",
                    SuggestGenericAdvice());
            }

            var partialAdvice = exitCode == 1
                ? "部分资源导出存在告警或失败，可查看输出目录中的 *.sqlite.build.json，或在命令行附加 --keep-json 排查明细。"
                : null;

            return new GameRunOutcome(
                plan,
                exitCode,
                sw.Elapsed.TotalSeconds,
                report,
                null,
                partialAdvice);
        }
        catch (Exception ex)
        {
            sw.Stop();
            env.Error.WriteLine($"错误：[{plan.Game.Spec.Id}] 建库失败：{ex.Message}");
            return new GameRunOutcome(
                plan,
                2,
                sw.Elapsed.TotalSeconds,
                null,
                ex.Message,
                SuggestNextStep(ex));
        }
    }

    private static void PrintFinalSummary(TextWriter output, IReadOnlyList<GameRunOutcome> outcomes)
    {
        output.WriteLine();
        output.WriteLine("============================================================");
        if (outcomes.Count == 1)
        {
            output.WriteLine("构建结果汇总：");
        }
        else
        {
            var succeeded = outcomes.Count(o => o.ErrorMessage is null && o.ExitCode == 0);
            var partial = outcomes.Count(o => o.ErrorMessage is null && o.ExitCode == 1);
            var failed = outcomes.Count - succeeded - partial;
            output.WriteLine(
                $"全部游戏构建汇总（共 {outcomes.Count} 个：成功 {succeeded}，部分告警 {partial}，失败 {failed}）：");
        }

        for (var i = 0; i < outcomes.Count; i++)
        {
            var item = outcomes[i];
            if (outcomes.Count > 1)
            {
                output.WriteLine($"------------------------------------------------------------");
                output.WriteLine(
                    $"[{i + 1}/{outcomes.Count}] {item.Plan.Game.DisplayName}（{item.Plan.Game.Spec.Id}）");
            }
            else
            {
                output.WriteLine($"  游戏：{item.Plan.Game.DisplayName}（{item.Plan.Game.Spec.Id}）");
            }

            if (item.ErrorMessage is not null)
            {
                output.WriteLine($"  状态：失败（耗时 {item.ElapsedSeconds:F2}s）");
                output.WriteLine($"  错误：{item.ErrorMessage}");
                output.WriteLine($"  下一步建议：{item.NextStepAdvice ?? SuggestGenericAdvice()}");
                continue;
            }

            var statusLabel = item.ExitCode == 0 ? "成功" : "完成（存在部分资源告警）";
            output.WriteLine($"  状态：{statusLabel}（总耗时 {item.ElapsedSeconds:F2}s）");

            var sqlitePath = item.Report?.SqlitePath
                ?? Path.Combine(item.Plan.OutputDirectory, $"{item.Plan.Game.Spec.Id}.sqlite");
            output.WriteLine($"  数据库路径：{sqlitePath}");
            output.WriteLine($"  文件哈希 (file_sha256)：{item.Report?.FileSha256 ?? "（未读取到构建报告）"}");
            output.WriteLine($"  内容哈希 (content_sha256)：{item.Report?.ContentSha256 ?? "（未读取到构建报告）"}");
            output.WriteLine($"  known-builds.json 比对：{FormatComparisonSummary(item.Report)}");

            if (!string.IsNullOrWhiteSpace(item.Report?.StageTimingsSummary))
            {
                output.WriteLine($"  分步耗时：{item.Report.StageTimingsSummary}");
            }

            if (!string.IsNullOrWhiteSpace(item.NextStepAdvice))
            {
                output.WriteLine($"  提示：{item.NextStepAdvice}");
            }
        }

        output.WriteLine("============================================================");
    }

    private static string FormatComparisonSummary(ParsedBuildReport? report)
    {
        if (report is null || string.IsNullOrWhiteSpace(report.ComparisonMessage))
        {
            return "（未读取到构建报告中的比对结果）";
        }

        var sourcePart = string.IsNullOrWhiteSpace(report.ComparisonSource)
            ? string.Empty
            : $"，来源：{report.ComparisonSource}";

        if (string.Equals(report.ComparisonStatus, "matched", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(report.MatchedRegisteredAt))
        {
            return $"{report.ComparisonMessage}（登记日期：{report.MatchedRegisteredAt}{sourcePart}）";
        }

        if (string.Equals(report.ComparisonStatus, "hash_mismatch", StringComparison.OrdinalIgnoreCase)
            && report.TableDiffCount > 0)
        {
            return $"{report.ComparisonMessage}（差异表 {report.TableDiffCount} 张{sourcePart}）";
        }

        return string.IsNullOrEmpty(sourcePart)
            ? report.ComparisonMessage
            : $"{report.ComparisonMessage}（来源：{report.ComparisonSource}）";
    }

    private static ParsedBuildReport? TryReadBuildReport(string outputDirectory, string gameId)
    {
        var reportPath = Path.Combine(outputDirectory, $"{gameId}.sqlite.build.json");
        var ioReportPath = PathSupport.ToIoPath(reportPath);
        if (!File.Exists(ioReportPath))
        {
            return null;
        }

        try
        {
            var json = File.ReadAllText(ioReportPath, Encoding.UTF8);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var sqliteFileName = $"{gameId}.sqlite";
            string? fileSha256 = null;
            string? contentSha256 = null;
            if (root.TryGetProperty("database", out var dbEl) && dbEl.ValueKind == JsonValueKind.Object)
            {
                if (dbEl.TryGetProperty("file_name", out var fnEl) && !string.IsNullOrWhiteSpace(fnEl.GetString()))
                {
                    sqliteFileName = fnEl.GetString()!;
                }

                fileSha256 = dbEl.TryGetProperty("file_sha256", out var fshaEl) ? fshaEl.GetString() : null;
                contentSha256 = dbEl.TryGetProperty("content_sha256", out var cshaEl) ? cshaEl.GetString() : null;
            }

            string? comparisonStatus = null;
            string? comparisonMessage = null;
            string? comparisonSource = null;
            string? matchedRegisteredAt = null;
            var tableDiffCount = 0;
            if (root.TryGetProperty("known_build_check", out var checkEl) && checkEl.ValueKind == JsonValueKind.Object)
            {
                comparisonStatus = checkEl.TryGetProperty("status", out var stEl) ? stEl.GetString() : null;
                comparisonMessage = checkEl.TryGetProperty("message", out var msgEl) ? msgEl.GetString() : null;
                comparisonSource = checkEl.TryGetProperty("source", out var srcEl) ? srcEl.GetString() : null;
                matchedRegisteredAt = checkEl.TryGetProperty("matched_registered_at", out var regEl) ? regEl.GetString() : null;
                if (checkEl.TryGetProperty("table_diffs", out var diffsEl) && diffsEl.ValueKind == JsonValueKind.Array)
                {
                    tableDiffCount = diffsEl.GetArrayLength();
                }
            }

            string? timingsSummary = null;
            if (root.TryGetProperty("timings_ms", out var timingsEl) && timingsEl.ValueKind == JsonValueKind.Object)
            {
                var parts = new List<string>();
                TryAppendTiming(parts, timingsEl, "game_version", "版本识别");
                TryAppendTiming(parts, timingsEl, "inventory", "盘点");
                TryAppendTiming(parts, timingsEl, "export", "导出");
                TryAppendTiming(parts, timingsEl, "db_build", "建库");
                TryAppendTiming(parts, timingsEl, "db_hash", "哈希");
                if (parts.Count > 0)
                {
                    timingsSummary = string.Join(" / ", parts);
                }
            }

            return new ParsedBuildReport(
                Path.Combine(outputDirectory, sqliteFileName),
                fileSha256,
                contentSha256,
                comparisonStatus,
                comparisonMessage,
                comparisonSource,
                matchedRegisteredAt,
                tableDiffCount,
                timingsSummary);
        }
        catch
        {
            return null;
        }
    }

    private static void TryAppendTiming(List<string> parts, JsonElement timingsEl, string key, string label)
    {
        if (timingsEl.TryGetProperty(key, out var valEl) && valEl.TryGetInt64(out var ms))
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"{label} {ms / 1000.0:F2}s"));
        }
    }

    private static OutputDirectoryState InspectOutputDirectoryState(string directoryPath)
    {
        var ioPath = PathSupport.ToIoPath(directoryPath);
        if (!Directory.Exists(ioPath) || !Directory.EnumerateFileSystemEntries(ioPath).Any())
        {
            return OutputDirectoryState.EmptyOrMissing;
        }

        var manifestPath = Path.Combine(ioPath, "manifest.json");
        if (!File.Exists(manifestPath))
        {
            return OutputDirectoryState.ForeignNonEmptyDirectory;
        }

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath, Encoding.UTF8));
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return OutputDirectoryState.ForeignNonEmptyDirectory;
            }

            var isOwnTool = doc.RootElement.TryGetProperty("tool", out var toolEl)
                && string.Equals(toolEl.GetString(), OutputGuard.ToolName, StringComparison.Ordinal);
            var isBuildDb = doc.RootElement.TryGetProperty("command", out var cmdEl)
                && string.Equals(cmdEl.GetString(), "build-db", StringComparison.Ordinal);

            return isOwnTool && isBuildDb
                ? OutputDirectoryState.OwnBuildDbDirectory
                : OutputDirectoryState.ForeignNonEmptyDirectory;
        }
        catch
        {
            return OutputDirectoryState.ForeignNonEmptyDirectory;
        }
    }

    private static DiscoveredSteamGame InspectManualGameDirectoryDefault(string fullPath)
    {
        var detected = GameDetector.Detect(fullPath);
        return new DiscoveredSteamGame(
            detected.Spec,
            detected.Spec.Title,
            detected.GameDirectory,
            detected.SteamBuildId,
            detected.AppManifestPath ?? string.Empty);
    }

    private static bool IsCancelToken(string trimmed)
    {
        return string.Equals(trimmed, "q", StringComparison.OrdinalIgnoreCase)
            || string.Equals(trimmed, "quit", StringComparison.OrdinalIgnoreCase)
            || string.Equals(trimmed, "exit", StringComparison.OrdinalIgnoreCase);
    }

    private static string SuggestNextStep(Exception ex)
    {
        if (ex is DirectoryNotFoundException or FileNotFoundException)
        {
            return "请确认游戏目录完整且已通过 Steam 下载完成，可在 Steam 中对该游戏执行“验证游戏文件的完整性”后重试。";
        }

        if (ex is UnauthorizedAccessException)
        {
            return "请检查输出目录是否具有写入权限，建议改选到桌面、文档或其他可写磁盘目录后重试。";
        }

        if (ex is InvalidDataException)
        {
            return "请确认所选目录是未损坏的 FINAL FANTASY I–VI Pixel Remaster 安装根目录（含 *_Data 文件夹），或在 Steam 中验证文件完整性后重试。";
        }

        if (ex is IOException)
        {
            if (ex.Message.Contains("非空", StringComparison.Ordinal)
                || ex.Message.Contains("不是 ffpr-export", StringComparison.Ordinal))
            {
                return "请重新运行向导并选择一个空目录或新的输出目录名称，或手动清理目标目录后重试。";
            }

            return "请确认目标磁盘剩余空间充足，且旧数据库文件未被其他软件占用后重试。";
        }

        return SuggestGenericAdvice();
    }

    private static string SuggestGenericAdvice()
    {
        return "请核对游戏目录与输出目录后重试；如需排查详情，可在命令行运行 ffpr-export build-db --game \"<游戏目录>\" --keep-json 查看完整日志。";
    }

    private static void WaitForExitPrompt(WizardEnvironment env)
    {
        env.Output.WriteLine();
        env.Output.Write("按回车退出……");
        _ = env.Input.ReadLine();
    }
}
