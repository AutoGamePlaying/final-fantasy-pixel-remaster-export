using AssetsTools.NET.Extra;
using Xunit;

namespace FfprExport.Tests;

public sealed class GameDetectionAndCatalogTests
{
    [Theory]
    [InlineData("ff1", "FINAL FANTASY", 1173770)]
    [InlineData("ff2", "FINAL FANTASY II", 1173780)]
    [InlineData("ff3", "FINAL FANTASY III", 1173790)]
    [InlineData("ff4", "FINAL FANTASY IV", 1173800)]
    [InlineData("ff5", "FINAL FANTASY V", 1173810)]
    [InlineData("ff6", "FINAL FANTASY VI", 1173820)]
    public void Detect_IdentifiesAllSixGamesUnityVersionAndSteamBuildId(string expectedId, string expectedTitle, int expectedAppId)
    {
        using var ws = new TempWorkspace($"ffpr-detect-{expectedId}");
        var spec = GameDetector.KnownGames.Single(g => g.Id == expectedId);
        var expectedBuild = $"2026{expectedAppId % 10000:D4}";
        var gameDir = SyntheticUnityFixtureBuilder.BuildSyntheticSteamGame(ws.Root, spec, steamBuildId: expectedBuild);

        var detected = GameDetector.Detect(gameDir);

        Assert.Equal(expectedId, detected.Spec.Id);
        Assert.Equal(expectedTitle, detected.Spec.Title);
        Assert.Equal(expectedAppId, detected.Spec.AppId);
        Assert.Equal(SyntheticUnityFixtureBuilder.DefaultUnityVersion, detected.UnityVersion);
        Assert.Equal(expectedBuild, detected.SteamBuildId);
        Assert.NotNull(detected.AppManifestPath);
    }

    [Fact]
    public void Detect_HandlesMissingAppManifestAndUnknownGameDirectory()
    {
        using var ws = new TempWorkspace("ffpr-detect-edge");
        var spec = GameDetector.KnownGames.Single(g => g.Id == "ff5");
        var gameDir = SyntheticUnityFixtureBuilder.BuildSyntheticSteamGame(ws.Root, spec);

        // Delete appmanifest so SteamBuildId becomes null gracefully without throwing.
        var manifestPath = Path.Combine(ws.Root, "steamapps", $"appmanifest_{spec.AppId}.acf");
        File.Delete(manifestPath);
        var detected = GameDetector.Detect(gameDir);
        Assert.Null(detected.SteamBuildId);

        // Unknown directory must fail cleanly.
        var unknownDir = Path.Combine(ws.Root, "not-a-game");
        Directory.CreateDirectory(unknownDir);
        var ex = Assert.Throws<InvalidDataException>(() => GameDetector.Detect(unknownDir));
        Assert.Contains("无法从目录识别", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseSteamBuildId_HandlesCommentsEscapesAndNestedBlocks()
    {
        var acf = """
            // Steam app manifest
            "AppState"
            {
                "appid"     "1173810"
                "LauncherPath"  "C:\\Program Files (x86)\\Steam\\steam.exe"
                "UserConfig"
                {
                    "buildid"   "99999999"
                }
                "buildid"   "20265087"
            }
            """;

        // Must pick top-level AppState -> buildid, ignoring nested UserConfig -> buildid.
        Assert.Equal("20265087", GameDetector.ParseSteamBuildId(acf));
        Assert.Null(GameDetector.ParseSteamBuildId("\"Other\" { \"buildid\" \"123\" }"));
    }

    [Fact]
    public void AddressablesCatalog_LoadsSyntheticBundlesAndDetectsFontAndUnindexedBundles()
    {
        using var ws = new TempWorkspace("ffpr-catalog");
        var spec = GameDetector.KnownGames.Single(g => g.Id == "ff5");
        var gameDir = SyntheticUnityFixtureBuilder.BuildSyntheticSteamGame(ws.Root, spec);

        // Drop an extra unindexed .bundle into StandaloneWindows64.
        var extraBundlePath = Path.Combine(gameDir, spec.DataDirName, "StreamingAssets", "aa", "StandaloneWindows64", "extra_unindexed.bundle");
        File.WriteAllBytes(extraBundlePath, [1, 2, 3]);

        var detected = GameDetector.Detect(gameDir);
        var snapshot = AddressablesCatalog.Load(detected);

        Assert.Equal("AddressablesMainContentCatalog", snapshot.LocatorId);
        Assert.Equal(2, snapshot.Bundles.Count);
        Assert.All(snapshot.Bundles, b => Assert.True(b.ExistsOnDisk));
        Assert.Single(snapshot.UnindexedBundlesOnDisk);
        Assert.Contains("extra_unindexed.bundle", snapshot.UnindexedBundlesOnDisk[0], StringComparison.Ordinal);
        Assert.Single(snapshot.EncryptedFontBundles);
        Assert.Contains("font_en.bundle", snapshot.EncryptedFontBundles[0], StringComparison.Ordinal);
    }

    [Fact]
    public void AssetClassifier_ClassifiesTypesAndRejectsPathTraversal()
    {
        Assert.Equal(AssetClassifier.DecisionExportText, AssetClassifier.Classify((int)AssetClassID.TextAsset, "Assets/GameAssets/Serial/Data/Master/item.csv").Decision);
        Assert.Equal(AssetClassifier.DecisionSkipAudio, AssetClassifier.Classify((int)AssetClassID.TextAsset, "Assets/GameAssets/Serial/Res/Audio/bgm.mab.bytes").Decision);
        Assert.Equal(AssetClassifier.DecisionSkipAudio, AssetClassifier.Classify((int)AssetClassID.TextAsset, "Assets/GameAssets/Common/Res/Audio/se.sab.bytes").Decision);
        Assert.Equal(AssetClassifier.DecisionExportJson, AssetClassifier.Classify((int)AssetClassID.MonoBehaviour).Decision);
        Assert.Equal(AssetClassifier.DecisionSkipImage, AssetClassifier.Classify((int)AssetClassID.Texture2D).Decision);
        Assert.Equal(AssetClassifier.DecisionSkipShader, AssetClassifier.Classify((int)AssetClassID.Shader).Decision);
        Assert.Equal(AssetClassifier.DecisionSkipFont, AssetClassifier.Classify((int)AssetClassID.Font).Decision);
        Assert.Equal(AssetClassifier.DecisionSkipVideo, AssetClassifier.Classify((int)AssetClassID.VideoClip).Decision);

        Assert.Equal("Data/Master/item.csv", AssetClassifier.MapContainerPathToRelativeOutput("Assets/GameAssets/Serial/Data/Master/item.csv"));
        Assert.Equal("AssetsPath.json", AssetClassifier.MapContainerPathToRelativeOutput("Assets/GameAssets/AssetsPath.json"));
        Assert.Equal("Common/Data/test.json", AssetClassifier.MapContainerPathToRelativeOutput("Assets/GameAssets/Common/Data/test.json"));

        Assert.Throws<InvalidDataException>(() => AssetClassifier.MapContainerPathToRelativeOutput("Assets/GameAssets/Serial/../../escape.txt"));
    }
}
