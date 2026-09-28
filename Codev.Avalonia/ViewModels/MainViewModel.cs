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
    private static readonly string ProjectTrustPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Codev", "avalonia-trusted-folders.json");
    private static readonly string ActiveConversationPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Codev", "avalonia-active-conversation.json");
    private static readonly JsonSerializerOptions BackupJsonOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    private readonly ObservableCollection<Codev.Conversation> _conversations = [];
    private readonly Codev.ProjectFolderTrustRegistry _projectFolderTrust = Codev.ProjectFolderTrustRegistry.Load(ProjectTrustPath);
    private Codev.Conversation? _active;
    private string _searchText = "";
    private string _draft = "";
    private string _model = "qwen3-coder:30b";
    private string _provider = "ollama";
    private string _outputStyle = Codev.ConversationOutputStyles.Balanced;
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
    private Uri _ollamaEndpoint = Codev.OllamaEndpoint.Default;
    private readonly Dictionary<string, string> _cloudApiKeys = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _generationCancellation;
    private bool _isGenerating;
    private bool _isUnloadingModel;
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

    public ObservableCollection<Codev.Conversation> PinnedConversations { get; } = [];
    public ObservableCollection<Codev.Conversation> RecentConversations { get; } = [];
    public ObservableCollection<Codev.ChatMessage> Messages { get; } = [];
    public ObservableCollection<string> SelectedContextFiles { get; } = [];
    public ObservableCollection<Codev.GitDiffComment> PendingDiffComments { get; } = [];
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
    public ICommand RemoveContextFileCommand { get; }
    public ICommand ClearContextFilesCommand { get; }
    public ICommand RemoveDiffCommentCommand { get; }
    public ICommand RewindConversationCommand { get; }
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
        RemoveContextFileCommand = new RelayCommand(value => { if (value is string path) RemoveContextFile(path); });
        ClearContextFilesCommand = new RelayCommand(_ => ClearContextFiles(), _ => SelectedContextFiles.Count > 0);
        RemoveDiffCommentCommand = new RelayCommand(value => { if (value is Codev.GitDiffComment comment) RemovePendingDiffComment(comment); });
        RewindConversationCommand = new RelayCommand(value => { if (value is int index) _ = RewindConversationAsync(index); },
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
                OnPropertyChanged(nameof(PinLabel));
                OnPropertyChanged(nameof(MessageCountLabel));
                OnPropertyChanged(nameof(ProjectLabel));
                OnPropertyChanged(nameof(ContextLabel));
                OnPropertyChanged(nameof(FileChangesCount));
                OnPropertyChanged(nameof(FileChangesLabel));
                OnPropertyChanged(nameof(CanReviewFileChanges));
                OnPropertyChanged(nameof(ArchiveLabel));
                OnPropertyChanged(nameof(Model));
                OnPropertyChanged(nameof(Provider));
                OnPropertyChanged(nameof(IsLocalModel));
                OnPropertyChanged(nameof(IsHostedModel));
                OnPropertyChanged(nameof(ProviderStatusLabel));
                OnPropertyChanged(nameof(IsPlanMode));
                OnPropertyChanged(nameof(PlanModeLabel));
                OnPropertyChanged(nameof(IsCodeTask));
                OnPropertyChanged(nameof(CodeTaskLabel));
                OnPropertyChanged(nameof(CanToggleCodeTaskMode));
                OnPropertyChanged(nameof(IncludeProjectContextForHosted));
                OnPropertyChanged(nameof(IncludeRepoMap));
                OnPropertyChanged(nameof(OutputStyle));
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
            }
        }
    }
    public string ConversationTitle => ActiveConversation?.Title is { Length: > 0 } title ? title : "New conversation";
    public string PinLabel => ActiveConversation?.IsPinned == true ? "★  Pinned" : "☆  Pin";
    public string ArchiveLabel => ActiveConversation?.IsArchived == true ? "Restore" : "Archive";
    public string MessageCountLabel => $"Local conversation · {Messages.Count} messages";
    public string ProjectLabel => ActiveConversation?.ProjectPath is { Length: > 0 } path ? Path.GetFileName(path) + " · " + path : "No project folder attached";
    public int FileChangesCount => ActiveConversation?.FileChanges?.Count ?? 0;
    public string FileChangesLabel => FileChangesCount == 0 ? "Files" : $"Files · {FileChangesCount}";
    public bool CanReviewFileChanges => HasProject && FileChangesCount > 0 && !IsGenerating && ActiveConversation?.PendingRequestCount == 0;
    public bool HasProject => ActiveConversation?.ProjectPath is { Length: > 0 } path && Directory.Exists(path);
    public bool IsProjectTrusted => ActiveConversation?.ProjectPath is { Length: > 0 } path && _projectFolderTrust.IsTrusted(path);
    public string? ProjectTrustRoot => ActiveConversation?.ProjectPath is { Length: > 0 } path ? _projectFolderTrust.FindTrustedRoot(path) : null;
    public bool IsProjectTrustInherited => IsProjectTrusted && ActiveConversation?.ProjectPath is { } path && !_projectFolderTrust.IsDirectTrustRoot(path);
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
    public bool CanIncludeRepoMap => HasProject && (SelectedContextFiles.Count > 0 || IsProjectTrusted) && (!IsHostedModel || IncludeProjectContextForHosted);
    public string RepoMapEstimateLabel => IncludeRepoMap && CanIncludeRepoMap ? "Repo map: up to ≈2,000 tokens." : "";
    public string ContextActionStatus { get; private set; } = "";
    public bool HasContextActionStatus => !string.IsNullOrWhiteSpace(ContextActionStatus);
    public string ConnectionStatus { get => _connectionStatus; private set => SetProperty(ref _connectionStatus, value); }
    public string ThemeLabel => _isDarkTheme ? "☼  Switch to light mode" : "☾  Switch to dark mode";
    public string OllamaEndpointDisplay => _ollamaEndpoint.ToString().TrimEnd('/');
    public string SendButtonLabel => IsGenerating && string.IsNullOrWhiteSpace(Draft) && PendingDiffComments.Count == 0 ? "■" : "↑";
    public bool HasPendingDiffComments => PendingDiffComments.Count > 0;
    public bool HasQueuedTurns => _requestQueue.Count > 0;
    public bool HasModels => Models.Count > 0;
    public bool IsModelPickerPlaceholderVisible => !HasModels;
    public string ProviderStatusLabel => $"{(IsCodeTask ? "Code task" : IsPlanMode ? "Plan" : "Chat")} · {(IsLocalModel ? (Codev.OllamaEndpoint.IsLoopback(_ollamaEndpoint) ? "local Ollama" : "remote Ollama") : $"{Provider} hosted model")}";
    public bool IsPlanMode => ActiveConversation?.IsPlanMode ?? false;
    public string PlanModeLabel => IsPlanMode ? "Plan mode" : "Chat mode";
    public bool IsCodeTask => ActiveConversation?.IsCodeTask ?? false;
    public string CodeTaskLabel => IsCodeTask ? "Code task on" : "Code task";
    public bool CanToggleCodeTaskMode => !IsGenerating && (IsCodeTask || (IsLocalModel && Codev.OllamaEndpoint.IsLoopback(_ollamaEndpoint) && Models.Any(choice => choice.Provider == "ollama" && RemoveLatestTag(choice.Name).Equals(RemoveLatestTag(Model), StringComparison.OrdinalIgnoreCase)) && HasProject && IsProjectTrusted && !IsPlanMode));
    public Func<string, string, string, bool, string?, Task<bool>>? ReviewFileChangeAsync { get; set; }
    public Func<int, Task<bool>>? ConfirmConversationRewindAsync { get; set; }
    public Func<string, string, string, bool, Task<bool>>? ApproveProjectCommandAsync { get; set; }
    public Func<string, Task<bool>>? ConfirmRepeatedToolCallAsync { get; set; }
    public string ModelPickerPlaceholder => _isLoadingModels ? "Loading Ollama models…" :
        ConnectionStatus.StartsWith("Ollama connected", StringComparison.OrdinalIgnoreCase)
            ? Codev.OllamaEndpoint.IsLoopback(_ollamaEndpoint) ? "No local models installed" : "No models available from server"
            : "Ollama unavailable";
    public bool IsQueuePaused => _queuePaused;
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
                OnPropertyChanged(nameof(CanReviewFileChanges));
                ((RelayCommand)SendCommand).NotifyCanExecuteChanged();
                ((RelayCommand)RewindConversationCommand).NotifyCanExecuteChanged();
                ((RelayCommand)StopGenerationCommand).NotifyCanExecuteChanged();
                ((RelayCommand)TogglePlanModeCommand).NotifyCanExecuteChanged();
                ((RelayCommand)ToggleCodeTaskCommand).NotifyCanExecuteChanged();
                OnPropertyChanged(nameof(CanToggleCodeTaskMode));
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

    private void TogglePlanMode()
    {
        if (ActiveConversation is not { } conversation || IsGenerating) return;
        conversation.IsPlanMode = !conversation.IsPlanMode;
        if (conversation.IsPlanMode) conversation.IsCodeTask = false;
        OnPropertyChanged(nameof(IsPlanMode));
        OnPropertyChanged(nameof(PlanModeLabel));
        OnPropertyChanged(nameof(IsCodeTask));
        OnPropertyChanged(nameof(CodeTaskLabel));
        OnPropertyChanged(nameof(CanToggleCodeTaskMode));
        OnPropertyChanged(nameof(ProviderStatusLabel));
        ((RelayCommand)ToggleCodeTaskCommand).NotifyCanExecuteChanged();
        ((RelayCommand)TogglePlanModeCommand).NotifyCanExecuteChanged();
        Persist();
    }

    private void ToggleCodeTaskMode()
    {
        if (ActiveConversation is not { } conversation || IsGenerating) return;
        if (conversation.IsCodeTask) conversation.IsCodeTask = false;
        else if (!CanToggleCodeTaskMode)
        {
            ReportContextActionStatus("Code task mode requires a trusted project folder and a loopback Ollama endpoint. Trust the attached folder and use local Ollama first.");
            return;
        }
        else { conversation.IsCodeTask = true; conversation.IsPlanMode = false; }
        OnPropertyChanged(nameof(IsCodeTask));
        OnPropertyChanged(nameof(CodeTaskLabel));
        OnPropertyChanged(nameof(IsPlanMode));
        OnPropertyChanged(nameof(PlanModeLabel));
        OnPropertyChanged(nameof(CanToggleCodeTaskMode));
        OnPropertyChanged(nameof(ProviderStatusLabel));
        ((RelayCommand)TogglePlanModeCommand).NotifyCanExecuteChanged();
        ((RelayCommand)ToggleCodeTaskCommand).NotifyCanExecuteChanged();
        Persist();
    }
    public bool CloudRequestsEnabled => _cloudRequestsEnabled;
    public bool IncludeProjectContextForHosted
    {
        get => ActiveConversation?.IncludeProjectContextForHosted ?? false;
        set
        {
            if (ActiveConversation is not { } conversation || conversation.IncludeProjectContextForHosted == value) return;
            conversation.IncludeProjectContextForHosted = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanIncludeRepoMap));
            OnPropertyChanged(nameof(RepoMapEstimateLabel));
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
            return;
        }
        if (IsCodeTask && choice.Provider != "ollama")
        {
            ReportContextActionStatus("Code task mode requires a local Ollama model. Turn Code task mode off before selecting a hosted model.");
            OnPropertyChanged(nameof(SelectedModel));
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
            OnPropertyChanged(nameof(ProviderStatusLabel));
            OnPropertyChanged(nameof(SelectedModel));
            OnPropertyChanged(nameof(CanToggleCodeTaskMode));
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
            OnPropertyChanged(nameof(ProviderStatusLabel));
            OnPropertyChanged(nameof(SelectedModel));
            OnPropertyChanged(nameof(CanToggleCodeTaskMode));
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
        OnPropertyChanged(nameof(CanIncludeRepoMap));
        OnPropertyChanged(nameof(RepoMapEstimateLabel));
        Reset(PendingDiffComments, conversation.PendingDiffComments ?? []);
        OnPropertyChanged(nameof(HasPendingDiffComments));
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
        OnPropertyChanged(nameof(HasProject));
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

    public IReadOnlyList<string> GetProjectFileSuggestions(string prefix)
    {
        if (ActiveConversation?.ProjectPath is not { Length: > 0 } projectPath || !Directory.Exists(projectPath)) return [];
        try
        {
            return Codev.ProjectFileMentionSuggestions.Find(new Codev.WorkspaceFileService(projectPath), prefix);
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
        if (conversation.IsCodeTask && (conversation.Provider != "ollama" || !Codev.OllamaEndpoint.IsLoopback(_ollamaEndpoint) || string.IsNullOrWhiteSpace(conversation.ProjectPath) || !_projectFolderTrust.IsTrusted(conversation.ProjectPath)))
        {
            ReportContextActionStatus("Code task was not queued: it requires a loopback Ollama endpoint and a currently trusted project folder.");
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
            conversation.Provider == "ollama" || conversation.IncludeProjectContextForHosted, conversation.IncludeRepoMap, conversation.OutputStyle);
        conversation.PendingTurns ??= [];
        conversation.PendingTurns.Add(queuedTurn);
        conversation.PendingRequestCount++;
        OnPropertyChanged(nameof(CanReviewFileChanges));
        ((RelayCommand)RewindConversationCommand).NotifyCanExecuteChanged();
        var turn = new QueuedChatTurn(conversation, queuedTurn);
        _requestQueue.Enqueue(turn);
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
        var assistantMessage = new Codev.ChatMessage("assistant", Codev.ConversationStatusReport.Build(
            conversation, ReferenceEquals(_generationConversation, conversation) && IsGenerating,
            conversation.PendingRequestCount, _queuePaused, _cloudRequestsEnabled,
            trustRoot is not null, trustRoot, OllamaEndpointDisplay, Codev.OllamaEndpoint.IsLoopback(_ollamaEndpoint)));
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

    private async Task ProcessQueuedTurnsAsync()
    {
        if (_queueProcessorRunning || _queuePaused || _requestQueue.Count == 0) return;
        _queueProcessorRunning = true;
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
            OnPropertyChanged(nameof(QueueStatusLabel));
            OnPropertyChanged(nameof(HasQueuedTurns));
            ((RelayCommand)ResumeQueueCommand).NotifyCanExecuteChanged();
        }
    }

    private async Task AppendAssistantDeltaAsync(Codev.Conversation conversation, int assistantIndex, System.Text.StringBuilder output, string delta)
    {
        if (delta.Length == 0) return;
        output.Append(delta);
        conversation.Messages[assistantIndex] = new Codev.ChatMessage("assistant", output.ToString());
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (ReferenceEquals(ActiveConversation, conversation)) Messages[assistantIndex] = conversation.Messages[assistantIndex];
        });
    }

    private async Task RunCodeTaskTurnAsync(Codev.Conversation conversation, int assistantIndex,
        List<OllamaChatMessage> history, Codev.WorkspaceFileService files, Codev.PersistedQueuedTurn turn,
        CancellationToken cancellationToken)
    {
        var shell = Codev.ShellCommandResolver.ResolveCurrent();
        object[] tools =
        [
            Tool("list_files", "List project files; pass a project-relative directory or an empty string for the root.", new { relative_directory = new { type = "string" } }, ["relative_directory"]),
            Tool("read_file", "Read a supported project text/source file using a project-relative path.", new { relative_path = new { type = "string" } }, ["relative_path"]),
            Tool("search_files", "Search supported project source files for a literal string.", new { query = new { type = "string" } }, ["query"]),
            Tool("create_file", "Propose a new supported source, text, or configuration file. Codev shows the full contents for approval before creating it.", new { relative_path = new { type = "string" }, content = new { type = "string" } }, ["relative_path", "content"]),
            Tool("write_file", "Propose a complete replacement for one existing project file. Codev shows the change and requires approval before applying it.", new { relative_path = new { type = "string" }, content = new { type = "string" } }, ["relative_path", "content"]),
            Tool("apply_patch", "Propose a strict unified-diff patch for one existing project file. Pass only @@ hunk headers and lines prefixed by space, +, or -. Do not include ---/+++ file headers. Every context/removal line must match exactly; Codev rejects mismatches before review. The complete resulting file is reviewed and checkpointed before applying.", new { relative_path = new { type = "string" }, patch = new { type = "string" } }, ["relative_path", "patch"]),
            Tool("verify_command", "Request approval to run a test or lint command in the project folder. Codev reports the exact exit status and bounded output to you. A failing run allows at most two reviewed repair attempts, and every subsequent verification run needs approval. After the cap, Codev blocks further edits and commands. Do not claim success unless this tool reports exit code 0.", new { command = new { type = "string" } }, ["command"]),
            Tool("run_command", $"Request approval to run one {shell.DisplayName} command in the project folder. Every invocation requires individual approval.", new { command = new { type = "string" } }, ["command"])
        ];
        var repeatedCalls = new Codev.RepeatedToolCallGuard();
        var executor = new Codev.CodeTaskToolExecutor(files, conversation,
            async proposal => await Dispatcher.UIThread.InvokeAsync(async () => await
                (ReviewFileChangeAsync?.Invoke(proposal.RelativePath, proposal.Before, proposal.After, proposal.IsNewFile, proposal.ProposedPatch) ?? Task.FromResult(false))),
            async proposal => await Dispatcher.UIThread.InvokeAsync(async () => await
                (ApproveProjectCommandAsync?.Invoke(proposal.Command, proposal.ProjectPath, proposal.ShellName, proposal.IsVerification) ?? Task.FromResult(false))),
            status: message => _ = SetConnectionStatusAsync(message));
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
                ["stream"] = false
            };
            var options = new Dictionary<string, object>();
            if (turn.NumCtx > 0) options["num_ctx"] = turn.NumCtx;
            if (turn.Temperature is { } temperature) options["temperature"] = temperature;
            if (options.Count > 0) payload["options"] = options;
            using var request = new HttpRequestMessage(HttpMethod.Post,
                Codev.OllamaEndpoint.ApiUri(_ollamaEndpoint, "api/chat")) { Content = JsonContent.Create(payload) };
            using var response = await _http.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"Ollama returned HTTP {(int)response.StatusCode} ({response.ReasonPhrase}).\n{body}");
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.TryGetProperty("error", out var apiError)) throw new InvalidOperationException(apiError.GetString() ?? apiError.ToString());
            if (root.TryGetProperty("prompt_eval_count", out var promptCount) && promptCount.TryGetInt32(out var promptTokens))
            {
                conversation.LastPromptTokens = promptTokens;
                conversation.LastPromptContext = turn.NumCtx;
                conversation.LastPromptModel = turn.Model;
            }
            var message = root.GetProperty("message");
            var text = message.TryGetProperty("content", out var content) ? content.GetString() ?? "" : "";
            var calls = message.TryGetProperty("tool_calls", out var callArray) && callArray.ValueKind == JsonValueKind.Array
                ? callArray.EnumerateArray().Select(call => call.Clone()).ToArray() : [];
            if (calls.Length == 0)
            {
                if (!string.IsNullOrWhiteSpace(text)) transcript.Append(text);
                await SetAssistantTranscriptAsync(conversation, assistantIndex, transcript.ToString());
                return;
            }

            history.Add(new OllamaChatMessage("assistant", text, calls.Length == 0 ? null : JsonSerializer.SerializeToElement(calls)));
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
                var result = await executor.ExecuteAsync(name, arguments, cancellationToken);
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

    private async Task SetConnectionStatusAsync(string status) => await Dispatcher.UIThread.InvokeAsync(() => ConnectionStatus = status);

    private async Task SetAssistantTranscriptAsync(Codev.Conversation conversation, int assistantIndex, string content)
    {
        conversation.Messages[assistantIndex] = new Codev.ChatMessage("assistant", content);
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (ReferenceEquals(ActiveConversation, conversation)) Messages[assistantIndex] = conversation.Messages[assistantIndex];
        });
    }

    private static object Tool(string name, string description, object properties, string[] required) => new
    {
        type = "function",
        function = new { name, description, parameters = new { type = "object", properties, required } }
    };

    private static string TruncateToolOutput(string value, int max = 6000) => value.Length <= max ? value : value[..max] + "\n… [tool output truncated]";

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
            if (savedTurn.IsCodeTask && (savedTurn.Provider != "ollama" || !Codev.OllamaEndpoint.IsLoopback(_ollamaEndpoint)))
                throw new InvalidOperationException("Code task turns can only run through a loopback Ollama endpoint. Switch to local Ollama before resuming this task.");
            var systemPrompt = Codev.ConversationSystemPrompt.Build(savedTurn.IsCodeTask, savedTurn.IsPlanMode, savedTurn.Provider == "ollama", savedTurn.OutputStyle);
            var priorMessages = conversation.Messages.Take(assistantIndex)
                .Select(message => new Codev.ChatMessage(message.Role, message.Content))
                .Prepend(new Codev.ChatMessage("system", systemPrompt))
                .ToList();
            var hasSelectedProjectFiles = savedTurn.ContextFiles is { Count: > 0 };
            var projectStillTrusted = !string.IsNullOrWhiteSpace(savedTurn.ProjectPath) && _projectFolderTrust.IsTrusted(savedTurn.ProjectPath);
            if (Codev.ProjectContextPolicy.ShouldInclude(savedTurn.ProjectPath, savedTurn.Provider,
                    savedTurn.IncludeProjectContext, hasSelectedProjectFiles, projectStillTrusted) &&
                Directory.Exists(savedTurn.ProjectPath))
            {
                var projectContext = await Codev.ProjectContextReader.ReadAsync(savedTurn.ProjectPath,
                    savedTurn.ContextFiles, savedTurn.ContextExclusions, token.Token);
                priorMessages.Add(new Codev.ChatMessage("system", projectContext));
                if (savedTurn.IncludeRepoMap)
                {
                    var repoMap = await Codev.RepoMapBuilder.BuildAsync(savedTurn.ProjectPath,
                        savedTurn.ContextFiles, savedTurn.ContextExclusions, token.Token);
                    priorMessages.Add(new Codev.ChatMessage("system", repoMap));
                }
            }
            var normalizedHistory = Codev.OllamaConversationHistory.Normalize(priorMessages);
            var output = new System.Text.StringBuilder();
            Codev.OllamaGenerationStats? generationStats = null;
            if (savedTurn.Provider == "ollama")
            {
                var history = normalizedHistory.Select(message => new OllamaChatMessage(message.Role, message.Content)).ToList();
                if (savedTurn.IsCodeTask)
                {
                    if (string.IsNullOrWhiteSpace(savedTurn.ProjectPath) || !_projectFolderTrust.IsTrusted(savedTurn.ProjectPath) || !Directory.Exists(savedTurn.ProjectPath))
                        throw new InvalidOperationException("The project folder is no longer trusted. Re-trust it before resuming this Code task.");
                    await RunCodeTaskTurnAsync(conversation, assistantIndex, history,
                        new Codev.WorkspaceFileService(savedTurn.ProjectPath, savedTurn.ContextExclusions), savedTurn, token.Token);
                }
                else
                {
                var payload = new Dictionary<string, object> { ["model"] = savedTurn.Model, ["messages"] = history, ["stream"] = true };
                var options = new Dictionary<string, object>();
                if (savedTurn.NumCtx > 0) options["num_ctx"] = savedTurn.NumCtx;
                if (savedTurn.Temperature is { } temperature) options["temperature"] = temperature;
                if (options.Count > 0) payload["options"] = options;
                using var request = new HttpRequestMessage(HttpMethod.Post, Codev.OllamaEndpoint.ApiUri(_ollamaEndpoint, "api/chat")) { Content = JsonContent.Create(payload) };
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
                    if (json.RootElement.TryGetProperty("message", out var message) && message.TryGetProperty("content", out var chunk))
                    {
                        var delta = chunk.GetString() ?? "";
                        if (delta.Length > 0 && firstTokenTime is null) firstTokenTime = requestTimer.Elapsed;
                        await AppendAssistantDeltaAsync(conversation, assistantIndex, output, delta);
                    }
                    generationStats = Codev.OllamaGenerationStats.FromFinalChunk(json.RootElement, firstTokenTime) ?? generationStats;
                }
                }
            }
            else
            {
                if (!_cloudRequestsEnabled || !_cloudApiKeys.TryGetValue(savedTurn.Provider, out var apiKey))
                    throw new InvalidOperationException("Reconnect this hosted provider and approve cloud requests before resuming the queued turn.");
                var cloudMessages = normalizedHistory.Select(message => new Codev.CloudChatMessage(message.Role, message.Content)).ToArray();
                await foreach (var delta in new Codev.CloudModelApiClient(_http).StreamChatAsync(savedTurn.Provider, apiKey, savedTurn.Model, cloudMessages, token.Token))
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
            conversation.Messages[assistantIndex] = new Codev.ChatMessage("assistant", string.IsNullOrWhiteSpace(partial) ? "Generation stopped." : partial + "\n\n[Generation stopped.]");
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or System.Text.Json.JsonException or IOException)
        {
            var partial = conversation.Messages[assistantIndex].Content;
            var detail = Codev.OllamaErrorDescription.Describe(ex);
            var failure = string.IsNullOrWhiteSpace(partial) ? detail : $"{partial}\n\n[Generation stopped: {detail}]";
            conversation.Messages[assistantIndex] = new Codev.ChatMessage("assistant", failure);
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
        ((RelayCommand)ResumeQueueCommand).NotifyCanExecuteChanged();
        Persist();
    }

    private void ResumeQueue()
    {
        if (_requestQueue.Count == 0) return;
        _queuePaused = false;
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
        PersistSettings();
    }

    private void LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var settings = Codev.AvaloniaUiSettings.Deserialize(File.ReadAllText(SettingsPath));
                _isDarkTheme = !string.Equals(settings.Theme, "light", StringComparison.OrdinalIgnoreCase);
                if (Codev.OllamaEndpoint.TryParse(settings.OllamaEndpoint, out var endpoint, out _)) _ollamaEndpoint = endpoint;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { _isDarkTheme = true; }
        if (Application.Current is { } app) app.RequestedThemeVariant = _isDarkTheme ? ThemeVariant.Dark : ThemeVariant.Light;
        OnPropertyChanged(nameof(ThemeLabel));
        OnPropertyChanged(nameof(OllamaEndpointDisplay));
    }

    private void PersistSettings()
    {
        var settings = new Codev.AvaloniaUiSettings(_isDarkTheme ? "dark" : "light", _ollamaEndpoint.ToString());
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
                OnPropertyChanged(nameof(IsModelPickerPlaceholderVisible));
                OnPropertyChanged(nameof(CanToggleCodeTaskMode));
                ((RelayCommand)ToggleCodeTaskCommand).NotifyCanExecuteChanged();
                if (Provider == "ollama" && allChoices.Length > 0)
                {
                    var resolved = Codev.OllamaModelSelection.ResolveInstalledTag(Model, allChoices.Select(item => item.Name));
                    if (resolved is not null && !resolved.Equals(Model, StringComparison.Ordinal)) Model = resolved;
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
        var key = string.IsNullOrWhiteSpace(apiKey) ? Environment.GetEnvironmentVariable(environmentName) : apiKey;
        if (string.IsNullOrWhiteSpace(key))
        {
            ReportContextActionStatus($"Enter a {provider} API key or set {environmentName} in the environment.");
            return false;
        }
        if (!allowCloudRequests)
        {
            _cloudRequestsEnabled = false;
            OnPropertyChanged(nameof(CloudRequestsEnabled));
            ReportContextActionStatus("Enable the cloud data and billing acknowledgement before connecting hosted models.");
            return false;
        }

        _cloudRequestsEnabled = true;
        _cloudApiKeys[provider] = key.Trim();
        OnPropertyChanged(nameof(CloudRequestsEnabled));
        ConnectionStatus = $"Connecting to {provider} · loading available models…";
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var choices = await new Codev.CloudModelApiClient(_http).ListModelsAsync(provider, key.Trim(), timeout.Token);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                foreach (var old in Models.Where(choice => choice.Provider.Equals(provider, StringComparison.OrdinalIgnoreCase)).ToArray()) Models.Remove(old);
                foreach (var model in choices.OrderBy(choice => choice.DisplayName, StringComparer.OrdinalIgnoreCase))
                    Models.Add(new ModelChoice(model.Id, $"{provider} · {model.DisplayName}", provider));
                if (Provider.Equals(provider, StringComparison.OrdinalIgnoreCase) && !Models.Any(choice => choice.Provider == provider && choice.Name.Equals(Model, StringComparison.OrdinalIgnoreCase)))
                    Models.Add(new ModelChoice(Model, $"{provider} · {Model}", provider));
                OnPropertyChanged(nameof(HasModels));
                OnPropertyChanged(nameof(IsModelPickerPlaceholderVisible));
                OnPropertyChanged(nameof(SelectedModel));
                ConnectionStatus = $"Connected to {provider} · {choices.Count} model(s) available";
            });
            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or OperationCanceledException or InvalidOperationException)
        {
            await Dispatcher.UIThread.InvokeAsync(() => ConnectionStatus = $"Could not connect to {provider}: {ex.Message}");
            return false;
        }
    }

    public void DisableCloudProviders()
    {
        _cloudRequestsEnabled = false;
        _cloudApiKeys.Clear();
        foreach (var choice in Models.Where(model => model.Provider != "ollama").ToArray()) Models.Remove(choice);
        if (Provider != "ollama" && ActiveConversation is { } conversation)
            Models.Add(new ModelChoice(conversation.Model, $"{Provider} · {conversation.Model} (connect key)", Provider));
        OnPropertyChanged(nameof(CloudRequestsEnabled));
        OnPropertyChanged(nameof(HasModels));
        OnPropertyChanged(nameof(IsModelPickerPlaceholderVisible));
        OnPropertyChanged(nameof(SelectedModel));
        ConnectionStatus = "Hosted requests disabled · local chats remain available";
    }

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
