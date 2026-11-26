using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using FfprExport.Gui;
using Xunit;

namespace FfprExport.Tests;

public sealed class GuiCoreTests
{
    [Fact]
    public void GuiGameCatalogShim_MatchesCoreGameDetectorKnownGames()
    {
        var shimPath = Path.Combine(TestHarness.FindRepoRoot(), "src", "FfprExport.Gui", "Shared", "GameCatalogShim.cs");
        Assert.True(File.Exists(shimPath), $"缺少 GUI 游戏清单共享桩：{shimPath}");

        var content = File.ReadAllText(shimPath, Encoding.UTF8);
        var entryRegex = new Regex(
            @"new\(""([^""]+)"",\s*""([^""]+)"",\s*(\d+),\s*""([^""]+)"",\s*""([^""]+)""\)",
            RegexOptions.CultureInvariant);

        var parsed = entryRegex.Matches(content)
            .Select(m => new GameSpec(
                m.Groups[1].Value,
                m.Groups[2].Value,
                int.Parse(m.Groups[3].Value),
                m.Groups[4].Value,
                m.Groups[5].Value))
            .ToArray();

        Assert.Equal(GameDetector.KnownGames, parsed);
    }

    [Fact]
    public void GuiProject_LinksSharedSteamLocatorWithoutCopying()
    {
        var repoRoot = TestHarness.FindRepoRoot();
        var guiCsproj = File.ReadAllText(Path.Combine(repoRoot, "src", "FfprExport.Gui", "FfprExport.Gui.csproj"), Encoding.UTF8);

        Assert.Contains(@"<Compile Include=""..\FfprExport\SteamLocator.cs""", guiCsproj, StringComparison.Ordinal);
        Assert.Contains("<OutputType>WinExe</OutputType>", guiCsproj, StringComparison.Ordinal);
        Assert.Contains("<TargetFramework>net10.0-windows</TargetFramework>", guiCsproj, StringComparison.Ordinal);
        Assert.Contains("<UseWindowsForms>true</UseWindowsForms>", guiCsproj, StringComparison.Ordinal);
        Assert.Contains("<EnableWindowsTargeting>true</EnableWindowsTargeting>", guiCsproj, StringComparison.Ordinal);
        Assert.Contains("<AssemblyName>ffpr-export-gui</AssemblyName>", guiCsproj, StringComparison.Ordinal);
        Assert.Contains("<TreatWarningsAsErrors>true</TreatWarningsAsErrors>", guiCsproj, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(repoRoot, "src", "FfprExport.Gui", "SteamLocator.cs")));
    }

    [Fact]
    public void GuiGameScanner_DiscoversSteamGamesAndValidatesManualDirectory()
    {
        using var ws = new TempWorkspace("ffpr-gui-scanner");
        var steamRoot = Path.Combine(ws.Root, "Steam");
        var ff1Dir = Path.Combine(steamRoot, "steamapps", "common", "FINAL FANTASY PR");
        var ff5Dir = Path.Combine(steamRoot, "steamapps", "common", "FINAL FANTASY V PR");
        Directory.CreateDirectory(Path.Combine(ff1Dir, "FINAL FANTASY_Data"));
        Directory.CreateDirectory(Path.Combine(ff5Dir, "FINAL FANTASY V_Data"));

        WriteAppManifest(steamRoot, 1173770, "FINAL FANTASY", "FINAL FANTASY PR", "20249397");
        WriteAppManifest(steamRoot, 1173810, "FINAL FANTASY V", "FINAL FANTASY V PR", "20265087");

        var discovered = GuiGameScanner.DiscoverGamesInSteamRoots([steamRoot]);
        Assert.Equal(2, discovered.Count);
        Assert.Equal("FF1", discovered[0].GameCode);
        Assert.Equal("20249397", discovered[0].BuildIdDisplay);
        Assert.False(discovered[0].IsManual);
        Assert.Equal("FF5", discovered[1].GameCode);
        Assert.Equal("20265087", discovered[1].BuildIdDisplay);

        // Manual inspection accepts quoted path and reads Steam buildid from ../../appmanifest_*.acf.
        var manualFf5 = GuiGameScanner.InspectManualGameDirectory($"  \"{ff5Dir}\"  ");
        Assert.Equal("ff5", manualFf5.Spec.Id);
        Assert.Equal("20265087", manualFf5.SteamBuildId);
        Assert.True(manualFf5.IsManual);

        // Manual standalone directory without Steam appmanifest still succeeds with unknown buildid.
        var standaloneFf3 = Path.Combine(ws.Root, "Standalone FF3");
        Directory.CreateDirectory(Path.Combine(standaloneFf3, "FINAL FANTASY III_Data"));
        var manualFf3 = GuiGameScanner.InspectManualGameDirectory(standaloneFf3);
        Assert.Equal("ff3", manualFf3.Spec.Id);
        Assert.Null(manualFf3.SteamBuildId);
        Assert.Equal("未知", manualFf3.BuildIdDisplay);

        // Directory with game exe but missing *_Data reports specific missing data directory error.
        var brokenFf4 = Path.Combine(ws.Root, "Broken FF4");
        Directory.CreateDirectory(brokenFf4);
        File.WriteAllBytes(Path.Combine(brokenFf4, "FINAL FANTASY IV.exe"), [0x4D, 0x5A]);
        var missingDataEx = Assert.Throws<DirectoryNotFoundException>(() => GuiGameScanner.InspectManualGameDirectory(brokenFf4));
        Assert.Contains("FINAL FANTASY IV_Data", missingDataEx.Message, StringComparison.Ordinal);

        // Unrelated directory is rejected.
        var randomDir = Path.Combine(ws.Root, "Not A Game");
        Directory.CreateDirectory(randomDir);
        Assert.Throws<InvalidDataException>(() => GuiGameScanner.InspectManualGameDirectory(randomDir));
        Assert.Throws<DirectoryNotFoundException>(() => GuiGameScanner.InspectManualGameDirectory(Path.Combine(ws.Root, "Missing")));
    }

    [Fact]
    public void GuiOutputPlanner_ComputesDefaultPathsDetectsDirectoryStatesAndPlansSafely()
    {
        using var ws = new TempWorkspace("ffpr-gui-output-plan");

        var defaultFromDocs = GuiOutputPlanner.GetDefaultOutputRoot(documentsDirectory: Path.Combine(ws.Root, "Documents"));
        Assert.Equal(Path.Combine(ws.Root, "Documents", "FF-Export"), defaultFromDocs);

        var defaultFromProfile = GuiOutputPlanner.GetDefaultOutputRoot(userProfileDirectory: Path.Combine(ws.Root, "UserHome"));
        Assert.Equal(Path.Combine(ws.Root, "UserHome", "Documents", "FF-Export"), defaultFromProfile);

        Assert.Equal("ff5-20265087", GuiOutputPlanner.BuildGameFolderName("FF5", "20265087"));
        Assert.Equal("ff5-unknown", GuiOutputPlanner.BuildGameFolderName("ff5", null));
        Assert.Equal("ff2-build_1_2", GuiOutputPlanner.BuildGameFolderName("ff2", " build:1/2 "));

        var outRoot = Path.Combine(ws.Root, "FF-Export");
        var ff1Spec = GameDetector.KnownGames.Single(g => g.Id == "ff1");
        var ff5Spec = GameDetector.KnownGames.Single(g => g.Id == "ff5");
        var ff6Spec = GameDetector.KnownGames.Single(g => g.Id == "ff6");

        var ff1Game = new GuiGameEntry(ff1Spec, "FINAL FANTASY", Path.Combine(ws.Root, "games", "ff1"), "20249397", null, false);
        var ff5Game = new GuiGameEntry(ff5Spec, "FINAL FANTASY V", Path.Combine(ws.Root, "games", "ff5"), "20265087", null, false);
        var ff6Game = new GuiGameEntry(ff6Spec, "FINAL FANTASY VI", Path.Combine(ws.Root, "games", "ff6"), null, null, false);

        // ff1 target: missing -> EmptyOrMissing, force=false
        // ff5 target: own build-db manifest -> OwnBuildDbDirectory, force=true
        var ff5Target = Path.Combine(outRoot, "ff5-20265087");
        Directory.CreateDirectory(ff5Target);
        File.WriteAllText(
            Path.Combine(ff5Target, "manifest.json"),
            """{"tool":"ffpr-export","command":"build-db"}""",
            OutputGuard.Utf8WithoutBom);

        // ff6 target: foreign non-empty file -> ForeignNonEmptyDirectory, force=false (requires GUI confirm)
        var ff6Target = Path.Combine(outRoot, "ff6-unknown");
        Directory.CreateDirectory(ff6Target);
        File.WriteAllText(Path.Combine(ff6Target, "personal-notes.txt"), "keep or confirm", OutputGuard.Utf8WithoutBom);

        var plans = GuiOutputPlanner.CreateExportPlan(outRoot, [ff1Game, ff5Game, ff6Game]);
        Assert.Equal(3, plans.Count);

        Assert.Equal(Path.Combine(outRoot, "ff1-20249397"), plans[0].OutputDirectory);
        Assert.Equal(OutputDirectoryState.EmptyOrMissing, plans[0].DirectoryState);
        Assert.False(plans[0].UseForceFlag);

        Assert.Equal(ff5Target, plans[1].OutputDirectory);
        Assert.Equal(OutputDirectoryState.OwnBuildDbDirectory, plans[1].DirectoryState);
        Assert.True(plans[1].UseForceFlag);

        Assert.Equal(ff6Target, plans[2].OutputDirectory);
        Assert.Equal(OutputDirectoryState.ForeignNonEmptyDirectory, plans[2].DirectoryState);
        Assert.False(plans[2].UseForceFlag);

        // Confirmed clearing removes foreign contents and leaves target empty.
        GuiOutputPlanner.ClearConfirmedForeignDirectory(ff6Target, [ff1Game.GameDirectory, ff5Game.GameDirectory, ff6Game.GameDirectory]);
        Assert.Equal(OutputDirectoryState.EmptyOrMissing, GuiOutputPlanner.InspectOutputDirectoryState(ff6Target));

        // Overlapping output directory with game directory is rejected.
        Assert.Throws<ArgumentException>(() =>
            GuiOutputPlanner.CreateExportPlan(ff1Game.GameDirectory, [ff1Game]));

        // Duplicate target mapping is rejected.
        var duplicateFf1 = ff1Game with { GameDirectory = Path.Combine(ws.Root, "games", "ff1-copy") };
        Assert.Throws<ArgumentException>(() =>
            GuiOutputPlanner.CreateExportPlan(outRoot, [ff1Game, duplicateFf1]));

        // Output preview reflects single vs multi selection.
        var singlePreview = GuiOutputPlanner.FormatOutputPreview(outRoot, [ff5Game]);
        Assert.Contains("ff5-20265087", singlePreview, StringComparison.Ordinal);
        var multiPreview = GuiOutputPlanner.FormatOutputPreview(outRoot, [ff1Game, ff5Game, ff6Game]);
        Assert.Contains("3 个子目录", multiPreview, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildReportReader_ParsesBuildJsonAndEvaluatesAllVerdicts()
    {
        using var ws = new TempWorkspace("ffpr-gui-report");
        var ff5Spec = GameDetector.KnownGames.Single(g => g.Id == "ff5");
        var ff5Game = new GuiGameEntry(ff5Spec, "FINAL FANTASY V", Path.Combine(ws.Root, "ff5-game"), "20265087", null, false);

        // 1. Matched
        var matchedDir = Path.Combine(ws.Root, "out-matched");
        WriteSyntheticBuildReport(
            matchedDir,
            "ff5",
            "completed",
            "20265087",
            "1111111111111111111111111111111111111111111111111111111111111111",
            "2222222222222222222222222222222222222222222222222222222222222222",
            "matched",
            "与登记的版本一致",
            "file",
            "2026-11-25",
            fileSha256MismatchOnly: false,
            tableDiffCount: 0);

        Assert.True(BuildReportReader.TryReadReportFromDirectory(matchedDir, "ff5", out var matchedReport, out var matchedErr));
        Assert.Null(matchedErr);
        Assert.NotNull(matchedReport);
        var matchedOutcome = BuildReportReader.EvaluateOutcome(ff5Game, matchedDir, exitCode: 0, cancelled: false, matchedReport);
        Assert.Equal(KnownBuildVerdictKind.Matched, matchedOutcome.Verdict);
        Assert.Equal("✓", matchedOutcome.VerdictSymbol);
        Assert.Equal("与登记的版本一致", matchedOutcome.VerdictShortText);
        Assert.Contains("2026-11-25", matchedOutcome.VerdictDetailText, StringComparison.Ordinal);
        Assert.Equal(Path.Combine(matchedDir, "ff5.sqlite"), matchedOutcome.SqlitePath);

        // 2. New game version (also verify wildcard *.build.json fallback when preferred name differs)
        var newVerDir = Path.Combine(ws.Root, "out-newver");
        WriteSyntheticBuildReport(
            newVerDir,
            "ff5",
            "completed",
            "20999999",
            "3333333333333333333333333333333333333333333333333333333333333333",
            "4444444444444444444444444444444444444444444444444444444444444444",
            "new_game_version",
            "新的游戏版本，未登记",
            "embedded",
            null,
            fileSha256MismatchOnly: false,
            tableDiffCount: 0,
            customReportFileName: "custom.build.json");

        Assert.True(BuildReportReader.TryReadReportFromDirectory(newVerDir, "ff5", out var newVerReport, out _));
        var newVerOutcome = BuildReportReader.EvaluateOutcome(ff5Game, newVerDir, exitCode: 0, cancelled: false, newVerReport);
        Assert.Equal(KnownBuildVerdictKind.NewGameVersion, newVerOutcome.Verdict);
        Assert.Equal("✓", newVerOutcome.VerdictSymbol);
        Assert.Equal("新版本，未登记", newVerOutcome.VerdictShortText);
        Assert.Equal("20999999", newVerOutcome.EffectiveBuildId);

        // 3. Hash mismatch with table diffs and partial warning (exit code 1)
        var mismatchDir = Path.Combine(ws.Root, "out-mismatch");
        WriteSyntheticBuildReport(
            mismatchDir,
            "ff5",
            "completed",
            "20265087",
            "5555555555555555555555555555555555555555555555555555555555555555",
            "6666666666666666666666666666666666666666666666666666666666666666",
            "hash_mismatch",
            "游戏版本已登记，但数据库哈希不同",
            "embedded",
            "2026-11-25",
            fileSha256MismatchOnly: false,
            tableDiffCount: 2);

        Assert.True(BuildReportReader.TryReadReportFromDirectory(mismatchDir, "ff5", out var mismatchReport, out _));
        var mismatchOutcome = BuildReportReader.EvaluateOutcome(ff5Game, mismatchDir, exitCode: 1, cancelled: false, mismatchReport);
        Assert.Equal(KnownBuildVerdictKind.HashMismatch, mismatchOutcome.Verdict);
        Assert.Equal("✗", mismatchOutcome.VerdictSymbol);
        Assert.Contains("哈希不同", mismatchOutcome.VerdictShortText, StringComparison.Ordinal);
        Assert.Contains("部分资源告警", mismatchOutcome.VerdictShortText, StringComparison.Ordinal);
        Assert.Contains("差异表 2 张", mismatchOutcome.VerdictDetailText, StringComparison.Ordinal);

        // 4. Incomplete sentinel report & cancelled & malformed JSON
        var inProgressDir = Path.Combine(ws.Root, "out-inprogress");
        WriteSyntheticBuildReport(
            inProgressDir,
            "ff5",
            "in_progress",
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            fileSha256MismatchOnly: false,
            tableDiffCount: 0);
        Assert.True(BuildReportReader.TryReadReportFromDirectory(inProgressDir, "ff5", out var sentinelReport, out _));
        var failedOutcome = BuildReportReader.EvaluateOutcome(ff5Game, inProgressDir, exitCode: 0, cancelled: false, sentinelReport);
        Assert.Equal(KnownBuildVerdictKind.Failed, failedOutcome.Verdict);
        Assert.Equal("✗", failedOutcome.VerdictSymbol);

        var cancelOutcome = BuildReportReader.EvaluateOutcome(ff5Game, matchedDir, exitCode: -1, cancelled: true, report: null);
        Assert.Equal(KnownBuildVerdictKind.Cancelled, cancelOutcome.Verdict);
        Assert.Equal("✗", cancelOutcome.VerdictSymbol);
        Assert.Equal("已取消", cancelOutcome.VerdictShortText);

        var badJsonDir = Path.Combine(ws.Root, "out-badjson");
        Directory.CreateDirectory(badJsonDir);
        File.WriteAllText(Path.Combine(badJsonDir, "ff5.sqlite.build.json"), "{not-valid-json", OutputGuard.Utf8WithoutBom);
        Assert.False(BuildReportReader.TryReadReportFromDirectory(badJsonDir, "ff5", out var badReport, out var badError));
        Assert.Null(badReport);
        Assert.Contains("ff5.sqlite.build.json", badError, StringComparison.Ordinal);

        // Clipboard formatting includes all reports with hashes.
        var clipboardText = BuildReportReader.FormatClipboardHashText([matchedOutcome, mismatchOutcome]);
        Assert.Contains("1111111111111111111111111111111111111111111111111111111111111111", clipboardText, StringComparison.Ordinal);
        Assert.Contains("6666666666666666666666666666666666666666666666666666666666666666", clipboardText, StringComparison.Ordinal);
        Assert.Contains("✓ 与登记的版本一致", clipboardText, StringComparison.Ordinal);
    }

    [Fact]
    public void CliCommandHelper_BuildsArgsLocatesCliParsesStepsAndConfiguresUtf8()
    {
        using var ws = new TempWorkspace("ffpr-gui-cli-helper");
        var binDir = Path.Combine(ws.Root, "dist-dir");
        Directory.CreateDirectory(binDir);

        // Missing CLI executable gives Chinese error naming ffpr-export.exe.
        var (missingResolved, expectedPath) = CliCommandHelper.FindCliExecutable(binDir);
        Assert.Null(missingResolved);
        Assert.Equal(Path.Combine(binDir, "ffpr-export.exe"), expectedPath);
        var missingMsg = CliCommandHelper.FormatMissingCliMessage(expectedPath);
        Assert.Contains("ffpr-export.exe", missingMsg, StringComparison.Ordinal);
        Assert.Contains(expectedPath, missingMsg, StringComparison.Ordinal);

        // Present CLI executable is discovered.
        File.WriteAllBytes(expectedPath, [0x4D, 0x5A]);
        var (foundResolved, _) = CliCommandHelper.FindCliExecutable(binDir);
        Assert.Equal(expectedPath, foundResolved);

        // Command arguments preserve spaces cleanly and respect --force.
        var argsWithoutForce = CliCommandHelper.BuildBuildDbArguments(
            @"C:\Program Files (x86)\Steam\steamapps\common\FINAL FANTASY V PR",
            @"C:\Users\Alice\Documents\FF-Export\ff5-20265087",
            force: false);
        Assert.Equal(
            [
                "build-db",
                "--game",
                @"C:\Program Files (x86)\Steam\steamapps\common\FINAL FANTASY V PR",
                "--out",
                @"C:\Users\Alice\Documents\FF-Export\ff5-20265087",
            ],
            argsWithoutForce);

        var argsWithForce = CliCommandHelper.BuildBuildDbArguments(
            "/games/ff1",
            "/out/ff1-123",
            force: true,
            steamBuildId: "123",
            keepJson: true,
            jobs: 4);
        Assert.Equal(
            ["build-db", "--game", "/games/ff1", "--out", "/out/ff1-123", "--steam-buildid", "123", "--keep-json", "--jobs", "4", "--force"],
            argsWithForce);

        // ProcessStartInfo enforces UTF-8 stdout/stderr and hidden window.
        var startInfo = CliCommandHelper.CreateStartInfo(expectedPath, argsWithoutForce);
        Assert.False(startInfo.UseShellExecute);
        Assert.True(startInfo.CreateNoWindow);
        Assert.True(startInfo.RedirectStandardOutput);
        Assert.True(startInfo.RedirectStandardError);
        Assert.Equal(Encoding.UTF8, startInfo.StandardOutputEncoding);
        Assert.Equal(Encoding.UTF8, startInfo.StandardErrorEncoding);
        Assert.Equal(argsWithoutForce, startInfo.ArgumentList);

        // Progress parsing supports both `==> [ffN] 步骤 n/m：...` and `==> 步骤 n/m`.
        Assert.True(CliCommandHelper.TryParseProgressStep("==> [ff5] 步骤 2/6：进程内执行 inventory 与 export", out var step2));
        Assert.Equal(2, step2.CurrentStep);
        Assert.Equal(6, step2.TotalSteps);
        Assert.Equal("进程内执行 inventory 与 export", step2.Description);

        Assert.True(CliCommandHelper.TryParseProgressStep("==> 步骤 5/6", out var step5));
        Assert.Equal(5, step5.CurrentStep);
        Assert.Equal(6, step5.TotalSteps);
        Assert.Null(step5.Description);

        Assert.False(CliCommandHelper.TryParseProgressStep("普通日志：步骤 1/6", out _));
        Assert.False(CliCommandHelper.TryParseProgressStep("==> [ff5] 步骤 7/6：越界", out _));

        // Game header & overall permille progress calculation.
        var ff5Spec = GameDetector.KnownGames.Single(g => g.Id == "ff5");
        var ff5Game = new GuiGameEntry(ff5Spec, "FINAL FANTASY V", "/games/ff5", "20265087", null, false);
        Assert.Equal("第 2 / 3 个：FF5", CliCommandHelper.FormatGameProgressHeader(2, 3, ff5Game));
        Assert.Equal(
            "第 2 / 3 个：FF5（步骤 2/6：进程内执行 inventory 与 export）",
            CliCommandHelper.FormatGameProgressHeader(2, 3, ff5Game, step2));

        var p0 = CliCommandHelper.CalculateProgressPermille(0, 2, null);
        var p1 = CliCommandHelper.CalculateProgressPermille(0, 2, new ProgressStepUpdate(1, 6, null));
        var p2 = CliCommandHelper.CalculateProgressPermille(0, 2, new ProgressStepUpdate(6, 6, null));
        var p3 = CliCommandHelper.CalculateProgressPermille(1, 2, null);
        var p4 = CliCommandHelper.CalculateProgressPermille(2, 2, null);
        Assert.Equal(0, p0);
        Assert.True(p0 < p1 && p1 < p2 && p2 < p3 && p3 == 500 && p4 == 1000);
    }

    [Fact]
    public void GuiSettingsStore_RoundTripsOutputRootAndCheckedStates()
    {
        using var ws = new TempWorkspace("ffpr-gui-settings");
        var appData = Path.Combine(ws.Root, "AppData");
        var settingsPath = GuiSettingsStore.GetDefaultSettingsPath(appData);
        Assert.Equal(Path.Combine(appData, "ffpr-export", "settings.json"), settingsPath);

        // Missing file returns defaults where every game starts checked.
        var emptyLoaded = GuiSettingsStore.Load(settingsPath);
        Assert.Null(emptyLoaded.OutputRoot);
        Assert.True(GuiSettingsStore.ResolveInitialCheckedState(emptyLoaded, Path.Combine(ws.Root, "ff1")));

        var ff1Dir = Path.Combine(ws.Root, "games", "FINAL FANTASY PR");
        var ff5Dir = Path.Combine(ws.Root, "games", "FINAL FANTASY V PR");
        var ff6Dir = Path.Combine(ws.Root, "games", "FINAL FANTASY VI PR");

        var saved = new GuiSettings
        {
            OutputRoot = Path.Combine(ws.Root, "Custom Export Root"),
            CheckedGames = new Dictionary<string, bool>(StringComparer.Ordinal)
            {
                [ff1Dir] = false,
                [ff5Dir] = true,
            },
        };
        GuiSettingsStore.Save(settingsPath, saved);
        Assert.True(File.Exists(settingsPath));

        var reloaded = GuiSettingsStore.Load(settingsPath);
        Assert.Equal(saved.OutputRoot, reloaded.OutputRoot);
        Assert.False(GuiSettingsStore.ResolveInitialCheckedState(reloaded, ff1Dir));
        Assert.True(GuiSettingsStore.ResolveInitialCheckedState(reloaded, ff5Dir));
        // Newly discovered game not yet in settings defaults to checked.
        Assert.True(GuiSettingsStore.ResolveInitialCheckedState(reloaded, ff6Dir));

        // Corrupt JSON gracefully falls back to defaults.
        File.WriteAllText(settingsPath, "{broken", OutputGuard.Utf8WithoutBom);
        var recovered = GuiSettingsStore.Load(settingsPath);
        Assert.Null(recovered.OutputRoot);
        Assert.True(GuiSettingsStore.ResolveInitialCheckedState(recovered, ff1Dir));
    }

    [Fact]
    public async Task CliProcessRunner_StreamsUtf8OutputAndCancelsChildProcessTree()
    {
        var cliDll = Path.Combine(AppContext.BaseDirectory, "ffpr-export.dll");
        Assert.True(File.Exists(cliDll), $"缺少 ffpr-export.dll：{cliDll}");

        var stdoutLines = new List<string>();
        var stderrLines = new List<string>();
        var startInfo = CliCommandHelper.CreateStartInfo(
            TestHarness.ResolveDotnetExecutable(),
            ["exec", cliDll, "--help"]);

        var exitCode = await CliProcessRunner.RunAsync(
            startInfo,
            line =>
            {
                lock (stdoutLines)
                {
                    stdoutLines.Add(line);
                }
            },
            line =>
            {
                lock (stderrLines)
                {
                    stderrLines.Add(line);
                }
            },
            CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Contains(stdoutLines, line => line.Contains("资源盘点与导出工具", StringComparison.Ordinal));
        Assert.Empty(stderrLines);

        // Verify cancellation kills a long-running child process promptly.
        var sleepStartInfo = OperatingSystem.IsWindows()
            ? CliCommandHelper.CreateStartInfo("cmd.exe", ["/c", "ping -n 30 127.0.0.1 >nul"])
            : CliCommandHelper.CreateStartInfo("/bin/sh", ["-c", "sleep 30"]);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        var sw = Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CliProcessRunner.RunAsync(sleepStartInfo, _ => { }, _ => { }, cts.Token));
        sw.Stop();
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), $"取消子进程耗时过长：{sw.Elapsed}");
    }

    [Fact]
    public async Task CliRedirectedSubprocess_OutputsValidBomlessUtf8ChineseOnStdoutAndStderr()
    {
        var cliDll = Path.Combine(AppContext.BaseDirectory, "ffpr-export.dll");
        Assert.True(File.Exists(cliDll), $"缺少 ffpr-export.dll：{cliDll}");

        var strictUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

        // 1. Redirected stdout (--help) must emit valid UTF-8 Chinese without BOM.
        var (helpExit, helpOutBytes, helpErrBytes) = await RunCliRawBytesAsync(cliDll, ["--help"]);
        Assert.Equal(0, helpExit);
        Assert.False(HasUtf8Bom(helpOutBytes), "重定向标准输出不应包含 UTF-8 BOM");
        Assert.Empty(helpErrBytes);
        var helpText = strictUtf8.GetString(helpOutBytes);
        Assert.Contains("资源盘点与导出工具", helpText, StringComparison.Ordinal);
        Assert.Contains("打印本帮助信息", helpText, StringComparison.Ordinal);

        // 2. Redirected stderr (invalid subcommand) must emit valid UTF-8 Chinese without BOM.
        var (errExit, errOutBytes, errErrBytes) = await RunCliRawBytesAsync(cliDll, ["__invalid_subcommand__"]);
        Assert.Equal(2, errExit);
        Assert.Empty(errOutBytes);
        Assert.False(HasUtf8Bom(errErrBytes), "重定向标准错误不应包含 UTF-8 BOM");
        var errText = strictUtf8.GetString(errErrBytes);
        Assert.Contains("错误：未知子命令：__invalid_subcommand__", errText, StringComparison.Ordinal);
    }

    [Fact]
    public void GuiStartupLogger_ResolvesPrimaryLogPathWritesStartupLineAndFallsBackToTemp()
    {
        using var ws = new TempWorkspace("ffpr-gui-startup-log");
        var localAppData = Path.Combine(ws.Root, "LocalAppData");
        var tempRoot = Path.Combine(ws.Root, "TempRoot");
        Directory.CreateDirectory(tempRoot);

        var primaryDir = GuiStartupLogger.GetPrimaryLogDirectory(localAppData);
        Assert.Equal(Path.Combine(localAppData, "ffpr-export", "logs"), primaryDir);

        var ts = new DateTimeOffset(2026, 11, 26, 10, 30, 45, TimeSpan.FromHours(9));
        var info = new GuiStartupInfo(
            Timestamp: ts,
            Version: "0.1.0",
            GitCommit: "abc1234def56",
            OsVersion: "Microsoft Windows 10.0.22631",
            DotNetVersion: ".NET 10.0.0",
            ProcessBitness: "64-bit (X64, OS=X64)",
            CurrentDirectory: ws.Root,
            ExecutablePath: Path.Combine(ws.Root, "ffpr-export-gui.exe"),
            DpiSetting: "PerMonitorV2",
            CommandLineArgs: "--self-test");

        var logger = GuiStartupLogger.Start(
            info,
            localAppDataOverride: localAppData,
            tempDirectoryOverride: tempRoot);

        Assert.False(logger.UsedTempFallback);
        Assert.Null(logger.InitializationWarning);
        Assert.Equal(Path.Combine(localAppData, "ffpr-export", "logs", "gui-20261126-103045.log"), logger.LogFilePath);
        Assert.True(File.Exists(logger.LogFilePath));

        var lines = File.ReadAllLines(logger.LogFilePath!, Encoding.UTF8);
        Assert.Single(lines);
        var startLine = lines[0];
        Assert.Contains("[START]", startLine, StringComparison.Ordinal);
        Assert.Contains("version=0.1.0", startLine, StringComparison.Ordinal);
        Assert.Contains("git=abc1234def56", startLine, StringComparison.Ordinal);
        Assert.Contains("os=Microsoft Windows 10.0.22631", startLine, StringComparison.Ordinal);
        Assert.Contains("dotnet=.NET 10.0.0", startLine, StringComparison.Ordinal);
        Assert.Contains("bitness=64-bit (X64, OS=X64)", startLine, StringComparison.Ordinal);
        Assert.Contains($"cwd={ws.Root}", startLine, StringComparison.Ordinal);
        Assert.Contains("ffpr-export-gui.exe", startLine, StringComparison.Ordinal);
        Assert.Contains("dpi=PerMonitorV2", startLine, StringComparison.Ordinal);
        Assert.Contains("args=--self-test", startLine, StringComparison.Ordinal);

        // When primary log directory cannot be created (blocked by a regular file), fall back to %TEMP%.
        var blockedLocalAppData = Path.Combine(ws.Root, "blocked-localappdata");
        File.WriteAllText(blockedLocalAppData, "not a directory", OutputGuard.Utf8WithoutBom);

        var fallbackLogger = GuiStartupLogger.Start(
            info,
            localAppDataOverride: blockedLocalAppData,
            tempDirectoryOverride: tempRoot);
        Assert.True(fallbackLogger.UsedTempFallback);
        Assert.NotNull(fallbackLogger.LogFilePath);
        Assert.StartsWith(tempRoot, fallbackLogger.LogFilePath!, StringComparison.Ordinal);
        Assert.True(File.Exists(fallbackLogger.LogFilePath));
        Assert.NotNull(fallbackLogger.InitializationWarning);

        // When %TEMP%\ffpr-export is also blocked by a regular file, fall back directly to %TEMP%.
        var tempRootDirect = Path.Combine(ws.Root, "TempDirect");
        Directory.CreateDirectory(tempRootDirect);
        File.WriteAllText(Path.Combine(tempRootDirect, "ffpr-export"), "file blocks subdir", OutputGuard.Utf8WithoutBom);

        var directTempLogger = GuiStartupLogger.Start(
            info,
            localAppDataOverride: blockedLocalAppData,
            tempDirectoryOverride: tempRootDirect);
        Assert.True(directTempLogger.UsedTempFallback);
        Assert.Equal(Path.Combine(tempRootDirect, "gui-20261126-103045.log"), directTempLogger.LogFilePath);
        Assert.True(File.Exists(directTempLogger.LogFilePath));

        // When both primary and temp locations fail, logger degrades gracefully without throwing.
        var blockedTemp = Path.Combine(ws.Root, "blocked-temp");
        File.WriteAllText(blockedTemp, "not a directory", OutputGuard.Utf8WithoutBom);
        var degradedLogger = GuiStartupLogger.Start(
            info,
            localAppDataOverride: blockedLocalAppData,
            tempDirectoryOverride: blockedTemp);
        Assert.Null(degradedLogger.LogFilePath);
        Assert.NotNull(degradedLogger.InitializationWarning);
        degradedLogger.WriteInfo("must not throw");
        degradedLogger.WriteException("test", new InvalidOperationException("boom"));
    }

    [Fact]
    public void GuiStartupLogger_RetainsLatestTenLogsHandlesCollisionsAndPreservesUnrelatedFiles()
    {
        using var ws = new TempWorkspace("ffpr-gui-log-retention");
        var localAppData = Path.Combine(ws.Root, "LocalAppData");
        var logsDir = Path.Combine(localAppData, "ffpr-export", "logs");
        Directory.CreateDirectory(logsDir);

        for (var sec = 1; sec <= 12; sec++)
        {
            var name = $"gui-20261126-1000{sec:D2}.log";
            File.WriteAllText(Path.Combine(logsDir, name), $"old log {sec}\n", OutputGuard.Utf8WithoutBom);
        }

        var unrelatedLog = Path.Combine(logsDir, "other-tool.log");
        var unrelatedTxt = Path.Combine(logsDir, "readme.txt");
        File.WriteAllText(unrelatedLog, "keep", OutputGuard.Utf8WithoutBom);
        File.WriteAllText(unrelatedTxt, "keep", OutputGuard.Utf8WithoutBom);

        var ts = new DateTimeOffset(2026, 11, 26, 10, 0, 13, TimeSpan.FromHours(9));
        var info = GuiStartupLogger.CollectStartupInfo(typeof(GuiCoreTests).Assembly, "PerMonitorV2", [], ts);

        var first = GuiStartupLogger.Start(info, localAppDataOverride: localAppData, maxLogFiles: 10);
        var second = GuiStartupLogger.Start(info, localAppDataOverride: localAppData, maxLogFiles: 10);

        Assert.Equal(Path.Combine(logsDir, "gui-20261126-100013.log"), first.LogFilePath);
        Assert.Equal(Path.Combine(logsDir, "gui-20261126-100013-1.log"), second.LogFilePath);

        var remainingGuiLogs = Directory.EnumerateFiles(logsDir, "gui-*.log")
            .Select(path => Path.GetFileName(path)!)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(10, remainingGuiLogs.Length);
        Assert.Equal(
            [
                "gui-20261126-100005.log",
                "gui-20261126-100006.log",
                "gui-20261126-100007.log",
                "gui-20261126-100008.log",
                "gui-20261126-100009.log",
                "gui-20261126-100010.log",
                "gui-20261126-100011.log",
                "gui-20261126-100012.log",
                "gui-20261126-100013-1.log",
                "gui-20261126-100013.log",
            ],
            remainingGuiLogs);

        Assert.True(File.Exists(unrelatedLog));
        Assert.True(File.Exists(unrelatedTxt));

        // Explicitly protected file is never pruned even if its timestamp is older.
        var protectedOld = Path.Combine(logsDir, "gui-20260101-000001.log");
        File.WriteAllText(protectedOld, "protected", OutputGuard.Utf8WithoutBom);
        var deleted = GuiStartupLogger.PruneOldLogs(logsDir, maxFiles: 10, protectedFilePath: protectedOld);
        Assert.Equal(1, deleted);
        Assert.True(File.Exists(protectedOld));
        Assert.Equal(10, Directory.EnumerateFiles(logsDir, "gui-*.log").Count());
    }

    [Fact]
    public void GuiStartupLogger_FormatsCompleteExceptionsFatalDialogAndSelfTestStderr()
    {
        using var ws = new TempWorkspace("ffpr-gui-ex-format");
        var localAppData = Path.Combine(ws.Root, "LocalAppData");

        Exception captured;
        try
        {
            try
            {
                throw new UnauthorizedAccessException("注册表访问被拒绝");
            }
            catch (Exception inner)
            {
                throw new InvalidOperationException("窗口初始化失败", inner);
            }
        }
        catch (Exception ex)
        {
            captured = ex;
        }

        var details = GuiStartupLogger.FormatExceptionDetails(captured, "MainForm/Application.Run");
        Assert.Contains("上下文: MainForm/Application.Run", details, StringComparison.Ordinal);
        Assert.Contains("Type: System.InvalidOperationException", details, StringComparison.Ordinal);
        Assert.Contains("Message: 窗口初始化失败", details, StringComparison.Ordinal);
        Assert.Contains("StackTrace:", details, StringComparison.Ordinal);
        Assert.Contains("InnerException [1]", details, StringComparison.Ordinal);
        Assert.Contains("Type: System.UnauthorizedAccessException", details, StringComparison.Ordinal);
        Assert.Contains("Message: 注册表访问被拒绝", details, StringComparison.Ordinal);

        var agg = new AggregateException(
            "多个错误",
            new IOException("文件锁冲突"),
            new FormatException("字段格式异常"));
        var aggDetails = GuiStartupLogger.FormatExceptionDetails(agg, "AggregateContext");
        Assert.Contains("InnerException [1.1]", aggDetails, StringComparison.Ordinal);
        Assert.Contains("InnerException [1.2]", aggDetails, StringComparison.Ordinal);
        Assert.Contains("文件锁冲突", aggDetails, StringComparison.Ordinal);
        Assert.Contains("字段格式异常", aggDetails, StringComparison.Ordinal);

        var info = GuiStartupLogger.CollectStartupInfo(typeof(GuiCoreTests).Assembly, "PerMonitorV2");
        var logger = GuiStartupLogger.Start(info, localAppDataOverride: localAppData);
        logger.WriteException("UnitTest", captured);
        logger.WriteUnhandledObject("AppDomain", "raw-non-exception-error", isTerminating: true);

        var logContent = File.ReadAllText(logger.LogFilePath!, Encoding.UTF8);
        Assert.Contains("System.InvalidOperationException", logContent, StringComparison.Ordinal);
        Assert.Contains("注册表访问被拒绝", logContent, StringComparison.Ordinal);
        Assert.Contains("raw-non-exception-error", logContent, StringComparison.Ordinal);

        var dialogText = GuiStartupLogger.FormatFatalDialogMessage(captured, logger.LogFilePath, isStartupFailure: true);
        Assert.Contains("窗口程序启动失败", dialogText, StringComparison.Ordinal);
        Assert.Contains("窗口初始化失败", dialogText, StringComparison.Ordinal);
        Assert.Contains("注册表访问被拒绝", dialogText, StringComparison.Ordinal);
        Assert.Contains(logger.LogFilePath!, dialogText, StringComparison.Ordinal);
        Assert.Contains("发送给开发者", dialogText, StringComparison.Ordinal);

        var noLogDialogText = GuiStartupLogger.FormatFatalDialogMessage(
            captured,
            logFilePath: null,
            logInitWarning: "磁盘只读",
            isStartupFailure: true);
        Assert.Contains("窗口程序启动失败", noLogDialogText, StringComparison.Ordinal);
        Assert.Contains("磁盘只读", noLogDialogText, StringComparison.Ordinal);

        var selfTestErr = GuiStartupLogger.FormatSelfTestFailureMessage(captured, logger.LogFilePath);
        Assert.Contains("GUI 自检失败", selfTestErr, StringComparison.Ordinal);
        Assert.Contains($"日志文件：{logger.LogFilePath}", selfTestErr, StringComparison.Ordinal);
        Assert.Contains("System.InvalidOperationException", selfTestErr, StringComparison.Ordinal);
    }

    [Fact]
    public void GuiStartupFallbacks_HandleSettingsFontOutputRootSteamAndCliFailuresWithoutThrowing()
    {
        using var ws = new TempWorkspace("ffpr-gui-fallbacks");

        // 1. Settings path & load diagnostics: provider exception, corrupt JSON, null JSON, unreadable directory path.
        var fallbackSettingsPath = GuiSettingsStore.GetDefaultSettingsPath(
            systemAppDataProvider: () => throw new UnauthorizedAccessException("AppData 不可访问"),
            fallbackBaseDirectory: ws.Root);
        Assert.Equal(Path.Combine(ws.Root, "ffpr-export", "settings.json"), fallbackSettingsPath);

        var missingResult = GuiSettingsStore.LoadWithDiagnostics(Path.Combine(ws.Root, "missing-settings.json"));
        Assert.Null(missingResult.WarningMessage);
        Assert.Null(missingResult.Error);

        var corruptPath = Path.Combine(ws.Root, "corrupt-settings.json");
        File.WriteAllText(corruptPath, "{not-json", OutputGuard.Utf8WithoutBom);
        var corruptResult = GuiSettingsStore.LoadWithDiagnostics(corruptPath);
        Assert.NotNull(corruptResult.WarningMessage);
        Assert.Contains("corrupt-settings.json", corruptResult.WarningMessage, StringComparison.Ordinal);
        Assert.IsType<JsonException>(corruptResult.Error);

        var nullJsonPath = Path.Combine(ws.Root, "null-settings.json");
        File.WriteAllText(nullJsonPath, "null", OutputGuard.Utf8WithoutBom);
        var nullResult = GuiSettingsStore.LoadWithDiagnostics(nullJsonPath);
        Assert.NotNull(nullResult.WarningMessage);

        var dirAsSettingsPath = Path.Combine(ws.Root, "dir-not-file.json");
        Directory.CreateDirectory(dirAsSettingsPath);
        var unreadableResult = GuiSettingsStore.LoadWithDiagnostics(dirAsSettingsPath);
        Assert.NotNull(unreadableResult.Settings);
        Assert.Null(unreadableResult.Settings.OutputRoot);
        Assert.NotNull(unreadableResult.WarningMessage);
        Assert.Contains("目录", unreadableResult.WarningMessage, StringComparison.Ordinal);

        var deniedSettingsResult = GuiSettingsStore.LoadWithDiagnostics(
            Path.Combine(ws.Root, "denied-settings.json"),
            readAllText: _ => throw new UnauthorizedAccessException("拒绝访问设置文件"));
        Assert.NotNull(deniedSettingsResult.WarningMessage);
        Assert.Contains("没有读取权限", deniedSettingsResult.WarningMessage, StringComparison.Ordinal);
        Assert.IsType<UnauthorizedAccessException>(deniedSettingsResult.Error);

        var invalidCharResult = GuiSettingsStore.LoadWithDiagnostics("bad\0path");
        Assert.NotNull(invalidCharResult.WarningMessage);
        Assert.NotNull(invalidCharResult.Error);

        // 2. Font fallback chain: primary -> secondary -> MessageBoxFont -> ultimate fallback.
        var primaryFont = GuiFontFallbackPolicy.ResolveFont(
            "界面字体",
            GuiFontFallbackPolicy.UiFontCandidates,
            family => family == "Microsoft YaHei UI" ? "yahei-ui" : null,
            () => ("msgbox", true, "Segoe UI"),
            () => ("default", false, "DefaultFont"));
        Assert.False(primaryFont.UsedFallback);
        Assert.Null(primaryFont.WarningMessage);
        Assert.True(primaryFont.OwnedByCaller);
        Assert.Equal("yahei-ui", primaryFont.Font);

        var msgBoxFallback = GuiFontFallbackPolicy.ResolveFont(
            "界面字体",
            GuiFontFallbackPolicy.UiFontCandidates,
            _ => throw new ArgumentException("字体未安装"),
            () => ("msgbox-font", true, "Segoe UI"),
            () => ("default", false, "DefaultFont"));
        Assert.True(msgBoxFallback.UsedFallback);
        Assert.True(msgBoxFallback.OwnedByCaller);
        Assert.Equal("msgbox-font", msgBoxFallback.Font);
        Assert.Contains("SystemFonts.MessageBoxFont", msgBoxFallback.WarningMessage, StringComparison.Ordinal);

        var ultimateFallback = GuiFontFallbackPolicy.ResolveFont(
            "日志字体",
            GuiFontFallbackPolicy.LogFontCandidates,
            _ => null,
            () => throw new InvalidOperationException("GDI+ 异常"),
            () => ("control-default", false, "Control.DefaultFont"));
        Assert.True(ultimateFallback.UsedFallback);
        Assert.False(ultimateFallback.OwnedByCaller);
        Assert.Equal("control-default", ultimateFallback.Font);
        Assert.NotNull(ultimateFallback.WarningMessage);

        // 3. Default output root fallbacks when Documents is redirected/missing or throws.
        var existingDocs = Path.Combine(ws.Root, "RealDocs");
        var existingProfile = Path.Combine(ws.Root, "RealProfile");
        var existingBase = Path.Combine(ws.Root, "AppBase");
        var existingTemp = Path.Combine(ws.Root, "TempDir");
        Directory.CreateDirectory(existingDocs);
        Directory.CreateDirectory(existingProfile);
        Directory.CreateDirectory(existingBase);
        Directory.CreateDirectory(existingTemp);

        var normalRoot = GuiOutputPlanner.ResolveDefaultOutputRoot(
            getDocumentsDirectory: () => existingDocs,
            getUserProfileDirectory: () => existingProfile,
            getBaseDirectory: () => existingBase,
            getTempDirectory: () => existingTemp);
        Assert.False(normalRoot.UsedFallback);
        Assert.Null(normalRoot.WarningMessage);
        Assert.Equal(Path.Combine(existingDocs, "FF-Export"), normalRoot.OutputRoot);

        var missingDocsRoot = GuiOutputPlanner.ResolveDefaultOutputRoot(
            getDocumentsDirectory: () => Path.Combine(ws.Root, "RedirectedMissingDocs"),
            getUserProfileDirectory: () => existingProfile,
            getBaseDirectory: () => existingBase,
            getTempDirectory: () => existingTemp);
        Assert.True(missingDocsRoot.UsedFallback);
        Assert.Equal(Path.Combine(existingProfile, "FF-Export"), missingDocsRoot.OutputRoot);
        Assert.Contains("文档", missingDocsRoot.WarningMessage, StringComparison.Ordinal);

        var throwingDocsAndMissingProfile = GuiOutputPlanner.ResolveDefaultOutputRoot(
            getDocumentsDirectory: () => throw new UnauthorizedAccessException("无法读取文档目录"),
            getUserProfileDirectory: () => Path.Combine(ws.Root, "MissingProfile"),
            getBaseDirectory: () => existingBase,
            getTempDirectory: () => existingTemp);
        Assert.True(throwingDocsAndMissingProfile.UsedFallback);
        Assert.Equal(Path.Combine(existingBase, "FF-Export"), throwingDocsAndMissingProfile.OutputRoot);
        Assert.Contains("无法读取文档目录", throwingDocsAndMissingProfile.WarningMessage, StringComparison.Ordinal);

        var tempOnlyRoot = GuiOutputPlanner.ResolveDefaultOutputRoot(
            getDocumentsDirectory: () => string.Empty,
            getUserProfileDirectory: () => string.Empty,
            getBaseDirectory: () => Path.Combine(ws.Root, "MissingBase"),
            getTempDirectory: () => existingTemp);
        Assert.True(tempOnlyRoot.UsedFallback);
        Assert.Equal(Path.Combine(existingTemp, "FF-Export"), tempOnlyRoot.OutputRoot);

        // 4. Steam scan safe wrapper catches permission & IO exceptions and prompts manual selection.
        var deniedScan = GuiGameScanner.DiscoverSteamGamesSafe(
            () => throw new UnauthorizedAccessException("无权访问 Steam 目录"));
        Assert.Empty(deniedScan.Games);
        Assert.IsType<UnauthorizedAccessException>(deniedScan.Error);
        Assert.Contains("手动选择游戏目录", deniedScan.WarningMessage, StringComparison.Ordinal);

        var ioScan = GuiGameScanner.DiscoverSteamGamesSafe(
            () => throw new IOException("磁盘 I/O 错误"));
        Assert.Empty(ioScan.Games);
        Assert.IsType<IOException>(ioScan.Error);
        Assert.Contains("扫描 Steam 游戏库失败", ioScan.WarningMessage, StringComparison.Ordinal);

        // 5. CLI executable safe lookup never throws on permission errors.
        var deniedCli = CliCommandHelper.FindCliExecutableSafe(
            ws.Root,
            fileExists: _ => throw new UnauthorizedAccessException("禁止访问程序目录"));
        Assert.Null(deniedCli.ResolvedPath);
        Assert.IsType<UnauthorizedAccessException>(deniedCli.Error);
        Assert.Contains("禁止访问程序目录", deniedCli.WarningMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void GuiProgramAndPackagingScript_EnforceDiagnosticsSelfTestAndSafePowerShellQuotes()
    {
        var repoRoot = TestHarness.FindRepoRoot();
        var programCs = File.ReadAllText(Path.Combine(repoRoot, "src", "FfprExport.Gui", "Program.cs"), Encoding.UTF8);

        Assert.Contains("Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException)", programCs, StringComparison.Ordinal);
        Assert.Contains("Application.ThreadException +=", programCs, StringComparison.Ordinal);
        Assert.Contains("AppDomain.CurrentDomain.UnhandledException +=", programCs, StringComparison.Ordinal);
        Assert.Contains("form.CreateControl();", programCs, StringComparison.Ordinal);
        Assert.Contains("GuiStartupLogger.Start(", programCs, StringComparison.Ordinal);

        var ps1Path = Path.Combine(repoRoot, "scripts", "package-windows.ps1");
        var ps1Content = File.ReadAllText(ps1Path, Encoding.UTF8);
        Assert.DoesNotContain('“', ps1Content);
        Assert.DoesNotContain('”', ps1Content);
        Assert.Contains("Start-Process", ps1Content, StringComparison.Ordinal);
        Assert.Contains("--self-test", ps1Content, StringComparison.Ordinal);
        Assert.Contains("-Wait", ps1Content, StringComparison.Ordinal);
        Assert.Contains("-PassThru", ps1Content, StringComparison.Ordinal);
    }

    private static bool HasUtf8Bom(byte[] bytes)
    {
        return bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
    }

    private static async Task<(int ExitCode, byte[] Stdout, byte[] Stderr)> RunCliRawBytesAsync(
        string cliDll,
        IReadOnlyList<string> args)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = TestHarness.ResolveDotnetExecutable(),
            WorkingDirectory = TestHarness.FindRepoRoot(),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add("exec");
        startInfo.ArgumentList.Add(cliDll);
        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("无法启动 ffpr-export 测试子进程");

        using var outMs = new MemoryStream();
        using var errMs = new MemoryStream();
        var outCopy = process.StandardOutput.BaseStream.CopyToAsync(outMs);
        var errCopy = process.StandardError.BaseStream.CopyToAsync(errMs);
        await Task.WhenAll(outCopy, errCopy);
        await process.WaitForExitAsync();

        return (process.ExitCode, outMs.ToArray(), errMs.ToArray());
    }

    private static void WriteAppManifest(
        string libraryRoot,
        int appId,
        string name,
        string installDir,
        string buildId)
    {
        var steamAppsDir = Path.Combine(libraryRoot, "steamapps");
        Directory.CreateDirectory(steamAppsDir);
        var content = $$"""
            "AppState"
            {
            	"appid"		"{{appId}}"
            	"name"		"{{name}}"
            	"installdir"		"{{installDir}}"
            	"buildid"		"{{buildId}}"
            }
            """;
        File.WriteAllText(
            Path.Combine(steamAppsDir, $"appmanifest_{appId}.acf"),
            content,
            OutputGuard.Utf8WithoutBom);
    }

    private static void WriteSyntheticBuildReport(
        string outDir,
        string gameId,
        string status,
        string? steamBuildId,
        string? fileSha256,
        string? contentSha256,
        string? checkStatus,
        string? checkMessage,
        string? checkSource,
        string? registeredAt,
        bool fileSha256MismatchOnly,
        int tableDiffCount,
        string? customReportFileName = null)
    {
        Directory.CreateDirectory(outDir);
        var sqliteName = $"{gameId}.sqlite";
        var diffs = Enumerable.Range(0, tableDiffCount)
            .Select(i => new { name = $"table_{i}", kind = "changed" })
            .ToArray();

        var report = new
        {
            format = 1,
            tool = OutputGuard.ToolName,
            command = "build-db",
            status,
            game_id = gameId,
            game_version = new
            {
                steam = new
                {
                    build_id = steamBuildId,
                },
            },
            database = new
            {
                file_name = sqliteName,
                size = 1024L,
                file_sha256 = fileSha256,
                content_sha256 = contentSha256,
            },
            known_build_check = new
            {
                status = checkStatus,
                message = checkMessage,
                source = checkSource,
                matched_registered_at = registeredAt,
                file_sha256_mismatch_only = fileSha256MismatchOnly,
                table_diffs = diffs,
            },
            timings_ms = new
            {
                total = 1234L,
            },
        };

        var fileName = customReportFileName ?? $"{sqliteName}.build.json";
        File.WriteAllText(
            Path.Combine(outDir, fileName),
            JsonSerializer.Serialize(report, FieldJsonWriter.SerializerOptions),
            OutputGuard.Utf8WithoutBom);
    }
}
