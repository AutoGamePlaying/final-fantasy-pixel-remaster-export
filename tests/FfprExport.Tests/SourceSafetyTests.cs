using System.Text.RegularExpressions;
using Xunit;

namespace FfprExport.Tests;

public sealed class SourceSafetyTests
{
    private static IReadOnlyList<(string RelativePath, string Content)> ReadRepoFiles()
    {
        var root = TestHarness.FindRepoRoot();
        var targets = new List<string>();

        foreach (var path in Directory.EnumerateFiles(Path.Combine(root, "src", "FfprExport"), "*", SearchOption.TopDirectoryOnly))
        {
            if (Path.GetExtension(path) is ".cs" or ".csproj")
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
}
