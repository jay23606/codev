using System.Diagnostics;

namespace Codev;

public sealed record FolderOpenCommand(string Executable, string Argument)
{
    public ProcessStartInfo CreateStartInfo()
    {
        var start = new ProcessStartInfo(Executable) { UseShellExecute = false };
        start.ArgumentList.Add(Argument);
        return start;
    }
}

public static class FolderOpenCommandResolver
{
    public static FolderOpenCommand Resolve(string operatingSystem, string path) => operatingSystem switch
    {
        "Windows" => new("explorer.exe", path),
        "macOS" => new("open", path),
        "Linux" => new("xdg-open", path),
        _ => throw new ArgumentOutOfRangeException(nameof(operatingSystem), operatingSystem, "Unsupported operating system.")
    };

    public static FolderOpenCommand ResolveCurrent(string path) =>
        Resolve(OperatingSystem.IsWindows() ? "Windows" : OperatingSystem.IsMacOS() ? "macOS" : "Linux", path);
}
