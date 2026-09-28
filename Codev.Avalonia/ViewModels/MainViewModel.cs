using System.Collections.ObjectModel;

namespace Codev.Avalonia.ViewModels;

public sealed class MainViewModel : ViewModelBase
{
    public ObservableCollection<SpikeMessage> Messages { get; } = [];

    public MainViewModel()
    {
        for (var index = 1; index <= 21; index++)
        {
            Messages.Add(new SpikeMessage("You", $"Review the API client change, especially cancellation and retries. This is conversation turn {index}."));
            var response = string.Join('\n', new[]
            {
                "**I checked the implementation** and traced the request path through the client and tests.",
                "",
                "The request uses a linked cancellation token and limits retries to transient failures. Here is the relevant shape:",
                "",
                "```csharp",
                "using var response = await client.SendAsync(request, cancellationToken);",
                "response.EnsureSuccessStatusCode();",
                "return await response.Content.ReadFromJsonAsync<ApiResult>(cancellationToken);",
                "```",
                "",
                "| Check | Result |",
                "|:--|:--|",
                "| Cancellation | Preserved |",
                "| Retry policy | Bounded |",
                "| Error body | Captured for review |",
                "",
                $"I would keep the timeout explicit and add one test for a canceled response. This is sample content for the Avalonia conversation layout (turn {index})."
            });
            Messages.Add(new SpikeMessage("Codev", response));
        }
    }
}

public sealed record SpikeMessage(string Role, string Content);
