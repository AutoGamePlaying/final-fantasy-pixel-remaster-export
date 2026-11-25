namespace FfprExport;

internal static class Program
{
    public const string ToolVersion = "0.1.0";
    public const string AssetsToolsVersion = "3.0.5";
    public const string AssetsToolsCpp2IlVersion = "3.0.4";

    public static async Task<int> Main(string[] args)
    {
        try
        {
            return await RunAsync(args);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"错误：{ex.Message}");
            return 2;
        }
    }

    private static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0)
        {
            return await InteractiveWizard.RunAsync();
        }

        switch (args[0])
        {
            case "--help":
            case "-h":
            case "help":
                PrintHelp();
                return 0;
            case "--version":
            case "-v":
                Console.WriteLine($"{OutputGuard.ToolName} {ToolVersion} (AssetsTools.NET {AssetsToolsVersion}, AssetsTools.NET.Cpp2IL {AssetsToolsCpp2IlVersion})");
                return 0;
            case "inventory":
                return await InventoryCommand.RunAsync(args[1..]);
            case "export":
                return await ExportCommand.RunAsync(args[1..]);
            case "build-db":
                return await BuildDbCommand.RunAsync(args[1..]);
            case "db":
                return await Db.DbCommand.RunAsync(args[1..]);
            default:
                throw new ArgumentException($"未知子命令：{args[0]}；可用命令：inventory、export");
        }
    }

    internal static string RequireValue(string[] args, ref int index, string option)
    {
        if (index + 1 >= args.Length)
        {
            throw new ArgumentException($"选项 {option} 需要一个值");
        }

        index++;
        return args[index];
    }

    private static void PrintHelp()
    {
        Console.WriteLine(
            $"""
            {OutputGuard.ToolName} {ToolVersion} — FINAL FANTASY I–VI Pixel Remaster 资源盘点与导出工具

            用法：
              ffpr-export inventory --game <Steam 游戏目录> [--out <输出目录>] [--jobs N] [--force]
              ffpr-export export    --game <Steam 游戏目录> [--out <输出目录>] [--jobs N] [--force]

            选项：
              --game <目录>   Steam 游戏安装目录（自动识别 FF1–FF6、Unity 版本与 Steam buildid）
              --out <目录>    输出目录（默认 .deps/ffpr/<ffN>/inventory 或 export，可通过 FFPR_DEPS 覆盖基准目录）
              --jobs <N>      并行线程数（默认 min(CPU 核数, 8)）
              --force         清空已有输出目录（仅允许清空带本工具同名子命令 manifest.json 的目录）
              --version       打印工具及 AssetsTools.NET 版本
              --help          打印本帮助信息
            """);
    }
}
