namespace Codev.Tests;

public sealed class ConversationPersistenceTests
{
    [Fact]
    public void Message_index_is_runtime_only_and_is_not_serialized()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(new ChatMessage("user", "hello") { MessageIndex = 3, IsQueued = true });
        Assert.DoesNotContain("MessageIndex", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("IsQueued", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Child_running_indicator_is_runtime_only_and_is_reflected_in_sidebar_labels()
    {
        var running = new Conversation { Title = "Independent task", IsChildTaskRunning = true };
        var waiting = new Conversation { Title = "Another task" };
        var parent = new Conversation { ChildConversations = [running, waiting] };

        Assert.Equal("Independent task · running", running.ChildConversationLabel);
        Assert.Equal("Child sessions · 2 · 1 running", parent.ChildConversationsLabel);
        var json = System.Text.Json.JsonSerializer.Serialize(parent);
        Assert.DoesNotContain("IsChildTaskRunning", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ChildConversationLabel", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void File_change_turn_provenance_round_trips_and_older_records_remain_unlinked()
    {
        var linked = new FileChangeRecord("src/app.cs", null, DateTimeOffset.UnixEpoch, "Create",
            PreviousFileExisted: false, TurnUserMessageIndex: 6, ResultFileExisted: true,
            ResultSha256: FileSnapshot.ComputeSha256("created"));
        var json = System.Text.Json.JsonSerializer.Serialize(linked);
        var restored = System.Text.Json.JsonSerializer.Deserialize<FileChangeRecord>(json);
        var olderJson = """{"RelativePath":"src/old.cs","CheckpointPath":null,"ChangedAt":"1970-01-01T00:00:00+00:00","Kind":"Create","PreviousFileExisted":false}""";
        var older = System.Text.Json.JsonSerializer.Deserialize<FileChangeRecord>(olderJson);

        Assert.Equal(6, restored!.TurnUserMessageIndex);
        Assert.True(restored.ResultFileExisted);
        Assert.Equal(FileSnapshot.ComputeSha256("created"), restored.ResultSha256);
        Assert.Null(older!.TurnUserMessageIndex);
    }

    [Fact]
    public void Snapshot_detaches_mutable_lists_and_preserves_queued_turn_data()
    {
        var turn = new PersistedQueuedTurn(1, "gpt-5.6", 8192, false, false, null,
            ["src/a.cs"], ["private"], DateTimeOffset.UnixEpoch, OutputStyle: ConversationOutputStyles.CodeOnly, ThinkEnabled: true,
            TopP: 0.8, TopK: 40, PresencePenalty: 0.2, RepeatPenalty: 1.1, NumPredict: 4096, AgentProfileName: "Debug");
        turn = turn with { OpenAiReasoningEffort = "high", OpenAiVerbosity = "low", OpenAiReasoningMode = "pro" };
        var source = new Conversation
        {
            ParentConversationId = Guid.NewGuid(),
            DelegatedFromMessageIndex = 1,
            DelegatedAgentName = "Debug",
            DelegatedResultReported = true,
            ChildWorktreeBranch = "codev/child-a1",
            ChildWorktreeStartCommit = "0123456789abcdef",
            ChildConversationsExpanded = false,
            Title = "Snapshot",
            Draft = "unsent prompt",
            Model = "gpt-5.6",
            Provider = CloudModelProviders.OpenAI,
            IsPlanMode = false,
            IsCodeTask = true,
            AgentProfileName = "Code",
            QueueEnabled = false,
            ThinkEnabled = true,
            Temperature = 0.25,
            TopP = 0.8,
            TopK = 40,
            PresencePenalty = 0.2,
            RepeatPenalty = 1.1,
            NumPredict = 4096,
            FileChangesPrunedThroughMessageIndex = 12,
            FileChangesPrunedUnlinked = true,
            OpenAiReasoningEffort = "HIGH",
            OpenAiVerbosity = "low",
            OpenAiReasoningMode = "pro",
            OutputStyle = ConversationOutputStyles.Explanatory,
            AllowHostedCodeTask = true,
            IncludeProjectContextForHosted = true,
            IncludeRepoMap = true,
            EnableSemanticSearch = true,
            LastPromptTokens = 321,
            LastPromptOutputTokens = 123,
            LastPromptContext = 4096,
            LastPromptModel = "gpt-5.6",
            LastPromptProvider = CloudModelProviders.OpenAI,
            CompactionSummary = "Accepted older context.",
            CompactionFromMessageCount = 2,
            CompactionThroughMessageCount = 4,
            Messages = [new("user", "before"), new ChatMessage("assistant", "reply")
            {
                GenerationStats = new OllamaGenerationStats(TimeSpan.FromMilliseconds(90), 32, 16d, TimeSpan.FromSeconds(1)),
                HostedUsage = new OpenAiCodeTaskUsage(160, 24, 2, 2, 2)
            }, new("user", "middle one"), new("assistant", "middle reply"), new("user", "next"), new("assistant", "next reply")],
            ContextFiles = ["src/a.cs"],
            PendingDiffComments = [new("src/a.cs", "+ updated", "Check null handling.")],
            PendingTurns = [turn]
        };

        var snapshot = Assert.Single(ConversationPersistence.CreateSnapshot([source]));
        source.Title = "changed";
        source.Draft = "changed draft";
        source.Messages[0] = new("user", "after");
        source.ContextFiles[0] = "src/b.cs";
        source.PendingDiffComments[0] = new("src/b.cs", "changed", "changed");
        source.PendingTurns[0] = turn with { Model = "gpt-5.5" };

        Assert.Equal("Snapshot", snapshot.Title);
        Assert.Equal(source.ParentConversationId, snapshot.ParentConversationId);
        Assert.Equal(1, snapshot.DelegatedFromMessageIndex);
        Assert.Equal("Debug", snapshot.DelegatedAgentName);
        Assert.True(snapshot.DelegatedResultReported);
        Assert.Equal("codev/child-a1", snapshot.ChildWorktreeBranch);
        Assert.Equal("0123456789abcdef", snapshot.ChildWorktreeStartCommit);
        Assert.False(snapshot.ChildConversationsExpanded);
        Assert.Empty(snapshot.ChildConversations);
        Assert.Equal(CloudModelProviders.OpenAI, snapshot.Provider);
        Assert.False(snapshot.IsPlanMode);
        Assert.True(snapshot.IsCodeTask);
        Assert.Equal("Code", snapshot.AgentProfileName);
        Assert.False(snapshot.QueueEnabled);
        Assert.True(snapshot.ThinkEnabled);
        Assert.Equal(0.25, snapshot.Temperature);
        Assert.Equal(0.8, snapshot.TopP);
        Assert.Equal(40, snapshot.TopK);
        Assert.Equal(0.2, snapshot.PresencePenalty);
        Assert.Equal(1.1, snapshot.RepeatPenalty);
        Assert.Equal(4096, snapshot.NumPredict);
        Assert.Equal("high", snapshot.OpenAiReasoningEffort);
        Assert.Equal("low", snapshot.OpenAiVerbosity);
        Assert.Equal("pro", snapshot.OpenAiReasoningMode);
        Assert.Equal(12, snapshot.FileChangesPrunedThroughMessageIndex);
        Assert.True(snapshot.FileChangesPrunedUnlinked);
        Assert.Equal(ConversationOutputStyles.Explanatory, snapshot.OutputStyle);
        Assert.True(snapshot.IncludeProjectContextForHosted);
        Assert.True(snapshot.AllowHostedCodeTask);
        Assert.True(snapshot.IncludeRepoMap);
        Assert.True(snapshot.EnableSemanticSearch);
        Assert.Equal(321, snapshot.LastPromptTokens);
        Assert.Equal(123, snapshot.LastPromptOutputTokens);
        Assert.Equal(CloudModelProviders.OpenAI, snapshot.LastPromptProvider);
        Assert.Equal("Accepted older context.", snapshot.CompactionSummary);
        Assert.Equal(2, snapshot.CompactionFromMessageCount);
        Assert.Equal(4, snapshot.CompactionThroughMessageCount);
        Assert.Equal("unsent prompt", snapshot.Draft);
        Assert.Equal("before", snapshot.Messages[0].Content);
        Assert.Equal(source.Messages[1].GenerationStats, snapshot.Messages[1].GenerationStats);
        Assert.Equal(source.Messages[1].HostedUsage, snapshot.Messages[1].HostedUsage);
        Assert.Equal("src/a.cs", Assert.Single(snapshot.ContextFiles));
        Assert.Equal(new GitDiffComment("src/a.cs", "+ updated", "Check null handling."), Assert.Single(snapshot.PendingDiffComments));
        Assert.Equal("gpt-5.6", Assert.Single(snapshot.PendingTurns).Model);
        Assert.Equal("src/a.cs", Assert.Single(snapshot.PendingTurns[0].ContextFiles!));
        Assert.Equal("private", Assert.Single(snapshot.PendingTurns[0].ContextExclusions!));
        Assert.Equal(ConversationOutputStyles.CodeOnly, snapshot.PendingTurns[0].OutputStyle);
        Assert.True(snapshot.PendingTurns[0].ThinkEnabled);
        Assert.Equal(0.8, snapshot.PendingTurns[0].TopP);
        Assert.Equal(40, snapshot.PendingTurns[0].TopK);
        Assert.Equal(0.2, snapshot.PendingTurns[0].PresencePenalty);
        Assert.Equal(1.1, snapshot.PendingTurns[0].RepeatPenalty);
        Assert.Equal(4096, snapshot.PendingTurns[0].NumPredict);
        Assert.Equal("high", snapshot.PendingTurns[0].OpenAiReasoningEffort);
        Assert.Equal("low", snapshot.PendingTurns[0].OpenAiVerbosity);
        Assert.Equal("pro", snapshot.PendingTurns[0].OpenAiReasoningMode);
        Assert.Equal("Debug", snapshot.PendingTurns[0].AgentProfileName);
    }
}
