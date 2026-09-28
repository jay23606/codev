using Codev;

namespace Codev.Tests;

public sealed class ShellCommandExecutionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Codev-shell-tests", Guid.NewGuid().ToString("N"));

    public ShellCommandExecutionTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task Runs_an_approved_command_with_the_selected_platform_shell()
    {
        var command = OperatingSystem.IsWindows()
            ? "Write-Output 'Codev shell smoke test'"
            : "printf 'Codev shell smoke test'";

        var result = await new WorkspaceFileService(_root).RunApprovedCommandAsync(command, TimeSpan.FromSeconds(20));

        Assert.Contains("Codev shell smoke test", result);
        Assert.Contains("Exit code: 0", result);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }
}
