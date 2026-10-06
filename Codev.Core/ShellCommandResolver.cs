using System.Diagnostics;

namespace Codev;

public sealed record ShellCommandSpec(string Executable, string DisplayName, IReadOnlyList<string> PrefixArguments)
{
    public ProcessStartInfo CreateStartInfo(string command, string workingDirectory)
    {
        var start = new ProcessStartInfo
        {
            FileName = Executable,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in PrefixArguments) start.ArgumentList.Add(argument);
        start.ArgumentList.Add(command);
        return start;
    }
}

public static class ShellCommandResolver
{
    public const string OverrideEnvironmentVariable = "CODEV_SHELL";

    public static ShellCommandSpec ResolveCurrent() => Resolve(
        OperatingSystem.IsWindows(),
        Environment.GetEnvironmentVariable(OverrideEnvironmentVariable),
        Environment.GetEnvironmentVariable("SHELL"),
        File.Exists("/bin/bash") || File.Exists("/usr/bin/bash") ? "bash" : "sh");

    public static ShellCommandSpec Resolve(bool isWindows, string? configuredOverride = null, string? userShell = null, string? fallbackUnixShell = null)
    {
        if (isWindows)
        {
            var executable = FirstNonEmpty(configuredOverride, "powershell.exe");
            return new ShellCommandSpec(executable, "PowerShell", ["-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command"]);
        }

        var unixShell = FirstNonEmpty(configuredOverride, userShell, fallbackUnixShell, "sh");
        var name = Path.GetFileName(unixShell).ToLowerInvariant() switch
        {
            "bash" => "bash",
            "zsh" => "zsh",
            "fish" => "fish",
            "sh" => "sh",
            _ => Path.GetFileName(unixShell)
        };
        return new ShellCommandSpec(unixShell, name, ["-c"]);
    }

    private static string FirstNonEmpty(params string?[] values) =>
        values.First(value => !string.IsNullOrWhiteSpace(value))!;
}
