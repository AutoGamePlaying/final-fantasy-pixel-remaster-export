namespace FfprExport.Gui;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Any(arg => string.Equals(arg, "--self-test", StringComparison.OrdinalIgnoreCase)))
        {
            var requireCli = args.Any(arg => string.Equals(arg, "--require-cli", StringComparison.OrdinalIgnoreCase));
            return RunSelfTest(requireCli);
        }

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new MainForm());
        return 0;
    }

    /// <summary>
    /// Headless dependency and initialization smoke test used in CI on windows-latest:
    /// loads WinForms assemblies, constructs and disposes MainForm without displaying a window,
    /// and verifies core helper routines and (optionally) the sibling ffpr-export.exe binary.
    /// </summary>
    private static int RunSelfTest(bool requireCli)
    {
        try
        {
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);

            // Verify core helpers and shared SteamLocator load cleanly.
            var defaultRoot = GuiOutputPlanner.GetDefaultOutputRoot();
            var settingsPath = GuiSettingsStore.GetDefaultSettingsPath();
            var sampleArgs = CliCommandHelper.BuildBuildDbArguments(@"C:\Game", Path.Combine(defaultRoot, "ff5-test"));
            _ = SteamLocator.ParseLibraryFoldersVdf(string.Empty);

            if (sampleArgs.Length < 5 || string.IsNullOrWhiteSpace(settingsPath))
            {
                throw new InvalidOperationException("GUI 核心自检失败：参数或设置路径初始化为空");
            }

            // Instantiate MainForm without showing or creating a native window handle to verify WinForms deps.
            using (var form = new MainForm(skipInitialLoad: true))
            {
                if (form.MinimumSize.Width < 720)
                {
                    throw new InvalidOperationException($"主窗口最小宽度不满足要求：{form.MinimumSize.Width}");
                }
            }

            var (cliPath, expectedCliPath) = CliCommandHelper.FindCliExecutable(
                AppContext.BaseDirectory,
                Environment.ProcessPath);
            if (requireCli && cliPath is null)
            {
                Console.Error.WriteLine($"错误：自检失败，同目录缺少 {CliCommandHelper.CliExecutableFileName}：{expectedCliPath}");
                return 2;
            }

            var cliStatus = cliPath is null ? "未检测到同目录 ffpr-export.exe（非打包目录）" : $"已找到 {cliPath}";
            Console.WriteLine($"自检通过：WinForms 与核心依赖加载正常（{cliStatus}）。");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"错误：GUI 自检失败：{ex}");
            return 1;
        }
    }
}
