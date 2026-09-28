using System.Text.Json;

namespace Codev.Tests;

public sealed class OllamaGenerationStatsTests
{
    [Fact]
    public void Reads_reported_durations_and_computes_generation_speed()
    {
        using var document = JsonDocument.Parse("""
            { "done": true, "eval_count": 64, "eval_duration": 2000000000, "load_duration": 500000000 }
            """);

        var stats = OllamaGenerationStats.FromFinalChunk(document.RootElement, TimeSpan.FromMilliseconds(1250));

        Assert.NotNull(stats);
        Assert.Equal(TimeSpan.FromMilliseconds(1250), stats.TimeToFirstToken);
        Assert.Equal(64, stats.OutputTokens);
        Assert.Equal(32d, stats.TokensPerSecond);
        Assert.Equal(TimeSpan.FromMilliseconds(500), stats.ModelLoadTime);
        Assert.Equal("first token 1.3 s · 32.0 tokens/s · 64 tokens · model load 500 ms", stats.ToDisplayString());
    }

    [Fact]
    public void Ignores_nonfinal_chunks_and_missing_metrics()
    {
        using var intermediate = JsonDocument.Parse("""{ "done": false, "eval_count": 7 }""");
        using var emptyFinal = JsonDocument.Parse("""{ "done": true }""");

        Assert.Null(OllamaGenerationStats.FromFinalChunk(intermediate.RootElement, TimeSpan.FromMilliseconds(20)));
        Assert.Null(OllamaGenerationStats.FromFinalChunk(emptyFinal.RootElement, null));
    }

    [Fact]
    public void Preserves_first_token_measurement_when_server_metrics_are_unavailable()
    {
        using var final = JsonDocument.Parse("""{ "done": true }""");

        var stats = OllamaGenerationStats.FromFinalChunk(final.RootElement, TimeSpan.FromMilliseconds(80));

        Assert.NotNull(stats);
        Assert.Equal("first token 80 ms", stats.ToDisplayString());
    }

    [Fact]
    public void Chat_message_stats_survive_json_persistence()
    {
        var message = new ChatMessage("assistant", "answer")
        {
            GenerationStats = new OllamaGenerationStats(TimeSpan.FromMilliseconds(80), 12, 24d, TimeSpan.Zero)
        };

        var restored = JsonSerializer.Deserialize<ChatMessage>(JsonSerializer.Serialize(message));

        Assert.Equal(message.GenerationStats, restored?.GenerationStats);
        Assert.True(restored?.HasGenerationStats);
        Assert.Contains("24.0 tokens/s", restored!.GenerationStatsLabel);
    }
}
