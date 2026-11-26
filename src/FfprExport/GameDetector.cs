using System.Text;
using AssetsTools.NET;

namespace FfprExport;

internal sealed record GameSpec(
    string Id,
    string Title,
    int AppId,
    string ExeName,
    string DataDirName);

internal sealed record DetectedGame(
    GameSpec Spec,
    string GameDirectory,
    string DataDirectory,
    string AddressablesDirectory,
    string CatalogPath,
    string UnityVersion,
    string? SteamBuildId,
    string? AppManifestPath,
    string? GameAssemblyPath,
    string? GlobalMetadataPath);

internal static class GameDetector
{
    internal static readonly IReadOnlyList<GameSpec> KnownGames =
    [
        new("ff1", "FINAL FANTASY", 1173770, "FINAL FANTASY.exe", "FINAL FANTASY_Data"),
        new("ff2", "FINAL FANTASY II", 1173780, "FINAL FANTASY II.exe", "FINAL FANTASY II_Data"),
        new("ff3", "FINAL FANTASY III", 1173790, "FINAL FANTASY III.exe", "FINAL FANTASY III_Data"),
        new("ff4", "FINAL FANTASY IV", 1173800, "FINAL FANTASY IV.exe", "FINAL FANTASY IV_Data"),
        new("ff5", "FINAL FANTASY V", 1173810, "FINAL FANTASY V.exe", "FINAL FANTASY V_Data"),
        new("ff6", "FINAL FANTASY VI", 1173820, "FINAL FANTASY VI.exe", "FINAL FANTASY VI_Data"),
    ];

    public static DetectedGame Detect(string gameDirectoryArgument)
    {
        if (string.IsNullOrWhiteSpace(gameDirectoryArgument))
        {
            throw new ArgumentException(L.Get("GameDetector_MissingGameArg"));
        }

        var fullGameDir = Path.GetFullPath(gameDirectoryArgument);
        if (!Directory.Exists(fullGameDir))
        {
            throw new DirectoryNotFoundException(L.Format("GameDetector_DirNotFound", gameDirectoryArgument));
        }

        var (spec, dataDir) = IdentifyGame(fullGameDir);
        var aaDir = Path.Combine(dataDir, "StreamingAssets", "aa");
        var catalogPath = Path.Combine(aaDir, "catalog.json");
        var unityVersion = DetectUnityVersion(dataDir, aaDir);
        var (steamBuildId, appManifestPath) = TryReadSteamBuildId(fullGameDir, spec.AppId);

        var gameAssemblyPath = FindFileCaseInsensitive(fullGameDir, "GameAssembly.dll");
        var globalMetadataPath = Path.Combine(dataDir, "il2cpp_data", "Metadata", "global-metadata.dat");
        if (!File.Exists(globalMetadataPath))
        {
            globalMetadataPath = null;
        }

        return new DetectedGame(
            spec,
            fullGameDir,
            dataDir,
            aaDir,
            catalogPath,
            unityVersion,
            steamBuildId,
            appManifestPath,
            gameAssemblyPath,
            globalMetadataPath);
    }

    private static (GameSpec Spec, string DataDir) IdentifyGame(string fullGameDir)
    {
        var dirs = Directory.GetDirectories(fullGameDir)
            .ToDictionary(path => Path.GetFileName(path)!, path => path, StringComparer.OrdinalIgnoreCase);
        var files = Directory.GetFiles(fullGameDir)
            .ToDictionary(path => Path.GetFileName(path)!, path => path, StringComparer.OrdinalIgnoreCase);

        // Prefer matching *_Data directory first, then fall back to the game executable.
        foreach (var spec in KnownGames)
        {
            if (dirs.TryGetValue(spec.DataDirName, out var matchedDataDir))
            {
                return (spec, matchedDataDir);
            }
        }

        foreach (var spec in KnownGames)
        {
            if (files.ContainsKey(spec.ExeName))
            {
                var expectedDataDir = Path.Combine(fullGameDir, spec.DataDirName);
                if (!Directory.Exists(expectedDataDir))
                {
                    throw new DirectoryNotFoundException(L.Format("GameDetector_MissingDataDir", spec.Title, spec.ExeName, expectedDataDir));
                }

                return (spec, expectedDataDir);
            }
        }

        throw new InvalidDataException(L.Format("GameDetector_UnrecognizedDir", fullGameDir));
    }

    internal static string DetectUnityVersion(string dataDir, string aaDir)
    {
        string[] candidateAssets =
        [
            "globalgamemanagers",
            "globalgamemanagers.assets",
            "resources.assets",
            "sharedassets0.assets",
            "level0",
        ];

        foreach (var candidate in candidateAssets)
        {
            var path = Path.Combine(dataDir, candidate);
            if (!File.Exists(path))
            {
                continue;
            }

            if (TryReadAssetsFileUnityVersion(path, out var version))
            {
                return version;
            }
        }

        // Fall back to any level* or sharedassets*.assets in *_Data.
        foreach (var path in Directory.EnumerateFiles(dataDir).OrderBy(p => p, StringComparer.Ordinal))
        {
            var name = Path.GetFileName(path);
            if ((!name.StartsWith("level", StringComparison.OrdinalIgnoreCase)
                 && !name.StartsWith("sharedassets", StringComparison.OrdinalIgnoreCase))
                || name.EndsWith(".resS", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith(".resource", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (TryReadAssetsFileUnityVersion(path, out var version))
            {
                return version;
            }
        }

        // Fall back to the first Addressables bundle header.
        var standaloneDir = Path.Combine(aaDir, "StandaloneWindows64");
        if (Directory.Exists(standaloneDir))
        {
            foreach (var bundlePath in Directory.EnumerateFiles(standaloneDir, "*.bundle").OrderBy(p => p, StringComparer.Ordinal))
            {
                if (TryReadBundleUnityVersion(bundlePath, out var version))
                {
                    return version;
                }
            }
        }

        throw new InvalidDataException(L.Format("GameDetector_UnityVersionNotFound", dataDir));
    }

    private static bool TryReadAssetsFileUnityVersion(string filePath, out string version)
    {
        version = string.Empty;
        try
        {
            using var stream = File.OpenRead(filePath);
            using var reader = new AssetsFileReader(stream);
            var assetsFile = new AssetsFile();
            assetsFile.Read(reader);
            var candidate = assetsFile.Metadata?.UnityVersion;
            if (!string.IsNullOrWhiteSpace(candidate) && candidate != "0.0.0")
            {
                version = candidate.Trim();
                return true;
            }
        }
        catch
        {
            // Ignore unreadable candidates and try the next file.
        }

        return false;
    }

    private static bool TryReadBundleUnityVersion(string bundlePath, out string version)
    {
        version = string.Empty;
        try
        {
            using var stream = File.OpenRead(bundlePath);
            using var reader = new AssetsFileReader(stream);
            var bundle = new AssetBundleFile();
            bundle.Read(reader);
            var candidate = bundle.Header?.EngineVersion;
            if (!string.IsNullOrWhiteSpace(candidate) && candidate != "0.0.0")
            {
                version = candidate.Trim();
                return true;
            }
        }
        catch
        {
            // Ignore unreadable candidates and try the next file.
        }

        return false;
    }

    internal static (string? BuildId, string? ManifestPath) TryReadSteamBuildId(string fullGameDir, int appId)
    {
        var commonDir = Directory.GetParent(fullGameDir);
        var steamAppsDir = commonDir?.Parent;
        if (steamAppsDir is null || !Directory.Exists(steamAppsDir.FullName))
        {
            return (null, null);
        }

        var manifestFileName = $"appmanifest_{appId}.acf";
        var manifestPath = FindFileCaseInsensitive(steamAppsDir.FullName, manifestFileName);
        if (manifestPath is null)
        {
            return (null, null);
        }

        var content = File.ReadAllText(manifestPath, Encoding.UTF8);
        var buildId = ParseSteamBuildId(content);
        return (buildId, manifestPath);
    }

    internal static string? ParseSteamBuildId(string acfContent)
    {
        // Minimal Valve KeyValues (VDF/ACF) tokenizer: locate top-level AppState -> buildid.
        var tokens = TokenizeVdf(acfContent);
        var depth = 0;
        var inAppState = false;
        for (var i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];
            if (token == "{")
            {
                depth++;
                continue;
            }

            if (token == "}")
            {
                if (depth == 1)
                {
                    inAppState = false;
                }

                depth = Math.Max(0, depth - 1);
                continue;
            }

            if (depth == 0
                && string.Equals(token, "AppState", StringComparison.OrdinalIgnoreCase)
                && i + 1 < tokens.Count
                && tokens[i + 1] == "{")
            {
                inAppState = true;
                continue;
            }

            if (inAppState && depth == 1 && i + 1 < tokens.Count)
            {
                var next = tokens[i + 1];
                if (next == "{")
                {
                    continue;
                }

                if (string.Equals(token, "buildid", StringComparison.OrdinalIgnoreCase))
                {
                    return next;
                }

                i++;
            }
        }

        return null;
    }

    private static List<string> TokenizeVdf(string text)
    {
        var tokens = new List<string>();
        var i = 0;
        while (i < text.Length)
        {
            var ch = text[i];
            if (char.IsWhiteSpace(ch))
            {
                i++;
                continue;
            }

            if (ch == '/' && i + 1 < text.Length && text[i + 1] == '/')
            {
                while (i < text.Length && text[i] != '\n')
                {
                    i++;
                }

                continue;
            }

            if (ch is '{' or '}')
            {
                tokens.Add(ch.ToString());
                i++;
                continue;
            }

            if (ch == '"')
            {
                i++;
                var sb = new StringBuilder();
                while (i < text.Length && text[i] != '"')
                {
                    if (text[i] == '\\' && i + 1 < text.Length)
                    {
                        i++;
                        sb.Append(text[i] switch
                        {
                            'n' => '\n',
                            't' => '\t',
                            'r' => '\r',
                            '\\' => '\\',
                            '"' => '"',
                            var other => other,
                        });
                    }
                    else
                    {
                        sb.Append(text[i]);
                    }

                    i++;
                }

                if (i < text.Length && text[i] == '"')
                {
                    i++;
                }

                tokens.Add(sb.ToString());
                continue;
            }

            var start = i;
            while (i < text.Length && !char.IsWhiteSpace(text[i]) && text[i] is not '{' and not '}' and not '"')
            {
                i++;
            }

            tokens.Add(text[start..i]);
        }

        return tokens;
    }

    private static string? FindFileCaseInsensitive(string directory, string fileName)
    {
        var direct = Path.Combine(directory, fileName);
        if (File.Exists(direct))
        {
            return direct;
        }

        foreach (var candidate in Directory.EnumerateFiles(directory))
        {
            if (string.Equals(Path.GetFileName(candidate), fileName, StringComparison.OrdinalIgnoreCase))
            {
                return candidate;
            }
        }

        return null;
    }
}
