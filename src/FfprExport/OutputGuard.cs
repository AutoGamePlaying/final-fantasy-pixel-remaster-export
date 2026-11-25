using System.Globalization;
using System.Text;
using System.Text.Json;

namespace FfprExport;

internal static class OutputGuard
{
    public const string ToolName = "ffpr-export";
    public const string DepsEnvVar = "FFPR_DEPS";
    private const string DefaultDepsRelativePath = ".deps";
    public static readonly UTF8Encoding Utf8WithoutBom = new(false);

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
            throw new ArgumentException("--out 不能为空");
        }

        var fullOut = Path.GetFullPath(outputArgument);
        var fullGame = Path.GetFullPath(gameDirectory);

        if (IsSameOrDescendant(fullGame, fullOut) || IsSameOrDescendant(fullOut, fullGame))
        {
            throw new ArgumentException($"--out 不能与游戏目录重叠或互为父子目录：{outputArgument}");
        }

        // Prevent writing into protected read-only tool/dependency subpaths inside .deps / FFPR_DEPS.
        var (depsFull, depsDisplay, _) = ResolveDepsRoot();
        if (IsSameOrDescendant(depsFull, fullOut))
        {
            if (IsSamePath(depsFull, fullOut))
            {
                throw new ArgumentException($"--out 不能直接指向依赖根目录 {depsDisplay}：{outputArgument}");
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
                    throw new ArgumentException($"--out 不能指向受保护目录 {protectedDisplay} 或其内部：{outputArgument}");
                }
            }

            var ffprRoot = Path.GetFullPath(Path.Combine(depsFull, "ffpr"));
            if (IsSamePath(ffprRoot, fullOut))
            {
                throw new ArgumentException($"--out 不能直接指向受保护目录 {depsDisplay}/ffpr 本身：{outputArgument}");
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
                throw new IOException($"输出目录非空：{outputArgument}；如需清空并重新生成，请加 --force");
            }

            using var manifest = RequireOwnManifest(outputPath, outputArgument);
            EnsureManifestMatchesCommand(manifest.RootElement, outputArgument, expectedCommand);
            var fileCount = Directory.EnumerateFiles(outputPath, "*", SearchOption.AllDirectories).LongCount();
            Console.WriteLine($"即将清空输出目录：{outputArgument}；文件数：{fileCount}");
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
                Console.WriteLine($"已清空输出目录：{outputArgument}；删除文件数：{deletedFiles}");
            }
            else
            {
                resumed = true;
                Console.WriteLine($"续跑模式：复用已有输出目录 {outputArgument}（已导出文件将跳过）");
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
                Console.WriteLine($"保留其他子命令的输出目录：{Path.GetFileName(entry)}/");
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
        var message = $"{outputArgument} 不是 {ToolName} 的输出目录，拒绝清空或复用；请换一个空目录或手动清理后重试";
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
            throw new IOException(
                $"{outputArgument} 是 {ToolName} {actualCommand ?? "未知命令"} 的输出目录，不能由 {expectedCommand} 清空或复用");
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
                throw new IOException(
                    $"{outputArgument} 是 {actualGameId} 的输出目录，不能用于 {expectedGameId}（如需更换请另选目录）");
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
            throw new InvalidDataException($"目标路径越界：{relativeUnixPath}");
        }

        return target;
    }

    public static long WriteUtf8Atomic(string targetPath, string content)
    {
        var bytes = Utf8WithoutBom.GetBytes(content);
        WriteBytesAtomic(targetPath, bytes);
        return bytes.LongLength;
    }

    public static void WriteBytesAtomic(string targetPath, byte[] bytes)
    {
        var parent = Path.GetDirectoryName(targetPath);
        if (!string.IsNullOrEmpty(parent))
        {
            Directory.CreateDirectory(parent);
        }

        var tempPath = $"{targetPath}.{Environment.ProcessId}.{Environment.CurrentManagedThreadId}.tmp";
        File.WriteAllBytes(tempPath, bytes);
        File.Move(tempPath, targetPath, overwrite: true);
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
