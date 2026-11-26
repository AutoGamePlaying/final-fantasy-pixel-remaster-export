using System.Globalization;
using System.Text;
using System.Text.Json;

namespace FfprExport.Db;

internal static class DbCommand
{
    public static Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0 || args[0] is "--help" or "-h" or "help")
        {
            PrintHelp();
            return Task.FromResult(args.Length == 0 ? 2 : 0);
        }

        var sub = args[0];
        var rest = args[1..];
        var exitCode = sub switch
        {
            "build" => RunBuild(rest),
            "hash" => RunHash(rest),
            "stats" => RunStats(rest),
            "sql" => RunSql(rest),
            "write-fixture" => RunWriteFixture(rest),
            _ => throw new ArgumentException(L.Format("Db_UnknownSubcommand", sub)),
        };

        return Task.FromResult(exitCode);
    }

    private static int RunBuild(string[] args)
    {
        string? exportDir = null;
        string? outPath = null;
        var jobs = Math.Min(Environment.ProcessorCount, 8);

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--help":
                case "-h":
                    PrintHelp();
                    return 0;
                case "--export":
                    exportDir = Program.RequireValue(args, ref i, "--export");
                    break;
                case "--out":
                    outPath = Program.RequireValue(args, ref i, "--out");
                    break;
                case "--jobs":
                    var rawJobs = Program.RequireValue(args, ref i, "--jobs");
                    if (!int.TryParse(rawJobs, NumberStyles.Integer, CultureInfo.InvariantCulture, out jobs) || jobs < 1)
                    {
                        throw new ArgumentException(L.Format("Cli_JobsMustBePositive", rawJobs));
                    }

                    break;
                default:
                    throw new ArgumentException(L.Format("Db_BuildUnknownArg", args[i]));
            }
        }

        if (string.IsNullOrWhiteSpace(exportDir))
        {
            throw new ArgumentException(L.Get("Db_BuildMissingExport"));
        }

        if (string.IsNullOrWhiteSpace(outPath))
        {
            outPath = TryInferDefaultOutPath(exportDir)
                ?? throw new ArgumentException(L.Get("Db_BuildMissingOut"));
        }

        var report = DbBuilder.Build(new DbBuildOptions(exportDir, outPath, jobs));

        Console.WriteLine(L.Format(
            "Db_BuildComplete",
            report.OutputPath,
            report.GameId,
            report.SchemaVersion,
            report.SqliteVersion,
            FormatBytes(report.FileSizeBytes),
            report.TotalMillis));
        Console.WriteLine(L.Format("Db_BuildManifest", report.ManifestPath));
        Console.WriteLine(L.Format("Db_BuildFileHash", report.FileSha256));
        Console.WriteLine(L.Format("Db_BuildContentHash", report.ContentSha256));
        Console.WriteLine(L.Get("Db_BuildStageReport"));
        foreach (var stage in report.Stages)
        {
            var noteSuffix = string.IsNullOrEmpty(stage.Note) ? string.Empty : L.Format("Db_BuildStageNote", stage.Note);
            Console.WriteLine(
                $"  {stage.Stage,-14} {stage.Status,-8} rows={stage.Rows,-8} millis={stage.Millis,-6}{noteSuffix}");
        }

        if (report.TotalIssues == 0)
        {
            Console.WriteLine(L.Get("Db_BuildIssuesZero"));
        }
        else
        {
            Console.WriteLine(L.Format("Db_BuildIssuesCount", report.TotalIssues));
            foreach (var issue in report.Issues)
            {
                Console.WriteLine($"  {issue.Stage,-12} {issue.Kind,-28} {issue.Count}");
            }
        }

        return 0;
    }

    private static int RunHash(string[] args)
    {
        string? dbPath = null;
        var jsonOutput = false;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--help":
                case "-h":
                    PrintHelp();
                    return 0;
                case "--json":
                    jsonOutput = true;
                    break;
                case "--db":
                    dbPath = Program.RequireValue(args, ref i, "--db");
                    break;
                default:
                    if (args[i].StartsWith('-'))
                    {
                        throw new ArgumentException(L.Format("Db_HashUnknownArg", args[i]));
                    }

                    if (dbPath is not null)
                    {
                        throw new ArgumentException(L.Format("Db_HashExtraPositional", args[i]));
                    }

                    dbPath = args[i];
                    break;
            }
        }

        if (string.IsNullOrWhiteSpace(dbPath))
        {
            throw new ArgumentException(L.Get("Db_HashMissingDb"));
        }

        var report = DbHasher.HashDatabase(dbPath);
        if (jsonOutput)
        {
            var json = JsonSerializer.Serialize(report, DbParsers.CompactJsonOptions);
            Console.WriteLine(json);
        }
        else
        {
            Console.WriteLine(L.Format("Db_HashDatabase", dbPath));
            Console.WriteLine(L.Format("Db_HashFormat", report.Format, report.SchemaVersion, report.SqliteVersion));
            Console.WriteLine(L.Format("Db_BuildFileHash", report.FileSha256));
            Console.WriteLine(L.Format("Db_BuildContentHash", report.ContentSha256));
            Console.WriteLine(L.Format("Db_HashTablesHeader", report.Tables.Count));
            foreach (var table in report.Tables)
            {
                Console.WriteLine($"  {table.Name,-28} {table.Rows,8}  {table.Sha256}");
            }
        }

        return 0;
    }

    private static int RunStats(string[] args)
    {
        string? dbPath = null;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--help":
                case "-h":
                    PrintHelp();
                    return 0;
                case "--db":
                    dbPath = Program.RequireValue(args, ref i, "--db");
                    break;
                default:
                    if (args[i].StartsWith('-'))
                    {
                        throw new ArgumentException(L.Format("Db_StatsUnknownArg", args[i]));
                    }

                    if (dbPath is not null)
                    {
                        throw new ArgumentException(L.Format("Db_StatsExtraPositional", args[i]));
                    }

                    dbPath = args[i];
                    break;
            }
        }

        if (string.IsNullOrWhiteSpace(dbPath))
        {
            throw new ArgumentException(L.Get("Db_StatsMissingDb"));
        }

        using var reader = DbReader.Open(dbPath);
        var stats = reader.GetStats();

        Console.WriteLine(L.Format("Db_HashDatabase", stats.Path));
        Console.WriteLine(L.Format("Db_StatsFileSize", stats.FileSizeBytes, FormatBytes(stats.FileSizeBytes), stats.SqliteVersion));
        Console.WriteLine(L.Get("Db_StatsMetaHeader"));
        foreach (var (k, v) in stats.Meta)
        {
            Console.WriteLine($"  {k} = {v}");
        }

        Console.WriteLine(L.Get("Db_StatsAreaSummaryHeader"));
        foreach (var areaGroup in stats.Tables.GroupBy(t => t.Area).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            Console.WriteLine(L.Format("Db_StatsAreaRow", areaGroup.Key, areaGroup.Sum(t => t.Rows), areaGroup.Count()));
        }

        Console.WriteLine(L.Get("Db_StatsTableRowsHeader"));
        foreach (var t in stats.Tables)
        {
            Console.WriteLine($"  {t.Name,-28} {t.Rows,10}");
        }

        if (stats.TotalIssues == 0)
        {
            Console.WriteLine(L.Get("Db_BuildIssuesZero"));
        }
        else
        {
            Console.WriteLine(L.Format("Db_BuildIssuesCount", stats.TotalIssues));
            foreach (var issue in stats.Issues)
            {
                Console.WriteLine($"  {issue.Stage,-12} {issue.Kind,-28} {issue.Count}");
            }
        }

        return 0;
    }

    private static int RunSql(string[] args)
    {
        string? dbPath = null;
        string? sql = null;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--help":
                case "-h":
                    PrintHelp();
                    return 0;
                case "--db":
                    dbPath = Program.RequireValue(args, ref i, "--db");
                    break;
                default:
                    if (args[i].StartsWith('-') && dbPath is null)
                    {
                        throw new ArgumentException(L.Format("Db_SqlUnknownArg", args[i]));
                    }

                    if (dbPath is null)
                    {
                        dbPath = args[i];
                    }
                    else if (sql is null)
                    {
                        sql = args[i];
                    }
                    else
                    {
                        throw new ArgumentException(L.Format("Db_SqlExtraPositional", args[i]));
                    }

                    break;
            }
        }

        if (string.IsNullOrWhiteSpace(dbPath))
        {
            throw new ArgumentException(L.Get("Db_SqlMissingDb"));
        }

        if (string.IsNullOrWhiteSpace(sql))
        {
            throw new ArgumentException(L.Get("Db_SqlMissingQuery"));
        }

        using var reader = DbReader.Open(dbPath);
        var rows = reader.ExecuteReadOnlySql(sql);
        foreach (var row in rows)
        {
            Console.WriteLine(JsonSerializer.Serialize(row, DbParsers.CompactJsonOptions));
        }

        return 0;
    }

    private static int RunWriteFixture(string[] args)
    {
        string? outDir = null;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--out":
                    outDir = Program.RequireValue(args, ref i, "--out");
                    break;
                default:
                    throw new ArgumentException(L.Format("Db_WriteFixtureUnknownArg", args[i]));
            }
        }

        if (string.IsNullOrWhiteSpace(outDir))
        {
            throw new ArgumentException(L.Get("Db_WriteFixtureMissingOut"));
        }

        var fullOut = Path.GetFullPath(outDir);
        if (Directory.Exists(fullOut) && Directory.EnumerateFileSystemEntries(fullOut).Any())
        {
            throw new IOException(L.Format("Db_WriteFixtureDirNotEmpty", outDir));
        }

        SyntheticDbFixture.CreateSyntheticExport(fullOut);
        Console.WriteLine(L.Format("Db_WriteFixtureCreated", fullOut));
        return 0;
    }

    private static string? TryInferDefaultOutPath(string exportDir)
    {
        var manifestPath = Path.Combine(Path.GetFullPath(exportDir), "manifest.json");
        if (!File.Exists(manifestPath))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath, Encoding.UTF8));
            if (doc.RootElement.TryGetProperty("game", out var gameEl)
                && gameEl.ValueKind == JsonValueKind.Object
                && gameEl.TryGetProperty("id", out var idEl))
            {
                var id = idEl.GetString()?.Trim().ToLowerInvariant();
                if (!string.IsNullOrWhiteSpace(id))
                {
                    return OutputGuard.ResolveDefaultDepsPath($"ffpr/{id}/db/{id}.sqlite");
                }
            }
        }
        catch
        {
            // Ignore unreadable manifest and require explicit --out.
        }

        return null;
    }

    private static string FormatBytes(long bytes)
    {
        const double KiB = 1024.0;
        const double MiB = 1024.0 * 1024.0;
        const double GiB = 1024.0 * 1024.0 * 1024.0;
        if (bytes >= GiB)
        {
            return $"{bytes / GiB:F2} GiB";
        }

        if (bytes >= MiB)
        {
            return $"{bytes / MiB:F2} MiB";
        }

        if (bytes >= KiB)
        {
            return $"{bytes / KiB:F2} KiB";
        }

        return $"{bytes} B";
    }

    private static void PrintHelp()
    {
        Console.WriteLine(L.Get("Db_HelpText"));
    }
}
