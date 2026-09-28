using Codev;

namespace Codev.Tests;

public sealed class ProjectContextPolicyTests
{
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
