namespace Codev;

/// <summary>Determines whether file proposals require a human review for the selected project mode.</summary>
public static class ProjectFileChangePolicy
{
    public static bool RequiresReview(ProjectCommandPermissionMode mode) => mode != ProjectCommandPermissionMode.Auto;
}
