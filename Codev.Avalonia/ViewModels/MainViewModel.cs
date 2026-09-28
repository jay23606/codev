using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Collections.Generic;
using System.Windows.Input;
using Avalonia.Threading;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace Codev.Avalonia.ViewModels;

/// <summary>Local conversation browser for the Avalonia renderer prototype.</summary>
public sealed class MainViewModel : ViewModelBase
{
    private static readonly string StorePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Codev", "avalonia-conversations.json");
    private readonly ObservableCollection<Codev.Conversation> _conversations = [];
    private Codev.Conversation? _active;
    private string _searchText = "";
    private string _draft = "";
    private string _model = "devstral-small-2-64k";
    private readonly DispatcherTimer _draftSaveTimer = new() { Interval = TimeSpan.FromMilliseconds(450) };
    private readonly HttpClient _http = new() { Timeout = Timeout.InfiniteTimeSpan };
    private CancellationTokenSource? _generationCancellation;
    private bool _isGenerating;
    private string _connectionStatus = "Checking Ollama…";

    public ObservableCollection<Codev.Conversation> PinnedConversations { get; } = [];
    public ObservableCollection<Codev.Conversation> RecentConversations { get; } = [];
    public ObservableCollection<Codev.ChatMessage> Messages { get; } = [];
    public ICommand NewConversationCommand { get; }
    public ICommand SelectConversationCommand { get; }
    public ICommand TogglePinCommand { get; }
    public ICommand ArchiveConversationCommand { get; }
    public ICommand SendCommand { get; }
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
        SendCommand = new RelayCommand(_ => { if (IsGenerating) StopGeneration(); else _ = SendDraftAsync(); }, _ => IsGenerating || !string.IsNullOrWhiteSpace(Draft));
        _draftSaveTimer.Tick += (_, _) => { _draftSaveTimer.Stop(); Persist(); };
        LoadConversations();
        if (_conversations.Count == 0)
        {
            _conversations.Add(new Codev.Conversation { Title = "New conversation", UpdatedAt = DateTimeOffset.Now });
            Persist();
        }
        RebuildLists();
        SelectConversation(_conversations.FirstOrDefault(c => !c.IsArchived) ?? _conversations[0]);
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
                ((RelayCommand)TogglePinCommand).NotifyCanExecuteChanged();
                ((RelayCommand)ArchiveConversationCommand).NotifyCanExecuteChanged();
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
    public string SendButtonLabel => IsGenerating ? "■" : "↑";
    public bool IsGenerating
    {
        get => _isGenerating;
        private set
        {
            if (SetProperty(ref _isGenerating, value))
            {
                OnPropertyChanged(nameof(SendButtonLabel));
                ((RelayCommand)SendCommand).NotifyCanExecuteChanged();
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
            _draftSaveTimer.Stop();
            _draftSaveTimer.Start();
        }
    }
    public string Model { get => ActiveConversation?.Model ?? _model; set { if (ActiveConversation is null) { SetProperty(ref _model, value); return; } if (ActiveConversation.Model == value) return; ActiveConversation.Model = value; OnPropertyChanged(); Persist(); } }

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
        ActiveConversation = conversation;
        _model = conversation.Model;
        Draft = conversation.Draft;
        Messages.Clear();
        foreach (var message in conversation.Messages) Messages.Add(message);
        OnPropertyChanged(nameof(MessageCountLabel));
        OnPropertyChanged(nameof(ProjectLabel));
        OnPropertyChanged(nameof(ContextLabel));
        OnPropertyChanged(nameof(PinLabel));
        OnPropertyChanged(nameof(ArchiveLabel));
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
        if (ActiveConversation is not { } conversation || string.IsNullOrWhiteSpace(Draft) || IsGenerating) return;
        var text = Draft.Trim();
        if (conversation.Title == "New conversation") conversation.Title = text.Length > 48 ? text[..48].TrimEnd() + "…" : text;
        else if (conversation.Messages.Count == 0) conversation.Title = text.Length > 48 ? text[..48].TrimEnd() + "…" : text;
        conversation.Messages.Add(new Codev.ChatMessage("user", text));
        conversation.Messages.Add(new Codev.ChatMessage("assistant", ""));
        conversation.Draft = "";
        conversation.UpdatedAt = DateTimeOffset.Now;
        Draft = "";
        Messages.Add(conversation.Messages[^1]);
        Messages.Add(conversation.Messages[^1]);
        var assistantIndex = conversation.Messages.Count - 1;
        var token = new CancellationTokenSource();
        _generationCancellation = token;
        IsGenerating = true;
        OnPropertyChanged(nameof(ConversationTitle));
        OnPropertyChanged(nameof(MessageCountLabel));
        Persist();
        RebuildLists();
        try
        {
            var history = conversation.Messages.Take(assistantIndex).Select(message => new OllamaChatMessage(message.Role, message.Content)).ToList();
            history.Insert(0, new OllamaChatMessage("system", "You are Codev, a practical coding assistant running locally. Be concise, focus on useful implementation details, and do not claim to have changed files or run commands. Ordinary chat is read-only."));
            var payload = new Dictionary<string, object> { ["model"] = conversation.Model, ["messages"] = history, ["stream"] = true };
            if (conversation.NumCtx > 0) payload["options"] = new Dictionary<string, object> { ["num_ctx"] = conversation.NumCtx };
            using var request = new HttpRequestMessage(HttpMethod.Post, Codev.OllamaEndpoint.ApiUri(Codev.OllamaEndpoint.Default, "api/chat")) { Content = JsonContent.Create(payload) };
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token.Token);
            response.EnsureSuccessStatusCode();
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
                    if (ReferenceEquals(ActiveConversation, conversation)) Messages[assistantIndex] = conversation.Messages[assistantIndex];
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
            conversation.Messages[assistantIndex] = new Codev.ChatMessage("assistant", $"Could not reach Ollama or complete the request.\n\n{ex.Message}\n\nCheck that Ollama is running at http://127.0.0.1:11434 and that the selected model is installed.");
        }
        finally
        {
            if (ReferenceEquals(ActiveConversation, conversation)) Messages[assistantIndex] = conversation.Messages[assistantIndex];
            IsGenerating = false;
            _generationCancellation = null;
            token.Dispose();
            conversation.UpdatedAt = DateTimeOffset.Now;
            OnPropertyChanged(nameof(MessageCountLabel));
            Persist();
            RebuildLists();
        }
    }

    private void StopGeneration() => _generationCancellation?.Cancel();

    public async Task LoadModelsAsync()
    {
        try
        {
            var response = await _http.GetFromJsonAsync<OllamaTags>(Codev.OllamaEndpoint.ApiUri(Codev.OllamaEndpoint.Default, "api/tags"));
            var installed = response?.Models?.Select(model => model.Name).ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];
            var known = new (string Display, string[] Aliases)[]
            {
                ("Devstral Small 2 · Q4 · 64K", ["devstral-small-2-64k", "devstral-small-2:q4_k_m", "hf.co/bartowski/mistralai_Devstral-Small-2-24B-Instruct-2512-GGUF:Q4_K_M"]),
                ("Qwen3-Coder-Next · Q2 · 24K", ["qwen3-coder-next-q2-24k", "qwen3-coder-next:q2_k_l", "hf.co/bartowski/Qwen_Qwen3-Coder-Next-GGUF:Q2_K_L"]),
                ("Qwen3-Coder 30B · Q4 · 64K", ["qwen3-coder:30b"])
            };
            var choices = known.Select(item => (item.Display, Name: installed.FirstOrDefault(name => item.Aliases.Any(alias => RemoveLatestTag(alias).Equals(RemoveLatestTag(name), StringComparison.OrdinalIgnoreCase)))))
                .Where(item => item.Name is not null).Select(item => new ModelChoice(item.Name!, item.Display)).ToArray();
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                Models.Clear();
                foreach (var model in choices) Models.Add(model);
                if (Models.Count > 0 && Models.All(m => !m.Name.Equals(Model, StringComparison.OrdinalIgnoreCase))) Model = Models[0].Name;
                ConnectionStatus = Models.Count == 0 ? "Ollama connected · no supported models installed" : $"Ollama connected · {Models.Count} local coding model(s)";
            });
        }
        catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException)
        {
            await Dispatcher.UIThread.InvokeAsync(() => ConnectionStatus = "Ollama is not reachable at 127.0.0.1:11434");
        }
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

    public void SavePendingDraft()
    {
        _draftSaveTimer.Stop();
        if (ActiveConversation is { } conversation) conversation.Draft = Draft;
        Persist();
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

    private void Persist()
    {
        try
        {
            var snapshot = Codev.ConversationPersistence.CreateSnapshot(_conversations);
            Codev.AtomicTextFile.WriteAsync(StorePath, JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true })).GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private sealed class OllamaTags { [JsonPropertyName("models")] public List<OllamaTag>? Models { get; set; } }
    private sealed class OllamaTag { [JsonPropertyName("name")] public string Name { get; set; } = ""; }
    private sealed record OllamaChatMessage(string Role, string Content);
    private static string RemoveLatestTag(string name) => name.EndsWith(":latest", StringComparison.OrdinalIgnoreCase) ? name[..^7] : name;
}

public sealed record ModelChoice(string Name, string DisplayName);

public sealed class RelayCommand(Action<object?> execute, Predicate<object?>? canExecute = null) : ICommand
{
    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? parameter) => canExecute?.Invoke(parameter) ?? true;
    public void Execute(object? parameter) => execute(parameter);
    public void NotifyCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
