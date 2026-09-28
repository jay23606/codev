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
            ["src/a.cs"], ["private"], DateTimeOffset.UnixEpoch, OutputStyle: ConversationOutputStyles.CodeOnly);
        var source = new Conversation
        {
            Title = "Snapshot",
            Draft = "unsent prompt",
            Model = "claude-sonnet-test",
            Provider = CloudModelProviders.Anthropic,
            IsPlanMode = false,
            IsCodeTask = true,
            OutputStyle = ConversationOutputStyles.Explanatory,
            IncludeProjectContextForHosted = true,
            IncludeRepoMap = true,
            Messages = [new("user", "before"), new ChatMessage("assistant", "reply")
            {
                GenerationStats = new OllamaGenerationStats(TimeSpan.FromMilliseconds(90), 32, 16d, TimeSpan.FromSeconds(1))
            }],
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
        Assert.Equal(ConversationOutputStyles.Explanatory, snapshot.OutputStyle);
        Assert.True(snapshot.IncludeProjectContextForHosted);
        Assert.True(snapshot.IncludeRepoMap);
        Assert.Equal("unsent prompt", snapshot.Draft);
        Assert.Equal("before", snapshot.Messages[0].Content);
        Assert.Equal(source.Messages[1].GenerationStats, snapshot.Messages[1].GenerationStats);
        Assert.Equal("src/a.cs", Assert.Single(snapshot.ContextFiles));
        Assert.Equal(new GitDiffComment("src/a.cs", "+ updated", "Check null handling."), Assert.Single(snapshot.PendingDiffComments));
        Assert.Equal("model-a", Assert.Single(snapshot.PendingTurns).Model);
        Assert.Equal("src/a.cs", Assert.Single(snapshot.PendingTurns[0].ContextFiles!));
        Assert.Equal("private", Assert.Single(snapshot.PendingTurns[0].ContextExclusions!));
        Assert.Equal(ConversationOutputStyles.CodeOnly, snapshot.PendingTurns[0].OutputStyle);
    }
}
