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
    private readonly ObservableCollection<Codev.Conversation> _conversations = [];
    private Codev.Conversation? _active;
    private string _searchText = "";
    private string _draft = "";
    private string _model = "qwen3-coder:30b";
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
    private CancellationTokenSource? _generationCancellation;
    private bool _isGenerating;
    private readonly Queue<QueuedChatTurn> _requestQueue = new();
    private bool _queuePaused;
    private bool _queueProcessorRunning;
    private string _connectionStatus = "Checking Ollama…";
    private bool _isDarkTheme = true;
    private CancellationTokenSource? _modelLoadCancellation;
    private long _modelSelectionRevision;
    private bool _isLoadingModels;

    public ObservableCollection<Codev.Conversation> PinnedConversations { get; } = [];
    public ObservableCollection<Codev.Conversation> RecentConversations { get; } = [];
    public ObservableCollection<Codev.ChatMessage> Messages { get; } = [];
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
                OnPropertyChanged(nameof(SelectedModel));
                OnPropertyChanged(nameof(ContextSize));
                RefreshContextSizes(Model);
                ((RelayCommand)TogglePinCommand).NotifyCanExecuteChanged();
                ((RelayCommand)ArchiveConversationCommand).NotifyCanExecuteChanged();
                ((RelayCommand)CancelQueuedCommand).NotifyCanExecuteChanged();
            }
        }
    }
    public string ConversationTitle => ActiveConversation?.Title is { Length: > 0 } title ? title : "New conversation";
    public string PinLabel => ActiveConversation?.IsPinned == true ? "★  Pinned" : "☆  Pin";
    public string ArchiveLabel => ActiveConversation?.IsArchived == true ? "Restore" : "Archive";
    public string MessageCountLabel => $"Local conversation · {Messages.Count} messages";
    public string ProjectLabel => ActiveConversation?.ProjectPath is { Length: > 0 } path ? Path.GetFileName(path) + " · " + path : "No project folder attached";
    public string ContextLabel => ActiveConversation?.ContextFiles.Count > 0 ? $"{ActiveConversation.ContextFiles.Count} context files" : "No project context";
    public string ConnectionStatus { get => _connectionStatus; private set => SetProperty(ref _connectionStatus, value); }
    public string ThemeLabel => _isDarkTheme ? "☼  Switch to light mode" : "☾  Switch to dark mode";
    public string SendButtonLabel => IsGenerating && string.IsNullOrWhiteSpace(Draft) ? "■" : "↑";
    public bool HasQueuedTurns => _requestQueue.Count > 0;
    public bool HasModels => Models.Count > 0;
    public bool IsModelPickerPlaceholderVisible => !HasModels;
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
            else { ActiveConversation.Model = value; OnPropertyChanged(); OnPropertyChanged(nameof(SelectedModel)); Persist(); }
            RefreshContextSizes(value);
            _ = WarmModelAsync(value);
        }
    }

    public ModelChoice? SelectedModel
    {
        get => Models.FirstOrDefault(choice => RemoveLatestTag(choice.Name).Equals(RemoveLatestTag(Model), StringComparison.OrdinalIgnoreCase));
        set
        {
            if (value is not null && !string.Equals(Model, value.Name, StringComparison.OrdinalIgnoreCase)) Model = value.Name;
        }
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
        var conversation = new Codev.Conversation { Title = "New conversation", UpdatedAt = DateTimeOffset.Now };
        _conversations.Insert(0, conversation);
        SelectConversation(conversation);
        Persist();
        RebuildLists();
    }

    private void SelectConversation(Codev.Conversation conversation)
    {
        var previousModel = Model;
        ActiveConversation = conversation;
        _model = conversation.Model;
        if (conversation.NumCtx > Codev.OllamaContextSizes.MaximumFor(conversation.Model))
        {
            conversation.NumCtx = 0;
            Persist();
        }
        RefreshContextSizes(conversation.Model);
        if (!string.Equals(previousModel, conversation.Model, StringComparison.OrdinalIgnoreCase)) _ = WarmModelAsync(conversation.Model);
        Draft = conversation.Draft;
        Messages.Clear();
        foreach (var message in conversation.Messages) Messages.Add(message);
        OnPropertyChanged(nameof(MessageCountLabel));
        OnPropertyChanged(nameof(ProjectLabel));
        OnPropertyChanged(nameof(ContextLabel));
        OnPropertyChanged(nameof(PinLabel));
        OnPropertyChanged(nameof(ArchiveLabel));
        PersistLastActiveConversationId(conversation.Id);
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
            false, false, conversation.ProjectPath, [.. conversation.ContextFiles], [], DateTimeOffset.Now, conversation.Temperature);
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

    private async Task ExecuteQueuedTurnAsync(QueuedChatTurn turn)
    {
        var conversation = turn.Conversation;
        var savedTurn = turn.Turn;
        var assistantIndex = savedTurn.AssistantIndex;
        var token = new CancellationTokenSource();
        _generationCancellation = token;
        conversation.PendingRequestCount = Math.Max(0, conversation.PendingRequestCount - 1);
        conversation.PendingTurns?.RemoveAll(item => item.AssistantIndex == assistantIndex);
        conversation.Messages[assistantIndex] = new Codev.ChatMessage("assistant", "");
        if (ReferenceEquals(ActiveConversation, conversation)) Messages[assistantIndex] = conversation.Messages[assistantIndex];
        IsGenerating = true;
        Persist();
        await _persistenceTask;
        try
        {
            var history = Codev.OllamaConversationHistory.Normalize(
                conversation.Messages.Take(assistantIndex).Select(message => new Codev.ChatMessage(message.Role, message.Content))
                    .Prepend(new Codev.ChatMessage("system", "You are Codev, a practical coding assistant running locally. Be concise, focus on useful implementation details, and do not claim to have changed files or run commands. Ordinary chat is read-only.")))
                .Select(message => new OllamaChatMessage(message.Role, message.Content)).ToList();
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
            var output = new System.Text.StringBuilder();
            while (await reader.ReadLineAsync(token.Token) is { } line)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                using var json = System.Text.Json.JsonDocument.Parse(line);
                if (json.RootElement.TryGetProperty("error", out var error)) throw new InvalidOperationException(error.GetString());
                if (json.RootElement.TryGetProperty("message", out var message) && message.TryGetProperty("content", out var chunk))
                {
                    output.Append(chunk.GetString());
                    var responseText = output.ToString();
                    conversation.Messages[assistantIndex] = new Codev.ChatMessage("assistant", responseText);
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        if (ReferenceEquals(ActiveConversation, conversation)) Messages[assistantIndex] = conversation.Messages[assistantIndex];
                    });
                }
            }
            if (string.IsNullOrWhiteSpace(conversation.Messages[assistantIndex].Content))
                conversation.Messages[assistantIndex] = new Codev.ChatMessage("assistant", "Ollama returned an empty response.");
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
                Models.Clear();
                foreach (var model in allChoices) Models.Add(model);
                OnPropertyChanged(nameof(HasModels));
                OnPropertyChanged(nameof(IsModelPickerPlaceholderVisible));
                if (Models.Count > 0)
                {
                    var resolved = Codev.OllamaModelSelection.ResolveInstalledTag(Model, Models.Select(item => item.Name));
                    if (resolved is not null && !resolved.Equals(Model, StringComparison.Ordinal)) Model = resolved;
                    else OnPropertyChanged(nameof(Model));
                }
                OnPropertyChanged(nameof(SelectedModel));
                RefreshContextSizes(Model);
                if (Models.Count == 0) ConnectionStatus = "Ollama connected · no local models installed";
                else
                {
                    ConnectionStatus = $"Ollama connected · {Models.Count} local model(s)";
                    _ = WarmModelAsync(Model);
                }
            });
        }
        catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException)
        {
            await Dispatcher.UIThread.InvokeAsync(() => ConnectionStatus = "Ollama is not reachable at 127.0.0.1:11434");
        }
        finally
        {
            _isLoadingModels = false;
            await Dispatcher.UIThread.InvokeAsync(() => OnPropertyChanged(nameof(ModelPickerPlaceholder)));
        }
    }

    public Task RefreshModelsAsync() => LoadModelsAsync();

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
        var next = new CancellationTokenSource();
        next.CancelAfter(TimeSpan.FromMinutes(5));
        var previous = Interlocked.Exchange(ref _modelLoadCancellation, next);
        previous?.Cancel();
        previous?.Dispose();
        await Dispatcher.UIThread.InvokeAsync(() => ConnectionStatus = $"Loading {Models.First(choice => choice.Name.Equals(model, StringComparison.OrdinalIgnoreCase)).DisplayName}…");
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
                    ConnectionStatus = $"Model load timed out · {Models.First(choice => choice.Name.Equals(model, StringComparison.OrdinalIgnoreCase)).DisplayName} may still be loading in Ollama";
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

public sealed record ModelChoice(string Name, string DisplayName);
public sealed record ContextSizeChoice(int Value, string DisplayName);

public sealed class RelayCommand(Action<object?> execute, Predicate<object?>? canExecute = null) : ICommand
{
    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? parameter) => canExecute?.Invoke(parameter) ?? true;
    public void Execute(object? parameter) => execute(parameter);
    public void NotifyCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
