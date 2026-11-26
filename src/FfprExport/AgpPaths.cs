using System.Text;

namespace FfprExport;

internal enum AgpRootSource
{
    AgpHomeEnv,
    WindowsLocalAppData,
    XdgDataHome,
    UnixDefaultLocalShare,
    FallbackBaseDirectory,
    FallbackTempDirectory,
    FallbackCurrentDirectory,
}

internal sealed record AgpRootResolution(
    string AgpHome,
    AgpRootSource Source,
    bool UsedFallback,
    string? WarningMessage,
    bool IsWindowsLayout);

/// <summary>
/// Single source of truth shared by CLI and GUI for resolving the unified AGP data root
/// (%LOCALAPPDATA%\agp on Windows, $XDG_DATA_HOME/agp or ~/.local/share/agp on Linux/macOS,
/// overridden by AGP_HOME) and deriving ffpr-export output, logs, and settings paths.
/// </summary>
internal static class AgpPaths
{
    public const string AgpHomeEnvVar = "AGP_HOME";
    public const string XdgDataHomeEnvVar = "XDG_DATA_HOME";
    public const string LocalAppDataEnvVar = "LOCALAPPDATA";
    public const string HomeEnvVar = "HOME";
    public const string AgpFolderName = "agp";
    public const string ToolName = "ffpr-export";
    public const string OutputFolderName = "output";
    public const string LogsFolderName = "logs";
    public const string SettingsFileName = "settings.json";

    /// <summary>
    /// Computes the AGP root directory (the "agp" layer itself) using the unified priority order:
    /// 1) AGP_HOME environment variable (used directly as the agp layer)
    /// 2) Windows: %LOCALAPPDATA%\agp
    /// 3) Linux/macOS: $XDG_DATA_HOME/agp, or ~/.local/share/agp when XDG_DATA_HOME is unset.
    /// Falls back safely to application base directory, temp directory, or relative "agp" if OS
    /// environment/profile queries fail, never throwing.
    /// </summary>
    public static AgpRootResolution ResolveAgpHome(
        bool? isWindowsOverride = null,
        Func<string, string?>? getEnvironmentVariable = null,
        Func<Environment.SpecialFolder, string?>? getSpecialFolder = null,
        Func<string?>? getBaseDirectory = null,
        Func<string?>? getTempDirectory = null)
    {
        var isWindows = isWindowsOverride ?? OperatingSystem.IsWindows();
        var getEnv = getEnvironmentVariable ?? Environment.GetEnvironmentVariable;
        var getFolder = getSpecialFolder ?? (folder => Environment.GetFolderPath(folder));
        var failureReasons = new List<string>();

        // 1. AGP_HOME directly serves as the "agp" layer on all platforms.
        var envAgpHome = TryInvokeProvider(() => getEnv(AgpHomeEnvVar), out var agpEnvError);
        if (!string.IsNullOrWhiteSpace(envAgpHome))
        {
            if (TryNormalizePath(envAgpHome, isWindows, out var normalizedAgpHome, out var normalizeError))
            {
                return new AgpRootResolution(
                    normalizedAgpHome,
                    AgpRootSource.AgpHomeEnv,
                    UsedFallback: false,
                    WarningMessage: null,
                    IsWindowsLayout: isWindows);
            }

            failureReasons.Add($"环境变量 {AgpHomeEnvVar} 路径无效（{envAgpHome}）：{normalizeError}");
        }
        else if (!string.IsNullOrWhiteSpace(agpEnvError))
        {
            failureReasons.Add($"读取环境变量 {AgpHomeEnvVar} 失败：{agpEnvError}");
        }

        // 2. Platform-specific default root.
        if (isWindows)
        {
            var localAppData = TryInvokeProvider(
                () => getFolder(Environment.SpecialFolder.LocalApplicationData),
                out var specialFolderError);

            if (string.IsNullOrWhiteSpace(localAppData))
            {
                localAppData = TryInvokeProvider(() => getEnv(LocalAppDataEnvVar), out var envLocalError);
                if (string.IsNullOrWhiteSpace(localAppData))
                {
                    var detail = !string.IsNullOrWhiteSpace(specialFolderError)
                        ? specialFolderError
                        : !string.IsNullOrWhiteSpace(envLocalError)
                            ? envLocalError
                            : "系统未返回 LocalApplicationData 且未设置 LOCALAPPDATA";
                    failureReasons.Add($"无法确定 Windows LOCALAPPDATA 目录（{detail}）");
                }
            }

            if (!string.IsNullOrWhiteSpace(localAppData))
            {
                if (TryCombine(isWindows, localAppData, [AgpFolderName], out var winAgpHome, out var combineError))
                {
                    return new AgpRootResolution(
                        winAgpHome,
                        AgpRootSource.WindowsLocalAppData,
                        UsedFallback: false,
                        WarningMessage: null,
                        IsWindowsLayout: isWindows);
                }

                failureReasons.Add($"拼接 %LOCALAPPDATA%\\{AgpFolderName} 失败（{localAppData}）：{combineError}");
            }
        }
        else
        {
            var xdgDataHome = TryInvokeProvider(() => getEnv(XdgDataHomeEnvVar), out var xdgError);
            if (!string.IsNullOrWhiteSpace(xdgDataHome))
            {
                if (TryCombine(isWindows, xdgDataHome, [AgpFolderName], out var xdgAgpHome, out var combineError))
                {
                    return new AgpRootResolution(
                        xdgAgpHome,
                        AgpRootSource.XdgDataHome,
                        UsedFallback: false,
                        WarningMessage: null,
                        IsWindowsLayout: isWindows);
                }

                failureReasons.Add($"拼接 ${XdgDataHomeEnvVar}/{AgpFolderName} 失败（{xdgDataHome}）：{combineError}");
            }
            else
            {
                if (!string.IsNullOrWhiteSpace(xdgError))
                {
                    failureReasons.Add($"读取环境变量 {XdgDataHomeEnvVar} 失败：{xdgError}");
                }

                var homeDir = TryInvokeProvider(() => getEnv(HomeEnvVar), out var homeEnvError);
                if (string.IsNullOrWhiteSpace(homeDir))
                {
                    homeDir = TryInvokeProvider(
                        () => getFolder(Environment.SpecialFolder.UserProfile),
                        out var profileError);
                    if (string.IsNullOrWhiteSpace(homeDir))
                    {
                        var detail = !string.IsNullOrWhiteSpace(profileError)
                            ? profileError
                            : !string.IsNullOrWhiteSpace(homeEnvError)
                                ? homeEnvError
                                : "未设置 HOME 且系统未返回用户主目录";
                        failureReasons.Add($"无法确定用户主目录（{detail}）");
                    }
                }

                if (!string.IsNullOrWhiteSpace(homeDir))
                {
                    if (TryCombine(isWindows, homeDir, [".local", "share", AgpFolderName], out var unixAgpHome, out var combineError))
                    {
                        return new AgpRootResolution(
                            unixAgpHome,
                            AgpRootSource.UnixDefaultLocalShare,
                            UsedFallback: false,
                            WarningMessage: null,
                            IsWindowsLayout: isWindows);
                    }

                    failureReasons.Add($"拼接 ~/.local/share/{AgpFolderName} 失败（{homeDir}）：{combineError}");
                }
            }
        }

        // 3. Safe fallbacks when primary user data locations are unavailable.
        var primaryReason = failureReasons.Count > 0
            ? string.Join("；", failureReasons)
            : "无法确定默认 AGP 数据根目录";

        var baseDir = TryInvokeProvider(getBaseDirectory ?? (() => AppContext.BaseDirectory), out _);
        if (!string.IsNullOrWhiteSpace(baseDir)
            && TryCombine(isWindows, baseDir, [AgpFolderName], out var baseAgpHome, out _))
        {
            return new AgpRootResolution(
                baseAgpHome,
                AgpRootSource.FallbackBaseDirectory,
                UsedFallback: true,
                WarningMessage: $"默认 AGP 根目录回退：{primaryReason}；已改用程序目录 {baseAgpHome}（也可通过环境变量 {AgpHomeEnvVar} 指定）。",
                IsWindowsLayout: isWindows);
        }

        var tempDir = TryInvokeProvider(getTempDirectory ?? Path.GetTempPath, out _);
        if (!string.IsNullOrWhiteSpace(tempDir)
            && TryCombine(isWindows, tempDir, [AgpFolderName], out var tempAgpHome, out _))
        {
            return new AgpRootResolution(
                tempAgpHome,
                AgpRootSource.FallbackTempDirectory,
                UsedFallback: true,
                WarningMessage: $"默认 AGP 根目录回退：{primaryReason}；已改用临时目录 {tempAgpHome}（建议通过环境变量 {AgpHomeEnvVar} 或手动选择目录）。",
                IsWindowsLayout: isWindows);
        }

        return new AgpRootResolution(
            AgpFolderName,
            AgpRootSource.FallbackCurrentDirectory,
            UsedFallback: true,
            WarningMessage: $"默认 AGP 根目录回退：{primaryReason}；未能确定默认绝对路径，已改用相对目录 {AgpFolderName}。",
            IsWindowsLayout: isWindows);
    }

    public static string GetToolRoot(AgpRootResolution? resolution = null)
    {
        var resolved = resolution ?? ResolveAgpHome();
        return CombineForLayout(resolved.IsWindowsLayout, resolved.AgpHome, ToolName);
    }

    public static string GetToolRoot(
        bool? isWindowsOverride,
        Func<string, string?>? getEnvironmentVariable = null,
        Func<Environment.SpecialFolder, string?>? getSpecialFolder = null,
        Func<string?>? getBaseDirectory = null,
        Func<string?>? getTempDirectory = null)
    {
        return GetToolRoot(ResolveAgpHome(
            isWindowsOverride,
            getEnvironmentVariable,
            getSpecialFolder,
            getBaseDirectory,
            getTempDirectory));
    }

    public static string GetOutputRoot(AgpRootResolution? resolution = null)
    {
        var resolved = resolution ?? ResolveAgpHome();
        return CombineForLayout(resolved.IsWindowsLayout, resolved.AgpHome, ToolName, OutputFolderName);
    }

    public static string GetOutputRoot(
        bool? isWindowsOverride,
        Func<string, string?>? getEnvironmentVariable = null,
        Func<Environment.SpecialFolder, string?>? getSpecialFolder = null,
        Func<string?>? getBaseDirectory = null,
        Func<string?>? getTempDirectory = null)
    {
        return GetOutputRoot(ResolveAgpHome(
            isWindowsOverride,
            getEnvironmentVariable,
            getSpecialFolder,
            getBaseDirectory,
            getTempDirectory));
    }

    public static string GetGameOutputDirectory(
        string gameId,
        string? steamBuildId,
        AgpRootResolution? resolution = null)
    {
        var resolved = resolution ?? ResolveAgpHome();
        return CombineForLayout(
            resolved.IsWindowsLayout,
            resolved.AgpHome,
            ToolName,
            OutputFolderName,
            BuildGameFolderName(gameId, steamBuildId));
    }

    public static string GetLogsDirectory(AgpRootResolution? resolution = null)
    {
        var resolved = resolution ?? ResolveAgpHome();
        return CombineForLayout(resolved.IsWindowsLayout, resolved.AgpHome, ToolName, LogsFolderName);
    }

    public static string GetSettingsPath(AgpRootResolution? resolution = null)
    {
        var resolved = resolution ?? ResolveAgpHome();
        return CombineForLayout(resolved.IsWindowsLayout, resolved.AgpHome, ToolName, SettingsFileName);
    }

    public static string BuildGameFolderName(string gameId, string? steamBuildId)
    {
        var normalizedId = string.IsNullOrWhiteSpace(gameId)
            ? "game"
            : gameId.Trim().ToLowerInvariant();
        var buildTag = SanitizeBuildIdForFolder(steamBuildId);
        return $"{normalizedId}-{buildTag}";
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

    private static string? TryInvokeProvider(Func<string?> provider, out string? error)
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

    private static bool TryNormalizePath(
        string rawPath,
        bool isWindows,
        out string normalized,
        out string? error)
    {
        normalized = string.Empty;
        error = null;
        try
        {
            var trimmed = rawPath.Trim();
            if (string.IsNullOrEmpty(trimmed) || trimmed.IndexOf('\0') >= 0)
            {
                error = "路径包含空字符或为空";
                return false;
            }

            if (isWindows == OperatingSystem.IsWindows())
            {
                normalized = PathSupport.TrimTrailingSeparators(Path.GetFullPath(trimmed));
            }
            else
            {
                normalized = TrimTrailingSeparatorsForLayout(trimmed, isWindows);
            }

            return !string.IsNullOrWhiteSpace(normalized);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static bool TryCombine(
        bool isWindows,
        string basePath,
        string[] segments,
        out string combined,
        out string? error)
    {
        combined = string.Empty;
        error = null;
        try
        {
            if (string.IsNullOrWhiteSpace(basePath) || basePath.IndexOf('\0') >= 0)
            {
                error = "基准路径为空或包含空字符";
                return false;
            }

            combined = CombineForLayout(isWindows, basePath, segments);
            return !string.IsNullOrWhiteSpace(combined);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static string CombineForLayout(bool isWindows, string basePath, params string[] segments)
    {
        if (isWindows == OperatingSystem.IsWindows())
        {
            var current = basePath.Trim();
            foreach (var segment in segments)
            {
                current = Path.Combine(current, segment);
            }

            return PathSupport.TrimTrailingSeparators(Path.GetFullPath(current));
        }

        var separator = isWindows ? '\\' : '/';
        var sb = new StringBuilder(TrimTrailingSeparatorsForLayout(basePath.Trim(), isWindows));
        foreach (var segment in segments)
        {
            if (string.IsNullOrWhiteSpace(segment))
            {
                continue;
            }

            if (sb.Length > 0 && sb[^1] != '/' && sb[^1] != '\\')
            {
                sb.Append(separator);
            }

            sb.Append(segment.Trim('/', '\\'));
        }

        return TrimTrailingSeparatorsForLayout(sb.ToString(), isWindows);
    }

    private static string TrimTrailingSeparatorsForLayout(string path, bool isWindows)
    {
        if (string.IsNullOrEmpty(path))
        {
            return path;
        }

        if (isWindows)
        {
            var trimmed = path.TrimEnd('\\', '/');
            if (trimmed.Length == 2 && char.IsAsciiLetter(trimmed[0]) && trimmed[1] == ':')
            {
                return trimmed + "\\";
            }

            return string.IsNullOrEmpty(trimmed) ? "\\" : trimmed;
        }

        var unixTrimmed = path.TrimEnd('/', '\\');
        return string.IsNullOrEmpty(unixTrimmed) ? "/" : unixTrimmed;
    }
}
