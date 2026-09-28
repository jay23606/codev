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

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
