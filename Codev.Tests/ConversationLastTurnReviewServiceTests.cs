using Codev;

namespace Codev.Tests;

public sealed class ConversationLastTurnReviewServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Codev-last-turn-review-tests", Guid.NewGuid().ToString("N"));
    private readonly List<Guid> _conversationIds = [];
    private WorkspaceFileService Files => new(_root);

    public ConversationLastTurnReviewServiceTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task Snapshot_contains_only_last_turn_changes_and_uses_its_first_checkpoint()
    {
        var appPath = Path.Combine(_root, "app.js");
        await File.WriteAllTextAsync(appPath, "initial\n");
        var conversation = NewConversation();

        var earlierCheckpoint = await Files.CreateCheckpointAsync("app.js", conversation.Id);
        await File.WriteAllTextAsync(appPath, "earlier turn\n");
        conversation.FileChanges.Add(new FileChangeRecord("app.js", earlierCheckpoint, DateTimeOffset.UnixEpoch, "Edit",
            TurnUserMessageIndex: 0, ResultFileExisted: true, ResultSha256: FileSnapshot.ComputeSha256("earlier turn\n")));

        var lastTurnCheckpoint = await Files.CreateCheckpointAsync("app.js", conversation.Id);
        await File.WriteAllTextAsync(appPath, "final answer\n");
        conversation.FileChanges.Add(new FileChangeRecord("app.js", lastTurnCheckpoint, DateTimeOffset.UnixEpoch.AddTicks(1), "Edit",
            TurnUserMessageIndex: 2, ResultFileExisted: true, ResultSha256: FileSnapshot.ComputeSha256("final answer\n")));
        await File.WriteAllTextAsync(Path.Combine(_root, "external.js"), "untracked user change\n");

        var snapshot = await ConversationLastTurnReviewService.BuildSnapshotAsync(conversation, Files);

        Assert.Equal("assistant's last turn", snapshot.Branch);
        Assert.Equal(["app.js"], snapshot.Files);
        Assert.Contains("-earlier turn", snapshot.Diff);
        Assert.Contains("+final answer", snapshot.Diff);
        Assert.DoesNotContain("initial", snapshot.Diff);
        Assert.DoesNotContain("external.js", snapshot.Diff);
        Assert.False(snapshot.Truncated);
    }

    [Fact]
    public async Task Snapshot_refuses_when_file_changed_after_the_turn()
    {
        var path = Path.Combine(_root, "app.js");
        await File.WriteAllTextAsync(path, "before\n");
        var conversation = NewConversation();
        var checkpoint = await Files.CreateCheckpointAsync("app.js", conversation.Id);
        await File.WriteAllTextAsync(path, "assistant result\n");
        conversation.FileChanges.Add(new FileChangeRecord("app.js", checkpoint, DateTimeOffset.UnixEpoch, "Edit",
            TurnUserMessageIndex: 2, ResultFileExisted: true, ResultSha256: FileSnapshot.ComputeSha256("assistant result\n")));
        await File.WriteAllTextAsync(path, "later external edit\n");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => ConversationLastTurnReviewService.BuildSnapshotAsync(conversation, Files));

        Assert.Contains("changed after Codev's last recorded edit", error.Message);
        Assert.Equal("later external edit\n", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task Snapshot_refuses_when_a_later_turn_changed_the_same_file()
    {
        var path = Path.Combine(_root, "app.js");
        await File.WriteAllTextAsync(path, "before\n");
        var conversation = NewConversation();
        var lastTurnCheckpoint = await Files.CreateCheckpointAsync("app.js", conversation.Id);
        await File.WriteAllTextAsync(path, "last turn\n");
        conversation.FileChanges.Add(new FileChangeRecord("app.js", lastTurnCheckpoint, DateTimeOffset.UnixEpoch, "Edit",
            TurnUserMessageIndex: 2, ResultFileExisted: true, ResultSha256: FileSnapshot.ComputeSha256("last turn\n")));
        var laterCheckpoint = await Files.CreateCheckpointAsync("app.js", conversation.Id);
        await File.WriteAllTextAsync(path, "subsequent turn\n");
        conversation.FileChanges.Add(new FileChangeRecord("app.js", laterCheckpoint, DateTimeOffset.UnixEpoch.AddTicks(1), "Edit",
            ResultFileExisted: true, ResultSha256: FileSnapshot.ComputeSha256("subsequent turn\n")));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => ConversationLastTurnReviewService.BuildSnapshotAsync(conversation, Files));

        Assert.Contains("later Codev or unlinked file-history change", error.Message);
    }

    [Fact]
    public async Task Snapshot_after_a_second_turn_reviews_only_that_turns_delta()
    {
        var path = Path.Combine(_root, "app.js");
        await File.WriteAllTextAsync(path, "baseline\n");
        var conversation = NewConversation();
        var firstTurnCheckpoint = await Files.CreateCheckpointAsync("app.js", conversation.Id);
        await File.WriteAllTextAsync(path, "first turn\n");
        conversation.FileChanges.Add(new FileChangeRecord("app.js", firstTurnCheckpoint, DateTimeOffset.UnixEpoch, "Edit",
            TurnUserMessageIndex: 2, ResultFileExisted: true, ResultSha256: FileSnapshot.ComputeSha256("first turn\n")));

        conversation.Messages.Add(new ChatMessage("user", "second prompt"));
        conversation.Messages.Add(new ChatMessage("assistant", "second response"));
        var secondTurnCheckpoint = await Files.CreateCheckpointAsync("app.js", conversation.Id);
        await File.WriteAllTextAsync(path, "second turn\n");
        conversation.FileChanges.Add(new FileChangeRecord("app.js", secondTurnCheckpoint, DateTimeOffset.UnixEpoch.AddTicks(1), "Edit",
            TurnUserMessageIndex: 4, ResultFileExisted: true, ResultSha256: FileSnapshot.ComputeSha256("second turn\n")));

        var snapshot = await ConversationLastTurnReviewService.BuildSnapshotAsync(conversation, Files);

        Assert.Equal(["app.js"], snapshot.Files);
        Assert.Contains("-first turn", snapshot.Diff);
        Assert.Contains("+second turn", snapshot.Diff);
        Assert.DoesNotContain("baseline", snapshot.Diff);
    }

    [Fact]
    public async Task Snapshot_includes_files_created_and_deleted_in_the_last_turn()
    {
        var deletedPath = Path.Combine(_root, "deleted.js");
        await File.WriteAllTextAsync(deletedPath, "remove me\n");
        var conversation = NewConversation();
        var deletionCheckpoint = await Files.CreateCheckpointAsync("deleted.js", conversation.Id);
        File.Delete(deletedPath);
        conversation.FileChanges.Add(new FileChangeRecord("deleted.js", deletionCheckpoint, DateTimeOffset.UnixEpoch, "Delete",
            TurnUserMessageIndex: 2, ResultFileExisted: false));

        await File.WriteAllTextAsync(Path.Combine(_root, "created.js"), "add me\n");
        conversation.FileChanges.Add(new FileChangeRecord("created.js", null, DateTimeOffset.UnixEpoch.AddTicks(1), "Create",
            PreviousFileExisted: false, TurnUserMessageIndex: 2, ResultFileExisted: true, ResultSha256: FileSnapshot.ComputeSha256("add me\n")));

        var snapshot = await ConversationLastTurnReviewService.BuildSnapshotAsync(conversation, Files);

        Assert.Equal(2, snapshot.Files.Count);
        Assert.Contains("-remove me", snapshot.Diff);
        Assert.Contains("+add me", snapshot.Diff);
    }

    [Fact]
    public async Task Snapshot_refuses_pending_or_trimmed_history()
    {
        var conversation = NewConversation();
        conversation.PendingRequestCount = 1;
        await Assert.ThrowsAsync<InvalidOperationException>(() => ConversationLastTurnReviewService.BuildSnapshotAsync(conversation, Files));
        conversation.PendingRequestCount = 0;
        conversation.FileChangesPrunedThroughMessageIndex = 2;
        await Assert.ThrowsAsync<InvalidOperationException>(() => ConversationLastTurnReviewService.BuildSnapshotAsync(conversation, Files));
    }

    private Conversation NewConversation()
    {
        var conversation = new Conversation
        {
            Messages = [new ChatMessage("user", "first"), new ChatMessage("assistant", "answer"),
                new ChatMessage("user", "last prompt"), new ChatMessage("assistant", "last response")]
        };
        _conversationIds.Add(conversation.Id);
        return conversation;
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
        var checkpointRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Codev", "checkpoints");
        foreach (var id in _conversationIds)
            try { Directory.Delete(Path.Combine(checkpointRoot, id.ToString("N")), recursive: true); } catch { }
    }
}
