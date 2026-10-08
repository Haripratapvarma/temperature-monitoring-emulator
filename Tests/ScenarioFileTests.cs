using System.Text.Json;
using TempLab.Protocol;

namespace TempLab.Tests;

/// <summary>The scenario files shipped in Docs/scenarios must stay loadable and valid.</summary>
public class ScenarioFileTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "TempLab.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repository root not found");
    }

    [Fact]
    public void Shipped_scenarios_parse_and_validate()
    {
        var files = Directory.GetFiles(Path.Combine(RepoRoot(), "Docs", "scenarios"), "*.json");
        Assert.True(files.Length >= 6);
        foreach (var f in files)
        {
            var sc = JsonSerializer.Deserialize<FaultScenario>(File.ReadAllText(f), ProtocolJson.Options);
            Assert.NotNull(sc);
            Assert.True(sc!.Validate() is null, $"{Path.GetFileName(f)}: {sc.Validate()}");
            Assert.NotEmpty(sc.Faults);
        }
    }
}
