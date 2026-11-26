using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace FfprExport.Gui;

/// <summary>
/// Captures host, runtime, version, path, and DPI diagnostics written on GUI startup.
/// </summary>
internal sealed record GuiStartupInfo(
    DateTimeOffset Timestamp,
    string Version,
    string GitCommit,
    string OsVersion,
    string DotNetVersion,
    string ProcessBitness,
    string CurrentDirectory,
    string ExecutablePath,
    string DpiSetting,
    string CommandLineArgs);

/// <summary>
/// Result of selecting a UI or log font along an ordered fallback chain.
/// </summary>
internal sealed record FontSelectionResult<TFont>(
    TFont Font,
    bool OwnedByCaller,
    string ResolvedName,
    bool UsedFallback,
    string? WarningMessage);

/// <summary>
/// Pure, cross-platform font fallback policy so font degradation behavior can be unit-tested
/// on Linux while MainForm supplies the actual GDI+/WinForms font factories on Windows.
/// </summary>
internal static class GuiFontFallbackPolicy
{
    public static readonly string[] UiFontCandidates = ["Microsoft YaHei UI", "Microsoft YaHei"];
    public static readonly string[] LogFontCandidates = ["Consolas", "Courier New"];

    public static FontSelectionResult<TFont> ResolveFont<TFont>(
        string roleLabel,
        IReadOnlyList<string> preferredFamilies,
        Func<string, TFont?> tryCreateFromFamily,
        Func<(TFont Font, bool Owned, string Name)?> tryCreateFromMessageBoxFont,
        Func<(TFont Font, bool Owned, string Name)> getUltimateFallback)
    {
        var primaryName = preferredFamilies.Count > 0 ? preferredFamilies[0] : "默认字体";
        var failures = new List<string>();

        for (var i = 0; i < preferredFamilies.Count; i++)
        {
            var family = preferredFamilies[i];
            if (string.IsNullOrWhiteSpace(family))
            {
                continue;
            }

            try
            {
                var created = tryCreateFromFamily(family);
                if (created is not null)
                {
                    var usedFallback = i > 0;
                    var warning = usedFallback
                        ? $"创建{roleLabel}时首选字体「{primaryName}」不可用，已回退到「{family}」。"
                        : null;
                    return new FontSelectionResult<TFont>(
                        created,
                        OwnedByCaller: true,
                        ResolvedName: family,
                        UsedFallback: usedFallback,
                        WarningMessage: warning);
                }

                failures.Add($"{family}: 字体未安装或不支持所需字重");
            }
            catch (Exception ex)
            {
                failures.Add($"{family}: {ex.Message}");
            }
        }

        try
        {
            var messageBoxFallback = tryCreateFromMessageBoxFont();
            if (messageBoxFallback is { } mb)
            {
                var reason = failures.Count > 0 ? $"（原因：{string.Join("；", failures)}）" : string.Empty;
                return new FontSelectionResult<TFont>(
                    mb.Font,
                    OwnedByCaller: mb.Owned,
                    ResolvedName: mb.Name,
                    UsedFallback: true,
                    WarningMessage: $"创建{roleLabel}失败，已回退到系统字体 SystemFonts.MessageBoxFont（{mb.Name}）{reason}。");
            }

            failures.Add("SystemFonts.MessageBoxFont: 不可用");
        }
        catch (Exception ex)
        {
            failures.Add($"SystemFonts.MessageBoxFont: {ex.Message}");
        }

        var ultimate = getUltimateFallback();
        var ultimateReason = failures.Count > 0 ? $"（原因：{string.Join("；", failures)}）" : string.Empty;
        return new FontSelectionResult<TFont>(
            ultimate.Font,
            OwnedByCaller: ultimate.Owned,
            ResolvedName: ultimate.Name,
            UsedFallback: true,
            WarningMessage: $"创建{roleLabel}失败，已回退到兜底字体「{ultimate.Name}」{ultimateReason}。");
    }
}

/// <summary>
/// Writes per-launch diagnostic logs under &lt;AGP 根目录&gt;\ffpr-export\logs\gui-yyyyMMdd-HHmmss.log
/// (falling back to %TEMP% when the primary log directory cannot be created), retains only the
/// latest 10 log files, and formats complete crash diagnostics for logs, MessageBox, and stderr.
/// </summary>
internal sealed partial class GuiStartupLogger
{
    public const int DefaultMaxLogFiles = 10;

    private static readonly UTF8Encoding Utf8WithoutBom = new(encoderShouldEmitUTF8Identifier: false);

    [GeneratedRegex(@"^gui-(\d{8}-\d{6})(?:-(\d+))?\.log$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LogFileNameRegex();

    private readonly object _writeLock = new();

    private GuiStartupLogger(
        string? logDirectory,
        string? logFilePath,
        bool usedTempFallback,
        string? initializationWarning)
    {
        LogDirectory = logDirectory;
        LogFilePath = logFilePath;
        UsedTempFallback = usedTempFallback;
        InitializationWarning = initializationWarning;
    }

    public string? LogDirectory { get; }

    public string? LogFilePath { get; }

    public bool UsedTempFallback { get; }

    public string? InitializationWarning { get; }

    public static string? GetPrimaryLogDirectory(
        bool? isWindowsOverride = null,
        Func<string, string?>? getEnvironmentVariable = null,
        Func<Environment.SpecialFolder, string?>? getSpecialFolder = null,
        Func<string?>? getBaseDirectory = null,
        Func<string?>? getTempDirectory = null)
    {
        var root = AgpPaths.ResolveAgpHome(
            isWindowsOverride,
            getEnvironmentVariable,
            getSpecialFolder,
            getBaseDirectory,
            getTempDirectory);
        return root.UsedFallback ? null : AgpPaths.GetLogsDirectory(root);
    }

    public static IReadOnlyList<string> GetFallbackLogDirectories(
        string? tempDirectoryOverride = null,
        Func<string?>? systemTempProvider = null)
    {
        string? tempDir = null;
        if (!string.IsNullOrWhiteSpace(tempDirectoryOverride))
        {
            tempDir = tempDirectoryOverride.Trim();
        }
        else
        {
            try
            {
                tempDir = systemTempProvider is not null ? systemTempProvider() : Path.GetTempPath();
            }
            catch
            {
                tempDir = null;
            }

            if (string.IsNullOrWhiteSpace(tempDir))
            {
                try
                {
                    tempDir = Environment.GetEnvironmentVariable("TEMP")
                        ?? Environment.GetEnvironmentVariable("TMP");
                }
                catch
                {
                    tempDir = null;
                }
            }
        }

        if (string.IsNullOrWhiteSpace(tempDir))
        {
            return [];
        }

        var trimmedTemp = PathSupport.TrimTrailingSeparators(tempDir.Trim());
        var results = new List<string>(2);
        try
        {
            var scoped = PathSupport.TrimTrailingSeparators(
                Path.Combine(trimmedTemp, AgpPaths.AgpFolderName, AgpPaths.ToolName, AgpPaths.LogsFolderName));
            if (!string.IsNullOrWhiteSpace(scoped))
            {
                results.Add(scoped);
            }
        }
        catch
        {
            // Ignore malformed subdirectory candidate and fall back to temp root directly.
        }

        if (!string.IsNullOrWhiteSpace(trimmedTemp)
            && !results.Contains(trimmedTemp, GuiGameScanner.PathComparer))
        {
            results.Add(trimmedTemp);
        }

        return results;
    }

    public static GuiStartupInfo CollectStartupInfo(
        Assembly entryAssembly,
        string dpiSetting,
        IReadOnlyList<string>? args = null,
        DateTimeOffset? timestampOverride = null)
    {
        var timestamp = timestampOverride ?? DateTimeOffset.Now;
        var version = ReadVersion(entryAssembly);
        var gitCommit = ReadGitCommit(entryAssembly);
        var osVersion = ReadOsVersion();
        var dotNetVersion = ReadDotNetVersion();
        var processBitness = ReadProcessBitness();
        var currentDirectory = ReadCurrentDirectory();
        var executablePath = ReadExecutablePath();
        var normalizedDpi = string.IsNullOrWhiteSpace(dpiSetting) ? "unknown" : dpiSetting.Trim();
        var commandLineArgs = args is null || args.Count == 0
            ? "(none)"
            : string.Join(" ", args.Select(SanitizeSingleLine));

        return new GuiStartupInfo(
            timestamp,
            version,
            gitCommit,
            osVersion,
            dotNetVersion,
            processBitness,
            currentDirectory,
            executablePath,
            normalizedDpi,
            commandLineArgs);
    }

    public static string FormatStartupLine(GuiStartupInfo info)
    {
        var stamp = info.Timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture);
        return $"[{stamp}] [START] "
            + $"version={SanitizeSingleLine(info.Version)} | "
            + $"git={SanitizeSingleLine(info.GitCommit)} | "
            + $"os={SanitizeSingleLine(info.OsVersion)} | "
            + $"dotnet={SanitizeSingleLine(info.DotNetVersion)} | "
            + $"bitness={SanitizeSingleLine(info.ProcessBitness)} | "
            + $"cwd={SanitizeSingleLine(info.CurrentDirectory)} | "
            + $"exe={SanitizeSingleLine(info.ExecutablePath)} | "
            + $"dpi={SanitizeSingleLine(info.DpiSetting)} | "
            + $"args={SanitizeSingleLine(info.CommandLineArgs)}";
    }

    public static GuiStartupLogger Start(
        GuiStartupInfo startupInfo,
        string? agpHomeOverride = null,
        string? tempDirectoryOverride = null,
        int maxLogFiles = DefaultMaxLogFiles,
        bool? isWindowsOverride = null,
        Func<string, string?>? getEnvironmentVariable = null,
        Func<Environment.SpecialFolder, string?>? getSpecialFolder = null,
        Func<string?>? systemTempProvider = null,
        Func<string?>? getBaseDirectory = null)
    {
        Func<string, string?>? effectiveGetEnv = !string.IsNullOrWhiteSpace(agpHomeOverride)
            ? name => string.Equals(name, AgpPaths.AgpHomeEnvVar, StringComparison.Ordinal)
                ? agpHomeOverride
                : (getEnvironmentVariable ?? Environment.GetEnvironmentVariable)(name)
            : getEnvironmentVariable;

        var rootResolution = AgpPaths.ResolveAgpHome(
            isWindowsOverride,
            effectiveGetEnv,
            getSpecialFolder,
            getBaseDirectory,
            systemTempProvider);

        var candidates = new List<(string Directory, bool IsTempFallback)>();
        var errors = new List<string>();
        if (!rootResolution.UsedFallback)
        {
            var primaryDir = AgpPaths.GetLogsDirectory(rootResolution);
            if (!string.IsNullOrWhiteSpace(primaryDir))
            {
                candidates.Add((primaryDir, IsTempFallback: false));
            }
        }
        else if (!string.IsNullOrWhiteSpace(rootResolution.WarningMessage))
        {
            errors.Add(rootResolution.WarningMessage);
        }

        foreach (var fallbackDir in GetFallbackLogDirectories(tempDirectoryOverride, systemTempProvider))
        {
            if (!candidates.Any(c => GuiGameScanner.PathComparer.Equals(c.Directory, fallbackDir)))
            {
                candidates.Add((fallbackDir, IsTempFallback: true));
            }
        }

        var startupLine = FormatStartupLine(startupInfo);
        if (candidates.Count == 0 && errors.Count == 0)
        {
            errors.Add("无法获取默认 AGP 日志目录");
        }

        foreach (var (directory, isTempFallback) in candidates)
        {
            try
            {
                var ioDir = PathSupport.ToIoPath(directory);
                Directory.CreateDirectory(ioDir);

                var createdPath = CreateUniqueLogFileAndWriteHeader(
                    directory,
                    startupInfo.Timestamp,
                    startupLine,
                    isTempFallback && errors.Count > 0 ? string.Join("；", errors) : null);

                PruneOldLogs(directory, maxLogFiles, createdPath);

                var warning = isTempFallback && errors.Count > 0
                    ? $"主日志目录不可用（{string.Join("；", errors)}），已回退到临时目录：{createdPath}"
                    : null;

                return new GuiStartupLogger(directory, createdPath, isTempFallback, warning);
            }
            catch (Exception ex)
            {
                errors.Add($"{directory}: {ex.Message}");
            }
        }

        var initWarning = errors.Count > 0
            ? $"无法创建启动日志文件：{string.Join("；", errors)}"
            : "无法创建启动日志文件：未找到可用的日志目录";
        return new GuiStartupLogger(
            logDirectory: null,
            logFilePath: null,
            usedTempFallback: false,
            initializationWarning: initWarning);
    }

    public static int PruneOldLogs(
        string logDirectory,
        int maxFiles = DefaultMaxLogFiles,
        string? protectedFilePath = null)
    {
        if (string.IsNullOrWhiteSpace(logDirectory))
        {
            return 0;
        }

        var keepCount = Math.Max(1, maxFiles);
        var ioDir = PathSupport.ToIoPath(logDirectory);
        if (!Directory.Exists(ioDir))
        {
            return 0;
        }

        var normalizedProtected = string.IsNullOrWhiteSpace(protectedFilePath)
            ? null
            : GuiGameScanner.NormalizePathKey(protectedFilePath);

        var matched = new List<(string FullPath, string NormalizedPath, string TimestampKey, int Suffix)>();
        try
        {
            foreach (var path in Directory.EnumerateFiles(ioDir, "gui-*.log", SearchOption.TopDirectoryOnly))
            {
                var fileName = Path.GetFileName(path);
                if (string.IsNullOrEmpty(fileName))
                {
                    continue;
                }

                var match = LogFileNameRegex().Match(fileName);
                if (!match.Success)
                {
                    continue;
                }

                var timestampKey = match.Groups[1].Value;
                var suffix = match.Groups[2].Success
                    && int.TryParse(match.Groups[2].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedSuffix)
                    ? parsedSuffix
                    : 0;
                var fullPath = Path.Combine(logDirectory, fileName);
                matched.Add((fullPath, GuiGameScanner.NormalizePathKey(fullPath), timestampKey, suffix));
            }
        }
        catch
        {
            return 0;
        }

        if (matched.Count <= keepCount)
        {
            return 0;
        }

        matched.Sort((a, b) =>
        {
            var tsCmp = string.Compare(b.TimestampKey, a.TimestampKey, StringComparison.Ordinal);
            if (tsCmp != 0)
            {
                return tsCmp;
            }

            var suffixCmp = b.Suffix.CompareTo(a.Suffix);
            if (suffixCmp != 0)
            {
                return suffixCmp;
            }

            return string.Compare(b.FullPath, a.FullPath, StringComparison.OrdinalIgnoreCase);
        });

        var kept = new HashSet<string>(GuiGameScanner.PathComparer);
        if (normalizedProtected is not null
            && matched.Any(m => GuiGameScanner.PathComparer.Equals(m.NormalizedPath, normalizedProtected)))
        {
            kept.Add(normalizedProtected);
        }

        foreach (var entry in matched)
        {
            if (kept.Count >= keepCount)
            {
                break;
            }

            kept.Add(entry.NormalizedPath);
        }

        var deleted = 0;
        foreach (var entry in matched)
        {
            if (kept.Contains(entry.NormalizedPath))
            {
                continue;
            }

            try
            {
                File.Delete(PathSupport.ToIoPath(entry.FullPath));
                deleted++;
            }
            catch
            {
                // Ignore locked or already-removed log files during cleanup.
            }
        }

        return deleted;
    }

    public void WriteInfo(string message)
    {
        WriteLine("INFO", message);
    }

    public void WriteWarning(string message)
    {
        WriteLine("WARN", message);
    }

    public void WriteException(string context, Exception? exception)
    {
        WriteBlock("ERROR", FormatExceptionDetails(exception, context));
    }

    public void WriteUnhandledObject(string context, object? exceptionObject, bool isTerminating)
    {
        var fullContext = isTerminating ? $"{context} (IsTerminating=true)" : $"{context} (IsTerminating=false)";
        if (exceptionObject is Exception ex)
        {
            WriteException(fullContext, ex);
            return;
        }

        var typeName = exceptionObject?.GetType().FullName ?? "null";
        var description = exceptionObject?.ToString() ?? "(null)";
        WriteBlock(
            "ERROR",
            $"上下文: {fullContext}\n非 Exception 未处理对象 ({typeName}): {description}");
    }

    public static string FormatExceptionDetails(Exception? exception, string context)
    {
        var sb = new StringBuilder();
        var normalizedContext = string.IsNullOrWhiteSpace(context) ? "Unknown" : context.Trim();
        sb.AppendLine($"上下文: {normalizedContext}");

        if (exception is null)
        {
            sb.Append("异常对象为空（null）。");
            return sb.ToString();
        }

        var visited = new HashSet<Exception>(ReferenceEqualityComparer.Instance);
        AppendExceptionChain(sb, exception, label: "Exception", depth: 0, visited);
        return sb.ToString().TrimEnd();
    }

    public static string FormatFatalDialogMessage(
        Exception? exception,
        string? logFilePath,
        string? logInitWarning = null,
        bool isStartupFailure = true)
    {
        var sb = new StringBuilder();
        sb.AppendLine(isStartupFailure ? "窗口程序启动失败。" : "窗口程序运行过程中发生未处理的错误。");
        sb.AppendLine();

        if (exception is not null)
        {
            sb.AppendLine($"错误类型：{exception.GetType().FullName}");
            sb.AppendLine($"错误信息：{exception.Message}");

            var innermost = GetInnermostException(exception);
            if (!ReferenceEquals(innermost, exception)
                && !string.IsNullOrWhiteSpace(innermost.Message)
                && !string.Equals(innermost.Message, exception.Message, StringComparison.Ordinal))
            {
                sb.AppendLine($"根本原因：{innermost.GetType().Name}: {innermost.Message}");
            }
        }
        else
        {
            sb.AppendLine("错误信息：发生未知的未处理错误。");
        }

        sb.AppendLine();
        if (!string.IsNullOrWhiteSpace(logFilePath))
        {
            sb.AppendLine("完整错误日志已写入：");
            sb.AppendLine(logFilePath);
            sb.AppendLine();
            sb.Append("请将该日志文件发送给开发者，以便定位问题原因。");
        }
        else
        {
            sb.AppendLine($"未能写入日志文件（{logInitWarning ?? "日志目录不可写"}）。");
            sb.Append("请将本提示窗口截图发送给开发者，以便定位问题原因。");
        }

        return sb.ToString();
    }

    public static string FormatSelfTestFailureMessage(
        Exception exception,
        string? logFilePath,
        string? logInitWarning = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine("错误：GUI 自检失败。");
        if (!string.IsNullOrWhiteSpace(logFilePath))
        {
            sb.AppendLine($"日志文件：{logFilePath}");
        }
        else
        {
            sb.AppendLine($"日志文件：不可用（{logInitWarning ?? "未能创建日志"}）");
        }

        sb.AppendLine(FormatExceptionDetails(exception, "RunSelfTest"));
        return sb.ToString().TrimEnd();
    }

    private void WriteLine(string level, string message)
    {
        if (string.IsNullOrWhiteSpace(LogFilePath))
        {
            return;
        }

        var stamp = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture);
        var line = $"[{stamp}] [{level}] {SanitizeSingleLine(message)}\n";
        TryAppendRaw(line);
    }

    private void WriteBlock(string level, string content)
    {
        if (string.IsNullOrWhiteSpace(LogFilePath))
        {
            return;
        }

        var stamp = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture);
        var normalized = content.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var payload = $"[{stamp}] [{level}]\n{normalized}\n";
        TryAppendRaw(payload);
    }

    private void TryAppendRaw(string text)
    {
        if (string.IsNullOrWhiteSpace(LogFilePath))
        {
            return;
        }

        lock (_writeLock)
        {
            try
            {
                using var stream = new FileStream(
                    PathSupport.ToIoPath(LogFilePath),
                    FileMode.Append,
                    FileAccess.Write,
                    FileShare.ReadWrite);
                using var writer = new StreamWriter(stream, Utf8WithoutBom);
                writer.Write(text);
                writer.Flush();
            }
            catch
            {
                // Diagnostic logging must never throw back into the application.
            }
        }
    }

    private static string CreateUniqueLogFileAndWriteHeader(
        string logDirectory,
        DateTimeOffset timestamp,
        string startupLine,
        string? fallbackReason)
    {
        var baseStamp = timestamp.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var payload = string.IsNullOrWhiteSpace(fallbackReason)
            ? startupLine + "\n"
            : $"{startupLine}\n[{timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture)}] [WARN] 主日志目录不可用，已回退到当前目录：{SanitizeSingleLine(fallbackReason)}\n";

        for (var attempt = 0; attempt < 100; attempt++)
        {
            var fileName = attempt == 0
                ? $"gui-{baseStamp}.log"
                : string.Create(CultureInfo.InvariantCulture, $"gui-{baseStamp}-{attempt}.log");
            var candidatePath = Path.Combine(logDirectory, fileName);
            var ioPath = PathSupport.ToIoPath(candidatePath);

            try
            {
                using var stream = new FileStream(ioPath, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite);
                using var writer = new StreamWriter(stream, Utf8WithoutBom);
                writer.Write(payload);
                writer.Flush();
                return candidatePath;
            }
            catch (IOException) when (File.Exists(ioPath) && attempt < 99)
            {
                // Same-second startup collision; retry with numeric suffix.
            }
        }

        var fallbackPath = Path.Combine(logDirectory, $"gui-{baseStamp}-{Environment.ProcessId}.log");
        using (var stream = new FileStream(PathSupport.ToIoPath(fallbackPath), FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
        using (var writer = new StreamWriter(stream, Utf8WithoutBom))
        {
            writer.Write(payload);
            writer.Flush();
        }

        return fallbackPath;
    }

    private static void AppendExceptionChain(
        StringBuilder sb,
        Exception ex,
        string label,
        int depth,
        HashSet<Exception> visited)
    {
        if (depth > 16 || !visited.Add(ex))
        {
            sb.AppendLine($"--- {label} (已省略重复或过深嵌套) ---");
            return;
        }

        sb.AppendLine($"--- {label} ---");
        sb.AppendLine($"Type: {ex.GetType().FullName}");
        sb.AppendLine($"Message: {ex.Message}");
        sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"HResult: 0x{ex.HResult:X8}"));
        if (!string.IsNullOrWhiteSpace(ex.Source))
        {
            sb.AppendLine($"Source: {ex.Source}");
        }

        if (ex.TargetSite is not null)
        {
            sb.AppendLine($"TargetSite: {ex.TargetSite}");
        }

        sb.AppendLine("StackTrace:");
        sb.AppendLine(string.IsNullOrWhiteSpace(ex.StackTrace) ? "  (无堆栈信息)" : ex.StackTrace);

        if (ex is AggregateException agg && agg.InnerExceptions.Count > 0)
        {
            for (var i = 0; i < agg.InnerExceptions.Count; i++)
            {
                AppendExceptionChain(
                    sb,
                    agg.InnerExceptions[i],
                    string.Create(CultureInfo.InvariantCulture, $"InnerException [{depth + 1}.{i + 1}]"),
                    depth + 1,
                    visited);
            }
        }
        else if (ex.InnerException is not null)
        {
            AppendExceptionChain(
                sb,
                ex.InnerException,
                string.Create(CultureInfo.InvariantCulture, $"InnerException [{depth + 1}]"),
                depth + 1,
                visited);
        }
    }

    private static Exception GetInnermostException(Exception ex)
    {
        var current = ex;
        var steps = 0;
        while (current.InnerException is not null && steps < 16)
        {
            current = current.InnerException;
            steps++;
        }

        return current;
    }

    private static string ReadVersion(Assembly assembly)
    {
        try
        {
            var info = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (!string.IsNullOrWhiteSpace(info))
            {
                return info.Split('+')[0].Trim();
            }

            return assembly.GetName().Version?.ToString(3) ?? "unknown";
        }
        catch
        {
            return "unknown";
        }
    }

    private static string ReadGitCommit(Assembly assembly)
    {
        try
        {
            foreach (var attr in assembly.GetCustomAttributes<AssemblyMetadataAttribute>())
            {
                if (string.Equals(attr.Key, "GitCommit", StringComparison.Ordinal)
                    && !string.IsNullOrWhiteSpace(attr.Value))
                {
                    return attr.Value.Trim();
                }
            }

            var info = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (!string.IsNullOrWhiteSpace(info))
            {
                var plusIdx = info.IndexOf('+');
                if (plusIdx >= 0 && plusIdx + 1 < info.Length)
                {
                    return info[(plusIdx + 1)..].Trim();
                }
            }
        }
        catch
        {
            // Fall through to unknown.
        }

        return "unknown";
    }

    private static string ReadOsVersion()
    {
        try
        {
            return $"{RuntimeInformation.OSDescription.Trim()} ({Environment.OSVersion.VersionString})";
        }
        catch
        {
            return "unknown";
        }
    }

    private static string ReadDotNetVersion()
    {
        try
        {
            return $"{RuntimeInformation.FrameworkDescription.Trim()} (CLR {Environment.Version})";
        }
        catch
        {
            return "unknown";
        }
    }

    private static string ReadProcessBitness()
    {
        try
        {
            var bits = Environment.Is64BitProcess ? "64-bit" : "32-bit";
            return $"{bits} ({RuntimeInformation.ProcessArchitecture}, OS={RuntimeInformation.OSArchitecture})";
        }
        catch
        {
            return Environment.Is64BitProcess ? "64-bit" : "32-bit";
        }
    }

    private static string ReadCurrentDirectory()
    {
        try
        {
            return Environment.CurrentDirectory;
        }
        catch (Exception ex)
        {
            return $"<读取失败: {ex.Message}>";
        }
    }

    private static string ReadExecutablePath()
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(Environment.ProcessPath))
            {
                return Environment.ProcessPath;
            }

            if (!string.IsNullOrWhiteSpace(AppContext.BaseDirectory))
            {
                return AppContext.BaseDirectory;
            }
        }
        catch
        {
            // Fall through to unknown.
        }

        return "unknown";
    }

    private static string SanitizeSingleLine(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return value
            .Replace("\r\n", " ", StringComparison.Ordinal)
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Trim();
    }
}
