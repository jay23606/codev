using System.Diagnostics;
using System.Text;

namespace Codev;

public sealed record BackgroundCommandSnapshot(string Id, Guid ConversationId, string Command, string WorkingDirectory,
    string Status, DateTimeOffset StartedAt, TimeSpan Elapsed, string Output, int? ExitCode)
{
    public string ElapsedLabel => $"{(int)Elapsed.TotalMinutes:00}:{Elapsed.Seconds:00}";
    public string DisplayLabel => $"{Id} · {ElapsedLabel} · {Status} · {Command}";
    public bool IsRunning => Status is "Running" or "Stopping";
}

/// <summary>Owns explicitly approved long-running shell processes and their bounded, untrusted output.</summary>
public sealed class BackgroundCommandManager : IAsyncDisposable
{
    public const int MaxRunningPerConversation = 3;
    public const int MaxRunningTotal = 8;
    public const int MaxOutputCharacters = 24_000;
    public const int MaxRetainedEntries = 100;
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private bool _disposed;

    public event EventHandler? Changed;

    public async Task<BackgroundCommandSnapshot> StartAsync(Guid conversationId, string command, string workingDirectory,
        ShellCommandSpec shell, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (conversationId == Guid.Empty) throw new ArgumentException("A conversation id is required.", nameof(conversationId));
        if (string.IsNullOrWhiteSpace(command) || command.Length > 4_000) throw new InvalidOperationException("Commands must contain 1–4,000 characters.");
        var root = Path.GetFullPath(workingDirectory);
        if (!Directory.Exists(root)) throw new InvalidOperationException("The project working directory no longer exists.");
        var startInfo = shell.CreateStartInfo(AutoSafeCommandClassifier.PrepareApprovedExecutionCommand(command, root), root);
        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        var entry = new Entry(Guid.NewGuid().ToString("N")[..12], conversationId, command, root, process, DateTimeOffset.UtcNow);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var running = _entries.Values.Count(item => item.Status == "Running");
            var conversationRunning = _entries.Values.Count(item => item.ConversationId == conversationId && item.Status == "Running");
            if (running >= MaxRunningTotal) throw new InvalidOperationException($"At most {MaxRunningTotal} background commands can run at once.");
            if (conversationRunning >= MaxRunningPerConversation) throw new InvalidOperationException($"At most {MaxRunningPerConversation} background commands can run in one conversation.");
            while (_entries.Count >= MaxRetainedEntries)
            {
                var completed = _entries.Values.Where(item => item.Status == "Exited").OrderBy(item => item.StartedAt).FirstOrDefault();
                if (completed is null) throw new InvalidOperationException("The background command history is full; stop or clear completed commands before starting another.");
                _entries.Remove(completed.Id);
                completed.Process.Dispose();
            }
            if (!process.Start()) throw new InvalidOperationException($"Could not start {shell.DisplayName}.");
            _entries.Add(entry.Id, entry);
            entry.ReadStdout = DrainAsync(process.StandardOutput, entry, "");
            entry.ReadStderr = DrainAsync(process.StandardError, entry, "STDERR: ");
            entry.WaitForExit = WatchExitAsync(entry);
        }
        Changed?.Invoke(this, EventArgs.Empty);
        await Task.Yield();
        return Snapshot(entry);
    }

    public IReadOnlyList<BackgroundCommandSnapshot> List(Guid conversationId)
    {
        lock (_gate) return _entries.Values.Where(item => item.ConversationId == conversationId)
            .OrderByDescending(item => item.StartedAt).Select(Snapshot).ToArray();
    }

    public BackgroundCommandSnapshot? Read(Guid conversationId, string id)
    {
        lock (_gate) return _entries.TryGetValue(id, out var entry) && entry.ConversationId == conversationId ? Snapshot(entry) : null;
    }

    public async Task<bool> StopAsync(Guid conversationId, string id)
    {
        Entry? entry;
        lock (_gate)
        {
            if (!_entries.TryGetValue(id, out entry) || entry.ConversationId != conversationId || entry.Status != "Running") return false;
            entry.Status = "Stopping";
        }
        Changed?.Invoke(this, EventArgs.Empty);
        await KillAndWaitAsync(entry);
        return true;
    }

    public async Task StopConversationAsync(Guid conversationId)
    {
        Entry[] entries;
        lock (_gate) entries = _entries.Values.Where(item => item.ConversationId == conversationId && item.Status == "Running").ToArray();
        foreach (var entry in entries) lock (_gate) entry.Status = "Stopping";
        if (entries.Length > 0) Changed?.Invoke(this, EventArgs.Empty);
        await Task.WhenAll(entries.Select(KillAndWaitAsync));
    }

    public async ValueTask DisposeAsync()
    {
        Entry[] entries;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            entries = _entries.Values.Where(item => item.Status is "Running" or "Stopping").ToArray();
            foreach (var entry in entries) entry.Status = "Stopping";
        }
        await Task.WhenAll(entries.Select(KillAndWaitAsync));
    }

    private async Task WatchExitAsync(Entry entry)
    {
        try
        {
            await entry.Process.WaitForExitAsync();
            try { await Task.WhenAll(entry.ReadStdout, entry.ReadStderr); } catch (IOException) { }
            lock (_gate)
            {
                entry.ExitCode = entry.Process.ExitCode;
                entry.Status = "Exited";
                entry.Process.Dispose();
            }
        }
        catch (ObjectDisposedException) { return; }
        catch (InvalidOperationException) { lock (_gate) entry.Status = "Exited"; }
        finally { Changed?.Invoke(this, EventArgs.Empty); }
    }

    private static async Task DrainAsync(StreamReader reader, Entry entry, string prefix)
    {
        var chunk = new char[2048];
        var firstChunk = true;
        while (true)
        {
            var count = await reader.ReadAsync(chunk);
            if (count == 0) return;
            lock (entry.OutputGate)
            {
                if (entry.Output.Length >= MaxOutputCharacters) continue;
                var available = MaxOutputCharacters - entry.Output.Length;
                var currentPrefix = firstChunk ? prefix : "";
                firstChunk = false;
                if (currentPrefix.Length > 0 && available <= currentPrefix.Length)
                {
                    entry.OutputTruncated = true;
                    continue;
                }
                var contentLength = Math.Min(count, Math.Max(0, available - currentPrefix.Length));
                var addition = currentPrefix + new string(chunk, 0, contentLength);
                entry.Output.Append(addition);
                if (contentLength < count || entry.Output.Length >= MaxOutputCharacters) entry.OutputTruncated = true;
            }
        }
    }

    private static async Task KillAndWaitAsync(Entry entry)
    {
        try { if (!entry.Process.HasExited) entry.Process.Kill(entireProcessTree: true); } catch { }
        try { await entry.WaitForExit; } catch { }
        try { await Task.WhenAll(entry.ReadStdout, entry.ReadStderr); } catch { }
        lock (entry.OutputGate)
        {
            if (entry.OutputTruncated && !entry.Output.ToString().EndsWith("\n[output truncated]", StringComparison.Ordinal))
            {
                const string marker = "\n[output truncated]";
                if (entry.Output.Length + marker.Length > MaxOutputCharacters) entry.Output.Length = MaxOutputCharacters - marker.Length;
                entry.Output.Append(marker);
            }
        }
    }

    private static BackgroundCommandSnapshot Snapshot(Entry entry)
    {
        string output;
        lock (entry.OutputGate)
        {
            output = entry.Output.ToString();
            if (entry.OutputTruncated && !output.EndsWith("\n[output truncated]", StringComparison.Ordinal)) output += "\n[output truncated]";
        }
        return new BackgroundCommandSnapshot(entry.Id, entry.ConversationId, entry.Command, entry.WorkingDirectory,
            entry.Status, entry.StartedAt, DateTimeOffset.UtcNow - entry.StartedAt, output, entry.ExitCode);
    }

    private sealed class Entry(string id, Guid conversationId, string command, string workingDirectory, Process process, DateTimeOffset startedAt)
    {
        public string Id { get; } = id;
        public Guid ConversationId { get; } = conversationId;
        public string Command { get; } = command;
        public string WorkingDirectory { get; } = workingDirectory;
        public Process Process { get; } = process;
        public DateTimeOffset StartedAt { get; } = startedAt;
        public object OutputGate { get; } = new();
        public StringBuilder Output { get; } = new();
        public string Status { get; set; } = "Running";
        public int? ExitCode { get; set; }
        public bool OutputTruncated { get; set; }
        public Task ReadStdout { get; set; } = Task.CompletedTask;
        public Task ReadStderr { get; set; } = Task.CompletedTask;
        public Task WaitForExit { get; set; } = Task.CompletedTask;
    }
}
