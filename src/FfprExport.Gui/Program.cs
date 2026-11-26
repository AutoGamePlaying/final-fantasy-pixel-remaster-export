using System.Runtime.CompilerServices;
using System.Text;

namespace FfprExport.Gui;

internal static class Program
{
    private const string ConfiguredHighDpiModeName = "PerMonitorV2";

    private static bool s_mainFormShown;
    private static int s_fatalDialogShown;

    [STAThread]
    private static int Main(string[] args)
    {
        ConfigureRedirectedConsoleEncoding();

        var isSelfTest = args.Any(arg => string.Equals(arg, "--self-test", StringComparison.OrdinalIgnoreCase));
        var requireCli = args.Any(arg => string.Equals(arg, "--require-cli", StringComparison.OrdinalIgnoreCase));

        var startupInfo = GuiStartupLogger.CollectStartupInfo(
            typeof(Program).Assembly,
            dpiSetting: ConfiguredHighDpiModeName,
            args: args);
        var logger = GuiStartupLogger.Start(startupInfo);

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
                ? RunSelfTest(logger, requireCli)
                : RunGui(logger);
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
    private static int RunGui(GuiStartupLogger logger)
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

        var dpiApplied = Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        logger.WriteInfo($"DPI 模式已配置：actual={Application.HighDpiMode}, setResult={dpiApplied}");

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        try
        {
            logger.WriteInfo("正在构造主窗口 MainForm...");
            using var mainForm = new MainForm(startupLogger: logger);
            mainForm.Shown += (_, _) =>
            {
                Volatile.Write(ref s_mainFormShown, true);
                logger.WriteInfo("主窗口已显示（Shown）。");
            };

            logger.WriteInfo("主窗口构造完成，正在进入消息循环 Application.Run...");
            Application.Run(mainForm);
            logger.WriteInfo($"窗口程序退出（退出码：{exitCode}）。");
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
    private static int RunSelfTest(GuiStartupLogger logger, bool requireCli)
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
            var dpiApplied = Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            logger.WriteInfo($"[self-test] DPI 模式已配置：actual={Application.HighDpiMode}, setResult={dpiApplied}");
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // Verify core helpers and shared SteamLocator load cleanly.
            var defaultRoot = GuiOutputPlanner.ResolveDefaultOutputRoot();
            var settingsPath = GuiSettingsStore.GetDefaultSettingsPath();
            var sampleArgs = CliCommandHelper.BuildBuildDbArguments(
                @"C:\Game",
                Path.Combine(defaultRoot.OutputRoot, "ff5-test"));
            _ = SteamLocator.ParseLibraryFoldersVdf(string.Empty);

            if (sampleArgs.Length < 5
                || string.IsNullOrWhiteSpace(settingsPath)
                || string.IsNullOrWhiteSpace(defaultRoot.OutputRoot))
            {
                throw new InvalidOperationException("GUI 核心自检失败：参数、默认输出目录或设置路径初始化为空");
            }

            // Walk the real MainForm construction, native handle creation (without Show),
            // initial load (settings, output root, Steam scan, CLI check), and disposal.
            logger.WriteInfo("[self-test] 正在构造 MainForm 并执行 CreateControl() 与初始化流程...");
            using (var form = new MainForm(startupLogger: logger))
            {
                form.CreateControl();
                form.RunSelfTestInitialization();

                if (form.MinimumSize.Width < 720)
                {
                    throw new InvalidOperationException($"主窗口最小宽度不满足要求：{form.MinimumSize.Width}");
                }

                if (!form.IsHandleCreated)
                {
                    throw new InvalidOperationException("主窗口句柄未能成功创建（IsHandleCreated=false）");
                }

                logger.WriteInfo($"[self-test] MainForm 构造、CreateControl() 与初始化完成（MinimumSize={form.MinimumSize}）。");
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
                    ?? $"同目录缺少 {CliCommandHelper.CliExecutableFileName}：{cliLookup.ExpectedPath}";
                var missingCliEx = new FileNotFoundException(detail, cliLookup.ExpectedPath);
                logger.WriteException("RunSelfTest.RequireCli", missingCliEx);
                Console.Error.WriteLine(GuiStartupLogger.FormatSelfTestFailureMessage(
                    missingCliEx,
                    logger.LogFilePath,
                    logger.InitializationWarning));
                return 2;
            }

            var cliStatus = cliLookup.ResolvedPath is null
                ? "未检测到同目录 ffpr-export.exe（非打包目录）"
                : $"已找到 {cliLookup.ResolvedPath}";
            var logStatus = string.IsNullOrWhiteSpace(logger.LogFilePath)
                ? $"日志不可用（{logger.InitializationWarning ?? "未知原因"}）"
                : $"日志：{logger.LogFilePath}";
            logger.WriteInfo($"[self-test] 自检通过（{cliStatus}）。");
            Console.WriteLine($"自检通过：WinForms 主窗口构造、CreateControl() 与核心依赖加载正常（{cliStatus}；{logStatus}）。");
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
            var title = isStartupFailure ? "窗口程序启动失败" : "窗口程序运行出错";
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

            Console.Error.WriteLine("错误：GUI 自检遇到非 Exception 未处理错误。");
            if (!string.IsNullOrWhiteSpace(logger.LogFilePath))
            {
                Console.Error.WriteLine($"日志文件：{logger.LogFilePath}");
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
