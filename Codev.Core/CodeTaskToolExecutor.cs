using System.Text.Json;
using System.Text.RegularExpressions;

namespace Codev;

public sealed record CodeTaskFileProposal(string RelativePath, string Before, string After, bool IsNewFile, string? ProposedPatch = null,
    IReadOnlyList<string>? ContextSources = null);
public sealed record CodeTaskCommandProposal(string Command, string ProjectPath, string ShellName, bool IsVerification = false,
    IReadOnlyList<string>? ContextSources = null, string? MatchingUntrustedSource = null);

/// <summary>Executes the bounded Code task tools. Mutations and shell commands require UI-provided approval.</summary>
public sealed class CodeTaskToolExecutor(
    WorkspaceFileService files,
    Conversation conversation,
    Func<CodeTaskFileProposal, Task<bool>> reviewFile,
    Func<CodeTaskCommandProposal, Task<bool>> approveCommand,
    IProgress<TimeSpan>? commandProgress = null,
    Action<string>? status = null,
    int maxRepairAttempts = 2,
    IEnumerable<string>? initialContextSources = null,
    Func<CodeTaskCommandProposal, Task<CommandApprovalOutcome>>? permissionApproval = null)
{
    private int _failedVerifications;
    private readonly List<(string Source, string Content)> _untrustedContents = [];
    private readonly List<string> _contextSources = initialContextSources?
        .Where(source => !string.IsNullOrWhiteSpace(source))
        .Select(ShortenSource)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Take(20)
        .ToList() ?? [];

    public async Task<string> ExecuteAsync(string name, JsonElement arguments, CancellationToken cancellationToken = default)
    {
        string Arg(string key) => arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty(key, out var value)
            ? value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : value.ToString()
            : "";
        try
        {
            return name switch
            {
                "list_files" => UntrustedToolOutput.Format("project file listing", string.Join("\n", files.ListFiles(Arg("relative_directory"), 160))),
                "read_file" => await ReadFileAsync(Arg("relative_path"), cancellationToken),
                "search_files" => await SearchFilesAsync(Arg("query"), cancellationToken),
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

    private async Task<string> ReadFileAsync(string relativePath, CancellationToken cancellationToken)
    {
        var content = Truncate(await files.ReadFileAsync(relativePath, cancellationToken));
        AddContextSource("File: " + relativePath);
        TrackUntrustedContent("File: " + relativePath, content);
        return UntrustedToolOutput.Format("project file", content, relativePath);
    }

    private async Task<string> SearchFilesAsync(string query, CancellationToken cancellationToken)
    {
        var results = string.Join("\n", await files.SearchFilesAsync(query, cancellationToken));
        AddContextSource("Search results for: " + query);
        results = Truncate(results);
        TrackUntrustedContent("Search results for: " + query, results);
        return UntrustedToolOutput.Format("project search results", results);
    }

    private void AddContextSource(string source)
    {
        source = ShortenSource(source);
        if (_contextSources.Contains(source, StringComparer.OrdinalIgnoreCase)) return;
        if (_contextSources.Count == 20) _contextSources.RemoveAt(0);
        _contextSources.Add(source);
    }

    private static string ShortenSource(string source) => source.Length <= 240 ? source : source[..240] + "…";

    private void TrackUntrustedContent(string source, string content)
    {
        if (_untrustedContents.Count == 16) _untrustedContents.RemoveAt(0);
        _untrustedContents.Add((ShortenSource(source), content.Length <= 8000 ? content : content[..8000]));
    }

    private string? FindCommandSource(string command)
    {
        var normalizedCommand = NormalizeCommand(command);
        if (normalizedCommand.Length < 8) return null;
        foreach (var (source, content) in _untrustedContents)
        {
            if (content.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Any(line => NormalizeCommand(line).Contains(normalizedCommand, StringComparison.OrdinalIgnoreCase)))
                return source;
            if (UntrustedCommandMatcher.IsLikelyRewrite(command, content)) return source;
        }
        return null;
    }

    private static string NormalizeCommand(string value) =>
        string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private async Task<string> CreateFileAsync(string relativePath, string content, CancellationToken cancellationToken)
    {
        if (RepairBudgetExhausted) return RepairLimitMessage;
        if (string.IsNullOrWhiteSpace(relativePath)) return "Error: a project-relative file path is required.";
        var fullPath = files.ResolvePath(relativePath);
        if (File.Exists(fullPath)) return "Rejected: a file already exists here. Use write_file to propose an edit.";
        if (!await reviewFile(new CodeTaskFileProposal(relativePath, "", content, IsNewFile: true, ContextSources: _contextSources.ToArray())))
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
        if (!await reviewFile(new CodeTaskFileProposal(relativePath, original.Content, content, IsNewFile: false, proposedPatch, _contextSources.ToArray())))
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
        var commandProposal = new CodeTaskCommandProposal(command, files.Root, shell.DisplayName,
            ContextSources: _contextSources.ToArray(), MatchingUntrustedSource: FindCommandSource(command));
        var approval = await RequestCommandApprovalAsync(commandProposal);
        if (approval is not (CommandApprovalOutcome.Approved or CommandApprovalOutcome.ApprovedReadOnly))
            return approval == CommandApprovalOutcome.Denied
                ? "Denied by a saved project command permission rule; the command was not run."
                : "Rejected by user; the command was not run.";
        if (approval == CommandApprovalOutcome.ApprovedReadOnly)
        {
            status?.Invoke("Code task · inspecting project files…");
            try
            {
                var output = await ReadOnlyCommandClassifier.ExecuteAsync(command, files.Root, shell.DisplayName, cancellationToken);
                AddContextSource("Output from read-only project inspection: " + command);
                TrackUntrustedContent("Output from read-only project inspection: " + command, output);
                return UntrustedToolOutput.Format("read-only project inspection output", Truncate(output, 8000));
            }
            finally { status?.Invoke("Code task · Thinking…"); }
        }
        status?.Invoke($"Code task · starting approved {shell.DisplayName} command…");
        var progress = new Progress<TimeSpan>(elapsed =>
            status?.Invoke($"Code task · command running · {elapsed:mm\\:ss}"));
        try
        {
            var output = await files.RunApprovedCommandAsync(command, TimeSpan.FromMinutes(3), cancellationToken, commandProgress ?? progress);
            AddContextSource("Output from approved command: " + command);
            TrackUntrustedContent("Output from approved command: " + command, output);
            return UntrustedToolOutput.Format("approved command output", Truncate(output, 8000));
        }
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
        var commandProposal = new CodeTaskCommandProposal(command, files.Root, shell.DisplayName, IsVerification: true,
            ContextSources: _contextSources.ToArray(), MatchingUntrustedSource: FindCommandSource(command));
        var approval = await RequestCommandApprovalAsync(commandProposal);
        if (approval != CommandApprovalOutcome.Approved)
            return approval == CommandApprovalOutcome.Denied
                ? "Verification denied by a saved project command permission rule; it was not run and no result is available."
                : "Verification rejected by user; it was not run and no result is available.";

        status?.Invoke("Code task · running approved verification…");
        try
        {
            var progress = commandProgress ?? new Progress<TimeSpan>(elapsed =>
                status?.Invoke($"Code task · verification running · {elapsed:mm\\:ss}"));
            var output = await files.RunApprovedCommandAsync(command, TimeSpan.FromMinutes(3), cancellationToken, progress);
            AddContextSource("Output from approved verification: " + command);
            TrackUntrustedContent("Output from approved verification: " + command, output);
            var exitMatch = Regex.Match(output, @"(?:^|\n)Exit code: (-?\d+)\s*$", RegexOptions.CultureInvariant);
            if (exitMatch.Success && int.TryParse(exitMatch.Groups[1].Value, out var exitCode) && exitCode == 0)
                return "Verification PASSED (exit code 0).\n" + UntrustedToolOutput.Format("approved verification command output", Truncate(output, 8000));

            _failedVerifications++;
            var limit = Math.Clamp(maxRepairAttempts, 0, 3);
            var budget = RepairBudgetExhausted
                ? "\nRepair limit reached: further Codev file edits and commands are blocked. Report the remaining failure."
                : $"\nVerification failures: {_failedVerifications}; repair attempts allowed: {limit}. You may make a reviewed fix and request verification again. Each run needs approval.";
            var verificationStatus = (exitMatch.Success
                ? $"Verification FAILED (exit code {exitMatch.Groups[1].Value}).\n"
                : "Verification FAILED (no successful exit status; command may have timed out).\n") + budget;
            return verificationStatus + "\n" + UntrustedToolOutput.Format("approved verification command output", Truncate(output, 8000));
        }
        finally { status?.Invoke("Code task · Thinking…"); }
    }

    private async Task<CommandApprovalOutcome> RequestCommandApprovalAsync(CodeTaskCommandProposal proposal)
    {
        if (permissionApproval is not null) return await permissionApproval(proposal);
        return await approveCommand(proposal) ? CommandApprovalOutcome.Approved : CommandApprovalOutcome.Rejected;
    }

    private static string Truncate(string value, int max = 6000) => value.Length <= max ? value : value[..max] + "\n… [tool output truncated]";
}
