using Codev;
using System.Diagnostics;

public sealed class ShellCommandResolverTests
{
    [Fact]
    public void Selects_powershell_on_windows_and_keeps_noninteractive_invocation()
    {
        var shell = ShellCommandResolver.Resolve(isWindows: true);
        var start = shell.CreateStartInfo("Write-Output ok", Environment.CurrentDirectory);

        Assert.Equal("PowerShell", shell.DisplayName);
        Assert.Equal("powershell.exe", shell.Executable);
        Assert.Equal(["-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", "Write-Output ok"], start.ArgumentList);
        Assert.False(start.UseShellExecute);
        Assert.True(start.CreateNoWindow);
        Assert.Equal(ProcessWindowStyle.Hidden, start.WindowStyle);
    }

    [Fact]
    public void Uses_the_configured_shell_override_on_unix()
    {
        var shell = ShellCommandResolver.Resolve(isWindows: false, configuredOverride: "/custom/zsh", userShell: "/bin/bash");
        var start = shell.CreateStartInfo("echo ok", Environment.CurrentDirectory);

        Assert.Equal("/custom/zsh", shell.Executable);
        Assert.Equal("zsh", shell.DisplayName);
        Assert.Equal(["-c", "echo ok"], start.ArgumentList);
    }

    [Fact]
    public void Uses_the_user_shell_then_a_portable_fallback_on_unix()
    {
        Assert.Equal("/bin/fish", ShellCommandResolver.Resolve(isWindows: false, userShell: "/bin/fish").Executable);
        Assert.Equal("bash", ShellCommandResolver.Resolve(isWindows: false, fallbackUnixShell: "bash").Executable);
        Assert.Equal("sh", ShellCommandResolver.Resolve(isWindows: false).Executable);
    }
}
