using Codev;

namespace Codev.Tests;

public sealed class ConversationFileChangeHistoryServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Codev-file-history-tests", Guid.NewGuid().ToString("N"));
    private readonly List<Guid> _conversationIds = [];
    private WorkspaceFileService Files => new(_root);

    public ConversationFileChangeHistoryServiceTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task Trim_deletes_only_orphaned_checkpoints_and_keeps_rewindable_recent_turns()
    {
        var conversation = NewConversation();
        var checkpoints = new List<string?>();
        for (var turn = 0; turn < 3; turn++)
        {
            var fileName = $"file{turn}.js";
            var fullPath = Path.Combine(_root, fileName);
            await File.WriteAllTextAsync(fullPath, $"before-{turn}");
            var checkpoint = await Files.CreateCheckpointAsync(fileName, conversation.Id);
            checkpoints.Add(checkpoint);
            await File.WriteAllTextAsync(fullPath, $"after-{turn}");
            conversation.FileChanges.Add(new FileChangeRecord(fileName, checkpoint, DateTimeOffset.UnixEpoch.AddTicks(turn), "Edit",
                TurnUserMessageIndex: turn * 2, ResultFileExisted: true, ResultSha256: FileSnapshot.ComputeSha256($"after-{turn}")));
        }

        Assert.True(ConversationFileChangeHistoryService.Trim(conversation, maxRetainedChanges: 2));

        Assert.Equal(2, conversation.FileChanges.Count);
        Assert.Equal(0, conversation.FileChangesPrunedThroughMessageIndex);
        Assert.False(File.Exists(checkpoints[0]));
        Assert.True(File.Exists(checkpoints[1]));
        Assert.True(File.Exists(checkpoints[2]));
        Assert.NotNull(ConversationFileChangeHistoryService.GetStatusMessage(conversation));
        await Assert.ThrowsAsync<InvalidOperationException>(() => ConversationCodeRewindService.BuildPlanAsync(conversation, 0, Files));
        var recentPlan = await ConversationCodeRewindService.BuildPlanAsync(conversation, 2, Files);
        Assert.Equal(2, recentPlan.Files.Count);
    }

    [Fact]
    public void Trim_marks_unlinked_evictions_as_a_global_code_rewind_boundary()
    {
        var conversation = NewConversation();
        conversation.FileChanges.AddRange(
        [
            new FileChangeRecord("one.js", null, DateTimeOffset.UnixEpoch, "Restore"),
            new FileChangeRecord("two.js", null, DateTimeOffset.UnixEpoch.AddTicks(1), "Restore"),
            new FileChangeRecord("three.js", null, DateTimeOffset.UnixEpoch.AddTicks(2), "Restore")
        ]);

        ConversationFileChangeHistoryService.Trim(conversation, maxRetainedChanges: 2);

        Assert.True(conversation.FileChangesPrunedUnlinked);
        Assert.Contains("without prompt links", ConversationFileChangeHistoryService.GetStatusMessage(conversation));
    }

    private Conversation NewConversation()
    {
        var conversation = new Conversation
        {
            Messages = [new ChatMessage("user", "one"), new ChatMessage("assistant", "a1"),
                new ChatMessage("user", "two"), new ChatMessage("assistant", "a2"),
                new ChatMessage("user", "three"), new ChatMessage("assistant", "a3")]
        };
        _conversationIds.Add(conversation.Id);
        return conversation;
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
        var checkpointRoot = Path.Combine(CodevDataPaths.LocalDataRoot, "Codev", "checkpoints");
        foreach (var id in _conversationIds)
            try { Directory.Delete(Path.Combine(checkpointRoot, id.ToString("N")), recursive: true); } catch { }
    }
}
