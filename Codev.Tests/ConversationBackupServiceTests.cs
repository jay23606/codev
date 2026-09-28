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
            Model = "devstral-small-2-64k",
            ProjectPath = @"C:\work\sample",
            PendingRequestCount = 3,
            Messages = [new("user", "Why does this fail?"), new("assistant", "I will trace the cause.")]
        };

        var backup = ConversationBackupService.Export([source], Options);
        var imported = Assert.Single(ConversationBackupService.Import(backup, Options));

        Assert.NotEqual(source.Id, imported.Id);
        Assert.Equal(source.Title, imported.Title);
        Assert.Equal(source.Model, imported.Model);
        Assert.Equal(source.ProjectPath, imported.ProjectPath);
        Assert.Equal(source.Messages, imported.Messages);
        Assert.Equal(0, imported.PendingRequestCount);
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
