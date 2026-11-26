using System.Text;
using System.Text.Json;

namespace FfprExport.Gui;

internal sealed record GuiOutputRootResolution(
    string OutputRoot,
    bool UsedFallback,
    string? WarningMessage);

/// <summary>
/// Computes default per-game output paths under Documents\FF-Export\&lt;ffN&gt;-&lt;buildid&gt;,
/// inspects existing output directories, and plans safe exports.
/// </summary>
internal static class GuiOutputPlanner
{
    public const string ToolName = "ffpr-export";
    public const string DefaultExportRootFolderName = "FF-Export";

    public static string GetDefaultOutputRoot(
        string? documentsDirectory = null,
        string? userProfileDirectory = null,
        string? fallbackBaseDirectory = null)
    {
        if (!string.IsNullOrWhiteSpace(documentsDirectory))
        {
            return PathSupport.TrimTrailingSeparators(
                Path.Combine(documentsDirectory.Trim(), DefaultExportRootFolderName));
        }

        if (!string.IsNullOrWhiteSpace(userProfileDirectory))
        {
            return PathSupport.TrimTrailingSeparators(
                Path.Combine(userProfileDirectory.Trim(), "Documents", DefaultExportRootFolderName));
        }

        var resolved = ResolveDefaultOutputRoot(
            getBaseDirectory: () => string.IsNullOrWhiteSpace(fallbackBaseDirectory)
                ? AppContext.BaseDirectory
                : fallbackBaseDirectory.Trim());
        return resolved.OutputRoot;
    }

    /// <summary>
    /// Resolves the default export root while guarding against redirected, missing, or inaccessible
    /// Documents / UserProfile folders. Never throws; returns a Chinese diagnostic warning when a
    /// fallback location is chosen so the GUI can inform the user and let them browse manually.
    /// </summary>
    public static GuiOutputRootResolution ResolveDefaultOutputRoot(
        Func<string?>? getDocumentsDirectory = null,
        Func<string?>? getUserProfileDirectory = null,
        Func<string?>? getBaseDirectory = null,
        Func<string?>? getTempDirectory = null,
        Func<string, bool>? directoryExists = null)
    {
        var existsCheck = directoryExists ?? TryDirectoryExistsSafe;
        var docsReason = string.Empty;

        var systemDocs = TryInvokePathProvider(
            getDocumentsDirectory ?? (() => Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)),
            out var docsError);
        if (!string.IsNullOrWhiteSpace(systemDocs))
        {
            if (TryCheckDirectoryExists(systemDocs, existsCheck, out var existsError))
            {
                if (TryCombineOutputRoot(systemDocs, DefaultExportRootFolderName, out var docsOutRoot))
                {
                    return new GuiOutputRootResolution(docsOutRoot, UsedFallback: false, WarningMessage: null);
                }

                docsReason = $"「文档」路径格式无效：{systemDocs}";
            }
            else
            {
                docsReason = string.IsNullOrWhiteSpace(existsError)
                    ? $"「文档」目录不存在或被重定向到不可用位置：{systemDocs}"
                    : $"检查「文档」目录失败（{systemDocs}）：{existsError}";
            }
        }
        else
        {
            docsReason = string.IsNullOrWhiteSpace(docsError)
                ? "系统未返回「文档」目录路径"
                : $"读取系统「文档」目录失败：{docsError}";
        }

        var systemProfile = TryInvokePathProvider(
            getUserProfileDirectory ?? (() => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)),
            out _);
        if (!string.IsNullOrWhiteSpace(systemProfile)
            && TryCheckDirectoryExists(systemProfile, existsCheck, out _))
        {
            var profileDocs = TryCombine(systemProfile, "Documents");
            if (!string.IsNullOrWhiteSpace(profileDocs)
                && TryCheckDirectoryExists(profileDocs, existsCheck, out _)
                && TryCombineOutputRoot(profileDocs, DefaultExportRootFolderName, out var profileDocsOut))
            {
                return new GuiOutputRootResolution(
                    profileDocsOut,
                    UsedFallback: true,
                    WarningMessage: $"默认输出位置回退：{docsReason}；已改用 {profileDocsOut}，也可点击「浏览…」手动选择。");
            }

            if (TryCombineOutputRoot(systemProfile, DefaultExportRootFolderName, out var profileOut))
            {
                return new GuiOutputRootResolution(
                    profileOut,
                    UsedFallback: true,
                    WarningMessage: $"默认输出位置回退：{docsReason}；已改用 {profileOut}，也可点击「浏览…」手动选择。");
            }
        }

        var baseDir = TryInvokePathProvider(
            getBaseDirectory ?? (() => AppContext.BaseDirectory),
            out _);
        if (!string.IsNullOrWhiteSpace(baseDir)
            && TryCheckDirectoryExists(baseDir, existsCheck, out _)
            && TryCombineOutputRoot(baseDir, DefaultExportRootFolderName, out var baseOut))
        {
            return new GuiOutputRootResolution(
                baseOut,
                UsedFallback: true,
                WarningMessage: $"默认输出位置回退：{docsReason}；已改用程序目录 {baseOut}，也可点击「浏览…」手动选择。");
        }

        var tempDir = TryInvokePathProvider(
            getTempDirectory ?? Path.GetTempPath,
            out _);
        if (!string.IsNullOrWhiteSpace(tempDir)
            && TryCombineOutputRoot(tempDir, DefaultExportRootFolderName, out var tempOut))
        {
            return new GuiOutputRootResolution(
                tempOut,
                UsedFallback: true,
                WarningMessage: $"默认输出位置回退：{docsReason}；已改用临时目录 {tempOut}，建议点击「浏览…」手动选择输出目录。");
        }

        return new GuiOutputRootResolution(
            DefaultExportRootFolderName,
            UsedFallback: true,
            WarningMessage: $"默认输出位置回退：{docsReason}；未能确定默认绝对路径，请点击「浏览…」手动选择输出目录。");
    }

    private static string? TryInvokePathProvider(Func<string?> provider, out string? error)
    {
        error = null;
        try
        {
            var value = provider();
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return null;
        }
    }

    private static bool TryCheckDirectoryExists(string path, Func<string, bool> existsCheck, out string? error)
    {
        error = null;
        try
        {
            return existsCheck(path);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
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

    private static string? TryCombine(string parent, string child)
    {
        try
        {
            return Path.Combine(parent, child);
        }
        catch
        {
            return null;
        }
    }

    private static bool TryCombineOutputRoot(string parent, string child, out string combined)
    {
        combined = string.Empty;
        try
        {
            combined = PathSupport.TrimTrailingSeparators(Path.Combine(parent, child));
            return !string.IsNullOrWhiteSpace(combined);
        }
        catch
        {
            return false;
        }
    }

    public static string BuildGameFolderName(string gameId, string? steamBuildId)
    {
        var normalizedId = string.IsNullOrWhiteSpace(gameId)
            ? "game"
            : gameId.Trim().ToLowerInvariant();
        var buildTag = SanitizeBuildIdForFolder(steamBuildId);
        return $"{normalizedId}-{buildTag}";
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
        if (string.IsNullOrWhiteSpace(steamBuildId))
        {
            return "unknown";
        }

        var invalidChars = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(steamBuildId.Length);
        foreach (var ch in steamBuildId.Trim())
        {
            if (char.IsWhiteSpace(ch)
                || ch is '/' or '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|'
                || Array.IndexOf(invalidChars, ch) >= 0)
            {
                sb.Append('_');
            }
            else
            {
                sb.Append(ch);
            }
        }

        var sanitized = sb.ToString().Trim('_');
        return string.IsNullOrEmpty(sanitized) ? "unknown" : sanitized;
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
