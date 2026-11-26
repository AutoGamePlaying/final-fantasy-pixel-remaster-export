using System.Globalization;
using System.Text;
using System.Text.Json;

namespace FfprExport;

internal static class OutputGuard
{
    public const string ToolName = "ffpr-export";
    public const string DepsEnvVar = "FFPR_DEPS";
    private const string DefaultDepsRelativePath = ".deps";
    public static readonly UTF8Encoding Utf8WithoutBom = TextFileWriter.Utf8WithoutBom;

    public static (string FullPath, string DisplayPath, bool FromEnv) ResolveDepsRoot()
    {
        var envValue = Environment.GetEnvironmentVariable(DepsEnvVar);
        if (!string.IsNullOrWhiteSpace(envValue))
        {
            var trimmed = envValue.Trim();
            return (Path.GetFullPath(trimmed), trimmed, true);
        }

        return (Path.GetFullPath(DefaultDepsRelativePath), DefaultDepsRelativePath, false);
    }

    public static string GetDefaultInventoryDirectory(string gameId)
    {
        return ResolveDefaultDepsPath($"ffpr/{gameId}/inventory");
    }

    public static string GetDefaultExportDirectory(string gameId)
    {
        return ResolveDefaultDepsPath($"ffpr/{gameId}/export");
    }

    internal static string ResolveDefaultDepsPath(string relativeSubpath)
    {
        var (_, displayPath, _) = ResolveDepsRoot();
        var normalizedSubpath = relativeSubpath.Replace('/', Path.DirectorySeparatorChar);
        return Path.Combine(displayPath, normalizedSubpath).Replace('\\', '/');
    }

    public static string ValidateOutputPath(string outputArgument, string gameDirectory)
    {
        if (string.IsNullOrWhiteSpace(outputArgument))
        {
            throw new ArgumentException(L.Get("OutputGuard_OutEmpty"));
        }

        var fullOut = Path.GetFullPath(outputArgument);
        var fullGame = Path.GetFullPath(gameDirectory);

        if (IsSameOrDescendant(fullGame, fullOut) || IsSameOrDescendant(fullOut, fullGame))
        {
            throw new ArgumentException(L.Format("OutputGuard_OverlapGame", outputArgument));
        }

        // Prevent writing into protected read-only tool/dependency subpaths inside .deps / FFPR_DEPS.
        var (depsFull, depsDisplay, _) = ResolveDepsRoot();
        if (IsSameOrDescendant(depsFull, fullOut))
        {
            if (IsSamePath(depsFull, fullOut))
            {
                throw new ArgumentException(L.Format("OutputGuard_DepsRoot", depsDisplay, outputArgument));
            }

            string[] protectedSubpaths =
            [
                "dotnet",
                "nuget",
                "dotnet-home",
                "nuget-http",
                "steamapps",
                "ffpr-legacy",
            ];

            foreach (var subpath in protectedSubpaths)
            {
                var protectedFull = Path.GetFullPath(Path.Combine(depsFull, subpath));
                if (IsSameOrDescendant(protectedFull, fullOut))
                {
                    var protectedDisplay = Path.Combine(depsDisplay, subpath).Replace('\\', '/');
                    throw new ArgumentException(L.Format("OutputGuard_ProtectedDepsSubdir", protectedDisplay, outputArgument));
                }
            }

            var ffprRoot = Path.GetFullPath(Path.Combine(depsFull, "ffpr"));
            if (IsSamePath(ffprRoot, fullOut))
            {
                throw new ArgumentException(L.Format("OutputGuard_ProtectedDepsFfpr", depsDisplay, outputArgument));
            }
        }

        return fullOut;
    }

    public static void PrepareCleanOutputDirectory(
        string outputPath,
        string outputArgument,
        bool force,
        string expectedCommand)
    {
        if (Directory.Exists(outputPath) && Directory.EnumerateFileSystemEntries(outputPath).Any())
        {
            if (!force)
            {
                throw new IOException(L.Format("OutputGuard_DirNotEmpty", outputArgument));
            }

            using var manifest = RequireOwnManifest(outputPath, outputArgument);
            EnsureManifestMatchesCommand(manifest.RootElement, outputArgument, expectedCommand);
            var fileCount = Directory.EnumerateFiles(outputPath, "*", SearchOption.AllDirectories).LongCount();
            Console.WriteLine(L.Format("OutputGuard_ClearingDir", outputArgument, fileCount));
            DeleteOwnEntries(outputPath, expectedCommand);
        }

        Directory.CreateDirectory(outputPath);
    }

    public static bool PrepareResumableOutputDirectory(
        string outputPath,
        string outputArgument,
        bool force,
        string expectedCommand,
        string expectedGameId)
    {
        var resumed = false;
        if (Directory.Exists(outputPath) && Directory.EnumerateFileSystemEntries(outputPath).Any())
        {
            using var manifest = RequireOwnManifest(outputPath, outputArgument);
            EnsureManifestMatchesCommand(manifest.RootElement, outputArgument, expectedCommand);
            EnsureManifestMatchesGame(manifest.RootElement, outputArgument, expectedGameId);

            if (force)
            {
                var deletedFiles = DeleteOwnEntries(outputPath, expectedCommand);
                Console.WriteLine(L.Format("OutputGuard_ClearedDir", outputArgument, deletedFiles));
            }
            else
            {
                resumed = true;
                Console.WriteLine(L.Format("OutputGuard_ResumeMode", outputArgument));
            }
        }

        Directory.CreateDirectory(outputPath);
        return resumed;
    }

    private static long DeleteOwnEntries(string outputPath, string expectedCommand)
    {
        long deletedFiles = 0;
        foreach (var entry in Directory.EnumerateFileSystemEntries(outputPath))
        {
            if (Directory.Exists(entry) && IsOtherCommandSubdirectory(entry, expectedCommand))
            {
                Console.WriteLine(L.Format("OutputGuard_PreserveOtherCommand", Path.GetFileName(entry)));
                continue;
            }

            if (Directory.Exists(entry))
            {
                deletedFiles += Directory.EnumerateFiles(entry, "*", SearchOption.AllDirectories).LongCount();
                Directory.Delete(entry, recursive: true);
            }
            else
            {
                deletedFiles++;
                File.Delete(entry);
            }
        }

        return deletedFiles;
    }

    private static JsonDocument RequireOwnManifest(string outputPath, string outputArgument)
    {
        var message = L.Format("OutputGuard_NotOwnDir", outputArgument, ToolName);
        var manifestPath = Path.Combine(outputPath, "manifest.json");
        if (!File.Exists(manifestPath))
        {
            throw new IOException(message);
        }

        try
        {
            var doc = JsonDocument.Parse(File.ReadAllText(manifestPath, Encoding.UTF8));
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("tool", out var toolEl)
                || !string.Equals(toolEl.GetString(), ToolName, StringComparison.Ordinal))
            {
                doc.Dispose();
                throw new IOException(message);
            }

            return doc;
        }
        catch (JsonException ex)
        {
            throw new IOException(message, ex);
        }
    }

    private static void EnsureManifestMatchesCommand(
        JsonElement root,
        string outputArgument,
        string expectedCommand)
    {
        var actualCommand = root.TryGetProperty("command", out var cmdEl) ? cmdEl.GetString() : null;
        if (!string.Equals(actualCommand, expectedCommand, StringComparison.Ordinal))
        {
            throw new IOException(L.Format(
                "OutputGuard_WrongCommand",
                outputArgument,
                ToolName,
                actualCommand ?? L.Get("BuildDb_UnknownCommand"),
                expectedCommand));
        }
    }

    private static void EnsureManifestMatchesGame(
        JsonElement root,
        string outputArgument,
        string expectedGameId)
    {
        if (root.TryGetProperty("game", out var gameEl)
            && gameEl.ValueKind == JsonValueKind.Object
            && gameEl.TryGetProperty("id", out var idEl))
        {
            var actualGameId = idEl.GetString();
            if (!string.IsNullOrEmpty(actualGameId)
                && !string.Equals(actualGameId, expectedGameId, StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException(L.Format("OutputGuard_WrongGame", outputArgument, actualGameId, expectedGameId));
            }
        }
    }

    private static bool IsOtherCommandSubdirectory(string directoryPath, string expectedCommand)
    {
        var manifestPath = Path.Combine(directoryPath, "manifest.json");
        if (!File.Exists(manifestPath))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath, Encoding.UTF8));
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            var isOwnTool = doc.RootElement.TryGetProperty("tool", out var toolEl)
                && string.Equals(toolEl.GetString(), ToolName, StringComparison.Ordinal);
            var command = doc.RootElement.TryGetProperty("command", out var cmdEl)
                ? cmdEl.GetString()
                : null;
            return isOwnTool && !string.Equals(command, expectedCommand, StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    public static string BuildSafeTargetPath(string outputRoot, string relativeUnixPath)
    {
        var fullRoot = Path.GetFullPath(outputRoot);
        var target = Path.GetFullPath(Path.Combine(fullRoot, relativeUnixPath.Replace('/', Path.DirectorySeparatorChar)));
        if (!IsSameOrDescendant(fullRoot, target) || IsSamePath(fullRoot, target))
        {
            throw new InvalidDataException(L.Format("OutputGuard_PathTraversal", relativeUnixPath));
        }

        return target;
    }

    public static long WriteUtf8Atomic(string targetPath, string content)
    {
        return TextFileWriter.WriteAllTextAtomic(targetPath, content);
    }

    public static void WriteBytesAtomic(string targetPath, byte[] bytes)
    {
        TextFileWriter.WriteBytesAtomic(targetPath, bytes);
    }

    public static string FormatUtc(DateTimeOffset dto)
    {
        return dto.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
    }

    public static string TruncateError(string message)
    {
        return message.Length <= 400 ? message : message[..400];
    }

    private static bool IsSamePath(string a, string b)
    {
        var rel = Path.GetRelativePath(a, b);
        return rel == ".";
    }

    private static bool IsSameOrDescendant(string parentPath, string candidatePath)
    {
        var rel = Path.GetRelativePath(parentPath, candidatePath);
        return rel == "."
            || (rel != ".."
                && !rel.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !rel.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal)
                && !Path.IsPathRooted(rel));
    }
}
