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

    public static string GetDefaultSettingsPath(string? appDataOverride = null)
    {
        var appData = !string.IsNullOrWhiteSpace(appDataOverride)
            ? appDataOverride.Trim()
            : Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        if (string.IsNullOrWhiteSpace(appData))
        {
            appData = AppContext.BaseDirectory;
        }

        return Path.Combine(appData, GuiOutputPlanner.ToolName, "settings.json");
    }

    public static GuiSettings Load(string settingsPath)
    {
        if (string.IsNullOrWhiteSpace(settingsPath))
        {
            return new GuiSettings();
        }

        var ioPath = PathSupport.ToIoPath(settingsPath);
        if (!File.Exists(ioPath))
        {
            return new GuiSettings();
        }

        try
        {
            var json = File.ReadAllText(ioPath, Encoding.UTF8);
            var parsed = JsonSerializer.Deserialize<GuiSettings>(json);
            if (parsed is null)
            {
                return new GuiSettings();
            }

            parsed.CheckedGames ??= new Dictionary<string, bool>(StringComparer.Ordinal);
            return parsed;
        }
        catch
        {
            return new GuiSettings();
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
