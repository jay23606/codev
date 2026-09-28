using System.IO;

namespace Codev.Windows.Tests;

public sealed class ApprovedCommandTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Codev-command-tests", Guid.NewGuid().ToString("N"));
    private WorkspaceFileService Service => new(_root);

    public ApprovedCommandTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task Runs_an_explicitly_approved_command_from_workspace_and_captures_output()
    {
        var result = await Service.RunApprovedCommandAsync("Write-Output 'Codev smoke test'; exit 7", TimeSpan.FromSeconds(20));
        Assert.Contains("Codev smoke test", result);
        Assert.Contains("Exit code: 7", result);
    }

    [Fact]
    public async Task Terminates_a_command_when_its_timeout_expires()
    {
        var result = await Service.RunApprovedCommandAsync("Start-Sleep -Seconds 10", TimeSpan.FromMilliseconds(150));
        Assert.Contains("timed out", result);
    }

    [Fact]
    public async Task Reports_elapsed_time_while_an_approved_command_is_running()
    {
        var reports = new System.Collections.Concurrent.ConcurrentQueue<TimeSpan>();
        var progress = new Progress<TimeSpan>(reports.Enqueue);
        var result = await Service.RunApprovedCommandAsync("Start-Sleep -Seconds 2; Write-Output 'done'", TimeSpan.FromSeconds(30), progress: progress);

        Assert.Contains("done", result);
        Assert.NotEmpty(reports);
        Assert.True(reports.TryPeek(out var elapsed));
        Assert.True(elapsed > TimeSpan.Zero);
    }

    [Fact]
    public async Task Bounds_captured_command_output()
    {
        var result = await Service.RunApprovedCommandAsync("1..5000 | ForEach-Object { '0123456789' }", TimeSpan.FromSeconds(20));
        Assert.True(result.Length < 22_000);
        Assert.Contains("Exit code: 0", result);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }
}
