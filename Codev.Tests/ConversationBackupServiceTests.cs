using System.IO;
using System.Text.Json;

namespace Codev.Tests;

public sealed class ConversationBackupServiceTests
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    [Fact]
    public void Export_import_round_trip_preserves_history_but_assigns_fresh_runtime_identity()
    {
        var source = new Conversation
        {
            Id = Guid.NewGuid(),
            Title = "Debug session",
            Draft = "an unsent prompt draft",
            Model = "qwen3-coder:30b",
            Temperature = 0.25,
            ProjectPath = @"C:\work\sample",
            PendingRequestCount = 3,
            Messages = [new("user", "Why does this fail?"), new("assistant", "I will trace the cause.")]
        };

        var backup = ConversationBackupService.Export([source], Options);
        var imported = Assert.Single(ConversationBackupService.Import(backup, Options));

        Assert.NotEqual(source.Id, imported.Id);
        Assert.Equal(source.Title, imported.Title);
        Assert.Equal(source.Draft, imported.Draft);
        Assert.Equal(source.Model, imported.Model);
        Assert.Equal(source.Temperature, imported.Temperature);
        Assert.Equal(source.ProjectPath, imported.ProjectPath);
        Assert.Equal(source.Messages, imported.Messages);
        Assert.Equal(0, imported.PendingRequestCount);
    }

    [Fact]
    public void Backup_export_does_not_include_runtime_queue_recovery_state()
    {
        var conversation = new Conversation
        {
            Messages = [new("user", "hello"), new("assistant", "")],
            PendingTurns = [new PersistedQueuedTurn(1, "model-a", 8192, true, false, @"C:\project", ["secret.cs"], [], DateTimeOffset.UnixEpoch)]
        };

        var backup = ConversationBackupService.Export([conversation], Options);
        var imported = Assert.Single(ConversationBackupService.Import(backup, Options));

        Assert.DoesNotContain("PendingTurns", backup, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(imported.PendingTurns);
        Assert.Equal("This request did not finish before it was imported.", imported.Messages[1].Content);
    }

    [Fact]
    public void Backup_import_discards_saved_queue_entries_even_if_the_file_contains_them()
    {
        var json = """[{"Messages":[{"Role":"user","Content":"do work"},{"Role":"assistant","Content":"Queued locally"}],"PendingTurns":[{"AssistantIndex":1,"Model":"model-a","NumCtx":8192,"IsCodeTask":true,"IsPlanMode":false,"ProjectPath":"C:\\work","ContextFiles":[],"ContextExclusions":[],"EnqueuedAt":"2026-01-01T00:00:00+00:00"}]}]""";

        var imported = Assert.Single(ConversationBackupService.Import(json, Options));

        Assert.Empty(imported.PendingTurns);
        Assert.Equal("Queued locally", imported.Messages[1].Content);
    }

    [Fact]
    public void Backup_omits_local_checkpoint_file_paths()
    {
        var conversation = new Conversation
        {
            FileChanges = [new FileChangeRecord("src/app.cs", @"C:\Users\me\AppData\checkpoint.txt", DateTimeOffset.Now, "Modified")]
        };

        var backup = ConversationBackupService.Export([conversation], Options);
        var imported = Assert.Single(ConversationBackupService.Import(backup, Options));

        Assert.DoesNotContain("checkpoint.txt", backup, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("src/app.cs", imported.FileChanges[0].RelativePath);
        Assert.Null(imported.FileChanges[0].CheckpointPath);
    }

    [Fact]
    public void Backup_import_normalizes_null_drafts_and_caps_oversized_drafts()
    {
        var nullDraft = Assert.Single(ConversationBackupService.Import("[{\"Draft\":null,\"Messages\":[]}]", Options));
        Assert.Equal("", nullDraft.Draft);

        var oversized = new Conversation { Draft = new string('x', 500_001) };
        var imported = Assert.Single(ConversationBackupService.Import(JsonSerializer.Serialize(new[] { oversized }, Options), Options));
        Assert.Equal(500_000, imported.Draft.Length);
    }

    [Fact]
    public void Backup_import_caps_context_files_to_the_prompt_limit()
    {
        var source = new Conversation { ContextFiles = Enumerable.Range(0, WorkspaceFileService.MaxContextFiles + 5).Select(index => $"file-{index}.cs").ToList() };

        var imported = Assert.Single(ConversationBackupService.Import(JsonSerializer.Serialize(new[] { source }, Options), Options));

        Assert.Equal(WorkspaceFileService.MaxContextFiles, imported.ContextFiles.Count);
    }

    [Fact]
    public void Import_marks_interrupted_assistant_turns_as_interrupted()
    {
        var json = """[{"Messages":[{"Role":"user","Content":"hello"},{"Role":"assistant","Content":""}]}]""";

        var imported = Assert.Single(ConversationBackupService.Import(json, Options));

        Assert.Equal("This request did not finish before it was imported.", imported.Messages[1].Content);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{}")]
    [InlineData("[null]")]
    [InlineData("[{\"Messages\":[{\"Role\":\"tool\",\"Content\":\"unsafe\"}]}]")]
    public void Import_rejects_empty_malformed_or_unsupported_backup_data(string json) =>
        Assert.Throws<InvalidDataException>(() => ConversationBackupService.Import(json, Options));
}
