namespace Codev.Tests;

public sealed class ProjectPersistenceTests
{
    [Fact]
    public void Snapshot_preserves_metadata_and_detaches_context_exclusions()
    {
        var source = new WorkspaceProject
        {
            Name = "Codev",
            Path = @"C:\work\Codev",
            IsPinned = true,
            Instructions = "Use small changes.",
            Knowledge = "The app is local-first.",
            ContextExclusions = ["private", "*.secret"],
            LastOpenedAt = DateTimeOffset.UnixEpoch
        };

        var snapshot = Assert.Single(ProjectPersistence.CreateSnapshot([source]));
        source.Name = "Changed";
        source.ContextExclusions[0] = "public";

        Assert.Equal("Codev", snapshot.Name);
        Assert.Equal(@"C:\work\Codev", snapshot.Path);
        Assert.True(snapshot.IsPinned);
        Assert.Equal("Use small changes.", snapshot.Instructions);
        Assert.Equal("The app is local-first.", snapshot.Knowledge);
        Assert.Equal(["private", "*.secret"], snapshot.ContextExclusions);
        Assert.Equal(DateTimeOffset.UnixEpoch, snapshot.LastOpenedAt);
    }
}
