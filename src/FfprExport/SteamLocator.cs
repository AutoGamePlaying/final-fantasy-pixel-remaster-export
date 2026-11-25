using System.Runtime.InteropServices;
using System.Text;

namespace FfprExport;

internal sealed record DiscoveredSteamGame(
    GameSpec Spec,
    string DisplayName,
    string GameDirectory,
    string? SteamBuildId,
    string AppManifestPath);

/// <summary>
/// Locates local Steam installations, parses libraryfolders.vdf and appmanifest_*.acf files,
/// and discovers installed FINAL FANTASY I-VI Pixel Remaster games without modifying game files.
/// </summary>
internal static class SteamLocator
{
    internal const string DefaultWindowsSteamPath = @"C:\Program Files (x86)\Steam";

    // Win32 HKEY_CURRENT_USER is sign-extended 0xFFFFFFFF80000001 on 64-bit Windows.
    private static readonly nint HkeyCurrentUser = new(unchecked((int)0x80000001));
    private const uint RrfRtRegSz = 0x00000002;
    private const uint RrfRtRegExpandSz = 0x00000004;

    private sealed record VdfEntry(string Key, string? StringValue, VdfObject? ObjectValue);

    private sealed class VdfObject
    {
        public List<VdfEntry> Entries { get; } = [];

        public string? GetString(string key)
        {
            foreach (var entry in Entries)
            {
                if (entry.StringValue is not null
                    && string.Equals(entry.Key, key, StringComparison.OrdinalIgnoreCase))
                {
                    return entry.StringValue;
                }
            }

            return null;
        }

        public VdfObject? GetObject(string key)
        {
            foreach (var entry in Entries)
            {
                if (entry.ObjectValue is not null
                    && string.Equals(entry.Key, key, StringComparison.OrdinalIgnoreCase))
                {
                    return entry.ObjectValue;
                }
            }

            return null;
        }
    }

    public static IReadOnlyList<DiscoveredSteamGame> DiscoverGames()
    {
        var steamRoots = DiscoverSteamRoots();
        return DiscoverGamesInSteamRoots(steamRoots);
    }

    internal static IReadOnlyList<string> DiscoverSteamRoots()
    {
        if (OperatingSystem.IsWindows())
        {
            return ResolveWindowsSteamRoots(
                TryReadWindowsSteamRegistryPath,
                static path => Directory.Exists(PathSupport.ToIoPath(path)));
        }

        return ResolveLinuxSteamRoots(
            GetUserHomeDirectory(),
            static path => Directory.Exists(PathSupport.ToIoPath(path)));
    }

    internal static IReadOnlyList<string> ResolveWindowsSteamRoots(
        Func<string?> readRegistrySteamPath,
        Func<string, bool> directoryExists)
    {
        var regPath = readRegistrySteamPath();
        if (!string.IsNullOrWhiteSpace(regPath))
        {
            var normalizedRegPath = NormalizeWindowsPathSeparator(regPath.Trim());
            if (!string.IsNullOrEmpty(normalizedRegPath) && directoryExists(normalizedRegPath))
            {
                return [normalizedRegPath];
            }
        }

        if (directoryExists(DefaultWindowsSteamPath))
        {
            return [DefaultWindowsSteamPath];
        }

        return [];
    }

    internal static IReadOnlyList<string> ResolveLinuxSteamRoots(
        string? homeDirectory,
        Func<string, bool> directoryExists)
    {
        if (string.IsNullOrWhiteSpace(homeDirectory))
        {
            return [];
        }

        var trimmedHome = homeDirectory.Trim();
        string[] candidates =
        [
            Path.Combine(trimmedHome, ".steam", "steam"),
            Path.Combine(trimmedHome, ".local", "share", "Steam"),
        ];

        var results = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var candidate in candidates)
        {
            if (!directoryExists(candidate))
            {
                continue;
            }

            var key = GetCanonicalDirectoryKey(candidate);
            if (seen.Add(key))
            {
                results.Add(candidate);
            }
        }

        return results;
    }

    internal static IReadOnlyList<string> DiscoverLibraryFolders(IEnumerable<string> steamRoots)
    {
        var results = new List<string>();
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var seen = new HashSet<string>(comparer);

        void TryAddLibrary(string rawPath)
        {
            if (string.IsNullOrWhiteSpace(rawPath))
            {
                return;
            }

            string fullPath;
            try
            {
                fullPath = PathSupport.TrimTrailingSeparators(Path.GetFullPath(rawPath.Trim()));
            }
            catch
            {
                return;
            }

            if (!Directory.Exists(PathSupport.ToIoPath(fullPath)))
            {
                return;
            }

            var canonicalKey = GetCanonicalDirectoryKey(fullPath);
            if (seen.Add(canonicalKey))
            {
                results.Add(fullPath);
            }
        }

        foreach (var steamRoot in steamRoots)
        {
            if (string.IsNullOrWhiteSpace(steamRoot))
            {
                continue;
            }

            TryAddLibrary(steamRoot);

            var steamAppsDir = FindSubdirectoryCaseInsensitive(steamRoot, "steamapps");
            var vdfPath = steamAppsDir is not null
                ? FindFileCaseInsensitive(steamAppsDir, "libraryfolders.vdf")
                : null;

            if (vdfPath is null)
            {
                var configDir = FindSubdirectoryCaseInsensitive(steamRoot, "config");
                vdfPath = configDir is not null
                    ? FindFileCaseInsensitive(configDir, "libraryfolders.vdf")
                    : null;
            }

            if (vdfPath is null)
            {
                continue;
            }

            string content;
            try
            {
                content = File.ReadAllText(PathSupport.ToIoPath(vdfPath), Encoding.UTF8);
            }
            catch
            {
                continue;
            }

            foreach (var libraryPath in ParseLibraryFoldersVdf(content))
            {
                TryAddLibrary(libraryPath);
            }
        }

        return results;
    }

    /// <summary>
    /// Parses Steam's libraryfolders.vdf (both modern nested format with "path" keys and legacy flat numeric format)
    /// and returns unescaped, deduplicated library paths in declaration order.
    /// </summary>
    internal static IReadOnlyList<string> ParseLibraryFoldersVdf(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return [];
        }

        var root = ParseVdf(content);
        var container = root.GetObject("libraryfolders") ?? root;

        var results = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in container.Entries)
        {
            string? rawPath = null;
            if (entry.ObjectValue is not null)
            {
                rawPath = entry.ObjectValue.GetString("path");
            }
            else if (entry.StringValue is not null && IsAsciiDigits(entry.Key))
            {
                rawPath = entry.StringValue;
            }

            if (string.IsNullOrWhiteSpace(rawPath))
            {
                continue;
            }

            var cleaned = TrimParsedLibraryPath(rawPath.Trim());
            if (string.IsNullOrEmpty(cleaned))
            {
                continue;
            }

            var dedupeKey = OperatingSystem.IsWindows() || LooksLikeWindowsPath(cleaned)
                ? cleaned.Replace('/', '\\')
                : cleaned;

            if (OperatingSystem.IsWindows() || LooksLikeWindowsPath(cleaned))
            {
                if (seen.Add(dedupeKey))
                {
                    results.Add(cleaned);
                }
            }
            else if (!results.Contains(cleaned, StringComparer.Ordinal))
            {
                results.Add(cleaned);
            }
        }

        return results;
    }

    internal static IReadOnlyList<DiscoveredSteamGame> DiscoverGamesInSteamRoots(IEnumerable<string> steamRoots)
    {
        var libraries = DiscoverLibraryFolders(steamRoots);
        return DiscoverGamesInLibraryFolders(libraries);
    }

    internal static IReadOnlyList<DiscoveredSteamGame> DiscoverGamesInLibraryFolders(IEnumerable<string> libraryFolders)
    {
        var discovered = new List<DiscoveredSteamGame>();
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var seenGameDirs = new HashSet<string>(comparer);

        foreach (var library in libraryFolders)
        {
            if (string.IsNullOrWhiteSpace(library) || !Directory.Exists(PathSupport.ToIoPath(library)))
            {
                continue;
            }

            var steamAppsDir = FindSubdirectoryCaseInsensitive(library, "steamapps");
            if (steamAppsDir is null)
            {
                continue;
            }

            var commonDir = FindSubdirectoryCaseInsensitive(steamAppsDir, "common");
            if (commonDir is null)
            {
                continue;
            }

            foreach (var spec in GameDetector.KnownGames)
            {
                var manifestFileName = $"appmanifest_{spec.AppId}.acf";
                var manifestPath = FindFileCaseInsensitive(steamAppsDir, manifestFileName);
                if (manifestPath is null)
                {
                    continue;
                }

                string acfContent;
                try
                {
                    acfContent = File.ReadAllText(PathSupport.ToIoPath(manifestPath), Encoding.UTF8);
                }
                catch
                {
                    continue;
                }

                var (name, installDir, buildId) = ParseAppManifest(acfContent);
                if (string.IsNullOrWhiteSpace(installDir))
                {
                    continue;
                }

                var gameDir = FindSubdirectoryCaseInsensitive(commonDir, installDir.Trim());
                if (gameDir is null)
                {
                    continue;
                }

                var normalizedGameDir = PathSupport.TrimTrailingSeparators(Path.GetFullPath(gameDir));
                var canonicalKey = GetCanonicalDirectoryKey(normalizedGameDir);
                if (!seenGameDirs.Add(canonicalKey))
                {
                    continue;
                }

                var displayName = string.IsNullOrWhiteSpace(name) ? spec.Title : name.Trim();
                var normalizedBuildId = string.IsNullOrWhiteSpace(buildId) ? null : buildId.Trim();

                discovered.Add(new DiscoveredSteamGame(
                    spec,
                    displayName,
                    normalizedGameDir,
                    normalizedBuildId,
                    manifestPath));
            }
        }

        discovered.Sort((a, b) =>
        {
            var appCmp = a.Spec.AppId.CompareTo(b.Spec.AppId);
            if (appCmp != 0)
            {
                return appCmp;
            }

            return StringComparer.OrdinalIgnoreCase.Compare(a.GameDirectory, b.GameDirectory);
        });

        return discovered;
    }

    internal static (string? Name, string? InstallDir, string? BuildId) ParseAppManifest(string acfContent)
    {
        if (string.IsNullOrWhiteSpace(acfContent))
        {
            return (null, null, null);
        }

        var root = ParseVdf(acfContent);
        var appState = root.GetObject("AppState");
        if (appState is null)
        {
            return (null, null, null);
        }

        return (
            appState.GetString("name"),
            appState.GetString("installdir"),
            appState.GetString("buildid"));
    }

    private static VdfObject ParseVdf(string text)
    {
        var index = 0;
        if (text.Length > 0 && text[0] == '\uFEFF')
        {
            index = 1;
        }

        return ParseVdfBlock(text, ref index, stopAtClosingBrace: false);
    }

    private static VdfObject ParseVdfBlock(string text, ref int index, bool stopAtClosingBrace)
    {
        var obj = new VdfObject();

        while (index < text.Length)
        {
            SkipWhitespaceAndComments(text, ref index);
            if (index >= text.Length)
            {
                break;
            }

            var ch = text[index];
            if (ch == '}')
            {
                index++;
                if (stopAtClosingBrace)
                {
                    break;
                }

                continue;
            }

            if (ch == '{')
            {
                index++;
                _ = ParseVdfBlock(text, ref index, stopAtClosingBrace: true);
                continue;
            }

            var key = ReadVdfToken(text, ref index);
            SkipWhitespaceAndComments(text, ref index);
            if (index >= text.Length)
            {
                break;
            }

            if (text[index] == '{')
            {
                index++;
                var child = ParseVdfBlock(text, ref index, stopAtClosingBrace: true);
                obj.Entries.Add(new VdfEntry(key, null, child));
                continue;
            }

            if (text[index] == '}')
            {
                if (stopAtClosingBrace)
                {
                    index++;
                    break;
                }

                index++;
                continue;
            }

            var value = ReadVdfToken(text, ref index);
            obj.Entries.Add(new VdfEntry(key, value, null));
        }

        return obj;
    }

    private static void SkipWhitespaceAndComments(string text, ref int index)
    {
        while (index < text.Length)
        {
            var ch = text[index];
            if (char.IsWhiteSpace(ch))
            {
                index++;
                continue;
            }

            if (ch == '/' && index + 1 < text.Length && text[index + 1] == '/')
            {
                index += 2;
                while (index < text.Length && text[index] != '\n')
                {
                    index++;
                }

                continue;
            }

            break;
        }
    }

    private static string ReadVdfToken(string text, ref int index)
    {
        if (text[index] == '"')
        {
            index++;
            var sb = new StringBuilder();
            while (index < text.Length)
            {
                var ch = text[index++];
                if (ch == '"')
                {
                    break;
                }

                if (ch == '\\' && index < text.Length)
                {
                    var escaped = text[index++];
                    sb.Append(escaped switch
                    {
                        'n' => '\n',
                        'r' => '\r',
                        't' => '\t',
                        '"' => '"',
                        '\\' => '\\',
                        _ => escaped,
                    });
                }
                else
                {
                    sb.Append(ch);
                }
            }

            return sb.ToString();
        }

        var start = index;
        while (index < text.Length
            && !char.IsWhiteSpace(text[index])
            && text[index] is not '{' and not '}' and not '"')
        {
            index++;
        }

        return text[start..index];
    }

    private static string NormalizeWindowsPathSeparator(string path)
    {
        return TrimParsedLibraryPath(path.Replace('/', '\\'));
    }

    private static string TrimParsedLibraryPath(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return path;
        }

        if (LooksLikeWindowsPath(path))
        {
            var trimmed = path.TrimEnd('\\', '/');
            if (trimmed.Length == 2 && trimmed[1] == ':')
            {
                return trimmed + '\\';
            }

            return trimmed;
        }

        if (path == "/")
        {
            return path;
        }

        var trimmedUnix = path.TrimEnd('/');
        return string.IsNullOrEmpty(trimmedUnix) ? "/" : trimmedUnix;
    }

    private static bool LooksLikeWindowsPath(string path)
    {
        return (path.Length >= 2 && char.IsAsciiLetter(path[0]) && path[1] == ':')
            || path.StartsWith(@"\\", StringComparison.Ordinal);
    }

    private static bool IsAsciiDigits(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        foreach (var ch in value)
        {
            if (!char.IsAsciiDigit(ch))
            {
                return false;
            }
        }

        return true;
    }

    private static string GetCanonicalDirectoryKey(string path)
    {
        try
        {
            var fullPath = PathSupport.TrimTrailingSeparators(Path.GetFullPath(path));
            var ioPath = PathSupport.ToIoPath(fullPath);
            if (Directory.Exists(ioPath))
            {
                var info = new DirectoryInfo(ioPath);
                var resolved = info.ResolveLinkTarget(returnFinalTarget: true);
                if (resolved is not null)
                {
                    return PathSupport.TrimTrailingSeparators(Path.GetFullPath(resolved.FullName));
                }
            }

            return fullPath;
        }
        catch
        {
            return path;
        }
    }

    private static string? GetUserHomeDirectory()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(profile))
        {
            return profile;
        }

        var homeEnv = Environment.GetEnvironmentVariable("HOME");
        return string.IsNullOrWhiteSpace(homeEnv) ? null : homeEnv;
    }

    private static string? FindSubdirectoryCaseInsensitive(string parentDirectory, string childName)
    {
        var ioParent = PathSupport.ToIoPath(parentDirectory);
        if (!Directory.Exists(ioParent))
        {
            return null;
        }

        var direct = Path.Combine(parentDirectory, childName);
        if (Directory.Exists(PathSupport.ToIoPath(direct)))
        {
            return direct;
        }

        try
        {
            foreach (var candidate in Directory.EnumerateDirectories(ioParent))
            {
                var fileName = Path.GetFileName(candidate);
                if (string.Equals(fileName, childName, StringComparison.OrdinalIgnoreCase))
                {
                    return Path.Combine(parentDirectory, fileName);
                }
            }
        }
        catch
        {
            // Ignore unreadable directories during discovery.
        }

        return null;
    }

    private static string? FindFileCaseInsensitive(string directory, string fileName)
    {
        var ioDir = PathSupport.ToIoPath(directory);
        if (!Directory.Exists(ioDir))
        {
            return null;
        }

        var direct = Path.Combine(directory, fileName);
        if (File.Exists(PathSupport.ToIoPath(direct)))
        {
            return direct;
        }

        try
        {
            foreach (var candidate in Directory.EnumerateFiles(ioDir))
            {
                var name = Path.GetFileName(candidate);
                if (string.Equals(name, fileName, StringComparison.OrdinalIgnoreCase))
                {
                    return Path.Combine(directory, name);
                }
            }
        }
        catch
        {
            // Ignore unreadable directories during discovery.
        }

        return null;
    }

    private static string? TryReadWindowsSteamRegistryPath()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            const string subKey = @"Software\Valve\Steam";
            const string valueName = "SteamPath";
            const uint flags = RrfRtRegSz | RrfRtRegExpandSz;

            uint byteLength = 0;
            var queryStatus = RegGetValueW(
                HkeyCurrentUser,
                subKey,
                valueName,
                flags,
                out _,
                null,
                ref byteLength);
            if (queryStatus != 0 || byteLength == 0 || byteLength > 65536)
            {
                return null;
            }

            var charCount = checked((int)((byteLength + 1) / 2));
            var buffer = new char[charCount];
            var readStatus = RegGetValueW(
                HkeyCurrentUser,
                subKey,
                valueName,
                flags,
                out _,
                buffer,
                ref byteLength);
            if (readStatus != 0)
            {
                return null;
            }

            var terminator = Array.IndexOf(buffer, '\0');
            var value = terminator >= 0 ? new string(buffer, 0, terminator) : new string(buffer);
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }
        catch
        {
            return null;
        }
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int RegGetValueW(
        nint hkey,
        string? lpSubKey,
        string? lpValue,
        uint dwFlags,
        out uint pdwType,
        [Out] char[]? pvData,
        ref uint pcbData);
}
