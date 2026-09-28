namespace Codev.Tests;

public sealed class ConversationStartupSelectionTests
{
    [Fact]
    public void Restores_last_active_non_archived_conversation_even_if_another_was_updated_later()
    {
        var lastActive = new Conversation { UpdatedAt = DateTimeOffset.UnixEpoch };
        var laterUpdated = new Conversation { UpdatedAt = DateTimeOffset.UnixEpoch.AddDays(1) };

        var selected = ConversationStartupSelection.Choose([laterUpdated, lastActive], lastActive.Id);

        Assert.Same(lastActive, selected);
    }

    [Fact]
    public void Falls_back_to_most_recent_non_archived_when_saved_selection_is_missing_or_archived()
    {
        var recent = new Conversation { UpdatedAt = DateTimeOffset.UnixEpoch.AddDays(2) };
        var archived = new Conversation { IsArchived = true, UpdatedAt = DateTimeOffset.UnixEpoch.AddDays(3) };
        var older = new Conversation { UpdatedAt = DateTimeOffset.UnixEpoch };

        Assert.Same(recent, ConversationStartupSelection.Choose([older, archived, recent], Guid.NewGuid()));
        Assert.Same(recent, ConversationStartupSelection.Choose([recent, older, archived], archived.Id));
    }

    [Fact]
    public void Chooses_most_recent_archived_conversation_only_when_every_conversation_is_archived()
    {
        var older = new Conversation { IsArchived = true, UpdatedAt = DateTimeOffset.UnixEpoch };
        var newer = new Conversation { IsArchived = true, UpdatedAt = DateTimeOffset.UnixEpoch.AddDays(1) };

        Assert.Same(newer, ConversationStartupSelection.Choose([older, newer], null));
    }

    [Fact]
    public void Returns_null_for_empty_list() => Assert.Null(ConversationStartupSelection.Choose([], null));
}
