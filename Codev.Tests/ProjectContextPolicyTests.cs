using Codev;

namespace Codev.Tests;

public sealed class ProjectContextPolicyTests
{
    [Theory]
    [InlineData(CloudModelProviders.OpenAI, true, false, null, false, true)]
    [InlineData(CloudModelProviders.OpenAI, false, false, null, false, false)]
    [InlineData(CloudModelProviders.OpenAI, true, false, @"C:\work\repo", false, true)]
    [InlineData(CloudModelProviders.Anthropic, true, false, null, false, false)]
    [InlineData("ollama", false, true, @"C:\work\repo", true, true)]
    [InlineData("ollama", false, false, @"C:\work\repo", true, false)]
    [InlineData("ollama", false, true, @"C:\work\repo", false, false)]
    [InlineData("ollama", false, true, null, false, false)]
    public void Code_task_availability_depends_on_provider_support_and_local_trust(string provider,
        bool connected, bool loopback, string? projectPath, bool trusted, bool expected) =>
        Assert.Equal(expected, ProjectContextPolicy.CanRunCodeTask(provider, connected, loopback, projectPath, trusted));

    [Fact]
    public void Untrusted_folder_auto_context_is_omitted_from_the_queued_turn()
    {
        Assert.Null(ProjectContextPolicy.GetProjectPathForQueuedTurn(@"C:\work\repo", false, false));
        Assert.False(ProjectContextPolicy.ShouldInclude(@"C:\work\repo", "ollama", false, false, false));
    }

    [Fact]
    public void Explicit_files_remain_available_in_an_untrusted_local_folder()
    {
        Assert.Equal(@"C:\work\repo", ProjectContextPolicy.GetProjectPathForQueuedTurn(@"C:\work\repo", true, false));
        Assert.True(ProjectContextPolicy.ShouldInclude(@"C:\work\repo", "ollama", false, true, false));
    }

    [Fact]
    public void Hosted_context_needs_its_own_opt_in_even_when_folder_is_trusted()
    {
        Assert.False(ProjectContextPolicy.ShouldInclude(@"C:\work\repo", CloudModelProviders.Anthropic, false, false, true));
        Assert.True(ProjectContextPolicy.ShouldInclude(@"C:\work\repo", CloudModelProviders.Anthropic, true, false, true));
    }

    [Fact]
    public void No_project_path_never_adds_project_context()
    {
        Assert.False(ProjectContextPolicy.ShouldInclude(null, "ollama", false, true, true));
    }
}
