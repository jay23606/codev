namespace Codev.Tests;

public sealed class ConversationHistoryClearServiceTests
{
    [Fact]
    public void Clearing_removes_messages_and_draft_but_keeps_project_settings_and_file_history()
    {
        var conversation = new Conversation
        {
            Title = "Bug investigation",
            Draft = "unsent text",
            Model = "claude-sonnet",
            Provider = CloudModelProviders.Anthropic,
            ProjectPath = @"C:\work\repo",
            ContextFiles = ["src/App.cs"],
            IsPinned = true,
            LastPromptTokens = 1200,
            LastPromptOutputTokens = 80,
            LastPromptContext = 8192,
            LastPromptModel = "claude-sonnet",
            CompactionSummary = "Old summary",
            CompactionThroughMessageCount = 2,
            TaskChecklist = [new TaskChecklistItem(Guid.NewGuid(), "Old task")],
            Messages = [new("user", "hello"), new("assistant", "world")],
            FileChanges = [new("src/App.cs", "checkpoint.json", DateTimeOffset.UnixEpoch, "Edit")]
        };

        Assert.True(ConversationHistoryClearService.Clear(conversation));

        Assert.Empty(conversation.Messages);
        Assert.Equal("New conversation", conversation.Title);
        Assert.Empty(conversation.Draft);
        Assert.Equal(0, conversation.LastPromptTokens);
        Assert.Null(conversation.LastPromptOutputTokens);
        Assert.Equal(0, conversation.LastPromptContext);
        Assert.Empty(conversation.LastPromptModel);
        Assert.Empty(conversation.CompactionSummary);
        Assert.Equal(0, conversation.CompactionFromMessageCount);
        Assert.Equal(0, conversation.CompactionThroughMessageCount);
        Assert.Empty(conversation.TaskChecklist);
        Assert.Equal("claude-sonnet", conversation.Model);
        Assert.Equal(CloudModelProviders.Anthropic, conversation.Provider);
        Assert.Equal(@"C:\work\repo", conversation.ProjectPath);
        Assert.Equal(["src/App.cs"], conversation.ContextFiles);
        Assert.True(conversation.IsPinned);
        Assert.Single(conversation.FileChanges);
    }

    [Fact]
    public void Clearing_is_refused_while_generating_or_with_queued_turns()
    {
        var conversation = new Conversation { Messages = [new("user", "keep me")] };
        Assert.False(ConversationHistoryClearService.Clear(conversation, isGenerating: true));
        conversation.PendingRequestCount = 1;
        Assert.False(ConversationHistoryClearService.Clear(conversation));
        conversation.PendingRequestCount = 0;
        conversation.PendingTurns = [new(1, "model", 0, false, false, null, [], [], DateTimeOffset.UnixEpoch)];
        Assert.False(ConversationHistoryClearService.Clear(conversation));
        Assert.Equal("keep me", conversation.Messages[0].Content);
    }
}
