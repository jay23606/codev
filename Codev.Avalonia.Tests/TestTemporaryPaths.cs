using System.Runtime.CompilerServices;

namespace Codev.Avalonia.Tests;

internal static class TestTemporaryPaths
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        if (!OperatingSystem.IsMacOS()) return;

        // macOS commonly exposes its temporary directory through /var -> /private/var.
        // Project path guards intentionally reject linked ancestors, so tests must use
        // the physical temp path rather than accidentally exercising that policy.
        var fullPath = Path.GetFullPath(Path.GetTempPath());
        var current = Path.GetPathRoot(fullPath)!;
        foreach (var segment in fullPath[current.Length..].Split(Path.DirectorySeparatorChar,
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            var directory = new DirectoryInfo(current);
            if (!directory.Exists) break;
            current = directory.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? current;
        }

        Environment.SetEnvironmentVariable("TMPDIR", Path.TrimEndingDirectorySeparator(current));
    }
}
