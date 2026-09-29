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
    [InlineData("ollama", false, true, null, false, true)]
    public void Code_task_availability_depends_on_provider_support_and_local_trust(string provider,
        bool connected, bool loopback, string? projectPath, bool trusted, bool expected) =>
        Assert.Equal(expected, ProjectContextPolicy.CanRunCodeTask(provider, connected, loopback, projectPath, trusted));

    [Fact]
    public void Loopback_ollama_can_start_without_a_project_because_toggle_creates_private_workspace() =>
        Assert.True(ProjectContextPolicy.CanRunCodeTask("ollama", false, true, null, false));

    [Theory]
    [InlineData(CloudModelProviders.OpenAI, true, false, false)]
    [InlineData(CloudModelProviders.OpenAI, true, true, true)]
    [InlineData(CloudModelProviders.OpenAI, false, true, false)]
    [InlineData("ollama", false, false, false)]
    public void Hosted_code_task_send_requires_its_separate_acknowledgement(string provider,
        bool connected, bool approved, bool expected) =>
        Assert.Equal(expected, ProjectContextPolicy.CanSendCodeTask(provider, connected, approved, false, null, false));

    [Theory]
    [InlineData(true, false, true, true)]
    [InlineData(true, false, false, false)]
    [InlineData(true, true, false, true)]
    [InlineData(false, true, true, false)]
    public void Openai_code_task_requires_task_consent_and_separate_sharing_for_attached_workspaces(
        bool taskConsent, bool workspaceSharing, bool privateWorkspace, bool expected) =>
        Assert.Equal(expected, ProjectContextPolicy.CanUseOpenAiCodeTaskWorkspace(taskConsent, workspaceSharing, privateWorkspace));

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
