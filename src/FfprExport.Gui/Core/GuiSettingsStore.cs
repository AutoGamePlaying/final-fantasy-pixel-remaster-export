using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FfprExport.Gui;

internal sealed class GuiSettings
{
    [JsonPropertyName("format")]
    public int Format { get; set; } = 1;

    [JsonPropertyName("output_root")]
    public string? OutputRoot { get; set; }

    [JsonPropertyName("checked_games")]
    public Dictionary<string, bool> CheckedGames { get; set; } = new(StringComparer.Ordinal);
}

internal sealed record GuiSettingsLoadResult(
    GuiSettings Settings,
    string? WarningMessage,
    Exception? Error);

/// <summary>
/// Persists last output root directory and per-game checkbox selections to
/// %APPDATA%\ffpr-export\settings.json without touching the Windows Registry.
/// </summary>
internal static class GuiSettingsStore
{
    private static readonly UTF8Encoding Utf8WithoutBom = new(encoderShouldEmitUTF8Identifier: false);

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
    };

    public static string GetDefaultSettingsPath(
        string? appDataOverride = null,
        Func<string?>? systemAppDataProvider = null,
        string? fallbackBaseDirectory = null,
        string? tempDirectoryOverride = null)
    {
        string? appData = null;
        if (!string.IsNullOrWhiteSpace(appDataOverride))
        {
            appData = appDataOverride.Trim();
        }
        else
        {
            try
            {
                appData = systemAppDataProvider is not null
                    ? systemAppDataProvider()
                    : Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            }
            catch
            {
                appData = null;
            }
        }

        if (string.IsNullOrWhiteSpace(appData))
        {
            appData = !string.IsNullOrWhiteSpace(fallbackBaseDirectory)
                ? fallbackBaseDirectory.Trim()
                : AppContext.BaseDirectory;
        }

        if (string.IsNullOrWhiteSpace(appData))
        {
            try
            {
                appData = !string.IsNullOrWhiteSpace(tempDirectoryOverride)
                    ? tempDirectoryOverride.Trim()
                    : Path.GetTempPath();
            }
            catch
            {
                appData = ".";
            }
        }

        try
        {
            return Path.Combine(appData, GuiOutputPlanner.ToolName, "settings.json");
        }
        catch
        {
            return Path.Combine(".", GuiOutputPlanner.ToolName, "settings.json");
        }
    }

    public static GuiSettings Load(string settingsPath)
    {
        return LoadWithDiagnostics(settingsPath).Settings;
    }

    public static GuiSettingsLoadResult LoadWithDiagnostics(
        string? settingsPath,
        Func<string, string>? readAllText = null)
    {
        if (string.IsNullOrWhiteSpace(settingsPath))
        {
            return new GuiSettingsLoadResult(new GuiSettings(), WarningMessage: null, Error: null);
        }

        string ioPath;
        try
        {
            var fullPath = Path.GetFullPath(settingsPath);
            ioPath = PathSupport.ToIoPath(fullPath);
        }
        catch (Exception ex)
        {
            return new GuiSettingsLoadResult(
                new GuiSettings(),
                $"读取设置失败：设置文件路径无效（{settingsPath}）：{ex.Message}；已改用默认设置。",
                ex);
        }

        try
        {
            string json;
            if (readAllText is not null)
            {
                json = readAllText(ioPath);
            }
            else
            {
                if (Directory.Exists(ioPath))
                {
                    return new GuiSettingsLoadResult(
                        new GuiSettings(),
                        $"读取设置失败：设置路径指向目录而非文件（{settingsPath}）；已改用默认设置。",
                        null);
                }

                if (!File.Exists(ioPath))
                {
                    return new GuiSettingsLoadResult(new GuiSettings(), WarningMessage: null, Error: null);
                }

                json = File.ReadAllText(ioPath, Encoding.UTF8);
            }

            var parsed = JsonSerializer.Deserialize<GuiSettings>(json);
            if (parsed is null)
            {
                return new GuiSettingsLoadResult(
                    new GuiSettings(),
                    $"读取设置失败：设置文件内容为空或格式无效（{settingsPath}）；已改用默认设置。",
                    null);
            }

            parsed.CheckedGames ??= new Dictionary<string, bool>(StringComparer.Ordinal);
            return new GuiSettingsLoadResult(parsed, WarningMessage: null, Error: null);
        }
        catch (UnauthorizedAccessException ex)
        {
            return new GuiSettingsLoadResult(
                new GuiSettings(),
                $"读取设置失败：没有读取权限（{settingsPath}）：{ex.Message}；已改用默认设置。",
                ex);
        }
        catch (JsonException ex)
        {
            return new GuiSettingsLoadResult(
                new GuiSettings(),
                $"读取设置失败：设置文件已损坏，JSON 解析出错（{settingsPath}）：{ex.Message}；已改用默认设置。",
                ex);
        }
        catch (Exception ex)
        {
            return new GuiSettingsLoadResult(
                new GuiSettings(),
                $"读取设置失败（{settingsPath}）：{ex.Message}；已改用默认设置。",
                ex);
        }
    }

    public static void Save(string settingsPath, GuiSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settingsPath))
        {
            return;
        }

        var ioTarget = PathSupport.ToIoPath(settingsPath);
        var parent = Path.GetDirectoryName(ioTarget);
        if (!string.IsNullOrEmpty(parent))
        {
            Directory.CreateDirectory(parent);
        }

        var payload = new GuiSettings
        {
            Format = 1,
            OutputRoot = string.IsNullOrWhiteSpace(settings.OutputRoot) ? null : settings.OutputRoot.Trim(),
            CheckedGames = new Dictionary<string, bool>(settings.CheckedGames, StringComparer.Ordinal),
        };

        var json = JsonSerializer.Serialize(payload, SerializerOptions) + "\n";
        var tempPath = $"{ioTarget}.{Environment.ProcessId}.{Environment.CurrentManagedThreadId}.tmp";
        File.WriteAllText(tempPath, json, Utf8WithoutBom);
        File.Move(tempPath, ioTarget, overwrite: true);
    }

    /// <summary>
    /// Returns the remembered checkbox state for a game directory, defaulting to true so newly
    /// discovered games are ready for one-click export.
    /// </summary>
    public static bool ResolveInitialCheckedState(GuiSettings settings, string gameDirectory)
    {
        if (settings.CheckedGames is null || settings.CheckedGames.Count == 0)
        {
            return true;
        }

        var targetKey = GuiGameScanner.NormalizePathKey(gameDirectory);
        var comparison = OperatingSystem.IsWindows() || LooksLikeWindowsPath(targetKey)
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        foreach (var (savedPath, isChecked) in settings.CheckedGames)
        {
            var normalizedSaved = GuiGameScanner.NormalizePathKey(savedPath);
            if (string.Equals(normalizedSaved, targetKey, comparison))
            {
                return isChecked;
            }
        }

        return true;
    }

    private static bool LooksLikeWindowsPath(string path)
    {
        return (path.Length >= 2 && char.IsAsciiLetter(path[0]) && path[1] == ':')
            || path.StartsWith(@"\\", StringComparison.Ordinal);
    }
}
