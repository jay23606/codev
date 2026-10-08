namespace Codev.Tests;

public sealed class OllamaModelSelectionTests
{
    [Fact]
    public void Filters_embedding_only_models_but_keeps_models_that_support_completion()
    {
        var models = OllamaModelSelection.FilterChatCapableModels(
        [
            ("nomic-embed-text:latest", (IReadOnlyCollection<string>)["embedding"]),
            ("qwen3.6:35b-a3b", (IReadOnlyCollection<string>)["completion", "tools", "thinking"]),
            ("hybrid-model", (IReadOnlyCollection<string>)["embedding", "completion"]),
            ("legacy-model", null)
        ]);

        Assert.Equal(["qwen3.6:35b-a3b", "hybrid-model", "legacy-model"], models);
    }

    [Fact]
    public void Filters_embedding_models_without_reordering_or_duplicating_chat_tags()
    {
        var models = OllamaModelSelection.FilterChatCapableModels(
        [
            ("qwen3.6:latest", (IReadOnlyCollection<string>)["completion"]),
            ("nomic-embed-text", (IReadOnlyCollection<string>)["embedding"]),
            ("qwen3.6:latest", (IReadOnlyCollection<string>)["completion"])
        ]);

        Assert.Equal(["qwen3.6:latest"], models);
    }

    [Fact]
    public void Resolves_a_saved_alias_to_the_actual_installed_latest_tag()
    {
        var resolved = OllamaModelSelection.ResolveInstalledTag("qwen3-coder-next-q2-24k", ["qwen3.8:27b", "qwen3-coder-next-q2-24k:latest"]);

        Assert.Equal("qwen3-coder-next-q2-24k:latest", resolved);
    }

    [Fact]
    public void Resolves_saved_alias_against_the_selectable_installed_choice()
    {
        var resolved = OllamaModelSelection.ResolveInstalledTag("qwen3-coder-next-q2-24k", ["qwen3-coder-next-q2-24k:latest"]);

        Assert.Equal("qwen3-coder-next-q2-24k:latest", resolved);
    }

    [Fact]
    public void Keeps_exact_installed_tag_and_ignores_blank_and_duplicate_tags()
    {
        var resolved = OllamaModelSelection.ResolveInstalledTag("Qwen3.8:27B", ["", "qwen3.8:27b", "Qwen3.8:27B"]);

        Assert.Equal("qwen3.8:27b", resolved);
    }

    [Fact]
    public void Falls_back_to_first_valid_installed_tag_when_saved_model_is_missing()
    {
        var resolved = OllamaModelSelection.ResolveInstalledTag("removed-model", ["", "qwen3.8:27b"]);

        Assert.Equal("qwen3.8:27b", resolved);
    }

    [Fact]
    public void Selects_the_first_valid_installed_tag_when_no_default_is_configured()
    {
        var resolved = OllamaModelSelection.ResolveInstalledTag("", ["qwen3.8:27b", "devstral-small-2:24b"]);

        Assert.Equal("qwen3.8:27b", resolved);
    }

    [Fact]
    public void Returns_null_when_no_nonblank_models_are_installed() =>
        Assert.Null(OllamaModelSelection.ResolveInstalledTag("model", ["", "  "]));
}
