using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace FfprExport.Gui;

/// <summary>
/// Locates the sibling CLI executable, constructs build-db arguments, parses step progress lines,
/// and configures UTF-8 ProcessStartInfo without invoking a shell.
/// </summary>
internal static partial class CliCommandHelper
{
    public const string CliExecutableFileName = "ffpr-export.exe";

    [GeneratedRegex(@"^\s*==>\s*(?:\[[^\]]+\]\s*)?步骤\s*(\d+)\s*/\s*(\d+)(?:\s*[：:]\s*(.*))?$", RegexOptions.CultureInvariant)]
    private static partial Regex ProgressStepRegex();

    public static (string? ResolvedPath, string ExpectedPath) FindCliExecutable(
        string baseDirectory,
        string? processPath = null,
        string cliFileName = CliExecutableFileName)
    {
        var normalizedBase = string.IsNullOrWhiteSpace(baseDirectory)
            ? AppContext.BaseDirectory
            : baseDirectory;
        var expectedPath = Path.Combine(normalizedBase, cliFileName);
        if (File.Exists(PathSupport.ToIoPath(expectedPath)))
        {
            return (Path.GetFullPath(expectedPath), Path.GetFullPath(expectedPath));
        }

        if (!string.IsNullOrWhiteSpace(processPath))
        {
            var processDir = Path.GetDirectoryName(processPath);
            if (!string.IsNullOrWhiteSpace(processDir))
            {
                var processCandidate = Path.Combine(processDir, cliFileName);
                if (File.Exists(PathSupport.ToIoPath(processCandidate)))
                {
                    return (Path.GetFullPath(processCandidate), Path.GetFullPath(expectedPath));
                }
            }
        }

        return (null, Path.GetFullPath(expectedPath));
    }

    public static string FormatMissingCliMessage(string expectedPath)
    {
        var fileName = Path.GetFileName(expectedPath);
        return $"找不到命令行导出程序：{fileName}。\n\n"
            + $"预期位置：{expectedPath}\n\n"
            + $"请确认压缩包已完整解压，并且 {fileName} 与 ffpr-export-gui.exe 位于同一文件夹。";
    }

    public static string[] BuildBuildDbArguments(
        string gameDirectory,
        string outputDirectory,
        bool force = false,
        string? steamBuildId = null,
        bool keepJson = false,
        int? jobs = null)
    {
        if (string.IsNullOrWhiteSpace(gameDirectory))
        {
            throw new ArgumentException("游戏目录不能为空", nameof(gameDirectory));
        }

        if (string.IsNullOrWhiteSpace(outputDirectory))
        {
            throw new ArgumentException("输出目录不能为空", nameof(outputDirectory));
        }

        var args = new List<string>
        {
            "build-db",
            "--game",
            gameDirectory,
            "--out",
            outputDirectory,
        };

        if (!string.IsNullOrWhiteSpace(steamBuildId))
        {
            args.Add("--steam-buildid");
            args.Add(steamBuildId.Trim());
        }

        if (keepJson)
        {
            args.Add("--keep-json");
        }

        if (jobs is > 0)
        {
            args.Add("--jobs");
            args.Add(jobs.Value.ToString(CultureInfo.InvariantCulture));
        }

        if (force)
        {
            args.Add("--force");
        }

        return [.. args];
    }

    public static ProcessStartInfo CreateStartInfo(
        string cliExecutablePath,
        IReadOnlyList<string> arguments,
        string? workingDirectory = null)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = cliExecutablePath,
            WorkingDirectory = string.IsNullOrWhiteSpace(workingDirectory)
                ? Path.GetDirectoryName(cliExecutablePath) ?? AppContext.BaseDirectory
                : workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        foreach (var arg in arguments)
        {
            startInfo.ArgumentList.Add(arg);
        }

        return startInfo;
    }

    public static bool TryParseProgressStep(string? line, out ProgressStepUpdate update)
    {
        update = default!;
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        var match = ProgressStepRegex().Match(line);
        if (!match.Success)
        {
            return false;
        }

        if (!int.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var current)
            || !int.TryParse(match.Groups[2].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var total)
            || total <= 0
            || current < 1
            || current > total)
        {
            return false;
        }

        var description = match.Groups[3].Success ? match.Groups[3].Value.Trim() : null;
        if (string.IsNullOrWhiteSpace(description))
        {
            description = null;
        }

        update = new ProgressStepUpdate(current, total, description);
        return true;
    }

    public static string FormatGameProgressHeader(
        int currentIndexOneBased,
        int totalCount,
        GuiGameEntry game,
        ProgressStepUpdate? step = null)
    {
        var prefix = string.Create(
            CultureInfo.InvariantCulture,
            $"第 {currentIndexOneBased} / {totalCount} 个：{game.GameCode}");
        if (step is null)
        {
            return prefix;
        }

        var stepPart = string.IsNullOrWhiteSpace(step.Description)
            ? string.Create(CultureInfo.InvariantCulture, $"步骤 {step.CurrentStep}/{step.TotalSteps}")
            : string.Create(CultureInfo.InvariantCulture, $"步骤 {step.CurrentStep}/{step.TotalSteps}：{step.Description}");
        return $"{prefix}（{stepPart}）";
    }

    /// <summary>
    /// Computes overall batch progress in [0, 1000] across sequential games and per-game steps.
    /// Within an active game, step s of m maps to s / (m + 1) so progress advances immediately
    /// on step 1 and reaches the game boundary only when the subprocess actually finishes.
    /// </summary>
    public static int CalculateProgressPermille(
        int completedGames,
        int totalGames,
        ProgressStepUpdate? currentStep = null)
    {
        if (totalGames <= 0)
        {
            return 0;
        }

        var clampedCompleted = Math.Clamp(completedGames, 0, totalGames);
        if (clampedCompleted >= totalGames)
        {
            return 1000;
        }

        double currentFraction = 0.0;
        if (currentStep is not null && currentStep.TotalSteps > 0)
        {
            var clampedStep = Math.Clamp(currentStep.CurrentStep, 0, currentStep.TotalSteps);
            currentFraction = clampedStep / (currentStep.TotalSteps + 1.0);
        }

        var ratio = (clampedCompleted + currentFraction) / totalGames;
        return Math.Clamp((int)Math.Round(ratio * 1000.0, MidpointRounding.AwayFromZero), 0, 1000);
    }
}
