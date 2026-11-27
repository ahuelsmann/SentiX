using System.Reflection;
using System.Xml.Linq;

namespace SentiX.Risk.Tests;

/// <summary>Guards the 0 % AI / zero-dependency non-negotiable of <c>SentiX.Risk</c> (AGENTS.md §3.1, specs/004 §1).</summary>
public sealed class PurityTests
{
    private static readonly Assembly RiskAssembly = typeof(HardRiskManager).Assembly;

    [Fact]
    public void RiskAssembly_ReferencesOnlyBaseClassLibrary()
    {
        var forbidden = new[] { "System.Net", "SentiX.", "OpenAI", "Anthropic", "Microsoft.", "Azure", "Npgsql" };

        var references = RiskAssembly.GetReferencedAssemblies().Select(a => a.Name!).ToArray();

        Assert.NotEmpty(references);
        Assert.All(references, name =>
        {
            Assert.True(name.StartsWith("System.", StringComparison.Ordinal) || name == "netstandard", $"Unexpected reference: {name}");
            Assert.DoesNotContain(forbidden, f => name.StartsWith(f, StringComparison.Ordinal));
        });
    }

    [Fact]
    public void RiskProject_HasNoProjectOrPackageReferences()
    {
        var project = XDocument.Load(Path.Combine(RiskSourceDirectory(), "SentiX.Risk.csproj"));

        Assert.Empty(project.Descendants("ProjectReference"));
        Assert.Empty(project.Descendants("PackageReference"));
        Assert.Equal("net10.0", project.Descendants("TargetFramework").Single().Value);
        Assert.Equal("enable", project.Descendants("Nullable").Single().Value);
        Assert.Equal("enable", project.Descendants("ImplicitUsings").Single().Value);
    }

    [Fact]
    public void RiskSources_ContainNoAmbientClockIoNetworkAiOrPlatformCode()
    {
        var forbidden = new[]
        {
            "DateTime.UtcNow", "DateTime.Now", "DateTime.Today", "DateTimeOffset.UtcNow", "DateTimeOffset.Now",
            "HttpClient", "System.Net", "System.IO", "File.", "Socket", "Random", "Stopwatch", "Environment.",
            "SentiX.DecisionSwarm", "SentiX.Execution", "OpenAI", "Anthropic", "SemanticKernel", "_WIN32", "Task.Delay",
        };

        var sources = Directory.GetFiles(RiskSourceDirectory(), "*.cs", SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                     && !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .ToArray();

        Assert.NotEmpty(sources);
        foreach (var source in sources)
        {
            var text = File.ReadAllText(source);
            foreach (var token in forbidden)
            {
                Assert.False(text.Contains(token, StringComparison.Ordinal), $"{Path.GetFileName(source)} contains forbidden token '{token}'.");
            }

            Assert.DoesNotContain("\r\n", text);
        }
    }

    private static string RiskSourceDirectory()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "src", "dotnet", "SentiX.Risk");
            if (File.Exists(Path.Combine(candidate, "SentiX.Risk.csproj")))
            {
                return candidate;
            }
        }

        throw new DirectoryNotFoundException("src/dotnet/SentiX.Risk not found above the test output directory.");
    }
}
