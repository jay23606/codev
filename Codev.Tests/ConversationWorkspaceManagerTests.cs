using Codev;

public sealed class ConversationWorkspaceManagerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "codev-workspaces-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void CreatesStableDistinctWorkspacePerConversation()
    {
        var manager = new ConversationWorkspaceManager(_root);
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();

        var first = manager.GetOrCreateWorkspace(firstId);
        var second = manager.GetOrCreateWorkspace(secondId);

        Assert.True(Directory.Exists(first));
        Assert.Equal(first, manager.GetOrCreateWorkspace(firstId));
        Assert.NotEqual(first, second);
        Assert.Equal(Path.Combine(_root, "Codev", "workspaces"), Directory.GetParent(first)!.FullName);
        Assert.True(manager.IsManagedWorkspace(first));
        Assert.True(manager.IsConversationWorkspace(firstId, first));
        Assert.False(manager.IsConversationWorkspace(secondId, first));
        Assert.False(manager.IsConversationWorkspace(Guid.Empty, first));
    }

    [Fact]
    public void DoesNotTreatArbitraryOrNestedPathsAsManagedWorkspaces()
    {
        var manager = new ConversationWorkspaceManager(_root);
        var arbitrary = Path.Combine(_root, "project");
        var nested = Path.Combine(manager.GetOrCreateWorkspace(Guid.NewGuid()), "nested");

        Assert.False(manager.IsManagedWorkspace(arbitrary));
        Assert.False(manager.IsManagedWorkspace(nested));
        Assert.False(manager.IsManagedWorkspace(""));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
