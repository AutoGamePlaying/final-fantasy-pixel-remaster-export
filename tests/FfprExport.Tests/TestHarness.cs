using System.Diagnostics;

namespace FfprExport.Tests;

internal sealed record CliResult(int ExitCode, string Stdout, string Stderr);

internal sealed class TempWorkspace : IDisposable
{
    public string Root { get; }
    public string DepsDir { get; }

    public TempWorkspace(string prefix)
    {
        Root = Path.Combine(Path.GetTempPath(), $"{prefix}-{Environment.ProcessId}-{Guid.NewGuid():N}");
        DepsDir = Path.Combine(Root, ".deps");
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
        IReadOnlyDictionary<string, string?>? extraEnv = null)
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
