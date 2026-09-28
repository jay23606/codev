namespace Codev.Tests;

public sealed class ProjectConversationResumeTests
{
    [Fact]
    public void Chooses_most_recent_nonarchived_conversation_for_same_normalized_project_path()
    {
        var root = Path.Combine(Path.GetTempPath(), "codev-resume", Guid.NewGuid().ToString("N"));
        var older = new Conversation { Title = "Older", ProjectPath = root, UpdatedAt = DateTimeOffset.UnixEpoch };
        var newer = new Conversation { Title = "Newer", ProjectPath = Path.Combine(root, "."), UpdatedAt = DateTimeOffset.UnixEpoch.AddHours(1) };
        var archived = new Conversation { Title = "Archived", ProjectPath = root, IsArchived = true, UpdatedAt = DateTimeOffset.UnixEpoch.AddHours(2) };

        var recent = ProjectConversationResume.FindMostRecent([older, newer, archived], root);

        Assert.Same(newer, recent);
    }

    [Fact]
    public void Excludes_current_conversation_and_other_project_paths()
    {
        var root = Path.Combine(Path.GetTempPath(), "codev-resume", Guid.NewGuid().ToString("N"));
        var current = new Conversation { ProjectPath = root };
        var another = new Conversation { ProjectPath = Path.Combine(root, "other") };

        var recent = ProjectConversationResume.FindMostRecent([current, another], root, current.Id);

        Assert.Null(recent);
    }
}
