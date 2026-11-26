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
/// Interactive console wizard launched when ffpr-export is started with no arguments
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
            env.Error.WriteLine(L.Format("Wizard_UnexpectedError", ex.Message));
            env.Output.WriteLine();
            env.Output.WriteLine(L.Format("Common_ErrorPrefix", ex.Message));
            env.Output.WriteLine(L.Format("Wizard_NextStep", SuggestNextStep(ex)));
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
        output.WriteLine(L.Format("Wizard_Title", OutputGuard.ToolName, Program.ToolVersion));
        output.WriteLine("============================================================");
        output.WriteLine(L.Get("Wizard_CliHint"));
        output.WriteLine();

        // Step 1: Discover Steam games or prompt user for a manual game directory.
        output.WriteLine(L.Get("Wizard_Step1Header"));
        IReadOnlyList<DiscoveredSteamGame> discovered;
        try
        {
            discovered = env.DiscoverGames();
        }
        catch (Exception ex)
        {
            output.WriteLine(L.Format("Wizard_ScanError", ex.Message));
            discovered = [];
        }

        var selectedGames = SelectGames(env, discovered);
        if (selectedGames is null || selectedGames.Count == 0)
        {
            output.WriteLine(L.Get("Wizard_Cancelled"));
            WaitForExitPrompt(env);
            return 0;
        }

        output.WriteLine();

        // Step 2: Select output directory (default: <agpHome>/ffpr-export/output/<gameId>-<steamBuildId>/).
        var plannedBuilds = SelectOutputDirectories(env, selectedGames);
        if (plannedBuilds is null || plannedBuilds.Count == 0)
        {
            output.WriteLine(L.Get("Wizard_Cancelled"));
            WaitForExitPrompt(env);
            return 0;
        }

        output.WriteLine();

        // Step 3: Confirm before starting.
        if (!ConfirmStart(env, plannedBuilds))
        {
            output.WriteLine(L.Get("Wizard_Cancelled"));
            WaitForExitPrompt(env);
            return 0;
        }

        output.WriteLine();

        // Step 4: Run build-db for each selected game using the shared BuildDbCommand entry point.
        output.WriteLine(L.Get("Wizard_Step4Header"));
        var outcomes = new List<GameRunOutcome>(plannedBuilds.Count);
        for (var i = 0; i < plannedBuilds.Count; i++)
        {
            var plan = plannedBuilds[i];
            if (plannedBuilds.Count > 1)
            {
                output.WriteLine();
                output.WriteLine("------------------------------------------------------------");
                output.WriteLine(L.Format("Wizard_BatchItemHeader", i + 1, plannedBuilds.Count, plan.Game.DisplayName, plan.Game.Spec.Id));
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
            output.WriteLine(L.Get("Wizard_NoGamesFound"));
            var manualGame = PromptManualGameDirectory(env);
            return manualGame is null ? null : [manualGame];
        }

        output.WriteLine(L.Format("Wizard_FoundGames", discovered.Count));
        for (var i = 0; i < discovered.Count; i++)
        {
            var game = discovered[i];
            var buildDisplay = string.IsNullOrWhiteSpace(game.SteamBuildId) ? L.Get("Common_Unknown") : game.SteamBuildId;
            output.WriteLine(L.Format("Wizard_GameListItem", i + 1, game.DisplayName, game.Spec.Id, buildDisplay));
            output.WriteLine(L.Format("Wizard_GameListDir", game.GameDirectory));
        }

        var allOptionNumber = discovered.Count + 1;
        output.WriteLine(L.Format("Wizard_AllOption", allOptionNumber, discovered.Count));
        output.WriteLine(L.Get("Wizard_ManualOption"));
        output.WriteLine();

        while (true)
        {
            var singleChoiceHint = discovered.Count == 1
                ? L.Get("Wizard_SingleChoiceHint")
                : L.Format("Wizard_RangeChoiceHint", discovered.Count);
            output.Write(L.Format("Wizard_SelectGamePrompt", singleChoiceHint, allOptionNumber));
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
                || string.Equals(trimmed, "全部", StringComparison.Ordinal)
                || string.Equals(trimmed, "すべて", StringComparison.Ordinal))
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

            output.WriteLine(L.Format("Wizard_InvalidGameChoice", allOptionNumber));
        }
    }

    private static DiscoveredSteamGame? PromptManualGameDirectory(WizardEnvironment env)
    {
        var output = env.Output;
        while (true)
        {
            output.WriteLine(L.Get("Wizard_ManualPathPrompt"));
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
                output.WriteLine(L.Format("Wizard_InvalidPathFormat", ex.Message));
                continue;
            }

            if (!Directory.Exists(PathSupport.ToIoPath(fullPath)))
            {
                output.WriteLine(L.Format("Wizard_DirNotFound", fullPath));
                continue;
            }

            try
            {
                var inspected = env.InspectManualGameDirectory(fullPath);
                var buildDisplay = string.IsNullOrWhiteSpace(inspected.SteamBuildId) ? L.Get("Common_Unknown") : inspected.SteamBuildId;
                output.WriteLine(L.Format("Wizard_GameIdentified", inspected.DisplayName, inspected.Spec.Id, buildDisplay));
                return inspected;
            }
            catch (Exception ex)
            {
                output.WriteLine(L.Format("Common_ErrorPrefix", ex.Message));
                output.WriteLine(L.Get("Wizard_ConfirmValidInstallDir"));
            }
        }
    }

    private static List<PlannedBuild>? SelectOutputDirectories(
        WizardEnvironment env,
        IReadOnlyList<DiscoveredSteamGame> selectedGames)
    {
        var output = env.Output;
        string defaultOutputRoot;
        if (env.ResolveDefaultOutputRoot is not null)
        {
            defaultOutputRoot = PathSupport.TrimTrailingSeparators(Path.GetFullPath(env.ResolveDefaultOutputRoot()));
        }
        else
        {
            var agpRoot = AgpPaths.RequireWritableAgpHome(
                isWindowsOverride: null,
                getBaseDirectory: () => env.BaseDirectory);
            var rootNotice = AgpPaths.FormatCliNotice(agpRoot);
            if (!string.IsNullOrWhiteSpace(rootNotice))
            {
                output.WriteLine(rootNotice);
            }

            defaultOutputRoot = AgpPaths.GetOutputRoot(agpRoot);
        }

        if (selectedGames.Count == 1)
        {
            var game = selectedGames[0];
            var defaultDir = Path.Combine(
                defaultOutputRoot,
                BuildDefaultGameFolderName(game.Spec.Id, game.SteamBuildId));

            output.WriteLine(L.Get("Wizard_Step2SingleHeader"));
            output.WriteLine(L.Format("Wizard_DefaultOutputDir", defaultDir));

            while (true)
            {
                output.Write(L.Get("Wizard_SingleOutputPrompt"));
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
                    output.WriteLine(L.Format("Wizard_InvalidOutputDir", ex.Message));
                    continue;
                }

                var state = InspectOutputDirectoryState(chosenDir);
                if (state == OutputDirectoryState.ForeignNonEmptyDirectory)
                {
                    output.WriteLine(L.Format("Wizard_ForeignOutputDir", OutputGuard.ToolName, chosenDir));
                    output.WriteLine(L.Get("Wizard_ChooseEmptyDir"));
                    continue;
                }

                var force = state == OutputDirectoryState.OwnBuildDbDirectory;
                if (force)
                {
                    output.WriteLine(L.Get("Wizard_OwnDirWillOverwrite"));
                }

                return [new PlannedBuild(game, chosenDir, force)];
            }
        }

        output.WriteLine(L.Get("Wizard_Step2BatchHeader"));
        output.WriteLine(L.Format("Wizard_DefaultOutputRoot", defaultOutputRoot));
        output.WriteLine(L.Get("Wizard_BatchSubdirsList"));
        foreach (var game in selectedGames)
        {
            var subDir = Path.Combine(
                defaultOutputRoot,
                BuildDefaultGameFolderName(game.Spec.Id, game.SteamBuildId));
            output.WriteLine(L.Format("Wizard_BatchSubdirItem", game.DisplayName, game.Spec.Id, subDir));
        }

        while (true)
        {
            output.Write(L.Get("Wizard_BatchOutputPrompt"));
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
                output.WriteLine(L.Format("Wizard_InvalidOutputRoot", ex.Message));
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
                    output.WriteLine(L.Format("Wizard_BatchGameOutputInvalid", game.Spec.Id, ex.Message));
                    valid = false;
                    break;
                }

                var state = InspectOutputDirectoryState(targetDir);
                if (state == OutputDirectoryState.ForeignNonEmptyDirectory)
                {
                    output.WriteLine(L.Format("Wizard_BatchForeignSubdir", OutputGuard.ToolName, targetDir));
                    output.WriteLine(L.Get("Wizard_ChooseEmptyRoot"));
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
                output.WriteLine(L.Get("Wizard_BatchOverwriteHint"));
            }

            return plans;
        }
    }

    private static bool ConfirmStart(WizardEnvironment env, IReadOnlyList<PlannedBuild> plannedBuilds)
    {
        var output = env.Output;
        output.WriteLine(L.Get("Wizard_Step3Header"));
        if (plannedBuilds.Count == 1)
        {
            var plan = plannedBuilds[0];
            var buildDisplay = string.IsNullOrWhiteSpace(plan.Game.SteamBuildId) ? L.Get("Common_Unknown") : plan.Game.SteamBuildId;
            var overwriteNote = plan.ForceOverwrite ? L.Get("Wizard_OverwriteNoteSingle") : string.Empty;
            output.WriteLine(L.Format("Wizard_SummaryGame", plan.Game.DisplayName, plan.Game.Spec.Id));
            output.WriteLine(L.Format("Wizard_SummaryBuildId", buildDisplay));
            output.WriteLine(L.Format("Wizard_SummaryGameDir", plan.Game.GameDirectory));
            output.WriteLine(L.Format("Wizard_SummaryOutputDir", plan.OutputDirectory, overwriteNote));
        }
        else
        {
            output.WriteLine(L.Format("Wizard_ConfirmBatchHeader", plannedBuilds.Count));
            for (var i = 0; i < plannedBuilds.Count; i++)
            {
                var plan = plannedBuilds[i];
                var buildDisplay = string.IsNullOrWhiteSpace(plan.Game.SteamBuildId) ? L.Get("Common_Unknown") : plan.Game.SteamBuildId;
                var overwriteNote = plan.ForceOverwrite ? L.Get("Wizard_OverwriteNoteBatch") : string.Empty;
                output.WriteLine(L.Format("Wizard_ConfirmBatchItem", i + 1, plan.Game.DisplayName, plan.Game.Spec.Id, buildDisplay));
                output.WriteLine(L.Format("Wizard_ConfirmBatchGameDir", plan.Game.GameDirectory));
                output.WriteLine(L.Format("Wizard_ConfirmBatchOutputDir", plan.OutputDirectory, overwriteNote));
            }
        }

        while (true)
        {
            output.Write(L.Get("Wizard_ConfirmPrompt"));
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

            output.WriteLine(L.Get("Wizard_InvalidConfirmInput"));
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
                    L.Format("Wizard_BuildDbExitCode", exitCode),
                    SuggestGenericAdvice());
            }

            var partialAdvice = exitCode == 1
                ? L.Get("Wizard_PartialWarningAdvice")
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
            env.Error.WriteLine(L.Format("Wizard_BuildFailedLog", plan.Game.Spec.Id, ex.Message));
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
            output.WriteLine(L.Get("Wizard_FinalSummarySingle"));
        }
        else
        {
            var succeeded = outcomes.Count(o => o.ErrorMessage is null && o.ExitCode == 0);
            var partial = outcomes.Count(o => o.ErrorMessage is null && o.ExitCode == 1);
            var failed = outcomes.Count - succeeded - partial;
            output.WriteLine(L.Format("Wizard_FinalSummaryBatch", outcomes.Count, succeeded, partial, failed));
        }

        for (var i = 0; i < outcomes.Count; i++)
        {
            var item = outcomes[i];
            if (outcomes.Count > 1)
            {
                output.WriteLine("------------------------------------------------------------");
                output.WriteLine($"[{i + 1}/{outcomes.Count}] {item.Plan.Game.DisplayName} ({item.Plan.Game.Spec.Id})");
            }
            else
            {
                output.WriteLine(L.Format("Wizard_SummaryGame", item.Plan.Game.DisplayName, item.Plan.Game.Spec.Id));
            }

            if (item.ErrorMessage is not null)
            {
                output.WriteLine(L.Format("Wizard_StatusFailed", item.ElapsedSeconds));
                output.WriteLine(L.Format("Wizard_ResultError", item.ErrorMessage));
                output.WriteLine(L.Format("Wizard_ResultNextStep", item.NextStepAdvice ?? SuggestGenericAdvice()));
                continue;
            }

            var statusLabel = item.ExitCode == 0 ? L.Get("Wizard_StatusSuccess") : L.Get("Wizard_StatusPartial");
            output.WriteLine(L.Format("Wizard_ResultStatus", statusLabel, item.ElapsedSeconds));

            var sqlitePath = item.Report?.SqlitePath
                ?? Path.Combine(item.Plan.OutputDirectory, $"{item.Plan.Game.Spec.Id}.sqlite");
            output.WriteLine(L.Format("Wizard_ResultDbPath", sqlitePath));
            output.WriteLine(L.Format("Wizard_ResultFileHash", item.Report?.FileSha256 ?? L.Get("Wizard_ReportNotRead")));
            output.WriteLine(L.Format("Wizard_ResultContentHash", item.Report?.ContentSha256 ?? L.Get("Wizard_ReportNotRead")));
            output.WriteLine(L.Format("Wizard_ResultKnownBuilds", FormatComparisonSummary(item.Report)));

            if (!string.IsNullOrWhiteSpace(item.Report?.StageTimingsSummary))
            {
                output.WriteLine(L.Format("Wizard_ResultStageTimings", item.Report.StageTimingsSummary));
            }

            if (!string.IsNullOrWhiteSpace(item.NextStepAdvice))
            {
                output.WriteLine(L.Format("Wizard_ResultHint", item.NextStepAdvice));
            }
        }

        output.WriteLine("============================================================");
    }

    private static string FormatComparisonSummary(ParsedBuildReport? report)
    {
        if (report is null || (string.IsNullOrWhiteSpace(report.ComparisonMessage) && string.IsNullOrWhiteSpace(report.ComparisonStatus)))
        {
            return L.Get("Wizard_ComparisonNotRead");
        }

        var message = (report.ComparisonStatus ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "matched" => KnownBuildsRegistry.MessageMatched,
            "new_game_version" => KnownBuildsRegistry.MessageNewGameVersion,
            "hash_mismatch" => KnownBuildsRegistry.MessageHashMismatch,
            _ => report.ComparisonMessage ?? L.Get("Wizard_ComparisonNotRead"),
        };

        var sourcePart = string.IsNullOrWhiteSpace(report.ComparisonSource)
            ? string.Empty
            : L.Format("Wizard_SourcePart", report.ComparisonSource);

        if (string.Equals(report.ComparisonStatus, "matched", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(report.MatchedRegisteredAt))
        {
            return L.Format("Wizard_ComparisonMatchedDetail", message, report.MatchedRegisteredAt, sourcePart);
        }

        if (string.Equals(report.ComparisonStatus, "hash_mismatch", StringComparison.OrdinalIgnoreCase)
            && report.TableDiffCount > 0)
        {
            return L.Format("Wizard_ComparisonMismatchDetail", message, report.TableDiffCount, sourcePart);
        }

        return string.IsNullOrEmpty(sourcePart)
            ? message
            : L.Format("Wizard_ComparisonWithSource", message, report.ComparisonSource);
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
                TryAppendTiming(parts, timingsEl, "game_version", L.Get("Wizard_TimingGameVersion"));
                TryAppendTiming(parts, timingsEl, "inventory", L.Get("Wizard_TimingInventory"));
                TryAppendTiming(parts, timingsEl, "export", L.Get("Wizard_TimingExport"));
                TryAppendTiming(parts, timingsEl, "db_build", L.Get("Wizard_TimingDbBuild"));
                TryAppendTiming(parts, timingsEl, "db_hash", L.Get("Wizard_TimingDbHash"));
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
            return L.Get("Wizard_AdviceMissingFiles");
        }

        if (ex is UnauthorizedAccessException)
        {
            return L.Get("Wizard_AdvicePermission");
        }

        if (ex is InvalidDataException)
        {
            return L.Get("Wizard_AdviceInvalidData");
        }

        if (ex is IOException)
        {
            if (ex.Message.Contains("--force", StringComparison.Ordinal)
                || ex.Message.Contains(OutputGuard.ToolName, StringComparison.Ordinal))
            {
                return L.Get("Wizard_AdviceOutputOccupied");
            }

            return L.Get("Wizard_AdviceDiskSpace");
        }

        return SuggestGenericAdvice();
    }

    private static string SuggestGenericAdvice()
    {
        return L.Get("Wizard_AdviceGeneric");
    }

    private static void WaitForExitPrompt(WizardEnvironment env)
    {
        env.Output.WriteLine();
        env.Output.Write(L.Get("Wizard_PressEnterToExit"));
        _ = env.Input.ReadLine();
    }
}
