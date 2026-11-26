using System.Text.Json;
using Xunit;

namespace FfprExport.Tests;

public sealed class InventoryAndExportTests
{
    [Fact]
    public async Task InventoryAndExport_EndToEndWithSpacesResumeAndForceGuardrails()
    {
        using var ws = new TempWorkspace("ffpr e2e spaces");
        var spec = GameDetector.KnownGames.Single(g => g.Id == "ff5");
        var gameDir = SyntheticUnityFixtureBuilder.BuildSyntheticSteamGame(
            ws.Root,
            spec,
            steamBuildId: "20265087",
            customCommonFolderName: "FINAL FANTASY V PR");

        var invOut = Path.Combine(ws.Root, "my outputs", "ff5 inventory");
        var expOut = Path.Combine(ws.Root, "my outputs", "ff5 export");

        // 1. Run inventory on paths containing spaces.
        var invResult = await TestHarness.RunCliAsync(
            ["inventory", "--game", gameDir, "--out", invOut, "--jobs", "2"],
            ffprDeps: ws.DepsDir);
        Assert.Equal(0, invResult.ExitCode);
        Assert.True(File.Exists(Path.Combine(invOut, "inventory.jsonl")));
        Assert.True(File.Exists(Path.Combine(invOut, "summary.json")));
        Assert.True(File.Exists(Path.Combine(invOut, "manifest.json")));

        using (var summaryDoc = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(invOut, "summary.json"))))
        {
            var root = summaryDoc.RootElement;
            Assert.Equal("ff5", root.GetProperty("game").GetProperty("id").GetString());
            Assert.Equal("20265087", root.GetProperty("steamBuildId").GetString());
            Assert.Equal("2019.4.26f1", root.GetProperty("unityVersion").GetString());
            Assert.Equal(2, root.GetProperty("totals").GetProperty("bundleCount").GetInt32());
            Assert.Equal(2, root.GetProperty("totals").GetProperty("dataFileCount").GetInt32());
            Assert.Equal(0, root.GetProperty("totals").GetProperty("failureCount").GetInt32());
        }

        // Inventory refuses non-empty directory without --force.
        var invNonEmpty = await TestHarness.RunCliAsync(
            ["--lang", "zh-Hans", "inventory", "--game", gameDir, "--out", invOut],
            ffprDeps: ws.DepsDir);
        Assert.Equal(2, invNonEmpty.ExitCode);
        Assert.Contains("输出目录非空", invNonEmpty.Stderr, StringComparison.Ordinal);

        // 2. Run export on paths containing spaces.
        var expResult = await TestHarness.RunCliAsync(
            ["export", "--game", gameDir, "--out", expOut, "--jobs", "2"],
            ffprDeps: ws.DepsDir);
        Assert.Equal(0, expResult.ExitCode);

        // Verify TextAssets exported verbatim at expected layout, while audio .mab.bytes was skipped.
        Assert.Equal("id,name\n1,Potion\n", await File.ReadAllTextAsync(Path.Combine(expOut, "Data", "Master", "item.csv")));
        Assert.Equal("{\"width\":16,\"height\":16,\"layers\":[]}", await File.ReadAllTextAsync(Path.Combine(expOut, "Res", "Map", "Map_00001", "Map_00001_1", "tilemap.json")));
        Assert.Equal("{\"entries\":[\"a\",\"b\"]}", await File.ReadAllTextAsync(Path.Combine(expOut, "AssetsPath.json")));
        Assert.Equal("{\"audio\":true}", await File.ReadAllTextAsync(Path.Combine(expOut, "_data", "sharedassets0.assets", "sead.win.config")));
        Assert.False(File.Exists(Path.Combine(expOut, "Res", "Audio", "bgm_01.mab.bytes")));

        // Verify standalone container MonoBehaviour exported as JSON with TypeTree fields.
        var standaloneMbPath = Path.Combine(expOut, "Res", "Config", "demo_config.asset.json");
        Assert.True(File.Exists(standaloneMbPath));
        using (var standaloneDoc = JsonDocument.Parse(await File.ReadAllTextAsync(standaloneMbPath)))
        {
            var r = standaloneDoc.RootElement;
            Assert.Equal("TypeTree", r.GetProperty("typeSource").GetString());
            Assert.Equal("DemoComponent", r.GetProperty("scriptClass").GetString());
            Assert.Equal("Last.Demo", r.GetProperty("scriptNamespace").GetString());
            var data = r.GetProperty("data");
            Assert.Equal(42, data.GetProperty("level").GetInt32());
            Assert.Equal("独立配置", data.GetProperty("label").GetString());
            Assert.Equal(3, data.GetProperty("values").GetArrayLength());
        }

        // Verify component MonoBehaviour grouped into _monobehaviour/bundles/<stem>.json with GameObject name.
        var groupedMbPath = Path.Combine(expOut, "_monobehaviour", "bundles", "ui_assets_all_0002.json");
        Assert.True(File.Exists(groupedMbPath));
        using (var groupedDoc = JsonDocument.Parse(await File.ReadAllTextAsync(groupedMbPath)))
        {
            var arr = groupedDoc.RootElement.GetProperty("monoBehaviours");
            Assert.Equal(1, arr.GetArrayLength());
            var item = arr[0];
            Assert.Equal("SamplePanelRoot", item.GetProperty("gameObjectName").GetString());
            Assert.Equal("DemoComponent", item.GetProperty("scriptClass").GetString());
            Assert.Equal(7, item.GetProperty("data").GetProperty("level").GetInt32());
            Assert.Equal("面板组件", item.GetProperty("data").GetProperty("label").GetString());
        }

        // Verify *_Data MonoBehaviour exported into _monobehaviour/data/level0.json.
        Assert.True(File.Exists(Path.Combine(expOut, "_monobehaviour", "data", "level0.json")));

        // Verify manifest contents.
        using (var manifestDoc = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(expOut, "manifest.json"))))
        {
            var root = manifestDoc.RootElement;
            Assert.Equal("ffpr-export", root.GetProperty("tool").GetString());
            Assert.Equal("export", root.GetProperty("command").GetString());
            Assert.Equal("20265087", root.GetProperty("steamBuildId").GetString());
            Assert.Equal("2019.4.26f1", root.GetProperty("unityVersion").GetString());
            Assert.Equal(0, root.GetProperty("totals").GetProperty("failedAssets").GetInt32());
            Assert.Equal(2, root.GetProperty("monoBehaviourTypeSources").GetProperty("typeTree").GetInt32());
            Assert.Equal(1, root.GetProperty("monoBehaviourTypeSources").GetProperty("baseOnly").GetInt32());
        }

        // 3. Resume mode: delete one file, re-run without --force -> only the deleted file is newly written.
        var itemCsvPath = Path.Combine(expOut, "Data", "Master", "item.csv");
        File.Delete(itemCsvPath);
        var resumeResult = await TestHarness.RunCliAsync(
            ["--lang", "zh-Hans", "export", "--game", gameDir, "--out", expOut, "--jobs", "2"],
            ffprDeps: ws.DepsDir);
        Assert.Equal(0, resumeResult.ExitCode);
        Assert.Contains("续跑模式", resumeResult.Stdout, StringComparison.Ordinal);
        Assert.True(File.Exists(itemCsvPath));
        using (var resumeManifest = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(expOut, "manifest.json"))))
        {
            var totals = resumeManifest.RootElement.GetProperty("totals");
            Assert.Equal(1, totals.GetProperty("newlyExportedAssets").GetInt32());
            Assert.True(totals.GetProperty("resumedAssets").GetInt32() >= 5);
        }

        // 4. --force guardrails:
        // 4a. Foreign non-empty directory is refused and untouched.
        var foreignDir = Path.Combine(ws.Root, "foreign-dir");
        Directory.CreateDirectory(foreignDir);
        await File.WriteAllTextAsync(Path.Combine(foreignDir, "keep.txt"), "do-not-delete");
        var foreignForce = await TestHarness.RunCliAsync(
            ["--lang", "zh-Hans", "export", "--game", gameDir, "--out", foreignDir, "--force"],
            ffprDeps: ws.DepsDir);
        Assert.Equal(2, foreignForce.ExitCode);
        Assert.Contains("不是 ffpr-export 的输出目录", foreignForce.Stderr, StringComparison.Ordinal);
        Assert.Equal("do-not-delete", await File.ReadAllTextAsync(Path.Combine(foreignDir, "keep.txt")));

        // 4b. Running `export --force` against an `inventory` directory is refused.
        var wrongCommand = await TestHarness.RunCliAsync(
            ["--lang", "zh-Hans", "export", "--game", gameDir, "--out", invOut, "--force"],
            ffprDeps: ws.DepsDir);
        Assert.Equal(2, wrongCommand.ExitCode);
        Assert.Contains("不能由 export 清空或复用", wrongCommand.Stderr, StringComparison.Ordinal);

        // 4c. --out overlapping --game is refused.
        var overlapGame = await TestHarness.RunCliAsync(
            ["--lang", "zh-Hans", "export", "--game", gameDir, "--out", Path.Combine(gameDir, "sub")],
            ffprDeps: ws.DepsDir);
        Assert.Equal(2, overlapGame.ExitCode);
        Assert.Contains("不能与游戏目录重叠", overlapGame.Stderr, StringComparison.Ordinal);

        // 4d. --out pointing to protected .deps/steamapps is refused.
        var protectedDeps = await TestHarness.RunCliAsync(
            ["--lang", "zh-Hans", "export", "--game", gameDir, "--out", Path.Combine(ws.DepsDir, "steamapps", "out")],
            ffprDeps: ws.DepsDir);
        Assert.Equal(2, protectedDeps.ExitCode);
        Assert.Contains("受保护目录", protectedDeps.Stderr, StringComparison.Ordinal);

        // 4e. --force succeeds on own export directory and preserves a sibling inventory subdirectory.
        var nestedInvDir = Path.Combine(expOut, "nested-inv");
        var nestedInvRes = await TestHarness.RunCliAsync(
            ["inventory", "--game", gameDir, "--out", nestedInvDir],
            ffprDeps: ws.DepsDir);
        Assert.Equal(0, nestedInvRes.ExitCode);
        var forceOwn = await TestHarness.RunCliAsync(
            ["--lang", "zh-Hans", "export", "--game", gameDir, "--out", expOut, "--force"],
            ffprDeps: ws.DepsDir);
        Assert.Equal(0, forceOwn.ExitCode);
        Assert.Contains("已清空输出目录", forceOwn.Stdout, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(nestedInvDir, "summary.json")));
    }

    [Fact]
    public async Task Export_RecordsSingleAssetAndSingleBundleFailuresWithoutAborting()
    {
        using var ws = new TempWorkspace("ffpr-fault-tolerance");
        var spec = GameDetector.KnownGames.Single(g => g.Id == "ff6");
        var gameDir = SyntheticUnityFixtureBuilder.BuildSyntheticSteamGame(
            ws.Root,
            spec,
            steamBuildId: "20265119",
            includeCorruptedBundle: true,
            includeCorruptedTextAsset: true);

        var expOut = Path.Combine(ws.Root, "out");
        var result = await TestHarness.RunCliAsync(
            ["export", "--game", gameDir, "--out", expOut, "--jobs", "2"],
            ffprDeps: ws.DepsDir);

        // Exit code 1 indicates partial failure while valid resources still exported.
        Assert.Equal(1, result.ExitCode);
        Assert.True(File.Exists(Path.Combine(expOut, "Data", "Master", "item.csv")));
        Assert.True(File.Exists(Path.Combine(expOut, "Res", "Config", "demo_config.asset.json")));

        using var manifestDoc = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(expOut, "manifest.json")));
        var root = manifestDoc.RootElement;
        var failures = root.GetProperty("failures");
        Assert.Equal(2, failures.GetArrayLength());

        var failedSources = failures.EnumerateArray()
            .Select(f => (f.GetProperty("sourceFile").GetString(), f.GetProperty("assetType").GetString()))
            .ToArray();
        Assert.Contains(failedSources, x => x.Item1!.Contains("corrupted_assets_all_9999.bundle", StringComparison.Ordinal) && x.Item2 == "AssetBundle");
        Assert.Contains(failedSources, x => x.Item1!.Contains("master_assets_all_0001.bundle", StringComparison.Ordinal) && x.Item2 == "TextAsset");
    }
}
