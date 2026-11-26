using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace FfprExport.Db;

internal sealed partial class DbBuilder
{
    private static long s_tempNonce;

    private readonly DbBuildOptions _options;
    private readonly string _exportRoot;
    private readonly int _jobs;

    private readonly List<DbBuildIssue> _issues = new();
    private readonly List<DbSourceRecord> _sources = new();
    private readonly List<DbStageReport> _stageReports = new();

    // Cross-stage in-memory indices (populated deterministically by earlier stages).
    private readonly Dictionary<string, (string Scope, Dictionary<string, string> PlainByLang)> _textByKey =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, ParsedMasterTable> _masterTables = new(StringComparer.Ordinal);
    private readonly Dictionary<long, ContentSummary> _contentById = new();
    private readonly Dictionary<(long TypeId, long TypeValue), ContentSummary> _contentByTypeValue = new();
    private readonly Dictionary<long, (string Zhc, string Ja, string En)> _monsterNamesById = new();
    private readonly Dictionary<long, (string Zhc, string Ja, string En)> _areaNamesById = new();
    private readonly Dictionary<string, int> _submapIdByName = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _submapIdByLowerName = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _scriptIdByKindAndName = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<long, string> _monsterAiScriptNameByMasterId = new();
    private readonly List<TreasureChestLinkCandidate> _treasureCandidates = new();
    private readonly List<ScriptTextCandidate> _scriptTextCandidates = new();

    private DbBuilder(DbBuildOptions options)
    {
        _options = options;
        _exportRoot = Path.GetFullPath(options.ExportDirectory);
        _jobs = Math.Max(1, options.Jobs);
    }

    public static DbBuildReport Build(DbBuildOptions options)
    {
        return new DbBuilder(options).Execute();
    }

    public static string GetManifestPath(string dbPath)
    {
        return dbPath + ".manifest.json";
    }

    private DbBuildReport Execute()
    {
        if (string.IsNullOrWhiteSpace(_options.ExportDirectory) || !Directory.Exists(_exportRoot))
        {
            throw new DirectoryNotFoundException($"导出目录不存在：{_options.ExportDirectory}");
        }

        if (string.IsNullOrWhiteSpace(_options.OutputPath))
        {
            throw new ArgumentException("缺少 --out <数据库文件路径> 参数");
        }

        var fullOutPath = Path.GetFullPath(_options.OutputPath);
        ValidateOutputDatabasePath(fullOutPath, _options.OutputPath);

        var parentDir = Path.GetDirectoryName(fullOutPath);
        if (string.IsNullOrEmpty(parentDir))
        {
            parentDir = Directory.GetCurrentDirectory();
        }

        Directory.CreateDirectory(parentDir);

        var totalSw = Stopwatch.StartNew();
        var (gameId, steamBuildId, unityVersion) = ReadExportManifestMetadata();

        var nonce = Interlocked.Increment(ref s_tempNonce);
        var fileName = Path.GetFileName(fullOutPath);
        var tempDbPath = Path.Combine(parentDir, $".{fileName}.tmp.{Environment.ProcessId}.{nonce}");
        var tempCleaned = false;

        try
        {
            using (var conn = OpenBuildConnection(tempDbPath))
            {
                DbSchema.ApplyTableSchemas(conn);

                RunStage(conn, "text", ImportTextStage);
                RunStage(conn, "master_raw", ImportMasterRawStage);
                RunStage(conn, "master_typed", ImportMasterTypedStage);
                RunStage(conn, "maps", ImportMapsStage);
                RunStage(conn, "scripts", ImportScriptsStage);
                RunStage(conn, "unity", ImportUnityStage);
                RunStage(conn, "config", ImportConfigStage);
                RunStage(conn, "relations", ImportRelationsStage);

                WriteBuildMetadata(conn, gameId, steamBuildId, unityVersion);

                DbSchema.ApplyIndexSchemas(conn);
                DbSchema.ExecuteNonQuery(conn, "ANALYZE; VACUUM;");
            }

            // Ensure no residual pooled connections hold the temporary file before atomic rename.
            SqliteConnection.ClearAllPools();
            File.Move(tempDbPath, fullOutPath, overwrite: true);
            tempCleaned = true;

            var hashReport = DbHasher.HashDatabase(fullOutPath);
            var fileSize = new FileInfo(fullOutPath).Length;
            totalSw.Stop();

            var issuesSummary = _issues
                .GroupBy(i => (i.Stage, i.Kind))
                .OrderBy(g => g.Key.Stage, StringComparer.Ordinal)
                .ThenBy(g => g.Key.Kind, StringComparer.Ordinal)
                .Select(g => new DbIssueSummary(g.Key.Stage, g.Key.Kind, g.LongCount()))
                .ToArray();
            var totalIssues = _issues.LongCount();

            var sortedSources = _sources
                .OrderBy(s => s.Stage, StringComparer.Ordinal)
                .ThenBy(s => s.Path, StringComparer.Ordinal)
                .ToArray();

            var manifestPath = GetManifestPath(fullOutPath);
            var manifest = new DbBuildManifest(
                Format: DbSchema.ManifestFormatVersion,
                BuiltAt: OutputGuard.FormatUtc(DateTimeOffset.UtcNow),
                GameId: gameId,
                SteamBuildId: steamBuildId,
                UnityVersion: unityVersion,
                Tool: new DbManifestToolInfo(
                    Tool: OutputGuard.ToolName,
                    ToolVersion: Program.ToolVersion,
                    SchemaVersion: DbSchema.SchemaVersion,
                    SqliteVersion: hashReport.SqliteVersion,
                    Jobs: _jobs,
                    TotalMillis: totalSw.ElapsedMilliseconds),
                Sources: sortedSources,
                Stages: _stageReports.ToArray(),
                Issues: issuesSummary,
                TotalIssues: totalIssues,
                Hash: hashReport);

            TextFileWriter.WriteJsonAtomic(
                manifestPath,
                manifest,
                DbParsers.PrettyJsonOptions,
                trailingNewLine: true);

            return new DbBuildReport(
                OutputPath: fullOutPath,
                ManifestPath: manifestPath,
                GameId: gameId,
                SchemaVersion: DbSchema.SchemaVersion,
                SqliteVersion: hashReport.SqliteVersion,
                FileSizeBytes: fileSize,
                FileSha256: hashReport.FileSha256,
                ContentSha256: hashReport.ContentSha256,
                TotalMillis: totalSw.ElapsedMilliseconds,
                Stages: _stageReports.ToArray(),
                Issues: issuesSummary,
                TotalIssues: totalIssues);
        }
        finally
        {
            if (!tempCleaned)
            {
                TryDeleteFile(tempDbPath);
                TryDeleteFile(tempDbPath + "-journal");
                TryDeleteFile(tempDbPath + "-wal");
                TryDeleteFile(tempDbPath + "-shm");
            }
        }
    }

    private void RunStage(
        SqliteConnection conn,
        string stageName,
        Func<SqliteConnection, SqliteTransaction, StageResult> action)
    {
        var sw = Stopwatch.StartNew();
        using var tx = conn.BeginTransaction();
        try
        {
            var result = action(conn, tx);
            tx.Commit();
            sw.Stop();
            _stageReports.Add(new DbStageReport(
                stageName,
                result.Status,
                result.Rows,
                sw.ElapsedMilliseconds,
                result.Note));
        }
        catch (Exception ex)
        {
            try
            {
                tx.Rollback();
            }
            catch
            {
                // Ignore rollback failure when propagating original stage exception.
            }

            throw new InvalidOperationException($"构建阶段 {stageName} 失败：{ex.Message}", ex);
        }
    }

    private void WriteBuildMetadata(
        SqliteConnection conn,
        string gameId,
        string? steamBuildId,
        string? unityVersion)
    {
        using var tx = conn.BeginTransaction();

        // 1. Insert sorted build issues with deterministic sequential IDs.
        var sortedIssues = _issues
            .OrderBy(i => i.Stage, StringComparer.Ordinal)
            .ThenBy(i => i.Kind, StringComparer.Ordinal)
            .ThenBy(i => i.Subject, StringComparer.Ordinal)
            .ThenBy(i => i.Detail, StringComparer.Ordinal)
            .ToList();
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText =
                "INSERT INTO build_issue (id, stage, kind, subject, detail) " +
                "VALUES ($id, $stage, $kind, $subject, $detail)";
            var pId = cmd.Parameters.Add("$id", SqliteType.Integer);
            var pStage = cmd.Parameters.Add("$stage", SqliteType.Text);
            var pKind = cmd.Parameters.Add("$kind", SqliteType.Text);
            var pSubject = cmd.Parameters.Add("$subject", SqliteType.Text);
            var pDetail = cmd.Parameters.Add("$detail", SqliteType.Text);

            for (var i = 0; i < sortedIssues.Count; i++)
            {
                var issue = sortedIssues[i];
                pId.Value = i + 1;
                pStage.Value = issue.Stage;
                pKind.Value = issue.Kind;
                pSubject.Value = issue.Subject;
                pDetail.Value = issue.Detail;
                cmd.ExecuteNonQuery();
            }
        }

        // 2. Insert sorted source records (relative logical paths & deterministic hashes only).
        var sortedSources = _sources
            .OrderBy(s => s.Stage, StringComparer.Ordinal)
            .ThenBy(s => s.Path, StringComparer.Ordinal)
            .ToList();
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText =
                "INSERT OR REPLACE INTO source (stage, path, sha256, file_count) " +
                "VALUES ($stage, $path, $sha, $cnt)";
            var pStage = cmd.Parameters.Add("$stage", SqliteType.Text);
            var pPath = cmd.Parameters.Add("$path", SqliteType.Text);
            var pSha = cmd.Parameters.Add("$sha", SqliteType.Text);
            var pCnt = cmd.Parameters.Add("$cnt", SqliteType.Integer);

            foreach (var src in sortedSources)
            {
                pStage.Value = src.Stage;
                pPath.Value = src.Path;
                pSha.Value = src.Sha256;
                pCnt.Value = src.FileCount;
                cmd.ExecuteNonQuery();
            }
        }

        // 3. Insert sorted stage reports (excluding volatile timings).
        var sortedStages = _stageReports
            .OrderBy(s => s.Stage, StringComparer.Ordinal)
            .ToList();
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText =
                "INSERT OR REPLACE INTO stage_report (stage, status, rows, note) " +
                "VALUES ($stage, $status, $rows, $note)";
            var pStage = cmd.Parameters.Add("$stage", SqliteType.Text);
            var pStatus = cmd.Parameters.Add("$status", SqliteType.Text);
            var pRows = cmd.Parameters.Add("$rows", SqliteType.Integer);
            var pNote = cmd.Parameters.Add("$note", SqliteType.Text);

            foreach (var st in sortedStages)
            {
                pStage.Value = st.Stage;
                pStatus.Value = st.Status;
                pRows.Value = st.Rows;
                pNote.Value = st.Note;
                cmd.ExecuteNonQuery();
            }
        }

        // 4. Insert deterministic meta entries (no timestamps or paths).
        var metaEntries = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            [DbSchema.MetaKeyGameId] = gameId,
            [DbSchema.MetaKeySchemaVersion] = DbSchema.SchemaVersion.ToString(CultureInfo.InvariantCulture),
            [DbSchema.MetaKeyToolVersion] = Program.ToolVersion,
        };
        if (!string.IsNullOrWhiteSpace(steamBuildId))
        {
            metaEntries[DbSchema.MetaKeySteamBuildId] = steamBuildId;
        }

        if (!string.IsNullOrWhiteSpace(unityVersion))
        {
            metaEntries[DbSchema.MetaKeyUnityVersion] = unityVersion;
        }

        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT OR REPLACE INTO meta (key, value) VALUES ($key, $val)";
            var pKey = cmd.Parameters.Add("$key", SqliteType.Text);
            var pVal = cmd.Parameters.Add("$val", SqliteType.Text);
            foreach (var (k, v) in metaEntries)
            {
                pKey.Value = k;
                pVal.Value = v;
                cmd.ExecuteNonQuery();
            }
        }

        tx.Commit();
    }

    private (string GameId, string? SteamBuildId, string? UnityVersion) ReadExportManifestMetadata()
    {
        var manifestPath = Path.Combine(_exportRoot, "manifest.json");
        if (File.Exists(manifestPath))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath, Encoding.UTF8));
                var root = doc.RootElement;
                string? gameId = null;
                if (root.TryGetProperty("game", out var gameEl)
                    && gameEl.ValueKind == JsonValueKind.Object
                    && gameEl.TryGetProperty("id", out var idEl))
                {
                    gameId = idEl.GetString()?.Trim();
                }

                var buildId = root.TryGetProperty("steamBuildId", out var bidEl) && bidEl.ValueKind == JsonValueKind.String
                    ? bidEl.GetString()?.Trim()
                    : null;
                var unityVer = root.TryGetProperty("unityVersion", out var uvEl) && uvEl.ValueKind == JsonValueKind.String
                    ? uvEl.GetString()?.Trim()
                    : null;

                if (!string.IsNullOrWhiteSpace(gameId))
                {
                    return (gameId.ToLowerInvariant(), buildId, unityVer);
                }
            }
            catch (Exception ex)
            {
                RecordIssue("meta", "invalid_export_manifest", "manifest.json", OutputGuard.TruncateError(ex.Message));
            }
        }

        return ("unknown", null, null);
    }

    private void RecordIssue(string stage, string kind, string subject, string detail)
    {
        _issues.Add(new DbBuildIssue(stage, kind, subject, detail));
    }

    private void RecordSource(string stage, string relativePath, IEnumerable<(string RelativePath, string FileSha256)> files)
    {
        var list = files.ToList();
        var sha = DbParsers.ComputeSourceSetSha256(list);
        _sources.Add(new DbSourceRecord(stage, relativePath, sha, list.Count));
    }

    private (string Zhc, string Ja, string En) ResolveMessageNames(string key)
    {
        if (string.IsNullOrEmpty(key) || !_textByKey.TryGetValue(key, out var entry))
        {
            return (string.Empty, string.Empty, string.Empty);
        }

        entry.PlainByLang.TryGetValue("zhc", out var zhc);
        entry.PlainByLang.TryGetValue("ja", out var ja);
        entry.PlainByLang.TryGetValue("en", out var en);
        return (zhc ?? string.Empty, ja ?? string.Empty, en ?? string.Empty);
    }

    private static SqliteConnection OpenBuildConnection(string dbPath)
    {
        var csb = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        };
        var conn = new SqliteConnection(csb.ToString());
        conn.Open();
        DbSchema.ApplyBuildPragmas(conn);
        return conn;
    }

    private static void ValidateOutputDatabasePath(string fullOutPath, string outputArgument)
    {
        if (Directory.Exists(fullOutPath))
        {
            throw new ArgumentException($"--out 必须是文件路径，不能指向已有目录：{outputArgument}");
        }

        var (depsFull, depsDisplay, _) = OutputGuard.ResolveDepsRoot();
        string[] protectedSubpaths = ["dotnet", "nuget", "dotnet-home", "nuget-http", "steamapps", "ffpr-legacy"];
        foreach (var sub in protectedSubpaths)
        {
            var protectedFull = Path.GetFullPath(Path.Combine(depsFull, sub));
            var rel = Path.GetRelativePath(protectedFull, fullOutPath);
            if (rel == "."
                || (rel != ".."
                    && !rel.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                    && !rel.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal)
                    && !Path.IsPathRooted(rel)))
            {
                throw new ArgumentException($"--out 不能写入受保护目录 {depsDisplay}/{sub}：{outputArgument}");
            }
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Best-effort cleanup for temporary database artifacts.
        }
    }

    private static List<TOut> ParseParallelOrdered<TIn, TOut>(
        IReadOnlyList<TIn> items,
        int jobs,
        Func<TIn, TOut> parser)
    {
        if (items.Count == 0)
        {
            return new List<TOut>();
        }

        if (jobs <= 1 || items.Count <= 4)
        {
            var sequential = new List<TOut>(items.Count);
            foreach (var item in items)
            {
                sequential.Add(parser(item));
            }

            return sequential;
        }

        var results = new TOut[items.Count];
        Parallel.For(
            0,
            items.Count,
            new ParallelOptions { MaxDegreeOfParallelism = jobs },
            i => results[i] = parser(items[i]));
        return new List<TOut>(results);
    }

    private sealed record StageResult(string Status, long Rows, string Note)
    {
        public static StageResult Ok(long rows, string note = "") => new("ok", rows, note);
        public static StageResult Skipped(string reason) => new("skipped", 0, reason);
    }

    private sealed record ParsedMasterTable(
        string Name,
        IReadOnlyList<string> RawHeaders,
        IReadOnlyList<string> CanonicalHeaders,
        Dictionary<string, int> CanonicalIndex,
        IReadOnlyList<ParsedMasterRow> Rows);

    private sealed record ParsedMasterRow(
        string RowKey,
        int RowOrd,
        long? NumericId,
        string[] Cells,
        string RawDocJson);

    private sealed record ContentSummary(
        long Id,
        string MesIdName,
        string NameZhc,
        string NameJa,
        string NameEn,
        long TypeId,
        long TypeValue);

    private sealed record TreasureChestLinkCandidate(
        int TreasureChestId,
        int SubmapId,
        string SubmapName,
        string SourceFile,
        int TiledId,
        long? ContentId,
        int ContentNum);

    private sealed record ScriptTextCandidate(
        int ScriptId,
        string ScriptPath,
        int InstructionOrd,
        string Mnemonic,
        int OperandIndex,
        string RawValue,
        bool ExplicitMessageMnemonic);
}
