using Avalonia.Headless.XUnit;
using Codev;
using Codev.Avalonia.ViewModels;
using System.Reflection;
using System.Text.Json;

namespace Codev.Avalonia.Tests;

public sealed class LiveLocalModelTests
{
    [AvaloniaFact]
    public async Task Live_local_code_task_applies_a_file_edit_in_auto_without_showing_review()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("CODEV_OLLAMA_LIVE_TESTS"), "1", StringComparison.Ordinal))
            return;

        var root = Path.Combine(Path.GetTempPath(), "Codev-live-ollama-auto-file", Guid.NewGuid().ToString("N"));
        var appData = Path.Combine(root, "app-data");
        var project = Path.Combine(root, "project");
        Directory.CreateDirectory(project);
        const string original = "Greeting before Auto mode.\n";
        const string expected = "Auto mode applied the local model's file edit.\n";
        var filePath = Path.Combine(project, "greeting.txt");
        await File.WriteAllTextAsync(filePath, original);
        MainViewModel? viewModel = null;
        var reviewWasShown = false;
        try
        {
            viewModel = new MainViewModel(appData);
            var conversation = Assert.IsType<Conversation>(viewModel.ActiveConversation);
            viewModel.SetProjectFolder(project);
            await viewModel.TrustProjectFolderAsync(project);
            await viewModel.SetProjectCommandPermissionModeAsync(ProjectCommandPermissionMode.Auto);
            conversation.Model = Environment.GetEnvironmentVariable("CODEV_OLLAMA_MODEL") ?? "qwen3.6:35b-a3b";
            conversation.Provider = "ollama";
            conversation.IsCodeTask = true;
            conversation.IsPlanMode = false;
            conversation.AgentProfileName = null;
            conversation.NumCtx = 8192;
            conversation.NumPredict = 1800;
            conversation.ThinkEnabled = false;
            conversation.Temperature = 0;
            viewModel.ReviewFileChangeAsync = (_, _, _, _, _, _) =>
            {
                reviewWasShown = true;
                return Task.FromResult(false);
            };
            viewModel.Draft = "Use write_file exactly once to replace greeting.txt with this exact content, including the final newline: " +
                JsonSerializer.Serialize(expected) + ". Do not call another tool. Do not say the edit is complete until Codev reports the tool result.";

            var send = typeof(MainViewModel).GetMethod("SendDraftAsync", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(send);
            var sendTask = Assert.IsAssignableFrom<Task>(send!.Invoke(viewModel, null));
            await sendTask;

            var sawGeneration = false;
            var deadline = DateTimeOffset.UtcNow.AddMinutes(4);
            while (DateTimeOffset.UtcNow < deadline)
            {
                sawGeneration |= viewModel.IsGenerating;
                if (sawGeneration && !viewModel.IsGenerating) break;
                await Task.Delay(100);
            }

            var transcript = string.Join("\n", conversation.Messages.Select(message => message.Content));
            Assert.True(sawGeneration, $"The local Code task did not start. Transcript: {transcript}");
            Assert.False(viewModel.IsGenerating, $"The local Code task did not finish within four minutes. Transcript: {transcript}");
            Assert.True(string.Equals(expected, await File.ReadAllTextAsync(filePath), StringComparison.Ordinal),
                $"The requested Auto file edit was not applied. Transcript: {transcript}");
            Assert.False(reviewWasShown, $"Auto mode opened the file review callback. Transcript: {transcript}");
            Assert.Contains("\"keep_alive\":\"30m\"", viewModel.GetLastPromptContextDetails(), StringComparison.Ordinal);
            var change = Assert.Single(conversation.FileChanges);
            Assert.Equal("Edit", change.Kind);
            Assert.NotNull(change.CheckpointPath);
            Assert.Equal(original, await File.ReadAllTextAsync(change.CheckpointPath!));
            Assert.Contains("checkpoint was saved", transcript, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (viewModel is not null)
            {
                if (viewModel.IsGenerating) viewModel.StopGenerationCommand.Execute(null);
                var stopDeadline = DateTimeOffset.UtcNow.AddSeconds(15);
                while (viewModel.IsGenerating && DateTimeOffset.UtcNow < stopDeadline) await Task.Delay(100);
                await viewModel.StopBackgroundCommandsAndShutdownAsync();
                await viewModel.SavePendingDraftAsync();
            }
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

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
            Assert.Contains("\"keep_alive\":\"30m\"", viewModel.GetLastPromptContextDetails(), StringComparison.Ordinal);
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
