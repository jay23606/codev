using System.Globalization;
using System.Text.Json;

namespace Codev;

/// <summary>Performance data returned by Ollama after a streamed chat response.</summary>
public sealed record OllamaGenerationStats(TimeSpan? TimeToFirstToken, int? OutputTokens, double? TokensPerSecond, TimeSpan? ModelLoadTime)
{
    public static OllamaGenerationStats? FromFinalChunk(JsonElement chunk, TimeSpan? timeToFirstToken)
    {
        if (chunk.ValueKind != JsonValueKind.Object || !chunk.TryGetProperty("done", out var done) || done.ValueKind != JsonValueKind.True)
            return null;

        var outputTokens = ReadInt32(chunk, "eval_count");
        var evalDurationNanoseconds = ReadInt64(chunk, "eval_duration");
        var loadDurationNanoseconds = ReadInt64(chunk, "load_duration");
        double? tokensPerSecond = outputTokens is > 0 && evalDurationNanoseconds is > 0
            ? outputTokens.Value * 1_000_000_000d / evalDurationNanoseconds.Value
            : null;
        TimeSpan? loadTime = loadDurationNanoseconds is { } duration && duration >= 0
            ? TimeSpan.FromMilliseconds(duration / 1_000_000d)
            : null;

        if (timeToFirstToken is null && outputTokens is null && tokensPerSecond is null && loadTime is null)
            return null;
        return new OllamaGenerationStats(timeToFirstToken, outputTokens, tokensPerSecond, loadTime);
    }

    public string ToDisplayString()
    {
        var parts = new List<string>(4);
        if (TimeToFirstToken is { } firstToken)
            parts.Add($"first token {FormatDuration(firstToken)}");
        if (TokensPerSecond is { } speed)
            parts.Add($"{speed.ToString("0.0", CultureInfo.InvariantCulture)} tokens/s");
        if (OutputTokens is { } tokens)
            parts.Add($"{tokens.ToString("N0", CultureInfo.InvariantCulture)} tokens");
        if (ModelLoadTime is { } loadTime)
            parts.Add($"model load {FormatDuration(loadTime)}");
        return string.Join(" · ", parts);
    }

    private static string FormatDuration(TimeSpan duration) => duration.TotalSeconds >= 1
        ? $"{duration.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture)} s"
        : $"{duration.TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture)} ms";

    private static int? ReadInt32(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.TryGetInt32(out var parsed) ? parsed : null;

    private static long? ReadInt64(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.TryGetInt64(out var parsed) ? parsed : null;
}
