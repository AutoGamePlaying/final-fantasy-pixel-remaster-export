using System.Text;
using System.Text.Json;
using Xunit;

namespace FfprExport.Tests;

public sealed class SteamLocatorAndWizardTests
{
    private sealed class FakeBuildDbRunner : IBuildDbDatabaseRunner
    {
        public string ContentSha256 { get; set; } = "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789";

        public Task<int> BuildAsync(string exportDirectory, string sqlitePath, int jobs)
        {
            var ioSqlitePath = PathSupport.ToIoPath(sqlitePath);
            var parent = Path.GetDirectoryName(ioSqlitePath);
            if (!string.IsNullOrEmpty(parent))
            {
                Directory.CreateDirectory(parent);
            }

            File.WriteAllBytes(ioSqlitePath, Encoding.ASCII.GetBytes("SQLite format 3\0wizard-synthetic-db"));
            return Task.FromResult(0);
        }

        public Task<FfprDbHashReport> HashAsync(string sqlitePath)
        {
            var fileSha = GameVersionDetector.ComputeFileSha256Lower(sqlitePath);
            var json = JsonSerializer.Serialize(new
            {
                format = 1,
                file_sha256 = fileSha,
                content_sha256 = ContentSha256,
                schema_version = 1,
                sqlite_version = "3.49.1",
                tables = new[]
                {
                    new { name = "item", rows = 12L, sha256 = "1111111111111111111111111111111111111111111111111111111111111111" },
                },
            });

            return Task.FromResult(InProcessDbCommandRunner.ParseDbHashReport(json, sqlitePath));
        }
    }

    [Fact]
    public void ParseLibraryFoldersVdf_HandlesMultipleLibrariesEscapedBackslashesAndLegacyFormat()
    {
        var modernVdf = """
            // Steam library folders
            "libraryfolders"
            {
            	"0"
            	{
            		"path"		"C:\\Program Files (x86)\\Steam"
            		"label"		""
            		"contentid"		"1234567890"
            		"apps"
            		{
            			"1173770"		"423971307"
            		}
            	}
            	"1"
            	{
            		"path"		"D:\\Games\\Steam Library\\"
            		"label"		"SSD"
            		"apps"
            		{
            			"1173810"		"512000000"
            		}
            	}
            	"2"
            	{
            		"path"		"E:\\Steam\\Sub Folder\\Library"
            		"apps"
            		{
            		}
            	}
            	"3"
            	{
            		"path"		"D:\\Games\\Steam Library"
            	}
            }
            """;

        var modernPaths = SteamLocator.ParseLibraryFoldersVdf(modernVdf);
        Assert.Equal(
            [
                @"C:\Program Files (x86)\Steam",
                @"D:\Games\Steam Library",
                @"E:\Steam\Sub Folder\Library",
            ],
            modernPaths);

        var legacyVdf = """
            "LibraryFolders"
            {
            	"TimeNextStatsReport"		"1600000000"
            	"ContentStatsID"		"-1234567890"
            	"1"		"D:\\SteamLibrary"
            	"2"		"F:\\My Games\\Steam"
            }
            """;

        var legacyPaths = SteamLocator.ParseLibraryFoldersVdf(legacyVdf);
        Assert.Equal(
            [
                @"D:\SteamLibrary",
                @"F:\My Games\Steam",
            ],
            legacyPaths);
    }

    [Fact]
    public void ResolveSteamRoots_CoversWindowsRegistryFallbackAndLinuxCandidates()
    {
        // Windows: registry hit (with forward slashes as Steam writes in HKCU) takes precedence.
        var regRoots = SteamLocator.ResolveWindowsSteamRoots(
            () => "D:/Games/Steam",
            path => string.Equals(path, @"D:\Games\Steam", StringComparison.OrdinalIgnoreCase));
        Assert.Equal([@"D:\Games\Steam"], regRoots);

        // Windows: registry missing -> falls back to default C:\Program Files (x86)\Steam.
        var fallbackRoots = SteamLocator.ResolveWindowsSteamRoots(
            () => null,
            path => string.Equals(path, SteamLocator.DefaultWindowsSteamPath, StringComparison.OrdinalIgnoreCase));
        Assert.Equal([SteamLocator.DefaultWindowsSteamPath], fallbackRoots);

        // Windows: stale registry path does not exist -> falls back to default.
        var staleFallback = SteamLocator.ResolveWindowsSteamRoots(
            () => @"Z:\NonExistent\Steam",
            path => string.Equals(path, SteamLocator.DefaultWindowsSteamPath, StringComparison.OrdinalIgnoreCase));
        Assert.Equal([SteamLocator.DefaultWindowsSteamPath], staleFallback);

        // Linux: checks ~/.steam/steam and ~/.local/share/Steam.
        using var ws = new TempWorkspace("ffpr-linux-steam-roots");
        var fakeHome = Path.Combine(ws.Root, "home");
        var steamDotDir = Path.Combine(fakeHome, ".steam", "steam");
        var localShareDir = Path.Combine(fakeHome, ".local", "share", "Steam");
        Directory.CreateDirectory(steamDotDir);
        Directory.CreateDirectory(localShareDir);

        var linuxRoots = SteamLocator.ResolveLinuxSteamRoots(fakeHome, Directory.Exists);
        Assert.Equal([steamDotDir, localShareDir], linuxRoots);
    }

    [Fact]
    public void DiscoverGamesInSteamRoots_FindsSupportedGamesAcrossMultipleLibrariesAndIgnoresUnrelated()
    {
        using var ws = new TempWorkspace("ffpr-steam-tree");
        var primarySteam = Path.Combine(ws.Root, "Primary Steam");
        var secondaryLibrary = Path.Combine(ws.Root, "Second Drive", "Steam Library");

        Directory.CreateDirectory(Path.Combine(primarySteam, "steamapps", "common"));
        Directory.CreateDirectory(Path.Combine(secondaryLibrary, "steamapps", "common"));

        // Write libraryfolders.vdf pointing to both primary and secondary libraries (plus a non-existent one).
        var vdf = $$"""
            "libraryfolders"
            {
            	"0"
            	{
            		"path"		"{{EscapeVdfPath(primarySteam)}}"
            	}
            	"1"
            	{
            		"path"		"{{EscapeVdfPath(secondaryLibrary)}}"
            	}
            	"2"
            	{
            		"path"		"{{EscapeVdfPath(Path.Combine(ws.Root, "Missing Library"))}}"
            	}
            }
            """;
        File.WriteAllText(
            Path.Combine(primarySteam, "steamapps", "libraryfolders.vdf"),
            vdf,
            OutputGuard.Utf8WithoutBom);

        // Primary library: FF5 (installed) + unrelated game (3259780) + orphaned FF2 manifest (folder missing).
        var ff5Dir = Path.Combine(primarySteam, "steamapps", "common", "FINAL FANTASY V PR");
        Directory.CreateDirectory(ff5Dir);
        WriteAppManifest(primarySteam, 1173810, "FINAL FANTASY V", "FINAL FANTASY V PR", "20265087");
        WriteAppManifest(primarySteam, 1173780, "FINAL FANTASY II", "FINAL FANTASY II PR", "20251646");

        var unrelatedDir = Path.Combine(primarySteam, "steamapps", "common", "Other Game");
        Directory.CreateDirectory(unrelatedDir);
        WriteAppManifest(primarySteam, 3259780, "Other Game", "Other Game", "99999999");

        // Secondary library: FF1 + FF6 (installed).
        var ff1Dir = Path.Combine(secondaryLibrary, "steamapps", "common", "FINAL FANTASY PR");
        var ff6Dir = Path.Combine(secondaryLibrary, "steamapps", "common", "FINAL FANTASY VI PR");
        Directory.CreateDirectory(ff1Dir);
        Directory.CreateDirectory(ff6Dir);
        WriteAppManifest(secondaryLibrary, 1173770, "FINAL FANTASY", "FINAL FANTASY PR", "20249397");
        WriteAppManifest(secondaryLibrary, 1173820, "FINAL FANTASY VI", "FINAL FANTASY VI PR", "20265119");

        var games = SteamLocator.DiscoverGamesInSteamRoots([primarySteam]);

        Assert.Equal(3, games.Count);
        Assert.Equal("ff1", games[0].Spec.Id);
        Assert.Equal("FINAL FANTASY", games[0].DisplayName);
        Assert.Equal("20249397", games[0].SteamBuildId);
        Assert.Equal(Path.GetFullPath(ff1Dir), games[0].GameDirectory);

        Assert.Equal("ff5", games[1].Spec.Id);
        Assert.Equal("FINAL FANTASY V", games[1].DisplayName);
        Assert.Equal("20265087", games[1].SteamBuildId);
        Assert.Equal(Path.GetFullPath(ff5Dir), games[1].GameDirectory);

        Assert.Equal("ff6", games[2].Spec.Id);
        Assert.Equal("FINAL FANTASY VI", games[2].DisplayName);
        Assert.Equal("20265119", games[2].SteamBuildId);
        Assert.Equal(Path.GetFullPath(ff6Dir), games[2].GameDirectory);
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("   ", "")]
    [InlineData("\"C:\\Program Files (x86)\\Steam\\steamapps\\common\\FINAL FANTASY V PR\"", @"C:\Program Files (x86)\Steam\steamapps\common\FINAL FANTASY V PR")]
    [InlineData("  'D:\\My Games\\FF5 PR'  ", @"D:\My Games\FF5 PR")]
    [InlineData("“D:\\My Games\\FF5 PR”", @"D:\My Games\FF5 PR")]
    [InlineData("  \"\"D:\\Quoted Twice\\FF5\"\"  ", @"D:\Quoted Twice\FF5")]
    [InlineData("/tmp/Bob's Games/FF5", "/tmp/Bob's Games/FF5")]
    public void NormalizeUserPath_StripsMatchingOuterQuotesAndWhitespace(string? raw, string expected)
    {
        Assert.Equal(expected, InteractiveWizard.NormalizeUserPath(raw));
    }

    [Fact]
    public async Task InteractiveWizard_SingleSelectionDefaultOutput_InvokesBuildDbEntryPointAndPrintsSummary()
    {
        using var ws = new TempWorkspace("ffpr-wizard-single");
        var baseDir = Path.Combine(ws.Root, "app dir with spaces");
        Directory.CreateDirectory(baseDir);

        var ff1Spec = GameDetector.KnownGames.Single(g => g.Id == "ff1");
        var ff5Spec = GameDetector.KnownGames.Single(g => g.Id == "ff5");
        var ff1Dir = Path.Combine(ws.Root, "steam", "common", "FINAL FANTASY PR");
        var ff5Dir = Path.Combine(ws.Root, "steam", "common", "FINAL FANTASY V PR");
        Directory.CreateDirectory(ff1Dir);
        Directory.CreateDirectory(ff5Dir);

        var discovered = new List<DiscoveredSteamGame>
        {
            new(ff1Spec, "FINAL FANTASY", ff1Dir, "20249397", Path.Combine(ws.Root, "appmanifest_1173770.acf")),
            new(ff5Spec, "FINAL FANTASY V", ff5Dir, "20265087", Path.Combine(ws.Root, "appmanifest_1173810.acf")),
        };

        var invokedCalls = new List<string[]>();
        const string expectedFileHash = "1111111111111111111111111111111111111111111111111111111111111111";
        const string expectedContentHash = "2222222222222222222222222222222222222222222222222222222222222222";

        // Input: choose "2" (FF5), press Enter (default output dir), press Enter (confirm), press Enter (exit).
        var stdin = new StringReader("2\n\n\n\n");
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var env = new WizardEnvironment(
            Input: stdin,
            Output: stdout,
            Error: stderr,
            BaseDirectory: baseDir,
            DiscoverGames: () => discovered,
            InspectManualGameDirectory: _ => throw new InvalidOperationException("不应走手动路径"),
            RunBuildDb: args =>
            {
                invokedCalls.Add(args);
                var outIdx = Array.IndexOf(args, "--out");
                var outDir = args[outIdx + 1];
                WriteFakeBuildArtifacts(
                    outDir,
                    "ff5",
                    expectedFileHash,
                    expectedContentHash,
                    "matched",
                    KnownBuildsRegistry.MessageMatched,
                    "2026-11-25");
                return Task.FromResult(0);
            });

        var exitCode = await InteractiveWizard.RunAsync(env);
        Assert.Equal(0, exitCode);

        var expectedOutDir = Path.Combine(baseDir, "output", "ff5-20265087");
        var call = Assert.Single(invokedCalls);
        Assert.Equal(["--game", ff5Dir, "--out", expectedOutDir], call);

        var text = stdout.ToString();
        Assert.Contains("FINAL FANTASY V", text, StringComparison.Ordinal);
        Assert.Contains("全部", text, StringComparison.Ordinal);
        Assert.Contains(Path.Combine(expectedOutDir, "ff5.sqlite"), text, StringComparison.Ordinal);
        Assert.Contains(expectedFileHash, text, StringComparison.Ordinal);
        Assert.Contains(expectedContentHash, text, StringComparison.Ordinal);
        Assert.Contains(KnownBuildsRegistry.MessageMatched, text, StringComparison.Ordinal);
        Assert.Contains("按回车退出", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InteractiveWizard_SelectAllOption_BuildsEveryDiscoveredGameAndSummarizesSuccessAndFailure()
    {
        using var ws = new TempWorkspace("ffpr-wizard-all");
        var baseDir = Path.Combine(ws.Root, "app");
        Directory.CreateDirectory(baseDir);

        var ff1Spec = GameDetector.KnownGames.Single(g => g.Id == "ff1");
        var ff5Spec = GameDetector.KnownGames.Single(g => g.Id == "ff5");
        var ff1Dir = Path.Combine(ws.Root, "steam", "FINAL FANTASY PR");
        var ff5Dir = Path.Combine(ws.Root, "steam", "FINAL FANTASY V PR");
        Directory.CreateDirectory(ff1Dir);
        Directory.CreateDirectory(ff5Dir);

        var discovered = new List<DiscoveredSteamGame>
        {
            new(ff1Spec, "FINAL FANTASY", ff1Dir, "20249397", ""),
            new(ff5Spec, "FINAL FANTASY V", ff5Dir, "20265087", ""),
        };

        var customOutRoot = Path.Combine(ws.Root, "batch output with spaces");
        var invokedCalls = new List<string[]>();

        // Input: "3" (All), quoted custom output root with spaces, Enter (confirm), Enter (exit).
        var stdin = new StringReader($"3\n\"{customOutRoot}\"\n\n\n");
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var env = new WizardEnvironment(
            Input: stdin,
            Output: stdout,
            Error: stderr,
            BaseDirectory: baseDir,
            DiscoverGames: () => discovered,
            InspectManualGameDirectory: _ => throw new InvalidOperationException("不应走手动路径"),
            RunBuildDb: args =>
            {
                invokedCalls.Add(args);
                var gameArg = args[Array.IndexOf(args, "--game") + 1];
                var outArg = args[Array.IndexOf(args, "--out") + 1];
                if (gameArg == ff1Dir)
                {
                    WriteFakeBuildArtifacts(
                        outArg,
                        "ff1",
                        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                        "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
                        "matched",
                        KnownBuildsRegistry.MessageMatched,
                        "2026-11-25");
                    return Task.FromResult(0);
                }

                throw new FileNotFoundException("缺少模拟资源文件：catalog.json");
            });

        var exitCode = await InteractiveWizard.RunAsync(env);
        Assert.Equal(2, exitCode);
        Assert.Equal(2, invokedCalls.Count);
        Assert.Equal(["--game", ff1Dir, "--out", Path.Combine(customOutRoot, "ff1-20249397")], invokedCalls[0]);
        Assert.Equal(["--game", ff5Dir, "--out", Path.Combine(customOutRoot, "ff5-20265087")], invokedCalls[1]);

        var text = stdout.ToString();
        Assert.Contains("全部游戏构建汇总", text, StringComparison.Ordinal);
        Assert.Contains("成功 1", text, StringComparison.Ordinal);
        Assert.Contains("失败 1", text, StringComparison.Ordinal);
        Assert.Contains("验证游戏文件的完整性", text, StringComparison.Ordinal);
        Assert.Contains("按回车退出", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InteractiveWizard_NoGamesFound_AcceptsQuotedManualPathAndRealBuildDbCommand()
    {
        using var ws = new TempWorkspace("ffpr-wizard-manual-real-builddb");
        var spec = GameDetector.KnownGames.Single(g => g.Id == "ff5");
        var steamRoot = Path.Combine(ws.Root, "My Steam Root");
        var gameDir = SyntheticUnityFixtureBuilder.BuildSyntheticSteamGame(
            steamRoot,
            spec,
            steamBuildId: "20265087",
            customCommonFolderName: "FINAL FANTASY V PR");
        WriteSyntheticPeExe(gameDir, spec.ExeName);

        var baseDir = Path.Combine(ws.Root, "exe dir");
        var customOutDir = Path.Combine(ws.Root, "custom out with spaces", "ff5 build");
        var emptyKnownDir = Path.Combine(ws.Root, "known-empty");
        Directory.CreateDirectory(baseDir);
        Directory.CreateDirectory(emptyKnownDir);

        var fakeDbRunner = new FakeBuildDbRunner();
        var buildDbCalled = false;

        // Input: quoted game path with spaces, quoted output path with spaces, Enter (confirm), Enter (exit).
        var stdin = new StringReader($"  \"{gameDir}\"  \n'{customOutDir}'\n\n\n");
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var env = new WizardEnvironment(
            Input: stdin,
            Output: stdout,
            Error: stderr,
            BaseDirectory: baseDir,
            DiscoverGames: () => [],
            InspectManualGameDirectory: path =>
            {
                var detected = GameDetector.Detect(path);
                return new DiscoveredSteamGame(
                    detected.Spec,
                    detected.Spec.Title,
                    detected.GameDirectory,
                    detected.SteamBuildId,
                    detected.AppManifestPath ?? string.Empty);
            },
            RunBuildDb: args =>
            {
                buildDbCalled = true;
                return BuildDbCommand.RunAsync(
                    args,
                    fakeDbRunner,
                    knownBuildsBaseDirOverride: emptyKnownDir,
                    stdout: stdout,
                    stderr: stderr);
            });

        var exitCode = await InteractiveWizard.RunAsync(env);
        Assert.Equal(0, exitCode);
        Assert.True(buildDbCalled, "向导必须调用 BuildDbCommand 建库入口");
        Assert.True(File.Exists(Path.Combine(customOutDir, "ff5.sqlite")));
        Assert.True(File.Exists(Path.Combine(customOutDir, "ff5.sqlite.build.json")));

        var text = stdout.ToString();
        Assert.Contains("未在本地 Steam 游戏库中自动找到", text, StringComparison.Ordinal);
        Assert.Contains(fakeDbRunner.ContentSha256, text, StringComparison.Ordinal);
        Assert.Contains(KnownBuildsRegistry.MessageNewGameVersion, text, StringComparison.Ordinal);
        Assert.Contains("按回车退出", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InteractiveWizard_RejectsForeignOutputDirectoryAndAutoForcesOwnBuildDbDirectory()
    {
        using var ws = new TempWorkspace("ffpr-wizard-output-guards");
        var baseDir = Path.Combine(ws.Root, "app");
        Directory.CreateDirectory(baseDir);

        var spec = GameDetector.KnownGames.Single(g => g.Id == "ff5");
        var gameDir = Path.Combine(ws.Root, "steam", "FINAL FANTASY V PR");
        Directory.CreateDirectory(gameDir);

        var foreignDir = Path.Combine(ws.Root, "foreign-non-empty");
        Directory.CreateDirectory(foreignDir);
        File.WriteAllText(Path.Combine(foreignDir, "user-note.txt"), "do not touch");

        var ownExistingDir = Path.Combine(ws.Root, "existing-build-db");
        Directory.CreateDirectory(ownExistingDir);
        File.WriteAllText(
            Path.Combine(ownExistingDir, "manifest.json"),
            """{"tool":"ffpr-export","command":"build-db"}""",
            OutputGuard.Utf8WithoutBom);

        var discovered = new List<DiscoveredSteamGame>
        {
            new(spec, "FINAL FANTASY V", gameDir, "20265087", ""),
        };

        string[]? capturedArgs = null;
        // Pick game 1 -> try foreignDir (rejected) -> pick ownExistingDir (accepted with --force) -> confirm -> exit.
        var stdin = new StringReader($"1\n\"{foreignDir}\"\n\"{ownExistingDir}\"\n\n\n");
        var stdout = new StringWriter();
        var env = new WizardEnvironment(
            Input: stdin,
            Output: stdout,
            Error: new StringWriter(),
            BaseDirectory: baseDir,
            DiscoverGames: () => discovered,
            InspectManualGameDirectory: _ => throw new InvalidOperationException("不应走手动路径"),
            RunBuildDb: args =>
            {
                capturedArgs = args;
                WriteFakeBuildArtifacts(
                    ownExistingDir,
                    "ff5",
                    "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc",
                    "dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd",
                    "new_game_version",
                    KnownBuildsRegistry.MessageNewGameVersion,
                    null);
                return Task.FromResult(0);
            });

        var exitCode = await InteractiveWizard.RunAsync(env);
        Assert.Equal(0, exitCode);
        Assert.NotNull(capturedArgs);
        Assert.Equal(["--game", gameDir, "--out", ownExistingDir, "--force"], capturedArgs);
        Assert.Contains("不是 ffpr-export 的建库目录", stdout.ToString(), StringComparison.Ordinal);
        Assert.Contains("清空并重新构建", stdout.ToString(), StringComparison.Ordinal);

        // Cancelling at confirmation must not call build-db.
        var calledOnCancel = false;
        var cancelEnv = env with
        {
            Input = new StringReader("1\n\nn\n\n"),
            Output = new StringWriter(),
            RunBuildDb = _ =>
            {
                calledOnCancel = true;
                return Task.FromResult(0);
            },
        };
        Assert.Equal(0, await InteractiveWizard.RunAsync(cancelEnv));
        Assert.False(calledOnCancel);
    }

    [Fact]
    public async Task CliWithNoArguments_LaunchesWizardWithRedirectedStdinAndBuildsSyntheticGame()
    {
        using var ws = new TempWorkspace("ffpr-wizard-cli-zero-args");
        var spec = GameDetector.KnownGames.Single(g => g.Id == "ff5");
        var fakeHome = Path.Combine(ws.Root, "fake-home");
        var steamRoot = Path.Combine(fakeHome, ".local", "share", "Steam");
        var gameDir = SyntheticUnityFixtureBuilder.BuildSyntheticSteamGame(
            steamRoot,
            spec,
            steamBuildId: "20265087",
            customCommonFolderName: "FINAL FANTASY V PR");
        WriteSyntheticPeExe(gameDir, spec.ExeName);

        // Ensure appmanifest includes installdir so SteamLocator discovers it automatically on Linux.
        WriteAppManifest(steamRoot, spec.AppId, spec.Title, "FINAL FANTASY V PR", "20265087");

        var outDir = Path.Combine(ws.Root, "wizard cli out");
        string pipedInput;
        Dictionary<string, string?>? extraEnv = null;
        if (OperatingSystem.IsWindows())
        {
            var hostGames = SteamLocator.DiscoverGames();
            pipedInput = hostGames.Count > 0
                ? $"0\n\"{gameDir}\"\n\"{outDir}\"\n\n\n"
                : $"\"{gameDir}\"\n\"{outDir}\"\n\n\n";
        }
        else
        {
            pipedInput = $"1\n\"{outDir}\"\n\n\n";
            extraEnv = new Dictionary<string, string?>
            {
                ["HOME"] = fakeHome,
            };
        }

        var result = await TestHarness.RunCliAsync(
            [],
            ffprDeps: ws.DepsDir,
            standardInput: pipedInput,
            extraEnv: extraEnv);

        Assert.True(result.ExitCode == 0, $"stdout:\n{result.Stdout}\nstderr:\n{result.Stderr}");
        Assert.True(File.Exists(Path.Combine(outDir, "ff5.sqlite")));
        Assert.True(File.Exists(Path.Combine(outDir, "ff5.sqlite.build.json")));
        Assert.Contains("导出与建库向导", result.Stdout, StringComparison.Ordinal);
        Assert.Contains("FINAL FANTASY V", result.Stdout, StringComparison.Ordinal);
        Assert.Contains("按回车退出", result.Stdout, StringComparison.Ordinal);
    }

    private static void WriteSyntheticPeExe(string gameDir, string exeName)
    {
        var peBytes = BuildDbTests.BuildMinimalPeWithVersionResource(
            fileVersion: (2019, 4, 26, 32270),
            productVersion: (1, 0, 0, 0),
            stringFileVersion: "2019.4.26.3227036",
            stringProductVersion: "2019.4.26.3227036",
            sectionName: ".rsrc",
            pe32Plus: true);
        File.WriteAllBytes(Path.Combine(gameDir, exeName), peBytes);
    }

    private static string EscapeVdfPath(string path) => path.Replace("\\", "\\\\", StringComparison.Ordinal);

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

    private static void WriteFakeBuildArtifacts(
        string outDir,
        string gameId,
        string fileSha256,
        string contentSha256,
        string checkStatus,
        string checkMessage,
        string? registeredAt)
    {
        Directory.CreateDirectory(outDir);
        var sqliteName = $"{gameId}.sqlite";
        File.WriteAllBytes(Path.Combine(outDir, sqliteName), Encoding.ASCII.GetBytes("SQLite format 3\0fake"));
        var report = new
        {
            format = 1,
            tool = OutputGuard.ToolName,
            command = "build-db",
            status = "completed",
            game_id = gameId,
            database = new
            {
                file_name = sqliteName,
                file_sha256 = fileSha256,
                content_sha256 = contentSha256,
            },
            known_build_check = new
            {
                status = checkStatus,
                message = checkMessage,
                source = "embedded",
                matched_registered_at = registeredAt,
                table_diffs = Array.Empty<object>(),
            },
            timings_ms = new
            {
                game_version = 100L,
                inventory = 200L,
                export = 300L,
                db_build = 400L,
                db_hash = 50L,
                total = 1050L,
            },
        };
        File.WriteAllText(
            Path.Combine(outDir, $"{sqliteName}.build.json"),
            JsonSerializer.Serialize(report, FieldJsonWriter.SerializerOptions),
            OutputGuard.Utf8WithoutBom);
    }
}
