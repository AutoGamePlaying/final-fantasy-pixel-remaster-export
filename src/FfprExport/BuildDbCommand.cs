using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace FfprExport;

internal sealed record BuildDbOptions(
    string GameDirectory,
    string? OutputArgument,
    string? SteamBuildId,
    bool KeepJson,
    int Jobs,
    bool Force,
    bool ProgressJson,
    bool ShowHelp);

internal interface IBuildDbDatabaseRunner
{
    Task<int> BuildAsync(string exportDirectory, string sqlitePath, int jobs);
    Task<FfprDbHashReport> HashAsync(string sqlitePath);
}

/// <summary>
/// Invokes the in-process `db build` and `db hash --json` subcommands without spawning a child process.
/// </summary>
internal sealed class InProcessDbCommandRunner : IBuildDbDatabaseRunner
{
    private static readonly SemaphoreSlim ConsoleCaptureGate = new(1, 1);

    public async Task<int> BuildAsync(string exportDirectory, string sqlitePath, int jobs)
    {
        string[] dbArgs =
        [
            "db",
            "build",
            "--export",
            exportDirectory,
            "--out",
            sqlitePath,
            "--jobs",
            jobs.ToString(CultureInfo.InvariantCulture),
        ];

        var (exitCode, stdout, stderr) = await InvokeProgramInProcessAsync(dbArgs);
        if (!string.IsNullOrWhiteSpace(stdout))
        {
            Console.Write(stdout);
            if (!stdout.EndsWith('\n'))
            {
                Console.WriteLine();
            }
        }

        if (exitCode != 0)
        {
            if (IsDbSubcommandUnavailable(stderr))
            {
                throw new NotSupportedException(L.Get("BuildDb_DbSubcommandUnavailableForBuild"));
            }

            var detail = string.IsNullOrWhiteSpace(stderr) ? L.Get("Common_UnknownError") : stderr.Trim();
            throw new InvalidOperationException(L.Format("BuildDb_DbBuildFailed", sqlitePath, exitCode, detail));
        }

        return exitCode;
    }

    public async Task<FfprDbHashReport> HashAsync(string sqlitePath)
    {
        var (exitCode, stdout, stderr) = await InvokeProgramInProcessAsync(["db", "hash", sqlitePath, "--json"]);
        if (exitCode != 0)
        {
            if (IsDbSubcommandUnavailable(stderr))
            {
                throw new NotSupportedException(L.Get("BuildDb_DbSubcommandUnavailableForHash"));
            }

            var detail = string.IsNullOrWhiteSpace(stderr) ? L.Get("Common_UnknownError") : stderr.Trim();
            throw new InvalidOperationException(L.Format("BuildDb_DbHashFailed", sqlitePath, exitCode, detail));
        }

        return ParseDbHashReport(stdout, sqlitePath);
    }

    internal static FfprDbHashReport ParseDbHashReport(string json, string sqlitePath)
    {
        FfprDbHashReport? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<FfprDbHashReport>(json);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                L.Format("BuildDb_ParseHashJsonFailed", sqlitePath, exception.Message),
                exception);
        }

        if (parsed is null
            || string.IsNullOrWhiteSpace(parsed.FileSha256)
            || string.IsNullOrWhiteSpace(parsed.ContentSha256)
            || parsed.Tables is null)
        {
            throw new InvalidDataException(L.Format("BuildDb_HashJsonMissingFields", sqlitePath));
        }

        return parsed with
        {
            FileSha256 = parsed.FileSha256.ToLowerInvariant(),
            ContentSha256 = parsed.ContentSha256.ToLowerInvariant(),
        };
    }

    private static bool IsDbSubcommandUnavailable(string stderr)
    {
        return stderr.Contains(L.Format("Cli_UnknownSubcommand", "db"), StringComparison.Ordinal);
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> InvokeProgramInProcessAsync(string[] args)
    {
        await ConsoleCaptureGate.WaitAsync();
        var originalOut = Console.Out;
        var originalErr = Console.Error;
        using var outWriter = new StringWriter(CultureInfo.InvariantCulture);
        using var errWriter = new StringWriter(CultureInfo.InvariantCulture);

        try
        {
            Console.SetOut(outWriter);
            Console.SetError(errWriter);
            var exitCode = await Program.RunInProcessAsync(args);
            return (exitCode, outWriter.ToString(), errWriter.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalErr);
            ConsoleCaptureGate.Release();
        }
    }
}

internal static class BuildDbCommand
{
    public static Task<int> RunAsync(string[] args)
    {
        return RunAsync(
            args,
            dbRunner: new InProcessDbCommandRunner(),
            knownBuildsBaseDirOverride: null,
            stdout: null,
            stderr: null);
    }

    internal static async Task<int> RunAsync(
        string[] args,
        IBuildDbDatabaseRunner dbRunner,
        string? knownBuildsBaseDirOverride = null,
        TextWriter? stdout = null,
        TextWriter? stderr = null)
    {
        var outWriter = stdout ?? Console.Out;
        var errWriter = stderr ?? Console.Error;
        var options = ParseArgs(args);
        if (options.ShowHelp)
        {
            PrintHelp(outWriter);
            return 0;
        }

        var totalStopwatch = Stopwatch.StartNew();

        // Step 1: Detect game (FF1-FF6), validate output guardrails, and prepare output directory.
        var game = GameDetector.Detect(options.GameDirectory);
        var effectiveBuildId = !string.IsNullOrWhiteSpace(game.SteamBuildId)
            ? game.SteamBuildId
            : options.SteamBuildId;
        string outputArgument;
        if (options.OutputArgument is not null)
        {
            outputArgument = options.OutputArgument;
        }
        else
        {
            var agpRoot = AgpPaths.RequireWritableAgpHome();
            var rootNotice = AgpPaths.FormatCliNotice(agpRoot);
            if (!string.IsNullOrWhiteSpace(rootNotice))
            {
                outWriter.WriteLine(rootNotice);
            }

            outputArgument = AgpPaths.GetGameOutputDirectory(game.Spec.Id, effectiveBuildId, agpRoot);
        }

        var outputPath = PathSupport.TrimTrailingSeparators(
            OutputGuard.ValidateOutputPath(outputArgument, game.GameDirectory));

        PrepareOutputDirectory(outputPath, outputArgument, options.Force, outWriter);

        var startedAt = DateTimeOffset.UtcNow;
        var sqliteFileName = $"{game.Spec.Id}.sqlite";
        var sqlitePath = Path.Combine(outputPath, sqliteFileName);
        var buildReportPath = Path.Combine(outputPath, $"{sqliteFileName}.build.json");
        var manifestPath = Path.Combine(outputPath, "manifest.json");

        WriteInProgressSentinels(manifestPath, buildReportPath, game, startedAt);

        // Step 2: Identify game version (Steam buildid, IL2CPP hashes, PE version, Unity version, resource fingerprint).
        EmitProgressStep(outWriter, options.ProgressJson, 1, 6, L.Format("BuildDb_Step1", game.Spec.Id));
        var versionStopwatch = Stopwatch.StartNew();
        var (gameVersion, fingerprintResult) = GameVersionDetector.Inspect(
            game,
            options.SteamBuildId,
            options.Jobs,
            errWriter);
        versionStopwatch.Stop();

        outWriter.WriteLine(L.Format("BuildDb_GameInfo", game.Spec.Id, game.Spec.Title, game.Spec.AppId, game.UnityVersion));
        outWriter.WriteLine(L.Format(
            "BuildDb_VersionInfo",
            game.Spec.Id,
            gameVersion.Steam.BuildId ?? "null",
            gameVersion.Steam.Source ?? L.Get("Common_None"),
            gameVersion.Exe?.FileVersion ?? "null",
            gameVersion.Exe?.ProductVersion ?? "null"));
        outWriter.WriteLine(L.Format(
            "BuildDb_FingerprintInfo",
            game.Spec.Id,
            gameVersion.ResourceFingerprint,
            gameVersion.BundleCount,
            fingerprintResult.HashedBytes,
            fingerprintResult.ElapsedMs));

        var (toolVersion, gitCommit) = KnownBuildsRegistry.ReadBuildMetadata();

        // Step 3: Run inventory and export in-process into <out>/json/{inventory,export}.
        EmitProgressStep(outWriter, options.ProgressJson, 2, 6, L.Format("BuildDb_Step2", game.Spec.Id));
        var jsonRoot = Path.Combine(outputPath, "json");
        var inventoryDir = Path.Combine(jsonRoot, "inventory");
        var exportDir = Path.Combine(jsonRoot, "export");
        Directory.CreateDirectory(PathSupport.ToIoPath(inventoryDir));
        Directory.CreateDirectory(PathSupport.ToIoPath(exportDir));

        var jobsText = options.Jobs.ToString(CultureInfo.InvariantCulture);

        var inventoryWatch = Stopwatch.StartNew();
        var inventoryExit = await InventoryCommand.RunAsync(
        [
            "--game", game.GameDirectory,
            "--out", inventoryDir,
            "--jobs", jobsText,
            "--force",
        ]);
        inventoryWatch.Stop();

        var exportWatch = Stopwatch.StartNew();
        var exportExit = await ExportCommand.RunAsync(
        [
            "--game", game.GameDirectory,
            "--out", exportDir,
            "--jobs", jobsText,
            "--force",
        ]);
        exportWatch.Stop();

        // Step 4: Run db build and db hash --json in-process.
        EmitProgressStep(outWriter, options.ProgressJson, 3, 6, L.Format("BuildDb_Step3", game.Spec.Id, sqliteFileName));
        var dbBuildWatch = Stopwatch.StartNew();
        await dbRunner.BuildAsync(exportDir, sqlitePath, options.Jobs);
        dbBuildWatch.Stop();

        if (!File.Exists(PathSupport.ToIoPath(sqlitePath)))
        {
            throw new FileNotFoundException(L.Format("BuildDb_DbFileMissing", sqlitePath));
        }

        EmitProgressStep(outWriter, options.ProgressJson, 4, 6, L.Format("BuildDb_Step4", game.Spec.Id));
        var dbHashWatch = Stopwatch.StartNew();
        var hashReport = await dbRunner.HashAsync(sqlitePath);
        dbHashWatch.Stop();

        var dbFileSize = new FileInfo(PathSupport.ToIoPath(sqlitePath)).Length;
        var dbInfo = new BuildDatabaseInfo(
            sqliteFileName,
            dbFileSize,
            hashReport.SchemaVersion,
            hashReport.SqliteVersion,
            hashReport.FileSha256.ToLowerInvariant(),
            hashReport.ContentSha256.ToLowerInvariant(),
            hashReport.Format,
            hashReport.Tables);

        outWriter.WriteLine(L.Format(
            "BuildDb_DbHashesInfo",
            game.Spec.Id,
            dbInfo.FileSha256,
            dbInfo.ContentSha256 ?? "null",
            dbInfo.Tables.Length));

        // Step 5 & 6: Compare against known-builds.json and print conclusion.
        EmitProgressStep(outWriter, options.ProgressJson, 5, 6, L.Format("BuildDb_Step5", game.Spec.Id));
        var (knownByGame, knownSource) = KnownBuildsRegistry.Load(knownBuildsBaseDirOverride);
        var comparison = KnownBuildsRegistry.Compare(
            knownByGame,
            knownSource,
            game.Spec.Id,
            gameVersion,
            dbInfo);
        KnownBuildsRegistry.PrintComparison(comparison, outWriter);

        // Step 7: Cleanup <out>/json/ unless --keep-json is passed.
        long cleanupJsonMs = 0;
        if (!options.KeepJson && Directory.Exists(PathSupport.ToIoPath(jsonRoot)))
        {
            var cleanupWatch = Stopwatch.StartNew();
            Directory.Delete(PathSupport.ToIoPath(jsonRoot), recursive: true);
            cleanupWatch.Stop();
            cleanupJsonMs = cleanupWatch.ElapsedMilliseconds;
            outWriter.WriteLine(L.Format("BuildDb_CleanedTempJson", game.Spec.Id, jsonRoot, cleanupJsonMs));
        }

        totalStopwatch.Stop();
        var finishedAt = DateTimeOffset.UtcNow;

        EmitProgressStep(outWriter, options.ProgressJson, 6, 6, L.Format("BuildDb_Step6", game.Spec.Id, Path.GetFileName(buildReportPath)));
        var toolsInfo = new BuildToolsInfo(
            new FfprExportToolInfo(
                toolVersion,
                gitCommit,
                Program.AssetsToolsVersion,
                Program.AssetsToolsCpp2IlVersion));

        var buildReport = new
        {
            format = 1,
            tool = OutputGuard.ToolName,
            command = "build-db",
            status = "completed",
            game_id = game.Spec.Id,
            started_at = OutputGuard.FormatUtc(startedAt),
            built_at = OutputGuard.FormatUtc(finishedAt),
            keep_json = options.KeepJson,
            game_version = gameVersion,
            tools = toolsInfo,
            database = dbInfo,
            known_build_check = comparison,
            export_exit_codes = new
            {
                inventory = inventoryExit,
                export = exportExit,
            },
            timings_ms = new
            {
                game_version = versionStopwatch.ElapsedMilliseconds,
                resource_fingerprint = fingerprintResult.ElapsedMs,
                inventory = inventoryWatch.ElapsedMilliseconds,
                export = exportWatch.ElapsedMilliseconds,
                db_build = dbBuildWatch.ElapsedMilliseconds,
                db_hash = dbHashWatch.ElapsedMilliseconds,
                cleanup_json = cleanupJsonMs,
                total = totalStopwatch.ElapsedMilliseconds,
            },
        };

        WriteJsonAtomically(buildReportPath, buildReport);
        WriteCompletedManifest(manifestPath, game, startedAt, finishedAt, totalStopwatch.Elapsed.TotalSeconds, sqliteFileName);

        outWriter.WriteLine(L.Format(
            "BuildDb_Completed",
            game.Spec.Id,
            sqlitePath,
            buildReportPath,
            totalStopwatch.Elapsed.TotalSeconds));

        return (inventoryExit != 0 || exportExit != 0) ? 1 : 0;
    }

    internal static void EmitProgressStep(
        TextWriter outWriter,
        bool progressJson,
        int step,
        int total,
        string humanLine)
    {
        if (progressJson)
        {
            outWriter.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"@@progress {{\"step\":{step},\"total\":{total}}}"));
        }

        outWriter.WriteLine(humanLine);
    }

    internal static BuildDbOptions ParseArgs(string[] args)
    {
        string? gameDirectory = null;
        string? outputArgument = null;
        string? steamBuildId = null;
        var keepJson = false;
        var jobs = Math.Clamp(Environment.ProcessorCount, 1, 8);
        var force = false;
        var progressJson = false;
        var showHelp = false;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--help":
                case "-h":
                    showHelp = true;
                    break;
                case "--game":
                    gameDirectory = Program.RequireValue(args, ref i, "--game");
                    break;
                case "--out":
                    outputArgument = Program.RequireValue(args, ref i, "--out");
                    break;
                case "--steam-buildid":
                    steamBuildId = Program.RequireValue(args, ref i, "--steam-buildid");
                    break;
                case "--keep-json":
                    keepJson = true;
                    break;
                case "--jobs":
                    var rawJobs = Program.RequireValue(args, ref i, "--jobs");
                    if (!int.TryParse(rawJobs, NumberStyles.Integer, CultureInfo.InvariantCulture, out jobs) || jobs < 1)
                    {
                        throw new ArgumentException(L.Format("Cli_JobsMustBePositive", rawJobs));
                    }

                    break;
                case "--force":
                    force = true;
                    break;
                case "--progress-json":
                    progressJson = true;
                    break;
                default:
                    throw new ArgumentException(L.Format("BuildDb_UnknownArg", args[i]));
            }
        }

        if (showHelp)
        {
            return new BuildDbOptions(string.Empty, outputArgument, steamBuildId, keepJson, jobs, force, progressJson, ShowHelp: true);
        }

        if (string.IsNullOrWhiteSpace(gameDirectory))
        {
            throw new ArgumentException(L.Get("BuildDb_MissingGameArg"));
        }

        return new BuildDbOptions(
            gameDirectory,
            outputArgument,
            steamBuildId,
            keepJson,
            jobs,
            force,
            progressJson,
            ShowHelp: false);
    }

    private static void PrepareOutputDirectory(
        string outputPath,
        string outputArgument,
        bool force,
        TextWriter outWriter)
    {
        var ioOutputPath = PathSupport.ToIoPath(outputPath);
        if (Directory.Exists(ioOutputPath) && Directory.EnumerateFileSystemEntries(ioOutputPath).Any())
        {
            if (!force)
            {
                throw new IOException(L.Format("BuildDb_OutputNotEmpty", outputArgument));
            }

            RequireOwnBuildDbDirectory(ioOutputPath, outputArgument);
            var fileCount = Directory.EnumerateFiles(ioOutputPath, "*", SearchOption.AllDirectories).LongCount();
            outWriter.WriteLine(L.Format("BuildDb_ClearingOutput", outputArgument, fileCount));
            DeleteOwnEntries(ioOutputPath);
        }

        Directory.CreateDirectory(ioOutputPath);
    }

    private static void RequireOwnBuildDbDirectory(string ioOutputPath, string outputArgument)
    {
        var manifestPath = Path.Combine(ioOutputPath, "manifest.json");
        if (!File.Exists(manifestPath))
        {
            throw new IOException(L.Format("BuildDb_NotOwnDirectory", outputArgument, OutputGuard.ToolName));
        }

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath, Encoding.UTF8));
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("tool", out var toolEl)
                || !string.Equals(toolEl.GetString(), OutputGuard.ToolName, StringComparison.Ordinal))
            {
                throw new IOException(L.Format("BuildDb_NotOwnDirectory", outputArgument, OutputGuard.ToolName));
            }

            var actualCommand = doc.RootElement.TryGetProperty("command", out var cmdEl) ? cmdEl.GetString() : null;
            if (!string.Equals(actualCommand, "build-db", StringComparison.Ordinal))
            {
                throw new IOException(L.Format(
                    "BuildDb_WrongCommandDirectory",
                    outputArgument,
                    OutputGuard.ToolName,
                    actualCommand ?? L.Get("BuildDb_UnknownCommand")));
            }
        }
        catch (JsonException ex)
        {
            throw new IOException(L.Format("BuildDb_NotOwnDirectory", outputArgument, OutputGuard.ToolName), ex);
        }
    }

    private static void DeleteOwnEntries(string ioOutputPath)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(ioOutputPath))
        {
            if (Directory.Exists(entry) && IsOtherCommandSubdirectory(entry))
            {
                continue;
            }

            if (Directory.Exists(entry))
            {
                Directory.Delete(entry, recursive: true);
            }
            else
            {
                File.Delete(entry);
            }
        }
    }

    private static bool IsOtherCommandSubdirectory(string ioDirectoryPath)
    {
        var manifestPath = Path.Combine(ioDirectoryPath, "manifest.json");
        if (!File.Exists(manifestPath))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath, Encoding.UTF8));
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            var isOwnTool = doc.RootElement.TryGetProperty("tool", out var toolEl)
                && string.Equals(toolEl.GetString(), OutputGuard.ToolName, StringComparison.Ordinal);
            var command = doc.RootElement.TryGetProperty("command", out var cmdEl)
                ? cmdEl.GetString()
                : null;
            return isOwnTool && !string.Equals(command, "build-db", StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    private static void WriteInProgressSentinels(
        string manifestPath,
        string buildReportPath,
        DetectedGame game,
        DateTimeOffset startedAt)
    {
        var manifestSentinel = new
        {
            tool = OutputGuard.ToolName,
            toolVersion = Program.ToolVersion,
            assetsToolsVersion = Program.AssetsToolsVersion,
            command = "build-db",
            status = "in_progress",
            game = new
            {
                id = game.Spec.Id,
                title = game.Spec.Title,
                appId = game.Spec.AppId,
                dataDirName = game.Spec.DataDirName,
            },
            startedAt = OutputGuard.FormatUtc(startedAt),
        };
        WriteJsonAtomically(manifestPath, manifestSentinel);

        var buildSentinel = new
        {
            format = 1,
            tool = OutputGuard.ToolName,
            command = "build-db",
            status = "in_progress",
            game_id = game.Spec.Id,
            started_at = OutputGuard.FormatUtc(startedAt),
        };
        WriteJsonAtomically(buildReportPath, buildSentinel);
    }

    private static void WriteCompletedManifest(
        string manifestPath,
        DetectedGame game,
        DateTimeOffset startedAt,
        DateTimeOffset finishedAt,
        double elapsedSeconds,
        string sqliteFileName)
    {
        var manifest = new
        {
            tool = OutputGuard.ToolName,
            toolVersion = Program.ToolVersion,
            assetsToolsVersion = Program.AssetsToolsVersion,
            command = "build-db",
            status = "completed",
            game = new
            {
                id = game.Spec.Id,
                title = game.Spec.Title,
                appId = game.Spec.AppId,
                dataDirName = game.Spec.DataDirName,
            },
            databaseFile = sqliteFileName,
            startedAt = OutputGuard.FormatUtc(startedAt),
            finishedAt = OutputGuard.FormatUtc(finishedAt),
            elapsedSeconds = Math.Round(elapsedSeconds, 3),
        };
        WriteJsonAtomically(manifestPath, manifest);
    }

    private static void WriteJsonAtomically(string targetPath, object value)
    {
        TextFileWriter.WriteJsonAtomic(
            targetPath,
            value,
            FieldJsonWriter.SerializerOptions,
            trailingNewLine: true);
    }

    private static void PrintHelp(TextWriter outWriter)
    {
        outWriter.WriteLine(L.Get("BuildDb_HelpText"));
    }
}
