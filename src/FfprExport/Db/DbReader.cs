using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;

namespace FfprExport.Db;

internal sealed class DbReader : IDisposable
{
    private static readonly HashSet<string> AllowedFirstKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "SELECT",
        "WITH",
        "EXPLAIN",
        "VALUES",
    };

    private static readonly HashSet<string> ForbiddenKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "INSERT",
        "UPDATE",
        "DELETE",
        "REPLACE",
        "DROP",
        "ALTER",
        "CREATE",
        "ATTACH",
        "DETACH",
        "VACUUM",
        "REINDEX",
        "ANALYZE",
        "PRAGMA",
        "BEGIN",
        "COMMIT",
        "ROLLBACK",
        "SAVEPOINT",
        "RELEASE",
    };

    private readonly SqliteConnection _connection;

    public string DatabasePath { get; }

    private DbReader(string databasePath, SqliteConnection connection)
    {
        DatabasePath = databasePath;
        _connection = connection;
    }

    public static DbReader Open(string databasePath, bool verifySchemaVersion = true)
    {
        if (string.IsNullOrWhiteSpace(databasePath) || !File.Exists(databasePath))
        {
            throw new FileNotFoundException($"数据库文件不存在：{databasePath}", databasePath);
        }

        var fullPath = Path.GetFullPath(databasePath);
        var csb = new SqliteConnectionStringBuilder
        {
            DataSource = fullPath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        };

        var conn = new SqliteConnection(csb.ToString());
        try
        {
            conn.Open();
            DbSchema.ExecuteNonQuery(
                conn,
                """
                PRAGMA query_only = ON;
                PRAGMA mmap_size = 268435456;
                PRAGMA cache_size = -65536;
                PRAGMA temp_store = MEMORY;
                PRAGMA busy_timeout = 5000;
                """);

            if (verifySchemaVersion)
            {
                var actualVersion = DbHasher.QuerySchemaVersion(conn);
                if (actualVersion != DbSchema.SchemaVersion)
                {
                    throw new InvalidDataException(
                        $"数据库表结构版本不匹配（文件：{databasePath}，期望 v{DbSchema.SchemaVersion}，实际 v{actualVersion}），请重新构建数据库");
                }
            }

            return new DbReader(fullPath, conn);
        }
        catch
        {
            conn.Dispose();
            throw;
        }
    }

    public SqliteConnection Connection => _connection;

    public DbStatsReport GetStats()
    {
        var fileSize = new FileInfo(DatabasePath).Length;
        var sqliteVersion = DbHasher.QuerySqliteVersion(_connection);

        var meta = new SortedDictionary<string, string>(StringComparer.Ordinal);
        using (var cmd = _connection.CreateCommand())
        {
            cmd.CommandText = "SELECT key, value FROM meta ORDER BY key";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                meta[reader.GetString(0)] = reader.GetString(1);
            }
        }

        var stages = new List<DbStageReport>();
        using (var cmd = _connection.CreateCommand())
        {
            cmd.CommandText = "SELECT stage, status, rows, note FROM stage_report ORDER BY stage";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                stages.Add(new DbStageReport(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetInt64(2),
                    0,
                    reader.GetString(3)));
            }
        }

        var issues = new List<DbIssueSummary>();
        long totalIssues = 0;
        using (var cmd = _connection.CreateCommand())
        {
            cmd.CommandText = "SELECT stage, kind, COUNT(*) FROM build_issue GROUP BY stage, kind ORDER BY stage, kind";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var count = reader.GetInt64(2);
                totalIssues += count;
                issues.Add(new DbIssueSummary(reader.GetString(0), reader.GetString(1), count));
            }
        }

        var ordinaryTables = DbHasher.ListOrdinaryTables(_connection);
        var tableCounts = new List<DbTableCount>(ordinaryTables.Count);
        foreach (var tableName in ordinaryTables)
        {
            using var countCmd = _connection.CreateCommand();
            countCmd.CommandText = $"SELECT COUNT(*) FROM {DbHasher.QuoteIdent(tableName)}";
            var rows = Convert.ToInt64(countCmd.ExecuteScalar(), CultureInfo.InvariantCulture);
            var area = DbSchema.TableAreas.TryGetValue(tableName, out var knownArea) ? knownArea : "other";
            tableCounts.Add(new DbTableCount(tableName, area, rows));
        }

        return new DbStatsReport(
            DatabasePath,
            fileSize,
            sqliteVersion,
            meta,
            stages,
            issues,
            totalIssues,
            tableCounts);
    }

    public static void ValidateReadOnlySql(string sql)
    {
        if (string.IsNullOrWhiteSpace(sql))
        {
            throw new ArgumentException("SQL 查询语句不能为空");
        }

        var tokens = TokenizeSqlWordsAndSemicolons(sql);
        if (tokens.Count == 0)
        {
            throw new ArgumentException("SQL 查询语句不包含可执行内容");
        }

        string? firstWord = null;
        var seenSemicolon = false;
        foreach (var token in tokens)
        {
            if (token == ";")
            {
                seenSemicolon = true;
                continue;
            }

            if (seenSemicolon)
            {
                throw new ArgumentException("只读查询仅允许单条 SQL 语句，拒绝多语句输入");
            }

            firstWord ??= token;

            if (ForbiddenKeywords.Contains(token))
            {
                throw new ArgumentException($"拒绝非只读 SQL 语句：包含不允许的关键字 {token.ToUpperInvariant()}");
            }
        }

        if (firstWord is null || !AllowedFirstKeywords.Contains(firstWord))
        {
            throw new ArgumentException(
                $"只读查询仅允许 SELECT / WITH / EXPLAIN / VALUES 语句，收到：{firstWord ?? "<空>"}");
        }
    }

    public IReadOnlyList<Dictionary<string, object?>> ExecuteReadOnlySql(string sql)
    {
        ValidateReadOnlySql(sql);

        var rows = new List<Dictionary<string, object?>>();
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        using var reader = cmd.ExecuteReader();
        var colCount = reader.FieldCount;
        var colNames = new string[colCount];
        for (var i = 0; i < colCount; i++)
        {
            colNames[i] = reader.GetName(i);
        }

        while (reader.Read())
        {
            var obj = new Dictionary<string, object?>(colCount, StringComparer.Ordinal);
            for (var i = 0; i < colCount; i++)
            {
                object? val = reader.IsDBNull(i) ? null : reader.GetValue(i);
                if (val is byte[] bytes)
                {
                    val = Convert.ToBase64String(bytes);
                }

                obj[colNames[i]] = val;
            }

            rows.Add(obj);
        }

        return rows;
    }

    public IReadOnlyList<QueryPlanStep> ExplainQueryPlan(
        string sql,
        params (string Name, object? Value)[] parameters)
    {
        ValidateReadOnlySql(sql);

        var steps = new List<QueryPlanStep>();
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "EXPLAIN QUERY PLAN " + sql;
        foreach (var (name, value) in parameters)
        {
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            steps.Add(new QueryPlanStep(
                reader.GetInt32(0),
                reader.GetInt32(1),
                reader.GetString(3)));
        }

        return steps;
    }

    public MapGridCellResult? QueryMapGridCell(
        string submapName,
        string layerKind,
        int x,
        int y,
        string? layerName = null)
    {
        using var cmd = _connection.CreateCommand();
        if (layerName is null)
        {
            cmd.CommandText =
                """
                SELECT s.id, s.name, g.layer_kind, g.layer_name, g.width, g.height, g.encoding, g.data
                FROM map_submap s
                JOIN map_grid g ON g.submap_id = s.id
                WHERE s.name = $submap AND g.layer_kind = $kind
                ORDER BY g.layer_name
                LIMIT 1
                """;
        }
        else
        {
            cmd.CommandText =
                """
                SELECT s.id, s.name, g.layer_kind, g.layer_name, g.width, g.height, g.encoding, g.data
                FROM map_submap s
                JOIN map_grid g ON g.submap_id = s.id
                WHERE s.name = $submap AND g.layer_kind = $kind AND g.layer_name = $layer
                """;
            cmd.Parameters.AddWithValue("$layer", layerName);
        }

        cmd.Parameters.AddWithValue("$submap", submapName);
        cmd.Parameters.AddWithValue("$kind", layerKind);

        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        return ReadGridCellFromReader(reader, x, y);
    }

    public IReadOnlyList<MapGridCellResult> QueryAllMapGridCellsAt(string submapName, int x, int y)
    {
        var results = new List<MapGridCellResult>();
        using var cmd = _connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT s.id, s.name, g.layer_kind, g.layer_name, g.width, g.height, g.encoding, g.data
            FROM map_submap s
            JOIN map_grid g ON g.submap_id = s.id
            WHERE s.name = $submap
            ORDER BY g.layer_kind, g.layer_name
            """;
        cmd.Parameters.AddWithValue("$submap", submapName);

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var width = reader.GetInt32(4);
            var height = reader.GetInt32(5);
            if (x >= 0 && x < width && y >= 0 && y < height)
            {
                results.Add(ReadGridCellFromReader(reader, x, y));
            }
        }

        return results;
    }

    public IReadOnlyList<TextSearchHit> SearchTextFts(string query, string? lang = null, int limit = 20)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return Array.Empty<TextSearchHit>();
        }

        var escaped = "\"" + query.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
        var hits = new List<TextSearchHit>();
        using var cmd = _connection.CreateCommand();
        if (string.IsNullOrWhiteSpace(lang))
        {
            cmd.CommandText =
                """
                SELECT scope, key, lang, value_plain
                FROM text_fts
                WHERE value_plain MATCH $q
                LIMIT $limit
                """;
        }
        else
        {
            cmd.CommandText =
                """
                SELECT scope, key, lang, value_plain
                FROM text_fts
                WHERE value_plain MATCH $q AND lang = $lang
                LIMIT $limit
                """;
            cmd.Parameters.AddWithValue("$lang", lang);
        }

        cmd.Parameters.AddWithValue("$q", escaped);
        cmd.Parameters.AddWithValue("$limit", Math.Max(1, limit));

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            hits.Add(new TextSearchHit(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3)));
        }

        return hits;
    }

    private static MapGridCellResult ReadGridCellFromReader(SqliteDataReader reader, int x, int y)
    {
        var submapId = reader.GetInt32(0);
        var submapName = reader.GetString(1);
        var layerKind = reader.GetString(2);
        var layerName = reader.GetString(3);
        var width = reader.GetInt32(4);
        var height = reader.GetInt32(5);
        var encoding = reader.GetString(6);
        if (!string.Equals(encoding, "i32le", StringComparison.Ordinal))
        {
            throw new InvalidDataException($"不支持的地图格子编码：{encoding}");
        }

        var blob = (byte[])reader.GetValue(7);
        var value = DbParsers.ReadInt32GridCell(blob, width, height, x, y);
        return new MapGridCellResult(submapId, submapName, layerKind, layerName, x, y, value);
    }

    private static List<string> TokenizeSqlWordsAndSemicolons(string sql)
    {
        var tokens = new List<string>();
        var i = 0;
        while (i < sql.Length)
        {
            var ch = sql[i];
            if (char.IsWhiteSpace(ch))
            {
                i++;
                continue;
            }

            // Line comment: -- ...
            if (ch == '-' && i + 1 < sql.Length && sql[i + 1] == '-')
            {
                i += 2;
                while (i < sql.Length && sql[i] != '\n')
                {
                    i++;
                }

                continue;
            }

            // Block comment: /* ... */
            if (ch == '/' && i + 1 < sql.Length && sql[i + 1] == '*')
            {
                i += 2;
                while (i + 1 < sql.Length && !(sql[i] == '*' && sql[i + 1] == '/'))
                {
                    i++;
                }

                i = Math.Min(sql.Length, i + 2);
                continue;
            }

            // String or quoted identifier: '...', "...", `...`
            if (ch is '\'' or '"' or '`')
            {
                var quote = ch;
                i++;
                while (i < sql.Length)
                {
                    if (sql[i] == quote)
                    {
                        if (i + 1 < sql.Length && sql[i + 1] == quote)
                        {
                            i += 2;
                            continue;
                        }

                        i++;
                        break;
                    }

                    i++;
                }

                continue;
            }

            // Bracket-quoted identifier: [...]
            if (ch == '[')
            {
                i++;
                while (i < sql.Length && sql[i] != ']')
                {
                    i++;
                }

                if (i < sql.Length)
                {
                    i++;
                }

                continue;
            }

            if (ch == ';')
            {
                tokens.Add(";");
                i++;
                continue;
            }

            if (char.IsLetter(ch) || ch == '_')
            {
                var sb = new StringBuilder();
                while (i < sql.Length && (char.IsLetterOrDigit(sql[i]) || sql[i] == '_'))
                {
                    sb.Append(sql[i]);
                    i++;
                }

                tokens.Add(sb.ToString());
                continue;
            }

            i++;
        }

        return tokens;
    }

    public void Dispose()
    {
        _connection.Dispose();
    }
}
