using System.Diagnostics;

namespace FfprExport.Gui;

/// <summary>
/// Executes a child CLI process asynchronously with UTF-8 stdout/stderr line streaming
/// and entire-process-tree termination upon cancellation.
/// </summary>
internal static class CliProcessRunner
{
    public static async Task<int> RunAsync(
        ProcessStartInfo startInfo,
        Action<string> onStdoutLine,
        Action<string> onStderrLine,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        using var process = new Process
        {
            StartInfo = startInfo,
            EnableRaisingEvents = true,
        };

        if (!process.Start())
        {
            throw new InvalidOperationException(L.Format("Gui_ProcessStartFailed", startInfo.FileName));
        }

        using var killRegistration = cancellationToken.Register(() => TryKillProcessTree(process));

        var stdoutTask = PumpLinesAsync(process.StandardOutput, onStdoutLine);
        var stderrTask = PumpLinesAsync(process.StandardError, onStderrLine);

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKillProcessTree(process);
            await WaitBrieflyAfterKillAsync(process).ConfigureAwait(false);
            await DrainQuietlyAsync(stdoutTask, stderrTask).ConfigureAwait(false);
            throw;
        }

        await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
        return process.ExitCode;
    }

    private static async Task PumpLinesAsync(StreamReader reader, Action<string> onLine)
    {
        try
        {
            while (true)
            {
                var line = await reader.ReadLineAsync().ConfigureAwait(false);
                if (line is null)
                {
                    break;
                }

                onLine(line);
            }
        }
        catch (ObjectDisposedException)
        {
            // Stream closed during cancellation.
        }
        catch (IOException)
        {
            // Pipe broken after process termination.
        }
    }

    private static void TryKillProcessTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Process may have already exited between check and kill.
        }
    }

    private static async Task WaitBrieflyAfterKillAsync(Process process)
    {
        try
        {
            using var waitCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await process.WaitForExitAsync(waitCts.Token).ConfigureAwait(false);
        }
        catch
        {
            // Best-effort wait after kill.
        }
    }

    private static async Task DrainQuietlyAsync(Task stdoutTask, Task stderrTask)
    {
        try
        {
            await Task.WhenAll(stdoutTask, stderrTask).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch
        {
            // Best-effort drain after kill.
        }
    }
}
