namespace FfprExport;

internal static class Program
{
    public const string ToolVersion = "0.1.0";
    public const string AssetsToolsVersion = "3.0.5";
    public const string AssetsToolsCpp2IlVersion = "3.0.4";

    public static async Task<int> Main(string[] args)
    {
        if (Console.IsOutputRedirected || Console.IsErrorRedirected)
        {
            Console.OutputEncoding = OutputGuard.Utf8WithoutBom;
        }

        try
        {
            var remainingArgs = AppLocalization.ApplyCliLanguageOrThrow(args);
            return await RunAsync(remainingArgs);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(L.Format("Common_ErrorPrefix", ex.Message));
            return 2;
        }
    }

    /// <summary>
    /// In-process subcommand entry point that preserves the currently active language without
    /// re-reading AGP_LANG (used by BuildDbCommand when invoking `db build` and `db hash`).
    /// </summary>
    internal static async Task<int> RunInProcessAsync(string[] args)
    {
        try
        {
            return await RunAsync(args);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(L.Format("Common_ErrorPrefix", ex.Message));
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
                throw new ArgumentException(L.Format("Cli_UnknownSubcommand", args[0]));
        }
    }

    internal static string RequireValue(string[] args, ref int index, string option)
    {
        if (index + 1 >= args.Length)
        {
            throw new ArgumentException(L.Format("Cli_OptionRequiresValue", option));
        }

        index++;
        return args[index];
    }

    private static void PrintHelp()
    {
        Console.WriteLine(L.Format("Cli_HelpText", OutputGuard.ToolName, ToolVersion));
    }
}
