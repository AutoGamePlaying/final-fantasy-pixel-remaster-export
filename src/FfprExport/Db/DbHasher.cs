using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace FfprExport.Db;

internal static class DbHasher
{
    public const byte ValueTagNull = 0x00;
    public const byte ValueTagInteger = 0x01;
    public const byte ValueTagReal = 0x02;
    public const byte ValueTagText = 0x03;
    public const byte ValueTagBlob = 0x04;

    private static readonly string[] VirtualShadowSuffixes =
    [
        "_data",
        "_idx",
        "_content",
        "_docsize",
        "_config",
        "_node",
        "_rowid",
        "_parent",
        "_segments",
        "_segdir",
        "_stat",
    ];

    public static DbHashReport HashDatabase(string dbPath)
    {
        if (string.IsNullOrWhiteSpace(dbPath) || !File.Exists(dbPath))
        {
            throw new FileNotFoundException($"数据库文件不存在：{dbPath}", dbPath);
        }

        var fileSha256 = DbParsers.ComputeFileSha256(dbPath);
        using var conn = OpenReadOnlyHashConnection(dbPath);
        var content = HashConnectionContent(conn);

        return new DbHashReport(
            Format: DbSchema.HashFormatVersion,
            FileSha256: fileSha256,
            ContentSha256: content.ContentSha256,
            SchemaVersion: content.SchemaVersion,
            SqliteVersion: content.SqliteVersion,
            Tables: content.Tables);
    }

    public static DbContentHashReport HashConnectionContent(SqliteConnection conn)
    {
        var sqliteVersion = QuerySqliteVersion(conn);
        var schemaVersion = QuerySchemaVersion(conn);
        var tableNames = ListOrdinaryTables(conn);

        var tables = new List<TableHash>(tableNames.Count);
        foreach (var tableName in tableNames)
        {
            tables.Add(HashTable(conn, tableName));
        }

        using var overall = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendUInt64Le(overall, (ulong)tables.Count);
        foreach (var table in tables)
        {
            DbParsers.AppendLengthPrefixedUtf8(overall, table.Name);
            DbParsers.AppendLengthPrefixedUtf8(overall, table.Sha256);
        }

        var contentSha256 = Convert.ToHexStringLower(overall.GetHashAndReset());
        return new DbContentHashReport(contentSha256, schemaVersion, sqliteVersion, tables);
    }

    public static string QuerySqliteVersion(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT sqlite_version()";
        return Convert.ToString(cmd.ExecuteScalar(), CultureInfo.InvariantCulture)
            ?? throw new InvalidDataException("查询 sqlite_version() 返回空值");
    }

    public static IReadOnlyList<string> ListOrdinaryTables(SqliteConnection conn)
    {
        var rawTables = new List<(string Name, string Sql)>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText =
                "SELECT name, COALESCE(sql, '') FROM sqlite_master " +
                "WHERE type = 'table' AND name NOT LIKE 'sqlite_%' " +
                "ORDER BY name";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                rawTables.Add((reader.GetString(0), reader.GetString(1)));
            }
        }

        var excluded = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (name, sql) in rawTables)
        {
            if (sql.TrimStart().StartsWith("CREATE VIRTUAL TABLE", StringComparison.OrdinalIgnoreCase))
            {
                excluded.Add(name);
                foreach (var suffix in VirtualShadowSuffixes)
                {
                    excluded.Add(name + suffix);
                }
            }
        }

        // Also consult PRAGMA table_list so any virtual, shadow, or view table is excluded.
        try
        {
            using var tlCmd = conn.CreateCommand();
            tlCmd.CommandText = "PRAGMA table_list";
            using var tlReader = tlCmd.ExecuteReader();
            while (tlReader.Read())
            {
                var schema = tlReader.IsDBNull(0) ? string.Empty : tlReader.GetString(0);
                var name = tlReader.IsDBNull(1) ? string.Empty : tlReader.GetString(1);
                var kind = tlReader.IsDBNull(2) ? string.Empty : tlReader.GetString(2);
                if (string.Equals(schema, "main", StringComparison.Ordinal)
                    && kind is "virtual" or "shadow" or "view")
                {
                    excluded.Add(name);
                }
            }
        }
        catch (SqliteException)
        {
            // Older SQLite builds without PRAGMA table_list fall back to DDL-based filtering above.
        }

        return rawTables
            .Select(t => t.Name)
            .Where(name => !excluded.Contains(name))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
    }

    public static byte[] EncodeCanonicalValue(object? value)
    {
        using var ms = new MemoryStream();
        WriteCanonicalValueToStream(ms, value);
        return ms.ToArray();
    }

    internal static void AppendCanonicalValue(IncrementalHash hasher, object? value)
    {
        if (value is null or DBNull)
        {
            Span<byte> tag = [ValueTagNull];
            hasher.AppendData(tag);
            return;
        }

        switch (value)
        {
            case long i64:
                {
                    Span<byte> buf = stackalloc byte[1 + 8 + 8];
                    buf[0] = ValueTagInteger;
                    BinaryPrimitives.WriteUInt64LittleEndian(buf.Slice(1, 8), 8UL);
                    BinaryPrimitives.WriteInt64LittleEndian(buf.Slice(9, 8), i64);
                    hasher.AppendData(buf);
                    return;
                }
            case int i32:
                {
                    AppendCanonicalValue(hasher, (long)i32);
                    return;
                }
            case double f64:
                {
                    Span<byte> buf = stackalloc byte[1 + 8 + 8];
                    buf[0] = ValueTagReal;
                    BinaryPrimitives.WriteUInt64LittleEndian(buf.Slice(1, 8), 8UL);
                    BinaryPrimitives.WriteUInt64LittleEndian(buf.Slice(9, 8), BitConverter.DoubleToUInt64Bits(f64));
                    hasher.AppendData(buf);
                    return;
                }
            case string text:
                {
                    var bytes = Encoding.UTF8.GetBytes(text);
                    Span<byte> header = stackalloc byte[1 + 8];
                    header[0] = ValueTagText;
                    BinaryPrimitives.WriteUInt64LittleEndian(header.Slice(1, 8), (ulong)bytes.Length);
                    hasher.AppendData(header);
                    hasher.AppendData(bytes);
                    return;
                }
            case byte[] blob:
                {
                    Span<byte> header = stackalloc byte[1 + 8];
                    header[0] = ValueTagBlob;
                    BinaryPrimitives.WriteUInt64LittleEndian(header.Slice(1, 8), (ulong)blob.Length);
                    hasher.AppendData(header);
                    hasher.AppendData(blob);
                    return;
                }
            default:
                throw new InvalidDataException($"规范化内容哈希不支持的 SQLite 值类型：{value.GetType().FullName}");
        }
    }

    private static void WriteCanonicalValueToStream(Stream stream, object? value)
    {
        if (value is null or DBNull)
        {
            stream.WriteByte(ValueTagNull);
            return;
        }

        switch (value)
        {
            case long i64:
                {
                    Span<byte> buf = stackalloc byte[1 + 8 + 8];
                    buf[0] = ValueTagInteger;
                    BinaryPrimitives.WriteUInt64LittleEndian(buf.Slice(1, 8), 8UL);
                    BinaryPrimitives.WriteInt64LittleEndian(buf.Slice(9, 8), i64);
                    stream.Write(buf);
                    return;
                }
            case int i32:
                {
                    WriteCanonicalValueToStream(stream, (long)i32);
                    return;
                }
            case double f64:
                {
                    Span<byte> buf = stackalloc byte[1 + 8 + 8];
                    buf[0] = ValueTagReal;
                    BinaryPrimitives.WriteUInt64LittleEndian(buf.Slice(1, 8), 8UL);
                    BinaryPrimitives.WriteUInt64LittleEndian(buf.Slice(9, 8), BitConverter.DoubleToUInt64Bits(f64));
                    stream.Write(buf);
                    return;
                }
            case string text:
                {
                    var bytes = Encoding.UTF8.GetBytes(text);
                    Span<byte> header = stackalloc byte[1 + 8];
                    header[0] = ValueTagText;
                    BinaryPrimitives.WriteUInt64LittleEndian(header.Slice(1, 8), (ulong)bytes.Length);
                    stream.Write(header);
                    stream.Write(bytes);
                    return;
                }
            case byte[] blob:
                {
                    Span<byte> header = stackalloc byte[1 + 8];
                    header[0] = ValueTagBlob;
                    BinaryPrimitives.WriteUInt64LittleEndian(header.Slice(1, 8), (ulong)blob.Length);
                    stream.Write(header);
                    stream.Write(blob);
                    return;
                }
            default:
                throw new InvalidDataException($"规范化内容哈希不支持的 SQLite 值类型：{value.GetType().FullName}");
        }
    }

    private static TableHash HashTable(SqliteConnection conn, string tableName)
    {
        var quotedTable = QuoteIdent(tableName);
        var columns = new List<(long Cid, string Name, string ColType, long Pk)>();

        using (var pragmaCmd = conn.CreateCommand())
        {
            pragmaCmd.CommandText = $"PRAGMA table_info({quotedTable})";
            using var reader = pragmaCmd.ExecuteReader();
            while (reader.Read())
            {
                columns.Add((
                    reader.GetInt64(0),
                    reader.GetString(1),
                    reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                    reader.GetInt64(5)));
            }
        }

        columns.Sort((a, b) => a.Cid.CompareTo(b.Cid));

        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendUInt64Le(hasher, (ulong)columns.Count);
        foreach (var col in columns)
        {
            DbParsers.AppendLengthPrefixedUtf8(hasher, col.Name);
            DbParsers.AppendLengthPrefixedUtf8(hasher, col.ColType);
        }

        var selectList = string.Join(", ", columns.Select(c => QuoteIdent(c.Name)));
        var pkCols = columns
            .Where(c => c.Pk > 0)
            .OrderBy(c => c.Pk)
            .Select(c => QuoteIdent(c.Name))
            .ToList();
        var orderClause = pkCols.Count > 0 ? string.Join(", ", pkCols) : "rowid";

        long rowCount = 0;
        using (var scanCmd = conn.CreateCommand())
        {
            scanCmd.CommandText = $"SELECT {selectList} FROM {quotedTable} ORDER BY {orderClause}";
            using var reader = scanCmd.ExecuteReader();
            while (reader.Read())
            {
                for (var colIdx = 0; colIdx < columns.Count; colIdx++)
                {
                    var val = reader.IsDBNull(colIdx) ? null : reader.GetValue(colIdx);
                    AppendCanonicalValue(hasher, val);
                }

                rowCount++;
            }
        }

        AppendUInt64Le(hasher, (ulong)rowCount);
        var sha256 = Convert.ToHexStringLower(hasher.GetHashAndReset());
        return new TableHash(tableName, rowCount, sha256);
    }

    internal static int QuerySchemaVersion(SqliteConnection conn)
    {
        using (var existsCmd = conn.CreateCommand())
        {
            existsCmd.CommandText = "SELECT EXISTS(SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = 'meta')";
            var hasMeta = Convert.ToInt64(existsCmd.ExecuteScalar(), CultureInfo.InvariantCulture) != 0;
            if (hasMeta)
            {
                using var metaCmd = conn.CreateCommand();
                metaCmd.CommandText = "SELECT value FROM meta WHERE key = $key";
                metaCmd.Parameters.AddWithValue("$key", DbSchema.MetaKeySchemaVersion);
                var raw = metaCmd.ExecuteScalar() as string;
                if (!string.IsNullOrWhiteSpace(raw)
                    && int.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                {
                    return parsed;
                }
            }
        }

        using var uvCmd = conn.CreateCommand();
        uvCmd.CommandText = "PRAGMA user_version";
        var uv = Convert.ToInt64(uvCmd.ExecuteScalar(), CultureInfo.InvariantCulture);
        return uv >= 0 && uv <= int.MaxValue ? (int)uv : 0;
    }

    private static SqliteConnection OpenReadOnlyHashConnection(string dbPath)
    {
        var csb = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        };
        var conn = new SqliteConnection(csb.ToString());
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
        return conn;
    }

    private static void AppendUInt64Le(IncrementalHash hasher, ulong value)
    {
        Span<byte> buf = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(buf, value);
        hasher.AppendData(buf);
    }

    public static string QuoteIdent(string name)
    {
        return "\"" + name.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }
}
