using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

namespace FfprExport.Tests;

public sealed class BuildDbTests
{
    private sealed class FakeDatabaseRunner : IBuildDbDatabaseRunner
    {
        public string? LastExportDir { get; private set; }
        public string? LastSqlitePath { get; private set; }
        public int LastJobs { get; private set; }
        public byte[] SqlitePayload { get; set; } = Encoding.ASCII.GetBytes("SQLite format 3\0synthetic-ffpr-db-v1");
        public string ContentSha256 { get; set; } = "deadbeef00000000000000000000000000000000000000000000000000000001";
        public FfprDbTableHash[] Tables { get; set; } =
        [
            new("item", 36, "1111111111111111111111111111111111111111111111111111111111111111"),
            new("monster", 128, "2222222222222222222222222222222222222222222222222222222222222222"),
        ];

        public Task<int> BuildAsync(string exportDirectory, string sqlitePath, int jobs)
        {
            LastExportDir = exportDirectory;
            LastSqlitePath = sqlitePath;
            LastJobs = jobs;

            if (!Directory.Exists(PathSupport.ToIoPath(exportDirectory)))
            {
                throw new DirectoryNotFoundException($"假 db build：缺少导出目录 {exportDirectory}");
            }

            if (!File.Exists(PathSupport.ToIoPath(Path.Combine(exportDirectory, "manifest.json"))))
            {
                throw new FileNotFoundException($"假 db build：导出目录缺少 manifest.json：{exportDirectory}");
            }

            var ioSqlitePath = PathSupport.ToIoPath(sqlitePath);
            var parent = Path.GetDirectoryName(ioSqlitePath);
            if (!string.IsNullOrEmpty(parent))
            {
                Directory.CreateDirectory(parent);
            }

            File.WriteAllBytes(ioSqlitePath, SqlitePayload);
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
                tables = Tables.Select(t => new { name = t.Name, rows = t.Rows, sha256 = t.Sha256 }).ToArray(),
            });

            var parsed = InProcessDbCommandRunner.ParseDbHashReport(json, sqlitePath);
            return Task.FromResult(parsed);
        }
    }

    [Fact]
    public void PeVersionReader_ParsesSyntheticPe32PlusPe32FallbackAndRejectsInvalidFiles()
    {
        // 1. PE32+ with StringFileInfo overriding fixed version in a non-standard section name.
        var pe64 = BuildMinimalPeWithVersionResource(
            fileVersion: (2019, 4, 26, 32270),
            productVersion: (1, 0, 0, 0),
            stringFileVersion: "2019.4.26.3227036",
            stringProductVersion: "1.2.3-ffpr",
            sectionName: ".link",
            pe32Plus: true);

        using (var ms = new MemoryStream(pe64))
        {
            var parsed = PeVersionReader.ReadFromStream(ms, "synthetic64.exe");
            Assert.Equal("2019.4.26.3227036", parsed.FileVersion);
            Assert.Equal("1.2.3-ffpr", parsed.ProductVersion);
            Assert.Equal("2019.4.26.32270", parsed.FixedFileVersion);
            Assert.Equal("1.0.0.0", parsed.FixedProductVersion);
        }

        // 2. PE32 with VS_FIXEDFILEINFO only (no StringFileInfo) falls back to fixed version numbers.
        var pe32 = BuildMinimalPeWithVersionResource(
            fileVersion: (5, 6, 1, 99),
            productVersion: (2, 4, 0, 1),
            stringFileVersion: null,
            stringProductVersion: null,
            sectionName: ".rsrc",
            pe32Plus: false);

        using (var ms = new MemoryStream(pe32))
        {
            var parsed = PeVersionReader.ReadFromStream(ms, "synthetic32.exe");
            Assert.Equal("5.6.1.99", parsed.FileVersion);
            Assert.Equal("2.4.0.1", parsed.ProductVersion);
        }

        // 3. Invalid non-PE bytes throw InvalidDataException with clear localized message.
        using (var ms = new MemoryStream(Encoding.UTF8.GetBytes("not a pe executable binary padding................................................")))
        {
            var enEx = Assert.Throws<InvalidDataException>(() => PeVersionReader.ReadFromStream(ms, "bad.exe"));
            Assert.Contains("Failed to parse PE version resource", enEx.Message, StringComparison.Ordinal);
        }

        using (AppLocalization.PushLanguage(AppLanguage.ZhHans))
        using (var ms = new MemoryStream(Encoding.UTF8.GetBytes("not a pe executable binary padding................................................")))
        {
            var zhEx = Assert.Throws<InvalidDataException>(() => PeVersionReader.ReadFromStream(ms, "bad.exe"));
            Assert.Contains("解析 PE 版本资源失败", zhEx.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ResourceFingerprint_ChangesWithContentAndIsInvariantToOrderAndParallelism()
    {
        using var ws = new TempWorkspace("ffpr-res-fp");
        var spec = GameDetector.KnownGames.Single(g => g.Id == "ff5");
        var gameDir = SyntheticUnityFixtureBuilder.BuildSyntheticSteamGame(ws.Root, spec);
        var detected = GameDetector.Detect(gameDir);

        var fpSingle = GameVersionDetector.ComputeResourceFingerprint(detected.DataDirectory, detected.CatalogPath, jobs: 1);
        var fpParallel = GameVersionDetector.ComputeResourceFingerprint(detected.DataDirectory, detected.CatalogPath, jobs: 4);

        // 2 Addressables bundles + 1 font bundle in StreamingAssets
        Assert.Equal(3, fpSingle.Bundles.Length);
        Assert.Equal(fpSingle.Fingerprint, fpParallel.Fingerprint);
        Assert.Equal(fpSingle.CatalogSha256, fpParallel.CatalogSha256);

        // Shuffling bundle order produces the exact same combined fingerprint.
        var shuffled = fpSingle.Bundles.Reverse().ToArray();
        Assert.Equal(
            fpSingle.Fingerprint,
            GameVersionDetector.CombineResourceFingerprint(fpSingle.CatalogSha256, shuffled));

        // Mutating a bundle changes the fingerprint.
        var bundlePath = Path.Combine(detected.AddressablesDirectory, "StandaloneWindows64", "master_assets_all_0001.bundle");
        var originalBundleBytes = File.ReadAllBytes(bundlePath);
        var modifiedBundleBytes = (byte[])originalBundleBytes.Clone();
        modifiedBundleBytes[^1] ^= 0xFF;
        File.WriteAllBytes(bundlePath, modifiedBundleBytes);

        var fpBundleChanged = GameVersionDetector.ComputeResourceFingerprint(detected.DataDirectory, detected.CatalogPath, jobs: 2);
        Assert.NotEqual(fpSingle.Fingerprint, fpBundleChanged.Fingerprint);

        // Restore bundle, mutate catalog.json -> fingerprint changes.
        File.WriteAllBytes(bundlePath, originalBundleBytes);
        var originalCatalog = File.ReadAllText(detected.CatalogPath);
        File.WriteAllText(detected.CatalogPath, originalCatalog + "\n", OutputGuard.Utf8WithoutBom);

        var fpCatalogChanged = GameVersionDetector.ComputeResourceFingerprint(detected.DataDirectory, detected.CatalogPath, jobs: 2);
        Assert.NotEqual(fpSingle.Fingerprint, fpCatalogChanged.Fingerprint);
    }

    [Fact]
    public void KnownBuildsRegistry_CoversAllThreeOutcomesGameGroupingAndLoadPriority()
    {
        using var ws = new TempWorkspace("ffpr-known-builds");

        var gameVersion = new GameVersionInfo(
            GameId: "ff5",
            Title: "FINAL FANTASY V",
            UnityVersion: "2019.4.26f1",
            Steam: new SteamBuildInfo(1173810, "20265087", "FINAL FANTASY V", "1792418402", "appmanifest"),
            Exe: new ExeVersionInfo("FINAL FANTASY V.exe", 2048, "exesha", "2019.4.26.3227036", "1.0.0", "2019.4.26.32270", "1.0.0.0"),
            GameAssembly: new BinaryFileHashInfo("GameAssembly.dll", 1024, "gasha_1"),
            GlobalMetadata: new BinaryFileHashInfo("FINAL FANTASY V_Data/il2cpp_data/Metadata/global-metadata.dat", 512, "metasha_1"),
            CatalogSha256: "catsha_1",
            CatalogSize: 256,
            BundleCount: 3,
            BundleTotalBytes: 4096,
            ResourceFingerprint: "aaaa1111bbbb2222");

        var registeredDb = new BuildDatabaseInfo(
            FileName: "ff5.sqlite",
            Size: 8192,
            SchemaVersion: 1,
            SqliteVersion: "3.49.1",
            FileSha256: "filehash_expected",
            ContentSha256: "contenthash_expected",
            HashFormat: 1,
            Tables:
            [
                new("item", 36, "sha_item_1"),
                new("monster", 200, "sha_monster_1"),
                new("job", 22, "sha_job_1"),
            ]);

        var registeredEntry = new KnownBuildEntry(
            RegisteredAt: "2026-11-25",
            Note: "synthetic baseline",
            GameVersion: gameVersion,
            Tools: null,
            Database: registeredDb);

        var registry = new Dictionary<string, KnownBuildEntry[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["ff1"] = [],
            ["ff2"] = [],
            ["ff3"] = [],
            ["ff4"] = [],
            ["ff5"] = [registeredEntry],
            ["ff6"] = [],
        };

        // 1. Exact match -> "与登记的版本一致"
        var matched = KnownBuildsRegistry.Compare(registry, "file", "ff5", gameVersion, registeredDb);
        Assert.Equal(KnownBuildComparisonStatus.Matched, matched.StatusKind);
        Assert.Equal(KnownBuildsRegistry.MessageMatched, matched.Message);
        Assert.Empty(matched.TableDiffs);

        // 2a. Different game id (ff6 empty) -> "新的游戏版本，未登记"
        var otherGame = KnownBuildsRegistry.Compare(registry, "file", "ff6", gameVersion with { GameId = "ff6" }, registeredDb);
        Assert.Equal(KnownBuildComparisonStatus.NewGameVersion, otherGame.StatusKind);
        Assert.Equal(KnownBuildsRegistry.MessageNewGameVersion, otherGame.Message);

        // 2b. Different resource fingerprint -> "新的游戏版本，未登记"
        var newVersion = KnownBuildsRegistry.Compare(
            registry,
            "file",
            "ff5",
            gameVersion with { ResourceFingerprint = "cccc3333dddd4444" },
            registeredDb);
        Assert.Equal(KnownBuildComparisonStatus.NewGameVersion, newVersion.StatusKind);
        Assert.Equal(KnownBuildsRegistry.MessageNewGameVersion, newVersion.Message);

        // 3. Same game version, different DB hash -> "游戏版本已登记，但数据库哈希不同" + differing tables
        var modifiedDb = registeredDb with
        {
            FileSha256 = "filehash_actual",
            ContentSha256 = "contenthash_actual",
            Tables =
            [
                new("item", 36, "sha_item_1"),      // unchanged
                new("ability", 90, "sha_ability"),  // added
                new("monster", 201, "sha_monster_2"), // changed
                // "job" removed
            ],
        };
        var mismatch = KnownBuildsRegistry.Compare(registry, "file", "ff5", gameVersion, modifiedDb);
        Assert.Equal(KnownBuildComparisonStatus.HashMismatch, mismatch.StatusKind);
        Assert.Equal(KnownBuildsRegistry.MessageHashMismatch, mismatch.Message);
        Assert.Equal(3, mismatch.TableDiffs.Length);

        Assert.Equal("added", Assert.Single(mismatch.TableDiffs, d => d.Name == "ability").Kind);
        Assert.Equal("removed", Assert.Single(mismatch.TableDiffs, d => d.Name == "job").Kind);
        var changed = Assert.Single(mismatch.TableDiffs, d => d.Name == "monster");
        Assert.Equal("changed", changed.Kind);
        Assert.Equal(200, changed.ExpectedRows);
        Assert.Equal(201, changed.ActualRows);

        var printed = new StringWriter();
        KnownBuildsRegistry.PrintComparison(mismatch, printed);
        var printedText = printed.ToString();
        Assert.Contains(KnownBuildsRegistry.MessageHashMismatch, printedText, StringComparison.Ordinal);
        Assert.Contains("ability", printedText, StringComparison.Ordinal);
        Assert.Contains("job", printedText, StringComparison.Ordinal);
        Assert.Contains("monster", printedText, StringComparison.Ordinal);

        // 4. Load priority: exe sibling file wins over embedded resource; embedded registry matches expected schema for ff1..ff6.
        var emptyDir = Path.Combine(ws.Root, "empty-bin");
        Directory.CreateDirectory(emptyDir);
        var (embeddedByGame, embeddedSource) = KnownBuildsRegistry.Load(emptyDir);
        Assert.Equal("embedded", embeddedSource);
        var hex64Regex = new Regex("^[0-9a-f]{64}$", RegexOptions.CultureInvariant);
        foreach (var id in new[] { "ff1", "ff2", "ff3", "ff4", "ff5", "ff6" })
        {
            Assert.True(embeddedByGame.ContainsKey(id), $"内嵌 known-builds.json 应包含 {id}");
            foreach (var entry in embeddedByGame[id])
            {
                Assert.False(string.IsNullOrWhiteSpace(entry.RegisteredAt), $"{id} 登记项缺少 registered_at");
                Assert.NotNull(entry.GameVersion);
                Assert.NotNull(entry.GameVersion.Steam);
                Assert.False(
                    string.IsNullOrWhiteSpace(entry.GameVersion.Steam.BuildId),
                    $"{id} 登记项缺少 game_version.steam.build_id");
                Assert.Matches(hex64Regex, entry.GameVersion.ResourceFingerprint);
                Assert.NotNull(entry.Database);
                Assert.Matches(hex64Regex, entry.Database.FileSha256);
                Assert.NotNull(entry.Database.ContentSha256);
                Assert.Matches(hex64Regex, entry.Database.ContentSha256);
                foreach (var table in entry.Database.Tables)
                {
                    Assert.Matches(hex64Regex, table.Sha256);
                }
            }
        }

        var adjacentDir = Path.Combine(ws.Root, "adjacent-bin");
        Directory.CreateDirectory(adjacentDir);
        File.WriteAllText(
            Path.Combine(adjacentDir, "known-builds.json"),
            JsonSerializer.Serialize(registry, FieldJsonWriter.SerializerOptions),
            OutputGuard.Utf8WithoutBom);
        var (fileByGame, fileSource) = KnownBuildsRegistry.Load(adjacentDir);
        Assert.Equal("file", fileSource);
        Assert.Single(fileByGame["ff5"]);
        Assert.Equal("aaaa1111bbbb2222", fileByGame["ff5"][0].GameVersion.ResourceFingerprint);
    }

    [Fact]
    public void WindowsLongPathSupportAndManifest_AreConfiguredProperly()
    {
        Assert.Equal(@"C:\Games\FF5", PathSupport.ApplyWindowsLongPathPrefix(@"C:\Games\FF5"));

        var longWinPath = @"D:\Output With Spaces\" + string.Join(@"\", Enumerable.Repeat("DeepSubDirectoryName", 15)) + @"\ff5.sqlite";
        Assert.True(longWinPath.Length > 260);
        var prefixed = PathSupport.ApplyWindowsLongPathPrefix(longWinPath);
        Assert.Equal(@"\\?\" + longWinPath, prefixed);
        Assert.Equal(prefixed, PathSupport.ApplyWindowsLongPathPrefix(prefixed));

        var longUnc = @"\\fileserver\share\" + string.Join(@"\", Enumerable.Repeat("DeepSegmentName", 18));
        Assert.True(longUnc.Length > 260);
        Assert.Equal(@"\\?\UNC\" + longUnc[2..], PathSupport.ApplyWindowsLongPathPrefix(longUnc));

        var repoRoot = TestHarness.FindRepoRoot();
        var manifest = File.ReadAllText(Path.Combine(repoRoot, "src", "FfprExport", "app.manifest"));
        Assert.Contains("<longPathAware", manifest, StringComparison.Ordinal);
        Assert.Contains(">true</longPathAware>", manifest, StringComparison.Ordinal);

        var csproj = File.ReadAllText(Path.Combine(repoRoot, "src", "FfprExport", "FfprExport.csproj"));
        Assert.Contains("<ApplicationManifest>app.manifest</ApplicationManifest>", csproj, StringComparison.Ordinal);
        Assert.Contains("known-builds.json", csproj, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BuildDb_EndToEndWithFakeDb_CoversVersionHashesKnownBuildsGuardrailsAndSteamSources()
    {
        using var langScope = AppLocalization.PushLanguage(AppLanguage.ZhHans);
        using var ws = new TempWorkspace("ffpr build db e2e");
        var spec = GameDetector.KnownGames.Single(g => g.Id == "ff5");
        var steamRoot = Path.Combine(ws.Root, "Steam Library");
        var gameDir = SyntheticUnityFixtureBuilder.BuildSyntheticSteamGame(
            steamRoot,
            spec,
            steamBuildId: "20265087",
            customCommonFolderName: "FINAL FANTASY V PR");

        // Enhance synthetic game directory with a valid PE exe, GameAssembly.dll, global-metadata.dat, and rich appmanifest.
        var peBytes = BuildMinimalPeWithVersionResource(
            fileVersion: (2019, 4, 26, 32270),
            productVersion: (1, 0, 0, 0),
            stringFileVersion: "2019.4.26.3227036",
            stringProductVersion: "2019.4.26.3227036",
            sectionName: ".rsrc",
            pe32Plus: true);
        await File.WriteAllBytesAsync(Path.Combine(gameDir, spec.ExeName), peBytes);

        var gameAssemblyBytes = Encoding.UTF8.GetBytes("SYNTHETIC_GAME_ASSEMBLY_DLL_PAYLOAD");
        await File.WriteAllBytesAsync(Path.Combine(gameDir, "GameAssembly.dll"), gameAssemblyBytes);

        var metadataDir = Path.Combine(gameDir, spec.DataDirName, "il2cpp_data", "Metadata");
        Directory.CreateDirectory(metadataDir);
        var metadataBytes = Encoding.UTF8.GetBytes("SYNTHETIC_GLOBAL_METADATA_DAT");
        await File.WriteAllBytesAsync(Path.Combine(metadataDir, "global-metadata.dat"), metadataBytes);

        var acfPath = Path.Combine(steamRoot, "steamapps", $"appmanifest_{spec.AppId}.acf");
        await File.WriteAllTextAsync(
            acfPath,
            $$"""
            "AppState"
            {
            	"appid"		"{{spec.AppId}}"
            	"name"		"FINAL FANTASY V"
            	"LastUpdated"		"1792418402"
            	"buildid"		"20265087"
            	"UserConfig"
            	{
            		"buildid"		"99999999"
            	}
            }
            """,
            OutputGuard.Utf8WithoutBom);

        var fakeDb = new FakeDatabaseRunner();
        var emptyKnownDir = Path.Combine(ws.Root, "known-empty");
        Directory.CreateDirectory(emptyKnownDir);

        // 1. Run build-db without --keep-json on a path containing spaces.
        var outDir1 = Path.Combine(ws.Root, "Output With Spaces", "run1");
        var out1 = new StringWriter();
        var err1 = new StringWriter();
        var exit1 = await BuildDbCommand.RunAsync(
            ["--game", gameDir, "--out", outDir1, "--jobs", "2"],
            fakeDb,
            knownBuildsBaseDirOverride: emptyKnownDir,
            stdout: out1,
            stderr: err1);

        Assert.Equal(0, exit1);
        Assert.Contains(KnownBuildsRegistry.MessageNewGameVersion, out1.ToString(), StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(outDir1, "ff5.sqlite")));
        Assert.True(File.Exists(Path.Combine(outDir1, "ff5.sqlite.build.json")));
        Assert.False(Directory.Exists(Path.Combine(outDir1, "json")), "不加 --keep-json 时应删除 json 目录");
        Assert.Equal(2, fakeDb.LastJobs);

        var buildReport1Text = await File.ReadAllTextAsync(Path.Combine(outDir1, "ff5.sqlite.build.json"));
        using var reportDoc1 = JsonDocument.Parse(buildReport1Text);
        var r1 = reportDoc1.RootElement;
        Assert.Equal("build-db", r1.GetProperty("command").GetString());
        Assert.Equal("completed", r1.GetProperty("status").GetString());
        Assert.Equal("ff5", r1.GetProperty("game_id").GetString());
        Assert.Equal("2019.4.26f1", r1.GetProperty("game_version").GetProperty("unity_version").GetString());
        Assert.Equal("20265087", r1.GetProperty("game_version").GetProperty("steam").GetProperty("build_id").GetString());
        Assert.Equal("appmanifest", r1.GetProperty("game_version").GetProperty("steam").GetProperty("source").GetString());
        Assert.Equal("1792418402", r1.GetProperty("game_version").GetProperty("steam").GetProperty("last_updated").GetString());
        Assert.Equal("2019.4.26.3227036", r1.GetProperty("game_version").GetProperty("exe").GetProperty("file_version").GetString());
        Assert.Equal(
            Convert.ToHexStringLower(SHA256.HashData(gameAssemblyBytes)),
            r1.GetProperty("game_version").GetProperty("game_assembly").GetProperty("sha256").GetString());
        Assert.Equal(
            Convert.ToHexStringLower(SHA256.HashData(metadataBytes)),
            r1.GetProperty("game_version").GetProperty("global_metadata").GetProperty("sha256").GetString());
        Assert.Equal(3, r1.GetProperty("game_version").GetProperty("bundle_count").GetInt32());
        Assert.False(string.IsNullOrWhiteSpace(r1.GetProperty("game_version").GetProperty("resource_fingerprint").GetString()));
        Assert.Equal(fakeDb.ContentSha256, r1.GetProperty("database").GetProperty("content_sha256").GetString());
        Assert.Equal(2, r1.GetProperty("database").GetProperty("tables").GetArrayLength());
        Assert.Equal("new_game_version", r1.GetProperty("known_build_check").GetProperty("status").GetString());
        Assert.True(r1.GetProperty("timings_ms").GetProperty("resource_fingerprint").GetInt64() >= 0);

        // 2. Register this build into an adjacent known-builds.json -> next run reports Matched; modified DB reports HashMismatch.
        var knownDir = Path.Combine(ws.Root, "known-registered");
        Directory.CreateDirectory(knownDir);
        var gvDeserialized = r1.GetProperty("game_version").Deserialize<GameVersionInfo>()!;
        var dbDeserialized = r1.GetProperty("database").Deserialize<BuildDatabaseInfo>()!;
        var registryWithFf5 = new Dictionary<string, KnownBuildEntry[]>
        {
            ["ff1"] = [],
            ["ff2"] = [],
            ["ff3"] = [],
            ["ff4"] = [],
            ["ff5"] = [new KnownBuildEntry("2026-11-25", "test", gvDeserialized, null, dbDeserialized)],
            ["ff6"] = [],
        };
        await File.WriteAllTextAsync(
            Path.Combine(knownDir, "known-builds.json"),
            JsonSerializer.Serialize(registryWithFf5, FieldJsonWriter.SerializerOptions),
            OutputGuard.Utf8WithoutBom);

        // 2a. Refuse non-empty own dir without --force; succeed with --force and report Matched.
        await Assert.ThrowsAsync<IOException>(() => BuildDbCommand.RunAsync(
            ["--game", gameDir, "--out", outDir1],
            fakeDb,
            knownBuildsBaseDirOverride: knownDir,
            stdout: new StringWriter(),
            stderr: new StringWriter()));

        var outMatched = new StringWriter();
        var exitMatched = await BuildDbCommand.RunAsync(
            ["--game", gameDir, "--out", outDir1, "--force"],
            fakeDb,
            knownBuildsBaseDirOverride: knownDir,
            stdout: outMatched,
            stderr: new StringWriter());
        Assert.Equal(0, exitMatched);
        Assert.Contains(KnownBuildsRegistry.MessageMatched, outMatched.ToString(), StringComparison.Ordinal);

        // 2b. Alter DB hash -> reports HashMismatch and lists differing table.
        fakeDb.ContentSha256 = "deadbeef00000000000000000000000000000000000000000000000000000999";
        fakeDb.Tables =
        [
            new("item", 37, "9999999999999999999999999999999999999999999999999999999999999999"),
            new("monster", 128, "2222222222222222222222222222222222222222222222222222222222222222"),
        ];
        var outMismatch = new StringWriter();
        var exitMismatch = await BuildDbCommand.RunAsync(
            ["--game", gameDir, "--out", outDir1, "--force"],
            fakeDb,
            knownBuildsBaseDirOverride: knownDir,
            stdout: outMismatch,
            stderr: new StringWriter());
        Assert.Equal(0, exitMismatch);
        var mismatchText = outMismatch.ToString();
        Assert.Contains(KnownBuildsRegistry.MessageHashMismatch, mismatchText, StringComparison.Ordinal);
        Assert.Contains("item", mismatchText, StringComparison.Ordinal);

        // 3. Guardrails: foreign directory refused even with --force; --out overlapping --game refused.
        var foreignDir = Path.Combine(ws.Root, "foreign-dir");
        Directory.CreateDirectory(foreignDir);
        await File.WriteAllTextAsync(Path.Combine(foreignDir, "notes.txt"), "keep");
        var foreignEx = await Assert.ThrowsAsync<IOException>(() => BuildDbCommand.RunAsync(
            ["--game", gameDir, "--out", foreignDir, "--force"],
            fakeDb,
            knownBuildsBaseDirOverride: emptyKnownDir,
            stdout: new StringWriter(),
            stderr: new StringWriter()));
        Assert.Contains("不是 ffpr-export 的输出目录", foreignEx.Message, StringComparison.Ordinal);
        Assert.Equal("keep", await File.ReadAllTextAsync(Path.Combine(foreignDir, "notes.txt")));

        var overlapEx = await Assert.ThrowsAsync<ArgumentException>(() => BuildDbCommand.RunAsync(
            ["--game", gameDir, "--out", Path.Combine(gameDir, "nested")],
            fakeDb,
            knownBuildsBaseDirOverride: emptyKnownDir,
            stdout: new StringWriter(),
            stderr: new StringWriter()));
        Assert.Contains("不能与游戏目录重叠", overlapEx.Message, StringComparison.Ordinal);

        // 4. --keep-json + deep >260 char output path + manual --steam-buildid when appmanifest is missing.
        File.Delete(acfPath);
        var deepOutDir = Path.Combine(
            ws.Root,
            "deep_output_root",
            string.Join(Path.DirectorySeparatorChar, Enumerable.Repeat("segment_over_twenty_chars_long", 9)));
        Assert.True(deepOutDir.Length > 260, $"测试路径应超过 260 字符，当前长度：{deepOutDir.Length}");

        var exitKeep = await BuildDbCommand.RunAsync(
            ["--game", gameDir, "--out", deepOutDir, "--steam-buildid", "98765432", "--keep-json"],
            fakeDb,
            knownBuildsBaseDirOverride: emptyKnownDir,
            stdout: new StringWriter(),
            stderr: new StringWriter());
        Assert.Equal(0, exitKeep);
        Assert.True(File.Exists(PathSupport.ToIoPath(Path.Combine(deepOutDir, "json", "inventory", "summary.json"))));
        Assert.True(File.Exists(PathSupport.ToIoPath(Path.Combine(deepOutDir, "json", "export", "Data", "Master", "item.csv"))));
        Assert.True(File.Exists(PathSupport.ToIoPath(Path.Combine(deepOutDir, "json", "export", "Res", "Config", "demo_config.asset.json"))));

        using (var manualDoc = JsonDocument.Parse(
            await File.ReadAllTextAsync(PathSupport.ToIoPath(Path.Combine(deepOutDir, "ff5.sqlite.build.json")))))
        {
            var steam = manualDoc.RootElement.GetProperty("game_version").GetProperty("steam");
            Assert.Equal("98765432", steam.GetProperty("build_id").GetString());
            Assert.Equal("manual", steam.GetProperty("source").GetString());
        }

        // 5. Missing both appmanifest and --steam-buildid -> null + warning, does not abort.
        var outDirNull = Path.Combine(ws.Root, "run-null-steam");
        var errNull = new StringWriter();
        var exitNull = await BuildDbCommand.RunAsync(
            ["--game", gameDir, "--out", outDirNull],
            fakeDb,
            knownBuildsBaseDirOverride: emptyKnownDir,
            stdout: new StringWriter(),
            stderr: errNull);
        Assert.Equal(0, exitNull);
        Assert.Contains("警告", errNull.ToString(), StringComparison.Ordinal);
        using (var nullDoc = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(outDirNull, "ff5.sqlite.build.json"))))
        {
            var steam = nullDoc.RootElement.GetProperty("game_version").GetProperty("steam");
            Assert.Equal(JsonValueKind.Null, steam.GetProperty("build_id").ValueKind);
            Assert.Equal(JsonValueKind.Null, steam.GetProperty("source").ValueKind);
        }
    }

    [Fact]
    public async Task BuildDbCli_HandlesHelpAndArgumentValidation()
    {
        var help = await TestHarness.RunCliAsync(["build-db", "--help"]);
        Assert.Equal(0, help.ExitCode);
        Assert.Contains("build-db --game", help.Stdout, StringComparison.Ordinal);

        var missingGame = await TestHarness.RunCliAsync(["build-db"]);
        Assert.Equal(2, missingGame.ExitCode);
        Assert.Contains("--game", missingGame.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BuildDbCli_EndToEndWithRealInProcessDb_BuildsReproducibleSqliteAndCleansJson()
    {
        using var ws = new TempWorkspace("ffpr-build-db-real-cli");
        var spec = GameDetector.KnownGames.Single(g => g.Id == "ff5");
        var steamRoot = Path.Combine(ws.Root, "Steam Library");
        var gameDir = SyntheticUnityFixtureBuilder.BuildSyntheticSteamGame(
            steamRoot,
            spec,
            steamBuildId: "20265087",
            customCommonFolderName: "FINAL FANTASY V PR");

        var peBytes = BuildMinimalPeWithVersionResource(
            fileVersion: (2019, 4, 26, 32270),
            productVersion: (1, 0, 0, 0),
            stringFileVersion: "2019.4.26.3227036",
            stringProductVersion: "2019.4.26.3227036",
            sectionName: ".rsrc",
            pe32Plus: true);
        await File.WriteAllBytesAsync(Path.Combine(gameDir, spec.ExeName), peBytes);
        await File.WriteAllBytesAsync(Path.Combine(gameDir, "GameAssembly.dll"), Encoding.UTF8.GetBytes("SYNTH_GA"));

        var outRun1 = Path.Combine(ws.Root, "Out With Spaces", "run1");
        var outRun2 = Path.Combine(ws.Root, "Out With Spaces", "run2");

        var res1 = await TestHarness.RunCliAsync(
            ["build-db", "--game", gameDir, "--out", outRun1, "--jobs", "2"],
            ffprDeps: ws.DepsDir);
        Assert.True(res1.ExitCode == 0, $"stdout:\n{res1.Stdout}\nstderr:\n{res1.Stderr}");
        Assert.True(File.Exists(Path.Combine(outRun1, "ff5.sqlite")));
        Assert.True(File.Exists(Path.Combine(outRun1, "ff5.sqlite.build.json")));
        Assert.False(Directory.Exists(Path.Combine(outRun1, "json")));
        Assert.Contains(KnownBuildsRegistry.MessageNewGameVersion, res1.Stdout, StringComparison.Ordinal);

        var res2 = await TestHarness.RunCliAsync(
            ["build-db", "--game", gameDir, "--out", outRun2, "--jobs", "1", "--keep-json"],
            ffprDeps: ws.DepsDir);
        Assert.True(res2.ExitCode == 0, $"stdout:\n{res2.Stdout}\nstderr:\n{res2.Stderr}");
        Assert.True(Directory.Exists(Path.Combine(outRun2, "json", "export")));
        Assert.True(Directory.Exists(Path.Combine(outRun2, "json", "inventory")));

        using var doc1 = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(outRun1, "ff5.sqlite.build.json")));
        using var doc2 = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(outRun2, "ff5.sqlite.build.json")));
        var db1 = doc1.RootElement.GetProperty("database");
        var db2 = doc2.RootElement.GetProperty("database");
        Assert.Equal(db1.GetProperty("file_sha256").GetString(), db2.GetProperty("file_sha256").GetString());
        Assert.Equal(db1.GetProperty("content_sha256").GetString(), db2.GetProperty("content_sha256").GetString());
        Assert.True(db1.GetProperty("tables").GetArrayLength() > 0);
    }

    [Fact]
    public async Task BuildDbCli_WithoutOutOption_UsesUnifiedAgpOutputDirectoryAndRespectsExplicitOut()
    {
        using var ws = new TempWorkspace("ffpr-build-db-default-out");
        var spec = GameDetector.KnownGames.Single(g => g.Id == "ff5");
        var steamRoot = Path.Combine(ws.Root, "Steam Library");
        var gameDir = SyntheticUnityFixtureBuilder.BuildSyntheticSteamGame(
            steamRoot,
            spec,
            steamBuildId: "20265087",
            customCommonFolderName: "FINAL FANTASY V PR");

        var peBytes = BuildMinimalPeWithVersionResource(
            fileVersion: (2019, 4, 26, 32270),
            productVersion: (1, 0, 0, 0),
            stringFileVersion: "2019.4.26.3227036",
            stringProductVersion: "2019.4.26.3227036",
            sectionName: ".rsrc",
            pe32Plus: true);
        await File.WriteAllBytesAsync(Path.Combine(gameDir, spec.ExeName), peBytes);

        // 1. Omitting --out uses <AGP_HOME>/ffpr-export/output/<gameId>-<steamBuildId>/.
        var defaultRes = await TestHarness.RunCliAsync(
            ["build-db", "--game", gameDir, "--jobs", "2"],
            ffprDeps: ws.DepsDir,
            agpHome: ws.AgpHomeDir);
        Assert.True(defaultRes.ExitCode == 0, $"stdout:\n{defaultRes.Stdout}\nstderr:\n{defaultRes.Stderr}");
        var expectedFromManifest = Path.Combine(ws.AgpHomeDir, "ffpr-export", "output", "ff5-20265087");
        Assert.True(File.Exists(Path.Combine(expectedFromManifest, "ff5.sqlite")));
        Assert.True(File.Exists(Path.Combine(expectedFromManifest, "ff5.sqlite.build.json")));

        // 2. Without appmanifest: --steam-buildid determines folder tag; without both -> unknown.
        var acfPath = Path.Combine(steamRoot, "steamapps", $"appmanifest_{spec.AppId}.acf");
        File.Delete(acfPath);

        var manualTagRes = await TestHarness.RunCliAsync(
            ["build-db", "--game", gameDir, "--steam-buildid", "98765432", "--jobs", "2"],
            ffprDeps: ws.DepsDir,
            agpHome: ws.AgpHomeDir);
        Assert.True(manualTagRes.ExitCode == 0, $"stdout:\n{manualTagRes.Stdout}\nstderr:\n{manualTagRes.Stderr}");
        var expectedManualDir = Path.Combine(ws.AgpHomeDir, "ffpr-export", "output", "ff5-98765432");
        Assert.True(File.Exists(Path.Combine(expectedManualDir, "ff5.sqlite")));

        var unknownTagRes = await TestHarness.RunCliAsync(
            ["build-db", "--game", gameDir, "--jobs", "2"],
            ffprDeps: ws.DepsDir,
            agpHome: ws.AgpHomeDir);
        Assert.True(unknownTagRes.ExitCode == 0, $"stdout:\n{unknownTagRes.Stdout}\nstderr:\n{unknownTagRes.Stderr}");
        var expectedUnknownDir = Path.Combine(ws.AgpHomeDir, "ffpr-export", "output", "ff5-unknown");
        Assert.True(File.Exists(Path.Combine(expectedUnknownDir, "ff5.sqlite")));

        // 3. Explicit --out still takes precedence over AGP_HOME default.
        var explicitOut = Path.Combine(ws.Root, "explicit-custom-out");
        var explicitRes = await TestHarness.RunCliAsync(
            ["build-db", "--game", gameDir, "--out", explicitOut, "--jobs", "2"],
            ffprDeps: ws.DepsDir,
            agpHome: ws.AgpHomeDir);
        Assert.True(explicitRes.ExitCode == 0, $"stdout:\n{explicitRes.Stdout}\nstderr:\n{explicitRes.Stderr}");
        Assert.True(File.Exists(Path.Combine(explicitOut, "ff5.sqlite")));
    }

    internal static byte[] BuildMinimalPeWithVersionResource(
        (ushort Major, ushort Minor, ushort Build, ushort Revision) fileVersion,
        (ushort Major, ushort Minor, ushort Build, ushort Revision) productVersion,
        string? stringFileVersion = null,
        string? stringProductVersion = null,
        string sectionName = ".rsrc",
        bool pe32Plus = true)
    {
        var versionBlob = BuildVersionResourceBlob(
            fileVersion,
            productVersion,
            stringFileVersion,
            stringProductVersion);

        const uint rsrcRva = 0x2000;
        const uint rsrcRawOffset = 0x200;
        const uint dir0Offset = 0;
        const uint dir1Offset = 24;
        const uint dir2Offset = 48;
        const uint dataEntryOffset = 72;
        const uint blobOffsetInSection = 88;

        var rsrcSection = new byte[AlignUp((int)blobOffsetInSection + versionBlob.Length, 0x200)];

        // Level 1: RT_VERSION (ID 16)
        BinaryPrimitives.WriteUInt16LittleEndian(rsrcSection.AsSpan((int)dir0Offset + 14, 2), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(rsrcSection.AsSpan((int)dir0Offset + 16, 4), 16u);
        BinaryPrimitives.WriteUInt32LittleEndian(rsrcSection.AsSpan((int)dir0Offset + 20, 4), 0x80000000u | dir1Offset);

        // Level 2: Resource ID 1
        BinaryPrimitives.WriteUInt16LittleEndian(rsrcSection.AsSpan((int)dir1Offset + 14, 2), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(rsrcSection.AsSpan((int)dir1Offset + 16, 4), 1u);
        BinaryPrimitives.WriteUInt32LittleEndian(rsrcSection.AsSpan((int)dir1Offset + 20, 4), 0x80000000u | dir2Offset);

        // Level 3: Language 0x0409
        BinaryPrimitives.WriteUInt16LittleEndian(rsrcSection.AsSpan((int)dir2Offset + 14, 2), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(rsrcSection.AsSpan((int)dir2Offset + 16, 4), 0x0409u);
        BinaryPrimitives.WriteUInt32LittleEndian(rsrcSection.AsSpan((int)dir2Offset + 20, 4), dataEntryOffset);

        // IMAGE_RESOURCE_DATA_ENTRY
        BinaryPrimitives.WriteUInt32LittleEndian(rsrcSection.AsSpan((int)dataEntryOffset + 0, 4), rsrcRva + blobOffsetInSection);
        BinaryPrimitives.WriteUInt32LittleEndian(rsrcSection.AsSpan((int)dataEntryOffset + 4, 4), (uint)versionBlob.Length);
        Buffer.BlockCopy(versionBlob, 0, rsrcSection, (int)blobOffsetInSection, versionBlob.Length);

        var pe = new byte[rsrcRawOffset + rsrcSection.Length];
        pe[0] = (byte)'M';
        pe[1] = (byte)'Z';
        const int peOff = 0x80;
        BinaryPrimitives.WriteUInt32LittleEndian(pe.AsSpan(0x3C, 4), peOff);

        pe[peOff + 0] = (byte)'P';
        pe[peOff + 1] = (byte)'E';
        pe[peOff + 2] = 0;
        pe[peOff + 3] = 0;
        BinaryPrimitives.WriteUInt16LittleEndian(pe.AsSpan(peOff + 4, 2), pe32Plus ? (ushort)0x8664 : (ushort)0x014C);
        BinaryPrimitives.WriteUInt16LittleEndian(pe.AsSpan(peOff + 6, 2), 1);

        ushort optSize = pe32Plus ? (ushort)240 : (ushort)224;
        BinaryPrimitives.WriteUInt16LittleEndian(pe.AsSpan(peOff + 20, 2), optSize);

        var optOff = peOff + 24;
        BinaryPrimitives.WriteUInt16LittleEndian(pe.AsSpan(optOff, 2), pe32Plus ? (ushort)0x020B : (ushort)0x010B);
        var numRvaOff = optOff + (pe32Plus ? 108 : 92);
        var ddOff = optOff + (pe32Plus ? 112 : 96);
        BinaryPrimitives.WriteUInt32LittleEndian(pe.AsSpan(numRvaOff, 4), 16u);
        BinaryPrimitives.WriteUInt32LittleEndian(pe.AsSpan(ddOff + 16, 4), rsrcRva);
        BinaryPrimitives.WriteUInt32LittleEndian(pe.AsSpan(ddOff + 20, 4), (uint)rsrcSection.Length);

        var secOff = optOff + optSize;
        var secNameBytes = Encoding.ASCII.GetBytes(sectionName);
        Buffer.BlockCopy(secNameBytes, 0, pe, secOff, Math.Min(8, secNameBytes.Length));
        BinaryPrimitives.WriteUInt32LittleEndian(pe.AsSpan(secOff + 8, 4), (uint)rsrcSection.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(pe.AsSpan(secOff + 12, 4), rsrcRva);
        BinaryPrimitives.WriteUInt32LittleEndian(pe.AsSpan(secOff + 16, 4), (uint)rsrcSection.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(pe.AsSpan(secOff + 20, 4), rsrcRawOffset);

        Buffer.BlockCopy(rsrcSection, 0, pe, (int)rsrcRawOffset, rsrcSection.Length);
        return pe;
    }

    private static byte[] BuildVersionResourceBlob(
        (ushort Major, ushort Minor, ushort Build, ushort Revision) fileVersion,
        (ushort Major, ushort Minor, ushort Build, ushort Revision) productVersion,
        string? stringFileVersion,
        string? stringProductVersion)
    {
        var fixedInfo = new byte[52];
        BinaryPrimitives.WriteUInt32LittleEndian(fixedInfo.AsSpan(0, 4), 0xFEEF04BDu);
        BinaryPrimitives.WriteUInt32LittleEndian(fixedInfo.AsSpan(4, 4), 0x00010000u);
        BinaryPrimitives.WriteUInt32LittleEndian(fixedInfo.AsSpan(8, 4), ((uint)fileVersion.Major << 16) | fileVersion.Minor);
        BinaryPrimitives.WriteUInt32LittleEndian(fixedInfo.AsSpan(12, 4), ((uint)fileVersion.Build << 16) | fileVersion.Revision);
        BinaryPrimitives.WriteUInt32LittleEndian(fixedInfo.AsSpan(16, 4), ((uint)productVersion.Major << 16) | productVersion.Minor);
        BinaryPrimitives.WriteUInt32LittleEndian(fixedInfo.AsSpan(20, 4), ((uint)productVersion.Build << 16) | productVersion.Revision);

        var children = new List<byte[]>();
        if (stringFileVersion is not null || stringProductVersion is not null)
        {
            var stringNodes = new List<byte[]>();
            if (stringFileVersion is not null)
            {
                stringNodes.Add(BuildVersionStringNode("FileVersion", stringFileVersion));
            }

            if (stringProductVersion is not null)
            {
                stringNodes.Add(BuildVersionStringNode("ProductVersion", stringProductVersion));
            }

            var tableNode = BuildVersionContainerNode("040904b0", 1, [], stringNodes);
            var sfiNode = BuildVersionContainerNode("StringFileInfo", 1, [], [tableNode]);
            children.Add(sfiNode);
        }

        return BuildVersionContainerNode("VS_VERSION_INFO", 0, fixedInfo, children);
    }

    private static byte[] BuildVersionStringNode(string key, string value)
    {
        var valueBytes = Encoding.Unicode.GetBytes(value + "\0");
        var charLenWithNull = (ushort)(value.Length + 1);
        return BuildVersionNodeRaw(key, wType: 1, wValueLength: charLenWithNull, valueBytes, []);
    }

    private static byte[] BuildVersionContainerNode(
        string key,
        ushort wType,
        byte[] valueBytes,
        IReadOnlyList<byte[]> children)
    {
        return BuildVersionNodeRaw(key, wType, (ushort)valueBytes.Length, valueBytes, children);
    }

    private static byte[] BuildVersionNodeRaw(
        string key,
        ushort wType,
        ushort wValueLength,
        byte[] valueBytes,
        IReadOnlyList<byte[]> children)
    {
        using var ms = new MemoryStream();
        ms.Write(new byte[6]);
        var keyBytes = Encoding.Unicode.GetBytes(key + "\0");
        ms.Write(keyBytes);
        PadTo4(ms);
        if (valueBytes.Length > 0)
        {
            ms.Write(valueBytes);
        }

        foreach (var child in children)
        {
            PadTo4(ms);
            ms.Write(child);
        }

        var result = ms.ToArray();
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(0, 2), (ushort)result.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(2, 2), wValueLength);
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(4, 2), wType);
        return result;
    }

    private static void PadTo4(MemoryStream ms)
    {
        while (ms.Length % 4 != 0)
        {
            ms.WriteByte(0);
        }
    }

    private static int AlignUp(int value, int alignment) => (value + alignment - 1) & ~(alignment - 1);
}
