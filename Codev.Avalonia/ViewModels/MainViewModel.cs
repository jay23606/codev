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
    private static readonly string ActiveConversationPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Codev", "avalonia-active-conversation.json");
    private static readonly JsonSerializerOptions BackupJsonOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    private readonly ObservableCollection<Codev.Conversation> _conversations = [];
    private Codev.Conversation? _active;
    private string _searchText = "";
    private string _draft = "";
    private string _model = "qwen3-coder:30b";
    private string _provider = "ollama";
    private bool _cloudRequestsEnabled;
    private int _contextSize;
    private readonly DispatcherTimer _draftSaveTimer = new() { Interval = TimeSpan.FromMilliseconds(450) };
    private readonly SemaphoreSlim _persistGate = new(1, 1);
    private Task _persistenceTask = Task.CompletedTask;
    private Task _themePersistenceTask = Task.CompletedTask;
    private Task _activeConversationPersistenceTask = Task.CompletedTask;
    private readonly SemaphoreSlim _activeConversationPersistGate = new(1, 1);
    private long _persistenceRevision;
    private long _activeConversationRevision;
    private readonly HttpClient _http = new() { Timeout = Timeout.InfiniteTimeSpan };
    private readonly Dictionary<string, string> _cloudApiKeys = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _generationCancellation;
    private bool _isGenerating;
    private readonly Queue<QueuedChatTurn> _requestQueue = new();
    private bool _queuePaused;
    private bool _queueProcessorRunning;
    private Codev.Conversation? _generationConversation;
    private string _connectionStatus = "Checking Ollama…";
    private bool _isDarkTheme = true;
    private CancellationTokenSource? _modelLoadCancellation;
    private long _modelSelectionRevision;
    private bool _isLoadingModels;

    public ObservableCollection<Codev.Conversation> PinnedConversations { get; } = [];
    public ObservableCollection<Codev.Conversation> RecentConversations { get; } = [];
    public ObservableCollection<Codev.ChatMessage> Messages { get; } = [];
    public ObservableCollection<string> SelectedContextFiles { get; } = [];
    public ObservableCollection<ContextSizeChoice> ContextSizes { get; } = [];
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
    public ICommand RemoveContextFileCommand { get; }
    public ICommand ClearContextFilesCommand { get; }
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
        RemoveContextFileCommand = new RelayCommand(value => { if (value is string path) RemoveContextFile(path); });
        ClearContextFilesCommand = new RelayCommand(_ => ClearContextFiles(), _ => SelectedContextFiles.Count > 0);
        SendCommand = new RelayCommand(_ =>
        {
            if (string.IsNullOrWhiteSpace(Draft)) StopGeneration();
            else _ = SendDraftAsync();
        }, _ => IsGenerating || !string.IsNullOrWhiteSpace(Draft));
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
        LoadTheme();
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
                OnPropertyChanged(nameof(PinLabel));
                OnPropertyChanged(nameof(MessageCountLabel));
                OnPropertyChanged(nameof(ProjectLabel));
                OnPropertyChanged(nameof(ContextLabel));
                OnPropertyChanged(nameof(ArchiveLabel));
                OnPropertyChanged(nameof(Model));
                OnPropertyChanged(nameof(Provider));
                OnPropertyChanged(nameof(IsLocalModel));
                OnPropertyChanged(nameof(IsHostedModel));
                OnPropertyChanged(nameof(ProviderStatusLabel));
                OnPropertyChanged(nameof(IsPlanMode));
                OnPropertyChanged(nameof(PlanModeLabel));
                OnPropertyChanged(nameof(IncludeProjectContextForHosted));
                OnPropertyChanged(nameof(SelectedModel));
                OnPropertyChanged(nameof(ContextSize));
                RefreshContextSizes(Model);
                ((RelayCommand)TogglePinCommand).NotifyCanExecuteChanged();
                ((RelayCommand)ArchiveConversationCommand).NotifyCanExecuteChanged();
                ((RelayCommand)CancelQueuedCommand).NotifyCanExecuteChanged();
                ((RelayCommand)TogglePlanModeCommand).NotifyCanExecuteChanged();
            }
        }
    }
    public string ConversationTitle => ActiveConversation?.Title is { Length: > 0 } title ? title : "New conversation";
    public string PinLabel => ActiveConversation?.IsPinned == true ? "★  Pinned" : "☆  Pin";
    public string ArchiveLabel => ActiveConversation?.IsArchived == true ? "Restore" : "Archive";
    public string MessageCountLabel => $"Local conversation · {Messages.Count} messages";
    public string ProjectLabel => ActiveConversation?.ProjectPath is { Length: > 0 } path ? Path.GetFileName(path) + " · " + path : "No project folder attached";
    public bool HasProject => ActiveConversation?.ProjectPath is { Length: > 0 } path && Directory.Exists(path);
    public bool HasSelectedContextFiles => SelectedContextFiles.Count > 0;
    public string ContextLabel => !HasProject ? "No project context" : SelectedContextFiles.Count > 0
        ? $"{SelectedContextFiles.Count} file(s) selected · no other files will be included"
        : "Project attached · bounded source files included automatically";
    public string ContextActionStatus { get; private set; } = "";
    public bool HasContextActionStatus => !string.IsNullOrWhiteSpace(ContextActionStatus);
    public string ConnectionStatus { get => _connectionStatus; private set => SetProperty(ref _connectionStatus, value); }
    public string ThemeLabel => _isDarkTheme ? "☼  Switch to light mode" : "☾  Switch to dark mode";
    public string SendButtonLabel => IsGenerating && string.IsNullOrWhiteSpace(Draft) ? "■" : "↑";
    public bool HasQueuedTurns => _requestQueue.Count > 0;
    public bool HasModels => Models.Count > 0;
    public bool IsModelPickerPlaceholderVisible => !HasModels;
    public string ProviderStatusLabel => $"{(IsPlanMode ? "Plan" : "Chat")} · {(IsLocalModel ? "local Ollama streaming" : $"{Provider} hosted model")}";
    public bool IsPlanMode => ActiveConversation?.IsPlanMode ?? false;
    public string PlanModeLabel => IsPlanMode ? "Plan mode" : "Chat mode";
    public string ModelPickerPlaceholder => _isLoadingModels ? "Loading local models…" :
        ConnectionStatus.StartsWith("Ollama connected", StringComparison.OrdinalIgnoreCase) ? "No local models installed" : "Ollama unavailable";
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
                ((RelayCommand)SendCommand).NotifyCanExecuteChanged();
                ((RelayCommand)StopGenerationCommand).NotifyCanExecuteChanged();
                ((RelayCommand)TogglePlanModeCommand).NotifyCanExecuteChanged();
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
            _ = WarmModelAsync(value);
        }
    }

    public string Provider => ActiveConversation?.Provider ?? _provider;
    public bool IsLocalModel => Provider == "ollama";
    public bool IsHostedModel => Codev.CloudModelProviders.IsCloud(Provider);

    private void TogglePlanMode()
    {
        if (ActiveConversation is not { } conversation || IsGenerating) return;
        conversation.IsPlanMode = !conversation.IsPlanMode;
        OnPropertyChanged(nameof(IsPlanMode));
        OnPropertyChanged(nameof(PlanModeLabel));
        OnPropertyChanged(nameof(ProviderStatusLabel));
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
            Persist();
        }
        RefreshContextSizes(choice.Name);
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
        Messages.Clear();
        foreach (var message in conversation.Messages) Messages.Add(message);
        Reset(SelectedContextFiles, conversation.ContextFiles);
        ContextActionStatus = "";
        OnPropertyChanged(nameof(ContextActionStatus));
        OnPropertyChanged(nameof(HasContextActionStatus));
        OnPropertyChanged(nameof(HasSelectedContextFiles));
        OnPropertyChanged(nameof(IncludeProjectContextForHosted));
        OnPropertyChanged(nameof(IsPlanMode));
        OnPropertyChanged(nameof(PlanModeLabel));
        OnPropertyChanged(nameof(HasProject));
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
        ContextActionStatus = "Project attached. Bounded source files will be included with local chat requests.";
        OnPropertyChanged(nameof(ProjectLabel));
        OnPropertyChanged(nameof(HasProject));
        OnPropertyChanged(nameof(ContextLabel));
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
        OnPropertyChanged(nameof(HasSelectedContextFiles));
        ((RelayCommand)ClearContextFilesCommand).NotifyCanExecuteChanged();
        Persist();
        return result;
    }

    private void RemoveContextFile(string path)
    {
        if (ActiveConversation is not { } conversation || !Codev.ProjectContextSelection.RemoveFile(conversation.ContextFiles, path)) return;
        Reset(SelectedContextFiles, conversation.ContextFiles);
        ContextActionStatus = $"Removed {path} from chat context";
        OnPropertyChanged(nameof(ContextActionStatus));
        OnPropertyChanged(nameof(HasContextActionStatus));
        OnPropertyChanged(nameof(ContextLabel));
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
        OnPropertyChanged(nameof(HasSelectedContextFiles));
        ((RelayCommand)ClearContextFilesCommand).NotifyCanExecuteChanged();
        Persist();
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
        if (ActiveConversation is not { } conversation || string.IsNullOrWhiteSpace(Draft)) return;
        if (conversation.Provider != "ollama" && (!_cloudRequestsEnabled || !_cloudApiKeys.ContainsKey(conversation.Provider)))
        {
            ReportContextActionStatus("Connect the selected provider and acknowledge that prompts and selected project context will be sent off-device before sending.");
            return;
        }
        var text = Draft.Trim();
        if (conversation.Title == "New conversation") conversation.Title = text.Length > 48 ? text[..48].TrimEnd() + "…" : text;
        else if (conversation.Messages.Count == 0) conversation.Title = text.Length > 48 ? text[..48].TrimEnd() + "…" : text;
        var userMessage = new Codev.ChatMessage("user", text);
        conversation.Messages.Add(userMessage);
        conversation.Messages.Add(new Codev.ChatMessage("assistant", ""));
        conversation.Draft = "";
        conversation.UpdatedAt = DateTimeOffset.Now;
        Draft = "";
        Messages.Add(userMessage);
        Messages.Add(conversation.Messages[^1]);
        var assistantIndex = conversation.Messages.Count - 1;
        var queuedTurn = new Codev.PersistedQueuedTurn(assistantIndex, conversation.Model, conversation.NumCtx,
            false, conversation.IsPlanMode, conversation.ProjectPath, [.. conversation.ContextFiles], [], DateTimeOffset.Now, conversation.Temperature, conversation.Provider,
            conversation.Provider == "ollama" || conversation.IncludeProjectContextForHosted);
        conversation.PendingTurns ??= [];
        conversation.PendingTurns.Add(queuedTurn);
        conversation.PendingRequestCount++;
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
            var systemPrompt = Codev.ConversationSystemPrompt.Build(savedTurn.IsPlanMode, savedTurn.Provider == "ollama");
            var priorMessages = conversation.Messages.Take(assistantIndex)
                .Select(message => new Codev.ChatMessage(message.Role, message.Content))
                .Prepend(new Codev.ChatMessage("system", systemPrompt))
                .ToList();
            if ((savedTurn.Provider == "ollama" || savedTurn.IncludeProjectContext) && !string.IsNullOrWhiteSpace(savedTurn.ProjectPath) && Directory.Exists(savedTurn.ProjectPath))
            {
                var projectContext = await Codev.ProjectContextReader.ReadAsync(savedTurn.ProjectPath,
                    savedTurn.ContextFiles, savedTurn.ContextExclusions, token.Token);
                priorMessages.Add(new Codev.ChatMessage("system", projectContext));
            }
            var normalizedHistory = Codev.OllamaConversationHistory.Normalize(priorMessages);
            var output = new System.Text.StringBuilder();
            if (savedTurn.Provider == "ollama")
            {
                var history = normalizedHistory.Select(message => new OllamaChatMessage(message.Role, message.Content)).ToList();
                var payload = new Dictionary<string, object> { ["model"] = savedTurn.Model, ["messages"] = history, ["stream"] = true };
                var options = new Dictionary<string, object>();
                if (savedTurn.NumCtx > 0) options["num_ctx"] = savedTurn.NumCtx;
                if (savedTurn.Temperature is { } temperature) options["temperature"] = temperature;
                if (options.Count > 0) payload["options"] = options;
                using var request = new HttpRequestMessage(HttpMethod.Post, Codev.OllamaEndpoint.ApiUri(Codev.OllamaEndpoint.Default, "api/chat")) { Content = JsonContent.Create(payload) };
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
                        await AppendAssistantDeltaAsync(conversation, assistantIndex, output, chunk.GetString() ?? "");
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
        OnPropertyChanged(nameof(IsQueuePaused));
        OnPropertyChanged(nameof(QueueStatusLabel));
        ((RelayCommand)ResumeQueueCommand).NotifyCanExecuteChanged();
        ((RelayCommand)CancelQueuedCommand).NotifyCanExecuteChanged();
        Persist();
        RebuildLists();
    }

    private void StopGeneration() => _generationCancellation?.Cancel();

    private void ToggleTheme()
    {
        _isDarkTheme = !_isDarkTheme;
        if (Application.Current is { } app) app.RequestedThemeVariant = _isDarkTheme ? ThemeVariant.Dark : ThemeVariant.Light;
        OnPropertyChanged(nameof(ThemeLabel));
        PersistTheme();
    }

    private void LoadTheme()
    {
        try
        {
            if (File.Exists(SettingsPath))
                _isDarkTheme = !string.Equals(JsonSerializer.Deserialize<string>(File.ReadAllText(SettingsPath)), "light", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { _isDarkTheme = true; }
        if (Application.Current is { } app) app.RequestedThemeVariant = _isDarkTheme ? ThemeVariant.Dark : ThemeVariant.Light;
        OnPropertyChanged(nameof(ThemeLabel));
    }

    private void PersistTheme()
    {
        var theme = _isDarkTheme ? "dark" : "light";
        _themePersistenceTask = Task.Run(async () =>
        {
            try { await Codev.AtomicTextFile.WriteAsync(SettingsPath, JsonSerializer.Serialize(theme)).ConfigureAwait(false); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        });
    }

    public async Task LoadModelsAsync()
    {
        if (_isLoadingModels) return;
        _isLoadingModels = true;
        OnPropertyChanged(nameof(ModelPickerPlaceholder));
        try
        {
            var response = await _http.GetFromJsonAsync<OllamaTags>(Codev.OllamaEndpoint.ApiUri(Codev.OllamaEndpoint.Default, "api/tags"));
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
                else if (allChoices.Length == 0) ConnectionStatus = "Ollama connected · no local models installed";
                else
                {
                    ConnectionStatus = $"Ollama connected · {allChoices.Length} local model(s)";
                    _ = WarmModelAsync(Model);
                }
            });
        }
        catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (Provider == "ollama") ConnectionStatus = "Ollama is not reachable at 127.0.0.1:11434";
            });
        }
        finally
        {
            _isLoadingModels = false;
            await Dispatcher.UIThread.InvokeAsync(() => OnPropertyChanged(nameof(ModelPickerPlaceholder)));
        }
    }

    public Task RefreshModelsAsync() => LoadModelsAsync();

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
        try { await _themePersistenceTask; }
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
                _conversations.Add(conversation);
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
            using var request = new HttpRequestMessage(HttpMethod.Post, Codev.OllamaEndpoint.ApiUri(Codev.OllamaEndpoint.Default, "api/generate"))
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
            Codev.OllamaEndpoint.ApiUri(Codev.OllamaEndpoint.Default, "api/ps"), cancellationToken);
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
        [property: JsonPropertyName("content")] string Content);
    private sealed record QueuedChatTurn(Codev.Conversation Conversation, Codev.PersistedQueuedTurn Turn);
    private static string RemoveLatestTag(string name) => name.EndsWith(":latest", StringComparison.OrdinalIgnoreCase) ? name[..^7] : name;
}

public sealed record ModelChoice(string Name, string DisplayName, string Provider = "ollama");
public sealed record ContextSizeChoice(int Value, string DisplayName);

public sealed class RelayCommand(Action<object?> execute, Predicate<object?>? canExecute = null) : ICommand
{
    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? parameter) => canExecute?.Invoke(parameter) ?? true;
    public void Execute(object? parameter) => execute(parameter);
    public void NotifyCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
