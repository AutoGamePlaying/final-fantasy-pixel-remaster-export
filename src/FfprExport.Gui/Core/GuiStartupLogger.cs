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
    string CommandLineArgs,
    string IntegrityLevel = "Unknown",
    string? AgpHome = null,
    string? AgpRootSource = null);

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
/// (never falling back to %TEMP%), retains only the latest 10 log files, and formats complete
/// crash diagnostics for logs, MessageBox, status bar, and stderr.
/// </summary>
internal sealed partial class GuiStartupLogger
{
    public const int DefaultMaxLogFiles = 10;

    [GeneratedRegex(@"^gui-(\d{8}-\d{6})(?:-(\d+))?\.log$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LogFileNameRegex();

    private readonly object _writeLock = new();

    private GuiStartupLogger(
        string? logDirectory,
        string? logFilePath,
        string? initializationWarning)
    {
        LogDirectory = logDirectory;
        LogFilePath = logFilePath;
        InitializationWarning = initializationWarning;
    }

    public string? LogDirectory { get; }

    public string? LogFilePath { get; }

    public string? InitializationWarning { get; }

    public static (string DataDirText, string IntegrityText) FormatStatusBarSummary(
        AgpRootResolution? agpRoot,
        ProcessIntegrityInfo? integrity = null)
    {
        var effectiveIntegrity = integrity ?? agpRoot?.Integrity ?? ProcessIntegrityInfo.Unknown;
        var dataDirText = agpRoot is { Succeeded: true } && !string.IsNullOrWhiteSpace(agpRoot.AgpHome)
            ? $"数据目录：{agpRoot.AgpHome}"
            : "数据目录：（未就绪）";
        var integrityText = $"完整性级别：{effectiveIntegrity.DisplayLabel}";
        return (dataDirText, integrityText);
    }

    public static string? GetPrimaryLogDirectory(
        bool? isWindowsOverride = null,
        Func<string, string?>? getEnvironmentVariable = null,
        Func<Environment.SpecialFolder, string?>? getSpecialFolder = null,
        Func<string?>? getBaseDirectory = null,
        Func<string?>? getLocalLowDirectory = null,
        Func<string, (bool Writable, string? Error)>? probeWritable = null,
        ProcessIntegrityInfo? integrityOverride = null)
    {
        var root = AgpPaths.ResolveAgpHome(
            isWindowsOverride,
            getEnvironmentVariable,
            getSpecialFolder,
            getBaseDirectory,
            getLocalLowDirectory,
            probeWritable,
            integrityOverride: integrityOverride);
        return root.Succeeded ? AgpPaths.GetLogsDirectory(root) : null;
    }

    public static GuiStartupInfo CollectStartupInfo(
        Assembly entryAssembly,
        string dpiSetting,
        IReadOnlyList<string>? args = null,
        DateTimeOffset? timestampOverride = null,
        ProcessIntegrityInfo? integrity = null,
        AgpRootResolution? agpRoot = null)
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
        var effectiveIntegrity = integrity ?? agpRoot?.Integrity ?? ProcessIntegrity.Detect();
        var agpHome = agpRoot is { Succeeded: true } && !string.IsNullOrWhiteSpace(agpRoot.AgpHome)
            ? agpRoot.AgpHome
            : null;
        var agpSource = agpRoot is { Succeeded: true }
            ? agpRoot.Source.ToString()
            : null;

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
            commandLineArgs,
            effectiveIntegrity.LogLabel,
            agpHome,
            agpSource);
    }

    public static string FormatStartupLine(GuiStartupInfo info)
    {
        var stamp = info.Timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture);
        var line = $"[{stamp}] [START] "
            + $"version={SanitizeSingleLine(info.Version)} | "
            + $"git={SanitizeSingleLine(info.GitCommit)} | "
            + $"os={SanitizeSingleLine(info.OsVersion)} | "
            + $"dotnet={SanitizeSingleLine(info.DotNetVersion)} | "
            + $"bitness={SanitizeSingleLine(info.ProcessBitness)} | "
            + $"integrity={SanitizeSingleLine(info.IntegrityLevel)} | "
            + $"cwd={SanitizeSingleLine(info.CurrentDirectory)} | "
            + $"exe={SanitizeSingleLine(info.ExecutablePath)} | "
            + $"dpi={SanitizeSingleLine(info.DpiSetting)} | "
            + $"args={SanitizeSingleLine(info.CommandLineArgs)}";

        if (!string.IsNullOrWhiteSpace(info.AgpHome))
        {
            var sourceSuffix = string.IsNullOrWhiteSpace(info.AgpRootSource)
                ? string.Empty
                : $" ({SanitizeSingleLine(info.AgpRootSource)})";
            line += $" | agp_home={SanitizeSingleLine(info.AgpHome)}{sourceSuffix}";
        }

        return line;
    }

    public static GuiStartupLogger Start(
        GuiStartupInfo startupInfo,
        AgpRootResolution? agpRoot = null,
        string? agpHomeOverride = null,
        int maxLogFiles = DefaultMaxLogFiles,
        bool? isWindowsOverride = null,
        Func<string, string?>? getEnvironmentVariable = null,
        Func<Environment.SpecialFolder, string?>? getSpecialFolder = null,
        Func<string?>? getBaseDirectory = null,
        Func<string?>? getLocalLowDirectory = null,
        Func<string, (bool Writable, string? Error)>? probeWritable = null,
        ProcessIntegrityInfo? integrityOverride = null)
    {
        AgpRootResolution rootResolution;
        if (agpRoot is not null)
        {
            rootResolution = agpRoot;
        }
        else
        {
            Func<string, string?>? effectiveGetEnv = !string.IsNullOrWhiteSpace(agpHomeOverride)
                ? name => string.Equals(name, AgpPaths.AgpHomeEnvVar, StringComparison.Ordinal)
                    ? agpHomeOverride
                    : (getEnvironmentVariable ?? Environment.GetEnvironmentVariable)(name)
                : getEnvironmentVariable;

            rootResolution = AgpPaths.ResolveAgpHome(
                isWindowsOverride,
                effectiveGetEnv,
                getSpecialFolder,
                getBaseDirectory,
                getLocalLowDirectory,
                probeWritable,
                integrityOverride: integrityOverride);
        }

        if (!rootResolution.Succeeded || string.IsNullOrWhiteSpace(rootResolution.AgpHome))
        {
            var initWarning = !string.IsNullOrWhiteSpace(rootResolution.WarningMessage)
                ? $"未找到可写的数据根目录，已跳过写入日志文件：{rootResolution.WarningMessage}"
                : "未找到可写的数据根目录，已跳过写入日志文件";
            return new GuiStartupLogger(
                logDirectory: null,
                logFilePath: null,
                initializationWarning: initWarning);
        }

        var startupLine = FormatStartupLine(startupInfo);
        var logsDirectory = AgpPaths.GetLogsDirectory(rootResolution);
        try
        {
            var ioDir = PathSupport.ToIoPath(logsDirectory);
            Directory.CreateDirectory(ioDir);

            var createdPath = CreateUniqueLogFileAndWriteHeader(
                logsDirectory,
                startupInfo.Timestamp,
                startupLine,
                rootNotice: AgpPaths.FormatUiNoticeBanner(rootResolution, integrityOverride));

            PruneOldLogs(logsDirectory, maxLogFiles, createdPath);
            return new GuiStartupLogger(logsDirectory, createdPath, initializationWarning: null);
        }
        catch (Exception ex)
        {
            return new GuiStartupLogger(
                logDirectory: null,
                logFilePath: null,
                initializationWarning: $"无法写入启动日志文件（{logsDirectory}）：{ex.Message}");
        }
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
                using var writer = TextFileWriter.CreateStreamWriter(stream, leaveOpen: true);
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
        string? rootNotice)
    {
        var baseStamp = timestamp.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var payload = string.IsNullOrWhiteSpace(rootNotice)
            ? startupLine + "\n"
            : $"{startupLine}\n[{timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture)}] [INFO] {SanitizeSingleLine(rootNotice)}\n";

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
                using var writer = TextFileWriter.CreateStreamWriter(stream, leaveOpen: true);
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
        using (var writer = TextFileWriter.CreateStreamWriter(stream, leaveOpen: true))
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
