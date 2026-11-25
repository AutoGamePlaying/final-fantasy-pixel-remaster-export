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
            _ => throw new ArgumentException($"未知 db 子命令：{sub}；可用子命令：build、hash、stats、sql"),
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
                        throw new ArgumentException($"--jobs 必须是正整数：{rawJobs}");
                    }

                    break;
                default:
                    throw new ArgumentException($"db build 未知参数：{args[i]}");
            }
        }

        if (string.IsNullOrWhiteSpace(exportDir))
        {
            throw new ArgumentException("db build 缺少 --export <导出目录> 参数");
        }

        if (string.IsNullOrWhiteSpace(outPath))
        {
            outPath = TryInferDefaultOutPath(exportDir)
                ?? throw new ArgumentException("db build 缺少 --out <数据库文件路径> 参数");
        }

        var report = DbBuilder.Build(new DbBuildOptions(exportDir, outPath, jobs));

        Console.WriteLine(
            $"构建完成：{report.OutputPath}（游戏 {report.GameId}，schema v{report.SchemaVersion}，SQLite {report.SqliteVersion}，{FormatBytes(report.FileSizeBytes)}，耗时 {report.TotalMillis} ms）");
        Console.WriteLine($"清单文件：{report.ManifestPath}");
        Console.WriteLine($"文件哈希（file_sha256）：{report.FileSha256}");
        Console.WriteLine($"内容哈希（content_sha256）：{report.ContentSha256}");
        Console.WriteLine("阶段报告：");
        foreach (var stage in report.Stages)
        {
            var noteSuffix = string.IsNullOrEmpty(stage.Note) ? string.Empty : $"  说明={stage.Note}";
            Console.WriteLine(
                $"  {stage.Stage,-14} {stage.Status,-8} rows={stage.Rows,-8} millis={stage.Millis,-6}{noteSuffix}");
        }

        if (report.TotalIssues == 0)
        {
            Console.WriteLine("构建问题（build_issue）：0 条");
        }
        else
        {
            Console.WriteLine($"构建问题（build_issue）：共 {report.TotalIssues} 条");
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
                        throw new ArgumentException($"db hash 未知参数：{args[i]}");
                    }

                    if (dbPath is not null)
                    {
                        throw new ArgumentException($"db hash 多余的位置参数：{args[i]}");
                    }

                    dbPath = args[i];
                    break;
            }
        }

        if (string.IsNullOrWhiteSpace(dbPath))
        {
            throw new ArgumentException("db hash 缺少 <库> 路径参数");
        }

        var report = DbHasher.HashDatabase(dbPath);
        if (jsonOutput)
        {
            var json = JsonSerializer.Serialize(report, DbParsers.CompactJsonOptions);
            Console.WriteLine(json);
        }
        else
        {
            Console.WriteLine($"数据库：{dbPath}");
            Console.WriteLine($"格式版本：v{report.Format}（schema v{report.SchemaVersion}，SQLite {report.SqliteVersion}）");
            Console.WriteLine($"文件哈希（file_sha256）：{report.FileSha256}");
            Console.WriteLine($"内容哈希（content_sha256）：{report.ContentSha256}");
            Console.WriteLine($"普通表哈希（共 {report.Tables.Count} 张）：");
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
                        throw new ArgumentException($"db stats 未知参数：{args[i]}");
                    }

                    if (dbPath is not null)
                    {
                        throw new ArgumentException($"db stats 多余的位置参数：{args[i]}");
                    }

                    dbPath = args[i];
                    break;
            }
        }

        if (string.IsNullOrWhiteSpace(dbPath))
        {
            throw new ArgumentException("db stats 缺少 <库> 路径参数");
        }

        using var reader = DbReader.Open(dbPath);
        var stats = reader.GetStats();

        Console.WriteLine($"数据库：{stats.Path}");
        Console.WriteLine($"文件大小：{stats.FileSizeBytes} 字节（{FormatBytes(stats.FileSizeBytes)}），SQLite {stats.SqliteVersion}");
        Console.WriteLine("元信息（meta）：");
        foreach (var (k, v) in stats.Meta)
        {
            Console.WriteLine($"  {k} = {v}");
        }

        Console.WriteLine("区域行数汇总：");
        foreach (var areaGroup in stats.Tables.GroupBy(t => t.Area).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            Console.WriteLine($"  {areaGroup.Key,-14} {areaGroup.Sum(t => t.Rows),10}（{areaGroup.Count()} 张表）");
        }

        Console.WriteLine("数据表行数：");
        foreach (var t in stats.Tables)
        {
            Console.WriteLine($"  {t.Name,-28} {t.Rows,10}");
        }

        if (stats.TotalIssues == 0)
        {
            Console.WriteLine("构建问题（build_issue）：0 条");
        }
        else
        {
            Console.WriteLine($"构建问题（build_issue）：共 {stats.TotalIssues} 条");
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
                        throw new ArgumentException($"db sql 未知参数：{args[i]}");
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
                        throw new ArgumentException($"db sql 多余的位置参数：{args[i]}");
                    }

                    break;
            }
        }

        if (string.IsNullOrWhiteSpace(dbPath))
        {
            throw new ArgumentException("db sql 缺少 <库> 路径参数");
        }

        if (string.IsNullOrWhiteSpace(sql))
        {
            throw new ArgumentException("db sql 缺少只读 SQL 查询语句参数");
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
                    throw new ArgumentException($"db write-fixture 未知参数：{args[i]}");
            }
        }

        if (string.IsNullOrWhiteSpace(outDir))
        {
            throw new ArgumentException("db write-fixture 缺少 --out <目录> 参数");
        }

        var fullOut = Path.GetFullPath(outDir);
        if (Directory.Exists(fullOut) && Directory.EnumerateFileSystemEntries(fullOut).Any())
        {
            throw new IOException($"合成夹具输出目录非空：{outDir}");
        }

        SyntheticDbFixture.CreateSyntheticExport(fullOut);
        Console.WriteLine($"已生成合成导出目录：{fullOut}");
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
        Console.WriteLine(
            """
            ffpr-export db — FF1–6 Pixel Remaster SQLite 数据库构建与只读查询工具

            用法：
              ffpr-export db build --export <.deps/ffpr/ffN/export> --out <ffN.sqlite> [--jobs N]
              ffpr-export db hash  <库> [--json]
              ffpr-export db stats <库>
              ffpr-export db sql   <库> "<SELECT …>"
            """);
    }
}
