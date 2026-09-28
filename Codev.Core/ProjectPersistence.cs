namespace Codev;

/// <summary>Creates detached snapshots of project metadata before asynchronous persistence.</summary>
public static class ProjectPersistence
{
    public static List<WorkspaceProject> CreateSnapshot(IEnumerable<WorkspaceProject> projects)
    {
        ArgumentNullException.ThrowIfNull(projects);
        return projects.Select(project => new WorkspaceProject
        {
            Id = project.Id,
            Name = project.Name,
            Path = project.Path,
            IsPinned = project.IsPinned,
            Instructions = project.Instructions,
            Knowledge = project.Knowledge,
            ContextExclusions = project.ContextExclusions is null ? [] : [.. project.ContextExclusions],
            LastOpenedAt = project.LastOpenedAt
        }).ToList();
    }
}
