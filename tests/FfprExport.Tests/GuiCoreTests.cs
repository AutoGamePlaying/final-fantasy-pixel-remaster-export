using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using FfprExport.Gui;
using Xunit;

namespace FfprExport.Tests;

public sealed class GuiCoreTests
{
    [Fact]
    public void GuiGameCatalogShim_MatchesCoreGameDetectorKnownGames()
    {
        var shimPath = Path.Combine(TestHarness.FindRepoRoot(), "src", "FfprExport.Gui", "Shared", "GameCatalogShim.cs");
        Assert.True(File.Exists(shimPath), $"缺少 GUI 游戏清单共享桩：{shimPath}");

        var content = File.ReadAllText(shimPath, Encoding.UTF8);
        var entryRegex = new Regex(
            @"new\(""([^""]+)"",\s*""([^""]+)"",\s*(\d+),\s*""([^""]+)"",\s*""([^""]+)""\)",
            RegexOptions.CultureInvariant);

        var parsed = entryRegex.Matches(content)
            .Select(m => new GameSpec(
                m.Groups[1].Value,
                m.Groups[2].Value,
                int.Parse(m.Groups[3].Value),
                m.Groups[4].Value,
                m.Groups[5].Value))
            .ToArray();

        Assert.Equal(GameDetector.KnownGames, parsed);
    }

    [Fact]
    public void GuiProject_LinksSharedSteamLocatorWithoutCopying()
    {
        var repoRoot = TestHarness.FindRepoRoot();
        var guiCsproj = File.ReadAllText(Path.Combine(repoRoot, "src", "FfprExport.Gui", "FfprExport.Gui.csproj"), Encoding.UTF8);

        Assert.Contains(@"<Compile Include=""..\FfprExport\AgpPaths.cs""", guiCsproj, StringComparison.Ordinal);
        Assert.Contains(@"<Compile Include=""..\FfprExport\AppLocalization.cs""", guiCsproj, StringComparison.Ordinal);
        Assert.Contains(@"<Compile Include=""..\FfprExport\SteamLocator.cs""", guiCsproj, StringComparison.Ordinal);
        Assert.Contains(@"<EmbeddedResource Include=""..\FfprExport\Resources\Strings.resx""", guiCsproj, StringComparison.Ordinal);
        Assert.Contains(@"<EmbeddedResource Include=""..\FfprExport\Resources\Strings.zh-Hans.resx""", guiCsproj, StringComparison.Ordinal);
        Assert.Contains(@"<EmbeddedResource Include=""..\FfprExport\Resources\Strings.ja.resx""", guiCsproj, StringComparison.Ordinal);
        Assert.Contains("<OutputType>WinExe</OutputType>", guiCsproj, StringComparison.Ordinal);
        Assert.Contains("<TargetFramework>net10.0-windows</TargetFramework>", guiCsproj, StringComparison.Ordinal);
        Assert.Contains("<UseWindowsForms>true</UseWindowsForms>", guiCsproj, StringComparison.Ordinal);
        Assert.Contains("<EnableWindowsTargeting>true</EnableWindowsTargeting>", guiCsproj, StringComparison.Ordinal);
        Assert.Contains("<AssemblyName>ffpr-export-gui</AssemblyName>", guiCsproj, StringComparison.Ordinal);
        Assert.Contains("<TreatWarningsAsErrors>true</TreatWarningsAsErrors>", guiCsproj, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(repoRoot, "src", "FfprExport.Gui", "AgpPaths.cs")));
        Assert.False(File.Exists(Path.Combine(repoRoot, "src", "FfprExport.Gui", "AppLocalization.cs")));
        Assert.False(File.Exists(Path.Combine(repoRoot, "src", "FfprExport.Gui", "SteamLocator.cs")));
    }

    [Fact]
    public void AgpPaths_ResolvesUnifiedRootPriorityAcrossPlatformsAndFallbacks()
    {
        using var langScope = AppLocalization.PushLanguage(AppLanguage.ZhHans);
        // Test process itself must already be isolated with AGP_HOME pointing into a temporary directory.
        var activeAgpHome = Environment.GetEnvironmentVariable(AgpPaths.AgpHomeEnvVar);
        Assert.False(string.IsNullOrWhiteSpace(activeAgpHome), "测试进程必须通过 AGP_HOME 隔离");

        static (bool Writable, string? Error) AlwaysWritable(string _) => (true, null);

        // 1. AGP_HOME takes top priority on both Windows and Linux/macOS and acts directly as the "agp" layer.
        var winFromEnv = AgpPaths.ResolveAgpHome(
            isWindowsOverride: true,
            getEnvironmentVariable: key => key switch
            {
                AgpPaths.AgpHomeEnvVar => @"D:\CustomAgp\",
                AgpPaths.LocalAppDataEnvVar => @"C:\Users\Alice\AppData\Local",
                _ => null,
            },
            getSpecialFolder: _ => @"C:\Users\Alice\AppData\Local",
            probeWritable: AlwaysWritable);
        Assert.True(winFromEnv.Succeeded);
        Assert.Equal(AgpRootSource.AgpHomeEnv, winFromEnv.Source);
        Assert.False(winFromEnv.UsedFallback);
        Assert.Equal(@"D:\CustomAgp", winFromEnv.AgpHome);
        Assert.Equal(@"D:\CustomAgp\tmp", AgpPaths.GetTempDirectory(winFromEnv));
        Assert.Equal(@"D:\CustomAgp\ffpr-export\output", AgpPaths.GetOutputRoot(winFromEnv));
        Assert.Equal(@"D:\CustomAgp\ffpr-export\output\ff5-20265087", AgpPaths.GetGameOutputDirectory("ff5", "20265087", winFromEnv));
        Assert.Equal(@"D:\CustomAgp\ffpr-export\logs", AgpPaths.GetLogsDirectory(winFromEnv));
        Assert.Equal(@"D:\CustomAgp\ffpr-export\settings.json", AgpPaths.GetSettingsPath(winFromEnv));

        var unixFromEnv = AgpPaths.ResolveAgpHome(
            isWindowsOverride: false,
            getEnvironmentVariable: key => key switch
            {
                AgpPaths.AgpHomeEnvVar => "/tmp/custom-agp/",
                AgpPaths.XdgDataHomeEnvVar => "/home/alice/.xdg-data",
                AgpPaths.HomeEnvVar => "/home/alice",
                _ => null,
            },
            getSpecialFolder: _ => "/home/alice",
            probeWritable: AlwaysWritable);
        Assert.True(unixFromEnv.Succeeded);
        Assert.Equal(AgpRootSource.AgpHomeEnv, unixFromEnv.Source);
        Assert.Equal("/tmp/custom-agp", unixFromEnv.AgpHome);
        Assert.Equal("/tmp/custom-agp/tmp", AgpPaths.GetTempDirectory(unixFromEnv));
        Assert.Equal("/tmp/custom-agp/ffpr-export/output", AgpPaths.GetOutputRoot(unixFromEnv));
        Assert.Equal("/tmp/custom-agp/ffpr-export/logs", AgpPaths.GetLogsDirectory(unixFromEnv));
        Assert.Equal("/tmp/custom-agp/ffpr-export/settings.json", AgpPaths.GetSettingsPath(unixFromEnv));

        // 2. Windows without AGP_HOME -> %LOCALAPPDATA%\agp (SpecialFolder first, then LOCALAPPDATA env).
        var winDefault = AgpPaths.ResolveAgpHome(
            isWindowsOverride: true,
            getEnvironmentVariable: _ => null,
            getSpecialFolder: f => f == Environment.SpecialFolder.LocalApplicationData
                ? @"C:\Users\Alice\AppData\Local"
                : null,
            probeWritable: AlwaysWritable);
        Assert.True(winDefault.Succeeded);
        Assert.Equal(AgpRootSource.WindowsLocalAppData, winDefault.Source);
        Assert.False(winDefault.UsedFallback);
        Assert.Equal(@"C:\Users\Alice\AppData\Local\agp", winDefault.AgpHome);
        Assert.Equal(@"C:\Users\Alice\AppData\Local\agp\ffpr-export\output", AgpPaths.GetOutputRoot(winDefault));
        Assert.Equal(@"C:\Users\Alice\AppData\Local\agp\ffpr-export\logs", AgpPaths.GetLogsDirectory(winDefault));
        Assert.Equal(@"C:\Users\Alice\AppData\Local\agp\ffpr-export\settings.json", AgpPaths.GetSettingsPath(winDefault));

        var winEnvFallback = AgpPaths.ResolveAgpHome(
            isWindowsOverride: true,
            getEnvironmentVariable: key => key == AgpPaths.LocalAppDataEnvVar ? @"C:\Users\Bob\AppData\Local" : null,
            getSpecialFolder: _ => throw new UnauthorizedAccessException("Shell folder unavailable"),
            probeWritable: AlwaysWritable);
        Assert.True(winEnvFallback.Succeeded);
        Assert.Equal(AgpRootSource.WindowsLocalAppData, winEnvFallback.Source);
        Assert.Equal(@"C:\Users\Bob\AppData\Local\agp", winEnvFallback.AgpHome);

        // 3a. Linux/macOS without AGP_HOME, with XDG_DATA_HOME -> $XDG_DATA_HOME/agp.
        var unixXdg = AgpPaths.ResolveAgpHome(
            isWindowsOverride: false,
            getEnvironmentVariable: key => key switch
            {
                AgpPaths.XdgDataHomeEnvVar => "/var/data/xdg/",
                AgpPaths.HomeEnvVar => "/home/alice",
                _ => null,
            },
            getSpecialFolder: _ => "/home/alice",
            probeWritable: AlwaysWritable);
        Assert.True(unixXdg.Succeeded);
        Assert.Equal(AgpRootSource.XdgDataHome, unixXdg.Source);
        Assert.Equal("/var/data/xdg/agp", unixXdg.AgpHome);
        Assert.Equal("/var/data/xdg/agp/ffpr-export/output", AgpPaths.GetOutputRoot(unixXdg));

        // 3b. Linux/macOS without AGP_HOME and without XDG_DATA_HOME -> ~/.local/share/agp.
        var unixDefault = AgpPaths.ResolveAgpHome(
            isWindowsOverride: false,
            getEnvironmentVariable: key => key == AgpPaths.HomeEnvVar ? "/home/alice" : null,
            getSpecialFolder: _ => null,
            probeWritable: AlwaysWritable);
        Assert.True(unixDefault.Succeeded);
        Assert.Equal(AgpRootSource.UnixDefaultLocalShare, unixDefault.Source);
        Assert.Equal("/home/alice/.local/share/agp", unixDefault.AgpHome);
        Assert.Equal("/home/alice/.local/share/agp/ffpr-export/output", AgpPaths.GetOutputRoot(unixDefault));
        Assert.Equal("/home/alice/.local/share/agp/ffpr-export/logs", AgpPaths.GetLogsDirectory(unixDefault));
        Assert.Equal("/home/alice/.local/share/agp/ffpr-export/settings.json", AgpPaths.GetSettingsPath(unixDefault));

        // 4. Fallback to portable <exeDir>\agp-data when LocalAppData & LocalLow fail.
        var portableFallback = AgpPaths.ResolveAgpHome(
            isWindowsOverride: true,
            getEnvironmentVariable: _ => null,
            getSpecialFolder: _ => throw new UnauthorizedAccessException("无法读取 LocalAppData"),
            getBaseDirectory: () => @"D:\Tools\ffpr",
            probeWritable: path => path == @"D:\Tools\ffpr\agp-data" ? (true, null) : (false, "拒绝访问"));
        Assert.True(portableFallback.Succeeded);
        Assert.Equal(AgpRootSource.PortableBaseDirectory, portableFallback.Source);
        Assert.True(portableFallback.UsedFallback);
        Assert.Equal(@"D:\Tools\ffpr\agp-data", portableFallback.AgpHome);
        Assert.Contains("%LOCALAPPDATA% 不可写", portableFallback.WarningMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void AgpPaths_CandidateOrderExplicitAgpHomeFailureAllFailedAndSavedLocation()
    {
        using var langScope = AppLocalization.PushLanguage(AppLanguage.ZhHans);
        Func<string, string?> winEnv = key => key switch
        {
            AgpPaths.LocalAppDataEnvVar => @"C:\Users\Alice\AppData\Local",
            AgpPaths.UserProfileEnvVar => @"C:\Users\Alice",
            _ => null,
        };
        Func<Environment.SpecialFolder, string?> winFolders = folder => folder switch
        {
            Environment.SpecialFolder.LocalApplicationData => @"C:\Users\Alice\AppData\Local",
            Environment.SpecialFolder.UserProfile => @"C:\Users\Alice",
            _ => null,
        };

        // 1. Verify exact Windows candidate probing order when every candidate is rejected:
        //    %LOCALAPPDATA%\agp -> %USERPROFILE%\AppData\LocalLow\agp -> <exeDir>\agp-data -> %USERPROFILE%\agp -> agp-location.txt
        var probedAllFailed = new List<string>();
        var allFailed = AgpPaths.ResolveAgpHome(
            isWindowsOverride: true,
            getEnvironmentVariable: winEnv,
            getSpecialFolder: winFolders,
            getBaseDirectory: () => @"D:\Tools\ffpr",
            probeWritable: path =>
            {
                probedAllFailed.Add(path);
                return (false, "Access denied");
            },
            readLocationFile: _ => @"E:\SavedAgp");

        Assert.False(allFailed.Succeeded);
        Assert.False(allFailed.ExplicitAgpHomeFailed);
        Assert.Equal(AgpRootSource.None, allFailed.Source);
        Assert.Equal(
            [
                @"C:\Users\Alice\AppData\Local\agp",
                @"C:\Users\Alice\AppData\LocalLow\agp",
                @"D:\Tools\ffpr\agp-data",
                @"C:\Users\Alice\agp",
                @"E:\SavedAgp",
            ],
            probedAllFailed);
        Assert.Contains("AGP_HOME", allFailed.WarningMessage, StringComparison.Ordinal);
        Assert.Throws<IOException>(() => AgpPaths.RequireWritableAgpHome(
            isWindowsOverride: true,
            getEnvironmentVariable: winEnv,
            getSpecialFolder: winFolders,
            getBaseDirectory: () => @"D:\Tools\ffpr",
            probeWritable: _ => (false, "Access denied")));

        // 2. When %LOCALAPPDATA% is unwritable, LocalLow is selected next.
        var localLowHit = AgpPaths.ResolveAgpHome(
            isWindowsOverride: true,
            getEnvironmentVariable: winEnv,
            getSpecialFolder: winFolders,
            getBaseDirectory: () => @"D:\Tools\ffpr",
            probeWritable: path => path == @"C:\Users\Alice\AppData\LocalLow\agp" ? (true, null) : (false, "拒绝访问"));
        Assert.True(localLowHit.Succeeded);
        Assert.Equal(AgpRootSource.WindowsLocalLowAppData, localLowHit.Source);
        Assert.True(localLowHit.UsedFallback);
        Assert.Equal(@"C:\Users\Alice\AppData\LocalLow\agp", localLowHit.AgpHome);

        // 3. When LocalAppData + LocalLow fail, <exeDir>\agp-data is selected.
        var portableHit = AgpPaths.ResolveAgpHome(
            isWindowsOverride: true,
            getEnvironmentVariable: winEnv,
            getSpecialFolder: winFolders,
            getBaseDirectory: () => @"D:\Tools\ffpr",
            probeWritable: path => path == @"D:\Tools\ffpr\agp-data" ? (true, null) : (false, "拒绝访问"));
        Assert.True(portableHit.Succeeded);
        Assert.Equal(AgpRootSource.PortableBaseDirectory, portableHit.Source);
        Assert.Equal(@"D:\Tools\ffpr\agp-data", portableHit.AgpHome);
        Assert.Contains("数据目录：D:\\Tools\\ffpr\\agp-data", AgpPaths.FormatUiNoticeBanner(portableHit), StringComparison.Ordinal);
        Assert.Contains("%LOCALAPPDATA% 不可写", AgpPaths.FormatUiNoticeBanner(portableHit), StringComparison.Ordinal);

        // 4. When LocalAppData + LocalLow + agp-data fail, %USERPROFILE%\agp is selected.
        var profileHit = AgpPaths.ResolveAgpHome(
            isWindowsOverride: true,
            getEnvironmentVariable: winEnv,
            getSpecialFolder: winFolders,
            getBaseDirectory: () => @"D:\Tools\ffpr",
            probeWritable: path => path == @"C:\Users\Alice\agp" ? (true, null) : (false, "拒绝访问"));
        Assert.True(profileHit.Succeeded);
        Assert.Equal(AgpRootSource.UserProfileAgp, profileHit.Source);
        Assert.Equal(@"C:\Users\Alice\agp", profileHit.AgpHome);
        Assert.Contains("数据目录：C:\\Users\\Alice\\agp", AgpPaths.FormatUiNoticeBanner(profileHit), StringComparison.Ordinal);
        Assert.Contains("%LOCALAPPDATA% 不可写", AgpPaths.FormatUiNoticeBanner(profileHit), StringComparison.Ordinal);

        // 5. When all 4 defaults fail, saved agp-location.txt path is tried and used if writable.
        var savedHit = AgpPaths.ResolveAgpHome(
            isWindowsOverride: true,
            getEnvironmentVariable: winEnv,
            getSpecialFolder: winFolders,
            getBaseDirectory: () => @"D:\Tools\ffpr",
            probeWritable: path => path == @"E:\SavedAgp" ? (true, null) : (false, "拒绝访问"),
            readLocationFile: path => path == @"D:\Tools\ffpr\agp-location.txt" ? "E:\\SavedAgp\n" : null);
        Assert.True(savedHit.Succeeded);
        Assert.Equal(AgpRootSource.SavedLocationFile, savedHit.Source);
        Assert.Equal(@"E:\SavedAgp", savedHit.AgpHome);

        // 6. Explicit AGP_HOME unwritable -> fails immediately without probing any fallback candidate!
        var explicitProbed = new List<string>();
        var explicitDenied = AgpPaths.ResolveAgpHome(
            isWindowsOverride: true,
            getEnvironmentVariable: key => key == AgpPaths.AgpHomeEnvVar ? @"Z:\LockedAgp" : winEnv(key),
            getSpecialFolder: winFolders,
            getBaseDirectory: () => @"D:\Tools\ffpr",
            probeWritable: path =>
            {
                explicitProbed.Add(path);
                return path == @"Z:\LockedAgp" ? (false, "目录只读") : (true, null);
            });
        Assert.False(explicitDenied.Succeeded);
        Assert.True(explicitDenied.ExplicitAgpHomeFailed);
        Assert.Equal([@"Z:\LockedAgp"], explicitProbed);
        Assert.Contains("AGP_HOME", explicitDenied.WarningMessage, StringComparison.Ordinal);
        Assert.Contains(@"Z:\LockedAgp", explicitDenied.WarningMessage, StringComparison.Ordinal);
        Assert.Contains("目录只读", explicitDenied.WarningMessage, StringComparison.Ordinal);

        // 7. Real filesystem write probe writes & deletes probe file under <root>/tmp, and rejects file-blocked path.
        using var ws = new TempWorkspace("ffpr-agp-probe");
        var writableRoot = Path.Combine(ws.Root, "writable-agp");
        Assert.True(AgpPaths.TryProbeWritableDirectory(writableRoot, out var okErr), $"预期可写失败：{okErr}");
        Assert.Null(okErr);
        Assert.True(Directory.Exists(writableRoot));
        Assert.Empty(Directory.EnumerateFiles(writableRoot, "*", SearchOption.AllDirectories));

        var blockedRoot = Path.Combine(ws.Root, "blocked-file");
        File.WriteAllText(blockedRoot, "not a directory", OutputGuard.Utf8WithoutBom);
        Assert.False(AgpPaths.TryProbeWritableDirectory(blockedRoot, out var blockedErr));
        Assert.False(string.IsNullOrWhiteSpace(blockedErr));

        // 8. Saving & reading agp-location.txt works cleanly.
        var exeDir = Path.Combine(ws.Root, "app");
        Directory.CreateDirectory(exeDir);
        Assert.True(AgpPaths.TrySaveCustomLocation(exeDir, writableRoot, out var saveErr), $"保存 agp-location.txt 失败：{saveErr}");
        Assert.Equal(writableRoot + "\n", File.ReadAllText(Path.Combine(exeDir, AgpPaths.LocationFileName), Encoding.UTF8));

        var fromRealLocationFile = AgpPaths.ResolveAgpHome(
            isWindowsOverride: OperatingSystem.IsWindows(),
            getEnvironmentVariable: _ => null,
            getSpecialFolder: _ => null,
            getBaseDirectory: () => exeDir,
            probeWritable: path => string.Equals(path, writableRoot, StringComparison.OrdinalIgnoreCase)
                ? (true, null)
                : (false, "模拟不可写"));
        Assert.True(fromRealLocationFile.Succeeded);
        Assert.Equal(AgpRootSource.SavedLocationFile, fromRealLocationFile.Source);
        Assert.Equal(writableRoot, fromRealLocationFile.AgpHome);
    }

    [Fact]
    public void ProcessIntegrity_ClassifiesRidsInjectsLowLevelSelectsLocalLowAndFormatsUiNotices()
    {
        using var langScope = AppLocalization.PushLanguage(AppLanguage.ZhHans);
        // 1. RID classification and injectable detector.
        Assert.Equal(ProcessIntegrityKind.Untrusted, ProcessIntegrity.ClassifyRid(0x0000));
        Assert.Equal(ProcessIntegrityKind.Low, ProcessIntegrity.ClassifyRid(0x1000));
        Assert.Equal(ProcessIntegrityKind.Low, ProcessIntegrity.ClassifyRid(0x1FFF));
        Assert.Equal(ProcessIntegrityKind.Medium, ProcessIntegrity.ClassifyRid(0x2000));
        Assert.Equal(ProcessIntegrityKind.Medium, ProcessIntegrity.ClassifyRid(0x2100));
        Assert.Equal(ProcessIntegrityKind.High, ProcessIntegrity.ClassifyRid(0x3000));
        Assert.Equal(ProcessIntegrityKind.System, ProcessIntegrity.ClassifyRid(0x4000));

        var injectedLow = ProcessIntegrity.Detect(() => ProcessIntegrity.FromRid(ProcessIntegrity.SecurityMandatoryLowRid));
        Assert.Equal(ProcessIntegrityKind.Low, injectedLow.Kind);
        Assert.True(injectedLow.IsLowOrUntrusted);
        Assert.Equal("Low (0x1000)", injectedLow.LogLabel);
        Assert.Contains("Low", injectedLow.DisplayLabel, StringComparison.Ordinal);

        // 2. Low-IL Windows resolution: %LOCALAPPDATA%\agp denied -> falls into %USERPROFILE%\AppData\LocalLow\agp.
        var probedLow = new List<string>();
        var lowResolution = AgpPaths.ResolveAgpHome(
            isWindowsOverride: true,
            getEnvironmentVariable: key => key switch
            {
                AgpPaths.LocalAppDataEnvVar => @"C:\Users\Player\AppData\Local",
                AgpPaths.UserProfileEnvVar => @"C:\Users\Player",
                _ => null,
            },
            getSpecialFolder: folder => folder switch
            {
                Environment.SpecialFolder.LocalApplicationData => @"C:\Users\Player\AppData\Local",
                Environment.SpecialFolder.UserProfile => @"C:\Users\Player",
                _ => null,
            },
            getBaseDirectory: () => @"D:\Downloads\ffpr-export",
            probeWritable: path =>
            {
                probedLow.Add(path);
                return path == @"C:\Users\Player\AppData\LocalLow\agp"
                    ? (true, null)
                    : (false, "Access to the path is denied.");
            },
            detectIntegrity: () => ProcessIntegrityInfo.Low);

        Assert.True(lowResolution.Succeeded);
        Assert.Equal(AgpRootSource.WindowsLocalLowAppData, lowResolution.Source);
        Assert.Equal(@"C:\Users\Player\AppData\LocalLow\agp", lowResolution.AgpHome);
        Assert.Equal(
            [
                @"C:\Users\Player\AppData\Local\agp",
                @"C:\Users\Player\AppData\LocalLow\agp",
            ],
            probedLow);

        // 3. UI banner, status bar, and all-failed dialog guidance under Low IL.
        var lowBanner = AgpPaths.FormatUiNoticeBanner(lowResolution, ProcessIntegrityInfo.Low);
        Assert.NotNull(lowBanner);
        Assert.Contains("低完整性级别", lowBanner, StringComparison.Ordinal);
        Assert.Contains("解除锁定", lowBanner, StringComparison.Ordinal);
        Assert.Contains("资源管理器重新解压", lowBanner, StringComparison.Ordinal);
        Assert.Contains("LocalLow", lowBanner, StringComparison.Ordinal);
        Assert.DoesNotContain("管理员", lowBanner, StringComparison.Ordinal);

        var (dataDirStatus, integrityStatus) = GuiStartupLogger.FormatStatusBarSummary(lowResolution, ProcessIntegrityInfo.Low);
        Assert.Contains(@"C:\Users\Player\AppData\LocalLow\agp", dataDirStatus, StringComparison.Ordinal);
        Assert.Contains("低完整性", integrityStatus, StringComparison.Ordinal);

        // Normal Medium IL + primary LocalAppData produces no warning banner.
        var normalMedium = AgpPaths.ResolveAgpHome(
            isWindowsOverride: true,
            getEnvironmentVariable: _ => null,
            getSpecialFolder: f => f == Environment.SpecialFolder.LocalApplicationData ? @"C:\Users\Player\AppData\Local" : null,
            probeWritable: _ => (true, null),
            detectIntegrity: () => ProcessIntegrityInfo.Medium);
        Assert.Null(AgpPaths.FormatUiNoticeBanner(normalMedium, ProcessIntegrityInfo.Medium));

        // All-failed GUI dialog mentions security software / whitelist / folder selection and admin only as last resort.
        var allFailedMedium = AgpPaths.ResolveAgpHome(
            isWindowsOverride: true,
            getEnvironmentVariable: _ => null,
            getSpecialFolder: f => f == Environment.SpecialFolder.LocalApplicationData ? @"C:\Users\Player\AppData\Local" : null,
            getBaseDirectory: () => @"C:\Program Files\ffpr-export",
            probeWritable: _ => (false, "拒绝访问"),
            detectIntegrity: () => ProcessIntegrityInfo.Medium);
        var guiFailDialog = AgpPaths.FormatGuiAllFailedDialogMessage(allFailedMedium, ProcessIntegrityInfo.Medium);
        Assert.Contains("安全软件", guiFailDialog, StringComparison.Ordinal);
        Assert.Contains("白名单", guiFailDialog, StringComparison.Ordinal);
        Assert.Contains("选择文件夹", guiFailDialog, StringComparison.Ordinal);
        Assert.Contains("agp-location.txt", guiFailDialog, StringComparison.Ordinal);
        Assert.Contains("最后手段", guiFailDialog, StringComparison.Ordinal);
    }

    [Fact]
    public void GuiGameScanner_DiscoversSteamGamesAndValidatesManualDirectory()
    {
        using var langScope = AppLocalization.PushLanguage(AppLanguage.ZhHans);
        using var ws = new TempWorkspace("ffpr-gui-scanner");
        var steamRoot = Path.Combine(ws.Root, "Steam");
        var ff1Dir = Path.Combine(steamRoot, "steamapps", "common", "FINAL FANTASY PR");
        var ff5Dir = Path.Combine(steamRoot, "steamapps", "common", "FINAL FANTASY V PR");
        Directory.CreateDirectory(Path.Combine(ff1Dir, "FINAL FANTASY_Data"));
        Directory.CreateDirectory(Path.Combine(ff5Dir, "FINAL FANTASY V_Data"));

        WriteAppManifest(steamRoot, 1173770, "FINAL FANTASY", "FINAL FANTASY PR", "20249397");
        WriteAppManifest(steamRoot, 1173810, "FINAL FANTASY V", "FINAL FANTASY V PR", "20265087");

        var discovered = GuiGameScanner.DiscoverGamesInSteamRoots([steamRoot]);
        Assert.Equal(2, discovered.Count);
        Assert.Equal("FF1", discovered[0].GameCode);
        Assert.Equal("20249397", discovered[0].BuildIdDisplay);
        Assert.False(discovered[0].IsManual);
        Assert.Equal("FF5", discovered[1].GameCode);
        Assert.Equal("20265087", discovered[1].BuildIdDisplay);

        // Manual inspection accepts quoted path and reads Steam buildid from ../../appmanifest_*.acf.
        var manualFf5 = GuiGameScanner.InspectManualGameDirectory($"  \"{ff5Dir}\"  ");
        Assert.Equal("ff5", manualFf5.Spec.Id);
        Assert.Equal("20265087", manualFf5.SteamBuildId);
        Assert.True(manualFf5.IsManual);

        // Manual standalone directory without Steam appmanifest still succeeds with unknown buildid.
        var standaloneFf3 = Path.Combine(ws.Root, "Standalone FF3");
        Directory.CreateDirectory(Path.Combine(standaloneFf3, "FINAL FANTASY III_Data"));
        var manualFf3 = GuiGameScanner.InspectManualGameDirectory(standaloneFf3);
        Assert.Equal("ff3", manualFf3.Spec.Id);
        Assert.Null(manualFf3.SteamBuildId);
        Assert.Equal("未知", manualFf3.BuildIdDisplay);

        // Directory with game exe but missing *_Data reports specific missing data directory error.
        var brokenFf4 = Path.Combine(ws.Root, "Broken FF4");
        Directory.CreateDirectory(brokenFf4);
        File.WriteAllBytes(Path.Combine(brokenFf4, "FINAL FANTASY IV.exe"), [0x4D, 0x5A]);
        var missingDataEx = Assert.Throws<DirectoryNotFoundException>(() => GuiGameScanner.InspectManualGameDirectory(brokenFf4));
        Assert.Contains("FINAL FANTASY IV_Data", missingDataEx.Message, StringComparison.Ordinal);

        // Unrelated directory is rejected.
        var randomDir = Path.Combine(ws.Root, "Not A Game");
        Directory.CreateDirectory(randomDir);
        Assert.Throws<InvalidDataException>(() => GuiGameScanner.InspectManualGameDirectory(randomDir));
        Assert.Throws<DirectoryNotFoundException>(() => GuiGameScanner.InspectManualGameDirectory(Path.Combine(ws.Root, "Missing")));
    }

    [Fact]
    public void GuiOutputPlanner_ComputesDefaultPathsDetectsDirectoryStatesAndPlansSafely()
    {
        using var langScope = AppLocalization.PushLanguage(AppLanguage.ZhHans);
        using var ws = new TempWorkspace("ffpr-gui-output-plan");

        var defaultFromAgpEnv = GuiOutputPlanner.GetDefaultOutputRoot(
            getEnvironmentVariable: key => key == AgpPaths.AgpHomeEnvVar ? ws.AgpHomeDir : null);
        Assert.Equal(Path.Combine(ws.AgpHomeDir, "ffpr-export", "output"), defaultFromAgpEnv);

        var defaultWin = GuiOutputPlanner.GetDefaultOutputRoot(
            isWindowsOverride: true,
            getEnvironmentVariable: _ => null,
            getSpecialFolder: f => f == Environment.SpecialFolder.LocalApplicationData
                ? @"C:\Users\Alice\AppData\Local"
                : null,
            probeWritable: _ => (true, null));
        Assert.Equal(@"C:\Users\Alice\AppData\Local\agp\ffpr-export\output", defaultWin);

        Assert.Equal("ff5-20265087", GuiOutputPlanner.BuildGameFolderName("FF5", "20265087"));
        Assert.Equal("ff5-unknown", GuiOutputPlanner.BuildGameFolderName("ff5", null));
        Assert.Equal("ff2-build_1_2", GuiOutputPlanner.BuildGameFolderName("ff2", " build:1/2 "));

        var outRoot = Path.Combine(ws.AgpHomeDir, "ffpr-export", "output");
        var ff1Spec = GameDetector.KnownGames.Single(g => g.Id == "ff1");
        var ff5Spec = GameDetector.KnownGames.Single(g => g.Id == "ff5");
        var ff6Spec = GameDetector.KnownGames.Single(g => g.Id == "ff6");

        var ff1Game = new GuiGameEntry(ff1Spec, "FINAL FANTASY", Path.Combine(ws.Root, "games", "ff1"), "20249397", null, false);
        var ff5Game = new GuiGameEntry(ff5Spec, "FINAL FANTASY V", Path.Combine(ws.Root, "games", "ff5"), "20265087", null, false);
        var ff6Game = new GuiGameEntry(ff6Spec, "FINAL FANTASY VI", Path.Combine(ws.Root, "games", "ff6"), null, null, false);

        // ff1 target: missing -> EmptyOrMissing, force=false
        // ff5 target: own build-db manifest -> OwnBuildDbDirectory, force=true
        var ff5Target = Path.Combine(outRoot, "ff5-20265087");
        Directory.CreateDirectory(ff5Target);
        File.WriteAllText(
            Path.Combine(ff5Target, "manifest.json"),
            """{"tool":"ffpr-export","command":"build-db"}""",
            OutputGuard.Utf8WithoutBom);

        // ff6 target: foreign non-empty file -> ForeignNonEmptyDirectory, force=false (requires GUI confirm)
        var ff6Target = Path.Combine(outRoot, "ff6-unknown");
        Directory.CreateDirectory(ff6Target);
        File.WriteAllText(Path.Combine(ff6Target, "personal-notes.txt"), "keep or confirm", OutputGuard.Utf8WithoutBom);

        var plans = GuiOutputPlanner.CreateExportPlan(outRoot, [ff1Game, ff5Game, ff6Game]);
        Assert.Equal(3, plans.Count);

        Assert.Equal(Path.Combine(outRoot, "ff1-20249397"), plans[0].OutputDirectory);
        Assert.Equal(OutputDirectoryState.EmptyOrMissing, plans[0].DirectoryState);
        Assert.False(plans[0].UseForceFlag);

        Assert.Equal(ff5Target, plans[1].OutputDirectory);
        Assert.Equal(OutputDirectoryState.OwnBuildDbDirectory, plans[1].DirectoryState);
        Assert.True(plans[1].UseForceFlag);

        Assert.Equal(ff6Target, plans[2].OutputDirectory);
        Assert.Equal(OutputDirectoryState.ForeignNonEmptyDirectory, plans[2].DirectoryState);
        Assert.False(plans[2].UseForceFlag);

        // Confirmed clearing removes foreign contents and leaves target empty.
        GuiOutputPlanner.ClearConfirmedForeignDirectory(ff6Target, [ff1Game.GameDirectory, ff5Game.GameDirectory, ff6Game.GameDirectory]);
        Assert.Equal(OutputDirectoryState.EmptyOrMissing, GuiOutputPlanner.InspectOutputDirectoryState(ff6Target));

        // Overlapping output directory with game directory is rejected.
        Assert.Throws<ArgumentException>(() =>
            GuiOutputPlanner.CreateExportPlan(ff1Game.GameDirectory, [ff1Game]));

        // Duplicate target mapping is rejected.
        var duplicateFf1 = ff1Game with { GameDirectory = Path.Combine(ws.Root, "games", "ff1-copy") };
        Assert.Throws<ArgumentException>(() =>
            GuiOutputPlanner.CreateExportPlan(outRoot, [ff1Game, duplicateFf1]));

        // Output preview reflects single vs multi selection.
        var singlePreview = GuiOutputPlanner.FormatOutputPreview(outRoot, [ff5Game]);
        Assert.Contains("ff5-20265087", singlePreview, StringComparison.Ordinal);
        var multiPreview = GuiOutputPlanner.FormatOutputPreview(outRoot, [ff1Game, ff5Game, ff6Game]);
        Assert.Contains("3 个子目录", multiPreview, StringComparison.Ordinal);

        // "打开输出文件夹" opens the actual directory used (selected result -> sole result -> current root).
        var customRoot = Path.Combine(ws.Root, "Custom Browsed Root");
        Directory.CreateDirectory(customRoot);
        Assert.Equal(
            ff5Target,
            GuiOutputPlanner.ResolveFolderToOpen(ff5Target, [ff5Target, ff6Target], customRoot));
        Assert.Equal(
            ff5Target,
            GuiOutputPlanner.ResolveFolderToOpen(null, [ff5Target], customRoot));
        Assert.Equal(
            customRoot,
            GuiOutputPlanner.ResolveFolderToOpen(null, [ff5Target, ff6Target], customRoot));
        Assert.Equal(
            customRoot,
            GuiOutputPlanner.ResolveFolderToOpen(Path.Combine(ws.Root, "missing-result"), null, customRoot));
        Assert.Equal(
            outRoot,
            GuiOutputPlanner.ResolveFolderToOpen(Path.Combine(outRoot, "missing-child"), null, Path.Combine(ws.Root, "missing-root")));
        Assert.Null(
            GuiOutputPlanner.ResolveFolderToOpen(null, null, Path.Combine(ws.Root, "missing-root")));
    }

    [Fact]
    public void BuildReportReader_ParsesBuildJsonAndEvaluatesAllVerdicts()
    {
        using var langScope = AppLocalization.PushLanguage(AppLanguage.ZhHans);
        using var ws = new TempWorkspace("ffpr-gui-report");
        var ff5Spec = GameDetector.KnownGames.Single(g => g.Id == "ff5");
        var ff5Game = new GuiGameEntry(ff5Spec, "FINAL FANTASY V", Path.Combine(ws.Root, "ff5-game"), "20265087", null, false);

        // 1. Matched
        var matchedDir = Path.Combine(ws.Root, "out-matched");
        WriteSyntheticBuildReport(
            matchedDir,
            "ff5",
            "completed",
            "20265087",
            "1111111111111111111111111111111111111111111111111111111111111111",
            "2222222222222222222222222222222222222222222222222222222222222222",
            "matched",
            "与登记的版本一致",
            "file",
            "2026-11-25",
            fileSha256MismatchOnly: false,
            tableDiffCount: 0);

        Assert.True(BuildReportReader.TryReadReportFromDirectory(matchedDir, "ff5", out var matchedReport, out var matchedErr));
        Assert.Null(matchedErr);
        Assert.NotNull(matchedReport);
        var matchedOutcome = BuildReportReader.EvaluateOutcome(ff5Game, matchedDir, exitCode: 0, cancelled: false, matchedReport);
        Assert.Equal(KnownBuildVerdictKind.Matched, matchedOutcome.Verdict);
        Assert.Equal("✓", matchedOutcome.VerdictSymbol);
        Assert.Equal("与登记的版本一致", matchedOutcome.VerdictShortText);
        Assert.Contains("2026-11-25", matchedOutcome.VerdictDetailText, StringComparison.Ordinal);
        Assert.Equal(Path.Combine(matchedDir, "ff5.sqlite"), matchedOutcome.SqlitePath);

        // 2. New game version (also verify wildcard *.build.json fallback when preferred name differs)
        var newVerDir = Path.Combine(ws.Root, "out-newver");
        WriteSyntheticBuildReport(
            newVerDir,
            "ff5",
            "completed",
            "20999999",
            "3333333333333333333333333333333333333333333333333333333333333333",
            "4444444444444444444444444444444444444444444444444444444444444444",
            "new_game_version",
            "新的游戏版本，未登记",
            "embedded",
            null,
            fileSha256MismatchOnly: false,
            tableDiffCount: 0,
            customReportFileName: "custom.build.json");

        Assert.True(BuildReportReader.TryReadReportFromDirectory(newVerDir, "ff5", out var newVerReport, out _));
        var newVerOutcome = BuildReportReader.EvaluateOutcome(ff5Game, newVerDir, exitCode: 0, cancelled: false, newVerReport);
        Assert.Equal(KnownBuildVerdictKind.NewGameVersion, newVerOutcome.Verdict);
        Assert.Equal("✓", newVerOutcome.VerdictSymbol);
        Assert.Equal("新版本，未登记", newVerOutcome.VerdictShortText);
        Assert.Equal("20999999", newVerOutcome.EffectiveBuildId);

        // 3. Hash mismatch with table diffs and partial warning (exit code 1)
        var mismatchDir = Path.Combine(ws.Root, "out-mismatch");
        WriteSyntheticBuildReport(
            mismatchDir,
            "ff5",
            "completed",
            "20265087",
            "5555555555555555555555555555555555555555555555555555555555555555",
            "6666666666666666666666666666666666666666666666666666666666666666",
            "hash_mismatch",
            "游戏版本已登记，但数据库哈希不同",
            "embedded",
            "2026-11-25",
            fileSha256MismatchOnly: false,
            tableDiffCount: 2);

        Assert.True(BuildReportReader.TryReadReportFromDirectory(mismatchDir, "ff5", out var mismatchReport, out _));
        var mismatchOutcome = BuildReportReader.EvaluateOutcome(ff5Game, mismatchDir, exitCode: 1, cancelled: false, mismatchReport);
        Assert.Equal(KnownBuildVerdictKind.HashMismatch, mismatchOutcome.Verdict);
        Assert.Equal("✗", mismatchOutcome.VerdictSymbol);
        Assert.Contains("哈希不同", mismatchOutcome.VerdictShortText, StringComparison.Ordinal);
        Assert.Contains("部分资源告警", mismatchOutcome.VerdictShortText, StringComparison.Ordinal);
        Assert.Contains("差异表 2 张", mismatchOutcome.VerdictDetailText, StringComparison.Ordinal);

        // 4. Incomplete sentinel report & cancelled & malformed JSON
        var inProgressDir = Path.Combine(ws.Root, "out-inprogress");
        WriteSyntheticBuildReport(
            inProgressDir,
            "ff5",
            "in_progress",
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            fileSha256MismatchOnly: false,
            tableDiffCount: 0);
        Assert.True(BuildReportReader.TryReadReportFromDirectory(inProgressDir, "ff5", out var sentinelReport, out _));
        var failedOutcome = BuildReportReader.EvaluateOutcome(ff5Game, inProgressDir, exitCode: 0, cancelled: false, sentinelReport);
        Assert.Equal(KnownBuildVerdictKind.Failed, failedOutcome.Verdict);
        Assert.Equal("✗", failedOutcome.VerdictSymbol);

        var cancelOutcome = BuildReportReader.EvaluateOutcome(ff5Game, matchedDir, exitCode: -1, cancelled: true, report: null);
        Assert.Equal(KnownBuildVerdictKind.Cancelled, cancelOutcome.Verdict);
        Assert.Equal("✗", cancelOutcome.VerdictSymbol);
        Assert.Equal("已取消", cancelOutcome.VerdictShortText);

        var badJsonDir = Path.Combine(ws.Root, "out-badjson");
        Directory.CreateDirectory(badJsonDir);
        File.WriteAllText(Path.Combine(badJsonDir, "ff5.sqlite.build.json"), "{not-valid-json", OutputGuard.Utf8WithoutBom);
        Assert.False(BuildReportReader.TryReadReportFromDirectory(badJsonDir, "ff5", out var badReport, out var badError));
        Assert.Null(badReport);
        Assert.Contains("ff5.sqlite.build.json", badError, StringComparison.Ordinal);

        // Clipboard formatting includes all reports with hashes.
        var clipboardText = BuildReportReader.FormatClipboardHashText([matchedOutcome, mismatchOutcome]);
        Assert.Contains("1111111111111111111111111111111111111111111111111111111111111111", clipboardText, StringComparison.Ordinal);
        Assert.Contains("6666666666666666666666666666666666666666666666666666666666666666", clipboardText, StringComparison.Ordinal);
        Assert.Contains("✓ 与登记的版本一致", clipboardText, StringComparison.Ordinal);
    }

    [Fact]
    public void CliCommandHelper_BuildsArgsLocatesCliParsesStepsAndConfiguresUtf8()
    {
        using var ws = new TempWorkspace("ffpr-gui-cli-helper");
        var binDir = Path.Combine(ws.Root, "dist-dir");
        Directory.CreateDirectory(binDir);

        // Missing CLI executable gives Chinese error naming ffpr-export.exe.
        var (missingResolved, expectedPath) = CliCommandHelper.FindCliExecutable(binDir);
        Assert.Null(missingResolved);
        Assert.Equal(Path.Combine(binDir, "ffpr-export.exe"), expectedPath);
        var missingMsg = CliCommandHelper.FormatMissingCliMessage(expectedPath);
        Assert.Contains("ffpr-export.exe", missingMsg, StringComparison.Ordinal);
        Assert.Contains(expectedPath, missingMsg, StringComparison.Ordinal);

        // Present CLI executable is discovered.
        File.WriteAllBytes(expectedPath, [0x4D, 0x5A]);
        var (foundResolved, _) = CliCommandHelper.FindCliExecutable(binDir);
        Assert.Equal(expectedPath, foundResolved);

        // Command arguments preserve spaces cleanly and respect --force.
        var argsWithoutForce = CliCommandHelper.BuildBuildDbArguments(
            @"C:\Program Files (x86)\Steam\steamapps\common\FINAL FANTASY V PR",
            @"C:\Users\Alice\AppData\Local\agp\ffpr-export\output\ff5-20265087",
            force: false);
        Assert.Equal(
            [
                "build-db",
                "--game",
                @"C:\Program Files (x86)\Steam\steamapps\common\FINAL FANTASY V PR",
                "--out",
                @"C:\Users\Alice\AppData\Local\agp\ffpr-export\output\ff5-20265087",
            ],
            argsWithoutForce);

        var argsWithForce = CliCommandHelper.BuildBuildDbArguments(
            "/games/ff1",
            "/out/ff1-123",
            force: true,
            steamBuildId: "123",
            keepJson: true,
            jobs: 4,
            language: "ja",
            progressJson: true);
        Assert.Equal(
            [
                "build-db",
                "--game",
                "/games/ff1",
                "--out",
                "/out/ff1-123",
                "--steam-buildid",
                "123",
                "--keep-json",
                "--jobs",
                "4",
                "--force",
                "--lang",
                "ja",
                "--progress-json",
            ],
            argsWithForce);

        // ProcessStartInfo enforces UTF-8 stdout/stderr, hidden window, and AGP_HOME / TEMP propagation.
        var startInfo = CliCommandHelper.CreateStartInfo(
            expectedPath,
            argsWithoutForce,
            agpHome: @"C:\Users\Alice\AppData\LocalLow\agp",
            tempDirectory: @"C:\Users\Alice\AppData\LocalLow\agp\tmp");
        Assert.False(startInfo.UseShellExecute);
        Assert.True(startInfo.CreateNoWindow);
        Assert.True(startInfo.RedirectStandardOutput);
        Assert.True(startInfo.RedirectStandardError);
        Assert.Equal(Encoding.UTF8, startInfo.StandardOutputEncoding);
        Assert.Equal(Encoding.UTF8, startInfo.StandardErrorEncoding);
        Assert.Equal(argsWithoutForce, startInfo.ArgumentList);
        Assert.Equal(@"C:\Users\Alice\AppData\LocalLow\agp", startInfo.Environment[AgpPaths.AgpHomeEnvVar]);
        Assert.Equal(@"C:\Users\Alice\AppData\LocalLow\agp\tmp", startInfo.Environment["TEMP"]);
        Assert.Equal(@"C:\Users\Alice\AppData\LocalLow\agp\tmp", startInfo.Environment["TMP"]);

        // Progress parsing ONLY recognizes language-independent `@@progress {"step":n,"total":m}` lines.
        Assert.True(CliCommandHelper.IsProgressProtocolLine("""@@progress {"step":2,"total":6}"""));
        Assert.False(CliCommandHelper.IsProgressProtocolLine("==> [ff5] Step 2/6: Running in-process inventory and export"));

        Assert.True(CliCommandHelper.TryParseProgressStep("""@@progress {"step":2,"total":6}""", out var step2));
        Assert.Equal(2, step2.CurrentStep);
        Assert.Equal(6, step2.TotalSteps);
        Assert.Null(step2.Description);

        Assert.True(CliCommandHelper.TryParseProgressStep("""  @@progress {"step":5,"total":6,"message":"Compare known builds"}  """, out var step5));
        Assert.Equal(5, step5.CurrentStep);
        Assert.Equal(6, step5.TotalSteps);
        Assert.Equal("Compare known builds", step5.Description);

        // Human-readable localized lines must never be parsed as progress protocol lines.
        Assert.False(CliCommandHelper.TryParseProgressStep("==> [ff5] 步骤 2/6：进程内执行 inventory 与 export", out _));
        Assert.False(CliCommandHelper.TryParseProgressStep("==> [ff5] Step 2/6: Running in-process inventory and export", out _));
        Assert.False(CliCommandHelper.TryParseProgressStep("==> [ff5] ステップ 2/6：プロセス内で inventory と export を実行中", out _));
        Assert.False(CliCommandHelper.TryParseProgressStep("==> 步骤 5/6", out _));
        Assert.False(CliCommandHelper.TryParseProgressStep("""@@progress {"step":7,"total":6}""", out _));
        Assert.False(CliCommandHelper.TryParseProgressStep("""@@progress {"step":0,"total":6}""", out _));
        Assert.False(CliCommandHelper.TryParseProgressStep("@@progress {invalid}", out _));

        // Game header & overall permille progress calculation across languages.
        var ff5Spec = GameDetector.KnownGames.Single(g => g.Id == "ff5");
        var ff5Game = new GuiGameEntry(ff5Spec, "FINAL FANTASY V", "/games/ff5", "20265087", null, false);
        Assert.Equal("Game 2 / 3: FF5", CliCommandHelper.FormatGameProgressHeader(2, 3, ff5Game));
        Assert.Equal("Game 2 / 3: FF5 (Step 2/6)", CliCommandHelper.FormatGameProgressHeader(2, 3, ff5Game, step2));
        Assert.Equal("Game 2 / 3: FF5 (Step 5/6: Compare known builds)", CliCommandHelper.FormatGameProgressHeader(2, 3, ff5Game, step5));

        using (AppLocalization.PushLanguage(AppLanguage.ZhHans))
        {
            Assert.Equal("第 2 / 3 个：FF5", CliCommandHelper.FormatGameProgressHeader(2, 3, ff5Game));
            Assert.Equal("第 2 / 3 个：FF5（步骤 2/6）", CliCommandHelper.FormatGameProgressHeader(2, 3, ff5Game, step2));
        }

        using (AppLocalization.PushLanguage(AppLanguage.Ja))
        {
            Assert.Equal("2 / 3 本目：FF5", CliCommandHelper.FormatGameProgressHeader(2, 3, ff5Game));
            Assert.Equal("2 / 3 本目：FF5（ステップ 2/6）", CliCommandHelper.FormatGameProgressHeader(2, 3, ff5Game, step2));
        }

        var p0 = CliCommandHelper.CalculateProgressPermille(0, 2, null);
        var p1 = CliCommandHelper.CalculateProgressPermille(0, 2, new ProgressStepUpdate(1, 6, null));
        var p2 = CliCommandHelper.CalculateProgressPermille(0, 2, new ProgressStepUpdate(6, 6, null));
        var p3 = CliCommandHelper.CalculateProgressPermille(1, 2, null);
        var p4 = CliCommandHelper.CalculateProgressPermille(2, 2, null);
        Assert.Equal(0, p0);
        Assert.True(p0 < p1 && p1 < p2 && p2 < p3 && p3 == 500 && p4 == 1000);
    }

    [Fact]
    public void GuiSettingsStore_RoundTripsOutputRootAndCheckedStates()
    {
        using var ws = new TempWorkspace("ffpr-gui-settings");
        var settingsPath = GuiSettingsStore.GetDefaultSettingsPath(
            getEnvironmentVariable: key => key == AgpPaths.AgpHomeEnvVar ? ws.AgpHomeDir : null);
        Assert.Equal(Path.Combine(ws.AgpHomeDir, "ffpr-export", "settings.json"), settingsPath);

        // Missing file returns defaults where every game starts checked.
        var emptyLoaded = GuiSettingsStore.Load(settingsPath);
        Assert.Null(emptyLoaded.OutputRoot);
        Assert.True(GuiSettingsStore.ResolveInitialCheckedState(emptyLoaded, Path.Combine(ws.Root, "ff1")));

        var ff1Dir = Path.Combine(ws.Root, "games", "FINAL FANTASY PR");
        var ff5Dir = Path.Combine(ws.Root, "games", "FINAL FANTASY V PR");
        var ff6Dir = Path.Combine(ws.Root, "games", "FINAL FANTASY VI PR");

        var saved = new GuiSettings
        {
            OutputRoot = Path.Combine(ws.Root, "Custom Export Root"),
            CheckedGames = new Dictionary<string, bool>(StringComparer.Ordinal)
            {
                [ff1Dir] = false,
                [ff5Dir] = true,
            },
        };
        GuiSettingsStore.Save(settingsPath, saved);
        Assert.True(File.Exists(settingsPath));

        var reloaded = GuiSettingsStore.Load(settingsPath);
        Assert.Equal(saved.OutputRoot, reloaded.OutputRoot);
        Assert.False(GuiSettingsStore.ResolveInitialCheckedState(reloaded, ff1Dir));
        Assert.True(GuiSettingsStore.ResolveInitialCheckedState(reloaded, ff5Dir));
        // Newly discovered game not yet in settings defaults to checked.
        Assert.True(GuiSettingsStore.ResolveInitialCheckedState(reloaded, ff6Dir));

        // Corrupt JSON gracefully falls back to defaults.
        File.WriteAllText(settingsPath, "{broken", OutputGuard.Utf8WithoutBom);
        var recovered = GuiSettingsStore.Load(settingsPath);
        Assert.Null(recovered.OutputRoot);
        Assert.True(GuiSettingsStore.ResolveInitialCheckedState(recovered, ff1Dir));
    }

    [Fact]
    public void GuiSettingsStore_MigratesLegacySettingsPreservingBackupAndRespectingPrecedence()
    {
        using var ws = new TempWorkspace("ffpr-gui-settings-migrate");
        var legacyAppData = Path.Combine(ws.Root, "LegacyAppData");
        var legacyPath = GuiSettingsStore.GetLegacySettingsPath(
            getSpecialFolder: f => f == Environment.SpecialFolder.ApplicationData ? legacyAppData : null)!;
        var newPath = GuiSettingsStore.GetDefaultSettingsPath(
            getEnvironmentVariable: key => key == AgpPaths.AgpHomeEnvVar ? ws.AgpHomeDir : null);

        Assert.Equal(Path.Combine(legacyAppData, "ffpr-export", "settings.json"), legacyPath);
        Assert.Equal(Path.Combine(ws.AgpHomeDir, "ffpr-export", "settings.json"), newPath);

        // When AGP_HOME is set, automatic migration lookup returns null so host %APPDATA% is never touched.
        Assert.Null(GuiSettingsStore.GetLegacySettingsPathForMigration(
            getEnvironmentVariable: key => key == AgpPaths.AgpHomeEnvVar ? ws.AgpHomeDir : null,
            getSpecialFolder: _ => legacyAppData));
        Assert.Equal(
            legacyPath,
            GuiSettingsStore.GetLegacySettingsPathForMigration(
                isWindowsOverride: true,
                getEnvironmentVariable: _ => null,
                getSpecialFolder: f => f switch
                {
                    Environment.SpecialFolder.LocalApplicationData => Path.Combine(ws.Root, "LocalAppData"),
                    Environment.SpecialFolder.ApplicationData => legacyAppData,
                    _ => null,
                },
                probeWritable: _ => (true, null)));

        // 1. Old exists, new does not exist -> read old, write new, rename old to .migrated (not deleted).
        var ff5Dir = Path.Combine(ws.Root, "games", "FINAL FANTASY V PR");
        var legacySettings = new GuiSettings
        {
            OutputRoot = Path.Combine(ws.Root, "My Custom Out"),
            CheckedGames = new Dictionary<string, bool>(StringComparer.Ordinal)
            {
                [ff5Dir] = false,
            },
        };
        GuiSettingsStore.Save(legacyPath, legacySettings);
        var originalLegacyContent = File.ReadAllText(legacyPath, Encoding.UTF8);

        var migratedResult = GuiSettingsStore.LoadWithMigration(newPath, legacyPath);
        Assert.Null(migratedResult.WarningMessage);
        Assert.Null(migratedResult.Error);
        Assert.Equal(legacyPath, migratedResult.MigratedFromPath);
        Assert.Equal(legacyPath + ".migrated", migratedResult.MigratedBackupPath);
        Assert.NotNull(migratedResult.InfoMessage);
        Assert.Equal(legacySettings.OutputRoot, migratedResult.Settings.OutputRoot);
        Assert.False(GuiSettingsStore.ResolveInitialCheckedState(migratedResult.Settings, ff5Dir));

        Assert.True(File.Exists(newPath));
        Assert.False(File.Exists(legacyPath));
        Assert.True(File.Exists(legacyPath + ".migrated"));
        Assert.Equal(originalLegacyContent, File.ReadAllText(legacyPath + ".migrated", Encoding.UTF8));

        var reloadedNew = GuiSettingsStore.Load(newPath);
        Assert.Equal(legacySettings.OutputRoot, reloadedNew.OutputRoot);
        Assert.False(GuiSettingsStore.ResolveInitialCheckedState(reloadedNew, ff5Dir));

        // 2. When new already exists, old is NOT migrated and NOT renamed.
        var newerSettings = new GuiSettings
        {
            OutputRoot = Path.Combine(ws.Root, "Newer Out"),
            CheckedGames = new Dictionary<string, bool>(StringComparer.Ordinal),
        };
        GuiSettingsStore.Save(newPath, newerSettings);
        GuiSettingsStore.Save(legacyPath, legacySettings);

        var keepNewResult = GuiSettingsStore.LoadWithMigration(newPath, legacyPath);
        Assert.Null(keepNewResult.MigratedFromPath);
        Assert.Equal(newerSettings.OutputRoot, keepNewResult.Settings.OutputRoot);
        Assert.True(File.Exists(legacyPath));

        // 3. Corrupt old settings (when new does not exist) emits warning and leaves old untouched.
        File.Delete(newPath);
        File.WriteAllText(legacyPath, "{corrupt-legacy", OutputGuard.Utf8WithoutBom);
        var corruptMigration = GuiSettingsStore.LoadWithMigration(newPath, legacyPath);
        Assert.NotNull(corruptMigration.WarningMessage);
        Assert.Null(corruptMigration.MigratedFromPath);
        Assert.False(File.Exists(newPath));
        Assert.True(File.Exists(legacyPath));
    }

    [Fact]
    public async Task CliProcessRunner_StreamsUtf8OutputAndCancelsChildProcessTree()
    {
        var cliDll = Path.Combine(AppContext.BaseDirectory, "ffpr-export.dll");
        Assert.True(File.Exists(cliDll), $"缺少 ffpr-export.dll：{cliDll}");

        var stdoutLines = new List<string>();
        var stderrLines = new List<string>();
        var startInfo = CliCommandHelper.CreateStartInfo(
            TestHarness.ResolveDotnetExecutable(),
            ["exec", cliDll, "--lang", "zh-Hans", "--help"]);
        startInfo.Environment.Remove(AppLocalization.EnvVarName);

        var exitCode = await CliProcessRunner.RunAsync(
            startInfo,
            line =>
            {
                lock (stdoutLines)
                {
                    stdoutLines.Add(line);
                }
            },
            line =>
            {
                lock (stderrLines)
                {
                    stderrLines.Add(line);
                }
            },
            CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Contains(stdoutLines, line => line.Contains("资源盘点、导出与 SQLite 建库工具", StringComparison.Ordinal));
        Assert.Empty(stderrLines);

        // Verify cancellation kills a long-running child process promptly.
        var sleepStartInfo = OperatingSystem.IsWindows()
            ? CliCommandHelper.CreateStartInfo("cmd.exe", ["/c", "ping -n 30 127.0.0.1 >nul"])
            : CliCommandHelper.CreateStartInfo("/bin/sh", ["-c", "sleep 30"]);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        var sw = Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CliProcessRunner.RunAsync(sleepStartInfo, _ => { }, _ => { }, cts.Token));
        sw.Stop();
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), $"取消子进程耗时过长：{sw.Elapsed}");
    }

    [Fact]
    public async Task CliRedirectedSubprocess_OutputsValidBomlessUtf8ChineseOnStdoutAndStderr()
    {
        var cliDll = Path.Combine(AppContext.BaseDirectory, "ffpr-export.dll");
        Assert.True(File.Exists(cliDll), $"缺少 ffpr-export.dll：{cliDll}");

        var strictUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

        // 1. Redirected stdout (--lang zh-Hans --help) must emit valid UTF-8 Chinese without BOM.
        var (helpExit, helpOutBytes, helpErrBytes) = await RunCliRawBytesAsync(cliDll, ["--lang", "zh-Hans", "--help"]);
        Assert.Equal(0, helpExit);
        Assert.False(HasUtf8Bom(helpOutBytes), "重定向标准输出不应包含 UTF-8 BOM");
        Assert.Empty(helpErrBytes);
        var helpText = strictUtf8.GetString(helpOutBytes);
        Assert.Contains("资源盘点、导出与 SQLite 建库工具", helpText, StringComparison.Ordinal);
        Assert.Contains("打印本帮助信息", helpText, StringComparison.Ordinal);

        // 2. Redirected stderr (invalid subcommand) must emit valid UTF-8 Chinese without BOM.
        var (errExit, errOutBytes, errErrBytes) = await RunCliRawBytesAsync(cliDll, ["--lang", "zh-Hans", "__invalid_subcommand__"]);
        Assert.Equal(2, errExit);
        Assert.Empty(errOutBytes);
        Assert.False(HasUtf8Bom(errErrBytes), "重定向标准错误不应包含 UTF-8 BOM");
        var errText = strictUtf8.GetString(errErrBytes);
        Assert.Contains("错误：未知子命令：__invalid_subcommand__", errText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CliSubprocess_SucceedsWithBlockedTempAndRejectsUnwritableExplicitAgpHome()
    {
        using var ws = new TempWorkspace("ffpr-cli-notemp");
        var blockedTemp = Path.Combine(ws.Root, "blocked-temp-file");
        File.WriteAllText(blockedTemp, "blocked", OutputGuard.Utf8WithoutBom);

        var blockedTempEnv = new Dictionary<string, string?>
        {
            ["TEMP"] = blockedTemp,
            ["TMP"] = blockedTemp,
            ["TMPDIR"] = blockedTemp,
        };

        // 1. --help succeeds even when TEMP/TMP/TMPDIR point to an unwritable regular file.
        var helpRes = await TestHarness.RunCliAsync(["--help"], extraEnv: blockedTempEnv, agpHome: ws.AgpHomeDir);
        Assert.Equal(0, helpRes.ExitCode);
        Assert.Contains("ffpr-export", helpRes.Stdout, StringComparison.Ordinal);

        // 2. db write-fixture + db build succeed with TEMP blocked (SQLite uses memory temp_store & sibling temp db).
        var fixtureDir = Path.Combine(ws.Root, "fixture-export");
        var dbPath = Path.Combine(ws.Root, "fixture.sqlite");
        var writeRes = await TestHarness.RunCliAsync(
            ["db", "write-fixture", "--out", fixtureDir],
            extraEnv: blockedTempEnv,
            agpHome: ws.AgpHomeDir);
        Assert.True(writeRes.ExitCode == 0, $"stdout:\n{writeRes.Stdout}\nstderr:\n{writeRes.Stderr}");

        var buildRes = await TestHarness.RunCliAsync(
            ["db", "build", "--export", fixtureDir, "--out", dbPath],
            extraEnv: blockedTempEnv,
            agpHome: ws.AgpHomeDir);
        Assert.True(buildRes.ExitCode == 0, $"stdout:\n{buildRes.Stdout}\nstderr:\n{buildRes.Stderr}");
        Assert.True(File.Exists(dbPath));

        // 3. build-db without --out fails immediately when explicit AGP_HOME is unwritable (blocked by a regular file).
        var spec = GameDetector.KnownGames.Single(g => g.Id == "ff5");
        var gameDir = SyntheticUnityFixtureBuilder.BuildSyntheticSteamGame(ws.Root, spec, steamBuildId: "20265087");
        var blockedAgpHome = Path.Combine(ws.Root, "blocked-agp-home");
        File.WriteAllText(blockedAgpHome, "not a directory", OutputGuard.Utf8WithoutBom);

        var failRes = await TestHarness.RunCliAsync(
            ["build-db", "--game", gameDir],
            ffprDeps: ws.DepsDir,
            extraEnv: blockedTempEnv,
            agpHome: blockedAgpHome);
        Assert.Equal(2, failRes.ExitCode);
        Assert.Contains("AGP_HOME", failRes.Stderr, StringComparison.Ordinal);
        Assert.Contains(blockedAgpHome, failRes.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void GuiStartupLogger_ResolvesPrimaryLogPathWritesIntegrityAndNeverFallsBackToTemp()
    {
        using var ws = new TempWorkspace("ffpr-gui-startup-log");
        var agpHome = ws.AgpHomeDir;

        var primaryDir = GuiStartupLogger.GetPrimaryLogDirectory(
            getEnvironmentVariable: key => key == AgpPaths.AgpHomeEnvVar ? agpHome : null);
        Assert.Equal(Path.Combine(agpHome, "ffpr-export", "logs"), primaryDir);

        var winPrimaryDir = GuiStartupLogger.GetPrimaryLogDirectory(
            isWindowsOverride: true,
            getEnvironmentVariable: _ => null,
            getSpecialFolder: f => f == Environment.SpecialFolder.LocalApplicationData
                ? @"C:\Users\Alice\AppData\Local"
                : null,
            probeWritable: _ => (true, null));
        Assert.Equal(@"C:\Users\Alice\AppData\Local\agp\ffpr-export\logs", winPrimaryDir);

        var ts = new DateTimeOffset(2026, 11, 26, 10, 30, 45, TimeSpan.FromHours(9));
        var info = new GuiStartupInfo(
            Timestamp: ts,
            Version: "0.1.0",
            GitCommit: "abc1234def56",
            OsVersion: "Microsoft Windows 10.0.22631",
            DotNetVersion: ".NET 10.0.0",
            ProcessBitness: "64-bit (X64, OS=X64)",
            CurrentDirectory: ws.Root,
            ExecutablePath: Path.Combine(ws.Root, "ffpr-export-gui.exe"),
            DpiSetting: "PerMonitorV2",
            CommandLineArgs: "--self-test",
            IntegrityLevel: ProcessIntegrityInfo.Low.LogLabel,
            AgpHome: agpHome,
            AgpRootSource: nameof(AgpRootSource.AgpHomeEnv));

        var logger = GuiStartupLogger.Start(
            info,
            agpHomeOverride: agpHome);

        Assert.Null(logger.InitializationWarning);
        Assert.Equal(Path.Combine(agpHome, "ffpr-export", "logs", "gui-20261126-103045.log"), logger.LogFilePath);
        Assert.True(File.Exists(logger.LogFilePath));

        var lines = File.ReadAllLines(logger.LogFilePath!, Encoding.UTF8);
        Assert.Single(lines);
        var startLine = lines[0];
        Assert.Contains("[START]", startLine, StringComparison.Ordinal);
        Assert.Contains("version=0.1.0", startLine, StringComparison.Ordinal);
        Assert.Contains("git=abc1234def56", startLine, StringComparison.Ordinal);
        Assert.Contains("os=Microsoft Windows 10.0.22631", startLine, StringComparison.Ordinal);
        Assert.Contains("dotnet=.NET 10.0.0", startLine, StringComparison.Ordinal);
        Assert.Contains("bitness=64-bit (X64, OS=X64)", startLine, StringComparison.Ordinal);
        Assert.Contains("integrity=Low (0x1000)", startLine, StringComparison.Ordinal);
        Assert.Contains($"cwd={ws.Root}", startLine, StringComparison.Ordinal);
        Assert.Contains("ffpr-export-gui.exe", startLine, StringComparison.Ordinal);
        Assert.Contains("dpi=PerMonitorV2", startLine, StringComparison.Ordinal);
        Assert.Contains("args=--self-test", startLine, StringComparison.Ordinal);
        Assert.Contains($"agp_home={agpHome}", startLine, StringComparison.Ordinal);

        // When primary AGP root is blocked, logger must NOT fall back to %TEMP%; degrades cleanly for MessageBox display.
        var blockedAgpHome = Path.Combine(ws.Root, "blocked-agp-home");
        File.WriteAllText(blockedAgpHome, "not a directory", OutputGuard.Utf8WithoutBom);

        var degradedLogger = GuiStartupLogger.Start(
            info,
            agpHomeOverride: blockedAgpHome);
        Assert.Null(degradedLogger.LogFilePath);
        Assert.NotNull(degradedLogger.InitializationWarning);
        degradedLogger.WriteInfo("must not throw");
        degradedLogger.WriteException("test", new InvalidOperationException("boom"));
    }

    [Fact]
    public void GuiStartupLogger_RetainsLatestTenLogsHandlesCollisionsAndPreservesUnrelatedFiles()
    {
        using var ws = new TempWorkspace("ffpr-gui-log-retention");
        var agpHome = ws.AgpHomeDir;
        var logsDir = Path.Combine(agpHome, "ffpr-export", "logs");
        Directory.CreateDirectory(logsDir);

        for (var sec = 1; sec <= 12; sec++)
        {
            var name = $"gui-20261126-1000{sec:D2}.log";
            File.WriteAllText(Path.Combine(logsDir, name), $"old log {sec}\n", OutputGuard.Utf8WithoutBom);
        }

        var unrelatedLog = Path.Combine(logsDir, "other-tool.log");
        var unrelatedTxt = Path.Combine(logsDir, "readme.txt");
        File.WriteAllText(unrelatedLog, "keep", OutputGuard.Utf8WithoutBom);
        File.WriteAllText(unrelatedTxt, "keep", OutputGuard.Utf8WithoutBom);

        var ts = new DateTimeOffset(2026, 11, 26, 10, 0, 13, TimeSpan.FromHours(9));
        var info = GuiStartupLogger.CollectStartupInfo(typeof(GuiCoreTests).Assembly, "PerMonitorV2", [], ts);

        var first = GuiStartupLogger.Start(info, agpHomeOverride: agpHome, maxLogFiles: 10);
        var second = GuiStartupLogger.Start(info, agpHomeOverride: agpHome, maxLogFiles: 10);

        Assert.Equal(Path.Combine(logsDir, "gui-20261126-100013.log"), first.LogFilePath);
        Assert.Equal(Path.Combine(logsDir, "gui-20261126-100013-1.log"), second.LogFilePath);

        var remainingGuiLogs = Directory.EnumerateFiles(logsDir, "gui-*.log")
            .Select(path => Path.GetFileName(path)!)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(10, remainingGuiLogs.Length);
        Assert.Equal(
            [
                "gui-20261126-100005.log",
                "gui-20261126-100006.log",
                "gui-20261126-100007.log",
                "gui-20261126-100008.log",
                "gui-20261126-100009.log",
                "gui-20261126-100010.log",
                "gui-20261126-100011.log",
                "gui-20261126-100012.log",
                "gui-20261126-100013-1.log",
                "gui-20261126-100013.log",
            ],
            remainingGuiLogs);

        Assert.True(File.Exists(unrelatedLog));
        Assert.True(File.Exists(unrelatedTxt));

        // Explicitly protected file is never pruned even if its timestamp is older.
        var protectedOld = Path.Combine(logsDir, "gui-20260101-000001.log");
        File.WriteAllText(protectedOld, "protected", OutputGuard.Utf8WithoutBom);
        var deleted = GuiStartupLogger.PruneOldLogs(logsDir, maxFiles: 10, protectedFilePath: protectedOld);
        Assert.Equal(1, deleted);
        Assert.True(File.Exists(protectedOld));
        Assert.Equal(10, Directory.EnumerateFiles(logsDir, "gui-*.log").Count());
    }

    [Fact]
    public void GuiStartupLogger_FormatsCompleteExceptionsFatalDialogAndSelfTestStderr()
    {
        using var langScope = AppLocalization.PushLanguage(AppLanguage.ZhHans);
        using var ws = new TempWorkspace("ffpr-gui-ex-format");

        Exception captured;
        try
        {
            try
            {
                throw new UnauthorizedAccessException("注册表访问被拒绝");
            }
            catch (Exception inner)
            {
                throw new InvalidOperationException("窗口初始化失败", inner);
            }
        }
        catch (Exception ex)
        {
            captured = ex;
        }

        var details = GuiStartupLogger.FormatExceptionDetails(captured, "MainForm/Application.Run");
        Assert.Contains("上下文: MainForm/Application.Run", details, StringComparison.Ordinal);
        Assert.Contains("Type: System.InvalidOperationException", details, StringComparison.Ordinal);
        Assert.Contains("Message: 窗口初始化失败", details, StringComparison.Ordinal);
        Assert.Contains("StackTrace:", details, StringComparison.Ordinal);
        Assert.Contains("InnerException [1]", details, StringComparison.Ordinal);
        Assert.Contains("Type: System.UnauthorizedAccessException", details, StringComparison.Ordinal);
        Assert.Contains("Message: 注册表访问被拒绝", details, StringComparison.Ordinal);

        var agg = new AggregateException(
            "多个错误",
            new IOException("文件锁冲突"),
            new FormatException("字段格式异常"));
        var aggDetails = GuiStartupLogger.FormatExceptionDetails(agg, "AggregateContext");
        Assert.Contains("InnerException [1.1]", aggDetails, StringComparison.Ordinal);
        Assert.Contains("InnerException [1.2]", aggDetails, StringComparison.Ordinal);
        Assert.Contains("文件锁冲突", aggDetails, StringComparison.Ordinal);
        Assert.Contains("字段格式异常", aggDetails, StringComparison.Ordinal);

        var info = GuiStartupLogger.CollectStartupInfo(typeof(GuiCoreTests).Assembly, "PerMonitorV2");
        var logger = GuiStartupLogger.Start(info, agpHomeOverride: ws.AgpHomeDir);
        logger.WriteException("UnitTest", captured);
        logger.WriteUnhandledObject("AppDomain", "raw-non-exception-error", isTerminating: true);

        var logContent = File.ReadAllText(logger.LogFilePath!, Encoding.UTF8);
        Assert.Contains("System.InvalidOperationException", logContent, StringComparison.Ordinal);
        Assert.Contains("注册表访问被拒绝", logContent, StringComparison.Ordinal);
        Assert.Contains("raw-non-exception-error", logContent, StringComparison.Ordinal);

        var dialogText = GuiStartupLogger.FormatFatalDialogMessage(captured, logger.LogFilePath, isStartupFailure: true);
        Assert.Contains("窗口程序启动失败", dialogText, StringComparison.Ordinal);
        Assert.Contains("窗口初始化失败", dialogText, StringComparison.Ordinal);
        Assert.Contains("注册表访问被拒绝", dialogText, StringComparison.Ordinal);
        Assert.Contains(logger.LogFilePath!, dialogText, StringComparison.Ordinal);
        Assert.Contains("发送给开发者", dialogText, StringComparison.Ordinal);

        var noLogDialogText = GuiStartupLogger.FormatFatalDialogMessage(
            captured,
            logFilePath: null,
            logInitWarning: "磁盘只读",
            isStartupFailure: true);
        Assert.Contains("窗口程序启动失败", noLogDialogText, StringComparison.Ordinal);
        Assert.Contains("磁盘只读", noLogDialogText, StringComparison.Ordinal);

        var selfTestErr = GuiStartupLogger.FormatSelfTestFailureMessage(captured, logger.LogFilePath);
        Assert.Contains("GUI 自检失败", selfTestErr, StringComparison.Ordinal);
        Assert.Contains($"日志文件：{logger.LogFilePath}", selfTestErr, StringComparison.Ordinal);
        Assert.Contains("System.InvalidOperationException", selfTestErr, StringComparison.Ordinal);
    }

    [Fact]
    public void GuiStartupFallbacks_HandleSettingsFontOutputRootSteamAndCliFailuresWithoutThrowing()
    {
        using var langScope = AppLocalization.PushLanguage(AppLanguage.ZhHans);
        using var ws = new TempWorkspace("ffpr-gui-fallbacks");

        // 1. Settings path & load diagnostics: provider exception, corrupt JSON, null JSON, unreadable directory path.
        var fallbackSettingsPath = GuiSettingsStore.GetDefaultSettingsPath(
            getEnvironmentVariable: _ => null,
            getSpecialFolder: _ => throw new UnauthorizedAccessException("LocalAppData 不可访问"),
            getBaseDirectory: () => ws.Root);
        Assert.Equal(Path.Combine(ws.Root, AgpPaths.PortableAgpFolderName, "ffpr-export", "settings.json"), fallbackSettingsPath);

        var missingResult = GuiSettingsStore.LoadWithDiagnostics(Path.Combine(ws.Root, "missing-settings.json"));
        Assert.Null(missingResult.WarningMessage);
        Assert.Null(missingResult.Error);

        var corruptPath = Path.Combine(ws.Root, "corrupt-settings.json");
        File.WriteAllText(corruptPath, "{not-json", OutputGuard.Utf8WithoutBom);
        var corruptResult = GuiSettingsStore.LoadWithDiagnostics(corruptPath);
        Assert.NotNull(corruptResult.WarningMessage);
        Assert.Contains("corrupt-settings.json", corruptResult.WarningMessage, StringComparison.Ordinal);
        Assert.IsType<JsonException>(corruptResult.Error);

        var nullJsonPath = Path.Combine(ws.Root, "null-settings.json");
        File.WriteAllText(nullJsonPath, "null", OutputGuard.Utf8WithoutBom);
        var nullResult = GuiSettingsStore.LoadWithDiagnostics(nullJsonPath);
        Assert.NotNull(nullResult.WarningMessage);

        var dirAsSettingsPath = Path.Combine(ws.Root, "dir-not-file.json");
        Directory.CreateDirectory(dirAsSettingsPath);
        var unreadableResult = GuiSettingsStore.LoadWithDiagnostics(dirAsSettingsPath);
        Assert.NotNull(unreadableResult.Settings);
        Assert.Null(unreadableResult.Settings.OutputRoot);
        Assert.NotNull(unreadableResult.WarningMessage);
        Assert.Contains("目录", unreadableResult.WarningMessage, StringComparison.Ordinal);

        var deniedSettingsResult = GuiSettingsStore.LoadWithDiagnostics(
            Path.Combine(ws.Root, "denied-settings.json"),
            readAllText: _ => throw new UnauthorizedAccessException("拒绝访问设置文件"));
        Assert.NotNull(deniedSettingsResult.WarningMessage);
        Assert.Contains("没有读取权限", deniedSettingsResult.WarningMessage, StringComparison.Ordinal);
        Assert.IsType<UnauthorizedAccessException>(deniedSettingsResult.Error);

        var invalidCharResult = GuiSettingsStore.LoadWithDiagnostics("bad\0path");
        Assert.NotNull(invalidCharResult.WarningMessage);
        Assert.NotNull(invalidCharResult.Error);

        // 2. Font fallback chain: primary -> secondary -> MessageBoxFont -> ultimate fallback.
        var primaryFont = GuiFontFallbackPolicy.ResolveFont(
            "界面字体",
            GuiFontFallbackPolicy.UiFontCandidates,
            family => family == "Microsoft YaHei UI" ? "yahei-ui" : null,
            () => ("msgbox", true, "Segoe UI"),
            () => ("default", false, "DefaultFont"));
        Assert.False(primaryFont.UsedFallback);
        Assert.Null(primaryFont.WarningMessage);
        Assert.True(primaryFont.OwnedByCaller);
        Assert.Equal("yahei-ui", primaryFont.Font);

        var msgBoxFallback = GuiFontFallbackPolicy.ResolveFont(
            "界面字体",
            GuiFontFallbackPolicy.UiFontCandidates,
            _ => throw new ArgumentException("字体未安装"),
            () => ("msgbox-font", true, "Segoe UI"),
            () => ("default", false, "DefaultFont"));
        Assert.True(msgBoxFallback.UsedFallback);
        Assert.True(msgBoxFallback.OwnedByCaller);
        Assert.Equal("msgbox-font", msgBoxFallback.Font);
        Assert.Contains("SystemFonts.MessageBoxFont", msgBoxFallback.WarningMessage, StringComparison.Ordinal);

        var ultimateFallback = GuiFontFallbackPolicy.ResolveFont(
            "日志字体",
            GuiFontFallbackPolicy.LogFontCandidates,
            _ => null,
            () => throw new InvalidOperationException("GDI+ 异常"),
            () => ("control-default", false, "Control.DefaultFont"));
        Assert.True(ultimateFallback.UsedFallback);
        Assert.False(ultimateFallback.OwnedByCaller);
        Assert.Equal("control-default", ultimateFallback.Font);
        Assert.NotNull(ultimateFallback.WarningMessage);

        // 3. Default output root fallbacks when primary AGP root providers fail or return empty.
        var existingBase = Path.Combine(ws.Root, "AppBase");
        Directory.CreateDirectory(existingBase);

        var normalRoot = GuiOutputPlanner.ResolveDefaultOutputRoot(
            getEnvironmentVariable: key => key == AgpPaths.AgpHomeEnvVar ? ws.AgpHomeDir : null,
            getBaseDirectory: () => existingBase);
        Assert.True(normalRoot.Succeeded);
        Assert.False(normalRoot.UsedFallback);
        Assert.Null(normalRoot.WarningMessage);
        Assert.Equal(Path.Combine(ws.AgpHomeDir, "ffpr-export", "output"), normalRoot.OutputRoot);

        var throwingPrimaryRoot = GuiOutputPlanner.ResolveDefaultOutputRoot(
            isWindowsOverride: true,
            getEnvironmentVariable: _ => null,
            getSpecialFolder: _ => throw new UnauthorizedAccessException("无法读取 LocalAppData 目录"),
            getBaseDirectory: () => existingBase,
            probeWritable: path => path == Path.Combine(existingBase, AgpPaths.PortableAgpFolderName) || path.EndsWith("\\agp-data", StringComparison.Ordinal)
                ? (true, null)
                : (false, "拒绝访问"));
        Assert.True(throwingPrimaryRoot.Succeeded);
        Assert.True(throwingPrimaryRoot.UsedFallback);
        Assert.Contains("%LOCALAPPDATA% 不可写", throwingPrimaryRoot.WarningMessage, StringComparison.Ordinal);

        var allBlockedRoot = GuiOutputPlanner.ResolveDefaultOutputRoot(
            getEnvironmentVariable: _ => null,
            getSpecialFolder: _ => string.Empty,
            getBaseDirectory: () => string.Empty,
            probeWritable: _ => (false, "全部不可写"));
        Assert.False(allBlockedRoot.Succeeded);
        Assert.True(allBlockedRoot.UsedFallback);
        Assert.Equal(string.Empty, allBlockedRoot.OutputRoot);
        Assert.Contains("AGP_HOME", allBlockedRoot.WarningMessage, StringComparison.Ordinal);

        // 4. Steam scan safe wrapper catches permission & IO exceptions and prompts manual selection.
        var deniedScan = GuiGameScanner.DiscoverSteamGamesSafe(
            () => throw new UnauthorizedAccessException("无权访问 Steam 目录"));
        Assert.Empty(deniedScan.Games);
        Assert.IsType<UnauthorizedAccessException>(deniedScan.Error);
        Assert.Contains("手动选择游戏目录", deniedScan.WarningMessage, StringComparison.Ordinal);

        var ioScan = GuiGameScanner.DiscoverSteamGamesSafe(
            () => throw new IOException("磁盘 I/O 错误"));
        Assert.Empty(ioScan.Games);
        Assert.IsType<IOException>(ioScan.Error);
        Assert.Contains("扫描 Steam 游戏库失败", ioScan.WarningMessage, StringComparison.Ordinal);

        // 5. CLI executable safe lookup never throws on permission errors.
        var deniedCli = CliCommandHelper.FindCliExecutableSafe(
            ws.Root,
            fileExists: _ => throw new UnauthorizedAccessException("禁止访问程序目录"));
        Assert.Null(deniedCli.ResolvedPath);
        Assert.IsType<UnauthorizedAccessException>(deniedCli.Error);
        Assert.Contains("禁止访问程序目录", deniedCli.WarningMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void GuiProgramAndPackagingScript_EnforceDiagnosticsSelfTestAndSafePowerShellQuotes()
    {
        var repoRoot = TestHarness.FindRepoRoot();
        var programCs = File.ReadAllText(Path.Combine(repoRoot, "src", "FfprExport.Gui", "Program.cs"), Encoding.UTF8);

        Assert.Contains("Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException)", programCs, StringComparison.Ordinal);
        Assert.Contains("Application.ThreadException +=", programCs, StringComparison.Ordinal);
        Assert.Contains("AppDomain.CurrentDomain.UnhandledException +=", programCs, StringComparison.Ordinal);
        Assert.Contains("form.CreateControl();", programCs, StringComparison.Ordinal);
        Assert.Contains("GuiStartupLogger.Start(", programCs, StringComparison.Ordinal);

        var ps1Path = Path.Combine(repoRoot, "scripts", "package-windows.ps1");
        var ps1Content = File.ReadAllText(ps1Path, Encoding.UTF8);
        Assert.DoesNotContain('“', ps1Content);
        Assert.DoesNotContain('”', ps1Content);
        Assert.Contains("Start-Process", ps1Content, StringComparison.Ordinal);
        Assert.Contains("--self-test", ps1Content, StringComparison.Ordinal);
        Assert.Contains("-Wait", ps1Content, StringComparison.Ordinal);
        Assert.Contains("-PassThru", ps1Content, StringComparison.Ordinal);
        Assert.Contains("IncludeNativeLibrariesForSelfExtract=false", ps1Content, StringComparison.Ordinal);
        Assert.Contains("e_sqlite3.dll", ps1Content, StringComparison.Ordinal);
    }

    private static bool HasUtf8Bom(byte[] bytes)
    {
        return bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
    }

    private static async Task<(int ExitCode, byte[] Stdout, byte[] Stderr)> RunCliRawBytesAsync(
        string cliDll,
        IReadOnlyList<string> args)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = TestHarness.ResolveDotnetExecutable(),
            WorkingDirectory = TestHarness.FindRepoRoot(),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.Environment.Remove(AppLocalization.EnvVarName);
        startInfo.ArgumentList.Add("exec");
        startInfo.ArgumentList.Add(cliDll);
        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("无法启动 ffpr-export 测试子进程");

        using var outMs = new MemoryStream();
        using var errMs = new MemoryStream();
        var outCopy = process.StandardOutput.BaseStream.CopyToAsync(outMs);
        var errCopy = process.StandardError.BaseStream.CopyToAsync(errMs);
        await Task.WhenAll(outCopy, errCopy);
        await process.WaitForExitAsync();

        return (process.ExitCode, outMs.ToArray(), errMs.ToArray());
    }

    private static void WriteAppManifest(
        string libraryRoot,
        int appId,
        string name,
        string installDir,
        string buildId)
    {
        var steamAppsDir = Path.Combine(libraryRoot, "steamapps");
        Directory.CreateDirectory(steamAppsDir);
        var content = $$"""
            "AppState"
            {
            	"appid"		"{{appId}}"
            	"name"		"{{name}}"
            	"installdir"		"{{installDir}}"
            	"buildid"		"{{buildId}}"
            }
            """;
        File.WriteAllText(
            Path.Combine(steamAppsDir, $"appmanifest_{appId}.acf"),
            content,
            OutputGuard.Utf8WithoutBom);
    }

    private static void WriteSyntheticBuildReport(
        string outDir,
        string gameId,
        string status,
        string? steamBuildId,
        string? fileSha256,
        string? contentSha256,
        string? checkStatus,
        string? checkMessage,
        string? checkSource,
        string? registeredAt,
        bool fileSha256MismatchOnly,
        int tableDiffCount,
        string? customReportFileName = null)
    {
        Directory.CreateDirectory(outDir);
        var sqliteName = $"{gameId}.sqlite";
        var diffs = Enumerable.Range(0, tableDiffCount)
            .Select(i => new { name = $"table_{i}", kind = "changed" })
            .ToArray();

        var report = new
        {
            format = 1,
            tool = OutputGuard.ToolName,
            command = "build-db",
            status,
            game_id = gameId,
            game_version = new
            {
                steam = new
                {
                    build_id = steamBuildId,
                },
            },
            database = new
            {
                file_name = sqliteName,
                size = 1024L,
                file_sha256 = fileSha256,
                content_sha256 = contentSha256,
            },
            known_build_check = new
            {
                status = checkStatus,
                message = checkMessage,
                source = checkSource,
                matched_registered_at = registeredAt,
                file_sha256_mismatch_only = fileSha256MismatchOnly,
                table_diffs = diffs,
            },
            timings_ms = new
            {
                total = 1234L,
            },
        };

        var fileName = customReportFileName ?? $"{sqliteName}.build.json";
        File.WriteAllText(
            Path.Combine(outDir, fileName),
            JsonSerializer.Serialize(report, FieldJsonWriter.SerializerOptions),
            OutputGuard.Utf8WithoutBom);
    }
}
