namespace Codev.Tests;

[CollectionDefinition("Shell process lifecycle", DisableParallelization = true)]
public sealed class ShellProcessLifecycleCollection
{
    public const string Name = "Shell process lifecycle";

    public static (string Command, Codev.ShellCommandSpec Shell) CreateLongRunningCommand() =>
        OperatingSystem.IsWindows()
            ? ("127.0.0.1", new Codev.ShellCommandSpec(Path.Combine(Environment.SystemDirectory, "ping.exe"), "ping.exe", ["-n", "30"]))
            : ("sleep 30", Codev.ShellCommandResolver.ResolveCurrent());
}
