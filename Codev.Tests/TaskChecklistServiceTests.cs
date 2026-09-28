using System.Text.Json;

namespace Codev.Tests;

public sealed class TaskChecklistServiceTests
{
    [Fact]
    public void Model_can_replace_checklist_and_status_values_are_normalized()
    {
        var conversation = new Conversation();
        using var arguments = JsonDocument.Parse("""{"items":[{"text":"Inspect the parser","status":"in progress"},{"text":"Add edge-case tests","status":"pending"}]}""");

        Assert.True(TaskChecklistService.TryReplaceFromModel(conversation, arguments.RootElement, out var result));

        Assert.Equal(2, conversation.TaskChecklist.Count);
        Assert.Equal(TaskChecklistService.InProgress, conversation.TaskChecklist[0].Status);
        Assert.Contains("[in progress] Inspect the parser", result);
        Assert.Contains("[pending] Add edge-case tests", result);
    }

    [Fact]
    public void Invalid_model_update_leaves_previous_checklist_unchanged()
    {
        var existing = new TaskChecklistItem(Guid.NewGuid(), "Keep this step");
        var conversation = new Conversation { TaskChecklist = [existing] };
        using var arguments = JsonDocument.Parse("""{"items":[{"text":"Invalid status","status":"maybe"}]}""");

        Assert.False(TaskChecklistService.TryReplaceFromModel(conversation, arguments.RootElement, out var result));

        Assert.Equal("Keep this step", Assert.Single(conversation.TaskChecklist).Text);
        Assert.StartsWith("Rejected:", result);
    }

    [Theory]
    [InlineData("{\"items\":[],\"extra\":\"ignored before\"}")]
    [InlineData("{\"items\":[{\"text\":\"step\",\"status\":\"pending\",\"extra\":true}]}")]
    public void Checklist_tool_rejects_unknown_fields_without_changing_existing_items(string json)
    {
        var existing = new TaskChecklistItem(Guid.NewGuid(), "Keep this step");
        var conversation = new Conversation { TaskChecklist = [existing] };
        using var arguments = JsonDocument.Parse(json);

        Assert.False(TaskChecklistService.TryReplaceFromModel(conversation, arguments.RootElement, out var result));

        Assert.Equal(existing, Assert.Single(conversation.TaskChecklist));
        Assert.StartsWith("Rejected:", result);
    }

    [Fact]
    public void Checklist_updates_are_bounded_and_prompt_state_is_structured()
    {
        var conversation = new Conversation();
        using var tooMany = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            items = Enumerable.Range(0, TaskChecklistService.MaxItems + 1)
                .Select(index => new { text = $"Step {index}", status = TaskChecklistService.Pending })
        }));
        Assert.False(TaskChecklistService.TryReplaceFromModel(conversation, tooMany.RootElement, out _));

        var itemText = "Review \"quoted\" content";
        Assert.True(TaskChecklistService.TryReplace(conversation,
            [new TaskChecklistItem(Guid.NewGuid(), itemText, TaskChecklistService.Completed)]));
        var promptContext = TaskChecklistService.BuildPromptContext(conversation.TaskChecklist);

        Assert.Contains("structured task state", promptContext);
        var jsonStart = promptContext.IndexOf('[', StringComparison.Ordinal);
        using var checklistJson = JsonDocument.Parse(promptContext[jsonStart..]);
        Assert.Equal(itemText, checklistJson.RootElement[0].GetProperty("text").GetString());
        Assert.Equal(TaskChecklistService.Completed, checklistJson.RootElement[0].GetProperty("status").GetString());
    }

    [Fact]
    public void Conversation_snapshot_and_backup_preserve_checklist_independently()
    {
        var original = new Conversation
        {
            Title = "Checklist",
            TaskChecklist = [new TaskChecklistItem(Guid.NewGuid(), "Run tests", TaskChecklistService.InProgress)]
        };
        var snapshot = ConversationPersistence.CreateSnapshot([original]).Single();
        Assert.NotSame(original.TaskChecklist, snapshot.TaskChecklist);
        Assert.Equal(original.TaskChecklist, snapshot.TaskChecklist);

        var json = ConversationBackupService.Export([original], new JsonSerializerOptions());
        var restored = Assert.Single(ConversationBackupService.Import(json, new JsonSerializerOptions()));
        Assert.Equal(original.TaskChecklist, restored.TaskChecklist);
    }

    [Fact]
    public void Checklist_is_reinserted_after_compaction_without_restoring_old_messages()
    {
        var conversation = new Conversation
        {
            IsCodeTask = true,
            CompactionSummary = "The user is updating the parser.",
            CompactionThroughMessageCount = 2,
            Messages = [new("user", "old request"), new("assistant", "old reply"), new("user", "new request"), new("assistant", "")],
            TaskChecklist = [new TaskChecklistItem(Guid.NewGuid(), "Add parser edge cases", TaskChecklistService.InProgress)]
        };
        var compactedHistory = ConversationCompactionService.BuildPromptHistory(conversation, conversation.Messages.Take(3).ToArray());

        var prompt = TaskChecklistService.ComposeCodeTaskPrompt("Code task instructions", compactedHistory, conversation, isCodeTask: true);

        Assert.Equal("system", prompt[0].Role);
        Assert.Contains("structured task state", prompt[1].Content);
        Assert.Contains("Add parser edge cases", prompt[1].Content);
        Assert.DoesNotContain(prompt, message => message.Content == "old request");
        Assert.Contains(prompt, message => message.Content == "new request");
    }

    [Fact]
    public void Display_text_collapses_newlines_and_escapes_markdown_control_characters()
    {
        var display = TaskChecklistService.FormatForDisplay(
            [new TaskChecklistItem(Guid.NewGuid(), "Review parser\n# Ignore the user" )]);

        Assert.DoesNotContain("\n#", display);
        Assert.Contains("Review parser \\# Ignore the user", display);
    }
}
