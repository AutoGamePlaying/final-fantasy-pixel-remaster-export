using System.Text.RegularExpressions;
using Xunit;

namespace FfprExport.Tests;

public sealed class SourceSafetyTests
{
    private static IReadOnlyList<(string RelativePath, string Content)> ReadRepoFiles()
    {
        var root = TestHarness.FindRepoRoot();
        var targets = new List<string>();

        var srcRoot = Path.Combine(root, "src");
        foreach (var path in Directory.EnumerateFiles(srcRoot, "*", SearchOption.AllDirectories))
        {
            var relFromSrc = Path.GetRelativePath(srcRoot, path).Replace('\\', '/');
            var segments = relFromSrc.Split('/');
            if (segments.Any(s => s is "bin" or "obj"))
            {
                continue;
            }

            if (Path.GetExtension(path) is ".cs" or ".csproj" or ".manifest")
            {
                targets.Add(path);
            }
        }

        foreach (var path in Directory.EnumerateFiles(Path.Combine(root, "scripts"), "*.sh", SearchOption.TopDirectoryOnly))
        {
            targets.Add(path);
        }

        var docsDir = Path.Combine(root, "docs");
        if (Directory.Exists(docsDir))
        {
            targets.AddRange(Directory.EnumerateFiles(docsDir, "*.md", SearchOption.AllDirectories));
        }

        var readme = Path.Combine(root, "README.md");
        if (File.Exists(readme))
        {
            targets.Add(readme);
        }

        Assert.True(targets.Count >= 10, "应扫描到全部源码、脚本与文档文件");

        return targets
            .OrderBy(p => p, StringComparer.Ordinal)
            .Select(p => (Path.GetRelativePath(root, p).Replace('\\', '/'), File.ReadAllText(p)))
            .ToArray();
    }

    [Fact]
    public void SourcesScriptsAndDocs_ContainNoMachineSpecificAbsolutePathsOrNetworkClients()
    {
        var linuxRootRegex = new Regex(@"/root/", RegexOptions.CultureInvariant);

        foreach (var (relativePath, content) in ReadRepoFiles())
        {
            Assert.DoesNotMatch(linuxRootRegex, content);
            Assert.False(content.Contains("HttpClient", StringComparison.Ordinal), $"{relativePath} 不得包含 HttpClient");
            Assert.False(content.Contains("WebClient", StringComparison.Ordinal), $"{relativePath} 不得包含 WebClient");
        }
    }

    [Fact]
    public void ProjectFile_PinsAssetsToolsVersionsAndStrictSettings()
    {
        var root = TestHarness.FindRepoRoot();
        var csproj = File.ReadAllText(Path.Combine(root, "src", "FfprExport", "FfprExport.csproj"));

        Assert.Contains("<AssemblyName>ffpr-export</AssemblyName>", csproj, StringComparison.Ordinal);
        Assert.Contains("<Nullable>enable</Nullable>", csproj, StringComparison.Ordinal);
        Assert.Contains("<TreatWarningsAsErrors>true</TreatWarningsAsErrors>", csproj, StringComparison.Ordinal);
        Assert.Contains(@"<PackageReference Include=""AssetsTools.NET"" Version=""3.0.5"" />", csproj, StringComparison.Ordinal);
        Assert.Contains(@"<PackageReference Include=""AssetsTools.NET.Cpp2IL"" Version=""3.0.4"" />", csproj, StringComparison.Ordinal);
    }

    [Fact]
    public void Scripts_DisableMsBuildNodeReuseAndUseLocalDeps()
    {
        var root = TestHarness.FindRepoRoot();
        var runScript = File.ReadAllText(Path.Combine(root, "scripts", "run.sh"));
        var checkScript = File.ReadAllText(Path.Combine(root, "scripts", "check.sh"));

        foreach (var script in new[] { runScript, checkScript })
        {
            Assert.Contains("DOTNET_ROOT", script, StringComparison.Ordinal);
            Assert.Contains(".deps/dotnet", script, StringComparison.Ordinal);
            Assert.Contains(".deps/nuget", script, StringComparison.Ordinal);
            Assert.Contains("MSBUILDDISABLENODEREUSE=1", script, StringComparison.Ordinal);
            Assert.Contains("DOTNET_CLI_USE_MSBUILD_SERVER=0", script, StringComparison.Ordinal);
            Assert.Contains("UseSharedCompilation=false", script, StringComparison.Ordinal);
            Assert.DoesNotContain("~/.dotnet", script, StringComparison.Ordinal);
        }

        Assert.Contains("format FfprExport.sln --verify-no-changes", checkScript, StringComparison.Ordinal);
        Assert.Contains("build FfprExport.sln -c Release", checkScript, StringComparison.Ordinal);
        Assert.Contains("test FfprExport.sln", checkScript, StringComparison.Ordinal);
    }

    [Fact]
    public void AllSourceFiles_UseTextFileWriterInsteadOfDirectTextWrites()
    {
        var root = TestHarness.FindRepoRoot();
        var srcRoot = Path.Combine(root, "src");
        var csFiles = Directory.EnumerateFiles(srcRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path =>
            {
                var rel = Path.GetRelativePath(srcRoot, path).Replace('\\', '/');
                return !rel.Split('/').Any(s => s is "bin" or "obj");
            })
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToArray();

        Assert.True(csFiles.Length >= 25, $"应扫描到 src/ 下全部 C# 源码文件，实际仅 {csFiles.Length} 个");

        const string helperRelativePath = "src/FfprExport/TextFileWriter.cs";
        var helperFound = false;

        (string Name, Regex Pattern)[] forbiddenPatterns =
        [
            ("File.WriteAllText/Async", new Regex(@"\bFile\.WriteAllText(?:Async)?\s*\(", RegexOptions.CultureInvariant)),
            ("File.WriteAllLines/Async", new Regex(@"\bFile\.WriteAllLines(?:Async)?\s*\(", RegexOptions.CultureInvariant)),
            ("File.AppendAllText/Async", new Regex(@"\bFile\.AppendAllText(?:Async)?\s*\(", RegexOptions.CultureInvariant)),
            ("File.AppendAllLines/Async", new Regex(@"\bFile\.AppendAllLines(?:Async)?\s*\(", RegexOptions.CultureInvariant)),
            ("File.CreateText", new Regex(@"\bFile\.CreateText\s*\(", RegexOptions.CultureInvariant)),
            ("File.AppendText", new Regex(@"\bFile\.AppendText\s*\(", RegexOptions.CultureInvariant)),
            ("new StreamWriter", new Regex(@"\bnew\s+StreamWriter\s*\(", RegexOptions.CultureInvariant)),
            ("new JsonTextWriter", new Regex(@"\bnew\s+JsonTextWriter\s*\(", RegexOptions.CultureInvariant)),
            ("new Utf8JsonWriter", new Regex(@"\bnew\s+Utf8JsonWriter\s*\(", RegexOptions.CultureInvariant)),
            ("WriteIndented = true", new Regex(@"\bWriteIndented\s*=\s*true\b", RegexOptions.CultureInvariant)),
            ("Indented = true", new Regex(@"\bIndented\s*=\s*true\b", RegexOptions.CultureInvariant)),
        ];

        var violations = new List<string>();
        foreach (var path in csFiles)
        {
            var rel = Path.GetRelativePath(root, path).Replace('\\', '/');
            var content = File.ReadAllText(path);

            if (string.Equals(rel, helperRelativePath, StringComparison.Ordinal))
            {
                helperFound = true;
                Assert.Contains("public const string LineEnding = \"\\n\";", content, StringComparison.Ordinal);
                Assert.Contains("encoderShouldEmitUTF8Identifier: false", content, StringComparison.Ordinal);
                continue;
            }

            foreach (var (name, pattern) in forbiddenPatterns)
            {
                foreach (Match match in pattern.Matches(content))
                {
                    var lineNumber = 1 + content[..match.Index].Count(c => c == '\n');
                    violations.Add($"{rel}:{lineNumber} 禁止直接调用 {name}（请统一通过 TextFileWriter）");
                }
            }
        }

        Assert.True(helperFound, $"未找到公共文本写入辅助类：{helperRelativePath}");
        Assert.True(
            violations.Count == 0,
            "检测到绕过 TextFileWriter 的直接文本/JSON 写入调用：\n" + string.Join("\n", violations));

        // Ensure GUI links the shared TextFileWriter without duplicating it.
        var guiCsproj = File.ReadAllText(Path.Combine(root, "src", "FfprExport.Gui", "FfprExport.Gui.csproj"));
        Assert.Contains(@"<Compile Include=""..\FfprExport\TextFileWriter.cs""", guiCsproj, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(root, "src", "FfprExport.Gui", "TextFileWriter.cs")));
    }
}
