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
            TopP = 0.85,
            TopK = 40,
            PresencePenalty = 0.5,
            RepeatPenalty = 1.1,
            NumPredict = 2048,
            IsPlanMode = true,
            ProjectPath = @"C:\work\app",
            ContextFiles = ["src/app.cs"]
        };

        var report = ConversationStatusReport.Build(conversation, false, 2, false, false);

        Assert.Contains("Model: Ollama (local) · qwen3-coder:30b", report);
        Assert.Contains("Context window: 65,536 tokens", report);
        Assert.Contains("Temperature: 0.2", report);
        Assert.Contains("Thinking: off", report);
        Assert.Contains("Sampling overrides: temperature=0.2, top_p=0.85, top_k=40, presence_penalty=0.5, repeat_penalty=1.1, num_predict=2048", report);
        Assert.Contains("Mode: Plan", report);
        Assert.Contains("Project: app", report);
        Assert.DoesNotContain(@"C:\work\app", report, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Project context: 1 selected file(s)", report);
        Assert.Contains("Repository map: off", report);
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
            IncludeProjectContextForHosted = false,
            IncludeRepoMap = true
        };

        var report = ConversationStatusReport.Build(conversation, true, 0, false, true);

        Assert.Contains($"Model: {displayName} · hosted-model", report);
        Assert.Contains("Context window: managed by provider", report);
        Assert.Contains("Thinking: unavailable for hosted providers", report);
        Assert.Contains("Project context: excluded from hosted requests", report);
        Assert.Contains("Repository map: unavailable under the current context policy", report);
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
    public void Remote_ollama_status_does_not_claim_requests_stay_local()
    {
        var report = ConversationStatusReport.Build(new Conversation { Model = "qwen3-coder:30b" }, false, 0, false, false,
            ollamaEndpoint: "https://ollama.example/api", ollamaEndpointIsLocal: false);

        Assert.Contains("Model: Ollama (remote · https://ollama.example) · qwen3-coder:30b", report);
    }

    [Theory]
    [InlineData("https://name:password@ollama.example:9443/api?token=secret#fragment", "https://ollama.example:9443")]
    [InlineData("https://[2001:db8::1]:9443/api?token=secret", "https://[2001:db8::1]:9443")]
    public void Remote_ollama_status_redacts_endpoint_paths_credentials_and_query(string endpoint, string safeHost)
    {
        var report = ConversationStatusReport.Build(new Conversation(), false, 0, false, false,
            ollamaEndpoint: endpoint, ollamaEndpointIsLocal: false);

        Assert.Contains($"Model: Ollama (remote · {safeHost})", report);
        Assert.DoesNotContain("password", report, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", report, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("/api", report, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Code_task_status_reports_approval_gated_tools()
    {
        var report = ConversationStatusReport.Build(new Conversation { IsCodeTask = true, ProjectPath = @"C:\work\repo" }, false, 0, false, false, projectFolderTrusted: true);

        Assert.Contains("Mode: Code task", report);
        Assert.Contains("Tools and file changes: available with per-change review and project command permissions", report);
        Assert.Contains("Project command permissions: ask every time", report);
    }

    [Fact]
    public void Status_reports_exact_project_allowlist_and_deny_counts()
    {
        var report = ConversationStatusReport.Build(new Conversation { IsCodeTask = true }, false, 0, false, false,
            commandPermissionMode: ProjectCommandPermissionMode.Allowlist, allowedCommandRules: 2, deniedCommandRules: 1);

        Assert.Contains("Project command permissions: exact allowlist · 2 allow rule(s), 1 deny rule(s); unlisted commands ask", report);
    }

    [Fact]
    public void Status_reports_last_local_usage_and_active_compaction_without_leaking_summary()
    {
        var report = ConversationStatusReport.Build(new Conversation
        {
            Model = "qwen3-coder:30b",
            NumCtx = 32_768,
            LastPromptTokens = 26_214,
            LastPromptContext = 32_768,
            LastPromptModel = "qwen3-coder:30b",
            LastPromptProvider = "ollama",
            CompactionSummary = "private summary text",
            CompactionThroughMessageCount = 8
        }, false, 0, false, false);

        Assert.Contains("Last request usage: 26,214 input tokens · 80% of 32,768 tokens", report);
        Assert.Contains("Conversation summary: active · first 8 messages summarized for future prompts", report);
        Assert.DoesNotContain("private summary text", report);
    }

    [Fact]
    public void Hosted_status_labels_provider_usage_without_inventing_context_percentage()
    {
        var report = ConversationStatusReport.Build(new Conversation
        {
            Provider = CloudModelProviders.OpenAI,
            Model = "hosted-model",
            LastPromptTokens = 1_024,
            LastPromptModel = "hosted-model",
            LastPromptProvider = CloudModelProviders.OpenAI
        }, false, 0, false, true);

        Assert.Contains("Last request usage: 1,024 provider-reported input tokens", report);
        Assert.DoesNotContain("% of", report);
    }

    [Fact]
    public void Status_does_not_attribute_usage_from_a_different_selected_model()
    {
        var report = ConversationStatusReport.Build(new Conversation
        {
            Model = "new-model",
            LastPromptTokens = 12_000,
            LastPromptContext = 16_384,
            LastPromptModel = "previous-model",
            LastPromptProvider = "ollama"
        }, false, 0, false, false);

        Assert.Contains("Last request usage: not available for the selected model yet", report);
    }

    [Fact]
    public void Status_does_not_attribute_usage_from_a_different_provider_with_same_model_id()
    {
        var report = ConversationStatusReport.Build(new Conversation
        {
            Provider = CloudModelProviders.OpenAI,
            Model = "same-model-id",
            LastPromptTokens = 12_000,
            LastPromptContext = 16_384,
            LastPromptModel = "same-model-id",
            LastPromptProvider = "ollama"
        }, false, 0, false, true);

        Assert.Contains("Last request usage: not available for the selected model yet", report);
    }

    [Fact]
    public void Status_lists_only_instruction_file_paths_from_the_last_request()
    {
        var report = ConversationStatusReport.Build(new Conversation { ProjectPath = @"C:\work\repo" }, false, 0, false, false,
            lastPromptInstructionFiles: ["AGENTS.md", ".codev/rules/tests.md"]);

        Assert.Contains("Project instruction files (last request): AGENTS.md, .codev/rules/tests.md", report);
        Assert.DoesNotContain("instruction contents", report);
    }

    [Fact]
    public void Instruction_file_names_are_extracted_from_assembled_project_guidance()
    {
        var instructions = """
            Applicable project guidance:
            --- AGENTS.md (project guidance; user-provided) ---
            private instruction contents
            --- .codev/rules/tests.md (project guidance; user-provided) ---
            more private contents
            """;

        Assert.Equal(["AGENTS.md", ".codev/rules/tests.md"], ProjectAgentInstructions.GetIncludedRelativePaths(instructions));
    }
}
