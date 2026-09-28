using System.Text.Json;

namespace Codev.Tests;

public sealed class CodeTaskToolExecutorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Codev-agent-tests", Guid.NewGuid().ToString("N"));
    private readonly Conversation _conversation = new();

    public CodeTaskToolExecutorTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task Rejected_new_file_proposal_leaves_the_project_unchanged()
    {
        CodeTaskFileProposal? reviewed = null;
        var executor = new CodeTaskToolExecutor(new WorkspaceFileService(_root), _conversation,
            proposal => { reviewed = proposal; return Task.FromResult(false); },
            _ => Task.FromResult(false));

        var result = await ExecuteAsync(executor, "create_file", """{"relative_path":"src/new.cs","content":"class NewFile {}"}""");

        Assert.Contains("Rejected by user", result);
        Assert.NotNull(reviewed);
        Assert.True(reviewed!.IsNewFile);
        Assert.Equal("src/new.cs", reviewed.RelativePath);
        Assert.False(File.Exists(Path.Combine(_root, "src", "new.cs")));
        Assert.Empty(_conversation.FileChanges);
    }

    [Fact]
    public async Task Listing_reading_and_searching_stay_inside_the_project()
    {
        Directory.CreateDirectory(Path.Combine(_root, "src"));
        File.WriteAllText(Path.Combine(_root, "src", "main.cs"), "class Main { const string Marker = \"inside\"; }\n");
        var executor = new CodeTaskToolExecutor(new WorkspaceFileService(_root), _conversation,
            _ => Task.FromResult(false), _ => Task.FromResult(false));

        var listing = await ExecuteAsync(executor, "list_files", """{"relative_directory":""}""");
        var content = await ExecuteAsync(executor, "read_file", """{"relative_path":"src/main.cs"}""");
        var matches = await ExecuteAsync(executor, "search_files", """{"query":"Marker"}""");

        var relativePath = Path.Combine("src", "main.cs");
        Assert.Contains(relativePath, listing);
        Assert.Contains("inside", content);
        Assert.Contains(relativePath + ":1", matches);
    }

    [Fact]
    public async Task Approved_new_file_creates_only_a_supported_project_file()
    {
        var executor = new CodeTaskToolExecutor(new WorkspaceFileService(_root), _conversation,
            proposal => Task.FromResult(proposal.IsNewFile), _ => Task.FromResult(false));

        var result = await ExecuteAsync(executor, "create_file", """{"relative_path":"new.js","content":"export const ready = true;"}""");

        Assert.Contains("created", result);
        Assert.Equal("export const ready = true;", File.ReadAllText(Path.Combine(_root, "new.js")));
        Assert.Equal("Create", Assert.Single(_conversation.FileChanges).Kind);
    }

    [Fact]
    public async Task Approved_replacement_creates_a_checkpoint_and_records_the_change()
    {
        var filePath = Path.Combine(_root, "Program.cs");
        File.WriteAllText(filePath, "class Old {}\n");
        CodeTaskFileProposal? reviewed = null;
        var executor = new CodeTaskToolExecutor(new WorkspaceFileService(_root), _conversation,
            proposal => { reviewed = proposal; return Task.FromResult(true); },
            _ => Task.FromResult(false));

        var result = await ExecuteAsync(executor, "write_file", """{"relative_path":"Program.cs","content":"class New {}\n"}""");

        Assert.Contains("checkpoint was saved", result);
        Assert.Equal("class Old {}\n", reviewed!.Before);
        Assert.Equal("class New {}\n", File.ReadAllText(filePath));
        var change = Assert.Single(_conversation.FileChanges);
        Assert.Equal("Edit", change.Kind);
        Assert.NotNull(change.CheckpointPath);
        Assert.True(File.Exists(change.CheckpointPath));
    }

    [Fact]
    public async Task File_change_during_review_is_not_overwritten()
    {
        var filePath = Path.Combine(_root, "Program.cs");
        File.WriteAllText(filePath, "class Original {}\n");
        var executor = new CodeTaskToolExecutor(new WorkspaceFileService(_root), _conversation,
            _ => { File.WriteAllText(filePath, "class ChangedExternally {}\n"); return Task.FromResult(true); },
            _ => Task.FromResult(false));

        var result = await ExecuteAsync(executor, "write_file", """{"relative_path":"Program.cs","content":"class Proposed {}\n"}""");

        Assert.StartsWith("Error:", result);
        Assert.Equal("class ChangedExternally {}\n", File.ReadAllText(filePath));
        Assert.Empty(_conversation.FileChanges);
    }

    [Fact]
    public async Task Shell_command_is_not_run_without_individual_approval()
    {
        CodeTaskCommandProposal? reviewed = null;
        var executor = new CodeTaskToolExecutor(new WorkspaceFileService(_root), _conversation,
            _ => Task.FromResult(false),
            proposal => { reviewed = proposal; return Task.FromResult(false); });

        var result = await ExecuteAsync(executor, "run_command", """{"command":"exit 27"}""");

        Assert.Contains("Rejected by user", result);
        Assert.NotNull(reviewed);
        Assert.Equal("exit 27", reviewed!.Command);
        Assert.Equal(Path.GetFullPath(_root), reviewed.ProjectPath);
    }

    [Fact]
    public async Task File_tools_reject_path_traversal_and_secret_files()
    {
        var executor = new CodeTaskToolExecutor(new WorkspaceFileService(_root), _conversation,
            _ => Task.FromResult(true), _ => Task.FromResult(true));

        var traversal = await ExecuteAsync(executor, "read_file", """{"relative_path":"../outside.cs"}""");
        var secret = await ExecuteAsync(executor, "create_file", """{"relative_path":".env","content":"SECRET=value"}""");

        Assert.StartsWith("Error:", traversal);
        Assert.StartsWith("Error:", secret);
        Assert.False(File.Exists(Path.Combine(_root, ".env")));
    }

    private static async Task<string> ExecuteAsync(CodeTaskToolExecutor executor, string name, string json)
    {
        using var document = JsonDocument.Parse(json);
        return await executor.ExecuteAsync(name, document.RootElement);
    }

    public void Dispose()
    {
        var localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var checkpointFolder = Path.GetFullPath(Path.Combine(localData, "Codev", "checkpoints", _conversation.Id.ToString("N")));
        var checkpointRoot = Path.GetFullPath(Path.Combine(localData, "Codev", "checkpoints")) + Path.DirectorySeparatorChar;
        if (checkpointFolder.StartsWith(checkpointRoot, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            foreach (var change in _conversation.FileChanges)
            {
                if (change.CheckpointPath is not { } checkpoint) continue;
                var fullCheckpoint = Path.GetFullPath(checkpoint);
                if (fullCheckpoint.StartsWith(checkpointFolder + Path.DirectorySeparatorChar, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) && File.Exists(fullCheckpoint))
                    File.Delete(fullCheckpoint);
            }
            if (Directory.Exists(checkpointFolder) && !Directory.EnumerateFileSystemEntries(checkpointFolder).Any()) Directory.Delete(checkpointFolder);
        }
        var expectedRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "Codev-agent-tests")) + Path.DirectorySeparatorChar;
        var fullRoot = Path.GetFullPath(_root);
        if (fullRoot.StartsWith(expectedRoot, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) && Directory.Exists(fullRoot))
            Directory.Delete(fullRoot, recursive: true);
    }
}
