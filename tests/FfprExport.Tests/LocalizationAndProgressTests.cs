using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using FfprExport.Gui;
using Xunit;

namespace FfprExport.Tests;

public sealed class LocalizationAndProgressTests
{
    private static readonly Regex PlaceholderRegex = new(@"\{(\d+(?:,[^:}]+)?(?::[^}]+)?)\}", RegexOptions.CultureInvariant);
    private static readonly Regex ResourceCallRegex = new(
        @"\b(?:L|AppLocalization)\.(?:Get|Format)\(\s*(?:[^,"")]+,\s*)?""([^""]+)""",
        RegexOptions.CultureInvariant);

    [Fact]
    public void ResxResources_HaveIdenticalKeysPlaceholdersAndRuntimeLookupAcrossAllThreeLanguages()
    {
        var repoRoot = TestHarness.FindRepoRoot();
        var resourcesDir = Path.Combine(repoRoot, "src", "FfprExport", "Resources");

        var enEntries = LoadResxEntries(Path.Combine(resourcesDir, "Strings.resx"));
        var zhEntries = LoadResxEntries(Path.Combine(resourcesDir, "Strings.zh-Hans.resx"));
        var jaEntries = LoadResxEntries(Path.Combine(resourcesDir, "Strings.ja.resx"));

        Assert.True(enEntries.Count >= 400, $"Expected comprehensive resource coverage, found only {enEntries.Count} keys.");

        var enKeys = enEntries.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray();
        var zhKeys = zhEntries.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray();
        var jaKeys = jaEntries.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray();

        Assert.Equal(enKeys, zhKeys);
        Assert.Equal(enKeys, jaKeys);

        foreach (var key in enKeys)
        {
            var enVal = enEntries[key];
            var zhVal = zhEntries[key];
            var jaVal = jaEntries[key];

            Assert.False(string.IsNullOrWhiteSpace(enVal), $"en key '{key}' is empty.");
            Assert.False(string.IsNullOrWhiteSpace(zhVal), $"zh-Hans key '{key}' is empty.");
            Assert.False(string.IsNullOrWhiteSpace(jaVal), $"ja key '{key}' is empty.");
            Assert.DoesNotContain('\r', enVal);
            Assert.DoesNotContain('\r', zhVal);
            Assert.DoesNotContain('\r', jaVal);

            var enPlaceholders = ExtractPlaceholders(enVal);
            var zhPlaceholders = ExtractPlaceholders(zhVal);
            var jaPlaceholders = ExtractPlaceholders(jaVal);
            Assert.True(
                enPlaceholders.SetEquals(zhPlaceholders) && enPlaceholders.SetEquals(jaPlaceholders),
                $"Placeholder mismatch for key '{key}': en=[{string.Join(", ", enPlaceholders)}], zh-Hans=[{string.Join(", ", zhPlaceholders)}], ja=[{string.Join(", ", jaPlaceholders)}]");

            // Verify ResourceManager resolves every single key accurately for all three cultures.
            Assert.Equal(enVal, AppLocalization.Get(key, AppLanguage.En));
            Assert.Equal(zhVal, AppLocalization.Get(key, AppLanguage.ZhHans));
            Assert.Equal(jaVal, AppLocalization.Get(key, AppLanguage.Ja));
        }

        // Verify every L.Get/L.Format key referenced in src/ exists in the .resx files and no orphan keys remain.
        var srcRoot = Path.Combine(repoRoot, "src");
        var referencedKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in Directory.EnumerateFiles(srcRoot, "*.cs", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(srcRoot, path).Replace('\\', '/');
            if (rel.Split('/').Any(s => s is "bin" or "obj"))
            {
                continue;
            }

            var content = File.ReadAllText(path, Encoding.UTF8);
            foreach (Match match in ResourceCallRegex.Matches(content))
            {
                referencedKeys.Add(match.Groups[1].Value);
            }
        }

        var missingFromResx = referencedKeys.Except(enEntries.Keys, StringComparer.Ordinal).OrderBy(k => k).ToArray();
        var unusedInResx = enEntries.Keys.Except(referencedKeys, StringComparer.Ordinal).OrderBy(k => k).ToArray();
        Assert.Empty(missingFromResx);
        Assert.Empty(unusedInResx);
    }

    [Fact]
    public void LanguageResolution_FollowsStrictPriorityNeverFollowsSystemCultureAndPersistsInSettings()
    {
        // Strict priority: --lang > AGP_LANG > settings.json > default en.
        Assert.Equal(AppLanguage.Ja, AppLocalization.ResolveLanguage("ja", "zh-Hans", "en"));
        Assert.Equal(AppLanguage.ZhHans, AppLocalization.ResolveLanguage(null, "zh-Hans", "ja"));
        Assert.Equal(AppLanguage.Ja, AppLocalization.ResolveLanguage(null, null, "ja"));
        Assert.Equal(AppLanguage.En, AppLocalization.ResolveLanguage(null, null, null));

        // Invalid env or saved value falls through cleanly to lower-priority valid value or default en.
        Assert.Equal(AppLanguage.ZhHans, AppLocalization.ResolveLanguage(null, "fr-FR", "zh-Hans"));
        Assert.Equal(AppLanguage.En, AppLocalization.ResolveLanguage(null, "fr-FR", "de-DE"));

        // Case-insensitive parsing works for all three supported codes.
        Assert.True(AppLocalization.TryParseLanguage("EN", out var parsedEn) && parsedEn == AppLanguage.En);
        Assert.True(AppLocalization.TryParseLanguage("zh-hans", out var parsedZh) && parsedZh == AppLanguage.ZhHans);
        Assert.True(AppLocalization.TryParseLanguage("JA", out var parsedJa) && parsedJa == AppLanguage.Ja);
        Assert.False(AppLocalization.TryParseLanguage("zh-CN", out _));
        Assert.False(AppLocalization.TryParseLanguage("ja-JP", out _));

        // Never auto-follows system/OS UI culture.
        var originalUiCulture = CultureInfo.CurrentUICulture;
        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("ja");
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("zh-Hans");
            Assert.Equal(
                AppLanguage.En,
                AppLocalization.ResolveLanguageFromEnvironment(
                    cliLanguage: null,
                    savedSettingLanguage: null,
                    getEnvironmentVariable: _ => null));
        }
        finally
        {
            CultureInfo.CurrentUICulture = originalUiCulture;
            CultureInfo.CurrentCulture = originalCulture;
        }

        // CLI extraction supports both `--lang <val>` and `--lang=<val>`, and leaves option values like `--game --lang` alone.
        var extracted1 = AppLocalization.ExtractCliLanguageArgument(["build-db", "--lang", "ja", "--game", "/tmp/ff5"]);
        Assert.Equal("ja", extracted1.RawLanguage);
        Assert.False(extracted1.MissingValue);
        Assert.Equal(["build-db", "--game", "/tmp/ff5"], extracted1.RemainingArgs);

        var extracted2 = AppLocalization.ExtractCliLanguageArgument(["--lang=zh-Hans", "--help"]);
        Assert.Equal("zh-Hans", extracted2.RawLanguage);
        Assert.False(extracted2.MissingValue);
        Assert.Equal(["--help"], extracted2.RemainingArgs);

        var extractedMissing = AppLocalization.ExtractCliLanguageArgument(["--help", "--lang"]);
        Assert.True(extractedMissing.MissingValue);

        var extractedGameVal = AppLocalization.ExtractCliLanguageArgument(["build-db", "--game", "--lang", "--out", "/tmp/out"]);
        Assert.Null(extractedGameVal.RawLanguage);
        Assert.Equal(["build-db", "--game", "--lang", "--out", "/tmp/out"], extractedGameVal.RemainingArgs);

        // GUI settings round-trip `language` and stay backward-compatible with older settings files lacking `language`.
        using var ws = new TempWorkspace("ffpr-settings-lang");
        var settingsPath = Path.Combine(ws.Root, "settings.json");
        GuiSettingsStore.Save(settingsPath, new GuiSettings
        {
            OutputRoot = Path.Combine(ws.Root, "out"),
            Language = "ja",
        });

        var reloaded = GuiSettingsStore.Load(settingsPath);
        Assert.Equal("ja", reloaded.Language);
        Assert.Equal("ja", GuiSettingsStore.TryPeekSavedLanguage(settingsPath));

        var legacyNoLangPath = Path.Combine(ws.Root, "legacy-settings.json");
        File.WriteAllText(
            legacyNoLangPath,
            """{"format":1,"output_root":"/tmp/out","checked_games":{}}""",
            OutputGuard.Utf8WithoutBom);
        var loadedLegacy = GuiSettingsStore.Load(legacyNoLangPath);
        Assert.Null(loadedLegacy.Language);
        Assert.Null(GuiSettingsStore.TryPeekSavedLanguage(legacyNoLangPath));
    }

    [Fact]
    public async Task BuildDb_ProgressJsonEmitsLanguageIndependentLinesOnlyWhenRequested()
    {
        using var ws = new TempWorkspace("ffpr-progress-json");
        var spec = GameDetector.KnownGames.Single(g => g.Id == "ff5");
        var steamRoot = Path.Combine(ws.Root, "Steam");
        var gameDir = SyntheticUnityFixtureBuilder.BuildSyntheticSteamGame(
            steamRoot,
            spec,
            steamBuildId: "20265087",
            customCommonFolderName: "FINAL FANTASY V PR");
        var peBytes = BuildDbTests.BuildMinimalPeWithVersionResource(
            fileVersion: (2019, 4, 26, 32270),
            productVersion: (1, 0, 0, 0),
            stringFileVersion: "2019.4.26.3227036",
            stringProductVersion: "2019.4.26.3227036");
        await File.WriteAllBytesAsync(Path.Combine(gameDir, spec.ExeName), peBytes);

        // 1. Without --progress-json, no @@progress lines are emitted.
        var outPlain = Path.Combine(ws.Root, "out-plain");
        var plainRes = await TestHarness.RunCliAsync(
            ["build-db", "--game", gameDir, "--out", outPlain, "--jobs", "2"],
            ffprDeps: ws.DepsDir);
        Assert.True(plainRes.ExitCode == 0, $"stdout:\n{plainRes.Stdout}\nstderr:\n{plainRes.Stderr}");
        Assert.DoesNotContain("@@progress", plainRes.Stdout, StringComparison.Ordinal);
        Assert.Contains("==> [ff5] Step 1/6:", plainRes.Stdout, StringComparison.Ordinal);
        Assert.Contains("==> [ff5] Step 6/6:", plainRes.Stdout, StringComparison.Ordinal);

        // 2. With --progress-json across en, zh-Hans, and ja: identical machine-readable protocol + localized human text.
        (string LangCode, string ExpectedHumanMarker)[] cases =
        [
            ("en", "==> [ff5] Step 1/6:"),
            ("zh-Hans", "==> [ff5] 步骤 1/6："),
            ("ja", "==> [ff5] ステップ 1/6："),
        ];

        foreach (var (langCode, expectedMarker) in cases)
        {
            var outLang = Path.Combine(ws.Root, $"out-{langCode}");
            var res = await TestHarness.RunCliAsync(
                ["--lang", langCode, "build-db", "--game", gameDir, "--out", outLang, "--jobs", "2", "--progress-json"],
                ffprDeps: ws.DepsDir);
            Assert.True(res.ExitCode == 0, $"lang={langCode}\nstdout:\n{res.Stdout}\nstderr:\n{res.Stderr}");
            Assert.Contains(expectedMarker, res.Stdout, StringComparison.Ordinal);

            var lines = res.Stdout.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
            var parsedSteps = new List<ProgressStepUpdate>();
            foreach (var line in lines)
            {
                if (CliCommandHelper.TryParseProgressStep(line, out var step))
                {
                    Assert.True(CliCommandHelper.IsProgressProtocolLine(line));
                    parsedSteps.Add(step);
                }
                else
                {
                    Assert.False(CliCommandHelper.IsProgressProtocolLine(line));
                }
            }

            Assert.Equal(6, parsedSteps.Count);
            for (var expectedStep = 1; expectedStep <= 6; expectedStep++)
            {
                Assert.Equal(expectedStep, parsedSteps[expectedStep - 1].CurrentStep);
                Assert.Equal(6, parsedSteps[expectedStep - 1].TotalSteps);
            }
        }
    }

    [Fact]
    public async Task CliSmoke_EnglishDefaultAndExplicitOptionsWorkEndToEnd()
    {
        // Default language (no --lang, no AGP_LANG) must be English.
        var defaultHelp = await TestHarness.RunCliAsync(["--help"]);
        Assert.Equal(0, defaultHelp.ExitCode);
        Assert.Empty(defaultHelp.Stderr);
        Assert.Contains("asset inventory, export, and SQLite build tool", defaultHelp.Stdout, StringComparison.Ordinal);
        Assert.Contains("--lang <code>", defaultHelp.Stdout, StringComparison.Ordinal);
        Assert.Contains("--progress-json", defaultHelp.Stdout, StringComparison.Ordinal);

        // Default error message must be English.
        var defaultErr = await TestHarness.RunCliAsync(["__unknown_cmd__"]);
        Assert.Equal(2, defaultErr.ExitCode);
        Assert.Contains("Error: Unknown subcommand: __unknown_cmd__", defaultErr.Stderr, StringComparison.Ordinal);

        // Invalid --lang returns exit code 2 with clear message.
        var invalidLang = await TestHarness.RunCliAsync(["--lang", "fr", "--help"]);
        Assert.Equal(2, invalidLang.ExitCode);
        Assert.Contains("Unsupported language 'fr'", invalidLang.Stderr, StringComparison.Ordinal);

        // --lang en overrides AGP_LANG=ja.
        var overrideEnv = await TestHarness.RunCliAsync(
            ["--lang", "en", "build-db", "--help"],
            extraEnv: new Dictionary<string, string?> { [AppLocalization.EnvVarName] = "ja" });
        Assert.Equal(0, overrideEnv.ExitCode);
        Assert.Contains("Usage:", overrideEnv.Stdout, StringComparison.Ordinal);
        Assert.Contains("Emit machine-readable @@progress JSON lines", overrideEnv.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CliSmoke_JapaneseViaCliOptionAndEnvVarWorksEndToEnd()
    {
        // 1. --lang ja on --help and invalid command.
        var jaHelp = await TestHarness.RunCliAsync(["--lang", "ja", "--help"]);
        Assert.Equal(0, jaHelp.ExitCode);
        Assert.Empty(jaHelp.Stderr);
        Assert.Contains("アセット棚卸し・エクスポート・SQLite 構築ツール", jaHelp.Stdout, StringComparison.Ordinal);
        Assert.Contains("このヘルプを表示", jaHelp.Stdout, StringComparison.Ordinal);

        var jaErr = await TestHarness.RunCliAsync(["--lang", "ja", "__unknown_cmd__"]);
        Assert.Equal(2, jaErr.ExitCode);
        Assert.Contains("エラー：不明なサブコマンドです：__unknown_cmd__", jaErr.Stderr, StringComparison.Ordinal);

        // 2. AGP_LANG=ja applies when --lang is omitted.
        var jaEnvHelp = await TestHarness.RunCliAsync(
            ["build-db", "--help"],
            extraEnv: new Dictionary<string, string?> { [AppLocalization.EnvVarName] = "ja" });
        Assert.Equal(0, jaEnvHelp.ExitCode);
        Assert.Contains("使い方：", jaEnvHelp.Stdout, StringComparison.Ordinal);
        Assert.Contains("機械可読な @@progress JSON 進捗行を出力", jaEnvHelp.Stdout, StringComparison.Ordinal);

        // 3. Interactive wizard in Japanese (cancel at manual prompt via empty Enter).
        using var ws = new TempWorkspace("ffpr-wizard-ja-smoke");
        var fakeHome = Path.Combine(ws.Root, "empty-home");
        Directory.CreateDirectory(fakeHome);
        var env = new Dictionary<string, string?>
        {
            [AppLocalization.EnvVarName] = "ja",
        };
        if (!OperatingSystem.IsWindows())
        {
            env["HOME"] = fakeHome;
        }

        var jaWizard = await TestHarness.RunCliAsync(
            [],
            ffprDeps: ws.DepsDir,
            standardInput: "q\n\n",
            extraEnv: env,
            agpHome: ws.AgpHomeDir);
        Assert.Equal(0, jaWizard.ExitCode);
        Assert.Contains("エクスポート＆構築ウィザード", jaWizard.Stdout, StringComparison.Ordinal);
        Assert.Contains("操作をキャンセルしました。", jaWizard.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildReportReader_ReevaluatesVerdictsAcrossAllLanguages()
    {
        var spec = GameDetector.KnownGames.Single(g => g.Id == "ff5");
        var game = new GuiGameEntry(spec, "FINAL FANTASY V", "/games/ff5", "20265087", null, false);
        var report = new GuiBuildReport(
            ReportPath: "/out/ff5.sqlite.build.json",
            Status: "completed",
            GameId: "ff5",
            SteamBuildId: "20265087",
            SqlitePath: "/out/ff5.sqlite",
            FileSize: 1024,
            FileSha256: "1111111111111111111111111111111111111111111111111111111111111111",
            ContentSha256: "2222222222222222222222222222222222222222222222222222222222222222",
            ComparisonStatus: "matched",
            ComparisonMessage: "matched",
            ComparisonSource: "embedded",
            MatchedRegisteredAt: "2026-11-25",
            FileSha256MismatchOnly: false,
            TableDiffCount: 0,
            TotalElapsedMs: 1200,
            InventoryExitCode: 0,
            ExportExitCode: 0);

        GuiGameExportResult enResult;
        using (AppLocalization.PushLanguage(AppLanguage.En))
        {
            enResult = BuildReportReader.EvaluateOutcome(game, "/out", exitCode: 0, cancelled: false, report);
            Assert.Equal("Matches registered build", enResult.VerdictShortText);
        }

        using (AppLocalization.PushLanguage(AppLanguage.ZhHans))
        {
            var zhResult = BuildReportReader.ReevaluateForCurrentLanguage(enResult);
            Assert.Equal("与登记的版本一致", zhResult.VerdictShortText);
            Assert.Contains("登记日期：2026-11-25", zhResult.VerdictDetailText, StringComparison.Ordinal);
        }

        using (AppLocalization.PushLanguage(AppLanguage.Ja))
        {
            var jaResult = BuildReportReader.ReevaluateForCurrentLanguage(enResult);
            Assert.Equal("登録済みビルドと一致", jaResult.VerdictShortText);
            Assert.Contains("登録日：2026-11-25", jaResult.VerdictDetailText, StringComparison.Ordinal);
        }
    }

    private static Dictionary<string, string> LoadResxEntries(string path)
    {
        Assert.True(File.Exists(path), $"Missing resource file: {path}");
        var doc = XDocument.Load(path);
        var entries = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var dataEl in doc.Root!.Elements("data"))
        {
            var name = dataEl.Attribute("name")?.Value;
            Assert.False(string.IsNullOrWhiteSpace(name), $"Unnamed <data> entry in {path}");
            var value = dataEl.Element("value")?.Value ?? string.Empty;
            Assert.True(entries.TryAdd(name!, value), $"Duplicate key '{name}' in {path}");
        }

        return entries;
    }

    private static HashSet<string> ExtractPlaceholders(string template)
    {
        var cleaned = template.Replace("{{", string.Empty, StringComparison.Ordinal)
            .Replace("}}", string.Empty, StringComparison.Ordinal);
        return PlaceholderRegex.Matches(cleaned)
            .Select(m => m.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);
    }
}
