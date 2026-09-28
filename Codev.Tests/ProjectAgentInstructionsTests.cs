using System.IO;

namespace Codev.Tests;

public sealed class ProjectAgentInstructionsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Codev-agent-instructions", Guid.NewGuid().ToString("N"));

    public ProjectAgentInstructionsTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task Loads_optional_root_guidance_with_a_clear_label()
    {
        File.WriteAllText(Path.Combine(_root, "AGENTS.md"), "\nUse xUnit and keep handlers small.\n");

        var result = await ProjectAgentInstructions.LoadAsync(new WorkspaceFileService(_root));

        Assert.Contains("Applicable project guidance", result);
        Assert.Contains("--- AGENTS.md (project guidance; user-provided) ---", result);
        Assert.EndsWith("Use xUnit and keep handlers small.", result);
    }

    [Fact]
    public async Task Returns_empty_when_file_is_absent_or_excluded()
    {
        Assert.Equal("", await ProjectAgentInstructions.LoadAsync(new WorkspaceFileService(_root)));
        File.WriteAllText(Path.Combine(_root, "AGENTS.md"), "Do not include me.");

        Assert.Equal("", await ProjectAgentInstructions.LoadAsync(new WorkspaceFileService(_root, ["AGENTS.md"])));
    }

    [Fact]
    public async Task Caps_large_guidance_and_discloses_truncation()
    {
        File.WriteAllText(Path.Combine(_root, "AGENTS.md"), new string('a', ProjectAgentInstructions.MaxCharacters + 100));

        var result = await ProjectAgentInstructions.LoadAsync(new WorkspaceFileService(_root));

        Assert.Contains("[Project guidance was truncated to fit the context budget.]", result);
        Assert.True(result.Length <= ProjectAgentInstructions.MaxCharacters);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
    }
}
