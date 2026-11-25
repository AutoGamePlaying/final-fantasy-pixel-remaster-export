using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using FfprExport.Db;
using Microsoft.Data.Sqlite;
using Xunit;

namespace FfprExport.Tests;

public sealed class DatabaseTests
{
    [Fact]
    public void BuildSyntheticDatabase_CoversAllAreasAliasesCoordinatesChestsAndDanglingIssues()
    {
        using var ws = new TempWorkspace("ffpr-db-synth");
        var exportDir = SyntheticDbFixture.CreateSyntheticExport(Path.Combine(ws.Root, "export"));
        var dbPath = Path.Combine(ws.Root, "out", "ff5.sqlite");

        var report = DbBuilder.Build(new DbBuildOptions(exportDir, dbPath, Jobs: 2));

        Assert.True(File.Exists(dbPath));
        Assert.True(File.Exists(report.ManifestPath));
        Assert.Equal("ff5", report.GameId);
        Assert.Equal(DbSchema.SchemaVersion, report.SchemaVersion);
        Assert.Equal(64, report.FileSha256.Length);
        Assert.Equal(64, report.ContentSha256.Length);

        using var reader = DbReader.Open(dbPath);
        var stats = reader.GetStats();

        // 1. Meta has no volatile timestamps or paths.
        Assert.Equal("ff5", stats.Meta[DbSchema.MetaKeyGameId]);
        Assert.Equal("1", stats.Meta[DbSchema.MetaKeySchemaVersion]);
        Assert.Equal(Program.ToolVersion, stats.Meta[DbSchema.MetaKeyToolVersion]);
        Assert.DoesNotContain(stats.Meta.Keys, k => k.Contains("time", StringComparison.OrdinalIgnoreCase) || k.Contains("path", StringComparison.OrdinalIgnoreCase));

        // 2. Text & tag stripping + FTS5 trigram search.
        var itemText = QuerySingle(reader, "SELECT value, value_plain FROM text WHERE scope='system' AND key='MSG_ITEM_01' AND lang='zhc'");
        Assert.Equal("<IC_DRAG>合成药水", itemText["value"]);
        Assert.Equal("合成药水", itemText["value_plain"]);

        var ftsZhc = reader.SearchTextFts("合成药水", "zhc");
        Assert.Single(ftsZhc);
        Assert.Equal("MSG_ITEM_01", ftsZhc[0].Key);

        var ftsEn = reader.SearchTextFts("Synth World", "en");
        Assert.Single(ftsEn);
        Assert.Equal("STORY_01", ftsEn[0].Key);

        // 3. Generic master layer preserves all CSVs (including custom_extra & duplicate content.id=30).
        Assert.Equal(1L, QueryScalarLong(reader, "SELECT COUNT(*) FROM master_table WHERE name='custom_extra'"));
        Assert.Equal(2L, QueryScalarLong(reader, "SELECT COUNT(*) FROM master_row WHERE table_name='content' AND id=30"));

        // 4. Column alias normalization (while original columns stay in doc).
        var itemRow = QuerySingle(reader, "SELECT battle_effect_asset_id, name_zhc, name_ja, name_en, doc FROM item WHERE id=1");
        Assert.Equal(54L, itemRow["battle_effect_asset_id"]);
        Assert.Equal("合成药水", itemRow["name_zhc"]);
        Assert.Equal("テストポーション", itemRow["name_ja"]);
        Assert.Equal("Synth Potion", itemRow["name_en"]);
        Assert.Contains("\"attle_effect_asset_id\":\"54\"", (string)itemRow["doc"]!, StringComparison.Ordinal);

        var monsterRow = QuerySingle(reader, "SELECT attack_plus_group, ability_defense_rate, name_zhc, doc FROM monster WHERE id=1");
        Assert.Equal(33L, monsterRow["attack_plus_group"]);
        Assert.Equal(44L, monsterRow["ability_defense_rate"]);
        Assert.Equal("合成哥布林", monsterRow["name_zhc"]);
        Assert.Contains("\"attack_plus_grop\":\"33\"", (string)monsterRow["doc"]!, StringComparison.Ordinal);
        Assert.Contains("\"ability_defense_reat\":\"44\"", (string)monsterRow["doc"]!, StringComparison.Ordinal);

        var effectRow = QuerySingle(reader, "SELECT se_asset_id_2, doc FROM battle_effect_asset WHERE id=10");
        Assert.Equal(200L, effectRow["se_asset_id_2"]);
        Assert.Contains("\"se_asset_id_1\":\"200\"", (string)effectRow["doc"]!, StringComparison.Ordinal);

        // 5. Map tile coordinate rules (gid -> (x/16, y/16 - 1); non-gid -> (x/16, y/16)).
        var gidObj = QuerySingle(reader, "SELECT tile_x, tile_y, gid, object_type FROM map_object WHERE name='SynthChest'");
        Assert.Equal(30L, gidObj["tile_x"]);
        Assert.Equal(13L, gidObj["tile_y"]);
        Assert.Equal(1023L, gidObj["gid"]);
        Assert.Equal(7L, gidObj["object_type"]);

        var noGidObj = QuerySingle(reader, "SELECT tile_x, tile_y, gid FROM map_object WHERE name='RegionWithoutGid'");
        Assert.Equal(30L, noGidObj["tile_x"]);
        Assert.Equal(14L, noGidObj["tile_y"]);
        Assert.Null(noGidObj["gid"]);

        // Inline entity, UpperObject, GroundObject, route_instruction all imported.
        Assert.Equal(1L, QueryScalarLong(reader, "SELECT COUNT(*) FROM map_object WHERE name='InlineTrigger'"));
        Assert.Equal(1L, QueryScalarLong(reader, "SELECT COUNT(*) FROM map_object WHERE name='UpperDoor'"));
        Assert.Equal(1L, QueryScalarLong(reader, "SELECT COUNT(*) FROM map_object WHERE name='GroundSwitch'"));
        Assert.Equal(1L, QueryScalarLong(reader, "SELECT COUNT(*) FROM map_layer WHERE layer_kind='route_instruction'"));

        // 6. Compact grid queries (collision, damage, hidden_passage, sub_collision, encount_grid).
        Assert.Equal(2, reader.QueryMapGridCell("Map_20010", "collision", 2, 1)?.Value);
        Assert.Equal(5, reader.QueryMapGridCell("Map_20010", "damage", 1, 1)?.Value);
        Assert.Equal(1, reader.QueryMapGridCell("Map_20010", "hidden_passage", 2, 2)?.Value);
        Assert.Equal(2, reader.QueryMapGridCell("Map_20010", "sub_collision", 3, 2)?.Value);
        Assert.Equal(102, reader.QueryMapGridCell("Map_20010", "encount_grid", 1, 1)?.Value);
        Assert.Equal(5, reader.QueryAllMapGridCellsAt("Map_20010", 1, 1).Count);

        // 7. Treasure chest -> content link & map.csv -> submap link.
        var chestRow = QuerySingle(
            reader,
            """
            SELECT tc.tile_x, tc.tile_y, tc.content_id, tc.content_num, tc.content_name_zhc, c.mes_id_name
            FROM treasure_chest tc
            JOIN rel_treasure_content rtc ON rtc.treasure_chest_id = tc.id
            JOIN content c ON c.id = rtc.content_id
            WHERE tc.name = 'SynthChest'
            """);
        Assert.Equal(30L, chestRow["tile_x"]);
        Assert.Equal(13L, chestRow["tile_y"]);
        Assert.Equal(4L, chestRow["content_id"]);
        Assert.Equal(2L, chestRow["content_num"]);
        Assert.Equal("合成羽翼", chestRow["content_name_zhc"]);
        Assert.Equal("MSG_ITEM_02", chestRow["mes_id_name"]);

        Assert.Equal(1L, QueryScalarLong(reader, "SELECT COUNT(*) FROM rel_map_submap WHERE map_id=30 AND map_name='Map_20010'"));
        Assert.Equal(1L, QueryScalarLong(reader, "SELECT COUNT(*) FROM map WHERE id=30 AND submap_id IS NOT NULL"));

        // 8. Scripts: both EntryPoint and Segments Monster AI + inline script + instruction index.
        Assert.Equal("EntryPoint", QuerySingle(reader, "SELECT format FROM script WHERE name='sc_ai_legacy'")["format"]);
        Assert.Equal("Segments", QuerySingle(reader, "SELECT format FROM script WHERE name='sc_ai_modern'")["format"]);
        Assert.Equal(1L, QueryScalarLong(reader, "SELECT COUNT(*) FROM script WHERE name='sc_inline_test'"));
        Assert.Equal(2L, QueryScalarLong(reader, "SELECT COUNT(*) FROM rel_monster_ai"));
        Assert.True(QueryScalarLong(reader, "SELECT COUNT(*) FROM rel_script_text WHERE text_key='STORY_01'") >= 2);

        // 9. Unity objects & unpacked InputActionAsset.
        Assert.Equal(3L, QueryScalarLong(reader, "SELECT COUNT(*) FROM unity_object"));
        Assert.Equal(1L, QueryScalarLong(reader, "SELECT COUNT(*) FROM input_action_map WHERE name='Player'"));
        Assert.Equal(2L, QueryScalarLong(reader, "SELECT COUNT(*) FROM input_action"));
        Assert.Equal(2L, QueryScalarLong(reader, "SELECT COUNT(*) FROM input_binding"));

        // 10. Config docs.
        Assert.Equal(7L, QueryScalarLong(reader, "SELECT COUNT(*) FROM config_doc"));

        // 11. Dangling references are recorded into build_issue without aborting.
        var issueKinds = reader.ExecuteReadOnlySql("SELECT DISTINCT kind FROM build_issue ORDER BY kind")
            .Select(r => (string)r["kind"]!)
            .ToHashSet(StringComparer.Ordinal);
        Assert.Contains("duplicate_master_row_id", issueKinds);
        Assert.Contains("dangling_party_monster", issueKinds);
        Assert.Contains("dangling_monster_set_party", issueKinds);
        Assert.Contains("dangling_encount_monster_set", issueKinds);
        Assert.Contains("dangling_shop_group", issueKinds);
        Assert.Contains("dangling_shop_content", issueKinds);
        Assert.Contains("dangling_treasure_content", issueKinds);
        Assert.Contains("dangling_monster_ai_script", issueKinds);
        Assert.Contains("dangling_map_submap", issueKinds);
        Assert.Contains("dangling_script_text", issueKinds);
    }

    [Fact]
    public void AbilityColumnNormalization_HandlesFf3ShiftedColumnsAndPreservesRawDoc()
    {
        using var ws = new TempWorkspace("ffpr-db-ff3-ability");
        var exportDir = SyntheticDbFixture.CreateSyntheticExport(
            Path.Combine(ws.Root, "export"),
            useFf3AbilityLayout: true,
            includeDanglingReferences: false);
        var dbPath = Path.Combine(ws.Root, "ff3.sqlite");

        DbBuilder.Build(new DbBuildOptions(exportDir, dbPath, Jobs: 1));

        using var reader = DbReader.Open(dbPath);
        var row = QuerySingle(reader, "SELECT ability_random_group_id, buy, sell, sales_not_possible, doc FROM ability WHERE id=1");
        Assert.Equal(77L, row["ability_random_group_id"]);
        Assert.Equal(150L, row["buy"]);
        Assert.Equal(75L, row["sell"]);
        Assert.Equal(0L, row["sales_not_possible"]);
        Assert.Contains("\"ability_random_group_id\":\"77\"", (string)row["doc"]!, StringComparison.Ordinal);
    }

    [Fact]
    public void TypedTablesAndKeyQueries_UseIndexesWithoutFullTableScans()
    {
        using var ws = new TempWorkspace("ffpr-db-explain");
        var exportDir = SyntheticDbFixture.CreateSyntheticExport(
            Path.Combine(ws.Root, "export"),
            includeDanglingReferences: false,
            extraRowsPerTypedTable: 40);
        var dbPath = Path.Combine(ws.Root, "ff5.sqlite");

        DbBuilder.Build(new DbBuildOptions(exportDir, dbPath, Jobs: 2));
        using var reader = DbReader.Open(dbPath);

        // Every typed master table: PK query + name/key lookup query.
        var indexedQueries = new (string Label, string Sql)[]
        {
            ("content PK", "SELECT * FROM content WHERE id = 2"),
            ("content name", "SELECT * FROM content WHERE name_zhc = '合成药水'"),
            ("item PK", "SELECT * FROM item WHERE id = 1"),
            ("item name", "SELECT * FROM item WHERE name_zhc = '合成药水'"),
            ("weapon PK", "SELECT * FROM weapon WHERE id = 1"),
            ("weapon name", "SELECT * FROM weapon WHERE name_en = 'Synth Sword'"),
            ("armor PK", "SELECT * FROM armor WHERE id = 1"),
            ("armor name", "SELECT * FROM armor WHERE name_ja = 'テストアーマー'"),
            ("ability PK", "SELECT * FROM ability WHERE id = 1"),
            ("ability name", "SELECT * FROM ability WHERE name_zhc = '合成治愈'"),
            ("monster PK", "SELECT * FROM monster WHERE id = 1"),
            ("monster name", "SELECT * FROM monster WHERE name_zhc = '合成哥布林'"),
            ("monster_party PK", "SELECT * FROM monster_party WHERE id = 1"),
            ("monster_party name", "SELECT * FROM monster_party WHERE name_zhc = '合成哥布林×2'"),
            ("monster_set PK", "SELECT * FROM monster_set WHERE id = 10"),
            ("job PK", "SELECT * FROM job WHERE id = 1"),
            ("job name", "SELECT * FROM job WHERE name_zhc = '合成骑士'"),
            ("command PK", "SELECT * FROM command WHERE id = 1"),
            ("command name", "SELECT * FROM command WHERE name_zhc = '合成攻击'"),
            ("condition PK", "SELECT * FROM condition WHERE id = 1"),
            ("condition name", "SELECT * FROM condition WHERE name_zhc = '合成中毒'"),
            ("area PK", "SELECT * FROM area WHERE id = 1"),
            ("area name", "SELECT * FROM area WHERE name_zhc = '合成平原'"),
            ("map PK", "SELECT * FROM map WHERE id = 30"),
            ("map name", "SELECT * FROM map WHERE map_name = 'Map_20010'"),
            ("map display name", "SELECT * FROM map WHERE name_zhc = '合成城镇'"),
            ("encount_area PK", "SELECT * FROM encount_area WHERE id = 1"),
            ("encount_area grid_number", "SELECT * FROM encount_area WHERE encount_area_grid_number = 100"),
            ("encount_area_grid PK", "SELECT * FROM encount_area_grid WHERE id = 1"),
            ("encount_area_grid base", "SELECT * FROM encount_area_grid WHERE base_number = 100"),
            ("product_group PK", "SELECT * FROM product_group WHERE id = 1"),
            ("product_group name", "SELECT * FROM product_group WHERE name_zhc = '合成杂货店'"),
            ("product PK", "SELECT * FROM product WHERE id = 1"),
            ("product name", "SELECT * FROM product WHERE name_zhc = '合成药水'"),
            ("character_status PK", "SELECT * FROM character_status WHERE id = 1"),
            ("character_status name", "SELECT * FROM character_status WHERE name_zhc = '合成主角'"),
            ("battle_effect_asset PK", "SELECT * FROM battle_effect_asset WHERE id = 10"),
            ("battle_effect_asset name", "SELECT * FROM battle_effect_asset WHERE asset_name = 'ef_synth_slash'"),
            // Additional core lookups across text, maps, scripts, unity, and relations
            ("text key+lang", "SELECT * FROM text WHERE key = 'MSG_ITEM_01' AND lang = 'zhc'"),
            ("map_object tile", "SELECT * FROM map_object WHERE submap_id = 1 AND tile_x = 30 AND tile_y = 13"),
            ("treasure_chest submap", "SELECT * FROM treasure_chest WHERE submap_id = 1"),
            ("script_instruction mnemonic", "SELECT * FROM script_instruction WHERE mnemonic = 'Msg'"),
            ("unity_object class", "SELECT * FROM unity_object WHERE class = 'Last.UI.CommonWindow'"),
            ("rel_encount_area_party fwd", "SELECT * FROM rel_encount_area_party WHERE encount_area_id = 1"),
            ("rel_encount_area_party rev", "SELECT * FROM rel_encount_area_party WHERE monster_party_id = 1"),
            ("rel_monster_party_member rev", "SELECT * FROM rel_monster_party_member WHERE monster_id = 1"),
            ("rel_shop_product fwd", "SELECT * FROM rel_shop_product WHERE shop_id = 1"),
            ("rel_shop_product rev", "SELECT * FROM rel_shop_product WHERE content_id = 2"),
            ("rel_treasure_content rev", "SELECT * FROM rel_treasure_content WHERE content_id = 4"),
            ("rel_monster_ai rev", "SELECT * FROM rel_monster_ai WHERE script_id = 1"),
            ("rel_map_submap rev", "SELECT * FROM rel_map_submap WHERE submap_id = 1"),
            ("rel_script_text rev", "SELECT * FROM rel_script_text WHERE text_key = 'STORY_01'"),
        };

        foreach (var (label, sql) in indexedQueries)
        {
            var plan = reader.ExplainQueryPlan(sql);
            Assert.NotEmpty(plan);
            foreach (var step in plan)
            {
                Assert.False(
                    step.Detail.StartsWith("SCAN ", StringComparison.OrdinalIgnoreCase),
                    $"查询 [{label}] 出现全表扫描：{step.Detail}（SQL: {sql}）");
                Assert.Contains("SEARCH", step.Detail, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    [Fact]
    public async Task ReproducibleBuild_DifferentOutputDirJobsAndTimezone_ProduceIdenticalFileAndContentHash()
    {
        using var ws = new TempWorkspace("ffpr-db-repro");
        var exportDir = SyntheticDbFixture.CreateSyntheticExport(Path.Combine(ws.Root, "export"));
        var db1 = Path.Combine(ws.Root, "run_alpha", "ff5.sqlite");
        var db2 = Path.Combine(ws.Root, "run_beta", "nested", "ff5.sqlite");

        var r1 = await RunCliWithEnvAsync(
            ["db", "build", "--export", exportDir, "--out", db1, "--jobs", "1"],
            new Dictionary<string, string> { ["TZ"] = "UTC" });
        Assert.True(r1.ExitCode == 0, $"run 1 failed: {r1.Stderr}");

        var r2 = await RunCliWithEnvAsync(
            ["db", "build", "--export", exportDir, "--out", db2, "--jobs", "4"],
            new Dictionary<string, string> { ["TZ"] = "America/New_York" });
        Assert.True(r2.ExitCode == 0, $"run 2 failed: {r2.Stderr}");

        var h1 = DbHasher.HashDatabase(db1);
        var h2 = DbHasher.HashDatabase(db2);

        Assert.Equal(h1.FileSha256, h2.FileSha256);
        Assert.Equal(h1.ContentSha256, h2.ContentSha256);
        Assert.Equal(File.ReadAllBytes(db1), File.ReadAllBytes(db2));

        // Verify deterministic SQLite header PRAGMAs.
        using var reader = DbReader.Open(db1);
        Assert.Equal(DbSchema.PageSize, QueryScalarLong(reader, "PRAGMA page_size"));
        Assert.Equal(0L, QueryScalarLong(reader, "PRAGMA auto_vacuum"));
        Assert.Equal(DbSchema.ApplicationId, QueryScalarLong(reader, "PRAGMA application_id"));
        Assert.Equal(DbSchema.SchemaVersion, QueryScalarLong(reader, "PRAGMA user_version"));
        using (var encCmd = reader.Connection.CreateCommand())
        {
            encCmd.CommandText = "PRAGMA encoding";
            Assert.Equal("UTF-8", encCmd.ExecuteScalar()?.ToString());
        }
    }

    [Fact]
    public void CanonicalContentHash_EncodingChangesOnMutationAndIgnoresInsertOrder()
    {
        // 1. Canonical value encoding tests matching ffr-db/src/hash.rs byte-for-byte.
        Assert.Equal([DbHasher.ValueTagNull], DbHasher.EncodeCanonicalValue(null));

        foreach (var v in new long[] { 0, 1, -1, 42, long.MinValue, long.MaxValue })
        {
            var expected = new byte[1 + 8 + 8];
            expected[0] = DbHasher.ValueTagInteger;
            BinaryPrimitives.WriteUInt64LittleEndian(expected.AsSpan(1, 8), 8UL);
            BinaryPrimitives.WriteInt64LittleEndian(expected.AsSpan(9, 8), v);
            Assert.Equal(expected, DbHasher.EncodeCanonicalValue(v));
        }

        foreach (var v in new double[] { 0.0, -0.0, 1.5, -1234.5678, double.MinValue, double.MaxValue })
        {
            var expected = new byte[1 + 8 + 8];
            expected[0] = DbHasher.ValueTagReal;
            BinaryPrimitives.WriteUInt64LittleEndian(expected.AsSpan(1, 8), 8UL);
            BinaryPrimitives.WriteUInt64LittleEndian(expected.AsSpan(9, 8), BitConverter.DoubleToUInt64Bits(v));
            Assert.Equal(expected, DbHasher.EncodeCanonicalValue(v));
        }

        var cjkBytes = Encoding.UTF8.GetBytes("合成药水/FFPR");
        var expectedText = new byte[1 + 8 + cjkBytes.Length];
        expectedText[0] = DbHasher.ValueTagText;
        BinaryPrimitives.WriteUInt64LittleEndian(expectedText.AsSpan(1, 8), (ulong)cjkBytes.Length);
        cjkBytes.CopyTo(expectedText.AsSpan(9));
        Assert.Equal(expectedText, DbHasher.EncodeCanonicalValue("合成药水/FFPR"));

        var rawBlob = new byte[] { 0x00, 0x01, 0xFE, 0xFF };
        var expectedBlob = new byte[1 + 8 + rawBlob.Length];
        expectedBlob[0] = DbHasher.ValueTagBlob;
        BinaryPrimitives.WriteUInt64LittleEndian(expectedBlob.AsSpan(1, 8), (ulong)rawBlob.Length);
        rawBlob.CopyTo(expectedBlob.AsSpan(9));
        Assert.Equal(expectedBlob, DbHasher.EncodeCanonicalValue(rawBlob));

        // Distinct types never collide.
        Assert.NotEqual(DbHasher.EncodeCanonicalValue(null), DbHasher.EncodeCanonicalValue(string.Empty));
        Assert.NotEqual(DbHasher.EncodeCanonicalValue(string.Empty), DbHasher.EncodeCanonicalValue(Array.Empty<byte>()));
        Assert.NotEqual(DbHasher.EncodeCanonicalValue(0L), DbHasher.EncodeCanonicalValue(0.0));

        // 2. Insertion order independence & mutation sensitivity.
        using var ws = new TempWorkspace("ffpr-db-hash");
        var dbA = Path.Combine(ws.Root, "a.sqlite");
        var dbB = Path.Combine(ws.Root, "b.sqlite");
        var dbC = Path.Combine(ws.Root, "c.sqlite");

        CreateMiniHashDb(dbA, [(1, "alpha", 1.5, [1, 2]), (2, "beta", -0.0, [3, 4]), (3, null, 0.0, null)]);
        CreateMiniHashDb(dbB, [(3, null, 0.0, null), (1, "alpha", 1.5, [1, 2]), (2, "beta", -0.0, [3, 4])]);
        CreateMiniHashDb(dbC, [(1, "alpha", 1.5, [1, 2]), (2, "beta-changed", -0.0, [3, 4]), (3, null, 0.0, null)]);

        var hashA = DbHasher.HashDatabase(dbA);
        var hashB = DbHasher.HashDatabase(dbB);
        var hashC = DbHasher.HashDatabase(dbC);

        // Same content inserted in different order => identical content_sha256.
        Assert.Equal(hashA.ContentSha256, hashB.ContentSha256);
        Assert.Equal(hashA.Tables, hashB.Tables);

        // Changing a single row => different content_sha256.
        Assert.NotEqual(hashA.ContentSha256, hashC.ContentSha256);

        // FTS5 virtual/shadow tables & sqlite_stat1 excluded.
        Assert.DoesNotContain(hashA.Tables, t => t.Name.StartsWith("sqlite_", StringComparison.Ordinal));
        Assert.DoesNotContain(hashA.Tables, t => t.Name.StartsWith("notes_fts", StringComparison.Ordinal));
        Assert.Contains(hashA.Tables, t => t.Name == "sample" && t.Rows == 3);
    }

    [Fact]
    public async Task CliCommands_HashJsonStatsAndReadOnlySqlGuard_WorkAsSpecified()
    {
        using var ws = new TempWorkspace("ffpr-db-cli");
        var exportDir = SyntheticDbFixture.CreateSyntheticExport(Path.Combine(ws.Root, "export"));
        var dbPath = Path.Combine(ws.Root, "ff5.sqlite");

        var buildRes = await TestHarness.RunCliAsync(["db", "build", "--export", exportDir, "--out", dbPath]);
        Assert.True(buildRes.ExitCode == 0, buildRes.Stderr);
        var originalBytes = File.ReadAllBytes(dbPath);

        // 1. db hash --json matches exact contract expected by task 27c.
        var hashRes = await TestHarness.RunCliAsync(["db", "hash", dbPath, "--json"]);
        Assert.True(hashRes.ExitCode == 0, hashRes.Stderr);
        using (var doc = JsonDocument.Parse(hashRes.Stdout.Trim()))
        {
            var root = doc.RootElement;
            Assert.Equal(1, root.GetProperty("format").GetInt32());
            Assert.Equal(64, root.GetProperty("file_sha256").GetString()!.Length);
            Assert.Equal(64, root.GetProperty("content_sha256").GetString()!.Length);
            Assert.Equal(1, root.GetProperty("schema_version").GetInt32());
            Assert.StartsWith("3.", root.GetProperty("sqlite_version").GetString(), StringComparison.Ordinal);

            var tables = root.GetProperty("tables");
            Assert.True(tables.GetArrayLength() >= 30);
            var itemEntry = tables.EnumerateArray().First(t => t.GetProperty("name").GetString() == "item");
            Assert.Equal(2, itemEntry.GetProperty("rows").GetInt64());
            Assert.Equal(64, itemEntry.GetProperty("sha256").GetString()!.Length);

            // Virtual/shadow/internal tables excluded.
            Assert.DoesNotContain(tables.EnumerateArray(), t =>
            {
                var n = t.GetProperty("name").GetString()!;
                return n.StartsWith("sqlite_", StringComparison.Ordinal) || n.StartsWith("text_fts", StringComparison.Ordinal);
            });
        }

        // 2. db stats
        var statsRes = await TestHarness.RunCliAsync(["db", "stats", dbPath]);
        Assert.True(statsRes.ExitCode == 0, statsRes.Stderr);
        Assert.Contains("game_id = ff5", statsRes.Stdout, StringComparison.Ordinal);
        Assert.Contains("treasure_chest", statsRes.Stdout, StringComparison.Ordinal);

        // 3. db sql read-only SELECT works
        var sqlRes = await TestHarness.RunCliAsync(["db", "sql", dbPath, "SELECT id, name_zhc FROM item ORDER BY id"]);
        Assert.True(sqlRes.ExitCode == 0, sqlRes.Stderr);
        var lines = sqlRes.Stdout.Trim().Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        Assert.Contains("合成药水", lines[0], StringComparison.Ordinal);

        // 4. db sql rejects all write/DDL/multi-statement queries without modifying DB
        string[] forbiddenQueries =
        [
            "DELETE FROM item",
            "UPDATE item SET buy = 0 WHERE id = 1",
            "INSERT INTO meta(key, value) VALUES ('x', 'y')",
            "DROP TABLE item",
            "CREATE TABLE evil (id INTEGER)",
            "PRAGMA user_version = 99",
            "SELECT 1; DELETE FROM item",
            "WITH cte AS (SELECT 1) DELETE FROM item",
        ];

        foreach (var badSql in forbiddenQueries)
        {
            var res = await TestHarness.RunCliAsync(["db", "sql", dbPath, badSql]);
            Assert.NotEqual(0, res.ExitCode);
            Assert.Contains("错误：", res.Stderr, StringComparison.Ordinal);
        }

        Assert.Equal(originalBytes, File.ReadAllBytes(dbPath));
    }

    private static void CreateMiniHashDb(string path, IReadOnlyList<(long Id, string? Label, double Score, byte[]? Payload)> rows)
    {
        var csb = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        };
        using var conn = new SqliteConnection(csb.ToString());
        conn.Open();
        DbSchema.ExecuteNonQuery(
            conn,
            """
            PRAGMA user_version = 1;
            CREATE TABLE sample (id INTEGER PRIMARY KEY, label TEXT, score REAL, payload BLOB);
            CREATE VIRTUAL TABLE notes_fts USING fts5(body, tokenize='trigram');
            """);

        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "INSERT INTO sample (id, label, score, payload) VALUES ($id, $lbl, $sc, $pl)";
        var pId = cmd.Parameters.Add("$id", SqliteType.Integer);
        var pLbl = cmd.Parameters.Add("$lbl", SqliteType.Text);
        var pSc = cmd.Parameters.Add("$sc", SqliteType.Real);
        var pPl = cmd.Parameters.Add("$pl", SqliteType.Blob);

        foreach (var r in rows)
        {
            pId.Value = r.Id;
            pLbl.Value = r.Label is not null ? r.Label : DBNull.Value;
            pSc.Value = r.Score;
            pPl.Value = r.Payload is not null ? r.Payload : DBNull.Value;
            cmd.ExecuteNonQuery();
        }

        DbSchema.ExecuteNonQuery(conn, "INSERT INTO notes_fts (body) VALUES ('trigram sample text');");
        tx.Commit();
        DbSchema.ExecuteNonQuery(conn, "ANALYZE;");
    }

    private static Dictionary<string, object?> QuerySingle(DbReader reader, string sql)
    {
        var rows = reader.ExecuteReadOnlySql(sql);
        Assert.Single(rows);
        return rows[0];
    }

    private static long QueryScalarLong(DbReader reader, string sql)
    {
        using var cmd = reader.Connection.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    private static async Task<CliResult> RunCliWithEnvAsync(
        IReadOnlyList<string> args,
        IReadOnlyDictionary<string, string> envOverrides)
    {
        var dllPath = Path.Combine(AppContext.BaseDirectory, "ffpr-export.dll");
        var startInfo = new ProcessStartInfo
        {
            FileName = TestHarness.ResolveDotnetExecutable(),
            WorkingDirectory = TestHarness.FindRepoRoot(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            // The CLI writes UTF-8 when redirected; decode it as UTF-8 too (Windows would default to the OEM code page).
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };
        startInfo.ArgumentList.Add("exec");
        startInfo.ArgumentList.Add(dllPath);
        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        foreach (var (k, v) in envOverrides)
        {
            startInfo.Environment[k] = v;
        }

        using var proc = Process.Start(startInfo)
            ?? throw new InvalidOperationException("无法启动 ffpr-export 子进程");
        var stdoutTask = proc.StandardOutput.ReadToEndAsync();
        var stderrTask = proc.StandardError.ReadToEndAsync();
        await proc.WaitForExitAsync();
        return new CliResult(proc.ExitCode, await stdoutTask, await stderrTask);
    }
}
