using System.Text;
using System.Text.Json;

namespace FfprExport.Gui;

internal sealed record GuiOutputRootResolution(
    string OutputRoot,
    bool UsedFallback,
    string? WarningMessage,
    bool Succeeded = true);

/// <summary>
/// Computes default per-game output paths under &lt;AGP 根目录&gt;\ffpr-export\output\&lt;ffN&gt;-&lt;buildid&gt;,
/// inspects existing output directories, resolves the folder to open, and plans safe exports.
/// </summary>
internal static class GuiOutputPlanner
{
    public const string ToolName = AgpPaths.ToolName;

    public static string GetDefaultOutputRoot(
        bool? isWindowsOverride = null,
        Func<string, string?>? getEnvironmentVariable = null,
        Func<Environment.SpecialFolder, string?>? getSpecialFolder = null,
        Func<string?>? getBaseDirectory = null,
        Func<string?>? getLocalLowDirectory = null,
        Func<string, (bool Writable, string? Error)>? probeWritable = null,
        Func<string, string?>? readLocationFile = null,
        ProcessIntegrityInfo? integrityOverride = null,
        Func<ProcessIntegrityInfo>? detectIntegrity = null)
    {
        return ResolveDefaultOutputRoot(
            isWindowsOverride,
            getEnvironmentVariable,
            getSpecialFolder,
            getBaseDirectory,
            getLocalLowDirectory,
            probeWritable,
            readLocationFile,
            integrityOverride,
            detectIntegrity).OutputRoot;
    }

    /// <summary>
    /// Resolves the default export root from an already-resolved AGP root.
    /// Never throws; propagates any Chinese diagnostic warning when a fallback root is chosen.
    /// </summary>
    public static GuiOutputRootResolution ResolveDefaultOutputRoot(AgpRootResolution root)
    {
        if (!root.Succeeded || string.IsNullOrWhiteSpace(root.AgpHome))
        {
            return new GuiOutputRootResolution(
                string.Empty,
                UsedFallback: true,
                WarningMessage: root.WarningMessage ?? "未能确定可写的数据根目录",
                Succeeded: false);
        }

        return new GuiOutputRootResolution(
            AgpPaths.GetOutputRoot(root),
            root.UsedFallback,
            root.WarningMessage,
            Succeeded: true);
    }

    /// <summary>
    /// Resolves the default export root via the shared AgpPaths root resolver.
    /// Never throws; propagates any Chinese diagnostic warning when a fallback root is chosen.
    /// </summary>
    public static GuiOutputRootResolution ResolveDefaultOutputRoot(
        bool? isWindowsOverride = null,
        Func<string, string?>? getEnvironmentVariable = null,
        Func<Environment.SpecialFolder, string?>? getSpecialFolder = null,
        Func<string?>? getBaseDirectory = null,
        Func<string?>? getLocalLowDirectory = null,
        Func<string, (bool Writable, string? Error)>? probeWritable = null,
        Func<string, string?>? readLocationFile = null,
        ProcessIntegrityInfo? integrityOverride = null,
        Func<ProcessIntegrityInfo>? detectIntegrity = null)
    {
        var root = AgpPaths.ResolveAgpHome(
            isWindowsOverride,
            getEnvironmentVariable,
            getSpecialFolder,
            getBaseDirectory,
            getLocalLowDirectory,
            probeWritable,
            readLocationFile,
            integrityOverride,
            detectIntegrity);
        return ResolveDefaultOutputRoot(root);
    }

    /// <summary>
    /// Resolves the actual directory that the GUI "打开输出文件夹" button should open:
    /// prefers the selected export result directory, then the sole completed result directory,
    /// then the current output root text box value, and finally the parent of a failed result path.
    /// </summary>
    public static string? ResolveFolderToOpen(
        string? selectedResultOutputDirectory,
        IReadOnlyList<string>? resultOutputDirectories,
        string? currentOutputRootText,
        Func<string, bool>? directoryExists = null)
    {
        var existsCheck = directoryExists ?? TryDirectoryExistsSafe;

        if (TryResolveExistingDirectory(selectedResultOutputDirectory, existsCheck, out var selectedDir))
        {
            return selectedDir;
        }

        if (resultOutputDirectories is { Count: 1 }
            && TryResolveExistingDirectory(resultOutputDirectories[0], existsCheck, out var singleDir))
        {
            return singleDir;
        }

        var normalizedRoot = GuiGameScanner.NormalizeUserPath(currentOutputRootText);
        if (TryResolveExistingDirectory(normalizedRoot, existsCheck, out var rootDir))
        {
            return rootDir;
        }

        if (!string.IsNullOrWhiteSpace(selectedResultOutputDirectory))
        {
            try
            {
                var parent = Path.GetDirectoryName(PathSupport.TrimTrailingSeparators(selectedResultOutputDirectory.Trim()));
                if (TryResolveExistingDirectory(parent, existsCheck, out var parentDir))
                {
                    return parentDir;
                }
            }
            catch
            {
                // Ignore invalid paths when probing fallback parent directory.
            }
        }

        return null;
    }

    private static bool TryResolveExistingDirectory(
        string? candidate,
        Func<string, bool> existsCheck,
        out string resolved)
    {
        resolved = string.Empty;
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return false;
        }

        var trimmed = candidate.Trim();
        try
        {
            if (!existsCheck(trimmed))
            {
                return false;
            }
        }
        catch
        {
            return false;
        }

        try
        {
            resolved = PathSupport.TrimTrailingSeparators(Path.GetFullPath(trimmed));
        }
        catch
        {
            resolved = PathSupport.TrimTrailingSeparators(trimmed);
        }

        return !string.IsNullOrWhiteSpace(resolved);
    }

    private static bool TryDirectoryExistsSafe(string path)
    {
        try
        {
            return Directory.Exists(PathSupport.ToIoPath(path));
        }
        catch
        {
            return false;
        }
    }

    public static string BuildGameFolderName(string gameId, string? steamBuildId)
    {
        return AgpPaths.BuildGameFolderName(gameId, steamBuildId);
    }

    public static string BuildGameOutputDirectory(string outputRoot, string gameId, string? steamBuildId)
    {
        var normalizedRoot = GuiGameScanner.NormalizeUserPath(outputRoot);
        if (string.IsNullOrWhiteSpace(normalizedRoot))
        {
            throw new ArgumentException("输出根目录不能为空");
        }

        var fullRoot = PathSupport.TrimTrailingSeparators(Path.GetFullPath(normalizedRoot));
        var folderName = BuildGameFolderName(gameId, steamBuildId);
        return PathSupport.TrimTrailingSeparators(Path.Combine(fullRoot, folderName));
    }

    public static OutputDirectoryState InspectOutputDirectoryState(string directoryPath)
    {
        var ioPath = PathSupport.ToIoPath(directoryPath);
        if (!Directory.Exists(ioPath) || !Directory.EnumerateFileSystemEntries(ioPath).Any())
        {
            return OutputDirectoryState.EmptyOrMissing;
        }

        var manifestPath = Path.Combine(ioPath, "manifest.json");
        if (!File.Exists(manifestPath))
        {
            return OutputDirectoryState.ForeignNonEmptyDirectory;
        }

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath, Encoding.UTF8));
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return OutputDirectoryState.ForeignNonEmptyDirectory;
            }

            var isOwnTool = doc.RootElement.TryGetProperty("tool", out var toolEl)
                && string.Equals(toolEl.GetString(), ToolName, StringComparison.Ordinal);
            var isBuildDb = doc.RootElement.TryGetProperty("command", out var cmdEl)
                && string.Equals(cmdEl.GetString(), "build-db", StringComparison.Ordinal);

            return isOwnTool && isBuildDb
                ? OutputDirectoryState.OwnBuildDbDirectory
                : OutputDirectoryState.ForeignNonEmptyDirectory;
        }
        catch
        {
            return OutputDirectoryState.ForeignNonEmptyDirectory;
        }
    }

    public static void ValidateTargetOutputPath(string outputDirectory, IEnumerable<string> protectedGameDirectories)
    {
        if (string.IsNullOrWhiteSpace(outputDirectory))
        {
            throw new ArgumentException("输出目录不能为空");
        }

        var fullOut = PathSupport.TrimTrailingSeparators(Path.GetFullPath(outputDirectory));
        if (IsFilesystemRoot(fullOut))
        {
            throw new ArgumentException($"输出目录不能直接指向磁盘根目录：{fullOut}");
        }

        foreach (var gameDir in protectedGameDirectories)
        {
            if (string.IsNullOrWhiteSpace(gameDir))
            {
                continue;
            }

            var fullGame = PathSupport.TrimTrailingSeparators(Path.GetFullPath(gameDir));
            if (PathSupport.IsSameOrDescendantPath(fullGame, fullOut)
                || PathSupport.IsSameOrDescendantPath(fullOut, fullGame))
            {
                throw new ArgumentException($"输出目录不能与游戏目录重叠或互为父子目录：{fullOut}");
            }
        }
    }

    public static IReadOnlyList<PlannedGameExport> CreateExportPlan(
        string outputRoot,
        IReadOnlyList<GuiGameEntry> selectedGames)
    {
        if (selectedGames.Count == 0)
        {
            throw new ArgumentException("请至少勾选一个要导出的游戏");
        }

        var normalizedRoot = GuiGameScanner.NormalizeUserPath(outputRoot);
        if (string.IsNullOrWhiteSpace(normalizedRoot))
        {
            throw new ArgumentException("请先填写或选择一个有效的输出根目录");
        }

        string fullRoot;
        try
        {
            fullRoot = PathSupport.TrimTrailingSeparators(Path.GetFullPath(normalizedRoot));
        }
        catch (Exception ex)
        {
            throw new ArgumentException($"输出根目录格式无效：{normalizedRoot}（{ex.Message}）", ex);
        }

        var allGameDirs = selectedGames.Select(g => g.GameDirectory).ToArray();
        var seenTargets = new HashSet<string>(GuiGameScanner.PathComparer);
        var plans = new List<PlannedGameExport>(selectedGames.Count);

        foreach (var game in selectedGames)
        {
            var targetDir = BuildGameOutputDirectory(fullRoot, game.Spec.Id, game.SteamBuildId);
            ValidateTargetOutputPath(targetDir, allGameDirs);

            if (!seenTargets.Add(targetDir))
            {
                throw new ArgumentException(
                    $"选中的多个游戏映射到了相同的输出目录：{targetDir}（请取消重复勾选或分批导出）");
            }

            var state = InspectOutputDirectoryState(targetDir);
            var useForce = state == OutputDirectoryState.OwnBuildDbDirectory;
            plans.Add(new PlannedGameExport(game, targetDir, state, useForce));
        }

        return plans;
    }

    /// <summary>
    /// Deletes existing entries inside a user-confirmed foreign output directory so the CLI
    /// build-db subcommand (which intentionally refuses to purge foreign folders) can build into it.
    /// </summary>
    public static void ClearConfirmedForeignDirectory(
        string directoryPath,
        IEnumerable<string> protectedGameDirectories)
    {
        ValidateTargetOutputPath(directoryPath, protectedGameDirectories);
        var ioPath = PathSupport.ToIoPath(directoryPath);
        if (!Directory.Exists(ioPath))
        {
            return;
        }

        foreach (var entry in Directory.EnumerateFileSystemEntries(ioPath))
        {
            if (Directory.Exists(entry))
            {
                Directory.Delete(entry, recursive: true);
            }
            else
            {
                File.Delete(entry);
            }
        }
    }

    public static string FormatOutputPreview(string outputRoot, IReadOnlyList<GuiGameEntry> checkedGames)
    {
        var normalizedRoot = GuiGameScanner.NormalizeUserPath(outputRoot);
        if (string.IsNullOrWhiteSpace(normalizedRoot))
        {
            return "子目录预览：请先指定输出根目录";
        }

        if (checkedGames.Count == 0)
        {
            return "子目录预览：请至少勾选一个游戏";
        }

        try
        {
            if (checkedGames.Count == 1)
            {
                var single = checkedGames[0];
                var singlePath = BuildGameOutputDirectory(normalizedRoot, single.Spec.Id, single.SteamBuildId);
                return $"子目录预览：{singlePath}{Path.DirectorySeparatorChar}";
            }

            var first = checkedGames[0];
            var firstFolder = BuildGameFolderName(first.Spec.Id, first.SteamBuildId);
            var second = checkedGames[1];
            var secondFolder = BuildGameFolderName(second.Spec.Id, second.SteamBuildId);
            var suffix = checkedGames.Count > 2 ? $" 等 {checkedGames.Count} 个子目录" : " 共 2 个子目录";
            return $"子目录预览：将分别输出到 {firstFolder}、{secondFolder}{suffix}";
        }
        catch
        {
            return "子目录预览：输出根目录路径格式无效";
        }
    }

    public static string SanitizeBuildIdForFolder(string? steamBuildId)
    {
        return AgpPaths.SanitizeBuildIdForFolder(steamBuildId);
    }

    private static bool IsFilesystemRoot(string fullPath)
    {
        var root = Path.GetPathRoot(fullPath);
        if (string.IsNullOrEmpty(root))
        {
            return false;
        }

        return string.Equals(
            PathSupport.TrimTrailingSeparators(fullPath),
            PathSupport.TrimTrailingSeparators(root),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }
}
