namespace Codev.Tests;

public sealed class ChildSessionCreationPolicyTests
{
    [Fact]
    public void Allows_delegation_while_the_root_conversation_is_generating_its_current_turn()
    {
        Assert.True(ChildSessionCreationPolicy.CanCreateChild(conversationIsBusy: true, isCurrentParentTurn: true));
    }

    [Fact]
    public void Rejects_manual_child_creation_when_the_parent_is_busy_for_another_reason()
    {
        Assert.False(ChildSessionCreationPolicy.CanCreateChild(conversationIsBusy: true, isCurrentParentTurn: false));
    }

    [Fact]
    public void Allows_manual_child_creation_when_the_parent_is_idle()
    {
        Assert.True(ChildSessionCreationPolicy.CanCreateChild(conversationIsBusy: false, isCurrentParentTurn: false));
    }
}
