using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;

namespace FfprExport.Tests;

internal sealed record CliResult(int ExitCode, string Stdout, string Stderr);

/// <summary>
/// Ensures every unit test and child process in this test assembly uses an isolated temporary
/// AGP_HOME directory and never writes to the host machine's real %LOCALAPPDATA% / ~/.local/share.
/// </summary>
internal static class TestAgpIsolation
{
    private static readonly object Gate = new();
    private static string? s_ownedTempAgpHome;

    [ModuleInitializer]
    internal static void Initialize()
    {
        _ = EnsureIsolatedAgpHome();
    }

    public static string EnsureIsolatedAgpHome()
    {
        lock (Gate)
        {
            var existing = Environment.GetEnvironmentVariable(AgpPaths.AgpHomeEnvVar);
            if (!string.IsNullOrWhiteSpace(existing))
            {
                return existing.Trim();
            }

            if (string.IsNullOrWhiteSpace(s_ownedTempAgpHome))
            {
                s_ownedTempAgpHome = Path.Combine(
                    Path.GetTempPath(),
                    $"ffpr-test-agp-{Environment.ProcessId}-{Guid.NewGuid():N}");
                Directory.CreateDirectory(s_ownedTempAgpHome);
                AppDomain.CurrentDomain.ProcessExit += (_, _) => CleanupOwnedDir();
            }

            Environment.SetEnvironmentVariable(AgpPaths.AgpHomeEnvVar, s_ownedTempAgpHome);
            return s_ownedTempAgpHome;
        }
    }

    private static void CleanupOwnedDir()
    {
        var dir = s_ownedTempAgpHome;
        if (string.IsNullOrWhiteSpace(dir))
        {
            return;
        }

        try
        {
            var ioDir = PathSupport.ToIoPath(dir);
            if (Directory.Exists(ioDir))
            {
                Directory.Delete(ioDir, recursive: true);
            }
        }
        catch
        {
            // Ignore transient cleanup errors during test host teardown.
        }
    }
}

internal sealed class TempWorkspace : IDisposable
{
    public string Root { get; }
    public string DepsDir { get; }
    public string AgpHomeDir { get; }

    public TempWorkspace(string prefix)
    {
        Root = Path.Combine(Path.GetTempPath(), $"{prefix}-{Environment.ProcessId}-{Guid.NewGuid():N}");
        DepsDir = Path.Combine(Root, ".deps");
        AgpHomeDir = Path.Combine(Root, "agp-home");
        Directory.CreateDirectory(DepsDir);
    }

    public void Dispose()
    {
        try
        {
            var ioRoot = PathSupport.ToIoPath(Root);
            if (Directory.Exists(ioRoot))
            {
                Directory.Delete(ioRoot, recursive: true);
            }
        }
        catch
        {
            // Ignore transient cleanup failures in temporary directories.
        }
    }
}

internal static class TestHarness
{
    public static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "FfprExport.sln")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("找不到仓库根目录（未找到 FfprExport.sln）");
    }

    internal static string ResolveDotnetExecutable()
    {
        var dotnetRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        if (!string.IsNullOrWhiteSpace(dotnetRoot))
        {
            var candidate = Path.Combine(dotnetRoot, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        var repoDotnet = Path.Combine(FindRepoRoot(), ".deps", "dotnet", OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
        if (File.Exists(repoDotnet))
        {
            return repoDotnet;
        }

        if (!string.IsNullOrWhiteSpace(Environment.ProcessPath)
            && Path.GetFileNameWithoutExtension(Environment.ProcessPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            return Environment.ProcessPath;
        }

        return "dotnet";
    }

    public static async Task<CliResult> RunCliAsync(
        IReadOnlyList<string> args,
        string? ffprDeps = null,
        string? workingDirectory = null,
        string? standardInput = null,
        IReadOnlyDictionary<string, string?>? extraEnv = null,
        string? agpHome = null)
    {
        var dllPath = Path.Combine(AppContext.BaseDirectory, "ffpr-export.dll");
        if (!File.Exists(dllPath))
        {
            throw new FileNotFoundException($"测试构建产物不存在：{dllPath}");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = ResolveDotnetExecutable(),
            WorkingDirectory = workingDirectory ?? FindRepoRoot(),
            RedirectStandardInput = standardInput is not null,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            // The CLI writes UTF-8 when redirected; decode it as UTF-8 too (Windows would default to the OEM code page).
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
            StandardInputEncoding = standardInput is not null ? new UTF8Encoding(false) : null,
        };

        startInfo.ArgumentList.Add("exec");
        startInfo.ArgumentList.Add(dllPath);
        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        if (ffprDeps is null)
        {
            startInfo.Environment.Remove(OutputGuard.DepsEnvVar);
        }
        else
        {
            startInfo.Environment[OutputGuard.DepsEnvVar] = ffprDeps;
        }

        startInfo.Environment[AgpPaths.AgpHomeEnvVar] = string.IsNullOrWhiteSpace(agpHome)
            ? TestAgpIsolation.EnsureIsolatedAgpHome()
            : agpHome.Trim();

        if (extraEnv is not null)
        {
            foreach (var (key, value) in extraEnv)
            {
                if (value is null)
                {
                    startInfo.Environment.Remove(key);
                }
                else
                {
                    startInfo.Environment[key] = value;
                }
            }
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("无法启动 ffpr-export 子进程");

        if (standardInput is not null)
        {
            await process.StandardInput.WriteAsync(standardInput);
            await process.StandardInput.FlushAsync();
            process.StandardInput.Close();
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        return new CliResult(process.ExitCode, await stdoutTask, await stderrTask);
    }
}
