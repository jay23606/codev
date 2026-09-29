namespace Codev;

public sealed record PromptContextSection(string Name, string Content);

/// <summary>An in-memory snapshot of the last assembled chat request. It is deliberately not persisted.</summary>
public sealed record PromptContextSnapshot(
    string Provider,
    string Model,
    int ContextLimit,
    IReadOnlyList<PromptContextSection> Sections,
    IReadOnlyList<ChatMessage> Messages,
    int? ActualPromptTokens = null,
    string? SerializedRequestBody = null)
{
    public int EstimatedPromptTokens => EstimateTokens(SerializedRequestBody?.Length ?? Messages.Sum(message => message.Content.Length) + Messages.Count * 4);

    public string ToDisplayText()
    {
        var output = new System.Text.StringBuilder();
        output.AppendLine($"Provider: {Provider}")
            .AppendLine($"Model: {Model}")
            .AppendLine(ContextLimit > 0 ? $"Context limit: {ContextLimit:N0} tokens" : "Context limit: model default");
        output.AppendLine($"Estimated input: ≈{EstimatedPromptTokens:N0} tokens (rough text estimate)");
        output.AppendLine(ActualPromptTokens is { } actual
            ? $"Provider-reported input: {actual:N0} tokens"
            : "Provider-reported input: not reported");
        output.AppendLine().AppendLine("Context components:");
        foreach (var section in Sections)
            output.AppendLine($"• {section.Name}: {section.Content.Length:N0} characters · ≈{EstimateTokens(section.Content.Length):N0} tokens");

        output.AppendLine().AppendLine("Conversation messages included with this request (normalized before provider-specific formatting):");
        foreach (var message in Messages)
            output.AppendLine().Append('[').Append(message.Role).AppendLine("]").AppendLine(message.Content);
        if (SerializedRequestBody is { } requestBody)
            output.AppendLine().AppendLine("Exact request JSON body (before HTTP headers):").AppendLine(requestBody);
        return output.ToString();
    }

    private static int EstimateTokens(int characters) => Math.Max(0, (characters + 3) / 4);
}

public static class PromptContextBreakdown
{
    public static PromptContextSnapshot Create(
        string provider,
        string model,
        int contextLimit,
        IEnumerable<PromptContextSection> sections,
        IEnumerable<ChatMessage> normalizedMessages,
        string? serializedRequestBody = null)
    {
        ArgumentNullException.ThrowIfNull(sections);
        ArgumentNullException.ThrowIfNull(normalizedMessages);
        var safeSections = sections.Where(section => !string.IsNullOrWhiteSpace(section.Content)).ToArray();
        var safeMessages = normalizedMessages.Select(message => new ChatMessage(message.Role, message.Content)).ToArray();
        return new PromptContextSnapshot(provider, model, Math.Max(0, contextLimit), safeSections, safeMessages,
            SerializedRequestBody: serializedRequestBody);
    }
}
