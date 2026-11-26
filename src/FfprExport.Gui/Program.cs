using System.Runtime.CompilerServices;
using System.Text;

namespace FfprExport.Gui;

internal static class Program
{
    private const string ConfiguredHighDpiModeName = "PerMonitorV2";

    private static bool s_mainFormShown;
    private static int s_fatalDialogShown;
    private static bool s_winFormsConfigured;

    [STAThread]
    private static int Main(string[] args)
    {
        ConfigureRedirectedConsoleEncoding();

        var extractedLang = AppLocalization.ExtractCliLanguageArgument(args);
        var effectiveArgs = extractedLang.RemainingArgs;
        var isSelfTest = effectiveArgs.Any(arg => string.Equals(arg, "--self-test", StringComparison.OrdinalIgnoreCase));
        var requireCli = effectiveArgs.Any(arg => string.Equals(arg, "--require-cli", StringComparison.OrdinalIgnoreCase));

        var rawEnvLanguage = Environment.GetEnvironmentVariable(AppLocalization.EnvVarName);
        var hasValidEnvLanguage = AppLocalization.TryParseLanguage(rawEnvLanguage, out var envLanguage);
        AppLocalization.SetProcessLanguage(hasValidEnvLanguage ? envLanguage : AppLanguage.En);

        if (extractedLang.MissingValue)
        {
            var errMsg = L.Format("Cli_OptionRequiresValue", AppLocalization.CliOptionName);
            if (isSelfTest)
            {
                Console.Error.WriteLine(L.Format("Common_ErrorPrefix", errMsg));
            }
            else
            {
                EnsureWinFormsConfigured(logger: null);
                MessageBox.Show(errMsg, L.Get("Gui_FatalTitleStartup"), MessageBoxButtons.OK, MessageBoxIcon.Error);
            }

            return 2;
        }

        var hasCliLanguageOverride = false;
        if (extractedLang.RawLanguage is not null)
        {
            if (!AppLocalization.TryParseLanguage(extractedLang.RawLanguage, out var cliLanguage))
            {
                var errMsg = L.Format("Lang_InvalidValue", extractedLang.RawLanguage);
                if (isSelfTest)
                {
                    Console.Error.WriteLine(L.Format("Common_ErrorPrefix", errMsg));
                }
                else
                {
                    EnsureWinFormsConfigured(logger: null);
                    MessageBox.Show(errMsg, L.Get("Gui_FatalTitleStartup"), MessageBoxButtons.OK, MessageBoxIcon.Error);
                }

                return 2;
            }

            AppLocalization.SetProcessLanguage(cliLanguage);
            hasCliLanguageOverride = true;
        }

        var hasExternalLanguageOverride = hasCliLanguageOverride || hasValidEnvLanguage;

        var integrity = ProcessIntegrity.Detect();
        var agpRoot = AgpPaths.ResolveAgpHome(integrityOverride: integrity);

        if (!agpRoot.Succeeded)
        {
            if (isSelfTest)
            {
                Console.Error.WriteLine(L.Format("Gui_SelfTestAgpFailed", AgpPaths.FormatCliAllFailedError(agpRoot)));
                return 1;
            }

            agpRoot = ResolveAgpRootInteractively(agpRoot, integrity);
            if (agpRoot is null || !agpRoot.Succeeded)
            {
                return 1;
            }
        }

        if (!hasExternalLanguageOverride)
        {
            var settingsPath = GuiSettingsStore.GetDefaultSettingsPath(agpRoot);
            var legacySettingsPath = GuiSettingsStore.GetLegacySettingsPathForMigration(agpRoot);
            var savedLang = GuiSettingsStore.TryPeekSavedLanguage(settingsPath, legacySettingsPath);
            AppLocalization.SetProcessLanguage(AppLocalization.ResolveLanguage(
                cliLanguage: null,
                envLanguage: null,
                savedSettingLanguage: savedLang));
        }

        // Redirect process-local TEMP/TMP into <agpRoot>\tmp before WinForms initialization so
        // framework internals (e.g. visual-styles activation context) never touch system %TEMP%.
        var tmpConfigured = AgpPaths.TryConfigureProcessTempDirectory(agpRoot, out var configuredTmpDir, out var tmpError);

        var startupInfo = GuiStartupLogger.CollectStartupInfo(
            typeof(Program).Assembly,
            dpiSetting: ConfiguredHighDpiModeName,
            args: args,
            integrity: integrity,
            agpRoot: agpRoot);
        var logger = GuiStartupLogger.Start(startupInfo, agpRoot: agpRoot);

        if (tmpConfigured && !string.IsNullOrWhiteSpace(configuredTmpDir))
        {
            logger.WriteInfo(L.Format("Gui_TempRedirectedLog", configuredTmpDir));
        }
        else if (!string.IsNullOrWhiteSpace(tmpError))
        {
            logger.WriteWarning(L.Format("Gui_TempRedirectFailedLog", tmpError));
        }

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            logger.WriteUnhandledObject(
                "AppDomain.CurrentDomain.UnhandledException",
                e.ExceptionObject,
                e.IsTerminating);
            Environment.ExitCode = 1;

            var ex = e.ExceptionObject as Exception;
            if (isSelfTest)
            {
                WriteSelfTestUnhandledToStderr(logger, ex, e.ExceptionObject);
                return;
            }

            TryShowFatalMessageBoxSafe(
                logger,
                ex,
                isStartupFailure: !Volatile.Read(ref s_mainFormShown));
        };

        try
        {
            return isSelfTest
                ? RunSelfTest(logger, agpRoot, integrity, requireCli, hasExternalLanguageOverride)
                : RunGui(logger, agpRoot, integrity, hasExternalLanguageOverride);
        }
        catch (Exception ex)
        {
            logger.WriteException(isSelfTest ? "Program.Main(--self-test)" : "Program.Main", ex);
            if (isSelfTest)
            {
                Console.Error.WriteLine(GuiStartupLogger.FormatSelfTestFailureMessage(
                    ex,
                    logger.LogFilePath,
                    logger.InitializationWarning));
            }
            else
            {
                TryShowFatalMessageBoxSafe(
                    logger,
                    ex,
                    isStartupFailure: !Volatile.Read(ref s_mainFormShown));
            }

            return 1;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static AgpRootResolution? ResolveAgpRootInteractively(
        AgpRootResolution initialFailure,
        ProcessIntegrityInfo integrity)
    {
        EnsureWinFormsConfigured(logger: null);

        if (initialFailure.ExplicitAgpHomeFailed)
        {
            var message = initialFailure.WarningMessage
                ?? L.Format("Gui_AgpExplicitEnvFailedFallback", AgpPaths.AgpHomeEnvVar);
            MessageBox.Show(
                message,
                L.Get("Gui_DialogTitleDataDirUnwritable"),
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return null;
        }

        var promptText = AgpPaths.FormatGuiAllFailedDialogMessage(initialFailure, integrity);
        var confirm = MessageBox.Show(
            promptText,
            L.Get("Gui_DialogTitleDefaultDataDirFailed"),
            MessageBoxButtons.OKCancel,
            MessageBoxIcon.Warning);
        if (confirm != DialogResult.OK)
        {
            return null;
        }

        while (true)
        {
            using var dialog = new FolderBrowserDialog
            {
                Description = L.Get("Gui_FolderDialogDataRoot"),
                UseDescriptionForTitle = true,
                ShowNewFolderButton = true,
            };

            if (dialog.ShowDialog() != DialogResult.OK || string.IsNullOrWhiteSpace(dialog.SelectedPath))
            {
                return null;
            }

            var chosen = dialog.SelectedPath.Trim();
            if (AgpPaths.TryProbeWritableDirectory(chosen, out var probeError))
            {
                // Best-effort persistence beside the exe; ignore failure if the exe directory is read-only.
                _ = AgpPaths.TrySaveCustomLocation(AppContext.BaseDirectory, chosen, out _);
                return AgpPaths.CreateUserSelectedResolution(chosen, initialFailure, integrity);
            }

            var retry = MessageBox.Show(
                L.Format("Gui_SelectedDirStillUnwritable", chosen, probeError ?? L.Get("Gui_NoWritePermission")),
                L.Get("Gui_DialogTitleSelectedDirUnwritable"),
                MessageBoxButtons.RetryCancel,
                MessageBoxIcon.Warning);
            if (retry != DialogResult.Retry)
            {
                return null;
            }
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int RunGui(
        GuiStartupLogger logger,
        AgpRootResolution agpRoot,
        ProcessIntegrityInfo integrity,
        bool hasExternalLanguageOverride)
    {
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);

        var exitCode = 0;
        Application.ThreadException += (_, e) =>
        {
            logger.WriteException("Application.ThreadException", e.Exception);
            Interlocked.CompareExchange(ref exitCode, 1, 0);
            Environment.ExitCode = 1;

            TryShowFatalMessageBox(
                logger,
                e.Exception,
                isStartupFailure: !Volatile.Read(ref s_mainFormShown));

            try
            {
                Application.Exit();
            }
            catch (Exception exitEx)
            {
                logger.WriteException("Application.Exit", exitEx);
            }
        };

        EnsureWinFormsConfigured(logger);

        try
        {
            logger.WriteInfo(L.Get("Gui_LogConstructingMainForm"));
            using var mainForm = new MainForm(
                startupLogger: logger,
                agpRoot: agpRoot,
                integrityInfo: integrity,
                hasExternalLanguageOverride: hasExternalLanguageOverride);
            mainForm.Shown += (_, _) =>
            {
                Volatile.Write(ref s_mainFormShown, true);
                logger.WriteInfo(L.Get("Gui_LogMainFormShown"));
            };

            logger.WriteInfo(L.Get("Gui_LogEnteringMessageLoop"));
            Application.Run(mainForm);
            logger.WriteInfo(L.Format("Gui_LogExiting", exitCode));
            return exitCode;
        }
        catch (Exception ex)
        {
            logger.WriteException("MainForm/Application.Run", ex);
            TryShowFatalMessageBox(
                logger,
                ex,
                isStartupFailure: !Volatile.Read(ref s_mainFormShown));
            return 1;
        }
    }

    /// <summary>
    /// Headless smoke test used in CI on windows-latest and after single-file packaging:
    /// configures WinForms DPI and visual styles, verifies core helpers, constructs MainForm
    /// through the real startup path, invokes CreateControl() and native handle creation without
    /// displaying a window, runs initial state loading, disposes the form, and optionally checks
    /// the sibling ffpr-export.exe binary.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int RunSelfTest(
        GuiStartupLogger logger,
        AgpRootResolution agpRoot,
        ProcessIntegrityInfo integrity,
        bool requireCli,
        bool hasExternalLanguageOverride)
    {
        Exception? uiThreadException = null;
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) =>
        {
            uiThreadException ??= e.Exception;
            logger.WriteException("RunSelfTest.ThreadException", e.Exception);
        };

        try
        {
            EnsureWinFormsConfigured(logger);

            // Verify core helpers and shared SteamLocator load cleanly.
            var defaultRoot = GuiOutputPlanner.ResolveDefaultOutputRoot(agpRoot);
            var settingsPath = GuiSettingsStore.GetDefaultSettingsPath(agpRoot);
            var sampleArgs = CliCommandHelper.BuildBuildDbArguments(
                @"C:\Game",
                Path.Combine(defaultRoot.OutputRoot, "ff5-test"),
                language: AppLocalization.CurrentLanguageCode,
                progressJson: true);
            _ = SteamLocator.ParseLibraryFoldersVdf(string.Empty);

            if (sampleArgs.Length < 5
                || string.IsNullOrWhiteSpace(settingsPath)
                || string.IsNullOrWhiteSpace(defaultRoot.OutputRoot))
            {
                throw new InvalidOperationException(L.Get("Gui_SelfTestCoreFailed"));
            }

            // Walk the real MainForm construction, native handle creation (without Show),
            // initial load (settings, output root, Steam scan, CLI check), and disposal.
            logger.WriteInfo(L.Get("Gui_SelfTestConstructingLog"));
            using (var form = new MainForm(
                startupLogger: logger,
                agpRoot: agpRoot,
                integrityInfo: integrity,
                hasExternalLanguageOverride: hasExternalLanguageOverride))
            {
                form.CreateControl();
                form.RunSelfTestInitialization();

                if (form.MinimumSize.Width < 720)
                {
                    throw new InvalidOperationException(L.Format("Gui_SelfTestMinWidthFailed", form.MinimumSize.Width));
                }

                if (!form.IsHandleCreated)
                {
                    throw new InvalidOperationException(L.Get("Gui_SelfTestHandleFailed"));
                }

                logger.WriteInfo(L.Format("Gui_SelfTestConstructedLog", form.MinimumSize));
            }

            if (uiThreadException is not null)
            {
                throw uiThreadException;
            }

            var cliLookup = CliCommandHelper.FindCliExecutableSafe(
                AppContext.BaseDirectory,
                Environment.ProcessPath);
            if (requireCli && cliLookup.ResolvedPath is null)
            {
                var detail = cliLookup.WarningMessage
                    ?? L.Format("Gui_SelfTestMissingCliFallback", CliCommandHelper.CliExecutableFileName, cliLookup.ExpectedPath);
                var missingCliEx = new FileNotFoundException(detail, cliLookup.ExpectedPath);
                logger.WriteException("RunSelfTest.RequireCli", missingCliEx);
                Console.Error.WriteLine(GuiStartupLogger.FormatSelfTestFailureMessage(
                    missingCliEx,
                    logger.LogFilePath,
                    logger.InitializationWarning));
                return 2;
            }

            var cliStatus = cliLookup.ResolvedPath is null
                ? L.Get("Gui_SelfTestCliNotFound")
                : L.Format("Gui_SelfTestCliFound", cliLookup.ResolvedPath);
            var logStatus = string.IsNullOrWhiteSpace(logger.LogFilePath)
                ? L.Format("Gui_SelfTestLogUnavailable", logger.InitializationWarning ?? L.Get("Common_Unknown"))
                : L.Format("Gui_SelfTestLogPath", logger.LogFilePath);
            logger.WriteInfo(L.Format("Gui_SelfTestPassedLog", cliStatus, integrity.LogLabel, agpRoot.AgpHome));
            Console.WriteLine(L.Format("Gui_SelfTestPassedStdout", cliStatus, integrity.LogLabel, agpRoot.AgpHome, logStatus));
            return 0;
        }
        catch (Exception ex)
        {
            logger.WriteException("RunSelfTest", ex);
            Console.Error.WriteLine(GuiStartupLogger.FormatSelfTestFailureMessage(
                ex,
                logger.LogFilePath,
                logger.InitializationWarning));
            return 1;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void EnsureWinFormsConfigured(GuiStartupLogger? logger)
    {
        if (!s_winFormsConfigured)
        {
            var dpiApplied = Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            logger?.WriteInfo(L.Format("Gui_DpiConfiguredLog", Application.HighDpiMode, dpiApplied));
            Application.SetCompatibleTextRenderingDefault(false);
            s_winFormsConfigured = true;
        }

        // EnableVisualStyles can extract an activation-context manifest to TEMP in single-file apps;
        // TEMP is already redirected to <agpRoot>\tmp when available, and app.manifest also enables
        // Common-Controls v6 natively, so catch any failure instead of crashing startup.
        try
        {
            Application.EnableVisualStyles();
        }
        catch (Exception ex)
        {
            logger?.WriteWarning(L.Format("Gui_EnableVisualStylesFailedLog", ex.Message));
        }
    }

    private static void TryShowFatalMessageBoxSafe(
        GuiStartupLogger logger,
        Exception? exception,
        bool isStartupFailure)
    {
        try
        {
            TryShowFatalMessageBox(logger, exception, isStartupFailure);
        }
        catch (Exception ex)
        {
            logger.WriteException("TryShowFatalMessageBoxSafe", ex);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void TryShowFatalMessageBox(
        GuiStartupLogger logger,
        Exception? exception,
        bool isStartupFailure)
    {
        if (Interlocked.Exchange(ref s_fatalDialogShown, 1) != 0)
        {
            return;
        }

        try
        {
            var title = isStartupFailure ? L.Get("Gui_FatalTitleStartup") : L.Get("Gui_FatalTitleRuntime");
            var text = GuiStartupLogger.FormatFatalDialogMessage(
                exception,
                logger.LogFilePath,
                logger.InitializationWarning,
                isStartupFailure);
            MessageBox.Show(text, title, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        catch (Exception mbEx)
        {
            logger.WriteException("MessageBox.Show", mbEx);
        }
    }

    private static void WriteSelfTestUnhandledToStderr(
        GuiStartupLogger logger,
        Exception? exception,
        object? rawObject)
    {
        try
        {
            if (exception is not null)
            {
                Console.Error.WriteLine(GuiStartupLogger.FormatSelfTestFailureMessage(
                    exception,
                    logger.LogFilePath,
                    logger.InitializationWarning));
                return;
            }

            Console.Error.WriteLine(L.Get("Gui_SelfTestUnhandledNonException"));
            if (!string.IsNullOrWhiteSpace(logger.LogFilePath))
            {
                Console.Error.WriteLine(L.Format("Gui_SelfTestLogFileLine", logger.LogFilePath));
            }

            Console.Error.WriteLine(rawObject?.ToString() ?? "(null)");
        }
        catch
        {
            // Never throw from an AppDomain.UnhandledException handler.
        }
    }

    private static void ConfigureRedirectedConsoleEncoding()
    {
        try
        {
            if (Console.IsOutputRedirected || Console.IsErrorRedirected)
            {
                Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
            }
        }
        catch
        {
            // Ignore console encoding setup failures on WinExe without an attached console.
        }
    }
}
