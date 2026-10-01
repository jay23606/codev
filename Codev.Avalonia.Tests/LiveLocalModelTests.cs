using Avalonia.Headless.XUnit;
using Codev;
using Codev.Avalonia.ViewModels;
using System.Text.Json;

namespace Codev.Avalonia.Tests;

public sealed class LiveLocalModelTests
{
    [AvaloniaFact]
    public async Task Live_local_chat_in_a_fresh_conversation_runs_while_a_restored_turn_stays_paused()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("CODEV_OLLAMA_LIVE_TESTS"), "1", StringComparison.Ordinal))
            return;

        var root = Path.Combine(Path.GetTempPath(), "Codev-live-ollama-queue-isolation", Guid.NewGuid().ToString("N"));
        var appData = Path.Combine(root, "Codev");
        Directory.CreateDirectory(appData);
        var model = Environment.GetEnvironmentVariable("CODEV_OLLAMA_MODEL") ?? "qwen3.8:27b";
        const string expected = "Fresh conversation ran independently";
        var savedFixture = new Conversation
        {
            Title = "Unresumed local request",
            Model = model,
            Provider = "ollama",
            Messages =
            [
                new ChatMessage("user", "This saved prompt must stay paused during the smoke test."),
                new ChatMessage("assistant", "Saved locally · select Resume saved queue to run")
            ],
            PendingTurns =
            [
                new PersistedQueuedTurn(1, model, 8192, false, false, null, [], [], DateTimeOffset.Now,
                    Temperature: 0, NumPredict: 160)
            ]
        };
        await File.WriteAllTextAsync(Path.Combine(appData, "avalonia-conversations.json"),
            JsonSerializer.Serialize(new[] { savedFixture }));

        MainViewModel? viewModel = null;
        try
        {
            viewModel = new MainViewModel(root);
            var savedConversation = Assert.Single(viewModel.RecentConversations,
                item => item.Id == savedFixture.Id);
            viewModel.NewConversationCommand.Execute(null);
            var freshConversation = Assert.IsType<Conversation>(viewModel.ActiveConversation);
            viewModel.Draft = $"Reply with this exact phrase: {expected}";
            viewModel.SendCommand.Execute(null);

            var startDeadline = DateTimeOffset.UtcNow.AddSeconds(10);
            while (!viewModel.IsGenerating && DateTimeOffset.UtcNow < startDeadline)
                await Task.Delay(50);
            Assert.True(viewModel.IsGenerating,
                "A new local prompt in a separate conversation stayed blocked behind the restored queue.");

            var completionDeadline = DateTimeOffset.UtcNow.AddMinutes(4);
            while (viewModel.IsGenerating && DateTimeOffset.UtcNow < completionDeadline)
                await Task.Delay(100);

            Assert.False(viewModel.IsGenerating, "The fresh local response did not finish within four minutes.");
            Assert.Contains(expected, freshConversation.Messages[1].Content, StringComparison.OrdinalIgnoreCase);
            Assert.True(viewModel.IsQueuePaused);
            Assert.True(viewModel.HasQueuedTurns);
            Assert.Equal("Saved locally · select Resume saved queue to run", savedConversation.Messages[1].Content);
            Assert.Single(savedConversation.PendingTurns);
        }
        finally
        {
            if (viewModel is not null)
            {
                if (viewModel.IsGenerating) viewModel.StopGenerationCommand.Execute(null);
                await viewModel.StopBackgroundCommandsAndShutdownAsync();
                await viewModel.SavePendingDraftAsync();
            }
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [AvaloniaFact]
    public async Task Live_local_chat_resumes_a_saved_turn_streams_a_reply_and_clears_queue_state()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("CODEV_OLLAMA_LIVE_TESTS"), "1", StringComparison.Ordinal))
            return;

        var root = Path.Combine(Path.GetTempPath(), "Codev-live-ollama-queue", Guid.NewGuid().ToString("N"));
        var appData = Path.Combine(root, "Codev");
        Directory.CreateDirectory(appData);
        var model = Environment.GetEnvironmentVariable("CODEV_OLLAMA_MODEL") ?? "qwen3.8:27b";
        const string expected = "Codev local streaming queue passed";
        var conversation = new Conversation
        {
            Title = "Local model streaming smoke",
            Model = model,
            Provider = "ollama",
            Messages =
            [
                new ChatMessage("user", $"Reply with this exact phrase: {expected}"),
                new ChatMessage("assistant", "Saved locally · select Resume saved queue to run")
            ],
            PendingTurns =
            [
                new PersistedQueuedTurn(1, model, 8192, false, false, null, [], [], DateTimeOffset.Now,
                    Temperature: 0, NumPredict: 160)
            ]
        };
        await File.WriteAllTextAsync(Path.Combine(appData, "avalonia-conversations.json"),
            JsonSerializer.Serialize(new[] { conversation }));

        MainViewModel? viewModel = null;
        try
        {
            viewModel = new MainViewModel(root);
            Assert.True(viewModel.IsQueuePaused);
            viewModel.ResumeQueueCommand.Execute(null);

            var deadline = DateTimeOffset.UtcNow.AddMinutes(4);
            while (DateTimeOffset.UtcNow < deadline && (viewModel.IsGenerating || viewModel.HasQueuedTurns ||
                   viewModel.Messages.ElementAtOrDefault(1)?.Content is "Saved locally · select Resume saved queue to run" or ""))
                await Task.Delay(100);

            var reply = viewModel.Messages.ElementAtOrDefault(1)?.Content;
            Assert.False(viewModel.IsGenerating, "The live local response did not finish within four minutes.");
            Assert.False(viewModel.HasQueuedTurns, "The resumed request remained queued after completion.");
            Assert.False(string.IsNullOrWhiteSpace(reply), "The local model returned an empty response.");
            Assert.Contains(expected, reply!, StringComparison.OrdinalIgnoreCase);
            Assert.NotNull(viewModel.Messages[1].GenerationStats);
        }
        finally
        {
            if (viewModel is not null)
            {
                if (viewModel.IsGenerating) viewModel.StopGenerationCommand.Execute(null);
                await viewModel.StopBackgroundCommandsAndShutdownAsync();
                await viewModel.SavePendingDraftAsync();
            }
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
