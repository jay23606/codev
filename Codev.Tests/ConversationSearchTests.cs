using System.IO;

namespace Codev.Tests;

public sealed class ConversationSearchTests
{
    [Fact]
    public void Search_matches_titles_and_message_content_without_case_sensitivity()
    {
        var conversation = new Conversation
        {
            Title = "Feature planning",
            Messages = [new("user", "Can you explain the async queue?"), new("assistant", "It processes requests serially.")]
        };

        Assert.True(ConversationSearch.Matches(conversation, "FEATURE"));
        Assert.True(ConversationSearch.Matches(conversation, "ASYNC QUEUE"));
        Assert.False(ConversationSearch.Matches(conversation, "not present"));
        Assert.True(ConversationSearch.Matches(conversation, "  "));
    }

    [Fact]
    public void Search_excerpt_includes_context_around_the_first_message_match()
    {
        var conversation = new Conversation
        {
            Messages = [new("user", "Before the requested phrase, then important surrounding context, and finally after the phrase appears.")]
        };

        var excerpt = ConversationSearch.FindMessageExcerpt(conversation, "important surrounding");

        Assert.NotNull(excerpt);
        Assert.Contains("important surrounding", excerpt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Before", excerpt, StringComparison.Ordinal);
    }

    [Fact]
    public void Scope_is_current_workspace_by_default_and_can_include_every_project()
    {
        var projectConversation = new Conversation { ProjectPath = Path.Combine(Path.GetTempPath(), "codev-project") };
        var otherProjectConversation = new Conversation { ProjectPath = Path.Combine(Path.GetTempPath(), "another-project") };
        var unassignedConversation = new Conversation();

        Assert.True(ConversationSearch.IsInScope(projectConversation, projectConversation.ProjectPath, includeAllProjects: false));
        Assert.False(ConversationSearch.IsInScope(otherProjectConversation, projectConversation.ProjectPath, includeAllProjects: false));
        Assert.False(ConversationSearch.IsInScope(unassignedConversation, projectConversation.ProjectPath, includeAllProjects: false));
        Assert.True(ConversationSearch.IsInScope(otherProjectConversation, projectConversation.ProjectPath, includeAllProjects: true));
        Assert.True(ConversationSearch.IsInScope(unassignedConversation, projectConversation.ProjectPath, includeAllProjects: true));
    }

    [Fact]
    public void Scope_compares_workspace_paths_without_trailing_separator_differences()
    {
        var root = Path.Combine(Path.GetTempPath(), "codev-project");
        var conversation = new Conversation { ProjectPath = root + Path.DirectorySeparatorChar };

        Assert.True(ConversationSearch.IsInScope(conversation, root, includeAllProjects: false));
    }
}
