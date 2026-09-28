using System.Text.Json;
using System.Text.RegularExpressions;

namespace Codev;

public sealed record CodeTaskFileProposal(string RelativePath, string Before, string After, bool IsNewFile, string? ProposedPatch = null);
public sealed record CodeTaskCommandProposal(string Command, string ProjectPath, string ShellName, bool IsVerification = false);

/// <summary>Executes the bounded Code task tools. Mutations and shell commands require UI-provided approval.</summary>
public sealed class CodeTaskToolExecutor(
    WorkspaceFileService files,
    Conversation conversation,
    Func<CodeTaskFileProposal, Task<bool>> reviewFile,
    Func<CodeTaskCommandProposal, Task<bool>> approveCommand,
    IProgress<TimeSpan>? commandProgress = null,
    Action<string>? status = null,
    int maxRepairAttempts = 2)
{
    private int _failedVerifications;

    public async Task<string> ExecuteAsync(string name, JsonElement arguments, CancellationToken cancellationToken = default)
    {
        string Arg(string key) => arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty(key, out var value)
            ? value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : value.ToString()
            : "";
        try
        {
            return name switch
            {
                "list_files" => string.Join("\n", files.ListFiles(Arg("relative_directory"), 160)),
                "read_file" => Truncate(await files.ReadFileAsync(Arg("relative_path"), cancellationToken)),
                "search_files" => string.Join("\n", await files.SearchFilesAsync(Arg("query"), cancellationToken)),
                "create_file" => await CreateFileAsync(Arg("relative_path"), Arg("content"), cancellationToken),
                "write_file" => await WriteFileAsync(Arg("relative_path"), Arg("content"), cancellationToken),
                "apply_patch" => await ApplyPatchAsync(Arg("relative_path"), Arg("patch"), cancellationToken),
                "verify_command" => await VerifyCommandAsync(Arg("command"), cancellationToken),
                "run_command" => await RunCommandAsync(Arg("command"), cancellationToken),
                _ => "Error: this tool is not available."
            };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return "Error: " + ex.Message; }
    }

    private async Task<string> CreateFileAsync(string relativePath, string content, CancellationToken cancellationToken)
    {
        if (RepairBudgetExhausted) return RepairLimitMessage;
        if (string.IsNullOrWhiteSpace(relativePath)) return "Error: a project-relative file path is required.";
        var fullPath = files.ResolvePath(relativePath);
        if (File.Exists(fullPath)) return "Rejected: a file already exists here. Use write_file to propose an edit.";
        if (!await reviewFile(new CodeTaskFileProposal(relativePath, "", content, IsNewFile: true)))
            return "Rejected by user; no file was created.";
        await files.CreateFileAtomicAsync(relativePath, content, cancellationToken);
        conversation.FileChanges.Add(new FileChangeRecord(relativePath, null, DateTimeOffset.Now, "Create", PreviousFileExisted: false));
        return "Approved and created the new project file.";
    }

    private async Task<string> WriteFileAsync(string relativePath, string content, CancellationToken cancellationToken)
    {
        if (RepairBudgetExhausted) return RepairLimitMessage;
        if (string.IsNullOrWhiteSpace(relativePath)) return "Error: a project-relative file path is required.";
        var fullPath = files.ResolvePath(relativePath);
        if (!File.Exists(fullPath)) return "Rejected: no file exists at this path. Use create_file to propose a new file.";
        var original = await files.ReadFileSnapshotAsync(relativePath, cancellationToken);
        return await ReviewAndWriteAsync(relativePath, original, content, proposedPatch: null, cancellationToken);
    }

    private async Task<string> ApplyPatchAsync(string relativePath, string patch, CancellationToken cancellationToken)
    {
        if (RepairBudgetExhausted) return RepairLimitMessage;
        if (string.IsNullOrWhiteSpace(relativePath)) return "Error: a project-relative file path is required.";
        if (string.IsNullOrWhiteSpace(patch)) return "Error: a unified-diff patch is required.";
        var fullPath = files.ResolvePath(relativePath);
        if (!File.Exists(fullPath)) return "Rejected: no file exists at this path. Use create_file to propose a new file.";
        var original = await files.ReadFileSnapshotAsync(relativePath, cancellationToken);
        var content = UnifiedDiffApplier.Apply(original.Content, patch);
        return await ReviewAndWriteAsync(relativePath, original, content, patch, cancellationToken);
    }

    private async Task<string> ReviewAndWriteAsync(string relativePath, FileSnapshot original, string content, string? proposedPatch, CancellationToken cancellationToken)
    {
        if (!await reviewFile(new CodeTaskFileProposal(relativePath, original.Content, content, IsNewFile: false, proposedPatch)))
            return "Rejected by user; the file was left unchanged.";
        var checkpoint = await files.CreateCheckpointAsync(relativePath, conversation.Id, cancellationToken, original.Sha256);
        await files.WriteFileAtomicAsync(relativePath, content, cancellationToken, original.Sha256);
        if (checkpoint is not null) conversation.FileChanges.Add(new FileChangeRecord(relativePath, checkpoint, DateTimeOffset.Now, "Edit"));
        return "Approved and applied. A local checkpoint was saved before the change.";
    }

    private async Task<string> RunCommandAsync(string command, CancellationToken cancellationToken)
    {
        if (RepairBudgetExhausted) return RepairLimitMessage;
        if (string.IsNullOrWhiteSpace(command) || command.Length > 4000)
            return "Rejected: command must contain 1–4,000 characters.";
        var shell = ShellCommandResolver.ResolveCurrent();
        if (!await approveCommand(new CodeTaskCommandProposal(command, files.Root, shell.DisplayName)))
            return "Rejected by user; the command was not run.";
        status?.Invoke($"Code task · starting approved {shell.DisplayName} command…");
        var progress = new Progress<TimeSpan>(elapsed =>
            status?.Invoke($"Code task · command running · {elapsed:mm\\:ss}"));
        try { return await files.RunApprovedCommandAsync(command, TimeSpan.FromMinutes(3), cancellationToken, commandProgress ?? progress); }
        finally { status?.Invoke("Code task · Thinking…"); }
    }

    private bool RepairBudgetExhausted => _failedVerifications > Math.Clamp(maxRepairAttempts, 0, 3);
    private const string RepairLimitMessage = "Rejected: the verification repair limit has been reached for this task. Further Codev file edits and commands are blocked; report the remaining failure.";

    private async Task<string> VerifyCommandAsync(string command, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(command) || command.Length > 4000)
            return "Rejected: verification command must contain 1–4,000 characters.";
        if (RepairBudgetExhausted) return RepairLimitMessage;
        var shell = ShellCommandResolver.ResolveCurrent();
        if (!await approveCommand(new CodeTaskCommandProposal(command, files.Root, shell.DisplayName, IsVerification: true)))
            return "Verification rejected by user; it was not run and no result is available.";

        status?.Invoke("Code task · running approved verification…");
        try
        {
            var progress = commandProgress ?? new Progress<TimeSpan>(elapsed =>
                status?.Invoke($"Code task · verification running · {elapsed:mm\\:ss}"));
            var output = await files.RunApprovedCommandAsync(command, TimeSpan.FromMinutes(3), cancellationToken, progress);
            var exitMatch = Regex.Match(output, @"(?:^|\n)Exit code: (-?\d+)\s*$", RegexOptions.CultureInvariant);
            if (exitMatch.Success && int.TryParse(exitMatch.Groups[1].Value, out var exitCode) && exitCode == 0)
                return "Verification PASSED (exit code 0).\n" + Truncate(output, 8000);

            _failedVerifications++;
            var limit = Math.Clamp(maxRepairAttempts, 0, 3);
            var budget = RepairBudgetExhausted
                ? "\nRepair limit reached: further Codev file edits and commands are blocked. Report the remaining failure."
                : $"\nVerification failures: {_failedVerifications}; repair attempts allowed: {limit}. You may make a reviewed fix and request verification again. Each run needs approval.";
            return (exitMatch.Success
                ? $"Verification FAILED (exit code {exitMatch.Groups[1].Value}).\n"
                : "Verification FAILED (no successful exit status; command may have timed out).\n") + Truncate(output, 8000) + budget;
        }
        finally { status?.Invoke("Code task · Thinking…"); }
    }

    private static string Truncate(string value, int max = 6000) => value.Length <= max ? value : value[..max] + "\n… [tool output truncated]";
}
