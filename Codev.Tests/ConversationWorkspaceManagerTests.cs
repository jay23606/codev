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

    [Fact]
    public async Task Startup_auto_defaults_apply_only_to_private_conversation_workspaces()
    {
        var manager = new ConversationWorkspaceManager(_root);
        var workspaceId = Guid.NewGuid();
        var workspace = manager.GetOrCreateWorkspace(workspaceId);
        var attachedId = Guid.NewGuid();
        var attached = Path.Combine(_root, "attached-project");
        Directory.CreateDirectory(attached);
        var otherWorkspace = manager.GetWorkspacePath(Guid.NewGuid());
        var nestedId = Guid.NewGuid();
        var nested = Path.Combine(workspace, "nested");
        Directory.CreateDirectory(nested);

        var results = manager.FindExistingConversationWorkspaces(
        [
            new Conversation { Id = workspaceId, ProjectPath = workspace },
            new Conversation { Id = workspaceId, ProjectPath = workspace },
            new Conversation { Id = attachedId, ProjectPath = attached },
            new Conversation { Id = attachedId, ProjectPath = otherWorkspace },
            new Conversation { Id = nestedId, ProjectPath = nested }
        ]);

        Assert.Equal([workspace], results);
        var permissions = ProjectCommandPermissionRegistry.Load(Path.Combine(_root, "command-permissions.json"));
        foreach (var path in results)
            if (!permissions.HasProjectSettings(path))
                await permissions.SetModeAsync(path, ProjectCommandPermissionMode.Auto);

        Assert.Equal(ProjectCommandPermissionMode.Auto, permissions.GetMode(workspace));
        Assert.Equal(ProjectCommandPermissionMode.AskEveryTime, permissions.GetMode(attached));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
