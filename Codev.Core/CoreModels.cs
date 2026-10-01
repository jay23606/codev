using System.Text.Json.Serialization;
using System.Text;

namespace Codev;

public sealed record PersistedQueuedTurn(int AssistantIndex, string Model, int NumCtx, bool IsCodeTask, bool IsPlanMode,
    string? ProjectPath, List<string>? ContextFiles, List<string>? ContextExclusions, DateTimeOffset EnqueuedAt, double? Temperature = null, string Provider = "ollama", bool IncludeProjectContext = false, bool IncludeRepoMap = false, string OutputStyle = ConversationOutputStyles.Balanced, bool ThinkEnabled = false,
    double? TopP = null, int? TopK = null, double? PresencePenalty = null, double? RepeatPenalty = null, int? NumPredict = null,
    bool ProjectFolderTrusted = false, string? OpenAiReasoningEffort = null, string? OpenAiVerbosity = null,
    string? OpenAiReasoningMode = null, string? AgentProfileName = null);

public sealed class Conversation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? ParentConversationId { get; set; }
    public int? DelegatedFromMessageIndex { get; set; }
    public string? DelegatedAgentName { get; set; }
    public bool DelegatedResultReported { get; set; }
    public string? ChildWorktreeBranch { get; set; }
    public string? ChildWorktreeStartCommit { get; set; }
    public bool ChildConversationsExpanded { get; set; } = true;
    [JsonIgnore] public bool IsChildTaskRunning { get; set; }
    [JsonIgnore] public List<Conversation> ChildConversations { get; set; } = [];
    [JsonIgnore] public bool HasChildConversations => ChildConversations.Count > 0;
    [JsonIgnore] public string ChildConversationLabel => IsChildTaskRunning ? Title + " · running" : Title;
    [JsonIgnore] public string ChildConversationsLabel
    {
        get
        {
            var running = ChildConversations.Count(child => child.IsChildTaskRunning);
            return running == 0 ? $"Child sessions · {ChildConversations.Count}" : $"Child sessions · {ChildConversations.Count} · {running} running";
        }
    }
    public string Title { get; set; } = "";
    public string Draft { get; set; } = "";
    public string Model { get; set; } = "";
    public string Provider { get; set; } = "ollama";
    public bool IsPlanMode { get; set; }
    public bool IsCodeTask { get; set; }
    public string? AgentProfileName { get; set; }
    public bool QueueEnabled { get; set; } = true;
    public bool ThinkEnabled { get; set; }
    private string _outputStyle = ConversationOutputStyles.Balanced;
    public string OutputStyle { get => _outputStyle; set => _outputStyle = ConversationOutputStyles.Normalize(value); }
    public bool AllowHostedCodeTask { get; set; }
    public bool IncludeProjectContextForHosted { get; set; }
    public bool IncludeRepoMap { get; set; }
    public int NumCtx { get; set; }
    public double? Temperature { get; set; }
    public double? TopP { get; set; }
    public int? TopK { get; set; }
    public double? PresencePenalty { get; set; }
    public double? RepeatPenalty { get; set; }
    public int? NumPredict { get; set; }
    public string? OpenAiReasoningEffort { get; set; }
    public string? OpenAiVerbosity { get; set; }
    public string? OpenAiReasoningMode { get; set; }
    public int LastPromptTokens { get; set; }
    public int? LastPromptOutputTokens { get; set; }
    public int LastPromptContext { get; set; }
    public string LastPromptModel { get; set; } = "";
    public string LastPromptProvider { get; set; } = "";
    public string CompactionSummary { get; set; } = "";
    public int CompactionFromMessageCount { get; set; }
    public int CompactionThroughMessageCount { get; set; }
    private List<TaskChecklistItem> _taskChecklist = [];
    public List<TaskChecklistItem> TaskChecklist { get => _taskChecklist; set => _taskChecklist = TaskChecklistService.NormalizeImported(value).ToList(); }
    public bool IsPinned { get; set; }
    public bool IsArchived { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;
    public string? ProjectPath { get; set; }
    public List<ChatMessage> Messages { get; set; } = [];
    public List<FileChangeRecord> FileChanges { get; set; } = [];
    public int? FileChangesPrunedThroughMessageIndex { get; set; }
    public bool FileChangesPrunedUnlinked { get; set; }
    public List<string> ContextFiles { get; set; } = [];
    public List<GitDiffComment> PendingDiffComments { get; set; } = [];
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<PersistedQueuedTurn> PendingTurns { get; set; } = [];
    [JsonIgnore] public int PendingRequestCount { get; set; }
}

public sealed record FileChangeRecord(string RelativePath, string? CheckpointPath, DateTimeOffset ChangedAt, string Kind,
    bool PreviousFileExisted = true, int? TurnUserMessageIndex = null,
    bool? ResultFileExisted = null, string? ResultSha256 = null);

public enum ConversationRewindChoice { Cancel, ConversationOnly, CodeOnly, CodeAndConversation }
public enum CodeRewindReviewResult { Cancelled, NoChanges, Restored }

public sealed record GitDiffComment(string RelativePath, string SelectedDiff, string Comment);

public sealed record TaskChecklistItem(Guid Id, string Text, string Status = TaskChecklistService.Pending)
{
    [JsonIgnore]
    public string DisplayStatus => Status switch
    {
        TaskChecklistService.InProgress => "In progress",
        TaskChecklistService.Completed => "Done",
        _ => "To do"
    };
}

public sealed record ChatMessage(string Role, string Content)
{
    [JsonIgnore] public bool IsUser => string.Equals(Role, "user", StringComparison.OrdinalIgnoreCase);
    [JsonIgnore] public bool IsAssistant => string.Equals(Role, "assistant", StringComparison.OrdinalIgnoreCase);
    [JsonIgnore] public int MessageIndex { get; init; } = -1;
    [JsonIgnore] public bool IsQueued { get; init; }
    public string Thinking { get; init; } = "";
    [JsonIgnore] public bool HasThinking => !string.IsNullOrWhiteSpace(Thinking);
    public OllamaGenerationStats? GenerationStats { get; init; }
    public OpenAiCodeTaskUsage? HostedUsage { get; init; }
    [JsonIgnore] public bool HasHostedUsage => HostedUsage is not null;
    [JsonIgnore] public string HostedUsageLabel => HostedUsage?.DisplayLabel ?? "";
    [JsonIgnore] public bool HasGenerationStats => GenerationStats is not null;
    [JsonIgnore] public string GenerationStatsLabel => GenerationStats?.ToDisplayString() ?? "";
    [JsonIgnore] public string DisplayContent => ToolOutputTranscriptParser.Parse(Content).DisplayText;
    [JsonIgnore] public IReadOnlyList<ChatToolOutput> ToolOutputs => ToolOutputTranscriptParser.Parse(Content).Outputs;
    [JsonIgnore] public IReadOnlyList<ChatToolOutput> CommandToolOutputs => ToolOutputs;
    [JsonIgnore] public IReadOnlyList<ChatToolOutput> NonCommandToolOutputs => Array.Empty<ChatToolOutput>();
    [JsonIgnore] public bool HasCommandToolOutputs => HasToolOutputs;
    [JsonIgnore] public bool HasNonCommandToolOutputs => false;
    [JsonIgnore] public bool HasToolOutputs => ToolOutputs.Count > 0;
    [JsonIgnore] public string CommandToolOutputsHeader => ToolOutputSummary.Build(ToolOutputs);
}

public sealed record ChatToolOutput(string Header, string Content, string? Command = null, string? Activity = null, string? Path = null)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public bool HasCommand => Activity is not ("mcp_tool" or "mcp_prompt" or "mcp_resource" or "mcp_resource_template") && !string.IsNullOrWhiteSpace(Command);

    [System.Text.Json.Serialization.JsonIgnore]
    public string Summary => Activity switch
    {
        "background_command_started" => $"Started background command · {Command}",
        "background_command_output" => $"Read background command output · {Command}",
        _ when HasCommand => $"Ran {Command}",
        "created_file" => $"Created file · {Path}",
        "edited_file" => $"Edited file · {Path}",
        "applied_patch" => $"Applied patch · {Path}",
        "read_file" => $"Read file · {Path}",
        "search_files" => "Searched files",
        "search_web" or "web_search" => "Searched the web",
        "list_files" => "Listed files",
        "mcp_tool" => $"Used MCP tool · {Command ?? Header}",
        "mcp_prompt" => $"Used MCP prompt · {Command ?? Header}",
        "mcp_resource" => $"Read MCP resource · {Command ?? Header}",
        "mcp_resource_template" => $"Read MCP resource template · {Command ?? Header}",
        "loaded_skill" => $"Loaded skill · {Path ?? Header}",
        _ => Header
    };
}

public static class ToolOutputSummary
{
    public static string Build(IReadOnlyList<ChatToolOutput> outputs)
    {
        var activities = new List<string>();
        var createdFileCount = outputs.Count(output => output.Activity == "created_file");
        var editedFileCount = outputs.Count(output => output.Activity is "edited_file" or "applied_patch");
        foreach (var output in outputs)
        {
            var activity = output.Activity switch
            {
                "background_command_started" => "started background commands",
                "background_command_output" => "read background command output",
                _ when output.HasCommand => "ran commands",
                "created_file" => createdFileCount == 1 ? "created a file" : "created files",
                "edited_file" or "applied_patch" => editedFileCount == 1 ? "edited a file" : "edited files",
                "read_file" => "read files",
                "search_files" => "searched files",
                "list_files" => "listed files",
                "mcp_tool" => "used MCP tools",
                "mcp_prompt" => "used MCP prompts",
                "mcp_resource" => "read MCP resources",
                "mcp_resource_template" => "read MCP resource templates",
                "loaded_skill" => "loaded skills",
                "search_web" or "web_search" => "searched the web",
                _ => "did other things"
            };
            if (activity is not null && !activities.Contains(activity, StringComparer.Ordinal))
                activities.Add(activity);
        }
        if (activities.Count == 0) return "Ran tools";
        var summary = string.Join(", ", activities);
        return char.ToUpperInvariant(summary[0]) + summary[1..];
    }
}

/// <summary>Separates untrusted tool result envelopes from assistant prose for compact, collapsed display.</summary>
public static class ToolOutputTranscriptParser
{
    private static readonly System.Text.Json.JsonSerializerOptions PrettyJson = new() { WriteIndented = true };
    private static readonly System.Text.Json.JsonDocumentOptions JsonOptions = new() { MaxDepth = 32 };

    public static (string DisplayText, IReadOnlyList<ChatToolOutput> Outputs) Parse(string? transcript)
    {
        transcript ??= "";
        var visible = new StringBuilder(transcript.Length);
        var outputs = new List<ChatToolOutput>();
        var cursor = 0;
        while (cursor < transcript.Length)
        {
            var lineStart = cursor;
            var lineEnd = transcript.IndexOf('\n', lineStart);
            if (lineEnd < 0) lineEnd = transcript.Length;
            var heading = transcript[lineStart..lineEnd].TrimEnd('\r');
            var contentStart = lineEnd < transcript.Length ? lineEnd + 1 : lineEnd;
            var jsonStart = contentStart;
            while (jsonStart < transcript.Length && char.IsWhiteSpace(transcript[jsonStart])) jsonStart++;

            if (heading.Length >= 4 && heading.StartsWith("**", StringComparison.Ordinal) &&
                heading.EndsWith("**", StringComparison.Ordinal) && jsonStart < transcript.Length &&
                transcript[jsonStart] == '{' && TryFindJsonEnd(transcript, jsonStart, out var jsonEnd) &&
                TryReadToolOutput(transcript[jsonStart..jsonEnd], out var output))
            {
                visible.Append(transcript, cursor, lineStart - cursor);
                outputs.Add(output with { Header = heading[2..^2] + " · " + output.Header });
                cursor = jsonEnd;
                while (cursor < transcript.Length && transcript[cursor] is '\r' or '\n') cursor++;
                continue;
            }

            visible.Append(transcript, lineStart, contentStart - lineStart);
            cursor = contentStart;
        }
        return (visible.ToString().TrimEnd(), outputs);
    }

    private static bool TryReadToolOutput(string json, out ChatToolOutput output)
    {
        output = new ChatToolOutput("Tool output", json);
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(json, JsonOptions);
            var root = document.RootElement;
            if (root.ValueKind != System.Text.Json.JsonValueKind.Object || !root.TryGetProperty("type", out var type)) return false;
            if (type.GetString() == "agent_skill_output")
            {
                var name = root.TryGetProperty("name", out var nameValue) ? nameValue.GetString() : null;
                var skillContent = root.TryGetProperty("content", out var skillContentValue) && skillContentValue.ValueKind == System.Text.Json.JsonValueKind.String
                    ? skillContentValue.GetString() ?? "" : "";
                if (string.IsNullOrWhiteSpace(name) || skillContent.Length == 0) return false;
                try
                {
                    using var innerSkill = System.Text.Json.JsonDocument.Parse(skillContent, JsonOptions);
                    skillContent = System.Text.Json.JsonSerializer.Serialize(innerSkill.RootElement, PrettyJson);
                }
                catch (System.Text.Json.JsonException) { }
                output = new ChatToolOutput("Loaded skill", skillContent, null, "loaded_skill", name);
                return true;
            }
            if (type.GetString() != "untrusted_tool_output") return false;
            var source = root.TryGetProperty("source", out var sourceValue) ? sourceValue.GetString() : null;
            var path = root.TryGetProperty("path", out var pathValue) ? pathValue.GetString() : null;
            var content = root.TryGetProperty("content", out var contentValue) && contentValue.ValueKind == System.Text.Json.JsonValueKind.String
                ? contentValue.GetString() ?? "" : "";
            var command = root.TryGetProperty("command", out var commandValue) && commandValue.ValueKind == System.Text.Json.JsonValueKind.String
                ? commandValue.GetString() : null;
            var activity = root.TryGetProperty("activity", out var activityValue) && activityValue.ValueKind == System.Text.Json.JsonValueKind.String
                ? activityValue.GetString() : null;
            try
            {
                using var inner = System.Text.Json.JsonDocument.Parse(content, JsonOptions);
                content = System.Text.Json.JsonSerializer.Serialize(inner.RootElement, PrettyJson);
            }
            catch (System.Text.Json.JsonException) { }
            var header = string.Join(" · ", new[] { source, path }.Where(part => !string.IsNullOrWhiteSpace(part)));
            output = new ChatToolOutput(string.IsNullOrWhiteSpace(header) ? "Tool output" : header, content, command, activity, path);
            return true;
        }
        catch (System.Text.Json.JsonException) { return false; }
    }

    private static bool TryFindJsonEnd(string text, int start, out int end)
    {
        end = start;
        var depth = 0;
        var inString = false;
        var escaped = false;
        for (var i = start; i < text.Length; i++)
        {
            var ch = text[i];
            if (inString)
            {
                if (escaped) escaped = false;
                else if (ch == '\\') escaped = true;
                else if (ch == '"') inString = false;
                continue;
            }
            if (ch == '"') inString = true;
            else if (ch == '{') depth++;
            else if (ch == '}' && --depth == 0) { end = i + 1; return true; }
        }
        return false;
    }
}

public sealed class WorkspaceProject
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "Project";
    public string Path { get; set; } = "";
    public bool IsPinned { get; set; }
    public string Instructions { get; set; } = "";
    public string Knowledge { get; set; } = "";
    public List<string> ContextExclusions { get; set; } = [];
    public DateTimeOffset LastOpenedAt { get; set; } = DateTimeOffset.Now;
}
