namespace Codev.Tests;

public sealed class ConversationForkServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "codev-conversation-fork-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Fork_copies_history_settings_and_checkpoints_into_an_independent_identity()
    {
        var checkpointRoot = Path.Combine(_root, "checkpoints");
        var sourceId = Guid.NewGuid();
        var sourceDirectory = Path.Combine(checkpointRoot, sourceId.ToString("N"));
        Directory.CreateDirectory(sourceDirectory);
        var checkpoint = Path.Combine(sourceDirectory, "before-change.snapshot");
        await File.WriteAllTextAsync(checkpoint, "original file contents");
        var source = new Conversation
        {
            Id = sourceId,
            ParentConversationId = Guid.NewGuid(),
            DelegatedFromMessageIndex = 1,
            DelegatedAgentName = "Debug",
            DelegatedResultReported = true,
            ChildWorktreeBranch = "codev/child-source",
            ChildWorktreeStartCommit = "0123456789abcdef",
            Title = "Fix project",
            Draft = "continue this thought",
            Model = "qwen-coder:latest",
            Provider = "ollama",
            ProjectPath = _root,
            NumCtx = 32768,
            OutputStyle = ConversationOutputStyles.Concise,
            Messages = [new ChatMessage("user", "change the parser"), new ChatMessage("assistant", "I will inspect it.")],
            FileChanges = [new FileChangeRecord("src/parser.cs", checkpoint, DateTimeOffset.UnixEpoch, "Edit")],
            PendingDiffComments = [new GitDiffComment("src/parser.cs", "+ fix", "Keep it simple.")]
        };

        var fork = await ConversationForkService.CreateForkAsync(source, checkpointRoot);

        Assert.NotEqual(source.Id, fork.Id);
        Assert.Null(fork.ParentConversationId);
        Assert.Null(fork.DelegatedFromMessageIndex);
        Assert.Null(fork.DelegatedAgentName);
        Assert.False(fork.DelegatedResultReported);
        Assert.Null(fork.ChildWorktreeBranch);
        Assert.Null(fork.ChildWorktreeStartCommit);
        Assert.Equal("Fix project · fork", fork.Title);
        Assert.Equal(source.Draft, fork.Draft);
        Assert.Equal(source.Model, fork.Model);
        Assert.Equal(source.NumCtx, fork.NumCtx);
        Assert.Equal(source.OutputStyle, fork.OutputStyle);
        Assert.Equal(source.ProjectPath, fork.ProjectPath);
        Assert.Equal(source.Messages, fork.Messages);
        Assert.Equal(source.PendingDiffComments, fork.PendingDiffComments);
        var forkCheckpoint = Assert.Single(fork.FileChanges).CheckpointPath!;
        Assert.StartsWith(Path.Combine(checkpointRoot, fork.Id.ToString("N")) + Path.DirectorySeparatorChar, forkCheckpoint,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        Assert.Equal("original file contents", await File.ReadAllTextAsync(forkCheckpoint));
        await File.WriteAllTextAsync(forkCheckpoint, "fork changed");
        Assert.Equal("original file contents", await File.ReadAllTextAsync(checkpoint));
    }


    [Fact]
    public async Task Fork_refuses_checkpoint_path_outside_source_and_cleans_partial_copy()
    {
        var checkpointRoot = Path.Combine(_root, "checkpoints");
        var sourceId = Guid.NewGuid();
        var sourceDirectory = Path.Combine(checkpointRoot, sourceId.ToString("N"));
        Directory.CreateDirectory(sourceDirectory);
        var validCheckpoint = Path.Combine(sourceDirectory, "valid.snapshot");
        var outsideCheckpoint = Path.Combine(_root, "outside.snapshot");
        await File.WriteAllTextAsync(validCheckpoint, "safe");
        await File.WriteAllTextAsync(outsideCheckpoint, "outside");
        var source = new Conversation
        {
            Id = sourceId,
            FileChanges =
            [
                new FileChangeRecord("a.cs", validCheckpoint, DateTimeOffset.UnixEpoch, "Edit"),
                new FileChangeRecord("b.cs", outsideCheckpoint, DateTimeOffset.UnixEpoch, "Edit")
            ]
        };

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => ConversationForkService.CreateForkAsync(source, checkpointRoot));

        Assert.Single(Directory.GetDirectories(checkpointRoot));
    }

    [Fact]
    public async Task Side_chat_keeps_history_through_selected_prompt_and_drops_pending_queue()
    {
        var source = new Conversation
        {
            Title = "Long task",
            Messages =
            [
                new("user", "first prompt"),
                new("assistant", "first answer"),
                new("user", "branch from here"),
                new("assistant", "later answer")
            ],
            CompactionSummary = "Summary of the first turn.",
            CompactionThroughMessageCount = 2,
            PendingTurns = [new PersistedQueuedTurn(3, "model", 4096, false, false, null, [], [], DateTimeOffset.UtcNow)]
        };

        var sideChat = await ConversationForkService.CreateSideChatAsync(source, 2, Path.Combine(_root, "checkpoints"));

        Assert.NotEqual(source.Id, sideChat.Id);
        Assert.StartsWith("Side chat · branch from here", sideChat.Title, StringComparison.Ordinal);
        Assert.Equal("branch from here", sideChat.Draft);
        Assert.Equal(["first prompt", "first answer"], sideChat.Messages.Select(message => message.Content));
        Assert.Equal([0, 1], sideChat.Messages.Select(message => message.MessageIndex));
        Assert.Empty(sideChat.PendingTurns);
        Assert.Equal(0, sideChat.PendingRequestCount);
        Assert.Empty(sideChat.CompactionSummary);
        Assert.Equal(0, sideChat.CompactionThroughMessageCount);
        Assert.Equal(4, source.Messages.Count);
    }

    [Fact]
    public async Task Side_chat_keeps_compaction_when_its_entire_range_precedes_the_branch_point()
    {
        var source = new Conversation
        {
            Messages =
            [
                new("user", "first prompt"), new("assistant", "first answer"),
                new("user", "second prompt"), new("assistant", "second answer"),
                new("user", "branch from here"), new("assistant", "later answer")
            ],
            CompactionSummary = "Summary of the first turn.",
            CompactionThroughMessageCount = 2
        };

        var sideChat = await ConversationForkService.CreateSideChatAsync(source, 4, Path.Combine(_root, "checkpoints"));

        Assert.Equal("Summary of the first turn.", sideChat.CompactionSummary);
        Assert.Equal(2, sideChat.CompactionThroughMessageCount);
        Assert.Contains("Summary of the first turn.", ConversationCompactionService.BuildPromptHistory(sideChat, sideChat.Messages)[0].Content);
        Assert.Equal(["first prompt", "first answer", "second prompt", "second answer"],
            sideChat.Messages.Select(message => message.Content));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
