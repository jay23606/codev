namespace Codev.Tests;

public sealed class ConversationSidebarOrderingTests
{
    [Theory]
    [InlineData(false, "b,a,c")]
    [InlineData(true, "b,c,a")]
    public void TryMove_reorders_and_assigns_persistable_order(bool insertAfter, string expected)
    {
        var conversations = new List<Conversation>
        {
            new Conversation { Id = Guid.NewGuid(), Title = "a" },
            new Conversation { Id = Guid.NewGuid(), Title = "b" },
            new Conversation { Id = Guid.NewGuid(), Title = "c" }
        };

        var moved = ConversationSidebarOrdering.TryMove(conversations, conversations[0].Id, conversations[1].Id, insertAfter);

        Assert.True(moved);
        Assert.Equal(expected.Split(','), conversations.Select(item => item.Title));
        Assert.Equal(Enumerable.Range(0, 3).Select(index => (int?)index), conversations.Select(item => item.SidebarOrder));
    }

    [Fact]
    public void TryMove_rejects_missing_and_identical_items_without_changing_order()
    {
        var conversations = new List<Conversation> { new() { Title = "a" }, new() { Title = "b" } };
        var originalIds = conversations.Select(item => item.Id).ToArray();

        Assert.False(ConversationSidebarOrdering.TryMove(conversations, originalIds[0], originalIds[0], insertAfter: false));
        Assert.False(ConversationSidebarOrdering.TryMove(conversations, Guid.NewGuid(), originalIds[1], insertAfter: false));
        Assert.Equal(originalIds, conversations.Select(item => item.Id));
    }
}
