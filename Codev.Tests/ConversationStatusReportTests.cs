using Codev;

namespace Codev.Tests;

public sealed class ConversationStatusReportTests
{
    [Fact]
    public void Local_status_reports_model_context_project_mode_and_queue_without_network_details()
    {
        var conversation = new Conversation
        {
            Model = "qwen3-coder:30b",
            NumCtx = 65536,
            Temperature = 0.2,
            IsPlanMode = true,
            ProjectPath = @"C:\work\app",
            ContextFiles = ["src/app.cs"]
        };

        var report = ConversationStatusReport.Build(conversation, false, 2, false, false);

        Assert.Contains("Model: Ollama (local) · qwen3-coder:30b", report);
        Assert.Contains("Context window: 65,536 tokens", report);
        Assert.Contains("Temperature: 0.2", report);
        Assert.Contains("Mode: Plan", report);
        Assert.Contains("Project: app", report);
        Assert.DoesNotContain(@"C:\work\app", report, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Project context: 1 selected file(s)", report);
        Assert.Contains("Folder trust: untrusted", report);
        Assert.Contains("Queue: 2 queued turn(s)", report);
        Assert.Contains("Hosted requests: not in use", report);
        Assert.DoesNotContain("api-key", report, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(CloudModelProviders.OpenAI, "OpenAI API")]
    [InlineData(CloudModelProviders.Anthropic, "Anthropic API")]
    public void Hosted_status_reports_provider_consent_and_project_context_scope(string provider, string displayName)
    {
        var conversation = new Conversation
        {
            Provider = provider,
            Model = "hosted-model",
            ProjectPath = @"C:\work\private",
            IncludeProjectContextForHosted = false
        };

        var report = ConversationStatusReport.Build(conversation, true, 0, false, true);

        Assert.Contains($"Model: {displayName} · hosted-model", report);
        Assert.Contains("Context window: managed by provider", report);
        Assert.Contains("Project context: excluded from hosted requests", report);
        Assert.Contains("Queue: response in progress", report);
        Assert.Contains("Hosted requests: enabled for this session", report);
        Assert.Contains("Folder trust: untrusted", report);
    }

    [Fact]
    public void Trusted_project_is_visible_and_reports_automatic_context()
    {
        var conversation = new Conversation { ProjectPath = @"C:\work\repo" };

        var report = ConversationStatusReport.Build(conversation, false, 0, false, false, projectFolderTrusted: true, projectFolderTrustRoot: @"C:\work");

        Assert.Contains("Project: repo", report);
        Assert.Contains("Folder trust: trusted · work", report);
        Assert.DoesNotContain(@"C:\work", report, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Project context: bounded source files included automatically", report);
    }

    [Fact]
    public void Code_task_status_reports_approval_gated_tools()
    {
        var report = ConversationStatusReport.Build(new Conversation { IsCodeTask = true, ProjectPath = @"C:\work\repo" }, false, 0, false, false, projectFolderTrusted: true);

        Assert.Contains("Mode: Code task", report);
        Assert.Contains("Tools and file changes: available with per-change and per-command approval", report);
    }
}
