using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace FfprExport.Gui;

internal sealed record CliLookupResult(
    string? ResolvedPath,
    string ExpectedPath,
    string? WarningMessage,
    Exception? Error);

/// <summary>
/// Locates the sibling CLI executable, constructs build-db arguments, parses language-independent
/// @@progress JSON lines, and configures UTF-8 ProcessStartInfo without invoking a shell.
/// </summary>
internal static class CliCommandHelper
{
    public const string CliExecutableFileName = "ffpr-export.exe";
    public const string ProgressProtocolPrefix = "@@progress";

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

    public static CliLookupResult FindCliExecutableSafe(
        string? baseDirectory,
        string? processPath = null,
        string cliFileName = CliExecutableFileName,
        Func<string, bool>? fileExists = null)
    {
        var existsCheck = fileExists ?? (path => File.Exists(PathSupport.ToIoPath(path)));
        var fallbackExpected = string.IsNullOrWhiteSpace(cliFileName) ? CliExecutableFileName : cliFileName;

        try
        {
            var normalizedBase = string.IsNullOrWhiteSpace(baseDirectory)
                ? AppContext.BaseDirectory
                : baseDirectory;
            var expectedPath = Path.Combine(normalizedBase, cliFileName);
            string fullExpected;
            try
            {
                fullExpected = Path.GetFullPath(expectedPath);
            }
            catch
            {
                fullExpected = expectedPath;
            }

            if (existsCheck(expectedPath))
            {
                return new CliLookupResult(fullExpected, fullExpected, WarningMessage: null, Error: null);
            }

            if (!string.IsNullOrWhiteSpace(processPath))
            {
                var processDir = Path.GetDirectoryName(processPath);
                if (!string.IsNullOrWhiteSpace(processDir))
                {
                    var processCandidate = Path.Combine(processDir, cliFileName);
                    if (existsCheck(processCandidate))
                    {
                        var fullCandidate = Path.GetFullPath(processCandidate);
                        return new CliLookupResult(fullCandidate, fullExpected, WarningMessage: null, Error: null);
                    }
                }
            }

            return new CliLookupResult(
                ResolvedPath: null,
                ExpectedPath: fullExpected,
                WarningMessage: L.Format("Gui_CliNotFoundWarning", cliFileName, fullExpected),
                Error: null);
        }
        catch (Exception ex)
        {
            return new CliLookupResult(
                ResolvedPath: null,
                ExpectedPath: fallbackExpected,
                WarningMessage: L.Format("Gui_CliCheckErrorWarning", cliFileName, ex.Message),
                Error: ex);
        }
    }

    public static string FormatMissingCliMessage(string expectedPath)
    {
        var fileName = Path.GetFileName(expectedPath);
        return L.Format("Gui_MissingCliMessage", fileName, expectedPath);
    }

    public static string[] BuildBuildDbArguments(
        string gameDirectory,
        string outputDirectory,
        bool force = false,
        string? steamBuildId = null,
        bool keepJson = false,
        int? jobs = null,
        string? language = null,
        bool progressJson = false)
    {
        if (string.IsNullOrWhiteSpace(gameDirectory))
        {
            throw new ArgumentException(L.Get("Gui_ArgGameDirEmpty"), nameof(gameDirectory));
        }

        if (string.IsNullOrWhiteSpace(outputDirectory))
        {
            throw new ArgumentException(L.Get("Gui_ArgOutputDirEmpty"), nameof(outputDirectory));
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

        if (!string.IsNullOrWhiteSpace(language))
        {
            args.Add(AppLocalization.CliOptionName);
            args.Add(language.Trim());
        }

        if (progressJson)
        {
            args.Add("--progress-json");
        }

        return [.. args];
    }

    public static ProcessStartInfo CreateStartInfo(
        string cliExecutablePath,
        IReadOnlyList<string> arguments,
        string? workingDirectory = null,
        string? agpHome = null,
        string? tempDirectory = null)
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

        if (!string.IsNullOrWhiteSpace(agpHome))
        {
            startInfo.Environment[AgpPaths.AgpHomeEnvVar] = agpHome.Trim();
        }

        if (!string.IsNullOrWhiteSpace(tempDirectory))
        {
            var trimmedTemp = tempDirectory.Trim();
            startInfo.Environment["TEMP"] = trimmedTemp;
            startInfo.Environment["TMP"] = trimmedTemp;
        }

        foreach (var arg in arguments)
        {
            startInfo.ArgumentList.Add(arg);
        }

        return startInfo;
    }

    public static bool IsProgressProtocolLine(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        var trimmed = line.TrimStart();
        return trimmed.StartsWith(ProgressProtocolPrefix, StringComparison.Ordinal)
            && (trimmed.Length == ProgressProtocolPrefix.Length || char.IsWhiteSpace(trimmed[ProgressProtocolPrefix.Length]));
    }

    /// <summary>
    /// Parses machine-readable progress lines in the language-independent format:
    /// <c>@@progress {"step":n,"total":m}</c> (with optional <c>"message"</c>).
    /// Human-readable step lines (such as <c>==> Step n/m</c>) are intentionally ignored.
    /// </summary>
    public static bool TryParseProgressStep(string? line, out ProgressStepUpdate update)
    {
        update = default!;
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        var trimmed = line.Trim();
        if (!trimmed.StartsWith(ProgressProtocolPrefix, StringComparison.Ordinal)
            || trimmed.Length <= ProgressProtocolPrefix.Length
            || !char.IsWhiteSpace(trimmed[ProgressProtocolPrefix.Length]))
        {
            return false;
        }

        var payload = trimmed[ProgressProtocolPrefix.Length..].Trim();
        if (payload.Length == 0 || payload[0] != '{')
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            if (!root.TryGetProperty("step", out var stepEl)
                || stepEl.ValueKind != JsonValueKind.Number
                || !stepEl.TryGetInt32(out var current)
                || !root.TryGetProperty("total", out var totalEl)
                || totalEl.ValueKind != JsonValueKind.Number
                || !totalEl.TryGetInt32(out var total)
                || total <= 0
                || current < 1
                || current > total)
            {
                return false;
            }

            string? description = null;
            if (root.TryGetProperty("message", out var msgEl) && msgEl.ValueKind == JsonValueKind.String)
            {
                var rawMsg = msgEl.GetString()?.Trim();
                if (!string.IsNullOrWhiteSpace(rawMsg))
                {
                    description = rawMsg;
                }
            }

            update = new ProgressStepUpdate(current, total, description);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static string FormatGameProgressHeader(
        int currentIndexOneBased,
        int totalCount,
        GuiGameEntry game,
        ProgressStepUpdate? step = null)
    {
        var prefix = L.Format("Gui_ProgressGamePrefix", currentIndexOneBased, totalCount, game.GameCode);
        if (step is null)
        {
            return prefix;
        }

        var stepPart = string.IsNullOrWhiteSpace(step.Description)
            ? L.Format("Gui_ProgressStepOnly", step.CurrentStep, step.TotalSteps)
            : L.Format("Gui_ProgressStepWithDesc", step.CurrentStep, step.TotalSteps, step.Description);
        return L.Format("Gui_ProgressGameWithStep", prefix, stepPart);
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
