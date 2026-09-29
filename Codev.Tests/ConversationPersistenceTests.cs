namespace Codev.Tests;

public sealed class ConversationPersistenceTests
{
    [Fact]
    public void Message_index_is_runtime_only_and_is_not_serialized()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(new ChatMessage("user", "hello") { MessageIndex = 3 });
        Assert.DoesNotContain("MessageIndex", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Snapshot_detaches_mutable_lists_and_preserves_queued_turn_data()
    {
        var turn = new PersistedQueuedTurn(1, "model-a", 8192, false, false, null,
            ["src/a.cs"], ["private"], DateTimeOffset.UnixEpoch, OutputStyle: ConversationOutputStyles.CodeOnly, ThinkEnabled: true,
            TopP: 0.8, TopK: 40, PresencePenalty: 0.2, RepeatPenalty: 1.1, NumPredict: 4096);
        var source = new Conversation
        {
            Title = "Snapshot",
            Draft = "unsent prompt",
            Model = "claude-sonnet-test",
            Provider = CloudModelProviders.Anthropic,
            IsPlanMode = false,
            IsCodeTask = true,
            ThinkEnabled = true,
            Temperature = 0.25,
            TopP = 0.8,
            TopK = 40,
            PresencePenalty = 0.2,
            RepeatPenalty = 1.1,
            NumPredict = 4096,
            OutputStyle = ConversationOutputStyles.Explanatory,
            IncludeProjectContextForHosted = true,
            IncludeRepoMap = true,
            LastPromptTokens = 321,
            LastPromptContext = 4096,
            LastPromptModel = "claude-sonnet-test",
            LastPromptProvider = CloudModelProviders.Anthropic,
            CompactionSummary = "Accepted older context.",
            CompactionFromMessageCount = 2,
            CompactionThroughMessageCount = 4,
            Messages = [new("user", "before"), new ChatMessage("assistant", "reply")
            {
                GenerationStats = new OllamaGenerationStats(TimeSpan.FromMilliseconds(90), 32, 16d, TimeSpan.FromSeconds(1))
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
        source.PendingTurns[0] = turn with { Model = "model-b" };

        Assert.Equal("Snapshot", snapshot.Title);
        Assert.Equal(CloudModelProviders.Anthropic, snapshot.Provider);
        Assert.False(snapshot.IsPlanMode);
        Assert.True(snapshot.IsCodeTask);
        Assert.True(snapshot.ThinkEnabled);
        Assert.Equal(0.25, snapshot.Temperature);
        Assert.Equal(0.8, snapshot.TopP);
        Assert.Equal(40, snapshot.TopK);
        Assert.Equal(0.2, snapshot.PresencePenalty);
        Assert.Equal(1.1, snapshot.RepeatPenalty);
        Assert.Equal(4096, snapshot.NumPredict);
        Assert.Equal(ConversationOutputStyles.Explanatory, snapshot.OutputStyle);
        Assert.True(snapshot.IncludeProjectContextForHosted);
        Assert.True(snapshot.IncludeRepoMap);
        Assert.Equal(321, snapshot.LastPromptTokens);
        Assert.Equal(CloudModelProviders.Anthropic, snapshot.LastPromptProvider);
        Assert.Equal("Accepted older context.", snapshot.CompactionSummary);
        Assert.Equal(2, snapshot.CompactionFromMessageCount);
        Assert.Equal(4, snapshot.CompactionThroughMessageCount);
        Assert.Equal("unsent prompt", snapshot.Draft);
        Assert.Equal("before", snapshot.Messages[0].Content);
        Assert.Equal(source.Messages[1].GenerationStats, snapshot.Messages[1].GenerationStats);
        Assert.Equal("src/a.cs", Assert.Single(snapshot.ContextFiles));
        Assert.Equal(new GitDiffComment("src/a.cs", "+ updated", "Check null handling."), Assert.Single(snapshot.PendingDiffComments));
        Assert.Equal("model-a", Assert.Single(snapshot.PendingTurns).Model);
        Assert.Equal("src/a.cs", Assert.Single(snapshot.PendingTurns[0].ContextFiles!));
        Assert.Equal("private", Assert.Single(snapshot.PendingTurns[0].ContextExclusions!));
        Assert.Equal(ConversationOutputStyles.CodeOnly, snapshot.PendingTurns[0].OutputStyle);
        Assert.True(snapshot.PendingTurns[0].ThinkEnabled);
        Assert.Equal(0.8, snapshot.PendingTurns[0].TopP);
        Assert.Equal(40, snapshot.PendingTurns[0].TopK);
        Assert.Equal(0.2, snapshot.PendingTurns[0].PresencePenalty);
        Assert.Equal(1.1, snapshot.PendingTurns[0].RepeatPenalty);
        Assert.Equal(4096, snapshot.PendingTurns[0].NumPredict);
    }
}
