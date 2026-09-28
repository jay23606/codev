using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Collections.Generic;
using System.Windows.Input;
using Avalonia.Threading;

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

    public ObservableCollection<Codev.Conversation> PinnedConversations { get; } = [];
    public ObservableCollection<Codev.Conversation> RecentConversations { get; } = [];
    public ObservableCollection<Codev.ChatMessage> Messages { get; } = [];
    public ICommand NewConversationCommand { get; }
    public ICommand SelectConversationCommand { get; }
    public ICommand TogglePinCommand { get; }
    public ICommand ArchiveConversationCommand { get; }
    public ICommand SendCommand { get; }
    public IReadOnlyList<ModelChoice> Models { get; } =
    [
        new("devstral-small-2-64k", "Devstral Small 2 · Q4 · 64K"),
        new("qwen3-coder-next-q2-24k", "Qwen3-Coder-Next · Q2 · 24K"),
        new("qwen3-coder:30b", "Qwen3-Coder 30B · Q4 · 64K")
    ];

    public MainViewModel()
    {
        NewConversationCommand = new RelayCommand(_ => NewConversation());
        SelectConversationCommand = new RelayCommand(value => { if (value is Codev.Conversation conversation) SelectConversation(conversation); });
        TogglePinCommand = new RelayCommand(_ => TogglePin(), _ => ActiveConversation is not null);
        ArchiveConversationCommand = new RelayCommand(_ => ArchiveConversation(), _ => ActiveConversation is not null);
        ToggleArchiveViewCommand = new RelayCommand(_ => { ShowArchived = !ShowArchived; RebuildLists(); });
        SendCommand = new RelayCommand(_ => SendDraft(), _ => !string.IsNullOrWhiteSpace(Draft));
        _draftSaveTimer.Tick += (_, _) => { _draftSaveTimer.Stop(); Persist(); };
        LoadConversations();
        if (_conversations.Count == 0)
        {
            _conversations.Add(new Codev.Conversation { Title = "New conversation", UpdatedAt = DateTimeOffset.Now });
            Persist();
        }
        RebuildLists();
        SelectConversation(_conversations.FirstOrDefault(c => !c.IsArchived) ?? _conversations[0]);
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

    private void SendDraft()
    {
        if (ActiveConversation is not { } conversation || string.IsNullOrWhiteSpace(Draft)) return;
        var text = Draft.Trim();
        if (conversation.Title == "New conversation") conversation.Title = text.Length > 48 ? text[..48].TrimEnd() + "…" : text;
        else if (conversation.Messages.Count == 0) conversation.Title = text.Length > 48 ? text[..48].TrimEnd() + "…" : text;
        conversation.Messages.Add(new Codev.ChatMessage("user", text));
        conversation.Draft = "";
        conversation.UpdatedAt = DateTimeOffset.Now;
        Draft = "";
        Messages.Add(conversation.Messages[^1]);
        OnPropertyChanged(nameof(ConversationTitle));
        OnPropertyChanged(nameof(MessageCountLabel));
        Persist();
        RebuildLists();
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
}

public sealed record ModelChoice(string Name, string DisplayName);

public sealed class RelayCommand(Action<object?> execute, Predicate<object?>? canExecute = null) : ICommand
{
    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? parameter) => canExecute?.Invoke(parameter) ?? true;
    public void Execute(object? parameter) => execute(parameter);
    public void NotifyCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
