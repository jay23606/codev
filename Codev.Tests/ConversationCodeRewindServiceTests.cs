using Codev;
using System.Runtime.InteropServices;

namespace Codev.Tests;

public sealed class ConversationCodeRewindServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Codev-code-rewind-tests", Guid.NewGuid().ToString("N"));
    private readonly string _linkedHardLinkPath;
    private readonly List<Guid> _conversationIds = [];
    private WorkspaceFileService Files => new(_root);

    public ConversationCodeRewindServiceTests()
    {
        Directory.CreateDirectory(_root);
        _linkedHardLinkPath = Path.Combine(Path.GetDirectoryName(_root)!, Path.GetFileName(_root) + "-shared.js");
    }

    [Fact]
    public async Task Plan_and_apply_restore_files_to_the_state_before_the_selected_turn()
    {
        var path = Path.Combine(_root, "app.js");
        await File.WriteAllTextAsync(path, "before");
        var conversation = NewConversation();
        var checkpoint = await Files.CreateCheckpointAsync("app.js", conversation.Id);
        await File.WriteAllTextAsync(path, "after");
        conversation.FileChanges.Add(new FileChangeRecord("app.js", checkpoint, DateTimeOffset.UnixEpoch, "Edit",
            TurnUserMessageIndex: 2, ResultFileExisted: true, ResultSha256: await HashFileAsync(path)));

        var plan = await ConversationCodeRewindService.BuildPlanAsync(conversation, 2, Files);
        Assert.Single(plan.Files);
        Assert.Equal("before", plan.Files[0].RestoreContent);

        await ConversationCodeRewindService.ApplyPlanAsync(conversation, plan, Files);

        Assert.Equal("before", await File.ReadAllTextAsync(path));
        var rewind = Assert.Single(conversation.FileChanges, change => change.Kind == "Code rewind");
        Assert.Equal(2, rewind.TurnUserMessageIndex);
        Assert.Equal("after", await Files.ReadCheckpointAsync("app.js", conversation.Id, rewind.CheckpointPath!));
        Assert.Equal(FileSnapshot.ComputeSha256("before"), rewind.ResultSha256);
    }

    [Fact]
    public async Task Plan_uses_the_first_checkpoint_when_the_turn_changed_a_file_more_than_once()
    {
        var path = Path.Combine(_root, "app.js");
        await File.WriteAllTextAsync(path, "initial");
        var conversation = NewConversation();
        var firstCheckpoint = await Files.CreateCheckpointAsync("app.js", conversation.Id);
        await File.WriteAllTextAsync(path, "intermediate");
        conversation.FileChanges.Add(new FileChangeRecord("app.js", firstCheckpoint, DateTimeOffset.UnixEpoch, "Edit",
            TurnUserMessageIndex: 2, ResultFileExisted: true, ResultSha256: FileSnapshot.ComputeSha256("intermediate")));
        var secondCheckpoint = await Files.CreateCheckpointAsync("app.js", conversation.Id);
        await File.WriteAllTextAsync(path, "final");
        conversation.FileChanges.Add(new FileChangeRecord("app.js", secondCheckpoint, DateTimeOffset.UnixEpoch.AddTicks(1), "Edit",
            TurnUserMessageIndex: 2, ResultFileExisted: true, ResultSha256: FileSnapshot.ComputeSha256("final")));

        var plan = await ConversationCodeRewindService.BuildPlanAsync(conversation, 2, Files);

        Assert.Equal("initial", Assert.Single(plan.Files).RestoreContent);
    }

    [Fact]
    public async Task Plan_fails_closed_when_current_file_no_longer_matches_Codev_history()
    {
        var path = Path.Combine(_root, "app.js");
        await File.WriteAllTextAsync(path, "before");
        var conversation = NewConversation();
        var checkpoint = await Files.CreateCheckpointAsync("app.js", conversation.Id);
        await File.WriteAllTextAsync(path, "after");
        conversation.FileChanges.Add(new FileChangeRecord("app.js", checkpoint, DateTimeOffset.UnixEpoch, "Edit",
            TurnUserMessageIndex: 2, ResultFileExisted: true, ResultSha256: FileSnapshot.ComputeSha256("after")));
        await File.WriteAllTextAsync(path, "external edit");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => ConversationCodeRewindService.BuildPlanAsync(conversation, 2, Files));

        Assert.Contains("has changed", error.Message);
        Assert.Equal("external edit", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task Checkpoint_creation_refuses_to_read_a_hard_linked_file()
    {
        if (!FileHardLinkInspector.IsSupportedPlatform) return;
        var path = Path.Combine(_root, "shared.js");
        var linkedPath = _linkedHardLinkPath;
        await File.WriteAllTextAsync(path, "before");
        Assert.True(TryCreateHardLink(path, linkedPath));
        var conversation = NewConversation();

        Assert.Equal(2, FileHardLinkInspector.TryGetLinkCount(path));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Files.CreateCheckpointAsync("shared.js", conversation.Id));

        Assert.Equal("before", await File.ReadAllTextAsync(path));
        Assert.Equal("before", await File.ReadAllTextAsync(linkedPath));
    }

    [Fact]
    public async Task Apply_refuses_to_replace_a_file_that_becomes_hard_linked_after_review()
    {
        if (!FileHardLinkInspector.IsSupportedPlatform) return;
        var path = Path.Combine(_root, "shared-after-review.js");
        await File.WriteAllTextAsync(path, "before");
        var conversation = NewConversation();
        var checkpoint = await Files.CreateCheckpointAsync("shared-after-review.js", conversation.Id);
        await File.WriteAllTextAsync(path, "after");
        conversation.FileChanges.Add(new FileChangeRecord("shared-after-review.js", checkpoint, DateTimeOffset.UnixEpoch, "Edit",
            TurnUserMessageIndex: 2, ResultFileExisted: true, ResultSha256: FileSnapshot.ComputeSha256("after")));
        var plan = await ConversationCodeRewindService.BuildPlanAsync(conversation, 2, Files);

        Assert.True(TryCreateHardLink(path, _linkedHardLinkPath));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => ConversationCodeRewindService.ApplyPlanAsync(conversation, plan, Files));

        Assert.Contains("2 hard links", error.Message, StringComparison.Ordinal);
        Assert.Equal("after", await File.ReadAllTextAsync(path));
        Assert.Equal("after", await File.ReadAllTextAsync(_linkedHardLinkPath));
    }

    [Fact]
    public async Task Rewind_preserves_checkpoint_bytes_and_records_the_raw_file_hash()
    {
        var path = Path.Combine(_root, "app.js");
        var before = new byte[] { 0xFF, 0xC3, 0x28 };
        await File.WriteAllBytesAsync(path, before);
        var conversation = NewConversation();
        var checkpoint = await Files.CreateCheckpointAsync("app.js", conversation.Id);
        await File.WriteAllTextAsync(path, "after");
        conversation.FileChanges.Add(new FileChangeRecord("app.js", checkpoint, DateTimeOffset.UnixEpoch, "Edit",
            TurnUserMessageIndex: 2, ResultFileExisted: true, ResultSha256: FileSnapshot.ComputeSha256("after")));

        var plan = await ConversationCodeRewindService.BuildPlanAsync(conversation, 2, Files);
        await ConversationCodeRewindService.ApplyPlanAsync(conversation, plan, Files);

        Assert.Equal(before, await File.ReadAllBytesAsync(path));
        var rewind = Assert.Single(conversation.FileChanges, change => change.Kind == "Code rewind");
        Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(before)), rewind.ResultSha256);
        Assert.Empty((await ConversationCodeRewindService.BuildPlanAsync(conversation, 2, Files)).Files);
    }

    [Fact]
    public async Task Plan_fails_closed_for_later_unlinked_history_or_legacy_state()
    {
        var path = Path.Combine(_root, "app.js");
        await File.WriteAllTextAsync(path, "before");
        var conversation = NewConversation();
        var checkpoint = await Files.CreateCheckpointAsync("app.js", conversation.Id);
        await File.WriteAllTextAsync(path, "after");
        conversation.FileChanges.Add(new FileChangeRecord("app.js", checkpoint, DateTimeOffset.UnixEpoch, "Edit",
            TurnUserMessageIndex: 2, ResultFileExisted: true, ResultSha256: FileSnapshot.ComputeSha256("after")));
        conversation.FileChanges.Add(new FileChangeRecord("app.js", checkpoint, DateTimeOffset.UnixEpoch.AddTicks(1), "Restore"));

        var unlinked = await Assert.ThrowsAsync<InvalidOperationException>(() => ConversationCodeRewindService.BuildPlanAsync(conversation, 2, Files));
        Assert.Contains("no prompt association", unlinked.Message);

        conversation.FileChanges.RemoveAt(1);
        conversation.FileChanges[0] = conversation.FileChanges[0] with { ResultFileExisted = null, ResultSha256 = null };
        var legacy = await Assert.ThrowsAsync<InvalidOperationException>(() => ConversationCodeRewindService.BuildPlanAsync(conversation, 2, Files));
        Assert.Contains("predates state tracking", legacy.Message);
    }

    [Fact]
    public async Task Applying_a_create_rewind_removes_the_new_file_and_records_the_undo_state()
    {
        var path = Path.Combine(_root, "new.js");
        await File.WriteAllTextAsync(path, "created");
        var conversation = NewConversation();
        conversation.FileChanges.Add(new FileChangeRecord("new.js", null, DateTimeOffset.UnixEpoch, "Create", PreviousFileExisted: false,
            TurnUserMessageIndex: 0, ResultFileExisted: true, ResultSha256: FileSnapshot.ComputeSha256("created")));

        var plan = await ConversationCodeRewindService.BuildPlanAsync(conversation, 0, Files);
        await ConversationCodeRewindService.ApplyPlanAsync(conversation, plan, Files);

        Assert.False(File.Exists(path));
        var rewind = Assert.Single(conversation.FileChanges, change => change.Kind == "Code rewind");
        Assert.True(rewind.PreviousFileExisted);
        Assert.False(rewind.ResultFileExisted);
        Assert.Null(rewind.ResultSha256);
    }

    [Fact]
    public async Task Apply_rolls_back_earlier_files_if_a_later_file_changed_after_review()
    {
        var firstPath = Path.Combine(_root, "first.js");
        var secondPath = Path.Combine(_root, "second.js");
        await File.WriteAllTextAsync(firstPath, "first-before");
        await File.WriteAllTextAsync(secondPath, "second-before");
        var conversation = NewConversation();
        var firstCheckpoint = await Files.CreateCheckpointAsync("first.js", conversation.Id);
        await File.WriteAllTextAsync(firstPath, "first-after");
        conversation.FileChanges.Add(new FileChangeRecord("first.js", firstCheckpoint, DateTimeOffset.UnixEpoch, "Edit",
            TurnUserMessageIndex: 2, ResultFileExisted: true, ResultSha256: FileSnapshot.ComputeSha256("first-after")));
        var secondCheckpoint = await Files.CreateCheckpointAsync("second.js", conversation.Id);
        await File.WriteAllTextAsync(secondPath, "second-after");
        conversation.FileChanges.Add(new FileChangeRecord("second.js", secondCheckpoint, DateTimeOffset.UnixEpoch, "Edit",
            TurnUserMessageIndex: 2, ResultFileExisted: true, ResultSha256: FileSnapshot.ComputeSha256("second-after")));

        var plan = await ConversationCodeRewindService.BuildPlanAsync(conversation, 2, Files);
        await File.WriteAllTextAsync(secondPath, "external concurrent edit");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => ConversationCodeRewindService.ApplyPlanAsync(conversation, plan, Files));

        Assert.Contains("No planned file changes remain applied", error.Message);
        Assert.Equal("first-after", await File.ReadAllTextAsync(firstPath));
        Assert.Equal("external concurrent edit", await File.ReadAllTextAsync(secondPath));
        Assert.DoesNotContain(conversation.FileChanges, change => change.Kind == "Code rewind");
    }

    [Fact]
    public async Task Plan_refuses_to_run_while_turns_are_pending_or_for_a_non_user_message()
    {
        var conversation = NewConversation();
        conversation.PendingRequestCount = 1;
        await Assert.ThrowsAsync<InvalidOperationException>(() => ConversationCodeRewindService.BuildPlanAsync(conversation, 0, Files));
        conversation.PendingRequestCount = 0;
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => ConversationCodeRewindService.BuildPlanAsync(conversation, 1, Files));
    }

    private Conversation NewConversation()
    {
        var conversation = new Conversation
        {
            Messages = [new ChatMessage("user", "first"), new ChatMessage("assistant", "answer"),
                new ChatMessage("user", "second"), new ChatMessage("assistant", "answer")]
        };
        _conversationIds.Add(conversation.Id);
        return conversation;
    }

    private static async Task<string> HashFileAsync(string path) => FileSnapshot.ComputeSha256(await File.ReadAllTextAsync(path));

    private static bool TryCreateHardLink(string existingPath, string newPath)
    {
        if (OperatingSystem.IsWindows()) return CreateHardLinkWindows(newPath, existingPath, IntPtr.Zero);
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()) return CreateHardLinkUnix(existingPath, newPath) == 0;
        return false;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkWindows(string newFileName, string existingFileName, IntPtr securityAttributes);

    [DllImport("libc", EntryPoint = "link", SetLastError = true)]
    private static extern int CreateHardLinkUnix(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string existingPath,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string newPath);

    public void Dispose()
    {
        try { File.Delete(_linkedHardLinkPath); } catch { }
        try { Directory.Delete(_root, recursive: true); } catch { }
        var checkpointRoot = Path.Combine(CodevDataPaths.LocalDataRoot, "Codev", "checkpoints");
        foreach (var id in _conversationIds)
            try { Directory.Delete(Path.Combine(checkpointRoot, id.ToString("N")), recursive: true); } catch { }
    }
}
