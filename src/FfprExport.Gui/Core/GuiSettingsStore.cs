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

    [JsonPropertyName("language")]
    public string? Language { get; set; }

    [JsonPropertyName("checked_games")]
    public Dictionary<string, bool> CheckedGames { get; set; } = new(StringComparer.Ordinal);
}

internal sealed record GuiSettingsLoadResult(
    GuiSettings Settings,
    string? WarningMessage,
    Exception? Error,
    string? InfoMessage = null,
    string? MigratedFromPath = null,
    string? MigratedBackupPath = null);

/// <summary>
/// Persists last output root directory, UI language, and per-game checkbox selections to
/// &lt;AGP root&gt;\ffpr-export\settings.json without touching the Windows Registry,
/// and migrates legacy %APPDATA%\ffpr-export\settings.json on first launch.
/// </summary>
internal static class GuiSettingsStore
{
    public const string MigratedSuffix = ".migrated";

    private static readonly JsonSerializerOptions SerializerOptions = TextFileWriter.CreateJsonSerializerOptions(
        writeIndented: true);

    public static string GetDefaultSettingsPath(AgpRootResolution root)
    {
        return root.Succeeded && !string.IsNullOrWhiteSpace(root.AgpHome)
            ? AgpPaths.GetSettingsPath(root)
            : string.Empty;
    }

    public static string GetDefaultSettingsPath(
        bool? isWindowsOverride = null,
        Func<string, string?>? getEnvironmentVariable = null,
        Func<Environment.SpecialFolder, string?>? getSpecialFolder = null,
        Func<string?>? getBaseDirectory = null,
        Func<string?>? getLocalLowDirectory = null,
        Func<string, (bool Writable, string? Error)>? probeWritable = null,
        Func<string, string?>? readLocationFile = null,
        ProcessIntegrityInfo? integrityOverride = null,
        Func<ProcessIntegrityInfo>? detectIntegrity = null)
    {
        var root = AgpPaths.ResolveAgpHome(
            isWindowsOverride,
            getEnvironmentVariable,
            getSpecialFolder,
            getBaseDirectory,
            getLocalLowDirectory,
            probeWritable,
            readLocationFile,
            integrityOverride,
            detectIntegrity);
        return GetDefaultSettingsPath(root);
    }

    /// <summary>
    /// Returns the legacy settings path (%APPDATA%\ffpr-export\settings.json on Windows,
    /// ~/.config/ffpr-export/settings.json on Linux/macOS) used prior to AGP root consolidation.
    /// </summary>
    public static string? GetLegacySettingsPath(
        Func<Environment.SpecialFolder, string?>? getSpecialFolder = null,
        Func<string, string?>? getEnvironmentVariable = null)
    {
        var getFolder = getSpecialFolder ?? (folder => Environment.GetFolderPath(folder));
        var getEnv = getEnvironmentVariable ?? Environment.GetEnvironmentVariable;

        string? appData = null;
        try
        {
            appData = getFolder(Environment.SpecialFolder.ApplicationData);
        }
        catch
        {
            appData = null;
        }

        if (string.IsNullOrWhiteSpace(appData))
        {
            try
            {
                appData = getEnv("APPDATA");
            }
            catch
            {
                appData = null;
            }
        }

        if (string.IsNullOrWhiteSpace(appData))
        {
            return null;
        }

        try
        {
            return Path.Combine(appData.Trim(), AgpPaths.ToolName, AgpPaths.SettingsFileName);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Resolves the legacy settings path for automatic startup migration, returning null when
    /// AGP_HOME is explicitly set so tests, CI, and isolated AGP_HOME environments never read
    /// or rename the host user's real %APPDATA% settings file.
    /// </summary>
    public static string? GetLegacySettingsPathForMigration(
        AgpRootResolution root,
        Func<string, string?>? getEnvironmentVariable = null,
        Func<Environment.SpecialFolder, string?>? getSpecialFolder = null)
    {
        if (!root.Succeeded || root.Source == AgpRootSource.AgpHomeEnv)
        {
            return null;
        }

        return GetLegacySettingsPath(getSpecialFolder, getEnvironmentVariable);
    }

    public static string? GetLegacySettingsPathForMigration(
        bool? isWindowsOverride = null,
        Func<string, string?>? getEnvironmentVariable = null,
        Func<Environment.SpecialFolder, string?>? getSpecialFolder = null,
        Func<string?>? getBaseDirectory = null,
        Func<string?>? getLocalLowDirectory = null,
        Func<string, (bool Writable, string? Error)>? probeWritable = null,
        Func<string, string?>? readLocationFile = null,
        ProcessIntegrityInfo? integrityOverride = null,
        Func<ProcessIntegrityInfo>? detectIntegrity = null)
    {
        var root = AgpPaths.ResolveAgpHome(
            isWindowsOverride,
            getEnvironmentVariable,
            getSpecialFolder,
            getBaseDirectory,
            getLocalLowDirectory,
            probeWritable,
            readLocationFile,
            integrityOverride,
            detectIntegrity);
        return GetLegacySettingsPathForMigration(root, getEnvironmentVariable, getSpecialFolder);
    }

    /// <summary>
    /// Loads GUI settings from the new unified path. If the new settings file does not yet exist
    /// while a legacy settings file exists, reads the legacy file, writes it to the new path,
    /// and renames the legacy file to settings.json.migrated without deleting it.
    /// </summary>
    public static GuiSettingsLoadResult LoadWithMigration(
        string? settingsPath,
        string? legacySettingsPath,
        Func<string, string>? readAllText = null)
    {
        if (string.IsNullOrWhiteSpace(settingsPath) || string.IsNullOrWhiteSpace(legacySettingsPath))
        {
            return LoadWithDiagnostics(settingsPath, readAllText);
        }

        string newFull;
        string legacyFull;
        string ioNewPath;
        string ioLegacyPath;
        try
        {
            newFull = PathSupport.TrimTrailingSeparators(Path.GetFullPath(settingsPath.Trim()));
            legacyFull = PathSupport.TrimTrailingSeparators(Path.GetFullPath(legacySettingsPath.Trim()));
            ioNewPath = PathSupport.ToIoPath(newFull);
            ioLegacyPath = PathSupport.ToIoPath(legacyFull);
        }
        catch
        {
            return LoadWithDiagnostics(settingsPath, readAllText);
        }

        if (GuiGameScanner.PathComparer.Equals(newFull, legacyFull))
        {
            return LoadWithDiagnostics(settingsPath, readAllText);
        }

        if (File.Exists(ioNewPath) || Directory.Exists(ioNewPath))
        {
            return LoadWithDiagnostics(settingsPath, readAllText);
        }

        if (!File.Exists(ioLegacyPath) || Directory.Exists(ioLegacyPath))
        {
            return LoadWithDiagnostics(settingsPath, readAllText);
        }

        var legacyResult = LoadWithDiagnostics(legacyFull, readAllText);
        if (legacyResult.Error is not null || !string.IsNullOrWhiteSpace(legacyResult.WarningMessage))
        {
            return new GuiSettingsLoadResult(
                new GuiSettings(),
                L.Format("Gui_SettingsMigrateReadFailed", legacyFull, legacyResult.WarningMessage),
                legacyResult.Error);
        }

        try
        {
            Save(newFull, legacyResult.Settings);
        }
        catch (Exception ex)
        {
            return new GuiSettingsLoadResult(
                legacyResult.Settings,
                L.Format("Gui_SettingsMigrateWriteFailed", legacyFull, newFull, ex.Message),
                ex);
        }

        var migratedPath = legacyFull + MigratedSuffix;
        try
        {
            File.Move(ioLegacyPath, PathSupport.ToIoPath(migratedPath), overwrite: true);
        }
        catch (Exception ex)
        {
            return new GuiSettingsLoadResult(
                legacyResult.Settings,
                L.Format("Gui_SettingsMigrateRenameFailed", newFull, migratedPath, ex.Message),
                ex,
                MigratedFromPath: legacyFull);
        }

        return new GuiSettingsLoadResult(
            legacyResult.Settings,
            WarningMessage: null,
            Error: null,
            InfoMessage: L.Format("Gui_SettingsMigratedInfo", legacyFull, newFull, migratedPath),
            MigratedFromPath: legacyFull,
            MigratedBackupPath: migratedPath);
    }

    public static GuiSettings Load(string settingsPath)
    {
        return LoadWithDiagnostics(settingsPath).Settings;
    }

    /// <summary>
    /// Best-effort read-only peek of the saved UI language before MainForm runs full settings
    /// loading/migration so startup logs and early UI controls start in the persisted language.
    /// </summary>
    public static string? TryPeekSavedLanguage(
        string? settingsPath,
        string? legacySettingsPath = null,
        Func<string, string>? readAllText = null)
    {
        var primary = TryPeekLanguageFromFile(settingsPath, readAllText);
        if (!string.IsNullOrWhiteSpace(primary))
        {
            return primary;
        }

        return TryPeekLanguageFromFile(legacySettingsPath, readAllText);
    }

    private static string? TryPeekLanguageFromFile(string? path, Func<string, string>? readAllText)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var result = LoadWithDiagnostics(path, readAllText);
        return AppLocalization.TryParseLanguage(result.Settings.Language, out var parsed)
            ? AppLocalization.ToCode(parsed)
            : null;
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
                L.Format("Gui_SettingsPathInvalid", settingsPath, ex.Message),
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
                        L.Format("Gui_SettingsPathIsDir", settingsPath),
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
                    L.Format("Gui_SettingsEmptyOrInvalid", settingsPath),
                    null);
            }

            parsed.CheckedGames ??= new Dictionary<string, bool>(StringComparer.Ordinal);
            return new GuiSettingsLoadResult(parsed, WarningMessage: null, Error: null);
        }
        catch (UnauthorizedAccessException ex)
        {
            return new GuiSettingsLoadResult(
                new GuiSettings(),
                L.Format("Gui_SettingsUnauthorized", settingsPath, ex.Message),
                ex);
        }
        catch (JsonException ex)
        {
            return new GuiSettingsLoadResult(
                new GuiSettings(),
                L.Format("Gui_SettingsCorruptJson", settingsPath, ex.Message),
                ex);
        }
        catch (Exception ex)
        {
            return new GuiSettingsLoadResult(
                new GuiSettings(),
                L.Format("Gui_LoadSettingsFailed", settingsPath, ex.Message),
                ex);
        }
    }

    public static void Save(string settingsPath, GuiSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settingsPath))
        {
            return;
        }

        string? normalizedLanguage = AppLocalization.TryParseLanguage(settings.Language, out var parsedLanguage)
            ? AppLocalization.ToCode(parsedLanguage)
            : null;

        var payload = new GuiSettings
        {
            Format = 1,
            OutputRoot = string.IsNullOrWhiteSpace(settings.OutputRoot) ? null : settings.OutputRoot.Trim(),
            Language = normalizedLanguage,
            CheckedGames = new Dictionary<string, bool>(settings.CheckedGames, StringComparer.Ordinal),
        };

        TextFileWriter.WriteJsonAtomic(settingsPath, payload, SerializerOptions, trailingNewLine: true);
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
