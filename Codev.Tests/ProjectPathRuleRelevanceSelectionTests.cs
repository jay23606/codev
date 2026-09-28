using System.Text.Json;

namespace Codev.Tests;

public sealed class ProjectPathRuleRelevanceSelectionTests
{
    [Fact]
    public void Builds_a_bounded_metadata_only_input()
    {
        var task = new string('t', ProjectPathRuleRelevanceSelection.MaxTaskCharacters + 100);
        var rules = new[] { new ProjectPathRuleCandidate("architecture", "Use existing services.") };

        var input = ProjectPathRuleRelevanceSelection.BuildInput(task, ["src/app.cs"], rules);
        using var document = JsonDocument.Parse(input);

        Assert.Equal(ProjectPathRuleRelevanceSelection.MaxTaskCharacters, document.RootElement.GetProperty("task").GetString()!.Length);
        Assert.Equal("src/app.cs", document.RootElement.GetProperty("includedFiles")[0].GetString());
        Assert.Equal("architecture", document.RootElement.GetProperty("rules")[0].GetProperty("name").GetString());
        Assert.DoesNotContain("rule body", input);
    }

    [Fact]
    public void Accepts_only_allowlisted_rule_names_from_json_or_a_fenced_json_response()
    {
        var candidates = new[] { new ProjectPathRuleCandidate("javascript", "JS guidance"), new ProjectPathRuleCandidate("tests", "Test guidance") };

        var selected = ProjectPathRuleRelevanceSelection.ParseResponse(
            "Selected rules: ```json\n{\"rules\":[\"javascript\",\"unknown\",\"javascript\",\"tests\"]}\n```", candidates);

        Assert.Equal(["javascript", "tests"], selected);
    }

    [Fact]
    public void Ignores_malformed_or_oversized_selector_responses()
    {
        var candidates = new[] { new ProjectPathRuleCandidate("javascript", "JS guidance") };

        Assert.Empty(ProjectPathRuleRelevanceSelection.ParseResponse("not json", candidates));
        Assert.Empty(ProjectPathRuleRelevanceSelection.ParseResponse(new string('x', ProjectPathRuleRelevanceSelection.MaxResponseCharacters + 1), candidates));
    }
}
