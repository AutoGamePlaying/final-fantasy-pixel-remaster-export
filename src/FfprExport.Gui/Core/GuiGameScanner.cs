using System.Text;

namespace FfprExport.Gui;

/// <summary>
/// Discovers FF1-FF6 Pixel Remaster installations via the shared SteamLocator and validates
/// manually selected game directories without pulling AssetsTools.NET into the GUI binary.
/// </summary>
internal static class GuiGameScanner
{
    public static StringComparer PathComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public static IReadOnlyList<GuiGameEntry> DiscoverSteamGames()
    {
        return MapDiscoveredGames(SteamLocator.DiscoverGames());
    }

    internal static IReadOnlyList<GuiGameEntry> DiscoverGamesInSteamRoots(IEnumerable<string> steamRoots)
    {
        return MapDiscoveredGames(SteamLocator.DiscoverGamesInSteamRoots(steamRoots));
    }

    internal static IReadOnlyList<GuiGameEntry> MapDiscoveredGames(IEnumerable<DiscoveredSteamGame> discovered)
    {
        var results = new List<GuiGameEntry>();
        foreach (var game in discovered)
        {
            results.Add(new GuiGameEntry(
                game.Spec,
                game.DisplayName,
                game.GameDirectory,
                game.SteamBuildId,
                string.IsNullOrWhiteSpace(game.AppManifestPath) ? null : game.AppManifestPath,
                IsManual: false));
        }

        return results;
    }

    /// <summary>
    /// Validates a user-selected directory to ensure it contains a supported FF1-FF6 Pixel Remaster
    /// installation (matching *_Data folder or known game executable + *_Data folder) and attempts
    /// to read the Steam buildid from ../../appmanifest_&lt;appid&gt;.acf when present.
    /// </summary>
    public static GuiGameEntry InspectManualGameDirectory(string rawDirectoryPath)
    {
        var normalizedInput = NormalizeUserPath(rawDirectoryPath);
        if (string.IsNullOrWhiteSpace(normalizedInput))
        {
            throw new ArgumentException("请选择游戏目录");
        }

        string fullGameDir;
        try
        {
            fullGameDir = PathSupport.TrimTrailingSeparators(Path.GetFullPath(normalizedInput));
        }
        catch (Exception ex)
        {
            throw new ArgumentException($"游戏目录路径格式无效：{normalizedInput}（{ex.Message}）", ex);
        }

        var ioGameDir = PathSupport.ToIoPath(fullGameDir);
        if (!Directory.Exists(ioGameDir))
        {
            throw new DirectoryNotFoundException($"游戏目录不存在：{fullGameDir}");
        }

        var spec = IdentifyGameSpec(fullGameDir, ioGameDir);
        var (manifestName, steamBuildId, manifestPath) = TryReadAppManifestNearGame(fullGameDir, spec.AppId);
        var displayName = string.IsNullOrWhiteSpace(manifestName) ? spec.Title : manifestName.Trim();

        return new GuiGameEntry(
            spec,
            displayName,
            fullGameDir,
            string.IsNullOrWhiteSpace(steamBuildId) ? null : steamBuildId.Trim(),
            manifestPath,
            IsManual: true);
    }

    public static string NormalizeUserPath(string? rawInput)
    {
        if (string.IsNullOrWhiteSpace(rawInput))
        {
            return string.Empty;
        }

        var text = rawInput.Trim();
        while (text.Length >= 2)
        {
            var first = text[0];
            var last = text[^1];
            var isMatchingQuotePair = (first == '"' && last == '"')
                || (first == '\'' && last == '\'')
                || (first == '“' && last == '”')
                || (first == '‘' && last == '’');
            if (!isMatchingQuotePair)
            {
                break;
            }

            text = text[1..^1].Trim();
        }

        return text;
    }

    public static string NormalizePathKey(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        try
        {
            return PathSupport.TrimTrailingSeparators(Path.GetFullPath(path.Trim()));
        }
        catch
        {
            return path.Trim();
        }
    }

    private static GameSpec IdentifyGameSpec(string fullGameDir, string ioGameDir)
    {
        var dirs = Directory.GetDirectories(ioGameDir)
            .Select(path => Path.GetFileName(path) ?? string.Empty)
            .Where(name => !string.IsNullOrEmpty(name))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var files = Directory.GetFiles(ioGameDir)
            .Select(path => Path.GetFileName(path) ?? string.Empty)
            .Where(name => !string.IsNullOrEmpty(name))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Prefer matching *_Data directory first, matching GameDetector.IdentifyGame behavior.
        foreach (var spec in GameDetector.KnownGames)
        {
            if (dirs.Contains(spec.DataDirName))
            {
                return spec;
            }
        }

        foreach (var spec in GameDetector.KnownGames)
        {
            if (files.Contains(spec.ExeName))
            {
                var expectedDataDir = Path.Combine(fullGameDir, spec.DataDirName);
                throw new DirectoryNotFoundException(
                    $"识别到 {spec.Title}（{spec.ExeName}），但缺少数据目录：{expectedDataDir}");
            }
        }

        throw new InvalidDataException(
            $"无法从目录识别 FF1–FF6 Pixel Remaster 游戏（未找到已知 exe 或 *_Data 目录）：{fullGameDir}");
    }

    private static (string? Name, string? BuildId, string? ManifestPath) TryReadAppManifestNearGame(
        string fullGameDir,
        int appId)
    {
        var commonDir = Directory.GetParent(fullGameDir);
        var steamAppsDir = commonDir?.Parent;
        if (steamAppsDir is null)
        {
            return (null, null, null);
        }

        var ioSteamApps = PathSupport.ToIoPath(steamAppsDir.FullName);
        if (!Directory.Exists(ioSteamApps))
        {
            return (null, null, null);
        }

        var manifestFileName = $"appmanifest_{appId}.acf";
        string? manifestPath = null;
        var directPath = Path.Combine(steamAppsDir.FullName, manifestFileName);
        if (File.Exists(PathSupport.ToIoPath(directPath)))
        {
            manifestPath = directPath;
        }
        else
        {
            try
            {
                foreach (var candidate in Directory.EnumerateFiles(ioSteamApps))
                {
                    var name = Path.GetFileName(candidate);
                    if (string.Equals(name, manifestFileName, StringComparison.OrdinalIgnoreCase))
                    {
                        manifestPath = Path.Combine(steamAppsDir.FullName, name);
                        break;
                    }
                }
            }
            catch
            {
                return (null, null, null);
            }
        }

        if (manifestPath is null)
        {
            return (null, null, null);
        }

        try
        {
            var content = File.ReadAllText(PathSupport.ToIoPath(manifestPath), Encoding.UTF8);
            var (name, _, buildId) = SteamLocator.ParseAppManifest(content);
            return (name, buildId, manifestPath);
        }
        catch
        {
            return (null, null, manifestPath);
        }
    }
}
