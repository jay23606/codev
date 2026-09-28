namespace Codev.Tests;

public sealed class OllamaModelSelectionTests
{
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
    public void Returns_null_when_no_nonblank_models_are_installed() =>
        Assert.Null(OllamaModelSelection.ResolveInstalledTag("model", ["", "  "]));
}
