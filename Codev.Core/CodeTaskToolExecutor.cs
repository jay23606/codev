using System.Text.Json;
using System.Text.RegularExpressions;

namespace Codev;

public sealed record CodeTaskFileProposal(string RelativePath, string Before, string After, bool IsNewFile, string? ProposedPatch = null,
    IReadOnlyList<string>? ContextSources = null);
public sealed record CodeTaskCommandProposal(string Command, string ProjectPath, string ShellName, bool IsVerification = false,
    IReadOnlyList<string>? ContextSources = null, string? MatchingUntrustedSource = null, bool ProfileApprovalSatisfied = false);

/// <summary>Executes bounded Code task tools. The UI supplies file-review and command-policy decisions, which may allow, deny, or prompt according to the active project and agent modes.</summary>
public sealed class CodeTaskToolExecutor(
    WorkspaceFileService files,
    Conversation conversation,
    Func<CodeTaskFileProposal, Task<bool>> reviewFile,
    Func<CodeTaskCommandProposal, Task<bool>> approveCommand,
    IProgress<TimeSpan>? commandProgress = null,
    Action<string>? status = null,
    int maxRepairAttempts = 2,
    IEnumerable<string>? initialContextSources = null,
    Func<CodeTaskCommandProposal, Task<CommandApprovalOutcome>>? permissionApproval = null,
    int? turnUserMessageIndex = null,
    IReadOnlyDictionary<string, McpCodeTaskTool>? mcpTools = null,
    Func<McpCodeTaskTool, JsonElement, bool, Task<CommandApprovalOutcome>>? mcpPermissionApproval = null,
    Func<string, JsonElement, Task<AgentToolProfileDecision>>? agentProfilePermission = null,
    AgentProfile? agentProfile = null,
    IReadOnlyDictionary<string, SlashCommandDefinition>? agentSkills = null,
    Func<SlashCommandDefinition, string, CancellationToken, Task<string>>? agentSkillInvocation = null,
    Func<McpCodeTaskTool, JsonElement, CancellationToken, Task<string>>? mcpCall = null)
{
    private static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, int>> ToolArgumentLimits =
        new Dictionary<string, IReadOnlyDictionary<string, int>>(StringComparer.Ordinal)
        {
            ["list_files"] = new Dictionary<string, int>(StringComparer.Ordinal) { ["relative_directory"] = 240 },
            ["read_file"] = new Dictionary<string, int>(StringComparer.Ordinal) { ["relative_path"] = 240 },
            ["search_files"] = new Dictionary<string, int>(StringComparer.Ordinal) { ["query"] = 1_000 },
            ["create_file"] = new Dictionary<string, int>(StringComparer.Ordinal) { ["relative_path"] = 240, ["content"] = 500_000 },
            ["write_file"] = new Dictionary<string, int>(StringComparer.Ordinal) { ["relative_path"] = 240, ["content"] = 500_000 },
            ["apply_patch"] = new Dictionary<string, int>(StringComparer.Ordinal) { ["relative_path"] = 240, ["patch"] = 500_000 },
            ["verify_command"] = new Dictionary<string, int>(StringComparer.Ordinal) { ["command"] = 4_000 },
            ["run_command"] = new Dictionary<string, int>(StringComparer.Ordinal) { ["command"] = 4_000 }
        };
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
        if (ToolArgumentLimits.ContainsKey(name) && ValidateToolArguments(name, arguments) is { } error)
            return "Rejected: " + error;
        if (agentSkills?.ContainsKey(name) == true && ValidateAgentSkillArguments(arguments) is { } skillError)
            return "Rejected: " + skillError;
        if (agentSkills?.TryGetValue(name, out var requestedSkill) == true && requestedSkill.UserOnly)
            return "Denied: this skill is configured for user-only invocation.";
        string Arg(string key) => arguments.TryGetProperty(key, out var value) ? value.GetString() ?? "" : "";
        if ((name is "create_file" or "write_file" or "apply_patch") &&
            !AgentProfilePolicy.CanEditPath(agentProfile, Arg("relative_path"), files.Root))
            return "Rejected: the selected agent profile does not allow edits to this path.";
        var profileApprovalSatisfied = false;
        if (agentProfilePermission is not null)
        {
            var profileDecision = await agentProfilePermission(name, arguments);
            if (profileDecision is AgentToolProfileDecision.Denied or AgentToolProfileDecision.Rejected)
                return profileDecision == AgentToolProfileDecision.Denied
                    ? "Denied by the selected agent profile; this tool is unavailable."
                    : "Rejected by the selected agent profile; the tool was not run.";
            profileApprovalSatisfied = profileDecision == AgentToolProfileDecision.ApprovedOnce;
        }
        try
        {
            if (mcpTools is not null && mcpTools.TryGetValue(name, out var mcpTool))
                return await ExecuteMcpToolAsync(mcpTool, arguments, profileApprovalSatisfied, cancellationToken);
            if (agentSkills is not null && agentSkills.TryGetValue(name, out var agentSkill))
                return await ExecuteAgentSkillAsync(agentSkill, arguments, cancellationToken);
            return name switch
            {
                "list_files" => ListFiles(Arg("relative_directory")),
                "read_file" => await ReadFileAsync(Arg("relative_path"), cancellationToken),
                "search_files" => await SearchFilesAsync(Arg("query"), cancellationToken),
                "create_file" => await CreateFileAsync(Arg("relative_path"), Arg("content"), cancellationToken),
                "write_file" => await WriteFileAsync(Arg("relative_path"), Arg("content"), cancellationToken),
                "apply_patch" => await ApplyPatchAsync(Arg("relative_path"), Arg("patch"), cancellationToken),
                "verify_command" => await VerifyCommandAsync(Arg("command"), cancellationToken, profileApprovalSatisfied),
                "run_command" => await RunCommandAsync(Arg("command"), cancellationToken, profileApprovalSatisfied),
                _ => "Error: this tool is not available."
            };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return "Error: " + ex.Message; }
    }

    private async Task<string> ExecuteAgentSkillAsync(SlashCommandDefinition skill, JsonElement arguments, CancellationToken cancellationToken)
    {
        if (skill.UserOnly) return "Denied: this skill is configured for user-only invocation.";
        if (agentSkillInvocation is null) return "Rejected: agent skill loading is not available for this task.";
        var invocationArguments = arguments.TryGetProperty("arguments", out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? "" : "";
        var result = await agentSkillInvocation(skill, invocationArguments, cancellationToken);
        return AgentSkillTool.FormatLoadedPrompt(skill, result);
    }

    private static string? ValidateAgentSkillArguments(JsonElement arguments)
    {
        if (arguments.ValueKind != JsonValueKind.Object) return "skill arguments must be a JSON object.";
        foreach (var property in arguments.EnumerateObject())
            if (!property.NameEquals("arguments")) return $"'{property.Name}' is not an accepted argument for this skill.";
        if (!arguments.TryGetProperty("arguments", out var value) || value.ValueKind != JsonValueKind.String)
            return "required argument 'arguments' must be a string.";
        var text = value.GetString() ?? "";
        return text.Length > 4_000 ? "skill arguments exceed the 4,000-character limit." : null;
    }

    private async Task<string> ExecuteMcpToolAsync(McpCodeTaskTool tool, JsonElement arguments, bool profileApprovalSatisfied, CancellationToken cancellationToken)
    {
        if (arguments.ValueKind != JsonValueKind.Object || arguments.GetRawText().Length > 500_000)
            return "Rejected: MCP tool arguments must be a JSON object no larger than 500,000 characters.";
        var approval = mcpPermissionApproval is null
            ? CommandApprovalOutcome.Rejected
            : await mcpPermissionApproval(tool, arguments, profileApprovalSatisfied);
        if (approval is not (CommandApprovalOutcome.Approved or CommandApprovalOutcome.ApprovedReadOnly))
            return approval == CommandApprovalOutcome.Denied
                ? "Denied by a saved project MCP tool permission rule; the tool was not called."
                : "MCP tool call rejected; the server was not called.";

        status?.Invoke($"Code task · calling {tool.ServerName}/{tool.ToolName}…");
        try
        {
            var result = mcpCall is not null
                ? await mcpCall(tool, arguments, cancellationToken).ConfigureAwait(false)
                : await CallLegacyMcpToolAsync(tool, arguments, cancellationToken).ConfigureAwait(false);
            var operation = tool.Operation.DisplayName();
            var activity = tool.Operation.ActivityName();
            var source = $"MCP {operation} output: {tool.ServerName}/{tool.ToolName}";
            AddContextSource(source);
            TrackUntrustedContent(source, result);
            return UntrustedToolOutput.Format($"MCP {operation} output", result, command: tool.ServerName + "/" + tool.ToolName, activity: "mcp_" + activity);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (OperationCanceledException)
        {
            return UntrustedToolOutput.Format($"MCP {tool.Operation.DisplayName()} error", "The MCP server canceled the operation.", command: tool.ServerName + "/" + tool.ToolName, activity: "mcp_" + tool.Operation.ActivityName());
        }
        catch (TimeoutException)
        {
            return UntrustedToolOutput.Format($"MCP {tool.Operation.DisplayName()} error", $"The external MCP operation timed out after {tool.ExecutionTimeoutMs} ms.", command: tool.ServerName + "/" + tool.ToolName, activity: "mcp_" + tool.Operation.ActivityName());
        }
        catch (Exception ex)
        {
            return UntrustedToolOutput.Format($"MCP {tool.Operation.DisplayName()} error", $"The external MCP operation failed ({ex.GetType().Name}). Check the server configuration and logs.", command: tool.ServerName + "/" + tool.ToolName, activity: "mcp_" + tool.Operation.ActivityName());
        }
        finally { status?.Invoke("Code task · Thinking…"); }
    }

    private static async Task<string> CallLegacyMcpToolAsync(McpCodeTaskTool tool, JsonElement arguments, CancellationToken cancellationToken)
    {
        if (tool.Operation != McpCodeTaskOperationKind.Tool || tool.ClientTool is null)
            throw new InvalidOperationException("This MCP operation is no longer available.");
        var callArguments = JsonSerializer.Deserialize<Dictionary<string, object?>>(arguments.GetRawText(), new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? [];
        var output = await McpOperationTimeout.RunAsync(
            async token => await tool.ClientTool.CallAsync(callArguments, cancellationToken: token).ConfigureAwait(false), tool.ExecutionTimeoutMs, cancellationToken);
        var text = new System.Text.StringBuilder();
        var truncated = false;
        if (output.IsError == true) McpToolOutputFormatter.Append(text, "MCP server reported a tool error.", ref truncated);
        foreach (var block in output.Content)
        {
            if (block is ModelContextProtocol.Protocol.TextContentBlock content && !string.IsNullOrEmpty(content.Text))
                McpToolOutputFormatter.Append(text, content.Text, ref truncated);
            else McpToolOutputFormatter.Append(text, $"[{block.Type} content omitted from text-only Code task results]", ref truncated);
            if (truncated) break;
        }
        if (output.StructuredContent is { } structured && structured.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null))
        {
            McpToolOutputFormatter.Append(text, "Structured content:", ref truncated, blankLine: true);
            McpToolOutputFormatter.Append(text, structured.GetRawText(), ref truncated, separator: false);
        }
        return text.Length == 0 ? "MCP tool returned no text content." : text.ToString();
    }

    private static string? ValidateToolArguments(string name, JsonElement arguments)
    {
        if (arguments.ValueKind != JsonValueKind.Object) return "tool arguments must be a JSON object.";
        var limits = ToolArgumentLimits[name];
        foreach (var property in arguments.EnumerateObject())
            if (!limits.ContainsKey(property.Name)) return $"'{property.Name}' is not an accepted argument for {name}.";
        foreach (var (key, maxLength) in limits)
        {
            if (!arguments.TryGetProperty(key, out var value)) return $"required argument '{key}' is missing.";
            if (value.ValueKind != JsonValueKind.String) return $"argument '{key}' must be a string.";
            var text = value.GetString() ?? "";
            if (text.Length > maxLength) return $"argument '{key}' exceeds the {maxLength}-character limit.";
            if (key != "relative_directory" && key != "content" && string.IsNullOrWhiteSpace(text))
                return $"argument '{key}' cannot be empty.";
        }
        return null;
    }

    private async Task<string> ReadFileAsync(string relativePath, CancellationToken cancellationToken)
    {
        var content = Truncate(await files.ReadFileAsync(relativePath, cancellationToken));
        AddContextSource("File: " + relativePath);
        TrackUntrustedContent("File: " + relativePath, content);
        return UntrustedToolOutput.Format("project file", content, relativePath, activity: "read_file");
    }

    private string ListFiles(string relativeDirectory)
    {
        var source = string.IsNullOrWhiteSpace(relativeDirectory)
            ? "Project file listing"
            : "Project file listing: " + relativeDirectory;
        var listing = string.Join("\n", files.ListFiles(relativeDirectory, 160));
        AddContextSource(source);
        TrackUntrustedContent(source, listing);
        return UntrustedToolOutput.Format("project file listing", listing, path: relativeDirectory, activity: "list_files");
    }

    private async Task<string> SearchFilesAsync(string query, CancellationToken cancellationToken)
    {
        var results = string.Join("\n", await files.SearchFilesAsync(query, cancellationToken));
        AddContextSource("Search results for: " + query);
        results = Truncate(results);
        TrackUntrustedContent("Search results for: " + query, results);
        return UntrustedToolOutput.Format("project search results", results, activity: "search_files");
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
        var created = await files.ReadFileSnapshotAsync(relativePath, cancellationToken);
        ConversationFileChangeHistoryService.Record(conversation, new FileChangeRecord(relativePath, null, DateTimeOffset.Now, "Create", PreviousFileExisted: false,
            TurnUserMessageIndex: turnUserMessageIndex, ResultFileExisted: true, ResultSha256: created.Sha256));
        return FormatFileChangeOutput("project file created", "Created the new project file.", relativePath,
            "created_file", content);
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
        var written = await files.ReadFileSnapshotAsync(relativePath, cancellationToken);
        if (checkpoint is not null) ConversationFileChangeHistoryService.Record(conversation, new FileChangeRecord(relativePath, checkpoint, DateTimeOffset.Now, "Edit",
            TurnUserMessageIndex: turnUserMessageIndex, ResultFileExisted: true, ResultSha256: written.Sha256));
        return FormatFileChangeOutput("project file updated", "Applied the change. A local checkpoint was saved before the change.", relativePath,
            proposedPatch is null ? "edited_file" : "applied_patch", content);
    }

    private static string FormatFileChangeOutput(string source, string message, string relativePath, string activity, string proposedContent)
    {
        var warnings = InstructionFollowingContentDetector.Detect(proposedContent);
        if (warnings.Count > 0)
            message += "\nAdvisory: the proposed file content matched instruction-risk patterns (" + string.Join(", ", warnings) +
                "). This advisory does not change the selected permission mode.";
        return UntrustedToolOutput.Format(source, message, relativePath, activity: activity);
    }

    private async Task<string> RunCommandAsync(string command, CancellationToken cancellationToken, bool profileApprovalSatisfied)
    {
        if (RepairBudgetExhausted) return RepairLimitMessage;
        if (string.IsNullOrWhiteSpace(command) || command.Length > 4000)
            return "Rejected: command must contain 1–4,000 characters.";
        var shell = ShellCommandResolver.ResolveCurrent();
        var commandProposal = new CodeTaskCommandProposal(command, files.Root, shell.DisplayName,
            ContextSources: _contextSources.ToArray(), MatchingUntrustedSource: FindCommandSource(command), ProfileApprovalSatisfied: profileApprovalSatisfied);
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
                var output = await ReadOnlyCommandClassifier.ExecuteAsync(command, files.Root, shell.DisplayName, cancellationToken, files.ContextExclusions);
                AddContextSource("Output from read-only project inspection: " + command);
                TrackUntrustedContent("Output from read-only project inspection: " + command, output);
                return UntrustedToolOutput.Format("read-only project inspection output", Truncate(output, 8000), command: command);
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
            return UntrustedToolOutput.Format("approved command output", Truncate(output, 8000), command: command);
        }
        finally { status?.Invoke("Code task · Thinking…"); }
    }

    private bool RepairBudgetExhausted => _failedVerifications > Math.Clamp(maxRepairAttempts, 0, 3);
    private const string RepairLimitMessage = "Rejected: the verification repair limit has been reached for this task. Further Codev file edits and commands are blocked; report the remaining failure.";

    private async Task<string> VerifyCommandAsync(string command, CancellationToken cancellationToken, bool profileApprovalSatisfied)
    {
        if (string.IsNullOrWhiteSpace(command) || command.Length > 4000)
            return "Rejected: verification command must contain 1–4,000 characters.";
        if (RepairBudgetExhausted) return RepairLimitMessage;
        var shell = ShellCommandResolver.ResolveCurrent();
        var commandProposal = new CodeTaskCommandProposal(command, files.Root, shell.DisplayName, IsVerification: true,
            ContextSources: _contextSources.ToArray(), MatchingUntrustedSource: FindCommandSource(command), ProfileApprovalSatisfied: profileApprovalSatisfied);
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
                return "Verification PASSED (exit code 0).\n" + UntrustedToolOutput.Format("approved verification command output", Truncate(output, 8000), command: command);

            _failedVerifications++;
            var limit = Math.Clamp(maxRepairAttempts, 0, 3);
            var budget = RepairBudgetExhausted
                ? "\nRepair limit reached: further Codev file edits and commands are blocked. Report the remaining failure."
                : $"\nVerification failures: {_failedVerifications}; repair attempts allowed: {limit}. You may make a reviewed fix and request verification again; Codev will apply the configured command permission policy.";
            var verificationStatus = (exitMatch.Success
                ? $"Verification FAILED (exit code {exitMatch.Groups[1].Value}).\n"
                : "Verification FAILED (no successful exit status; command may have timed out).\n") + budget;
            return verificationStatus + "\n" + UntrustedToolOutput.Format("approved verification command output", Truncate(output, 8000), command: command);
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
