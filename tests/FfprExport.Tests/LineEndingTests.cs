using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using AssetsTools.NET.Extra;
using FfprExport.Db;
using FfprExport.Gui;
using Xunit;

namespace FfprExport.Tests;

public sealed class LineEndingTests
{
    private const string ExportFixtureManifestEnvVar = "FFPR_EXPORT_FIXTURE_MANIFEST_OUT";

    private static readonly Regex VolatileTimestampPropRegex = new(
        @"(""(?:startedAt|finishedAt)""\s*:\s*"")[^""]*("")",
        RegexOptions.CultureInvariant);

    private static readonly Regex VolatileElapsedPropRegex = new(
        @"(""elapsedSeconds""\s*:\s*)-?[0-9]+(?:\.[0-9]+)?(?:[eE][+-]?[0-9]+)?",
        RegexOptions.CultureInvariant);

    private sealed record ExportFileHashEntry(
        string Path,
        long Size,
        string Sha256,
        bool Normalized,
        bool HasCr,
        bool HasBom);

    [Fact]
    public async Task TextFileWriter_ForcesLfWithoutBom_EvenWithSimulatedWindowsCrlfOptionsAndInputs()
    {
        using var ws = new TempWorkspace("ffpr-lf-writer");

        // 1. All shared JSON option singletons must pin NewLine = "\n".
        Assert.Equal("\n", FieldJsonWriter.IndentedOptions.NewLine);
        Assert.Equal("\n", FieldJsonWriter.SerializerOptions.NewLine);
        Assert.Equal("\n", FieldJsonWriter.CompactSerializerOptions.NewLine);
        Assert.Equal("\n", DbParsers.PrettyJsonOptions.NewLine);
        Assert.Equal("\n", DbParsers.CompactJsonOptions.NewLine);
        Assert.Equal("\n", DbParsers.CompactWriterOptions.NewLine);

        // 2. WriteAllText / WriteAllTextAtomic normalize Windows CRLF and lone CR inputs to LF without BOM.
        var plainPath = Path.Combine(ws.Root, "sub", "plain.txt");
        var atomicPath = Path.Combine(ws.Root, "sub", "atomic.txt");
        var crlfInput = "line1\r\nline2\rline3\r\n";

        var plainLen = TextFileWriter.WriteAllText(plainPath, crlfInput);
        var atomicLen = TextFileWriter.WriteAllTextAtomic(atomicPath, crlfInput);

        var plainBytes = await File.ReadAllBytesAsync(plainPath);
        var atomicBytes = await File.ReadAllBytesAsync(atomicPath);
        Assert.Equal(plainBytes.LongLength, plainLen);
        Assert.Equal(atomicBytes.LongLength, atomicLen);
        AssertLfWithoutBom(plainBytes, "plain.txt");
        AssertLfWithoutBom(atomicBytes, "atomic.txt");
        Assert.Equal("line1\nline2\nline3\n", Encoding.UTF8.GetString(plainBytes));
        Assert.Equal(plainBytes, atomicBytes);

        // 3. Simulate Windows JsonSerializerOptions (NewLine = "\r\n") -> WriteJsonAtomic still outputs pure LF.
        var windowsSerializerOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            NewLine = "\r\n",
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
        var samplePayload = new
        {
            title = "测试",
            nested = new { count = 2, note = "a\r\nb" },
            items = new[] { 1, 2, 3 },
        };

        var rawWindowsJson = JsonSerializer.Serialize(samplePayload, windowsSerializerOptions);
        Assert.Contains("\r\n", rawWindowsJson, StringComparison.Ordinal);

        var jsonNoTailPath = Path.Combine(ws.Root, "json-no-tail.json");
        var jsonWithTailPath = Path.Combine(ws.Root, "json-with-tail.json");
        TextFileWriter.WriteJsonAtomic(jsonNoTailPath, samplePayload, windowsSerializerOptions, trailingNewLine: false);
        TextFileWriter.WriteJsonAtomic(jsonWithTailPath, samplePayload, windowsSerializerOptions, trailingNewLine: true);

        var jsonNoTailBytes = await File.ReadAllBytesAsync(jsonNoTailPath);
        var jsonWithTailBytes = await File.ReadAllBytesAsync(jsonWithTailPath);
        AssertLfWithoutBom(jsonNoTailBytes, "json-no-tail.json");
        AssertLfWithoutBom(jsonWithTailBytes, "json-with-tail.json");
        Assert.Equal((byte)'}', jsonNoTailBytes[^1]);
        Assert.Equal((byte)'\n', jsonWithTailBytes[^1]);

        // Escaped \r\n inside JSON string values must remain intact after round-trip.
        using (var doc = JsonDocument.Parse(jsonNoTailBytes))
        {
            Assert.Equal("a\r\nb", doc.RootElement.GetProperty("nested").GetProperty("note").GetString());
        }

        // 4. Simulate Windows JsonWriterOptions (NewLine = "\r\n") -> WriteUtf8JsonAtomic & CreateUtf8JsonWriter force LF.
        var windowsWriterOptions = new JsonWriterOptions
        {
            Indented = true,
            NewLine = "\r\n",
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        var utf8JsonPath = Path.Combine(ws.Root, "utf8-json.json");
        var utf8Len = TextFileWriter.WriteUtf8JsonAtomic(
            utf8JsonPath,
            windowsWriterOptions,
            writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("key", "value");
                writer.WriteString("escaped", "x\r\ny");
                writer.WritePropertyName("arr");
                writer.WriteStartArray();
                writer.WriteNumberValue(10);
                writer.WriteNumberValue(20);
                writer.WriteEndArray();
                writer.WriteEndObject();
            });

        var utf8JsonBytes = await File.ReadAllBytesAsync(utf8JsonPath);
        Assert.Equal(utf8JsonBytes.LongLength, utf8Len);
        AssertLfWithoutBom(utf8JsonBytes, "utf8-json.json");
        Assert.Contains("\n  \"key\": \"value\",\n", Encoding.UTF8.GetString(utf8JsonBytes), StringComparison.Ordinal);

        using (var ms = new MemoryStream())
        {
            using (var writer = TextFileWriter.CreateUtf8JsonWriter(ms, windowsWriterOptions))
            {
                writer.WriteStartObject();
                writer.WriteBoolean("ok", true);
                writer.WriteEndObject();
            }

            AssertLfWithoutBom(ms.ToArray(), "in-memory Utf8JsonWriter");
        }

        // 5. CreateStreamWriter forces NewLine = "\n" without BOM for both path and Stream overloads.
        var streamPath = Path.Combine(ws.Root, "lines.jsonl");
        await using (var writer = TextFileWriter.CreateStreamWriter(streamPath, append: false))
        {
            Assert.Equal("\n", writer.NewLine);
            await writer.WriteLineAsync("""{"id":1}""");
            writer.WriteLine("""{"id":2}""");
        }

        var jsonlBytes = await File.ReadAllBytesAsync(streamPath);
        AssertLfWithoutBom(jsonlBytes, "lines.jsonl");
        Assert.Equal("{\"id\":1}\n{\"id\":2}\n", Encoding.UTF8.GetString(jsonlBytes));

        using (var ms = new MemoryStream())
        {
            using (var writer = TextFileWriter.CreateStreamWriter(ms, leaveOpen: true))
            {
                Assert.Equal("\n", writer.NewLine);
                writer.WriteLine("row1");
                writer.WriteLine("row2");
            }

            var memBytes = ms.ToArray();
            AssertLfWithoutBom(memBytes, "StreamWriter(Stream)");
            Assert.Equal("row1\nrow2\n", Encoding.UTF8.GetString(memBytes));
        }
    }

    [Fact]
    public async Task GuiSettingsAndStartupLogs_WriteLfWithoutBom()
    {
        using var ws = new TempWorkspace("ffpr-lf-gui");

        var settingsPath = Path.Combine(ws.Root, "settings.json");
        GuiSettingsStore.Save(
            settingsPath,
            new GuiSettings
            {
                OutputRoot = Path.Combine(ws.Root, "out"),
                CheckedGames = new Dictionary<string, bool>(StringComparer.Ordinal)
                {
                    ["C:\\Games\\FF5"] = true,
                },
            });

        var settingsBytes = await File.ReadAllBytesAsync(settingsPath);
        AssertLfWithoutBom(settingsBytes, "settings.json");
        Assert.Equal((byte)'\n', settingsBytes[^1]);

        var startupInfo = new GuiStartupInfo(
            Timestamp: new DateTimeOffset(2026, 11, 26, 12, 0, 0, TimeSpan.Zero),
            Version: "0.1.0",
            GitCommit: "abc1234",
            OsVersion: "TestOS",
            DotNetVersion: ".NET 10",
            ProcessBitness: "64-bit",
            CurrentDirectory: ws.Root,
            ExecutablePath: Path.Combine(ws.Root, "ffpr-export-gui.exe"),
            DpiSetting: "PerMonitorV2",
            CommandLineArgs: "--self-test");

        var logger = GuiStartupLogger.Start(
            startupInfo,
            agpHomeOverride: ws.AgpHomeDir);
        logger.WriteInfo("line one");
        logger.WriteException("TestContext", new InvalidOperationException("first\r\nsecond"));

        Assert.False(string.IsNullOrWhiteSpace(logger.LogFilePath));
        var logBytes = await File.ReadAllBytesAsync(logger.LogFilePath!);
        AssertLfWithoutBom(logBytes, "gui startup log");
    }

    [Fact]
    public async Task EndToEndBuildDbAndExport_WriteLfToolFilesAndPreserveVerbatimCrlfTextAssets()
    {
        using var ws = new TempWorkspace("ffpr-lf-e2e");
        var spec = GameDetector.KnownGames.Single(g => g.Id == "ff5");
        var gameDir = SyntheticUnityFixtureBuilder.BuildSyntheticSteamGame(
            ws.Root,
            spec,
            steamBuildId: "20265087",
            customCommonFolderName: "FINAL FANTASY V PR");

        var peBytes = BuildDbTests.BuildMinimalPeWithVersionResource(
            fileVersion: (2019, 4, 26, 32270),
            productVersion: (1, 0, 0, 0),
            stringFileVersion: "2019.4.26.3227036",
            stringProductVersion: "2019.4.26.3227036",
            sectionName: ".rsrc",
            pe32Plus: true);
        await File.WriteAllBytesAsync(Path.Combine(gameDir, spec.ExeName), peBytes);

        // Replace sharedassets0.assets with a CRLF TextAsset payload to verify TextAsset bytes stay verbatim.
        var crlfPayload = Encoding.UTF8.GetBytes("{\"audio\":true}\r\nsecond_line\r\n");
        var strippedTextType = new AssetsTools.NET.TypeTreeType
        {
            TypeId = (int)AssetClassID.TextAsset,
            IsStrippedType = false,
            ScriptTypeIndex = 0xFFFF,
            ScriptIdHash = AssetsTools.NET.Hash128.NewBlankHash(),
            TypeHash = AssetsTools.NET.Hash128.NewBlankHash(),
            TypeBlobIsDefinition = true,
            TypeBlob = new AssetsTools.NET.TypeTreeBlob { Nodes = [], StringBufferBytes = [] },
            TypeDependencies = [],
        };
        var shared0Bytes = SyntheticUnityFixtureBuilder.BuildAssetsFile(
            [strippedTextType],
            [new SyntheticAssetSpec(10, (int)AssetClassID.TextAsset, 0, 0xFFFF, SyntheticUnityFixtureBuilder.BuildTextAssetBytes("sead.win.config", crlfPayload))],
            typeTreeEnabled: false);
        await File.WriteAllBytesAsync(Path.Combine(gameDir, spec.DataDirName, "sharedassets0.assets"), shared0Bytes);

        var outDir = Path.Combine(ws.Root, "build-out");
        var res = await TestHarness.RunCliAsync(
            ["build-db", "--game", gameDir, "--out", outDir, "--jobs", "2", "--keep-json"],
            ffprDeps: ws.DepsDir);
        Assert.True(res.ExitCode == 0, $"stdout:\n{res.Stdout}\nstderr:\n{res.Stderr}");

        // Verbatim TextAsset must retain its exact CRLF bytes.
        var rawTextAssetPath = Path.Combine(outDir, "json", "export", "_data", "sharedassets0.assets", "sead.win.config");
        Assert.Equal(crlfPayload, await File.ReadAllBytesAsync(rawTextAssetPath));

        // Every tool-generated text file must be UTF-8 without BOM and pure LF.
        string[] generatedToolFiles =
        [
            Path.Combine(outDir, "manifest.json"),
            Path.Combine(outDir, "ff5.sqlite.build.json"),
            Path.Combine(outDir, "ff5.sqlite.manifest.json"),
            Path.Combine(outDir, "json", "inventory", "inventory.jsonl"),
            Path.Combine(outDir, "json", "inventory", "summary.json"),
            Path.Combine(outDir, "json", "inventory", "manifest.json"),
            Path.Combine(outDir, "json", "export", "manifest.json"),
            Path.Combine(outDir, "json", "export", "Res", "Config", "demo_config.asset.json"),
            Path.Combine(outDir, "json", "export", "_monobehaviour", "bundles", "ui_assets_all_0002.json"),
            Path.Combine(outDir, "json", "export", "_monobehaviour", "data", "level0.json"),
        ];

        foreach (var file in generatedToolFiles)
        {
            Assert.True(File.Exists(file), $"缺少预期输出文件：{file}");
            var bytes = await File.ReadAllBytesAsync(file);
            AssertLfWithoutBom(bytes, Path.GetRelativePath(outDir, file).Replace('\\', '/'));
        }
    }

    [Fact]
    public async Task ExportFixture_GeneratesCrossPlatformHashManifest()
    {
        using var ws = new TempWorkspace("ffpr-export-fixture-hash");
        var spec = GameDetector.KnownGames.Single(g => g.Id == "ff5");
        var gameDir = SyntheticUnityFixtureBuilder.BuildSyntheticSteamGame(
            ws.Root,
            spec,
            steamBuildId: "20265087",
            customCommonFolderName: "FINAL FANTASY V PR");

        var peBytes = BuildDbTests.BuildMinimalPeWithVersionResource(
            fileVersion: (2019, 4, 26, 32270),
            productVersion: (1, 0, 0, 0),
            stringFileVersion: "2019.4.26.3227036",
            stringProductVersion: "2019.4.26.3227036",
            sectionName: ".rsrc",
            pe32Plus: true);
        await File.WriteAllBytesAsync(Path.Combine(gameDir, spec.ExeName), peBytes);

        var run1Entries = await RunSyntheticExportAndCollectHashesAsync(gameDir, Path.Combine(ws.Root, "run1"), ws.DepsDir);
        var run2Entries = await RunSyntheticExportAndCollectHashesAsync(gameDir, Path.Combine(ws.Root, "run2"), ws.DepsDir);

        Assert.True(run1Entries.Count >= 11, $"合成导出应至少包含 11 个输出文件，实际仅 {run1Entries.Count} 个");
        Assert.Equal(run1Entries, run2Entries);

        foreach (var entry in run1Entries)
        {
            Assert.False(entry.HasCr, $"合成导出文件包含 CR（\\r）：{entry.Path}");
            Assert.False(entry.HasBom, $"合成导出文件包含 UTF-8 BOM：{entry.Path}");
        }

        // Verify key export and inventory outputs are covered in the manifest.
        var paths = run1Entries.Select(e => e.Path).ToHashSet(StringComparer.Ordinal);
        Assert.Contains("inventory/inventory.jsonl", paths);
        Assert.Contains("inventory/summary.json", paths);
        Assert.Contains("inventory/manifest.json", paths);
        Assert.Contains("export/manifest.json", paths);
        Assert.Contains("export/Res/Config/demo_config.asset.json", paths);
        Assert.Contains("export/_monobehaviour/bundles/ui_assets_all_0002.json", paths);
        Assert.Contains("export/_monobehaviour/data/level0.json", paths);

        var manifestOut = Environment.GetEnvironmentVariable(ExportFixtureManifestEnvVar);
        if (!string.IsNullOrWhiteSpace(manifestOut))
        {
            var payload = new
            {
                format = 1,
                game_id = spec.Id,
                steam_build_id = "20265087",
                file_count = run1Entries.Count,
                files = run1Entries.Select(e => new
                {
                    path = e.Path,
                    size = e.Size,
                    sha256 = e.Sha256,
                    normalized = e.Normalized,
                    has_cr = e.HasCr,
                    has_bom = e.HasBom,
                }).ToArray(),
            };

            TextFileWriter.WriteJsonAtomic(
                manifestOut.Trim(),
                payload,
                FieldJsonWriter.SerializerOptions,
                trailingNewLine: true);
        }
    }

    private static async Task<IReadOnlyList<ExportFileHashEntry>> RunSyntheticExportAndCollectHashesAsync(
        string gameDir,
        string outRoot,
        string depsDir)
    {
        var invOut = Path.Combine(outRoot, "inventory");
        var expOut = Path.Combine(outRoot, "export");

        var invRes = await TestHarness.RunCliAsync(
            ["inventory", "--game", gameDir, "--out", invOut, "--jobs", "2"],
            ffprDeps: depsDir);
        Assert.True(invRes.ExitCode == 0, $"inventory 失败：\nstdout:\n{invRes.Stdout}\nstderr:\n{invRes.Stderr}");

        var expRes = await TestHarness.RunCliAsync(
            ["export", "--game", gameDir, "--out", expOut, "--jobs", "2"],
            ffprDeps: depsDir);
        Assert.True(expRes.ExitCode == 0, $"export 失败：\nstdout:\n{expRes.Stdout}\nstderr:\n{expRes.Stderr}");

        var volatileFiles = new HashSet<string>(StringComparer.Ordinal)
        {
            "inventory/manifest.json",
            "inventory/summary.json",
            "export/manifest.json",
        };

        var allFiles = Directory.EnumerateFiles(outRoot, "*", SearchOption.AllDirectories)
            .Select(full => (FullPath: full, RelPath: Path.GetRelativePath(outRoot, full).Replace('\\', '/')))
            .OrderBy(item => item.RelPath, StringComparer.Ordinal)
            .ToArray();

        var entries = new List<ExportFileHashEntry>(allFiles.Length);
        foreach (var (fullPath, relPath) in allFiles)
        {
            var rawBytes = await File.ReadAllBytesAsync(fullPath);
            var hasBom = HasUtf8Bom(rawBytes);
            var hasCr = Array.IndexOf(rawBytes, (byte)'\r') >= 0;

            var normalized = volatileFiles.Contains(relPath);
            var bytesToHash = normalized ? ScrubVolatileTimingFieldsPreservingLineEndings(rawBytes) : rawBytes;
            var sha256 = Convert.ToHexStringLower(SHA256.HashData(bytesToHash));

            entries.Add(new ExportFileHashEntry(
                relPath,
                bytesToHash.LongLength,
                sha256,
                normalized,
                hasCr,
                hasBom));
        }

        return entries;
    }

    private static byte[] ScrubVolatileTimingFieldsPreservingLineEndings(byte[] rawBytes)
    {
        // Scrub only wall-clock timing values while leaving all line-ending bytes and BOM untouched.
        var text = TextFileWriter.Utf8WithoutBom.GetString(rawBytes);
        var scrubbed = VolatileTimestampPropRegex.Replace(text, "$1<NORMALIZED>$2");
        scrubbed = VolatileElapsedPropRegex.Replace(scrubbed, "${1}0");
        return TextFileWriter.Utf8WithoutBom.GetBytes(scrubbed);
    }

    private static bool HasUtf8Bom(byte[] bytes)
    {
        return bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
    }

    private static void AssertLfWithoutBom(byte[] bytes, string label)
    {
        Assert.False(HasUtf8Bom(bytes), $"{label} 不得包含 UTF-8 BOM");
        Assert.DoesNotContain((byte)'\r', bytes);
    }
}
