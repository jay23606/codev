using System.Text.Json.Serialization;

namespace Codev;

public sealed record PersistedQueuedTurn(int AssistantIndex, string Model, int NumCtx, bool IsCodeTask, bool IsPlanMode,
    string? ProjectPath, List<string>? ContextFiles, List<string>? ContextExclusions, DateTimeOffset EnqueuedAt, double? Temperature = null, string Provider = "ollama", bool IncludeProjectContext = false);

public sealed class Conversation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Title { get; set; } = "";
    public string Draft { get; set; } = "";
    public string Model { get; set; } = "qwen3-coder:30b";
    public string Provider { get; set; } = "ollama";
    public bool IsPlanMode { get; set; }
    public bool IsCodeTask { get; set; }
    public bool IncludeProjectContextForHosted { get; set; }
    public int NumCtx { get; set; }
    public double? Temperature { get; set; }
    public int LastPromptTokens { get; set; }
    public int LastPromptContext { get; set; }
    public string LastPromptModel { get; set; } = "";
    public bool IsPinned { get; set; }
    public bool IsArchived { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;
    public string? ProjectPath { get; set; }
    public List<ChatMessage> Messages { get; set; } = [];
    public List<FileChangeRecord> FileChanges { get; set; } = [];
    public List<string> ContextFiles { get; set; } = [];
    public List<GitDiffComment> PendingDiffComments { get; set; } = [];
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<PersistedQueuedTurn> PendingTurns { get; set; } = [];
    [JsonIgnore] public int PendingRequestCount { get; set; }
}

public sealed record FileChangeRecord(string RelativePath, string? CheckpointPath, DateTimeOffset ChangedAt, string Kind, bool PreviousFileExisted = true);

public sealed record GitDiffComment(string RelativePath, string SelectedDiff, string Comment);

public sealed record ChatMessage(string Role, string Content)
{
    [JsonIgnore] public bool IsUser => string.Equals(Role, "user", StringComparison.OrdinalIgnoreCase);
    [JsonIgnore] public bool IsAssistant => string.Equals(Role, "assistant", StringComparison.OrdinalIgnoreCase);
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
