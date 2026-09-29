using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Collections.Generic;
using System.Windows.Input;
using Avalonia.Threading;
using Avalonia;
using Avalonia.Styling;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Diagnostics;

namespace Codev.Avalonia.ViewModels;

/// <summary>Local conversation browser for the Avalonia renderer prototype.</summary>
public sealed class MainViewModel : ViewModelBase
{
    private static readonly string StorePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Codev", "avalonia-conversations.json");
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Codev", "avalonia-settings.json");
    private static readonly string LegacySettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Codev", "settings.json");
    private static readonly string ProjectTrustPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Codev", "avalonia-trusted-folders.json");
    private static readonly string ProjectCommandPermissionsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Codev", "avalonia-command-permissions.json");
    private static readonly string ActiveConversationPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Codev", "avalonia-active-conversation.json");
    private static readonly string UserSlashCommandsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Codev", "commands");
    private static readonly string UserSkillsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Codev", "skills");
    private static readonly JsonSerializerOptions BackupJsonOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    private readonly ObservableCollection<Codev.Conversation> _conversations = [];
    private readonly Codev.ProjectFolderTrustRegistry _projectFolderTrust = Codev.ProjectFolderTrustRegistry.Load(ProjectTrustPath);
    private readonly Codev.ConversationWorkspaceManager _conversationWorkspaces = new(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
    private readonly Codev.ProjectCommandPermissionRegistry _projectCommandPermissions = Codev.ProjectCommandPermissionRegistry.Load(ProjectCommandPermissionsPath);
    private Codev.Conversation? _active;
    private string _searchText = "";
    private string _draft = "";
    private string _model = "";
    private string _provider = "ollama";
    private string _outputStyle = Codev.ConversationOutputStyles.Balanced;
    private bool _thinkEnabled;
    private bool _cloudRequestsEnabled;
    private int _contextSize;
    private readonly DispatcherTimer _draftSaveTimer = new() { Interval = TimeSpan.FromMilliseconds(450) };
    private readonly SemaphoreSlim _persistGate = new(1, 1);
    private Task _persistenceTask = Task.CompletedTask;
    private Task _settingsPersistenceTask = Task.CompletedTask;
    private readonly SemaphoreSlim _settingsPersistGate = new(1, 1);
    private long _settingsRevision;
    private Task _activeConversationPersistenceTask = Task.CompletedTask;
    private readonly SemaphoreSlim _activeConversationPersistGate = new(1, 1);
    private long _persistenceRevision;
    private long _activeConversationRevision;
    private readonly HttpClient _http = new(new SocketsHttpHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
    private readonly Codev.ICloudApiKeyVault _cloudApiKeyVault = new Codev.CloudApiKeyVault();
    private Uri _ollamaEndpoint = Codev.OllamaEndpoint.Default;
    private readonly Dictionary<string, string> _cloudApiKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<Guid, Codev.PromptContextSnapshot> _lastPromptContexts = [];
    private readonly Dictionary<Guid, int> _lastPromptMessageCounts = [];
    private CancellationTokenSource? _generationCancellation;
    private bool _isGenerating;
    private bool _isReviewRunning;
    private bool _isUnloadingModel;
    private string _lastSlashCommandWarning = "";
    private readonly Queue<QueuedChatTurn> _requestQueue = new();
    private bool _queuePaused;
    private bool _queueProcessorRunning;
    private Codev.Conversation? _generationConversation;
    private string _connectionStatus = "Checking Ollama…";
    private bool _isDarkTheme = true;
    private CancellationTokenSource? _modelLoadCancellation;
    private long _modelSelectionRevision;
    private bool _isLoadingModels;
    private string _contextEstimateLabel = "No project files will be included.";
    private int _readingWidth = 800;

    public ObservableCollection<Codev.Conversation> PinnedConversations { get; } = [];
    public ObservableCollection<Codev.Conversation> RecentConversations { get; } = [];
    public ObservableCollection<Codev.ChatMessage> Messages { get; } = [];
    public ObservableCollection<Codev.PromptTemplate> PromptTemplates { get; } = [];
    public ObservableCollection<Codev.SamplingPreset> SamplingPresets { get; } = [];
    public double ReadingWidth => _readingWidth == 0 ? double.PositiveInfinity : _readingWidth;
    public bool IsCompactReadingWidth => _readingWidth == 640;
    public bool IsStandardReadingWidth => _readingWidth == 800;
    public bool IsWideReadingWidth => _readingWidth == 960;
    public bool IsFullReadingWidth => _readingWidth == 0;
    public ObservableCollection<string> SelectedContextFiles { get; } = [];
    public ObservableCollection<Codev.GitDiffComment> PendingDiffComments { get; } = [];
    public ObservableCollection<Codev.TaskChecklistItem> TaskChecklistItems { get; } = [];
    public ObservableCollection<ContextSizeChoice> ContextSizes { get; } = [];
    public ObservableCollection<OutputStyleChoice> OutputStyles { get; } =
    [
        new(Codev.ConversationOutputStyles.Balanced, "Balanced"),
        new(Codev.ConversationOutputStyles.Concise, "Concise"),
        new(Codev.ConversationOutputStyles.Explanatory, "Explanatory"),
        new(Codev.ConversationOutputStyles.CodeOnly, "Code only")
    ];
    public ICommand NewConversationCommand { get; }
    public ICommand SelectConversationCommand { get; }
    public ICommand TogglePinCommand { get; }
    public ICommand ArchiveConversationCommand { get; }
    public ICommand SendCommand { get; }
    public ICommand StopGenerationCommand { get; }
    public ICommand ResumeQueueCommand { get; }
    public ICommand CancelQueuedCommand { get; }
    public ICommand ToggleThemeCommand { get; }
    public ICommand TogglePlanModeCommand { get; }
    public ICommand ToggleCodeTaskCommand { get; }
    public ICommand EnableHostedCodeTaskConsentCommand { get; }
    public ICommand SummarizeConversationUpToCommand { get; }
    public ICommand SummarizeConversationFromCommand { get; }
    public ICommand RemoveContextFileCommand { get; }
    public ICommand ClearContextFilesCommand { get; }
    public ICommand RemoveDiffCommentCommand { get; }
    public ICommand RewindConversationCommand { get; }
    public ICommand EditPromptCommand { get; }
    public ObservableCollection<ModelChoice> Models { get; } =
    [
    ];

    public MainViewModel()
    {
        NewConversationCommand = new RelayCommand(_ => NewConversation());
        SelectConversationCommand = new RelayCommand(value => { if (value is Codev.Conversation conversation) SelectConversation(conversation); });
        TogglePinCommand = new RelayCommand(_ => TogglePin(), _ => ActiveConversation is not null);
        ArchiveConversationCommand = new RelayCommand(_ => ArchiveConversation(), _ => ActiveConversation is not null);
        ToggleArchiveViewCommand = new RelayCommand(_ => { ShowArchived = !ShowArchived; RebuildLists(); });
        ToggleThemeCommand = new RelayCommand(_ => ToggleTheme());
        TogglePlanModeCommand = new RelayCommand(_ => TogglePlanMode(), _ => ActiveConversation is not null && !IsGenerating);
        ToggleCodeTaskCommand = new RelayCommand(_ => ToggleCodeTaskMode(), _ => CanToggleCodeTaskMode);
        EnableHostedCodeTaskConsentCommand = new RelayCommand(_ => EnableHostedCodeTaskConsent(), _ => CanEnableHostedCodeTaskConsent);
        SummarizeConversationUpToCommand = new RelayCommand(value =>
        {
            if (value is Codev.ChatMessage message) _ = SummarizeConversationUpToAsync(message);
        }, value => value is Codev.ChatMessage message && CanSummarizeConversationUpTo(message));
        SummarizeConversationFromCommand = new RelayCommand(value =>
        {
            if (value is Codev.ChatMessage message) _ = SummarizeConversationFromAsync(message);
        }, value => value is Codev.ChatMessage message && CanSummarizeConversationFrom(message));
        RemoveContextFileCommand = new RelayCommand(value => { if (value is string path) RemoveContextFile(path); });
        ClearContextFilesCommand = new RelayCommand(_ => ClearContextFiles(), _ => SelectedContextFiles.Count > 0);
        RemoveDiffCommentCommand = new RelayCommand(value => { if (value is Codev.GitDiffComment comment) RemovePendingDiffComment(comment); });
        RewindConversationCommand = new RelayCommand(value => { if (value is int index) _ = RewindConversationAsync(index); },
            value => value is int index && CanRewindConversationMessage(index));
        EditPromptCommand = new RelayCommand(value => { if (value is int index) _ = EditPromptAsync(index); },
            value => value is int index && CanRewindConversationMessage(index));
        SendCommand = new RelayCommand(_ =>
        {
            if (string.IsNullOrWhiteSpace(Draft) && PendingDiffComments.Count == 0) StopGeneration();
            else _ = SendDraftAsync();
        }, _ => IsGenerating || !string.IsNullOrWhiteSpace(Draft) || PendingDiffComments.Count > 0);
        StopGenerationCommand = new RelayCommand(_ => StopGeneration(), _ => IsGenerating);
        ResumeQueueCommand = new RelayCommand(_ => ResumeQueue(), _ => HasQueuedTurns && _queuePaused);
        CancelQueuedCommand = new RelayCommand(_ => CancelQueuedTurns(), _ => ActiveConversation?.PendingRequestCount > 0);
        _draftSaveTimer.Tick += (_, _) => { _draftSaveTimer.Stop(); Persist(); };
        Messages.CollectionChanged += (_, _) =>
        {
            ((RelayCommand)SummarizeConversationUpToCommand).NotifyCanExecuteChanged();
            ((RelayCommand)SummarizeConversationFromCommand).NotifyCanExecuteChanged();
            OnPropertyChanged(nameof(ShouldOfferCompaction));
            OnPropertyChanged(nameof(ShouldWarnUnknownContext));
        };
        LoadConversations();
        if (_conversations.Count == 0)
        {
            _conversations.Add(new Codev.Conversation { Title = "New conversation", UpdatedAt = DateTimeOffset.Now });
            Persist();
        }
        RebuildLists();
        RestoreQueuedTurns();
        var startupConversation = Codev.ConversationStartupSelection.Choose(_conversations, LoadLastActiveConversationId());
        if (startupConversation is not null) SelectConversation(startupConversation);
        LoadSettings();
        _ = LoadModelsAsync();
    }

    public Codev.Conversation? ActiveConversation
    {
        get => _active;
        private set
        {
            if (SetProperty(ref _active, value))
            {
                OnPropertyChanged(nameof(ConversationTitle));
                ((RelayCommand)RewindConversationCommand).NotifyCanExecuteChanged();
                ((RelayCommand)SummarizeConversationUpToCommand).NotifyCanExecuteChanged();
                ((RelayCommand)SummarizeConversationFromCommand).NotifyCanExecuteChanged();
                OnPropertyChanged(nameof(PinLabel));
                OnPropertyChanged(nameof(MessageCountLabel));
                OnPropertyChanged(nameof(HasCompactionSummary));
                OnPropertyChanged(nameof(CompactionStatusLabel));
                OnPropertyChanged(nameof(ShouldOfferCompaction));
                OnPropertyChanged(nameof(ShouldWarnUnknownContext));
                OnPropertyChanged(nameof(ProjectLabel));
                OnPropertyChanged(nameof(ProjectCommandPermissionMode));
                OnPropertyChanged(nameof(ProjectCommandPermissionRules));
                OnPropertyChanged(nameof(ProjectCommandPermissionStoreNotice));
                OnPropertyChanged(nameof(ContextLabel));
                OnPropertyChanged(nameof(HasLastPromptContext));
                OnPropertyChanged(nameof(LastPromptContextLabel));
                OnPropertyChanged(nameof(FileChangesCount));
                OnPropertyChanged(nameof(FileChangesLabel));
                OnPropertyChanged(nameof(CanReviewFileChanges));
                OnPropertyChanged(nameof(ArchiveLabel));
                OnPropertyChanged(nameof(Model));
                OnPropertyChanged(nameof(Provider));
                OnPropertyChanged(nameof(IsLocalModel));
                OnPropertyChanged(nameof(IsHostedModel));
                OnPropertyChanged(nameof(IsOpenAIModel));
                OnPropertyChanged(nameof(CanOpenProjectActions));
                OnPropertyChanged(nameof(ProviderStatusLabel));
                OnPropertyChanged(nameof(IsPlanMode));
                OnPropertyChanged(nameof(PlanModeLabel));
                OnPropertyChanged(nameof(IsCodeTask));
                OnPropertyChanged(nameof(CodeTaskLabel));
                OnPropertyChanged(nameof(CanToggleCodeTaskMode));
                OnPropertyChanged(nameof(ShouldShowTaskChecklist));
                OnPropertyChanged(nameof(CanEditTaskChecklist));
                OnPropertyChanged(nameof(CanAddTaskChecklistItem));
                OnPropertyChanged(nameof(IncludeProjectContextForHosted));
                OnPropertyChanged(nameof(IncludeRepoMap));
                OnPropertyChanged(nameof(OutputStyle));
                OnPropertyChanged(nameof(ThinkEnabled));
                OnPropertyChanged(nameof(AdvancedModelSettingsLabel));
                OnPropertyChanged(nameof(CanIncludeRepoMap));
                OnPropertyChanged(nameof(SelectedModel));
                OnPropertyChanged(nameof(ContextSize));
                RefreshContextSizes(Model);
                ((RelayCommand)TogglePinCommand).NotifyCanExecuteChanged();
                ((RelayCommand)ArchiveConversationCommand).NotifyCanExecuteChanged();
                ((RelayCommand)CancelQueuedCommand).NotifyCanExecuteChanged();
                ((RelayCommand)TogglePlanModeCommand).NotifyCanExecuteChanged();
                ((RelayCommand)ToggleCodeTaskCommand).NotifyCanExecuteChanged();
                OnPropertyChanged(nameof(CanToggleCodeTaskMode));
                NotifyCodeTaskAvailabilityProperties();
                OnPropertyChanged(nameof(CanEditTaskChecklist));
                OnPropertyChanged(nameof(CanAddTaskChecklistItem));
            }
        }
    }
    public string ConversationTitle => ActiveConversation?.Title is { Length: > 0 } title ? title : "New conversation";
    public string PinLabel => ActiveConversation?.IsPinned == true ? "★  Pinned" : "☆  Pin";
    public string ArchiveLabel => ActiveConversation?.IsArchived == true ? "Restore" : "Archive";
    public string MessageCountLabel => $"Local conversation · {Messages.Count} messages";
    public bool HasCompactionSummary => ActiveConversation is { } conversation && !string.IsNullOrWhiteSpace(conversation.CompactionSummary);
    public string CompactionStatusLabel => HasCompactionSummary && ActiveConversation is { } conversation
        ? conversation.CompactionFromMessageCount == 0
            ? $"Earlier {conversation.CompactionThroughMessageCount} messages summarized for future prompts · original transcript preserved"
            : $"Messages {conversation.CompactionFromMessageCount + 1}–{conversation.CompactionThroughMessageCount} summarized for future prompts · original transcript preserved"
        : "";
    public bool ShouldOfferCompaction => ActiveConversation is { } conversation && !IsGenerating &&
        conversation.PendingRequestCount == 0 && !_queueProcessorRunning && _requestQueue.Count == 0 &&
        _lastPromptMessageCounts.TryGetValue(conversation.Id, out var messageCount) && messageCount == conversation.Messages.Count &&
        conversation.LastPromptModel.Equals(conversation.Model, StringComparison.OrdinalIgnoreCase) &&
        conversation.LastPromptContext > 0 && conversation.LastPromptContext == conversation.NumCtx &&
        Codev.ConversationCompactionService.FindBoundary(conversation.Messages, conversation.CompactionThroughMessageCount) > 0 &&
        Codev.ConversationCompactionService.ShouldOfferCompaction(conversation.LastPromptTokens, conversation.LastPromptContext);
    public bool ShouldWarnUnknownContext => ActiveConversation is { } conversation && !IsGenerating &&
        conversation.PendingRequestCount == 0 && !_queueProcessorRunning && _requestQueue.Count == 0 &&
        _lastPromptMessageCounts.TryGetValue(conversation.Id, out var messageCount) && messageCount == conversation.Messages.Count &&
        conversation.NumCtx <= 0 && conversation.LastPromptModel.Equals(conversation.Model, StringComparison.OrdinalIgnoreCase) &&
        Codev.ConversationCompactionService.ShouldWarnUnknownContext(conversation.LastPromptProvider, conversation.LastPromptTokens, conversation.LastPromptContext);
    public string UnknownContextWarningLabel => "Ollama is using the model's default context size, so Codev cannot tell how close this conversation is to its limit. Set a context size for usage-based compaction prompts, or compact manually from More.";
    public string CompactionOfferLabel => ActiveConversation is { LastPromptContext: > 0 } conversation
        ? $"This request used {Math.Round(100d * conversation.LastPromptTokens / conversation.LastPromptContext)}% of its selected context. Compact older turns before continuing?"
        : "This request used most of the selected context. Compact older turns before continuing?";
    public string ProjectLabel => ActiveConversation?.ProjectPath is { Length: > 0 } path
        ? _conversationWorkspaces.IsManagedWorkspace(path) ? $"Workspace · {Path.GetFileName(path)}" : Path.GetFileName(path) + " · " + path
        : "No project · Code task creates a workspace";
    public int FileChangesCount => ActiveConversation?.FileChanges?.Count ?? 0;
    public string FileChangesLabel => FileChangesCount == 0 ? "Files" : $"Files · {FileChangesCount}";
    public bool CanReviewFileChanges => HasProject && FileChangesCount > 0 && !IsGenerating && ActiveConversation?.PendingRequestCount == 0;
    public bool HasProject => ActiveConversation?.ProjectPath is { Length: > 0 } path && Directory.Exists(path);
    public bool IsProjectTrusted => ActiveConversation?.ProjectPath is { Length: > 0 } path && _projectFolderTrust.IsTrusted(path);
    public string? ProjectTrustRoot => ActiveConversation?.ProjectPath is { Length: > 0 } path ? _projectFolderTrust.FindTrustedRoot(path) : null;
    public bool IsProjectTrustInherited => IsProjectTrusted && ActiveConversation?.ProjectPath is { } path && !_projectFolderTrust.IsDirectTrustRoot(path);
    public Codev.ProjectCommandPermissionMode ProjectCommandPermissionMode => ActiveConversation?.ProjectPath is { Length: > 0 } path
        ? _projectCommandPermissions.GetMode(path) : Codev.ProjectCommandPermissionMode.AskEveryTime;
    public IReadOnlyList<Codev.ProjectCommandPermissionRule> ProjectCommandPermissionRules => ActiveConversation?.ProjectPath is { Length: > 0 } path
        ? _projectCommandPermissions.GetRules(path) : [];
    public bool CanPersistProjectCommandPermissions => _projectCommandPermissions.CanPersist;
    public string ProjectCommandPermissionStoreNotice => _projectCommandPermissions.LoadError ??
        "Command permissions are stored in Codev's local app data, outside the project folder.";
    public bool CanManageProjectTrust => HasProject && _projectFolderTrust.CanWrite && !IsFileSystemRoot(ActiveConversation!.ProjectPath!) && !IsProjectTrustInherited;
    public string ProjectTrustLabel => !HasProject ? "No folder" : !_projectFolderTrust.CanWrite ? "Trust settings unavailable" : IsFileSystemRoot(ActiveConversation!.ProjectPath!) ? "Choose project folder" :
        IsProjectTrustInherited ? "Trusted via parent" : IsProjectTrusted ? "Trusted · revoke" : "Untrusted · trust folder";
    public string ProjectTrustTooltip => IsProjectTrustInherited
        ? $"This folder inherits trust from {ProjectTrustRoot}. Attach that trusted root to revoke its trust."
        : !_projectFolderTrust.CanWrite ? $"Folder trust settings could not be loaded and were preserved: {_projectFolderTrust.LoadError}"
        : HasProject && IsFileSystemRoot(ActiveConversation!.ProjectPath!) ? "Trusting a filesystem root would trust every folder under it. Choose a narrower project folder."
        : "Trust or revoke trust for this project folder; untrusted folders can still be browsed and explicitly selected files can still be attached.";
    public bool HasSelectedContextFiles => SelectedContextFiles.Count > 0;
    public string ContextLabel => !HasProject ? "No project context" : SelectedContextFiles.Count > 0
        ? IsProjectTrusted ? $"{SelectedContextFiles.Count} file(s) selected · no other files will be included" : $"{SelectedContextFiles.Count} file(s) explicitly selected · folder untrusted"
        : IsProjectTrusted ? "Trusted project · bounded source files included automatically" : "Untrusted project · automatic context is off";
    public string ContextEstimateLabel => _contextEstimateLabel;
    public bool HasLastPromptContext => ActiveConversation is { } conversation && _lastPromptContexts.ContainsKey(conversation.Id);
    public string LastPromptContextLabel => ActiveConversation is { } conversation && _lastPromptContexts.TryGetValue(conversation.Id, out var snapshot)
        ? snapshot.ActualPromptTokens is { } actual ? $"Last request · {actual:N0} input tokens" : $"Last request · ≈{snapshot.EstimatedPromptTokens:N0} estimated tokens"
        : "View request context";
    public bool CanIncludeRepoMap => HasProject && (SelectedContextFiles.Count > 0 || IsProjectTrusted) && (!IsHostedModel || IncludeProjectContextForHosted);
    public string RepoMapEstimateLabel => IncludeRepoMap && CanIncludeRepoMap ? "Repo map: up to ≈2,000 tokens." : "";

    public string? GetLastPromptContextDetails() => ActiveConversation is { } conversation &&
        _lastPromptContexts.TryGetValue(conversation.Id, out var snapshot) ? snapshot.ToDisplayText() : null;
    public string ContextActionStatus { get; private set; } = "";
    public bool HasContextActionStatus => !string.IsNullOrWhiteSpace(ContextActionStatus);
    public string ConnectionStatus { get => _connectionStatus; private set => SetProperty(ref _connectionStatus, value); }
    public string ThemeLabel => _isDarkTheme ? "☼  Switch to light mode" : "☾  Switch to dark mode";
    public bool IsDarkTheme => _isDarkTheme;
    public string OllamaEndpointDisplay => _ollamaEndpoint.ToString().TrimEnd('/');
    public string SendButtonLabel => IsGenerating && string.IsNullOrWhiteSpace(Draft) && PendingDiffComments.Count == 0 ? "■" : "↑";
    public bool HasPendingDiffComments => PendingDiffComments.Count > 0;
    public bool HasTaskChecklist => TaskChecklistItems.Count > 0;
    public bool IsTaskChecklistEmpty => !HasTaskChecklist;
    public bool ShouldShowTaskChecklist => IsCodeTask || HasTaskChecklist;
    public bool CanEditTaskChecklist => !IsGenerating && (IsCodeTask || HasTaskChecklist);
    public bool CanAddTaskChecklistItem => CanEditTaskChecklist && TaskChecklistItems.Count < Codev.TaskChecklistService.MaxItems;
    public string TaskChecklistLabel => TaskChecklistItems.Count == 0
        ? "Task checklist · no steps yet"
        : $"Task checklist · {TaskChecklistItems.Count(item => item.Status == Codev.TaskChecklistService.Completed)}/{TaskChecklistItems.Count} done";
    public bool HasQueuedTurns => _requestQueue.Count > 0;
    public bool HasModels => Models.Any(choice => !string.IsNullOrWhiteSpace(choice.Name) && !string.IsNullOrWhiteSpace(choice.DisplayName));
    public string UserSlashCommandsFolder => UserSlashCommandsPath;
    public string? ProjectSlashCommandsFolder => HasProject && IsProjectTrusted
        ? Path.Combine(ActiveConversation!.ProjectPath!, ".codev", "commands")
        : null;
    public string UserSkillsFolder => UserSkillsPath;
    public string? ProjectSkillsFolder => HasProject && IsProjectTrusted
        ? Path.Combine(ActiveConversation!.ProjectPath!, ".codev", "skills")
        : null;
    public bool IsModelPickerPlaceholderVisible => !HasModels;
    public string ProviderStatusLabel => $"{(IsCodeTask ? "Code task" : IsPlanMode ? "Plan" : "Chat")} · {(IsLocalModel ? (Codev.OllamaEndpoint.IsLoopback(_ollamaEndpoint) ? "local Ollama" : "remote Ollama") : $"{Provider} hosted model")}";
    public bool IsPlanMode => ActiveConversation?.IsPlanMode ?? false;
    public string PlanModeLabel => IsPlanMode ? "Plan mode" : "Chat mode";
    public bool IsCodeTask => ActiveConversation?.IsCodeTask ?? false;
    public string CodeTaskLabel => IsCodeTask ? "Code task on" : CanEnterCodeTaskMode ? "Enable Code task" : "Code task unavailable";
    public string ConversationModeCycleTooltip => IsCodeTask
        ? "Ctrl+Shift+M switches Code task back to Chat."
        : IsPlanMode
            ? CanEnterCodeTaskMode ? "Ctrl+Shift+M switches Plan to Code task." : $"Ctrl+Shift+M switches Plan to Chat. {GetCodeTaskUnavailableReason()}"
            : $"Ctrl+Shift+M switches Chat to Plan. {GetCodeTaskUnavailableReason() ?? "Code task can also be selected."}";
    public bool CanEnterCodeTaskMode => GetCodeTaskUnavailableReason() is null;
    public bool CanEnableHostedCodeTaskConsent => ActiveConversation is not null && !IsGenerating &&
        IsOpenAIModel && _cloudRequestsEnabled && _cloudApiKeys.ContainsKey(Codev.CloudModelProviders.OpenAI) &&
        !IncludeProjectContextForHosted && (!HasProject || IsProjectTrusted);
    public bool CanToggleCodeTaskMode => ActiveConversation is not null && !IsGenerating;
    public bool ShowCodeTaskUnavailableReason => !IsCodeTask && GetCodeTaskUnavailableReason() is { } reason &&
        reason is not "Start or select a conversation first." &&
        reason is not "Wait for the current response to finish before changing conversation mode.";
    public string CodeTaskUnavailableReason => GetCodeTaskUnavailableReason() ?? "";
    public string CodeTaskTooltip => IsCodeTask
        ? $"Code task is on. {ProjectCommandPermissionMode switch
        {
            Codev.ProjectCommandPermissionMode.Allowlist => "Exact saved allow rules can skip approval; unlisted commands still ask.",
            Codev.ProjectCommandPermissionMode.ReadOnly => "Only recognized read-only inspections can skip approval; other commands ask.",
            _ => "Commands ask every time."
        }} Change this under Project actions → Command permissions. File changes still require review."
        : GetCodeTaskUnavailableReason() ?? "Enable Code task. Commands still follow the separate project command-approval policy.";

    private void NotifyCodeTaskAvailabilityProperties()
    {
        OnPropertyChanged(nameof(CanEnterCodeTaskMode));
        OnPropertyChanged(nameof(CodeTaskLabel));
        OnPropertyChanged(nameof(ShowCodeTaskUnavailableReason));
        OnPropertyChanged(nameof(CodeTaskUnavailableReason));
        OnPropertyChanged(nameof(CodeTaskTooltip));
        OnPropertyChanged(nameof(ConversationModeCycleTooltip));
        OnPropertyChanged(nameof(CanEnableHostedCodeTaskConsent));
        ((RelayCommand)EnableHostedCodeTaskConsentCommand).NotifyCanExecuteChanged();
    }

    private void EnableHostedCodeTaskConsent()
    {
        if (!CanEnableHostedCodeTaskConsent) return;
        IncludeProjectContextForHosted = true;
        ToggleCodeTaskMode();
    }
    public Func<string, string, string, bool, string?, IReadOnlyList<string>?, Task<bool>>? ReviewFileChangeAsync { get; set; }
    public Func<int, Task<bool>>? ConfirmConversationRewindAsync { get; set; }
    public Func<int, string, Task<string?>>? EditConversationPromptAsync { get; set; }
    public Func<Codev.ConversationCompactionProposal, Task>? ShowCompactionProposalAsync { get; set; }
    public Func<Codev.CodeTaskCommandProposal, Task<Codev.ProjectCommandApprovalChoice>>? ApproveProjectCommandAsync { get; set; }
    public Func<string, Task<bool>>? ConfirmRepeatedToolCallAsync { get; set; }
    public string ModelPickerPlaceholder => _isLoadingModels ? "Loading Ollama models…" :
        ConnectionStatus.StartsWith("Ollama connected", StringComparison.OrdinalIgnoreCase)
            ? Codev.OllamaEndpoint.IsLoopback(_ollamaEndpoint) ? "No local models installed" : "No models available from server"
            : "Ollama unavailable";
    public bool IsQueuePaused => _queuePaused;
    public bool CanClearConversation => Codev.ConversationHistoryClearService.CanClear(ActiveConversation,
        ActiveConversation is { } conversation && ReferenceEquals(_generationConversation, conversation));
    public string QueueStatusLabel => HasQueuedTurns
        ? _queuePaused ? $"{_requestQueue.Count} request(s) saved · resume when ready" : $"{_requestQueue.Count} request(s) queued"
        : "";
    public bool IsGenerating
    {
        get => _isGenerating;
        private set
        {
            if (SetProperty(ref _isGenerating, value))
            {
                OnPropertyChanged(nameof(SendButtonLabel));
                OnPropertyChanged(nameof(QueueStatusLabel));
                OnPropertyChanged(nameof(ShouldOfferCompaction));
                OnPropertyChanged(nameof(ShouldWarnUnknownContext));
                OnPropertyChanged(nameof(CanReviewFileChanges));
                ((RelayCommand)SendCommand).NotifyCanExecuteChanged();
                ((RelayCommand)RewindConversationCommand).NotifyCanExecuteChanged();
                ((RelayCommand)SummarizeConversationUpToCommand).NotifyCanExecuteChanged();
                ((RelayCommand)SummarizeConversationFromCommand).NotifyCanExecuteChanged();
                ((RelayCommand)StopGenerationCommand).NotifyCanExecuteChanged();
                ((RelayCommand)TogglePlanModeCommand).NotifyCanExecuteChanged();
                ((RelayCommand)ToggleCodeTaskCommand).NotifyCanExecuteChanged();
                OnPropertyChanged(nameof(CanToggleCodeTaskMode));
                NotifyCodeTaskAvailabilityProperties();
                OnPropertyChanged(nameof(CanEditTaskChecklist));
            }
        }
    }
    public string SearchText { get => _searchText; set { if (SetProperty(ref _searchText, value)) RebuildLists(); } }
    public string Draft
    {
        get => _draft;
        set
        {
            if (!SetProperty(ref _draft, value)) return;
            if (ActiveConversation is { } conversation) conversation.Draft = value;
            ((RelayCommand)SendCommand).NotifyCanExecuteChanged();
            OnPropertyChanged(nameof(SendButtonLabel));
            _draftSaveTimer.Stop();
            _draftSaveTimer.Start();
        }
    }
    public string Model
    {
        get => ActiveConversation?.Model ?? _model;
        set
        {
            if (string.IsNullOrWhiteSpace(value) || string.Equals(Model, value, StringComparison.OrdinalIgnoreCase)) return;
            if (ActiveConversation is null) SetProperty(ref _model, value);
            else { ActiveConversation.Model = value; ActiveConversation.Provider = "ollama"; OnPropertyChanged(); OnPropertyChanged(nameof(Provider)); OnPropertyChanged(nameof(IsLocalModel)); OnPropertyChanged(nameof(ProviderStatusLabel)); OnPropertyChanged(nameof(SelectedModel)); Persist(); }
            _provider = "ollama";
            RefreshContextSizes(value);
            RefreshContextEstimate();
            _ = WarmModelAsync(value);
        }
    }

    public string Provider => ActiveConversation?.Provider ?? _provider;
    public bool IsLocalModel => Provider == "ollama";
    public bool IsHostedModel => Codev.CloudModelProviders.IsCloud(Provider);
    public bool IsOpenAIModel => Provider == Codev.CloudModelProviders.OpenAI;
    public bool CanOpenProjectActions => HasProject || IsOpenAIModel;

    public string OutputStyle
    {
        get => Codev.ConversationOutputStyles.Normalize(ActiveConversation?.OutputStyle ?? _outputStyle);
        set
        {
            var normalized = Codev.ConversationOutputStyles.Normalize(value);
            if (string.Equals(OutputStyle, normalized, StringComparison.Ordinal)) return;
            if (ActiveConversation is null) _outputStyle = normalized;
            else ActiveConversation.OutputStyle = normalized;
            OnPropertyChanged();
            Persist();
        }
    }

    public bool ThinkEnabled
    {
        get => ActiveConversation?.ThinkEnabled ?? _thinkEnabled;
        set
        {
            if (ThinkEnabled == value) return;
            if (ActiveConversation is { } conversation) conversation.ThinkEnabled = value;
            else _thinkEnabled = value;
            OnPropertyChanged();
            Persist();
        }
    }

    public string AdvancedModelSettingsLabel => ActiveConversation is { } conversation &&
        (conversation.Temperature.HasValue || conversation.TopP.HasValue || conversation.TopK.HasValue ||
         conversation.PresencePenalty.HasValue || conversation.RepeatPenalty.HasValue || conversation.NumPredict.HasValue)
        ? "Advanced ·" : "Advanced";

    public void SetSamplingSettings(double? temperature, double? topP, int? topK,
        double? presencePenalty, double? repeatPenalty, int? numPredict)
    {
        if (ActiveConversation is not { } conversation) return;
        conversation.Temperature = Codev.ConversationSamplingSettings.NormalizeTemperature(temperature);
        conversation.TopP = Codev.ConversationSamplingSettings.NormalizeProbability(topP);
        conversation.TopK = Codev.ConversationSamplingSettings.NormalizeTopK(topK);
        conversation.PresencePenalty = Codev.ConversationSamplingSettings.NormalizePenalty(presencePenalty);
        conversation.RepeatPenalty = Codev.ConversationSamplingSettings.NormalizePenalty(repeatPenalty);
        conversation.NumPredict = Codev.ConversationSamplingSettings.NormalizeOutputTokens(numPredict);
        OnPropertyChanged(nameof(AdvancedModelSettingsLabel));
        Persist();
    }

    public Task<IReadOnlyDictionary<string, string>> LoadDeclaredModelDefaultsAsync(string model,
        CancellationToken cancellationToken = default) =>
        new Codev.OllamaModelParameterClient(_http, _ollamaEndpoint)
            .GetDeclaredDefaultsAsync(model, cancellationToken);

    public async Task<string> DraftCommitMessageAsync(Codev.GitStagedReview stagedReview, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stagedReview);
        if (ActiveConversation is not { ProjectPath: { Length: > 0 } projectPath } conversation || !IsProjectTrusted)
            throw new InvalidOperationException("Attach and trust a project folder before asking a model to draft a commit message.");
        if (conversation.Provider != "ollama" || !Codev.OllamaEndpoint.IsLoopback(_ollamaEndpoint) ||
            !Models.Any(choice => choice.Provider == "ollama" && RemoveLatestTag(choice.Name).Equals(RemoveLatestTag(conversation.Model), StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Commit-message drafts use only an installed local Ollama model on a loopback endpoint.");
        if (IsGenerating || HasQueuedTurns || _queueProcessorRunning)
            throw new InvalidOperationException("Wait for active and queued requests to finish before drafting a commit message.");

        var model = conversation.Model;
        var endpoint = _ollamaEndpoint;
        var boundedDiff = stagedReview.Diff.Length > Codev.GitCommitMessagePromptBuilder.MaxDiffCharacters
            ? stagedReview.Diff[..Codev.GitCommitMessagePromptBuilder.MaxDiffCharacters]
            : stagedReview.Diff;
        var wasTruncated = boundedDiff.Length < stagedReview.Diff.Length;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));
        _generationCancellation = timeout;
        IsGenerating = true;
        try
        {
            await SetConnectionStatusAsync("Drafting commit message with local Ollama…");
            if (!_projectFolderTrust.IsTrusted(projectPath)) throw new InvalidOperationException("Project trust was revoked. No staged diff was sent to the model.");
            var repository = new Codev.GitRepositoryService(projectPath);
            var recentSubjects = await repository.GetRecentCommitSubjectsAsync(cancellationToken: timeout.Token);
            var messages = Codev.GitCommitMessagePromptBuilder.Build(boundedDiff, recentSubjects);
            var options = Codev.OllamaRequestOptions.Build(conversation.NumCtx, conversation.Temperature, conversation.TopP,
                conversation.TopK, conversation.PresencePenalty, conversation.RepeatPenalty, 180) ?? new Dictionary<string, object>();
            if (conversation.NumCtx > 0) options["num_ctx"] = conversation.NumCtx;
            options["num_predict"] = 180;
            var payload = new Dictionary<string, object> { ["model"] = model, ["messages"] = messages, ["stream"] = false, ["think"] = false, ["options"] = options };
            using var response = await _http.PostAsJsonAsync(Codev.OllamaEndpoint.ApiUri(endpoint, "api/chat"), payload, timeout.Token);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"Ollama returned HTTP {(int)response.StatusCode} ({response.ReasonPhrase}). {await response.Content.ReadAsStringAsync(timeout.Token)}");
            using var result = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(timeout.Token), cancellationToken: timeout.Token);
            if (!result.RootElement.TryGetProperty("message", out var message) || !message.TryGetProperty("content", out var content) || string.IsNullOrWhiteSpace(content.GetString()))
                throw new InvalidOperationException("The selected model returned no commit-message draft.");
            if (!_projectFolderTrust.IsTrusted(projectPath)) throw new InvalidOperationException("Project trust was revoked while drafting. No commit was created.");
            var currentReview = await repository.GetStagedReviewAsync(timeout.Token);
            if (!string.Equals(currentReview.TreeId, stagedReview.TreeId, StringComparison.Ordinal) || !string.Equals(currentReview.Diff, stagedReview.Diff, StringComparison.Ordinal))
                throw new InvalidOperationException("The staged changes changed while drafting. Review the updated staged diff before committing.");
            var draft = content.GetString()!.Trim().Trim('`');
            ReportContextActionStatus(wasTruncated
                ? "Commit-message draft is ready; it used only the first 40,000 characters of the staged diff."
                : "Commit-message draft is ready to edit.");
            return draft;
        }
        finally
        {
            _generationCancellation = null;
            IsGenerating = false;
            await SetConnectionStatusAsync("Ollama ready");
        }
    }

    public async Task<string> DraftPullRequestDescriptionAsync(string baseBranch, CancellationToken cancellationToken = default)
    {
        if (ActiveConversation is not { ProjectPath: { Length: > 0 } projectPath } conversation || !IsProjectTrusted)
            throw new InvalidOperationException("Attach and trust a project folder before drafting a pull request description.");
        if (conversation.Provider != "ollama" || !Codev.OllamaEndpoint.IsLoopback(_ollamaEndpoint) ||
            !Models.Any(choice => choice.Provider == "ollama" && RemoveLatestTag(choice.Name).Equals(RemoveLatestTag(conversation.Model), StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Pull request drafts use only an installed local Ollama model on a loopback endpoint.");
        if (IsGenerating || HasQueuedTurns || _queueProcessorRunning)
            throw new InvalidOperationException("Wait for active and queued requests to finish before drafting a pull request description.");

        var model = conversation.Model;
        var endpoint = _ollamaEndpoint;
        var repository = new Codev.GitRepositoryService(projectPath);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));
        _generationCancellation = timeout;
        IsGenerating = true;
        try
        {
            await SetConnectionStatusAsync("Reading branch diff for local PR draft…");
            if (!_projectFolderTrust.IsTrusted(projectPath)) throw new InvalidOperationException("Project trust was revoked. No branch diff was sent to the model.");
            var snapshot = await repository.GetBranchReviewAsync(baseBranch, timeout.Token);
            if (snapshot.Files.Count == 0) throw new InvalidOperationException("The current branch has no changes relative to that base branch.");
            var boundedDiff = snapshot.Diff.Length > Codev.GitPullRequestPromptBuilder.MaxDiffCharacters
                ? snapshot.Diff[..Codev.GitPullRequestPromptBuilder.MaxDiffCharacters]
                : snapshot.Diff;
            var boundedSnapshot = snapshot with { Diff = boundedDiff, Truncated = snapshot.Truncated || boundedDiff.Length < snapshot.Diff.Length };
            if (!_projectFolderTrust.IsTrusted(projectPath)) throw new InvalidOperationException("Project trust was revoked. No branch diff was sent to the model.");
            var messages = Codev.GitPullRequestPromptBuilder.Build(boundedSnapshot);
            var options = Codev.OllamaRequestOptions.Build(conversation.NumCtx, conversation.Temperature, conversation.TopP,
                conversation.TopK, conversation.PresencePenalty, conversation.RepeatPenalty, 500) ?? new Dictionary<string, object>();
            if (conversation.NumCtx > 0) options["num_ctx"] = conversation.NumCtx;
            options["num_predict"] = 500;
            var payload = new Dictionary<string, object> { ["model"] = model, ["messages"] = messages, ["stream"] = false, ["think"] = false, ["options"] = options };
            await SetConnectionStatusAsync("Drafting PR title and description with local Ollama…");
            using var response = await _http.PostAsJsonAsync(Codev.OllamaEndpoint.ApiUri(endpoint, "api/chat"), payload, timeout.Token);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"Ollama returned HTTP {(int)response.StatusCode} ({response.ReasonPhrase}). {await response.Content.ReadAsStringAsync(timeout.Token)}");
            using var result = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(timeout.Token), cancellationToken: timeout.Token);
            if (!result.RootElement.TryGetProperty("message", out var message) || !message.TryGetProperty("content", out var content) || string.IsNullOrWhiteSpace(content.GetString()))
                throw new InvalidOperationException("The selected model returned no pull request draft.");
            if (!_projectFolderTrust.IsTrusted(projectPath)) throw new InvalidOperationException("Project trust was revoked while drafting. No PR was created or sent.");
            var current = await repository.GetBranchReviewAsync(baseBranch, timeout.Token);
            if (!string.Equals(current.Branch, snapshot.Branch, StringComparison.Ordinal) || !string.Equals(current.Diff, snapshot.Diff, StringComparison.Ordinal))
                throw new InvalidOperationException("The branch diff changed while drafting. Review the updated branch before using this draft.");
            ReportContextActionStatus("PR title and description draft is ready to copy and edit. No PR was created or sent.");
            return content.GetString()!.Trim().Trim('`');
        }
        finally
        {
            _generationCancellation = null;
            IsGenerating = false;
            await SetConnectionStatusAsync("Ollama ready");
        }
    }

    public async Task<string?> ReviewUncommittedChangesAsync(CancellationToken cancellationToken = default, bool securityFocused = false, string? commit = null, string? baseBranch = null)
    {
        if (ActiveConversation is not { } conversation || conversation.ProjectPath is not { Length: > 0 } projectPath || !IsProjectTrusted)
        {
            ReportContextActionStatus($"{(securityFocused ? "/security-review" : "/review")} needs an attached, trusted Git project.");
            return null;
        }
        var reviewModel = conversation.Model;
        var reviewEndpoint = _ollamaEndpoint;
        var reviewContext = conversation.NumCtx;
        var reviewTemperature = conversation.Temperature;
        var reviewTopP = conversation.TopP;
        var reviewTopK = conversation.TopK;
        var reviewPresencePenalty = conversation.PresencePenalty;
        var reviewRepeatPenalty = conversation.RepeatPenalty;
        var reviewOutputTokens = Math.Min(conversation.NumPredict ?? 3000, 3000);
        if (conversation.Provider != "ollama" || !Codev.OllamaEndpoint.IsLoopback(reviewEndpoint))
        {
            if (!securityFocused)
            {
                ReportContextActionStatus("/review uses only the selected local Ollama model and a loopback Ollama endpoint. Switch back to local Ollama to continue.");
                return null;
            }
        }
        if (conversation.Provider == "ollama" && !Codev.OllamaEndpoint.IsLoopback(reviewEndpoint))
        {
            reviewModel = "";
        }
        if (!string.IsNullOrWhiteSpace(reviewModel) && (conversation.Provider != "ollama" || !Models.Any(choice => choice.Provider == "ollama" &&
            RemoveLatestTag(choice.Name).Equals(RemoveLatestTag(reviewModel), StringComparison.OrdinalIgnoreCase))))
        {
            reviewModel = "";
        }
        if (!string.IsNullOrWhiteSpace(reviewModel) && !Codev.OllamaEndpoint.IsLoopback(reviewEndpoint)) reviewModel = "";
        if (string.IsNullOrWhiteSpace(reviewModel) && !securityFocused)
        {
            ReportContextActionStatus("Choose an installed Ollama model before starting /review.");
            return null;
        }
        if (IsGenerating || HasQueuedTurns || _queueProcessorRunning)
        {
            ReportContextActionStatus("Wait for active and queued requests to finish before starting a Git review.");
            return null;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(3));
        var reviewTimer = Stopwatch.StartNew();
        _isReviewRunning = true;
        _generationCancellation = timeout;
        IsGenerating = true;
        try
        {
            var commandName = securityFocused ? "/security-review" : "/review";
            await SetConnectionStatusAsync($"{commandName} · reading local Git changes…");
            var repository = new Codev.GitRepositoryService(projectPath);
            var snapshot = !string.IsNullOrWhiteSpace(commit)
                ? await repository.GetCommitReviewAsync(commit, timeout.Token)
                : !string.IsNullOrWhiteSpace(baseBranch)
                    ? await repository.GetBranchReviewAsync(baseBranch, timeout.Token)
                    : await repository.GetWorkingTreeReviewAsync(timeout.Token);
            if (!_projectFolderTrust.IsTrusted(projectPath))
            {
                ReportContextActionStatus("Project trust was revoked while preparing the review. No diff was sent to the model.");
                return null;
            }
            if (snapshot.Files.Count == 0)
            {
                ReportContextActionStatus(string.IsNullOrWhiteSpace(commit) && string.IsNullOrWhiteSpace(baseBranch)
                    ? $"{commandName} found no uncommitted Git changes."
                    : string.IsNullOrWhiteSpace(commit) ? "The selected branch has no changes relative to the current branch to review."
                    : "That commit has no file changes to review.");
                return null;
            }

            var localFindings = securityFocused ? Codev.GitSecretPatternScanner.Scan(snapshot) : [];
            string? modelFindings = null;
            if (!string.IsNullOrWhiteSpace(reviewModel))
            {
                if (!_projectFolderTrust.IsTrusted(projectPath))
                {
                    ReportContextActionStatus("Project trust was revoked before the review could send its diff. No changes were sent to the model.");
                    return null;
                }
                var messages = Codev.GitReviewPromptBuilder.Build(snapshot, securityFocused);
                var options = Codev.OllamaRequestOptions.Build(reviewContext, reviewTemperature, reviewTopP, reviewTopK,
                    reviewPresencePenalty, reviewRepeatPenalty, reviewOutputTokens) ?? new Dictionary<string, object>();
                if (reviewContext > 0) options["num_ctx"] = reviewContext;
                options["num_predict"] = reviewOutputTokens;
                var payload = new Dictionary<string, object>
                {
                    ["model"] = reviewModel, ["messages"] = messages, ["stream"] = false, ["think"] = false, ["options"] = options
                };
                await SetConnectionStatusAsync($"{(securityFocused ? "/security-review" : "/review")} · {snapshot.Files.Count} changed files · local second opinion…");
                using var response = await _http.PostAsJsonAsync(Codev.OllamaEndpoint.ApiUri(reviewEndpoint, "api/chat"), payload, timeout.Token);
                if (!response.IsSuccessStatusCode)
                    throw new InvalidOperationException($"Ollama returned HTTP {(int)response.StatusCode} ({response.ReasonPhrase}). {await response.Content.ReadAsStringAsync(timeout.Token)}");
                using var result = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(timeout.Token), cancellationToken: timeout.Token);
                if (!result.RootElement.TryGetProperty("message", out var message) || !message.TryGetProperty("content", out var content) || string.IsNullOrWhiteSpace(content.GetString()))
                    throw new InvalidOperationException("The selected model returned no review findings.");
                modelFindings = content.GetString()?.Trim();
            }
            if (securityFocused && string.IsNullOrWhiteSpace(modelFindings))
                ReportContextActionStatus("/security-review complete · deterministic local secret scan only · no diff was sent to a model");
            else
                ReportContextActionStatus($"{(securityFocused ? "/security-review" : "/review")} complete · {snapshot.Files.Count} files · read-only local second opinion");
            var truncationNote = snapshot.Truncated ? "\n\n_Codev capped the review input; some changed content may not have been included._" : "";
            var secretReport = securityFocused
                ? (snapshot.Truncated ? "The diff was truncated; this scan covers only the included portion and may miss findings.\n" : "") +
                  (localFindings.Count == 0 ? "No common secret patterns were found on added lines." : string.Join("\n", localFindings.Select(f => $"- Possible {f.Kind} in `{f.FilePath}:{f.Line}` (value hidden)")) )
                : null;
            var body = securityFocused
                ? "Local secret-pattern scan\n" + secretReport + (modelFindings is null ? "" : "\n\nLocal model security review\n" + modelFindings)
                : modelFindings ?? "No model review was available.";
            return $"Read-only {(securityFocused ? "security review" : "second opinion")} · {snapshot.Branch} · {snapshot.Files.Count} files" +
                (snapshot.Truncated ? " · input capped" : "") + "\n\n" + body + truncationNote;
        }
        catch (OperationCanceledException)
        {
            ReportContextActionStatus(reviewTimer.Elapsed >= TimeSpan.FromMinutes(3)
                ? "Review timed out after three minutes. No project files were changed."
                : "Review canceled. No project files were changed.");
            return null;
        }
        catch (Exception ex)
        {
            ReportContextActionStatus(securityFocused && string.IsNullOrWhiteSpace(reviewModel)
                ? $"Could not scan local Git changes: {ex.Message}"
                : $"Could not review local Git changes: {ex.Message}");
            return null;
        }
        finally
        {
            _isReviewRunning = false;
            _generationCancellation = null;
            IsGenerating = false;
            await SetConnectionStatusAsync("Ollama ready");
        }
    }

    private void TogglePlanMode()
    {
        if (ActiveConversation is not { } conversation || IsGenerating) return;
        SetConversationMode(conversation.IsPlanMode ? ConversationMode.Chat : ConversationMode.Plan);
    }

    public void CycleConversationMode()
    {
        if (ActiveConversation is not { } conversation) return;
        if (IsGenerating)
        {
            ReportContextActionStatus("Wait for the current response to finish before changing conversation mode.");
            return;
        }
        var current = conversation.IsCodeTask ? ConversationMode.CodeTask : conversation.IsPlanMode ? ConversationMode.Plan : ConversationMode.Chat;
        var codeTaskUnavailableReason = CanEnterCodeTaskMode ? null : GetCodeTaskUnavailableReason();
        var next = Codev.ConversationModeCycle.Next(current, CanEnterCodeTaskMode);
        if (next == ConversationMode.CodeTask)
        {
            ToggleCodeTaskMode();
            return;
        }
        SetConversationMode(next);
        if (current == ConversationMode.Plan && next == ConversationMode.Chat)
            ReportContextActionStatus($"Code task is unavailable: {codeTaskUnavailableReason ?? "no supported provider is ready"} Switched to Chat mode.");
    }

    private void SetConversationMode(ConversationMode mode)
    {
        if (ActiveConversation is not { } conversation) return;
        conversation.IsPlanMode = mode == ConversationMode.Plan;
        conversation.IsCodeTask = mode == ConversationMode.CodeTask;
        OnPropertyChanged(nameof(IsPlanMode));
        OnPropertyChanged(nameof(PlanModeLabel));
        OnPropertyChanged(nameof(IsCodeTask));
        OnPropertyChanged(nameof(CodeTaskLabel));
        OnPropertyChanged(nameof(ConversationModeCycleTooltip));
        OnPropertyChanged(nameof(ShouldShowTaskChecklist));
        OnPropertyChanged(nameof(CanEditTaskChecklist));
        OnPropertyChanged(nameof(CanAddTaskChecklistItem));
        OnPropertyChanged(nameof(HasLastPromptContext));
        OnPropertyChanged(nameof(LastPromptContextLabel));
        OnPropertyChanged(nameof(CanEnterCodeTaskMode));
        NotifyCodeTaskAvailabilityProperties();
        OnPropertyChanged(nameof(CanToggleCodeTaskMode));
        OnPropertyChanged(nameof(ProviderStatusLabel));
        ((RelayCommand)ToggleCodeTaskCommand).NotifyCanExecuteChanged();
        ((RelayCommand)TogglePlanModeCommand).NotifyCanExecuteChanged();
        Persist();
    }

    public async Task SetProjectCommandPermissionModeAsync(Codev.ProjectCommandPermissionMode mode)
    {
        if (ActiveConversation?.ProjectPath is not { Length: > 0 } path) return;
        await _projectCommandPermissions.SetModeAsync(path, mode);
        OnPropertyChanged(nameof(ProjectCommandPermissionMode));
        ReportContextActionStatus(mode switch
        {
            Codev.ProjectCommandPermissionMode.Allowlist => "Project allowlist mode enabled. Exact saved allow rules skip approval; unlisted commands still ask, and saved denials always block.",
            Codev.ProjectCommandPermissionMode.ReadOnly => "Read-only command mode enabled. Only simple inspection commands with project-relative paths can skip approval; all other commands still ask.",
            _ => "Project commands will ask for approval every time; saved denials remain in force."
        });
    }

    public async Task RemoveProjectCommandPermissionRuleAsync(string command, Codev.ProjectCommandPermissionDecision decision)
    {
        if (ActiveConversation?.ProjectPath is not { Length: > 0 } path) return;
        await _projectCommandPermissions.RemoveRuleAsync(path, command, decision);
        OnPropertyChanged(nameof(ProjectCommandPermissionRules));
        ReportContextActionStatus("Saved command permission rule removed.");
    }

    private async Task<Codev.CommandApprovalOutcome> ApproveCommandWithProjectPolicyAsync(Codev.CodeTaskCommandProposal proposal,
        IReadOnlyList<string> contextExclusions)
    {
        var decision = _projectCommandPermissions.Evaluate(proposal.ProjectPath, proposal.Command, proposal.ShellName,
            allowReadOnly: !proposal.IsVerification, contextExclusions: contextExclusions);
        if (decision == Codev.ProjectCommandPermissionDecision.Deny)
        {
            _ = SetConnectionStatusAsync("Project command permission denied this exact command; it was not run.");
            return Codev.CommandApprovalOutcome.Denied;
        }
        if (decision == Codev.ProjectCommandPermissionDecision.Allow)
        {
            if (!proposal.IsVerification && _projectCommandPermissions.GetMode(proposal.ProjectPath) == Codev.ProjectCommandPermissionMode.ReadOnly)
            {
                _ = SetConnectionStatusAsync("Recognized read-only command; running through Codev's bounded file inspection, without launching a shell.");
                return Codev.CommandApprovalOutcome.ApprovedReadOnly;
            }
            _ = SetConnectionStatusAsync("Exact project allowlist match; running the previously approved command.");
            return Codev.CommandApprovalOutcome.Approved;
        }

        var choice = await (ApproveProjectCommandAsync?.Invoke(proposal) ?? Task.FromResult(Codev.ProjectCommandApprovalChoice.Cancel));
        switch (choice)
        {
            case Codev.ProjectCommandApprovalChoice.RunOnce:
                return Codev.CommandApprovalOutcome.Approved;
            case Codev.ProjectCommandApprovalChoice.AllowExactCommand:
                try
                {
                    await _projectCommandPermissions.SetRuleAsync(proposal.ProjectPath, proposal.Command,
                        Codev.ProjectCommandPermissionDecision.Allow, Codev.ProjectCommandPermissionMode.Allowlist);
                    OnPropertyChanged(nameof(ProjectCommandPermissionMode));
                    OnPropertyChanged(nameof(ProjectCommandPermissionRules));
                    _ = SetConnectionStatusAsync("Exact command added to this project's allowlist and approved for this run.");
                    return Codev.CommandApprovalOutcome.Approved;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
                {
                    _ = SetConnectionStatusAsync($"Could not save the project allow rule ({ex.GetType().Name}); command was not run.");
                    return Codev.CommandApprovalOutcome.Rejected;
                }
            case Codev.ProjectCommandApprovalChoice.DenyExactCommand:
                try
                {
                    await _projectCommandPermissions.SetRuleAsync(proposal.ProjectPath, proposal.Command,
                        Codev.ProjectCommandPermissionDecision.Deny);
                    OnPropertyChanged(nameof(ProjectCommandPermissionRules));
                    _ = SetConnectionStatusAsync("Exact command denied for this project; it was not run.");
                    return Codev.CommandApprovalOutcome.Denied;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
                {
                    _ = SetConnectionStatusAsync($"Could not save the project deny rule ({ex.GetType().Name}); command was not run.");
                    return Codev.CommandApprovalOutcome.Rejected;
                }
            default:
                return Codev.CommandApprovalOutcome.Rejected;
        }
    }

    private async void ToggleCodeTaskMode()
    {
        if (ActiveConversation is not { } conversation || IsGenerating) return;
        if (conversation.IsCodeTask) SetConversationMode(ConversationMode.Chat);
        else if (GetCodeTaskUnavailableReason() is { } reason)
        {
            ReportContextActionStatus(reason);
            return;
        }
        else
        {
            try
            {
                if (string.IsNullOrWhiteSpace(conversation.ProjectPath))
                {
                    var workspace = _conversationWorkspaces.GetOrCreateWorkspace(conversation.Id);
                    await _projectFolderTrust.TrustAsync(workspace);
                    SetProjectFolder(workspace);
                    ContextActionStatus = $"Created a dedicated Codev workspace for this conversation: {Path.GetFileName(workspace)}";
                    OnPropertyChanged(nameof(ContextActionStatus));
                    OnPropertyChanged(nameof(HasContextActionStatus));
                }
                SetConversationMode(ConversationMode.CodeTask);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
            {
                ReportContextActionStatus($"Could not prepare a Code task workspace: {ex.Message}");
            }
        }
    }

    public string? GetCodeTaskUnavailableReason()
    {
        if (ActiveConversation is null) return "Start or select a conversation first.";
        if (IsGenerating) return "Wait for the current response to finish before changing conversation mode.";
        if (HasProject && !IsProjectTrusted) return "Trust the attached project folder before enabling Code task.";
        if (Provider == Codev.CloudModelProviders.Anthropic) return "Hosted Code task currently supports OpenAI only. Anthropic models remain available for chat and Plan mode.";
        if (Provider == Codev.CloudModelProviders.OpenAI)
        {
            if (!_cloudRequestsEnabled || !_cloudApiKeys.ContainsKey(Provider)) return "Connect OpenAI and approve hosted requests before enabling Code task.";
            if (!IncludeProjectContextForHosted)
                return "OpenAI Code task sends requested workspace files and tool results to OpenAI. Allow this for the conversation to enable Code task.";
            return null;
        }
        if (!IsLocalModel) return "Select an installed local Ollama model for Code task.";
        if (!Codev.OllamaEndpoint.IsLoopback(_ollamaEndpoint)) return "Code task requires Ollama at a local loopback address (127.0.0.1 or localhost).";
        if (!Models.Any(choice => choice.Provider == "ollama" && RemoveLatestTag(choice.Name).Equals(RemoveLatestTag(Model), StringComparison.OrdinalIgnoreCase)))
            return "Select an installed Ollama model; the current model is not in the available local model list.";
        return null;
    }
    public bool CloudRequestsEnabled => _cloudRequestsEnabled;
    public bool IncludeProjectContextForHosted
    {
        get => ActiveConversation?.IncludeProjectContextForHosted ?? false;
        set
        {
            if (ActiveConversation is not { } conversation || conversation.IncludeProjectContextForHosted == value) return;
            conversation.IncludeProjectContextForHosted = value;
            if (!value && conversation.IsCodeTask && conversation.Provider == Codev.CloudModelProviders.OpenAI &&
                ReferenceEquals(_generationConversation, conversation)) _generationCancellation?.Cancel();
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanIncludeRepoMap));
            OnPropertyChanged(nameof(RepoMapEstimateLabel));
            NotifyCodeTaskAvailabilityProperties();
            RefreshContextEstimate();
            Persist();
        }
    }

    public bool IncludeRepoMap
    {
        get => ActiveConversation?.IncludeRepoMap ?? false;
        set
        {
            if (ActiveConversation is not { } conversation || conversation.IncludeRepoMap == value) return;
            if (value && !CanIncludeRepoMap) return;
            conversation.IncludeRepoMap = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(RepoMapEstimateLabel));
            RefreshContextEstimate();
            Persist();
        }
    }

    public ModelChoice? SelectedModel
    {
        get => Models.FirstOrDefault(choice => choice.Provider.Equals(Provider, StringComparison.OrdinalIgnoreCase) &&
            RemoveLatestTag(choice.Name).Equals(RemoveLatestTag(Model), StringComparison.OrdinalIgnoreCase));
        set
        {
            if (value is not null) SelectModel(value);
        }
    }

    private void SelectModel(ModelChoice choice)
    {
        if (_isUnloadingModel)
        {
            ReportContextActionStatus("Wait for the current Ollama unload operation before changing models.");
            OnPropertyChanged(nameof(SelectedModel));
            OnPropertyChanged(nameof(ShouldOfferCompaction));
            OnPropertyChanged(nameof(ShouldWarnUnknownContext));
            return;
        }
        if (IsCodeTask && choice.Provider != "ollama")
        {
            ReportContextActionStatus("Turn Code task mode off before switching its provider or model.");
            OnPropertyChanged(nameof(SelectedModel));
            OnPropertyChanged(nameof(ShouldOfferCompaction));
            OnPropertyChanged(nameof(ShouldWarnUnknownContext));
            return;
        }
        if (string.Equals(Provider, choice.Provider, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(Model, choice.Name, StringComparison.OrdinalIgnoreCase)) return;
        if (ActiveConversation is null)
        {
            _model = choice.Name;
            _provider = choice.Provider;
            OnPropertyChanged(nameof(Model));
            OnPropertyChanged(nameof(Provider));
            OnPropertyChanged(nameof(IsLocalModel));
            OnPropertyChanged(nameof(IsHostedModel));
            OnPropertyChanged(nameof(IsOpenAIModel));
            OnPropertyChanged(nameof(CanOpenProjectActions));
            OnPropertyChanged(nameof(ProviderStatusLabel));
            OnPropertyChanged(nameof(SelectedModel));
            OnPropertyChanged(nameof(ShouldOfferCompaction));
            OnPropertyChanged(nameof(ShouldWarnUnknownContext));
            OnPropertyChanged(nameof(CanToggleCodeTaskMode));
            NotifyCodeTaskAvailabilityProperties();
            ((RelayCommand)ToggleCodeTaskCommand).NotifyCanExecuteChanged();
        }
        else
        {
            ActiveConversation.Model = choice.Name;
            ActiveConversation.Provider = choice.Provider;
            OnPropertyChanged(nameof(Model));
            OnPropertyChanged(nameof(Provider));
            OnPropertyChanged(nameof(IsLocalModel));
            OnPropertyChanged(nameof(IsHostedModel));
            OnPropertyChanged(nameof(IsOpenAIModel));
            OnPropertyChanged(nameof(CanOpenProjectActions));
            OnPropertyChanged(nameof(ProviderStatusLabel));
            OnPropertyChanged(nameof(SelectedModel));
            OnPropertyChanged(nameof(ShouldOfferCompaction));
            OnPropertyChanged(nameof(ShouldWarnUnknownContext));
            OnPropertyChanged(nameof(CanToggleCodeTaskMode));
            NotifyCodeTaskAvailabilityProperties();
            ((RelayCommand)ToggleCodeTaskCommand).NotifyCanExecuteChanged();
            Persist();
        }
        RefreshContextSizes(choice.Name);
        RefreshContextEstimate();
        if (choice.Provider == "ollama") _ = WarmModelAsync(choice.Name);
        else ConnectionStatus = _cloudRequestsEnabled && _cloudApiKeys.ContainsKey(choice.Provider)
            ? $"Hosted model selected · {choice.DisplayName}"
            : $"{choice.DisplayName} selected · connect its API key to send";
    }

    public int ContextSize
    {
        get => ActiveConversation?.NumCtx ?? _contextSize;
        set
        {
            if (!ContextSizes.Any(choice => choice.Value == value)) return;
            if (ActiveConversation is null)
            {
                if (SetProperty(ref _contextSize, value)) return;
            }
            else
            {
                if (ActiveConversation.NumCtx == value) return;
                ActiveConversation.NumCtx = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ShouldOfferCompaction));
                OnPropertyChanged(nameof(ShouldWarnUnknownContext));
                Persist();
            }
        }
    }

    public void NewConversation()
    {
        ShowArchived = false;
        var conversation = new Codev.Conversation
        {
            Title = "New conversation",
            Model = Model,
            Provider = Provider,
            IsPlanMode = IsPlanMode,
            OutputStyle = OutputStyle,
            ThinkEnabled = ThinkEnabled,
            IncludeRepoMap = IncludeRepoMap,
            NumCtx = ContextSize,
            UpdatedAt = DateTimeOffset.Now
        };
        _conversations.Insert(0, conversation);
        SelectConversation(conversation);
        Persist();
        RebuildLists();
    }

    public async Task<bool> RenameConversationAsync(Codev.Conversation conversation, string title)
    {
        if (!_conversations.Contains(conversation) || string.IsNullOrWhiteSpace(title)) return false;
        conversation.Title = title.Trim();
        conversation.UpdatedAt = DateTimeOffset.Now;
        if (ReferenceEquals(ActiveConversation, conversation)) OnPropertyChanged(nameof(ConversationTitle));
        RebuildLists();
        Persist();
        await _persistenceTask;
        return true;
    }

    public async Task<bool> ForkConversationAsync(Codev.Conversation conversation)
    {
        if (!_conversations.Contains(conversation)) return false;
        if (IsConversationBusy(conversation))
        {
            ReportContextActionStatus("Wait for this conversation to finish and cancel its queued requests before forking it.");
            return false;
        }

        try
        {
            var fork = await Codev.ConversationForkService.CreateForkAsync(conversation);
            _conversations.Insert(0, fork);
            SelectConversation(fork);
            RebuildLists();
            Persist();
            await _persistenceTask;
            ReportContextActionStatus($"Forked conversation · {fork.Title}");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            ReportContextActionStatus($"Could not fork conversation: {ex.Message}");
            return false;
        }
    }

    public bool ToggleConversationArchive(Codev.Conversation conversation)
    {
        if (!_conversations.Contains(conversation)) return false;
        if (IsConversationBusy(conversation))
        {
            ReportContextActionStatus("Cancel queued requests and wait for this conversation to finish before archiving it.");
            return false;
        }

        conversation.IsArchived = !conversation.IsArchived;
        conversation.UpdatedAt = DateTimeOffset.Now;
        if (ReferenceEquals(ActiveConversation, conversation))
        {
            _showArchived = false;
            OnPropertyChanged(nameof(ShowArchived));
            OnPropertyChanged(nameof(ArchiveViewLabel));
            var next = _conversations.Where(item => !ReferenceEquals(item, conversation) && !item.IsArchived)
                .OrderByDescending(item => item.UpdatedAt).FirstOrDefault();
            if (next is not null) SelectConversation(next);
            else NewConversation();
        }
        RebuildLists();
        Persist();
        return true;
    }

    public async Task<bool> DeleteConversationAsync(Codev.Conversation conversation)
    {
        if (!_conversations.Contains(conversation)) return false;
        if (IsConversationBusy(conversation))
        {
            ReportContextActionStatus("Cancel queued requests and wait for this conversation to finish before deleting it.");
            return false;
        }

        var wasActive = ReferenceEquals(ActiveConversation, conversation);
        _conversations.Remove(conversation);
        if (wasActive)
        {
            _showArchived = false;
            OnPropertyChanged(nameof(ShowArchived));
            OnPropertyChanged(nameof(ArchiveViewLabel));
            var next = _conversations.Where(item => !item.IsArchived).OrderByDescending(item => item.UpdatedAt).FirstOrDefault();
            if (next is not null) SelectConversation(next);
            else NewConversation();
        }
        RebuildLists();
        Persist();
        await _persistenceTask;
        ReportContextActionStatus($"Deleted conversation · {conversation.Title}");
        return true;
    }

    private bool IsConversationBusy(Codev.Conversation conversation) =>
        conversation.PendingRequestCount > 0 || ReferenceEquals(_generationConversation, conversation);

    private void SelectConversation(Codev.Conversation conversation)
    {
        var previousModel = Model;
        var previousProvider = Provider;
        ActiveConversation = conversation;
        _model = conversation.Model;
        _provider = conversation.Provider;
        if (conversation.NumCtx > Codev.OllamaContextSizes.MaximumFor(conversation.Model))
        {
            conversation.NumCtx = 0;
            Persist();
        }
        RefreshContextSizes(conversation.Model);
        if (conversation.Provider == "ollama")
        {
            if (previousProvider != conversation.Provider || !string.Equals(previousModel, conversation.Model, StringComparison.OrdinalIgnoreCase)) _ = WarmModelAsync(conversation.Model);
        }
        else ConnectionStatus = _cloudRequestsEnabled && _cloudApiKeys.ContainsKey(conversation.Provider)
            ? $"Hosted model selected · {conversation.Model}"
            : $"{conversation.Model} selected · connect its API key to send";
        Draft = conversation.Draft;
        OnPropertyChanged(nameof(IncludeRepoMap));
        OnPropertyChanged(nameof(OutputStyle));
        OnPropertyChanged(nameof(ThinkEnabled));
        OnPropertyChanged(nameof(CanIncludeRepoMap));
        OnPropertyChanged(nameof(RepoMapEstimateLabel));
        Reset(PendingDiffComments, conversation.PendingDiffComments ?? []);
        OnPropertyChanged(nameof(HasPendingDiffComments));
        Reset(TaskChecklistItems, Codev.TaskChecklistService.NormalizeImported(conversation.TaskChecklist));
        OnPropertyChanged(nameof(HasTaskChecklist));
        OnPropertyChanged(nameof(IsTaskChecklistEmpty));
        OnPropertyChanged(nameof(ShouldShowTaskChecklist));
        OnPropertyChanged(nameof(CanEditTaskChecklist));
        OnPropertyChanged(nameof(CanAddTaskChecklistItem));
        OnPropertyChanged(nameof(TaskChecklistLabel));
        ((RelayCommand)SendCommand).NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(SendButtonLabel));
        Messages.Clear();
        for (var index = 0; index < conversation.Messages.Count; index++)
        {
            var message = conversation.Messages[index] with { MessageIndex = index };
            conversation.Messages[index] = message;
            Messages.Add(message);
        }
        Reset(SelectedContextFiles, conversation.ContextFiles);
        RefreshContextEstimate();
        ContextActionStatus = "";
        OnPropertyChanged(nameof(ContextActionStatus));
        OnPropertyChanged(nameof(HasContextActionStatus));
        OnPropertyChanged(nameof(HasSelectedContextFiles));
        OnPropertyChanged(nameof(IncludeProjectContextForHosted));
        OnPropertyChanged(nameof(IsPlanMode));
        OnPropertyChanged(nameof(PlanModeLabel));
        OnPropertyChanged(nameof(IsCodeTask));
        OnPropertyChanged(nameof(CodeTaskLabel));
        OnPropertyChanged(nameof(HasProject));
        OnPropertyChanged(nameof(IsProjectTrusted));
        OnPropertyChanged(nameof(CanToggleCodeTaskMode));
        NotifyCodeTaskAvailabilityProperties();
        ((RelayCommand)ToggleCodeTaskCommand).NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(ProjectTrustRoot));
        OnPropertyChanged(nameof(IsProjectTrustInherited));
        OnPropertyChanged(nameof(CanManageProjectTrust));
        OnPropertyChanged(nameof(ProjectTrustLabel));
        OnPropertyChanged(nameof(ProjectTrustTooltip));
        OnPropertyChanged(nameof(ContextLabel));
        ((RelayCommand)ClearContextFilesCommand).NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(MessageCountLabel));
        OnPropertyChanged(nameof(ProjectLabel));
        OnPropertyChanged(nameof(CanOpenProjectActions));
        OnPropertyChanged(nameof(ProjectCommandPermissionMode));
        OnPropertyChanged(nameof(ProjectCommandPermissionRules));
        OnPropertyChanged(nameof(ContextLabel));
        OnPropertyChanged(nameof(PinLabel));
        OnPropertyChanged(nameof(ArchiveLabel));
        PersistLastActiveConversationId(conversation.Id);
    }

    public void SetProjectFolder(string path)
    {
        if (ActiveConversation is not { } conversation) return;
        if (!Directory.Exists(path)) throw new DirectoryNotFoundException("The selected project folder no longer exists.");
        var fullPath = Path.GetFullPath(path);
        if (!string.Equals(conversation.ProjectPath, fullPath, StringComparison.OrdinalIgnoreCase))
            conversation.ContextFiles.Clear();
        conversation.ProjectPath = fullPath;
        Reset(SelectedContextFiles, conversation.ContextFiles);
        ContextActionStatus = _projectFolderTrust.IsTrusted(fullPath)
            ? "Project attached. Bounded source files will be included with local chat requests."
            : "Project attached as untrusted. Automatic source context is off until you trust this folder.";
        OnPropertyChanged(nameof(ProjectLabel));
        OnPropertyChanged(nameof(ProjectCommandPermissionMode));
        OnPropertyChanged(nameof(ProjectCommandPermissionRules));
        OnPropertyChanged(nameof(HasProject));
        OnPropertyChanged(nameof(IsProjectTrusted));
        OnPropertyChanged(nameof(CanToggleCodeTaskMode));
        NotifyCodeTaskAvailabilityProperties();
        ((RelayCommand)ToggleCodeTaskCommand).NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(ProjectTrustRoot));
        OnPropertyChanged(nameof(IsProjectTrustInherited));
        OnPropertyChanged(nameof(CanManageProjectTrust));
        OnPropertyChanged(nameof(ProjectTrustLabel));
        OnPropertyChanged(nameof(ProjectTrustTooltip));
        OnPropertyChanged(nameof(ContextLabel));
        RefreshContextEstimate();
        OnPropertyChanged(nameof(ContextActionStatus));
        OnPropertyChanged(nameof(HasContextActionStatus));
        OnPropertyChanged(nameof(HasSelectedContextFiles));
        ((RelayCommand)ClearContextFilesCommand).NotifyCanExecuteChanged();
        Persist();
    }

    public void ReportContextActionStatus(string message)
    {
        ContextActionStatus = message;
        OnPropertyChanged(nameof(ContextActionStatus));
        OnPropertyChanged(nameof(HasContextActionStatus));
    }

    public async Task<IReadOnlyList<Codev.SlashCommandDefinition>> GetSlashCommandSuggestionsAsync(
        string draft, int caretIndex, CancellationToken cancellationToken = default)
    {
        if (!Codev.SlashCommandCatalog.TryGetCommandToken(draft, caretIndex, out var token, out var hasArguments)) return [];
        var projectPath = ActiveConversation?.ProjectPath;
        var loaded = await Codev.CustomSlashCommandService.LoadAsync(UserSlashCommandsPath, projectPath,
            IsProjectTrusted, cancellationToken);
        if (loaded.Warnings.Count > 0)
        {
            var warning = loaded.Warnings[0];
            if (!warning.Equals(_lastSlashCommandWarning, StringComparison.Ordinal))
            {
                _lastSlashCommandWarning = warning;
                ReportContextActionStatus("Custom command: " + warning);
            }
        }

        IReadOnlyList<Codev.SlashCommandDefinition> builtIns = hasArguments
            ? Array.Empty<Codev.SlashCommandDefinition>()
            : Codev.SlashCommandCatalog.Suggest(draft, caretIndex);
        var userCommands = loaded.Commands.Where(command => hasArguments
            ? command.Name.Equals(token, StringComparison.OrdinalIgnoreCase)
            : command.Name.StartsWith(token, StringComparison.OrdinalIgnoreCase));
        var skills = await Codev.ProjectSkillCatalog.LoadAsync(UserSkillsPath, projectPath, IsProjectTrusted, cancellationToken);
        if (skills.Warnings.Count > 0)
        {
            var warning = skills.Warnings[0];
            if (!warning.Equals(_lastSlashCommandWarning, StringComparison.Ordinal))
            {
                _lastSlashCommandWarning = warning;
                ReportContextActionStatus("Skill: " + warning);
            }
        }
        var skillNames = skills.Skills.Where(command => !loaded.Commands.Any(item => item.Name.Equals(command.Name, StringComparison.OrdinalIgnoreCase)))
            .Select(command => command.Name).ToArray();
        var templates = Codev.PromptTemplateCatalog.ToSlashCommands(PromptTemplates, loaded.Commands.Select(command => command.Name).Concat(skillNames)).Where(command => hasArguments
            ? command.Name.Equals(token, StringComparison.OrdinalIgnoreCase)
            : command.Name.StartsWith(token, StringComparison.OrdinalIgnoreCase));
        var skillSuggestions = skillNames.Where(name => hasArguments
                ? name.Equals(token, StringComparison.OrdinalIgnoreCase)
                : name.StartsWith(token, StringComparison.OrdinalIgnoreCase))
            .Select(name => skills.Skills.First(skill => skill.Name.Equals(name, StringComparison.OrdinalIgnoreCase)));
        return builtIns.Concat(userCommands).Concat(skillSuggestions).Concat(templates).ToArray();
    }

    public async Task<Codev.SlashCommandExpansionResult> ExpandSlashCommandAsync(
        Codev.SlashCommandDefinition command, string invocation, CancellationToken cancellationToken = default)
    {
        if (!command.IsCustom) return new(false, "", "The selected command is not user-defined.");
        if (string.Equals(command.Scope, "template", StringComparison.OrdinalIgnoreCase))
        {
            var currentCustom = await Codev.CustomSlashCommandService.LoadAsync(UserSlashCommandsPath,
                ActiveConversation?.ProjectPath, IsProjectTrusted, cancellationToken);
            var currentTemplate = Codev.PromptTemplateCatalog.ToSlashCommands(PromptTemplates, currentCustom.Commands.Select(item => item.Name))
                .FirstOrDefault(item => item.Name.Equals(command.Name, StringComparison.OrdinalIgnoreCase));
            return currentTemplate is null
                ? new(false, "", "That saved prompt template is no longer available.")
                : Codev.CustomSlashCommandService.Expand(currentTemplate, invocation);
        }
        if (command.Scope is "skill-user" or "skill-project")
        {
            var skills = await Codev.ProjectSkillCatalog.LoadAsync(UserSkillsPath, ActiveConversation?.ProjectPath, IsProjectTrusted, cancellationToken);
            var currentSkill = skills.Skills.FirstOrDefault(item => item.Name.Equals(command.Name, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(item.Scope, command.Scope, StringComparison.OrdinalIgnoreCase));
            return currentSkill is null
                ? new(false, "", "That skill is no longer available. Check its Markdown file and project trust setting.")
                : await Codev.ProjectSkillCatalog.ReadPromptAsync(currentSkill, UserSkillsPath, ActiveConversation?.ProjectPath,
                    IsProjectTrusted, invocation, cancellationToken);
        }
        var loaded = await Codev.CustomSlashCommandService.LoadAsync(UserSlashCommandsPath,
            ActiveConversation?.ProjectPath, IsProjectTrusted, cancellationToken);
        var current = loaded.Commands.FirstOrDefault(item => item.Name.Equals(command.Name, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(item.Scope, command.Scope, StringComparison.OrdinalIgnoreCase));
        return current is null
            ? new(false, "", "That command is no longer available. Check its Markdown file and project trust setting.")
            : Codev.CustomSlashCommandService.Expand(current, invocation);
    }

    public void SavePromptTemplates(IEnumerable<Codev.PromptTemplate?> templates)
    {
        PromptTemplates.Clear();
        foreach (var template in Codev.PromptTemplateCatalog.Normalize(templates)) PromptTemplates.Add(template);
        PersistSettings();
    }

    public void SaveSamplingPresets(IEnumerable<Codev.SamplingPreset?> presets)
    {
        SamplingPresets.Clear();
        foreach (var preset in Codev.SamplingPresetCatalog.Normalize(presets)) SamplingPresets.Add(preset);
        PersistSettings();
    }

    public void SetReadingWidth(int width)
    {
        var normalized = Codev.AvaloniaUiSettings.NormalizeReadingWidth(width);
        if (_readingWidth == normalized) return;
        _readingWidth = normalized;
        OnPropertyChanged(nameof(ReadingWidth));
        OnPropertyChanged(nameof(IsCompactReadingWidth));
        OnPropertyChanged(nameof(IsStandardReadingWidth));
        OnPropertyChanged(nameof(IsWideReadingWidth));
        OnPropertyChanged(nameof(IsFullReadingWidth));
        PersistSettings();
    }

    public async Task<string> ExportActiveConversationMarkdownAsync()
    {
        if (ActiveConversation is not { } conversation) throw new InvalidOperationException("There is no active conversation to export.");
        await SavePendingDraftAsync();
        return Codev.ConversationMarkdownExporter.Export(conversation);
    }

    public async Task<string> ExportConversationBackupAsync()
    {
        await SavePendingDraftAsync();
        return Codev.ConversationBackupService.Export(_conversations, BackupJsonOptions);
    }

    public async Task<int> ImportConversationBackupAsync(string json)
    {
        await SavePendingDraftAsync();
        var imported = Codev.ConversationBackupService.Import(json, BackupJsonOptions);
        foreach (var conversation in imported.OrderBy(item => item.UpdatedAt))
        {
            conversation.IsArchived = false;
            if (!string.IsNullOrWhiteSpace(conversation.ProjectPath) && !Directory.Exists(conversation.ProjectPath))
                conversation.ProjectPath = null;
            _conversations.Insert(0, conversation);
        }
        _showArchived = false;
        OnPropertyChanged(nameof(ShowArchived));
        OnPropertyChanged(nameof(ArchiveViewLabel));
        RebuildLists();
        if (imported.Count > 0)
            SelectConversation(imported.OrderByDescending(item => item.UpdatedAt).First());
        Persist();
        await _persistenceTask;
        ReportContextActionStatus($"Imported {imported.Count} conversation(s). Existing history was left unchanged.");
        return imported.Count;
    }

    public Codev.ContextFileSelectionResult AddContextFiles(IEnumerable<string> paths)
    {
        if (ActiveConversation is not { ProjectPath: { Length: > 0 } projectPath } conversation || !Directory.Exists(projectPath))
            return new Codev.ContextFileSelectionResult(0, paths.Count());
        var service = new Codev.WorkspaceFileService(projectPath);
        var result = Codev.ProjectContextSelection.AddFiles(service, conversation.ContextFiles, paths);
        Reset(SelectedContextFiles, conversation.ContextFiles);
        ContextActionStatus = result.AddedCount > 0
            ? $"Added {result.AddedCount} file(s) to local chat context" + (result.IgnoredCount > 0 ? $" · ignored {result.IgnoredCount} unsupported, outside, duplicate, or over-limit file(s)" : "")
            : "No supported project files were added. Files outside the project and sensitive files are excluded.";
        OnPropertyChanged(nameof(ContextActionStatus));
        OnPropertyChanged(nameof(HasContextActionStatus));
        OnPropertyChanged(nameof(ContextLabel));
        RefreshContextEstimate();
        OnPropertyChanged(nameof(HasSelectedContextFiles));
        ((RelayCommand)ClearContextFilesCommand).NotifyCanExecuteChanged();
        Persist();
        return result;
    }

    public bool AddTaskChecklistItem(string text)
    {
        if (!CanEditTaskChecklist || ActiveConversation is not { } conversation || TaskChecklistItems.Count >= Codev.TaskChecklistService.MaxItems) return false;
        var items = TaskChecklistItems.Append(new Codev.TaskChecklistItem(Guid.NewGuid(), text)).ToArray();
        return ReplaceTaskChecklist(conversation, items);
    }

    public bool UpdateTaskChecklistItem(Codev.TaskChecklistItem item, string text)
    {
        if (!CanEditTaskChecklist || ActiveConversation is not { } conversation || !TaskChecklistItems.Any(existing => existing.Id == item.Id)) return false;
        var items = TaskChecklistItems.Select(existing => existing.Id == item.Id ? existing with { Text = text } : existing).ToArray();
        return ReplaceTaskChecklist(conversation, items);
    }

    public bool CycleTaskChecklistItemStatus(Codev.TaskChecklistItem item)
    {
        if (!CanEditTaskChecklist || ActiveConversation is not { } conversation || !TaskChecklistItems.Any(existing => existing.Id == item.Id)) return false;
        var items = TaskChecklistItems.Select(existing => existing.Id == item.Id ? existing with
        {
            Status = existing.Status switch
            {
                Codev.TaskChecklistService.Pending => Codev.TaskChecklistService.InProgress,
                Codev.TaskChecklistService.InProgress => Codev.TaskChecklistService.Completed,
                _ => Codev.TaskChecklistService.Pending
            }
        } : existing).ToArray();
        return ReplaceTaskChecklist(conversation, items);
    }

    public bool MoveTaskChecklistItem(Codev.TaskChecklistItem item, int offset)
    {
        if (!CanEditTaskChecklist || ActiveConversation is not { } conversation || offset is not (-1 or 1)) return false;
        var items = TaskChecklistItems.ToList();
        var index = items.FindIndex(existing => existing.Id == item.Id);
        var next = index + offset;
        if (index < 0 || next < 0 || next >= items.Count) return false;
        (items[index], items[next]) = (items[next], items[index]);
        return ReplaceTaskChecklist(conversation, items);
    }

    public bool RemoveTaskChecklistItem(Codev.TaskChecklistItem item)
    {
        if (!CanEditTaskChecklist || ActiveConversation is not { } conversation) return false;
        var items = TaskChecklistItems.Where(existing => existing.Id != item.Id).ToArray();
        if (items.Length == TaskChecklistItems.Count) return false;
        return ReplaceTaskChecklist(conversation, items);
    }

    private bool ReplaceTaskChecklist(Codev.Conversation conversation, IEnumerable<Codev.TaskChecklistItem> items)
    {
        if (!Codev.TaskChecklistService.TryReplace(conversation, items))
        {
            ReportContextActionStatus($"Checklist entries must be 1–{Codev.TaskChecklistService.MaxTextLength} characters, with at most {Codev.TaskChecklistService.MaxItems} items.");
            return false;
        }
        Reset(TaskChecklistItems, conversation.TaskChecklist);
        OnPropertyChanged(nameof(HasTaskChecklist));
        OnPropertyChanged(nameof(IsTaskChecklistEmpty));
        OnPropertyChanged(nameof(ShouldShowTaskChecklist));
        OnPropertyChanged(nameof(TaskChecklistLabel));
        OnPropertyChanged(nameof(CanAddTaskChecklistItem));
        Persist();
        return true;
    }

    private async Task<string> UpdateTaskChecklistFromModelAsync(Codev.Conversation conversation, JsonElement arguments)
    {
        return await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (!Codev.TaskChecklistService.TryReplaceFromModel(conversation, arguments, out var result)) return result;
            if (ReferenceEquals(ActiveConversation, conversation))
            {
                Reset(TaskChecklistItems, conversation.TaskChecklist);
                OnPropertyChanged(nameof(HasTaskChecklist));
                OnPropertyChanged(nameof(IsTaskChecklistEmpty));
                OnPropertyChanged(nameof(ShouldShowTaskChecklist));
                OnPropertyChanged(nameof(TaskChecklistLabel));
                OnPropertyChanged(nameof(CanAddTaskChecklistItem));
            }
            Persist();
            return "Task checklist updated:\n" + result;
        });
    }

    public IReadOnlyList<string> GetProjectFileSuggestions(string prefix)
    {
        if (ActiveConversation?.ProjectPath is not { Length: > 0 } projectPath || !Directory.Exists(projectPath)) return [];
        try
        {
            var service = new Codev.WorkspaceFileService(projectPath);
            if (prefix.StartsWith("rule:", StringComparison.OrdinalIgnoreCase))
                return CanSuggestProjectRules ? Codev.ProjectPathInstructionRuleParser.FindMentionSuggestions(service, prefix) : [];
            var files = Codev.ProjectFileMentionSuggestions.Find(service, prefix).ToList();
            if (CanSuggestProjectRules && "rule:".StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return ["rule:", .. files];
            return files;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            return [];
        }
    }

    public bool AddProjectFileMention(string relativePath)
    {
        if (ActiveConversation is not { ProjectPath: { Length: > 0 } projectPath } conversation || !Directory.Exists(projectPath)) return false;
        if (conversation.ContextFiles.Contains(relativePath, StringComparer.OrdinalIgnoreCase)) return true;
        var result = AddContextFiles([Path.Combine(projectPath, relativePath.Replace('/', Path.DirectorySeparatorChar))]);
        if (result.AddedCount > 0) return true;
        ReportContextActionStatus("This file could not be added to context. It may be excluded, unsupported, outside the project, or the 24-file limit may be full.");
        return false;
    }

    private void RemoveContextFile(string path)
    {
        if (ActiveConversation is not { } conversation || !Codev.ProjectContextSelection.RemoveFile(conversation.ContextFiles, path)) return;
        Reset(SelectedContextFiles, conversation.ContextFiles);
        ContextActionStatus = $"Removed {path} from chat context";
        OnPropertyChanged(nameof(ContextActionStatus));
        OnPropertyChanged(nameof(HasContextActionStatus));
        OnPropertyChanged(nameof(ContextLabel));
        RefreshContextEstimate();
        OnPropertyChanged(nameof(HasSelectedContextFiles));
        ((RelayCommand)ClearContextFilesCommand).NotifyCanExecuteChanged();
        Persist();
    }

    private void ClearContextFiles()
    {
        if (ActiveConversation is not { } conversation || conversation.ContextFiles.Count == 0) return;
        conversation.ContextFiles.Clear();
        SelectedContextFiles.Clear();
        ContextActionStatus = "File selections cleared. Bounded project source files will be included automatically.";
        OnPropertyChanged(nameof(ContextActionStatus));
        OnPropertyChanged(nameof(HasContextActionStatus));
        OnPropertyChanged(nameof(ContextLabel));
        RefreshContextEstimate();
        OnPropertyChanged(nameof(HasSelectedContextFiles));
        ((RelayCommand)ClearContextFilesCommand).NotifyCanExecuteChanged();
        Persist();
    }

    public bool AddPendingDiffComment(string relativePath, string selectedDiff, string comment)
    {
        if (ActiveConversation is not { } conversation || string.IsNullOrWhiteSpace(relativePath) ||
            string.IsNullOrWhiteSpace(selectedDiff) || string.IsNullOrWhiteSpace(comment)) return false;
        if (conversation.PendingDiffComments.Count >= Codev.GitDiffPromptBuilder.MaxComments ||
            selectedDiff.Length > Codev.GitDiffPromptBuilder.MaxDiffLength ||
            comment.Length > Codev.GitDiffPromptBuilder.MaxCommentLength) return false;
        var item = new Codev.GitDiffComment(relativePath.Trim(), selectedDiff.Trim(), comment.Trim());
        conversation.PendingDiffComments.Add(item);
        PendingDiffComments.Add(item);
        OnPropertyChanged(nameof(HasPendingDiffComments));
        ((RelayCommand)SendCommand).NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(SendButtonLabel));
        Persist();
        return true;
    }

    private void RemovePendingDiffComment(Codev.GitDiffComment comment)
    {
        if (ActiveConversation is not { } conversation || !conversation.PendingDiffComments.Remove(comment)) return;
        PendingDiffComments.Remove(comment);
        OnPropertyChanged(nameof(HasPendingDiffComments));
        ((RelayCommand)SendCommand).NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(SendButtonLabel));
        Persist();
    }

    public bool IsProjectPathTrusted(string path) => _projectFolderTrust.IsTrusted(path);
    public bool IsProjectPathKnown(string path) => _projectFolderTrust.IsKnown(path);

    public Codev.Conversation? FindMostRecentConversationForProject(string path)
    {
        if (ActiveConversation?.ProjectPath is { Length: > 0 } activePath && Codev.ProjectConversationResume.PathsEqual(activePath, path,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) return null;
        return Codev.ProjectConversationResume.FindMostRecent(_conversations, path, ActiveConversation?.Id);
    }

    public bool ResumeProjectConversation(Codev.Conversation conversation)
    {
        if (!_conversations.Contains(conversation) || conversation.IsArchived) return false;
        _showArchived = false;
        OnPropertyChanged(nameof(ShowArchived));
        OnPropertyChanged(nameof(ArchiveViewLabel));
        SelectConversation(conversation);
        RebuildLists();
        ReportContextActionStatus($"Resumed conversation · {conversation.Title}");
        return true;
    }

    public async Task MarkProjectFolderKnownAsync(string path)
    {
        await _projectFolderTrust.MarkKnownAsync(path);
        ReportContextActionStatus("Project folder kept untrusted. Automatic source context remains off.");
    }

    public async Task TrustProjectFolderAsync(string path, bool includeSubfolders = false)
    {
        var folder = includeSubfolders ? Directory.GetParent(Path.GetFullPath(path))?.FullName ?? Path.GetFullPath(path) : path;
        await _projectFolderTrust.TrustAsync(folder);
        await _projectFolderTrust.MarkKnownAsync(path);
        RefreshProjectTrustState();
        ((RelayCommand)ToggleCodeTaskCommand).NotifyCanExecuteChanged();
        ReportContextActionStatus(includeSubfolders
            ? $"Trusted {folder} and its subfolders on this device. Automatic bounded context is enabled."
            : "Project folder trusted on this device. Automatic bounded source context is enabled.");
    }

    public async Task ToggleProjectFolderTrustAsync()
    {
        if (ActiveConversation?.ProjectPath is not { Length: > 0 } path || !Directory.Exists(path)) return;
        if (_projectFolderTrust.FindTrustedRoot(path) is { } root)
        {
            if (IsProjectTrustInherited)
            {
                ReportContextActionStatus($"This folder inherits trust from {root}. Attach that trusted parent folder to revoke its trust.");
                return;
            }
            await _projectFolderTrust.RevokeAsync(path);
        }
        else
            await _projectFolderTrust.TrustAsync(path);
        RefreshProjectTrustState();
        ReportContextActionStatus(IsProjectTrusted
            ? "Project folder trusted on this device. Automatic bounded source context is enabled."
            : "Project trust revoked. Automatic source context is off; manually selected files remain available.");
    }

    private void RefreshProjectTrustState()
    {
        OnPropertyChanged(nameof(IsProjectTrusted));
        OnPropertyChanged(nameof(CanToggleCodeTaskMode));
        ((RelayCommand)ToggleCodeTaskCommand).NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(ProjectTrustRoot));
        OnPropertyChanged(nameof(IsProjectTrustInherited));
        OnPropertyChanged(nameof(CanManageProjectTrust));
        OnPropertyChanged(nameof(ProjectTrustLabel));
        OnPropertyChanged(nameof(ProjectTrustTooltip));
        OnPropertyChanged(nameof(ContextLabel));
        RefreshContextEstimate();
    }

    private static bool IsFileSystemRoot(string path)
    {
        var fullPath = Path.GetFullPath(path);
        return string.Equals(fullPath, Path.GetPathRoot(fullPath), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    private void RefreshContextEstimate()
    {
        var conversation = ActiveConversation;
        _contextEstimateLabel = Codev.ProjectContextEstimateLabel.ForProject(conversation?.ProjectPath,
            conversation?.Provider ?? "ollama", conversation?.IncludeProjectContextForHosted == true,
            conversation is not null && !string.IsNullOrWhiteSpace(conversation.ProjectPath) && _projectFolderTrust.IsTrusted(conversation.ProjectPath),
            conversation?.ContextFiles);
        OnPropertyChanged(nameof(ContextEstimateLabel));
        OnPropertyChanged(nameof(CanIncludeRepoMap));
        OnPropertyChanged(nameof(RepoMapEstimateLabel));
    }

    private bool CanRewindConversationMessage(int messageIndex) =>
        !IsGenerating && ActiveConversation is { PendingRequestCount: 0 } conversation &&
        !ReferenceEquals(_generationConversation, conversation) && messageIndex >= 0 && messageIndex < conversation.Messages.Count &&
        conversation.Messages[messageIndex].IsUser;

    private async Task RewindConversationAsync(int messageIndex)
    {
        if (ActiveConversation is not { } conversation || !CanRewindConversationMessage(messageIndex)) return;
        if (await (ConfirmConversationRewindAsync?.Invoke(messageIndex) ?? Task.FromResult(false)) != true) return;
        try
        {
            var prompt = Codev.ConversationRewindService.RestoreConversationOnly(conversation, messageIndex);
            Draft = prompt;
            Messages.Clear();
            foreach (var message in conversation.Messages) Messages.Add(message);
            OnPropertyChanged(nameof(MessageCountLabel));
            ContextActionStatus = "Conversation rewound before that prompt. Project files were left unchanged; review them in Files history.";
            OnPropertyChanged(nameof(ContextActionStatus));
            OnPropertyChanged(nameof(HasContextActionStatus));
            Persist();
            RebuildLists();
        }
        catch (InvalidOperationException ex) { ReportContextActionStatus(ex.Message); }
        finally { ((RelayCommand)RewindConversationCommand).NotifyCanExecuteChanged(); }
    }

    private async Task EditPromptAsync(int messageIndex)
    {
        if (ActiveConversation is not { } conversation || !CanRewindConversationMessage(messageIndex)) return;
        var original = conversation.Messages[messageIndex].Content;
        var revised = await (EditConversationPromptAsync?.Invoke(messageIndex, original) ?? Task.FromResult<string?>(null));
        if (revised is null || !CanRewindConversationMessage(messageIndex)) return;
        try
        {
            _ = Codev.ConversationRewindService.RestoreConversationOnly(conversation, messageIndex);
            Draft = revised;
            Messages.Clear();
            foreach (var message in conversation.Messages) Messages.Add(message);
            OnPropertyChanged(nameof(MessageCountLabel));
            ContextActionStatus = "Prompt revised. Send it when ready; later conversation messages were removed, and project files were left unchanged.";
            OnPropertyChanged(nameof(ContextActionStatus));
            OnPropertyChanged(nameof(HasContextActionStatus));
            Persist();
            RebuildLists();
        }
        catch (InvalidOperationException ex) { ReportContextActionStatus(ex.Message); }
        finally
        {
            ((RelayCommand)RewindConversationCommand).NotifyCanExecuteChanged();
            ((RelayCommand)EditPromptCommand).NotifyCanExecuteChanged();
        }
    }

    private void TogglePin()
    {
        if (ActiveConversation is not { } conversation) return;
        conversation.IsPinned = !conversation.IsPinned;
        conversation.UpdatedAt = DateTimeOffset.Now;
        OnPropertyChanged(nameof(PinLabel));
        Persist();
        RebuildLists();
    }

    private void ArchiveConversation()
    {
        if (ActiveConversation is not { } conversation) return;
        conversation.IsArchived = !conversation.IsArchived;
        OnPropertyChanged(nameof(ArchiveLabel));
        Persist();
        RebuildLists();
        if (conversation.IsArchived)
        {
            var next = _conversations.FirstOrDefault(c => !c.IsArchived);
            if (next is not null) SelectConversation(next);
            else { ShowArchived = true; RebuildLists(); SelectConversation(conversation); }
        }
        else SelectConversation(conversation);
    }

    private async Task SendDraftAsync()
    {
        if (ActiveConversation is not { } conversation || (string.IsNullOrWhiteSpace(Draft) && PendingDiffComments.Count == 0)) return;
        if (_isReviewRunning)
        {
            ReportContextActionStatus("Wait for /review to finish or stop it before sending another prompt.");
            return;
        }
        if (_isUnloadingModel)
        {
            ReportContextActionStatus("Wait for the Ollama model unload operation to finish before sending a prompt.");
            return;
        }
        var text = Draft.Trim();
        if (PendingDiffComments.Count == 0 && text.Equals("/status", StringComparison.OrdinalIgnoreCase))
        {
            AddStatusReport(conversation);
            return;
        }
        if (conversation.Provider != "ollama" && (!_cloudRequestsEnabled || !_cloudApiKeys.ContainsKey(conversation.Provider)))
        {
            ReportContextActionStatus("Connect the selected provider and acknowledge that prompts and selected project context will be sent off-device before sending.");
            return;
        }
        if (conversation.IsCodeTask && (conversation.Provider == "ollama"
                ? !Codev.OllamaEndpoint.IsLoopback(_ollamaEndpoint) || string.IsNullOrWhiteSpace(conversation.ProjectPath) || !_projectFolderTrust.IsTrusted(conversation.ProjectPath)
                : conversation.Provider != Codev.CloudModelProviders.OpenAI || !_cloudRequestsEnabled || !_cloudApiKeys.ContainsKey(conversation.Provider) ||
                  !conversation.IncludeProjectContextForHosted || string.IsNullOrWhiteSpace(conversation.ProjectPath) || !_projectFolderTrust.IsTrusted(conversation.ProjectPath)))
        {
            ReportContextActionStatus("Code task was not queued: it requires a supported provider, current cloud and project-context consent when hosted, and a currently trusted workspace.");
            return;
        }
        var sentText = Codev.GitDiffPromptBuilder.AppendComments(text, PendingDiffComments.ToArray());
        var titleText = string.IsNullOrWhiteSpace(text) ? "Review selected diff" : text;
        if (conversation.Title == "New conversation") conversation.Title = titleText.Length > 48 ? titleText[..48].TrimEnd() + "…" : titleText;
        else if (conversation.Messages.Count == 0) conversation.Title = titleText.Length > 48 ? titleText[..48].TrimEnd() + "…" : titleText;
        var userMessage = new Codev.ChatMessage("user", sentText) { MessageIndex = conversation.Messages.Count };
        conversation.Messages.Add(userMessage);
        conversation.Messages.Add(new Codev.ChatMessage("assistant", ""));
        conversation.Draft = "";
        conversation.UpdatedAt = DateTimeOffset.Now;
        Draft = "";
        Messages.Add(userMessage);
        Messages.Add(conversation.Messages[^1]);
        var assistantIndex = conversation.Messages.Count - 1;
        var hasExplicitProjectFiles = conversation.ContextFiles.Count > 0;
        var projectTrusted = !string.IsNullOrWhiteSpace(conversation.ProjectPath) && _projectFolderTrust.IsTrusted(conversation.ProjectPath);
        var contextProjectPath = Codev.ProjectContextPolicy.GetProjectPathForQueuedTurn(
            conversation.ProjectPath, hasExplicitProjectFiles, projectTrusted);
        var queuedTurn = new Codev.PersistedQueuedTurn(assistantIndex, conversation.Model, conversation.NumCtx,
            conversation.IsCodeTask, conversation.IsPlanMode, contextProjectPath, [.. conversation.ContextFiles], [], DateTimeOffset.Now, conversation.Temperature, conversation.Provider,
            conversation.Provider == "ollama" || conversation.IncludeProjectContextForHosted, conversation.IncludeRepoMap, conversation.OutputStyle, conversation.ThinkEnabled,
            conversation.TopP, conversation.TopK, conversation.PresencePenalty, conversation.RepeatPenalty, conversation.NumPredict);
        conversation.PendingTurns ??= [];
        conversation.PendingTurns.Add(queuedTurn);
        conversation.PendingRequestCount++;
        OnPropertyChanged(nameof(CanReviewFileChanges));
        ((RelayCommand)RewindConversationCommand).NotifyCanExecuteChanged();
        var turn = new QueuedChatTurn(conversation, queuedTurn);
        _requestQueue.Enqueue(turn);
        ((RelayCommand)SummarizeConversationUpToCommand).NotifyCanExecuteChanged();
        ((RelayCommand)SummarizeConversationFromCommand).NotifyCanExecuteChanged();
        conversation.Messages[assistantIndex] = new Codev.ChatMessage("assistant", "Queued locally · waiting for the current response");
        if (ReferenceEquals(ActiveConversation, conversation)) Messages[assistantIndex] = conversation.Messages[assistantIndex];
        OnPropertyChanged(nameof(QueueStatusLabel));
        OnPropertyChanged(nameof(HasQueuedTurns));
        ((RelayCommand)ResumeQueueCommand).NotifyCanExecuteChanged();
        ((RelayCommand)CancelQueuedCommand).NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(ConversationTitle));
        OnPropertyChanged(nameof(MessageCountLabel));
        Persist();
        RebuildLists();
        if (!_queuePaused) _ = ProcessQueuedTurnsAsync();
        await Task.CompletedTask;
    }

    private void AddStatusReport(Codev.Conversation conversation)
    {
        var userMessage = new Codev.ChatMessage("user", Draft.Trim()) { MessageIndex = conversation.Messages.Count };
        var trustRoot = string.IsNullOrWhiteSpace(conversation.ProjectPath) ? null : _projectFolderTrust.FindTrustedRoot(conversation.ProjectPath);
        IReadOnlyList<string>? instructionFiles = null;
        if (_lastPromptContexts.TryGetValue(conversation.Id, out var promptContext) &&
            promptContext.Provider.Equals(conversation.Provider, StringComparison.OrdinalIgnoreCase) &&
            promptContext.Model.Equals(conversation.Model, StringComparison.OrdinalIgnoreCase))
        {
            var instructions = promptContext.Sections.FirstOrDefault(section =>
                section.Name.Equals("Project instructions (AGENTS.md and selected rules)", StringComparison.Ordinal))?.Content;
            instructionFiles = Codev.ProjectAgentInstructions.GetIncludedRelativePaths(instructions);
        }
        var assistantMessage = new Codev.ChatMessage("assistant", Codev.ConversationStatusReport.Build(
            conversation, ReferenceEquals(_generationConversation, conversation) && IsGenerating,
            conversation.PendingRequestCount, _queuePaused, _cloudRequestsEnabled,
            trustRoot is not null, trustRoot, OllamaEndpointDisplay, Codev.OllamaEndpoint.IsLoopback(_ollamaEndpoint), instructionFiles,
            _projectCommandPermissions.GetMode(conversation.ProjectPath ?? ""),
            _projectCommandPermissions.GetRules(conversation.ProjectPath ?? "").Count(rule => rule.Decision == Codev.ProjectCommandPermissionDecision.Allow && Codev.ProjectCommandPermissionRegistry.CanCreateAllowRule(rule.Command)),
            _projectCommandPermissions.GetRules(conversation.ProjectPath ?? "").Count(rule => rule.Decision == Codev.ProjectCommandPermissionDecision.Deny)));
        conversation.Messages.Add(userMessage);
        conversation.Messages.Add(assistantMessage);
        conversation.Draft = "";
        conversation.PendingDiffComments.Clear();
        PendingDiffComments.Clear();
        OnPropertyChanged(nameof(HasPendingDiffComments));
        ((RelayCommand)SendCommand).NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(SendButtonLabel));
        conversation.UpdatedAt = DateTimeOffset.Now;
        Draft = "";
        Messages.Add(userMessage);
        Messages.Add(assistantMessage);
        OnPropertyChanged(nameof(MessageCountLabel));
        Persist();
        RebuildLists();
    }

    public bool ClearActiveConversationHistory()
    {
        if (ActiveConversation is not { } conversation) return false;
        if (!Codev.ConversationHistoryClearService.Clear(conversation,
                ReferenceEquals(_generationConversation, conversation)))
        {
            ReportContextActionStatus("Wait for this conversation to finish and clear its queued requests before clearing its history.");
            return false;
        }

        conversation.PendingDiffComments.Clear();
        PendingDiffComments.Clear();
        TaskChecklistItems.Clear();
        Messages.Clear();
        Draft = "";
        OnPropertyChanged(nameof(ConversationTitle));
        OnPropertyChanged(nameof(MessageCountLabel));
        OnPropertyChanged(nameof(HasCompactionSummary));
        OnPropertyChanged(nameof(CompactionStatusLabel));
        OnPropertyChanged(nameof(HasPendingDiffComments));
        OnPropertyChanged(nameof(HasTaskChecklist));
        OnPropertyChanged(nameof(IsTaskChecklistEmpty));
        OnPropertyChanged(nameof(ShouldShowTaskChecklist));
        OnPropertyChanged(nameof(TaskChecklistLabel));
        OnPropertyChanged(nameof(CanAddTaskChecklistItem));
        OnPropertyChanged(nameof(SendButtonLabel));
        OnPropertyChanged(nameof(QueueStatusLabel));
        ((RelayCommand)SendCommand).NotifyCanExecuteChanged();
        Persist();
        RebuildLists();
        ReportContextActionStatus("Conversation messages cleared. Project selection and file-change history were kept.");
        return true;
    }

    public async Task<Codev.ConversationCompactionProposal?> CreateCompactionProposalAsync(CancellationToken cancellationToken = default,
        int? throughMessageCount = null, int? fromMessageCount = null)
    {
        var conversation = ActiveConversation;
        if (!Codev.ConversationCompactionService.CanCompact(conversation, IsGenerating) || _queueProcessorRunning || _requestQueue.Count > 0)
        {
            ReportContextActionStatus("Wait until all conversations finish their queued turns before compacting history.");
            return null;
        }
        var boundary = throughMessageCount ?? Codev.ConversationCompactionService.FindBoundary(conversation!.Messages, conversation.CompactionThroughMessageCount);
        var from = fromMessageCount ?? (string.IsNullOrWhiteSpace(conversation!.CompactionSummary) ? 0 : conversation.CompactionFromMessageCount);
        if (!Codev.ConversationCompactionService.IsValidRange(conversation!.Messages, from, boundary) ||
            (string.IsNullOrWhiteSpace(conversation.CompactionSummary)
                ? boundary <= from
                : from != conversation.CompactionFromMessageCount || boundary <= conversation.CompactionThroughMessageCount))
        {
            ReportContextActionStatus("Choose a start and end boundary that enclose an un-compacted complete exchange.");
            return null;
        }

        try
        {
            var sourceMessages = Codev.ConversationCompactionService.BuildSummaryMessages(conversation, boundary, from);
            var summary = new System.Text.StringBuilder();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMinutes(3));
            if (Codev.CloudModelProviders.IsCloud(conversation.Provider))
            {
                if (!_cloudRequestsEnabled || !_cloudApiKeys.TryGetValue(conversation.Provider, out var key))
                    throw new InvalidOperationException("Reconnect the hosted provider before compacting with its model.");
                var messages = sourceMessages.Select(message => new Codev.CloudChatMessage(message.Role, message.Content)).ToArray();
                await foreach (var delta in new Codev.CloudModelApiClient(_http).StreamChatAsync(
                    conversation.Provider, key, conversation.Model, messages, timeout.Token, maxOutputTokens: 1500))
                {
                    summary.Append(delta);
                    if (summary.Length > Codev.ConversationCompactionService.MaxSummaryCharacters)
                        throw new InvalidOperationException("The generated summary exceeded the safe size limit.");
                }
            }
            else
            {
                var payload = new Dictionary<string, object>
                {
                    ["model"] = conversation.Model,
                    ["messages"] = sourceMessages,
                    ["stream"] = false,
                    ["think"] = false,
                    ["options"] = new Dictionary<string, object>
                    {
                        ["num_predict"] = 1500,
                        ["num_ctx"] = conversation.NumCtx > 0 ? conversation.NumCtx : 32768
                    }
                };
                using var response = await _http.PostAsJsonAsync(Codev.OllamaEndpoint.ApiUri(_ollamaEndpoint, "api/chat"), payload, timeout.Token);
                if (!response.IsSuccessStatusCode)
                    throw new InvalidOperationException($"Ollama returned HTTP {(int)response.StatusCode} ({response.ReasonPhrase}). {await response.Content.ReadAsStringAsync(timeout.Token)}");
                using var result = await System.Text.Json.JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(timeout.Token), cancellationToken: timeout.Token);
                if (!result.RootElement.TryGetProperty("message", out var message) || !message.TryGetProperty("content", out var content))
                    throw new InvalidOperationException("Ollama returned no summary text.");
                summary.Append(content.GetString());
            }

            if (string.IsNullOrWhiteSpace(summary.ToString())) throw new InvalidOperationException("The selected model returned an empty summary.");
            return new Codev.ConversationCompactionProposal(conversation.Id, boundary, summary.ToString().Trim(),
                (boundary - (string.IsNullOrWhiteSpace(conversation.CompactionSummary) ? from : conversation.CompactionThroughMessageCount)) / 2,
                Math.Max(0, (conversation.Messages.Count - boundary) / 2), from);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            ReportContextActionStatus("Compaction timed out after three minutes. The conversation is unchanged.");
            return null;
        }
        catch (Exception ex)
        {
            ReportContextActionStatus($"Could not summarize conversation: {ex.Message}");
            return null;
        }
    }

    private static int SummaryBoundaryFor(Codev.ChatMessage message) => message.IsAssistant ? message.MessageIndex + 1 : message.MessageIndex;

    private bool CanSummarizeConversationUpTo(Codev.ChatMessage message)
    {
        var conversation = ActiveConversation;
        var boundary = SummaryBoundaryFor(message);
        return Codev.ConversationCompactionService.CanCompact(conversation, IsGenerating) && !_queueProcessorRunning && _requestQueue.Count == 0 &&
            conversation is not null && (message.IsUser || message.IsAssistant) && boundary > conversation.CompactionThroughMessageCount &&
            Codev.ConversationCompactionService.IsValidBoundary(conversation.Messages, boundary);
    }

    private async Task SummarizeConversationUpToAsync(Codev.ChatMessage message)
    {
        var proposal = await CreateCompactionProposalAsync(throughMessageCount: SummaryBoundaryFor(message));
        if (proposal is not null) await (ShowCompactionProposalAsync?.Invoke(proposal) ?? Task.CompletedTask);
    }

    private bool CanSummarizeConversationFrom(Codev.ChatMessage message)
    {
        var conversation = ActiveConversation;
        if (conversation is null || !message.IsUser || !string.IsNullOrWhiteSpace(conversation.CompactionSummary)) return false;
        var boundary = Codev.ConversationCompactionService.FindBoundary(conversation.Messages);
        return Codev.ConversationCompactionService.CanCompact(conversation, IsGenerating) && !_queueProcessorRunning && _requestQueue.Count == 0 &&
            Codev.ConversationCompactionService.IsValidRange(conversation.Messages, message.MessageIndex, boundary);
    }

    private async Task SummarizeConversationFromAsync(Codev.ChatMessage message)
    {
        var boundary = ActiveConversation is { } conversation
            ? Codev.ConversationCompactionService.FindBoundary(conversation.Messages)
            : 0;
        var proposal = await CreateCompactionProposalAsync(throughMessageCount: boundary, fromMessageCount: message.MessageIndex);
        if (proposal is not null) await (ShowCompactionProposalAsync?.Invoke(proposal) ?? Task.CompletedTask);
    }

    public bool ApplyCompactionProposal(Codev.ConversationCompactionProposal proposal, string editedSummary)
    {
        if (!string.Equals(editedSummary, proposal.Summary, StringComparison.Ordinal)) proposal = proposal with { Summary = editedSummary };
        if (_queueProcessorRunning || _requestQueue.Count > 0 || ActiveConversation is not { } conversation || !Codev.ConversationCompactionService.Apply(conversation, proposal, IsGenerating))
        {
            ReportContextActionStatus("The conversation changed or a request was queued. The proposed summary was not applied.");
            return false;
        }
        _lastPromptMessageCounts.Remove(conversation.Id);
        OnPropertyChanged(nameof(HasCompactionSummary));
        OnPropertyChanged(nameof(CompactionStatusLabel));
        OnPropertyChanged(nameof(ShouldOfferCompaction));
        ((RelayCommand)SummarizeConversationUpToCommand).NotifyCanExecuteChanged();
        ((RelayCommand)SummarizeConversationFromCommand).NotifyCanExecuteChanged();
        Persist();
        ReportContextActionStatus("Summary applied to future prompts. The full transcript remains saved and visible.");
        return true;
    }

    public bool ClearActiveCompaction()
    {
        if (ActiveConversation is not { } conversation || !HasCompactionSummary) return false;
        if (!Codev.ConversationCompactionService.CanCompact(conversation, IsGenerating) || _queueProcessorRunning || _requestQueue.Count > 0)
        {
            ReportContextActionStatus("Wait until all conversations finish their queued turns before restoring full history.");
            return false;
        }
        Codev.ConversationCompactionService.Clear(conversation);
        _lastPromptMessageCounts.Remove(conversation.Id);
        OnPropertyChanged(nameof(HasCompactionSummary));
        OnPropertyChanged(nameof(CompactionStatusLabel));
        OnPropertyChanged(nameof(ShouldOfferCompaction));
        ((RelayCommand)SummarizeConversationUpToCommand).NotifyCanExecuteChanged();
        ((RelayCommand)SummarizeConversationFromCommand).NotifyCanExecuteChanged();
        Persist();
        ReportContextActionStatus("Future prompts will use the full conversation history again.");
        return true;
    }

    private async Task ProcessQueuedTurnsAsync()
    {
        if (_queueProcessorRunning || _queuePaused || _requestQueue.Count == 0) return;
        _queueProcessorRunning = true;
        ((RelayCommand)SummarizeConversationUpToCommand).NotifyCanExecuteChanged();
        ((RelayCommand)SummarizeConversationFromCommand).NotifyCanExecuteChanged();
        try
        {
            while (!_queuePaused && _requestQueue.TryDequeue(out var turn))
            {
                await ExecuteQueuedTurnAsync(turn);
            }
        }
        finally
        {
            _queueProcessorRunning = false;
            ((RelayCommand)SummarizeConversationUpToCommand).NotifyCanExecuteChanged();
            ((RelayCommand)SummarizeConversationFromCommand).NotifyCanExecuteChanged();
            OnPropertyChanged(nameof(QueueStatusLabel));
            OnPropertyChanged(nameof(HasQueuedTurns));
            ((RelayCommand)ResumeQueueCommand).NotifyCanExecuteChanged();
        }
    }

    private async Task AppendAssistantDeltaAsync(Codev.Conversation conversation, int assistantIndex, System.Text.StringBuilder output, string delta)
    {
        if (delta.Length == 0) return;
        output.Append(delta);
        conversation.Messages[assistantIndex] = conversation.Messages[assistantIndex] with { Content = output.ToString() };
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (ReferenceEquals(ActiveConversation, conversation)) Messages[assistantIndex] = conversation.Messages[assistantIndex];
        });
    }

    private async Task AppendAssistantThinkingAsync(Codev.Conversation conversation, int assistantIndex, System.Text.StringBuilder output, string delta)
    {
        if (delta.Length == 0) return;
        output.Append(delta);
        conversation.Messages[assistantIndex] = conversation.Messages[assistantIndex] with { Thinking = output.ToString() };
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (ReferenceEquals(ActiveConversation, conversation)) Messages[assistantIndex] = conversation.Messages[assistantIndex];
        });
    }

    private async Task RunCodeTaskTurnAsync(Codev.Conversation conversation, int assistantIndex,
        List<OllamaChatMessage> history, Codev.WorkspaceFileService files, Codev.PersistedQueuedTurn turn,
        System.Text.StringBuilder thinking, CancellationToken cancellationToken, IReadOnlyList<string> initialContextSources)
    {
        var shell = Codev.ShellCommandResolver.ResolveCurrent();
        var tools = CreateCodeTaskToolSchemas(shell);
        var repeatedCalls = new Codev.RepeatedToolCallGuard();
        var executor = new Codev.CodeTaskToolExecutor(files, conversation,
            async proposal => await Dispatcher.UIThread.InvokeAsync(async () => await
                (ReviewFileChangeAsync?.Invoke(proposal.RelativePath, proposal.Before, proposal.After, proposal.IsNewFile, proposal.ProposedPatch, proposal.ContextSources) ?? Task.FromResult(false))),
            _ => Task.FromResult(false),
            status: message => _ = SetConnectionStatusAsync(message), initialContextSources: initialContextSources,
            permissionApproval: proposal => Dispatcher.UIThread.InvokeAsync(async () => await ApproveCommandWithProjectPolicyAsync(proposal, files.ContextExclusions)));
        var transcript = new System.Text.StringBuilder();
        for (var round = 0; round < 8; round++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await SetConnectionStatusAsync($"Code task · thinking · step {round + 1}/8");
            var payload = new Dictionary<string, object>
            {
                ["model"] = turn.Model,
                ["messages"] = history,
                ["tools"] = tools,
                ["think"] = turn.ThinkEnabled,
                ["stream"] = false
            };
            if (Codev.OllamaRequestOptions.Build(turn.NumCtx, turn.Temperature, turn.TopP, turn.TopK,
                turn.PresencePenalty, turn.RepeatPenalty, turn.NumPredict) is { } options) payload["options"] = options;
            using var request = new HttpRequestMessage(HttpMethod.Post,
                Codev.OllamaEndpoint.ApiUri(_ollamaEndpoint, "api/chat"));
            var payloadJson = JsonSerializer.Serialize(payload, JsonSerializerOptions.Web);
            request.Content = new StringContent(payloadJson, System.Text.Encoding.UTF8, "application/json");
            var roundMessages = history.Select(message => new Codev.ChatMessage(message.Role, message.Content)).ToArray();
            var roundSections = new[]
            {
                new Codev.PromptContextSection("Current model messages and tool results",
                    string.Join("\n\n", history.Select(message => $"[{message.Role}]\n{message.Content}"))),
                new Codev.PromptContextSection("Available tool schemas", JsonSerializer.Serialize(tools, JsonSerializerOptions.Web)),
                new Codev.PromptContextSection("Generation controls", $"think={turn.ThinkEnabled}; num_ctx={turn.NumCtx}; temperature={turn.Temperature?.ToString() ?? "model default"}; top_p={turn.TopP?.ToString() ?? "model default"}; top_k={turn.TopK?.ToString() ?? "model default"}; presence_penalty={turn.PresencePenalty?.ToString() ?? "model default"}; repeat_penalty={turn.RepeatPenalty?.ToString() ?? "model default"}; num_predict={turn.NumPredict?.ToString() ?? "model default"}")
            };
            await SetLastPromptContextAsync(conversation, Codev.PromptContextBreakdown.Create("ollama", turn.Model,
                turn.NumCtx, roundSections, roundMessages, payloadJson));
            using var response = await _http.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"Ollama returned HTTP {(int)response.StatusCode} ({response.ReasonPhrase}).\n{body}");
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.TryGetProperty("error", out var apiError)) throw new InvalidOperationException(apiError.GetString() ?? apiError.ToString());
            if (root.TryGetProperty("prompt_eval_count", out var promptCount) && promptCount.TryGetInt32(out var promptTokens))
            {
                await RecordPromptTokenUsageAsync(conversation, turn.Provider, turn.Model, turn.NumCtx, promptTokens);
            }
            var message = root.GetProperty("message");
            if (message.TryGetProperty("thinking", out var thinkingChunk) && thinkingChunk.GetString() is { Length: > 0 } thinkingText)
            {
                if (thinking.Length > 0) thinking.AppendLine().AppendLine();
                await AppendAssistantThinkingAsync(conversation, assistantIndex, thinking, thinkingText);
            }
            var text = message.TryGetProperty("content", out var content) ? content.GetString() ?? "" : "";
            var calls = message.TryGetProperty("tool_calls", out var callArray) && callArray.ValueKind == JsonValueKind.Array
                ? callArray.EnumerateArray().Select(call => call.Clone()).ToArray() : [];
            if (calls.Length == 0)
            {
                if (!string.IsNullOrWhiteSpace(text)) transcript.Append(text);
                if (conversation.TaskChecklist.Count > 0)
                    transcript.AppendLine().AppendLine().Append("**Task checklist**").AppendLine().AppendLine(Codev.TaskChecklistService.FormatForDisplay(conversation.TaskChecklist));
                await SetAssistantTranscriptAsync(conversation, assistantIndex, transcript.ToString());
                return;
            }

            history.Add(new OllamaChatMessage("assistant", text, JsonSerializer.SerializeToElement(calls)));
            if (!string.IsNullOrWhiteSpace(text)) transcript.AppendLine(text);
            foreach (var call in calls)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var function = call.GetProperty("function");
                var name = function.GetProperty("name").GetString() ?? "";
                var arguments = function.TryGetProperty("arguments", out var args) ? args : default;
                if (string.IsNullOrWhiteSpace(turn.ProjectPath) || !_projectFolderTrust.IsTrusted(turn.ProjectPath))
                    throw new InvalidOperationException("Project trust was revoked during the Code task. No further tools will run until it is trusted again.");
                await SetConnectionStatusAsync($"Code task · {name.Replace('_', ' ')}");
                if (repeatedCalls.Record(name, arguments) >= Codev.RepeatedToolCallGuard.ConfirmationThreshold)
                {
                    var confirmed = await Dispatcher.UIThread.InvokeAsync(async () => await (ConfirmRepeatedToolCallAsync?.Invoke(name) ?? Task.FromResult(false)));
                    if (!confirmed)
                    {
                        transcript.AppendLine().AppendLine("Code task stopped because the same tool call repeated. Send a follow-up with more guidance to continue.");
                        await SetAssistantTranscriptAsync(conversation, assistantIndex, transcript.ToString());
                        return;
                    }
                    repeatedCalls.AllowOneMore();
                }
                var result = name == "update_task_checklist"
                    ? await UpdateTaskChecklistFromModelAsync(conversation, arguments)
                    : await executor.ExecuteAsync(name, arguments, cancellationToken);
                Persist();
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    OnPropertyChanged(nameof(FileChangesCount));
                    OnPropertyChanged(nameof(FileChangesLabel));
                    OnPropertyChanged(nameof(CanReviewFileChanges));
                });
                history.Add(new OllamaChatMessage("tool", result, null, name));
                transcript.AppendLine().Append("**").Append(name.Replace('_', ' ')).AppendLine("**").AppendLine(TruncateToolOutput(result));
                await SetAssistantTranscriptAsync(conversation, assistantIndex, transcript.ToString());
            }
            await SetConnectionStatusAsync("Code task · Thinking…");
        }
        throw new InvalidOperationException("Code task reached the eight-step tool limit. Send a follow-up to continue.");
    }

    private async Task RunOpenAiCodeTaskTurnAsync(Codev.Conversation conversation, int assistantIndex,
        IReadOnlyList<Codev.ChatMessage> normalizedHistory, Codev.PersistedQueuedTurn turn, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(turn.ProjectPath)) throw new InvalidOperationException("OpenAI Code task requires a trusted workspace.");
        var files = new Codev.WorkspaceFileService(turn.ProjectPath, turn.ContextExclusions);
        var toolSchemas = CreateOpenAiCodeTaskToolSchemas(Codev.ShellCommandResolver.ResolveCurrent());
        var executor = new Codev.CodeTaskToolExecutor(files, conversation,
            async proposal => await Dispatcher.UIThread.InvokeAsync(async () => await
                (ReviewFileChangeAsync?.Invoke(proposal.RelativePath, proposal.Before, proposal.After, proposal.IsNewFile, proposal.ProposedPatch, proposal.ContextSources) ?? Task.FromResult(false))),
            _ => Task.FromResult(false), status: message => _ = SetConnectionStatusAsync(message),
            permissionApproval: proposal => Dispatcher.UIThread.InvokeAsync(async () => await ApproveCommandWithProjectPolicyAsync(proposal, files.ContextExclusions)));
        var input = normalizedHistory.Select(message => (object)new { role = message.Role, content = message.Content }).ToList();
        var transcript = new System.Text.StringBuilder();
        var repeatedCalls = new Codev.RepeatedToolCallGuard();
        var client = new Codev.CloudModelApiClient(_http);
        for (var round = 0; round < 8; round++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_cloudRequestsEnabled || !conversation.IncludeProjectContextForHosted ||
                !_cloudApiKeys.TryGetValue(Codev.CloudModelProviders.OpenAI, out var currentOpenAiKey))
                throw new InvalidOperationException("OpenAI Code task stopped because hosted requests or project-context consent was turned off.");
            await SetConnectionStatusAsync($"OpenAI Code task · thinking · step {round + 1}/8");
            var requestBody = JsonSerializer.Serialize(new
            {
                model = turn.Model,
                input,
                tools = toolSchemas,
                tool_choice = "auto",
                stream = false,
                store = false,
                max_output_tokens = 4096
            }, JsonSerializerOptions.Web);
            await SetLastPromptContextAsync(conversation, Codev.PromptContextBreakdown.Create(
                turn.Provider, turn.Model, 0,
                [new Codev.PromptContextSection("Conversation and tool results", JsonSerializer.Serialize(input, JsonSerializerOptions.Web)),
                 new Codev.PromptContextSection("Available tool schemas", JsonSerializer.Serialize(toolSchemas, JsonSerializerOptions.Web))],
                normalizedHistory, requestBody));
            var response = await client.CreateOpenAiToolResponseAsync(currentOpenAiKey, turn.Model, input, toolSchemas,
                cancellationToken, body => SetLastPromptRequestBodyAsync(conversation, body));
            if (response.InputTokens is { } inputTokens)
                await RecordPromptTokenUsageAsync(conversation, turn.Provider, turn.Model, 0, inputTokens);
            if (response.FunctionCalls.Count == 0)
            {
                if (!string.IsNullOrWhiteSpace(response.OutputText)) transcript.Append(response.OutputText);
                if (conversation.TaskChecklist.Count > 0)
                    transcript.AppendLine().AppendLine().Append("**Task checklist**").AppendLine().AppendLine(Codev.TaskChecklistService.FormatForDisplay(conversation.TaskChecklist));
                await SetAssistantTranscriptAsync(conversation, assistantIndex, transcript.ToString());
                return;
            }
            if (!string.IsNullOrWhiteSpace(response.OutputText)) transcript.AppendLine(response.OutputText);
            var toolOutputs = new List<Codev.OpenAiFunctionOutput>(response.FunctionCalls.Count);
            foreach (var call in response.FunctionCalls)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var name = call.TryGetProperty("name", out var nameElement) ? nameElement.GetString() ?? "" : "";
                var callId = call.TryGetProperty("call_id", out var callIdElement) ? callIdElement.GetString() : null;
                var rawArguments = call.TryGetProperty("arguments", out var argumentsElement) ? argumentsElement.GetString() : null;
                if (string.IsNullOrWhiteSpace(callId) || string.IsNullOrWhiteSpace(rawArguments))
                    throw new InvalidOperationException("OpenAI returned a malformed function call; Codev did not run it.");
                using var argumentsDocument = JsonDocument.Parse(rawArguments);
                var arguments = argumentsDocument.RootElement.Clone();
                if (string.IsNullOrWhiteSpace(turn.ProjectPath) || !_projectFolderTrust.IsTrusted(turn.ProjectPath))
                    throw new InvalidOperationException("Workspace trust was revoked during the Code task. No further tools will run until it is trusted again.");
                if (repeatedCalls.Record(name, arguments) >= Codev.RepeatedToolCallGuard.ConfirmationThreshold)
                {
                    var confirmed = await Dispatcher.UIThread.InvokeAsync(async () => await (ConfirmRepeatedToolCallAsync?.Invoke(name) ?? Task.FromResult(false)));
                    if (!confirmed)
                    {
                        transcript.AppendLine().AppendLine("Code task stopped because the same tool call repeated. Send a follow-up with more guidance to continue.");
                        await SetAssistantTranscriptAsync(conversation, assistantIndex, transcript.ToString());
                        return;
                    }
                    repeatedCalls.AllowOneMore();
                }
                await SetConnectionStatusAsync($"OpenAI Code task · {name.Replace('_', ' ')}");
                var result = name == "update_task_checklist"
                    ? await UpdateTaskChecklistFromModelAsync(conversation, arguments)
                    : await executor.ExecuteAsync(name, arguments, cancellationToken);
                toolOutputs.Add(new Codev.OpenAiFunctionOutput(callId, result));
                Persist();
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    OnPropertyChanged(nameof(FileChangesCount));
                    OnPropertyChanged(nameof(FileChangesLabel));
                    OnPropertyChanged(nameof(CanReviewFileChanges));
                });
                transcript.AppendLine().Append("**").Append(name.Replace('_', ' ')).AppendLine("**").AppendLine(TruncateToolOutput(result));
                await SetAssistantTranscriptAsync(conversation, assistantIndex, transcript.ToString());
            }
            Codev.OpenAiToolCallHistory.AppendResponseAndOutputs(input, response, toolOutputs);
        }
        throw new InvalidOperationException("OpenAI Code task reached the eight-step tool limit. Send a follow-up to continue.");
    }

    private static object[] CreateCodeTaskToolSchemas(Codev.ShellCommandSpec shell) =>
        Codev.CodeTaskToolSchemaFactory.CreateOllamaTools(shell);

    private static IReadOnlyList<object> CreateOpenAiCodeTaskToolSchemas(Codev.ShellCommandSpec shell)
        => Codev.CodeTaskToolSchemaFactory.CreateOpenAiStrictTools(shell);

    private async Task SetConnectionStatusAsync(string status) => await Dispatcher.UIThread.InvokeAsync(() => ConnectionStatus = status);

    private async Task SetLastPromptContextAsync(Codev.Conversation conversation, Codev.PromptContextSnapshot snapshot)
    {
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            _lastPromptContexts[conversation.Id] = snapshot;
            while (_lastPromptContexts.Count > 8)
            {
                var oldest = _lastPromptContexts.Keys.FirstOrDefault(id => id != conversation.Id);
                if (oldest == Guid.Empty) break;
                _lastPromptContexts.Remove(oldest);
            }
            if (ReferenceEquals(ActiveConversation, conversation))
            {
                OnPropertyChanged(nameof(HasLastPromptContext));
                OnPropertyChanged(nameof(LastPromptContextLabel));
            }
        });
    }

    private async Task ClearLastPromptContextAsync(Codev.Conversation conversation)
    {
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            _lastPromptContexts.Remove(conversation.Id);
            if (ReferenceEquals(ActiveConversation, conversation))
            {
                OnPropertyChanged(nameof(HasLastPromptContext));
                OnPropertyChanged(nameof(LastPromptContextLabel));
            }
        });
    }

    private async Task SetLastPromptRequestBodyAsync(Codev.Conversation conversation, string requestBody)
    {
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (_lastPromptContexts.TryGetValue(conversation.Id, out var snapshot))
                _lastPromptContexts[conversation.Id] = snapshot with { SerializedRequestBody = requestBody };
            if (ReferenceEquals(ActiveConversation, conversation)) OnPropertyChanged(nameof(LastPromptContextLabel));
        });
    }

    private async Task RecordPromptTokenUsageAsync(Codev.Conversation conversation, string provider, string model, int contextLimit, int promptTokens)
    {
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            conversation.LastPromptTokens = promptTokens;
            conversation.LastPromptContext = contextLimit;
            conversation.LastPromptModel = model;
            conversation.LastPromptProvider = provider;
            _lastPromptMessageCounts[conversation.Id] = conversation.Messages.Count;
            if (_lastPromptContexts.TryGetValue(conversation.Id, out var snapshot))
                _lastPromptContexts[conversation.Id] = snapshot with { ActualPromptTokens = promptTokens };
            if (ReferenceEquals(ActiveConversation, conversation))
            {
                OnPropertyChanged(nameof(LastPromptContextLabel));
                OnPropertyChanged(nameof(ShouldOfferCompaction));
                OnPropertyChanged(nameof(ShouldWarnUnknownContext));
            }
        });
    }

    private async Task SetAssistantTranscriptAsync(Codev.Conversation conversation, int assistantIndex, string content)
    {
        conversation.Messages[assistantIndex] = conversation.Messages[assistantIndex] with { Content = content };
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (ReferenceEquals(ActiveConversation, conversation)) Messages[assistantIndex] = conversation.Messages[assistantIndex];
        });
    }

    private static string TruncateToolOutput(string value, int max = 6000) => value.Length <= max ? value : value[..max] + "\n… [tool output truncated]";

    private async Task<IReadOnlyList<string>> SelectModelRelevantProjectRulesAsync(
        Codev.PersistedQueuedTurn turn, string task, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(turn.ProjectPath) || !Directory.Exists(turn.ProjectPath)) return [];
        if (Codev.CloudModelProviders.IsCloud(turn.Provider) &&
            (!_cloudRequestsEnabled || !_cloudApiKeys.ContainsKey(turn.Provider))) return [];
        try
        {
            var service = new Codev.WorkspaceFileService(turn.ProjectPath, turn.ContextExclusions);
            var definitions = await Codev.ProjectPathInstructionRuleCatalog.LoadAsync(service, cancellationToken);
            var candidates = definitions.Where(rule => rule.Activation == Codev.ProjectPathRuleActivation.ModelRelevant)
                .Select(rule => new Codev.ProjectPathRuleCandidate(Path.GetFileNameWithoutExtension(rule.RelativePath), rule.Description, rule.Globs))
                .Take(Codev.ProjectPathInstructionRuleCatalog.MaxRules).ToArray();
            if (candidates.Length == 0) return [];

            await SetConnectionStatusAsync("Checking project rules with the selected model…");
            var fileCandidates = turn.ContextFiles is { Count: > 0 }
                ? turn.ContextFiles
                : service.ListContextFiles(maxEntries: Codev.WorkspaceFileService.MaxContextFiles);
            var includedFiles = new List<string>();
            foreach (var candidate in fileCandidates)
            {
                if (includedFiles.Count >= Codev.WorkspaceFileService.MaxContextFiles) break;
                try
                {
                    var fullPath = service.ResolvePath(candidate);
                    var relative = Path.GetRelativePath(service.Root, fullPath).Replace('\\', '/');
                    if (!File.Exists(fullPath) || service.IsContextExcluded(relative) || !service.IsSupportedContextFile(relative) ||
                        Path.GetFileName(relative).Equals(Codev.ProjectAgentInstructions.RelativePath, StringComparison.OrdinalIgnoreCase) ||
                        relative.StartsWith(".codev/rules/", StringComparison.OrdinalIgnoreCase)) continue;
                    includedFiles.Add(relative);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or NotSupportedException) { }
            }
            var input = Codev.ProjectPathRuleRelevanceSelection.BuildInput(task, includedFiles, candidates);
            string responseText;
            if (Codev.CloudModelProviders.IsCloud(turn.Provider))
            {
                if (!_cloudRequestsEnabled || !_cloudApiKeys.TryGetValue(turn.Provider, out var key)) return [];
                var result = new System.Text.StringBuilder();
                var messages = new[]
                {
                    new Codev.CloudChatMessage("system", Codev.ProjectPathRuleRelevanceSelection.SystemPrompt),
                    new Codev.CloudChatMessage("user", input)
                };
                await foreach (var delta in new Codev.CloudModelApiClient(_http).StreamChatAsync(
                                   turn.Provider, key, turn.Model, messages, cancellationToken, maxOutputTokens: 256))
                {
                    if (result.Length + delta.Length > Codev.ProjectPathRuleRelevanceSelection.MaxResponseCharacters) return [];
                    result.Append(delta);
                }
                responseText = result.ToString();
            }
            else
            {
                var payload = new
                {
                    model = turn.Model,
                    messages = new[]
                    {
                        new { role = "system", content = Codev.ProjectPathRuleRelevanceSelection.SystemPrompt },
                        new { role = "user", content = input }
                    },
                    stream = false,
                    format = "json",
                    think = false,
                    options = new { num_predict = 256 }
                };
                using var request = new HttpRequestMessage(HttpMethod.Post, Codev.OllamaEndpoint.ApiUri(_ollamaEndpoint, "api/chat"))
                { Content = JsonContent.Create(payload) };
                using var response = await _http.SendAsync(request, cancellationToken);
                if (!response.IsSuccessStatusCode) throw new HttpRequestException($"Rule relevance request returned HTTP {(int)response.StatusCode}.");
                using var document = await System.Text.Json.JsonDocument.ParseAsync(
                    await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
                if (document.RootElement.TryGetProperty("error", out var error))
                    throw new InvalidOperationException(error.GetString() ?? "Ollama could not evaluate project rules.");
                responseText = document.RootElement.TryGetProperty("message", out var message) &&
                               message.TryGetProperty("content", out var content) ? content.GetString() ?? "" : "";
            }
            var selected = Codev.ProjectPathRuleRelevanceSelection.ParseResponse(responseText, candidates);
            await SetConnectionStatusAsync($"Project rule check complete · {selected.Count} selected");
            return selected;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            await SetConnectionStatusAsync("Project rule selection failed; continuing without model-relevance rules.");
            return [];
        }
    }

    private async Task ExecuteQueuedTurnAsync(QueuedChatTurn turn)
    {
        var conversation = turn.Conversation;
        var savedTurn = turn.Turn;
        var assistantIndex = savedTurn.AssistantIndex;
        var token = new CancellationTokenSource();
        _generationCancellation = token;
        _generationConversation = conversation;
        conversation.PendingRequestCount = Math.Max(0, conversation.PendingRequestCount - 1);
        conversation.PendingTurns?.RemoveAll(item => item.AssistantIndex == assistantIndex);
        conversation.Messages[assistantIndex] = new Codev.ChatMessage("assistant", "");
        if (ReferenceEquals(ActiveConversation, conversation)) Messages[assistantIndex] = conversation.Messages[assistantIndex];
        IsGenerating = true;
        Persist();
        await _persistenceTask;
        try
        {
            if (savedTurn.IsCodeTask && (savedTurn.Provider == "ollama" && !Codev.OllamaEndpoint.IsLoopback(_ollamaEndpoint) ||
                savedTurn.Provider != "ollama" && (savedTurn.Provider != Codev.CloudModelProviders.OpenAI || !_cloudRequestsEnabled ||
                    !_cloudApiKeys.ContainsKey(savedTurn.Provider) || !savedTurn.IncludeProjectContext)))
                throw new InvalidOperationException("This Code task can no longer run because its provider connection or hosted project-context consent is unavailable.");
            if (savedTurn.IsCodeTask) await ClearLastPromptContextAsync(conversation);
            var systemPrompt = Codev.ConversationSystemPrompt.Build(savedTurn.IsCodeTask, savedTurn.IsPlanMode, savedTurn.Provider == "ollama", savedTurn.OutputStyle);
            var fullConversationHistory = conversation.Messages.Take(assistantIndex)
                .Select(message => new Codev.ChatMessage(message.Role, message.Content))
                .ToList();
            var conversationHistory = Codev.ConversationCompactionService.BuildPromptHistory(conversation, fullConversationHistory);
            var priorMessages = Codev.TaskChecklistService.ComposeCodeTaskPrompt(systemPrompt, conversationHistory, conversation, savedTurn.IsCodeTask).ToList();
            var hasSelectedProjectFiles = savedTurn.ContextFiles is { Count: > 0 };
            var projectStillTrusted = !string.IsNullOrWhiteSpace(savedTurn.ProjectPath) && _projectFolderTrust.IsTrusted(savedTurn.ProjectPath);
            var projectContext = "";
            Codev.ProjectContextReadResult? projectContextBreakdown = null;
            var repoMap = "";
            if (Codev.ProjectContextPolicy.ShouldInclude(savedTurn.ProjectPath, savedTurn.Provider,
                    savedTurn.IncludeProjectContext, hasSelectedProjectFiles, projectStillTrusted) &&
                Directory.Exists(savedTurn.ProjectPath))
            {
                var currentTask = conversation.Messages.Take(assistantIndex).LastOrDefault(message => message.IsUser)?.Content ?? "";
                var relevantRuleNames = projectStillTrusted
                    ? await SelectModelRelevantProjectRulesAsync(savedTurn, currentTask, token.Token)
                    : [];
                projectContextBreakdown = await Codev.ProjectContextReader.ReadDetailedAsync(savedTurn.ProjectPath,
                    savedTurn.ContextFiles, savedTurn.ContextExclusions, includeProjectInstructions: projectStillTrusted,
                    cancellationToken: token.Token,
                    manualRuleNames: Codev.ProjectPathInstructionRuleParser.FindManualMentions(
                        currentTask),
                    relevantRuleNames: relevantRuleNames);
                projectContext = projectContextBreakdown.Content;
                if (savedTurn.IncludeRepoMap)
                {
                    repoMap = await Codev.RepoMapBuilder.BuildAsync(savedTurn.ProjectPath,
                        savedTurn.ContextFiles, savedTurn.ContextExclusions, token.Token);
                }
                if (projectContext.Length > 0) priorMessages.Add(new Codev.ChatMessage("system", projectContext));
                if (repoMap.Length > 0) priorMessages.Add(new Codev.ChatMessage("system", repoMap));
            }
            var normalizedHistory = Codev.OllamaConversationHistory.Normalize(priorMessages);
            if (!savedTurn.IsCodeTask)
            {
                var sections = new List<Codev.PromptContextSection>
                {
                    new("System instruction", systemPrompt),
                    new("Conversation history", string.Join("\n\n", conversationHistory.Select(message => $"[{message.Role}]\n{message.Content}")))
                };
                if (projectContextBreakdown is { Instructions.Length: > 0 } detailedContext)
                    sections.Add(new("Project instructions (AGENTS.md and selected rules)", detailedContext.Instructions));
                if (projectContextBreakdown is { SourceExcerpts.Length: > 0 } sourceContext)
                    sections.Add(new("Selected project source excerpts", sourceContext.SourceExcerpts));
                if (repoMap.Length > 0) sections.Add(new("Repository map", repoMap));
                await SetLastPromptContextAsync(conversation, Codev.PromptContextBreakdown.Create(savedTurn.Provider, savedTurn.Model,
                    savedTurn.Provider == "ollama" ? savedTurn.NumCtx : 0, sections, normalizedHistory));
            }
            var output = new System.Text.StringBuilder();
            var thinking = new System.Text.StringBuilder();
            Codev.OllamaGenerationStats? generationStats = null;
            if (savedTurn.Provider == "ollama")
            {
                var history = normalizedHistory.Select(message => new OllamaChatMessage(message.Role, message.Content)).ToList();
                if (savedTurn.IsCodeTask)
                {
                    if (string.IsNullOrWhiteSpace(savedTurn.ProjectPath) || !_projectFolderTrust.IsTrusted(savedTurn.ProjectPath) || !Directory.Exists(savedTurn.ProjectPath))
                        throw new InvalidOperationException("The project folder is no longer trusted. Re-trust it before resuming this Code task.");
                    var contextSources = new List<string>();
                    if (projectContextBreakdown is { Instructions.Length: > 0 })
                        contextSources.Add("Trusted project instructions (AGENTS.md and selected rules)");
                    if (projectContextBreakdown is { SourceExcerpts.Length: > 0 })
                    {
                        if (savedTurn.ContextFiles is { Count: > 0 })
                            contextSources.AddRange(savedTurn.ContextFiles.Select(path => "Selected project file: " + path));
                        else
                            contextSources.Add("Automatically selected trusted project source excerpts");
                    }
                    if (!string.IsNullOrWhiteSpace(repoMap)) contextSources.Add("Repository map");
                    await RunCodeTaskTurnAsync(conversation, assistantIndex, history,
                        new Codev.WorkspaceFileService(savedTurn.ProjectPath, savedTurn.ContextExclusions), savedTurn, thinking, token.Token, contextSources);
                }
                else
                {
                var payload = new Dictionary<string, object> { ["model"] = savedTurn.Model, ["messages"] = history, ["think"] = savedTurn.ThinkEnabled, ["stream"] = true };
                if (Codev.OllamaRequestOptions.Build(savedTurn.NumCtx, savedTurn.Temperature, savedTurn.TopP, savedTurn.TopK,
                    savedTurn.PresencePenalty, savedTurn.RepeatPenalty, savedTurn.NumPredict) is { } options) payload["options"] = options;
                var payloadJson = JsonSerializer.Serialize(payload, JsonSerializerOptions.Web);
                await SetLastPromptRequestBodyAsync(conversation, payloadJson);
                using var request = new HttpRequestMessage(HttpMethod.Post, Codev.OllamaEndpoint.ApiUri(_ollamaEndpoint, "api/chat"))
                { Content = new StringContent(payloadJson, System.Text.Encoding.UTF8, "application/json") };
                var requestTimer = Stopwatch.StartNew();
                TimeSpan? firstTokenTime = null;
                using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token.Token);
                if (!response.IsSuccessStatusCode)
                {
                    var details = await response.Content.ReadAsStringAsync(token.Token);
                    throw new InvalidOperationException($"Ollama returned HTTP {(int)response.StatusCode} ({response.ReasonPhrase}).\n{details}");
                }
                await using var stream = await response.Content.ReadAsStreamAsync(token.Token);
                using var reader = new StreamReader(stream);
                while (await reader.ReadLineAsync(token.Token) is { } line)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    using var json = System.Text.Json.JsonDocument.Parse(line);
                    if (json.RootElement.TryGetProperty("error", out var error)) throw new InvalidOperationException(error.GetString());
                    if (json.RootElement.TryGetProperty("prompt_eval_count", out var promptCount) && promptCount.TryGetInt32(out var promptTokens))
                        await RecordPromptTokenUsageAsync(conversation, savedTurn.Provider, savedTurn.Model, savedTurn.NumCtx, promptTokens);
                    if (json.RootElement.TryGetProperty("message", out var message))
                    {
                        if (message.TryGetProperty("thinking", out var thinkingChunk) && thinkingChunk.GetString() is { Length: > 0 } thinkingDelta)
                            await AppendAssistantThinkingAsync(conversation, assistantIndex, thinking, thinkingDelta);
                        if (message.TryGetProperty("content", out var chunk))
                        {
                            var delta = chunk.GetString() ?? "";
                            if (delta.Length > 0 && firstTokenTime is null) firstTokenTime = requestTimer.Elapsed;
                            await AppendAssistantDeltaAsync(conversation, assistantIndex, output, delta);
                        }
                    }
                    generationStats = Codev.OllamaGenerationStats.FromFinalChunk(json.RootElement, firstTokenTime) ?? generationStats;
                }
                }
            }
            else if (savedTurn.IsCodeTask && savedTurn.Provider == Codev.CloudModelProviders.OpenAI)
            {
                if (string.IsNullOrWhiteSpace(savedTurn.ProjectPath) || !_projectFolderTrust.IsTrusted(savedTurn.ProjectPath) || !Directory.Exists(savedTurn.ProjectPath))
                    throw new InvalidOperationException("The project workspace is no longer trusted. Re-trust it before resuming this Code task.");
                if (!_cloudRequestsEnabled || !_cloudApiKeys.ContainsKey(savedTurn.Provider))
                    throw new InvalidOperationException("Reconnect OpenAI and approve hosted requests before resuming this Code task.");
                await RunOpenAiCodeTaskTurnAsync(conversation, assistantIndex, normalizedHistory, savedTurn, token.Token);
            }
            else
            {
                if (!_cloudRequestsEnabled || !_cloudApiKeys.TryGetValue(savedTurn.Provider, out var apiKey))
                    throw new InvalidOperationException("Reconnect this hosted provider and approve cloud requests before resuming the queued turn.");
                var cloudMessages = normalizedHistory.Select(message => new Codev.CloudChatMessage(message.Role, message.Content)).ToArray();
                await foreach (var delta in new Codev.CloudModelApiClient(_http).StreamChatAsync(
                                   savedTurn.Provider, apiKey, savedTurn.Model, cloudMessages, token.Token,
                                   onInputTokenCount: inputTokens => RecordPromptTokenUsageAsync(
                                       conversation, savedTurn.Provider, savedTurn.Model, 0, inputTokens),
                                   onRequestPayload: body => SetLastPromptRequestBodyAsync(conversation, body)))
                {
                    await AppendAssistantDeltaAsync(conversation, assistantIndex, output, delta);
                }
            }
            if (generationStats is not null)
                conversation.Messages[assistantIndex] = conversation.Messages[assistantIndex] with { GenerationStats = generationStats };
            if (string.IsNullOrWhiteSpace(conversation.Messages[assistantIndex].Content))
                conversation.Messages[assistantIndex] = new Codev.ChatMessage("assistant", "The selected provider returned an empty response.");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            var partial = conversation.Messages[assistantIndex].Content;
            conversation.Messages[assistantIndex] = conversation.Messages[assistantIndex] with { Content = string.IsNullOrWhiteSpace(partial) ? "Generation stopped." : partial + "\n\n[Generation stopped.]" };
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or System.Text.Json.JsonException or IOException)
        {
            var partial = conversation.Messages[assistantIndex].Content;
            var detail = Codev.OllamaErrorDescription.Describe(ex);
            var failure = string.IsNullOrWhiteSpace(partial) ? detail : $"{partial}\n\n[Generation stopped: {detail}]";
            conversation.Messages[assistantIndex] = conversation.Messages[assistantIndex] with { Content = failure };
        }
        finally
        {
            if (savedTurn.IsCodeTask)
            {
                var displayName = Models.FirstOrDefault(choice => choice.Provider == "ollama" && choice.Name.Equals(savedTurn.Model, StringComparison.OrdinalIgnoreCase))?.DisplayName ?? savedTurn.Model;
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (Provider == "ollama" && Model.Equals(savedTurn.Model, StringComparison.OrdinalIgnoreCase))
                        ConnectionStatus = $"Ready · {displayName}";
                });
            }
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (ReferenceEquals(ActiveConversation, conversation)) Messages[assistantIndex] = conversation.Messages[assistantIndex];
            });
            IsGenerating = false;
            _generationCancellation = null;
            _generationConversation = null;
            token.Dispose();
            conversation.UpdatedAt = DateTimeOffset.Now;
            OnPropertyChanged(nameof(MessageCountLabel));
            Persist();
            RebuildLists();
            OnPropertyChanged(nameof(QueueStatusLabel));
            ((RelayCommand)CancelQueuedCommand).NotifyCanExecuteChanged();
        }
    }

    private void RestoreQueuedTurns()
    {
        var restored = Codev.ConversationQueueRecovery.Restore(_conversations);
        foreach (var item in restored)
        {
            item.Conversation.PendingRequestCount++;
            _requestQueue.Enqueue(new QueuedChatTurn(item.Conversation, item.Turn));
        }
        foreach (var conversation in _conversations)
        {
            for (var i = 1; i < conversation.Messages.Count; i++)
            {
                if (conversation.Messages[i].Role == "assistant" && string.IsNullOrWhiteSpace(conversation.Messages[i].Content) &&
                    conversation.PendingTurns.All(turn => turn.AssistantIndex != i))
                    conversation.Messages[i] = new Codev.ChatMessage("assistant", "[Generation interrupted when Codev closed.]");
            }
        }
        _queuePaused = _requestQueue.Count > 0;
        OnPropertyChanged(nameof(QueueStatusLabel));
        OnPropertyChanged(nameof(HasQueuedTurns));
        OnPropertyChanged(nameof(CanReviewFileChanges));
        ((RelayCommand)RewindConversationCommand).NotifyCanExecuteChanged();
        ((RelayCommand)SummarizeConversationUpToCommand).NotifyCanExecuteChanged();
        ((RelayCommand)SummarizeConversationFromCommand).NotifyCanExecuteChanged();
        ((RelayCommand)ResumeQueueCommand).NotifyCanExecuteChanged();
        Persist();
    }

    private void ResumeQueue()
    {
        if (_requestQueue.Count == 0) return;
        _queuePaused = false;
        ((RelayCommand)SummarizeConversationUpToCommand).NotifyCanExecuteChanged();
        ((RelayCommand)SummarizeConversationFromCommand).NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(IsQueuePaused));
        OnPropertyChanged(nameof(QueueStatusLabel));
        ((RelayCommand)ResumeQueueCommand).NotifyCanExecuteChanged();
        _ = ProcessQueuedTurnsAsync();
    }

    private void CancelQueuedTurns()
    {
        if (ActiveConversation is not { } conversation || _requestQueue.Count == 0) return;
        var retained = new Queue<QueuedChatTurn>();
        while (_requestQueue.TryDequeue(out var turn))
        {
            if (!ReferenceEquals(turn.Conversation, conversation)) { retained.Enqueue(turn); continue; }
            conversation.Messages[turn.Turn.AssistantIndex] = new Codev.ChatMessage("assistant", "Queued request canceled before it was sent.");
            conversation.PendingTurns?.RemoveAll(item => item.AssistantIndex == turn.Turn.AssistantIndex);
            conversation.PendingRequestCount = Math.Max(0, conversation.PendingRequestCount - 1);
        }
        while (retained.TryDequeue(out var item)) _requestQueue.Enqueue(item);
        if (ReferenceEquals(ActiveConversation, conversation))
        {
            Messages.Clear();
            foreach (var message in conversation.Messages) Messages.Add(message);
        }
        if (_requestQueue.Count == 0) _queuePaused = false;
        OnPropertyChanged(nameof(HasQueuedTurns));
        OnPropertyChanged(nameof(CanReviewFileChanges));
        ((RelayCommand)RewindConversationCommand).NotifyCanExecuteChanged();
        ((RelayCommand)SummarizeConversationUpToCommand).NotifyCanExecuteChanged();
        ((RelayCommand)SummarizeConversationFromCommand).NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(IsQueuePaused));
        OnPropertyChanged(nameof(QueueStatusLabel));
        ((RelayCommand)ResumeQueueCommand).NotifyCanExecuteChanged();
        ((RelayCommand)CancelQueuedCommand).NotifyCanExecuteChanged();
        Persist();
        RebuildLists();
    }

    private void StopGeneration() => _generationCancellation?.Cancel();

    public async Task SaveFileChangesAsync()
    {
        OnPropertyChanged(nameof(FileChangesCount));
        OnPropertyChanged(nameof(FileChangesLabel));
        OnPropertyChanged(nameof(CanReviewFileChanges));
        Persist();
        await _persistenceTask;
    }

    private void ToggleTheme()
    {
        _isDarkTheme = !_isDarkTheme;
        if (Application.Current is { } app) app.RequestedThemeVariant = _isDarkTheme ? ThemeVariant.Dark : ThemeVariant.Light;
        OnPropertyChanged(nameof(ThemeLabel));
        OnPropertyChanged(nameof(IsDarkTheme));
        PersistSettings();
    }

    private void LoadSettings()
    {
        try
        {
            var settings = File.Exists(SettingsPath)
                ? Codev.AvaloniaUiSettings.Deserialize(File.ReadAllText(SettingsPath))
                : Codev.AvaloniaUiSettings.Default;
            _isDarkTheme = !string.Equals(settings.Theme, "light", StringComparison.OrdinalIgnoreCase);
            _readingWidth = Codev.AvaloniaUiSettings.NormalizeReadingWidth(settings.ReadingWidth);
            if (Codev.OllamaEndpoint.TryParse(settings.OllamaEndpoint, out var endpoint, out _)) _ollamaEndpoint = endpoint;
            var templates = settings.PromptTemplates ?? LoadLegacyPromptTemplates();
            foreach (var template in Codev.PromptTemplateCatalog.Normalize(templates)) PromptTemplates.Add(template);
            foreach (var preset in Codev.SamplingPresetCatalog.Normalize(settings.SamplingPresets)) SamplingPresets.Add(preset);
            if (settings.PromptTemplates is null && PromptTemplates.Count > 0) PersistSettings();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { _isDarkTheme = true; }
        if (Application.Current is { } app) app.RequestedThemeVariant = _isDarkTheme ? ThemeVariant.Dark : ThemeVariant.Light;
        OnPropertyChanged(nameof(ThemeLabel));
        OnPropertyChanged(nameof(IsDarkTheme));
        OnPropertyChanged(nameof(ReadingWidth));
        OnPropertyChanged(nameof(IsCompactReadingWidth));
        OnPropertyChanged(nameof(IsStandardReadingWidth));
        OnPropertyChanged(nameof(IsWideReadingWidth));
        OnPropertyChanged(nameof(IsFullReadingWidth));
        OnPropertyChanged(nameof(OllamaEndpointDisplay));
    }

    private void PersistSettings()
    {
        var settings = new Codev.AvaloniaUiSettings(_isDarkTheme ? "dark" : "light", _ollamaEndpoint.ToString(),
            PromptTemplates.ToList(), SamplingPresets.ToList(), _readingWidth);
        var revision = Interlocked.Increment(ref _settingsRevision);
        _settingsPersistenceTask = Task.Run(async () =>
        {
            await _settingsPersistGate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (revision == Interlocked.Read(ref _settingsRevision))
                    await Codev.AtomicTextFile.WriteAsync(SettingsPath, Codev.AvaloniaUiSettings.Serialize(settings)).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            finally { _settingsPersistGate.Release(); }
        });
    }

    private static List<Codev.PromptTemplate> LoadLegacyPromptTemplates()
    {
        try
        {
            var info = new FileInfo(LegacySettingsPath);
            if (!info.Exists || info.Length > 1024 * 1024) return [];
            return Codev.PromptTemplateCatalog.DeserializeLegacySettings(File.ReadAllText(LegacySettingsPath));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }

    public bool CanMentionProjectRule(string suggestion)
    {
        if (!CanSuggestProjectRules) return false;
        if (suggestion.Equals("rule:", StringComparison.OrdinalIgnoreCase)) return true;
        if (!suggestion.StartsWith("rule:", StringComparison.OrdinalIgnoreCase) ||
            ActiveConversation?.ProjectPath is not { Length: > 0 } projectPath || !Directory.Exists(projectPath)) return false;
        return GetProjectFileSuggestions(suggestion).Contains(suggestion, StringComparer.OrdinalIgnoreCase);
    }

    private bool CanSuggestProjectRules => IsProjectTrusted && (!IsHostedModel || IncludeProjectContextForHosted);

    public async Task<bool> SetOllamaEndpointAsync(string value)
    {
        if (!Codev.OllamaEndpoint.TryParse(value, out var endpoint, out var error))
        {
            ReportContextActionStatus(error);
            return false;
        }
        if (_ollamaEndpoint == endpoint) return true;
        if (IsGenerating || HasQueuedTurns || _isUnloadingModel)
        {
            ConnectionStatus = _isUnloadingModel
                ? "Wait for the current Ollama unload operation before changing the endpoint."
                : "Wait for the current response and queued turns to finish before changing the Ollama endpoint.";
            return false;
        }
        if (_isLoadingModels)
        {
            ConnectionStatus = "Wait for local model discovery to finish before changing the Ollama endpoint.";
            return false;
        }
        Interlocked.Increment(ref _modelSelectionRevision);
        _modelLoadCancellation?.Cancel();
        _ollamaEndpoint = endpoint;
        OnPropertyChanged(nameof(OllamaEndpointDisplay));
        OnPropertyChanged(nameof(ProviderStatusLabel));
        OnPropertyChanged(nameof(CanToggleCodeTaskMode));
        ((RelayCommand)ToggleCodeTaskCommand).NotifyCanExecuteChanged();
        PersistSettings();
        await LoadModelsAsync();
        return true;
    }

    public async Task LoadModelsAsync()
    {
        if (_isLoadingModels) return;
        _isLoadingModels = true;
        OnPropertyChanged(nameof(ModelPickerPlaceholder));
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var response = await _http.GetFromJsonAsync<OllamaTags>(Codev.OllamaEndpoint.ApiUri(_ollamaEndpoint, "api/tags"), timeout.Token);
            var installed = response?.Models?.Select(model => model.Name)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray() ?? [];
            var known = new (string Display, string[] Aliases)[]
            {
                ("Qwen3-Coder-Next · Q2 · 24K", ["qwen3-coder-next-q2-24k", "qwen3-coder-next:q2_k_l", "hf.co/bartowski/Qwen_Qwen3-Coder-Next-GGUF:Q2_K_L"]),
                ("Qwen3-Coder 30B · Q4 · 64K", ["qwen3-coder:30b"])
            };
            var choices = known.Select(item => (item.Display, Name: installed.FirstOrDefault(name => item.Aliases.Any(alias => RemoveLatestTag(alias).Equals(RemoveLatestTag(name), StringComparison.OrdinalIgnoreCase)))))
                .Where(item => item.Name is not null).Select(item => new ModelChoice(item.Name!, item.Display)).ToArray();
            var knownNames = known.SelectMany(item => item.Aliases).Select(RemoveLatestTag).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var allChoices = choices.Concat(installed.Where(name => !knownNames.Contains(RemoveLatestTag(name)))
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .Select(name => new ModelChoice(name, Codev.OllamaModelDisplayName.Format(name))))
                .Where(choice => !string.IsNullOrWhiteSpace(choice.Name) && !string.IsNullOrWhiteSpace(choice.DisplayName))
                .GroupBy(choice => choice.Name, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First()).ToArray();
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                var connectedCloudChoices = Models.Where(choice => choice.Provider != "ollama").ToArray();
                Models.Clear();
                foreach (var model in allChoices) Models.Add(model);
                foreach (var model in connectedCloudChoices) Models.Add(model);
                if (Provider != "ollama" && !Models.Any(choice => choice.Provider == Provider && choice.Name.Equals(Model, StringComparison.OrdinalIgnoreCase)))
                    Models.Add(new ModelChoice(Model, $"{Provider} · {Model} (connect key)", Provider));
                OnPropertyChanged(nameof(HasModels));
                NotifyCodeTaskAvailabilityProperties();
                OnPropertyChanged(nameof(IsModelPickerPlaceholderVisible));
                OnPropertyChanged(nameof(CanToggleCodeTaskMode));
                ((RelayCommand)ToggleCodeTaskCommand).NotifyCanExecuteChanged();
                if (Provider == "ollama" && allChoices.Length > 0)
                {
                    var resolved = Codev.OllamaModelSelection.ResolveInstalledTag(Model, allChoices.Select(item => item.Name));
                    if (resolved is not null && (SelectedModel is null || !resolved.Equals(Model, StringComparison.Ordinal))) Model = resolved;
                    else OnPropertyChanged(nameof(Model));
                }
                OnPropertyChanged(nameof(SelectedModel));
                RefreshContextSizes(Model);
                if (Provider != "ollama")
                    ConnectionStatus = _cloudRequestsEnabled && _cloudApiKeys.ContainsKey(Provider) ? $"Hosted model selected · {Model}" : $"{Provider} model selected · connect its API key to send";
                else if (allChoices.Length == 0) ConnectionStatus = $"Ollama connected · no models installed{(Codev.OllamaEndpoint.IsLoopback(_ollamaEndpoint) ? "" : " on remote server")}";
                else
                {
                    ConnectionStatus = $"Ollama connected · {allChoices.Length} model(s) · {(Codev.OllamaEndpoint.IsLoopback(_ollamaEndpoint) ? "local" : "remote")}";
                    _ = WarmModelAsync(Model);
                }
            });
        }
        catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException or OperationCanceledException)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (Provider == "ollama") ConnectionStatus = ex is OperationCanceledException
                    ? $"Ollama did not respond within 20 seconds at {_ollamaEndpoint.GetLeftPart(UriPartial.Authority)}"
                    : $"Ollama is not reachable at {_ollamaEndpoint.GetLeftPart(UriPartial.Authority)}";
            });
        }
        finally
        {
            _isLoadingModels = false;
            await Dispatcher.UIThread.InvokeAsync(() => OnPropertyChanged(nameof(ModelPickerPlaceholder)));
        }
    }

    public Task RefreshModelsAsync() => LoadModelsAsync();

    public async Task<IReadOnlyList<Codev.OllamaRunningModel>> GetLoadedModelsAsync(CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        return await new Codev.OllamaRuntimeClient(_http, _ollamaEndpoint).ListRunningModelsAsync(timeout.Token);
    }

    public async Task<bool> UnloadModelAsync(string model, CancellationToken cancellationToken = default)
    {
        if (_isUnloadingModel)
        {
            ReportContextActionStatus("An Ollama model unload is already in progress.");
            return false;
        }
        var blockReason = Codev.OllamaUnloadPolicy.GetBlockingReason(IsGenerating, HasQueuedTurns, _modelLoadCancellation is { IsCancellationRequested: false });
        if (blockReason is not null)
        {
            ReportContextActionStatus(blockReason);
            return false;
        }
        _isUnloadingModel = true;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            var runtime = new Codev.OllamaRuntimeClient(_http, _ollamaEndpoint);
            var loaded = await runtime.ListRunningModelsAsync(timeout.Token);
            blockReason = Codev.OllamaUnloadPolicy.GetBlockingReason(IsGenerating, HasQueuedTurns, _modelLoadCancellation is { IsCancellationRequested: false });
            if (blockReason is not null)
            {
                ReportContextActionStatus($"{blockReason} The unload was canceled.");
                return false;
            }
            var confirmedLoaded = loaded.FirstOrDefault(entry => entry.Name.Equals(model, StringComparison.OrdinalIgnoreCase));
            if (confirmedLoaded is null)
            {
                ReportContextActionStatus("That model is no longer loaded in Ollama. Refresh the loaded-model list.");
                return false;
            }
            await runtime.UnloadAsync(confirmedLoaded.Name, timeout.Token);
            ConnectionStatus = $"Unloaded {confirmedLoaded.Name} from Ollama memory";
            return true;
        }
        finally { _isUnloadingModel = false; }
    }

    public async Task<bool> ConnectCloudProviderAsync(string provider, string? apiKey, bool allowCloudRequests)
    {
        if (!CloudModelProviders.IsCloud(provider)) return false;
        var environmentName = provider == CloudModelProviders.OpenAI ? "OPENAI_API_KEY" : "ANTHROPIC_API_KEY";
        if (!allowCloudRequests)
        {
            _cloudRequestsEnabled = false;
            OnPropertyChanged(nameof(CloudRequestsEnabled));
            ReportContextActionStatus("Enable the cloud data and billing acknowledgement before connecting hosted models.");
            return false;
        }

        var enteredKey = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey.Trim();
        var key = enteredKey ?? Environment.GetEnvironmentVariable(environmentName)?.Trim();
        try
        {
            if (string.IsNullOrWhiteSpace(key)) key = await _cloudApiKeyVault.GetAsync(provider);
            if (string.IsNullOrWhiteSpace(key))
            {
                ReportContextActionStatus($"Enter a {provider} API key, set {environmentName}, or save a key in the OS credential store.");
                return false;
            }

            ConnectionStatus = $"Connecting to {provider} · loading available models…";
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var choices = await new Codev.CloudModelApiClient(_http).ListModelsAsync(provider, key.Trim(), timeout.Token);
            var keySaveWarning = "";
            if (enteredKey is not null)
            {
                try { await _cloudApiKeyVault.SaveAsync(provider, enteredKey); }
                catch (Exception ex) when (IsCredentialStoreFailure(ex))
                {
                    keySaveWarning = " · connected for this session, but the OS could not save the key";
                }
            }
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                _cloudApiKeys[provider] = key.Trim();
                _cloudRequestsEnabled = true;
                OnPropertyChanged(nameof(CloudRequestsEnabled));
                foreach (var old in Models.Where(choice => choice.Provider.Equals(provider, StringComparison.OrdinalIgnoreCase)).ToArray()) Models.Remove(old);
                foreach (var model in choices.OrderBy(choice => choice.DisplayName, StringComparer.OrdinalIgnoreCase))
                    Models.Add(new ModelChoice(model.Id, $"{provider} · {model.DisplayName}", provider));
                if (Provider.Equals(provider, StringComparison.OrdinalIgnoreCase) && !Models.Any(choice => choice.Provider == provider && choice.Name.Equals(Model, StringComparison.OrdinalIgnoreCase)))
                    Models.Add(new ModelChoice(Model, $"{provider} · {Model}", provider));
                OnPropertyChanged(nameof(HasModels));
                NotifyCodeTaskAvailabilityProperties();
                OnPropertyChanged(nameof(IsModelPickerPlaceholderVisible));
                OnPropertyChanged(nameof(SelectedModel));
                ConnectionStatus = $"Connected to {provider} · {choices.Count} model(s) available{keySaveWarning}";
            });
            return true;
        }
        catch (Exception ex) when (IsCredentialStoreFailure(ex) || ex is HttpRequestException or JsonException or OperationCanceledException or InvalidOperationException)
        {
            var message = IsCredentialStoreFailure(ex)
                ? $"Could not access the OS credential store. Paste the key to use it for this session. ({ex.GetType().Name})"
                : $"Could not connect to {provider}: {ex.Message}";
            await Dispatcher.UIThread.InvokeAsync(() => ConnectionStatus = message);
            return false;
        }
    }

    public async Task<bool> RemoveStoredCloudApiKeyAsync(string provider)
    {
        if (!CloudModelProviders.IsCloud(provider)) return false;
        try
        {
            var removed = await _cloudApiKeyVault.RemoveAsync(provider);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (_generationConversation is { IsCodeTask: true } running && running.Provider.Equals(provider, StringComparison.OrdinalIgnoreCase))
                    _generationCancellation?.Cancel();
                _cloudApiKeys.Remove(provider);
                if (ActiveConversation is { } active && active.Provider.Equals(provider, StringComparison.OrdinalIgnoreCase))
                {
                    active.Provider = "ollama";
                    active.Model = Models.FirstOrDefault(choice => choice.Provider == "ollama")?.Name ?? "";
                    active.IsCodeTask = false;
                    active.IsPlanMode = false;
                    _provider = "ollama";
                    _model = active.Model;
                    OnPropertyChanged(nameof(Model));
                    OnPropertyChanged(nameof(Provider));
                    OnPropertyChanged(nameof(IsLocalModel));
                    OnPropertyChanged(nameof(IsHostedModel));
                    OnPropertyChanged(nameof(IsOpenAIModel));
                    OnPropertyChanged(nameof(CanOpenProjectActions));
                    OnPropertyChanged(nameof(ProviderStatusLabel));
                    OnPropertyChanged(nameof(IsCodeTask));
                    OnPropertyChanged(nameof(IsPlanMode));
                    OnPropertyChanged(nameof(CanEnterCodeTaskMode));
                    _ = WarmModelAsync(active.Model);
                }
                if (_cloudApiKeys.Count == 0) _cloudRequestsEnabled = false;
                foreach (var choice in Models.Where(choice => choice.Provider.Equals(provider, StringComparison.OrdinalIgnoreCase)).ToArray())
                    Models.Remove(choice);
                _cloudRequestsEnabled = _cloudApiKeys.Count > 0;
                OnPropertyChanged(nameof(CloudRequestsEnabled));
                OnPropertyChanged(nameof(HasModels));
                OnPropertyChanged(nameof(IsModelPickerPlaceholderVisible));
                OnPropertyChanged(nameof(SelectedModel));
                NotifyCodeTaskAvailabilityProperties();
                ConnectionStatus = removed
                    ? $"Removed the saved {provider} key and disconnected it for this session. An environment variable may still provide a key."
                    : $"No saved {provider} key was found. An environment variable may still provide a key.";
            });
            return removed;
        }
        catch (Exception ex) when (IsCredentialStoreFailure(ex))
        {
            await Dispatcher.UIThread.InvokeAsync(() => ConnectionStatus = $"Could not remove the saved {provider} key from the OS credential store ({ex.GetType().Name}).");
            return false;
        }
    }

    public void DisableCloudProviders()
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_generationConversation is { IsCodeTask: true, Provider: Codev.CloudModelProviders.OpenAI }) _generationCancellation?.Cancel();
            _cloudRequestsEnabled = false;
            _cloudApiKeys.Clear();
            foreach (var choice in Models.Where(model => model.Provider != "ollama").ToArray()) Models.Remove(choice);
            if (Provider != "ollama" && ActiveConversation is { } conversation)
                Models.Add(new ModelChoice(conversation.Model, $"{Provider} · {conversation.Model} (connect key)", Provider));
            OnPropertyChanged(nameof(CloudRequestsEnabled));
            OnPropertyChanged(nameof(HasModels));
            NotifyCodeTaskAvailabilityProperties();
            OnPropertyChanged(nameof(IsModelPickerPlaceholderVisible));
            OnPropertyChanged(nameof(SelectedModel));
            ConnectionStatus = "Hosted requests disabled · local chats remain available";
        });
    }

    private static bool IsCredentialStoreFailure(Exception ex) => ex is IOException or UnauthorizedAccessException or
        DllNotFoundException or EntryPointNotFoundException or TypeInitializationException or PlatformNotSupportedException or
        System.Security.SecurityException;

    private void RebuildLists()
    {
        var visible = _conversations.Where(c => c.IsArchived == _showArchived && Codev.ConversationSearch.Matches(c, SearchText))
            .OrderByDescending(c => c.UpdatedAt).ToArray();
        Reset(PinnedConversations, visible.Where(c => c.IsPinned));
        Reset(RecentConversations, visible.Where(c => !c.IsPinned));
        OnPropertyChanged(nameof(ConversationTitle));
    }

    private bool _showArchived;
    public bool ShowArchived
    {
        get => _showArchived;
        private set
        {
            if (SetProperty(ref _showArchived, value)) OnPropertyChanged(nameof(ArchiveViewLabel));
        }
    }
    public string ArchiveViewLabel => ShowArchived ? "◷  Show recent" : "◷  Show archived";
    public ICommand ToggleArchiveViewCommand { get; }

    public void SetSelectedModel(string? model)
    {
        if (!string.IsNullOrWhiteSpace(model)) Model = model;
    }

    public async Task SavePendingDraftAsync()
    {
        _draftSaveTimer.Stop();
        if (ActiveConversation is { } conversation) conversation.Draft = Draft;
        Persist();
        try { await _persistenceTask; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        try { await _settingsPersistenceTask; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        try { await _activeConversationPersistenceTask; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static void Reset<T>(ObservableCollection<T> target, IEnumerable<T> source)
    {
        target.Clear();
        foreach (var item in source) target.Add(item);
    }

    private void LoadConversations()
    {
        try
        {
            if (!File.Exists(StorePath)) return;
            foreach (var conversation in JsonSerializer.Deserialize<List<Codev.Conversation>>(File.ReadAllText(StorePath)) ?? [])
            {
                conversation.PendingDiffComments ??= [];
                _conversations.Add(conversation);
                if (conversation.LastPromptTokens > 0 && conversation.Messages.LastOrDefault()?.IsAssistant == true)
                    _lastPromptMessageCounts[conversation.Id] = conversation.Messages.Count;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
    }

    private static Guid? LoadLastActiveConversationId()
    {
        try
        {
            if (!File.Exists(ActiveConversationPath)) return null;
            var saved = JsonSerializer.Deserialize<string>(File.ReadAllText(ActiveConversationPath));
            return Guid.TryParse(saved, out var id) ? id : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private void PersistLastActiveConversationId(Guid id)
    {
        var revision = Interlocked.Increment(ref _activeConversationRevision);
        var json = JsonSerializer.Serialize(id.ToString("D"));
        _activeConversationPersistenceTask = Task.Run(async () =>
        {
            await _activeConversationPersistGate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (revision < Interlocked.Read(ref _activeConversationRevision)) return;
                await Codev.AtomicTextFile.WriteAsync(ActiveConversationPath, json).ConfigureAwait(false);
            }
            finally { _activeConversationPersistGate.Release(); }
        });
    }

    private void Persist()
    {
        try
        {
            var snapshot = Codev.ConversationPersistence.CreateSnapshot(_conversations);
            var revision = Interlocked.Increment(ref _persistenceRevision);
            _persistenceTask = Task.Run(async () =>
            {
                var json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true });
                await _persistGate.WaitAsync().ConfigureAwait(false);
                try
                {
                    if (revision < Interlocked.Read(ref _persistenceRevision)) return;
                    await Codev.AtomicTextFile.WriteAsync(StorePath, json).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
                finally { _persistGate.Release(); }
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private void RefreshContextSizes(string model)
    {
        var selected = ActiveConversation?.NumCtx ?? _contextSize;
        var choices = Codev.OllamaContextSizes.ForModel(model);
        if (selected > 0 && !choices.Contains(selected))
        {
            selected = 0;
            if (ActiveConversation is { } conversation) conversation.NumCtx = 0;
            else _contextSize = 0;
            Persist();
        }
        ContextSizes.Clear();
        foreach (var value in choices)
            ContextSizes.Add(new ContextSizeChoice(value, value == 0 ? "Model default" : $"{value / 1024}K"));
        OnPropertyChanged(nameof(ContextSize));
    }

    private async Task WarmModelAsync(string model)
    {
        if (_isUnloadingModel) return;
        if (!Models.Any(choice => choice.Name.Equals(model, StringComparison.OrdinalIgnoreCase))) return;
        var revision = Interlocked.Increment(ref _modelSelectionRevision);
        var displayName = Models.First(choice => choice.Name.Equals(model, StringComparison.OrdinalIgnoreCase)).DisplayName;
        var stopwatch = Stopwatch.StartNew();
        var next = new CancellationTokenSource();
        next.CancelAfter(TimeSpan.FromMinutes(5));
        var previous = Interlocked.Exchange(ref _modelLoadCancellation, next);
        previous?.Cancel();
        previous?.Dispose();
        await SetModelLoadingStatusAsync(displayName, stopwatch.Elapsed, revision);
        try
        {
            if (await IsModelLoadedAsync(model, next.Token))
            {
                await SetModelReadyAsync(model, revision);
                return;
            }

            var payload = new Dictionary<string, object> { ["model"] = model, ["keep_alive"] = "5m", ["stream"] = true };
            using var request = new HttpRequestMessage(HttpMethod.Post, Codev.OllamaEndpoint.ApiUri(_ollamaEndpoint, "api/generate"))
            {
                Content = JsonContent.Create(payload)
            };
            var loadRequest = _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, next.Token);
            HttpResponseMessage? loadResponse = null;
            var ready = false;
            var lastProgressUpdate = TimeSpan.Zero;
            while (!next.IsCancellationRequested)
            {
                if (await IsModelLoadedAsync(model, next.Token))
                {
                    ready = true;
                    break;
                }
                if (loadRequest.IsCompleted && loadResponse is null)
                {
                    loadResponse = await loadRequest;
                    if (!loadResponse.IsSuccessStatusCode)
                    {
                        var details = await loadResponse.Content.ReadAsStringAsync(next.Token);
                        throw new InvalidOperationException($"Ollama returned HTTP {(int)loadResponse.StatusCode} ({loadResponse.ReasonPhrase}). {details}");
                    }
                }
                if (stopwatch.Elapsed - lastProgressUpdate >= TimeSpan.FromSeconds(5))
                {
                    lastProgressUpdate = stopwatch.Elapsed;
                    await SetModelLoadingStatusAsync(displayName, stopwatch.Elapsed, revision);
                }
                await Task.Delay(TimeSpan.FromMilliseconds(750), next.Token);
            }
            if (!ready) throw new OperationCanceledException(next.Token);

            // A load-only /api/generate request may keep streaming an empty-prompt completion.
            // Ollama's /api/ps endpoint is the authoritative readiness signal, so stop that stream
            // as soon as the selected model is resident and keep its requested keep-alive period.
            next.Cancel();
            if (loadResponse is null)
            {
                try { loadResponse = await loadRequest; }
                catch (OperationCanceledException) when (next.IsCancellationRequested) { }
            }
            loadResponse?.Dispose();
            await SetModelReadyAsync(model, revision);
        }
        catch (OperationCanceledException) when (next.IsCancellationRequested)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (revision == Interlocked.Read(ref _modelSelectionRevision))
                    ConnectionStatus = $"Model load timed out · {displayName} may still be loading in Ollama";
            });
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or System.Text.Json.JsonException or IOException)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (revision == Interlocked.Read(ref _modelSelectionRevision)) ConnectionStatus = $"Could not load model · {ex.Message}";
            });
        }
        finally
        {
            if (ReferenceEquals(Interlocked.CompareExchange(ref _modelLoadCancellation, null, next), next)) next.Dispose();
        }
    }

    private async Task<bool> IsModelLoadedAsync(string model, CancellationToken cancellationToken)
    {
        var running = await _http.GetFromJsonAsync<OllamaRunningModels>(
            Codev.OllamaEndpoint.ApiUri(_ollamaEndpoint, "api/ps"), cancellationToken);
        return running?.Models?.Any(item => !string.IsNullOrWhiteSpace(item.Name) &&
            RemoveLatestTag(item.Name).Equals(RemoveLatestTag(model), StringComparison.OrdinalIgnoreCase)) == true;
    }

    private async Task SetModelReadyAsync(string model, long revision)
    {
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (revision == Interlocked.Read(ref _modelSelectionRevision))
                ConnectionStatus = $"Ready · {Models.First(choice => choice.Name.Equals(model, StringComparison.OrdinalIgnoreCase)).DisplayName}";
        });
    }

    private Task SetModelLoadingStatusAsync(string displayName, TimeSpan elapsed, long revision) =>
        Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (revision == Interlocked.Read(ref _modelSelectionRevision))
                ConnectionStatus = $"Loading {displayName} · {elapsed:mm\\:ss} elapsed";
        }).GetTask();

    private sealed class OllamaTags { [JsonPropertyName("models")] public List<OllamaTag>? Models { get; set; } }
    private sealed class OllamaTag { [JsonPropertyName("name")] public string Name { get; set; } = ""; }
    private sealed class OllamaRunningModels { [JsonPropertyName("models")] public List<OllamaRunningModel>? Models { get; set; } }
    private sealed class OllamaRunningModel { [JsonPropertyName("name")] public string Name { get; set; } = ""; }
    private sealed record OllamaChatMessage(
        [property: JsonPropertyName("role")] string Role,
        [property: JsonPropertyName("content")] string Content,
        [property: JsonPropertyName("tool_calls"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] JsonElement? ToolCalls = null,
        [property: JsonPropertyName("tool_name"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ToolName = null);
    private sealed record QueuedChatTurn(Codev.Conversation Conversation, Codev.PersistedQueuedTurn Turn);
    private static string RemoveLatestTag(string name) => name.EndsWith(":latest", StringComparison.OrdinalIgnoreCase) ? name[..^7] : name;
}

public sealed record ModelChoice(string Name, string DisplayName, string Provider = "ollama");
public sealed record ContextSizeChoice(int Value, string DisplayName);
public sealed record OutputStyleChoice(string Value, string DisplayName);

public sealed class RelayCommand(Action<object?> execute, Predicate<object?>? canExecute = null) : ICommand
{
    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? parameter) => canExecute?.Invoke(parameter) ?? true;
    public void Execute(object? parameter) => execute(parameter);
    public void NotifyCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
