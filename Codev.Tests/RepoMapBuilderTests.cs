namespace Codev.Tests;

public sealed class RepoMapBuilderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Codev-repo-map-tests", Guid.NewGuid().ToString("N"));

    public RepoMapBuilderTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task Builds_file_outline_and_symbols_for_safe_files_only()
    {
        Directory.CreateDirectory(Path.Combine(_root, "src"));
        await File.WriteAllTextAsync(Path.Combine(_root, "src", "app.cs"), "public class AppRunner { public void Start() {} }");
        await File.WriteAllTextAsync(Path.Combine(_root, "src", "view.ts"), "export function renderPage() {}\nexport const appState = {};\ninterface ViewModel {}");
        await File.WriteAllTextAsync(Path.Combine(_root, ".env"), "secret-value");
        await File.WriteAllTextAsync(Path.Combine(_root, "private.md"), "excluded");

        var map = await RepoMapBuilder.BuildAsync(_root, contextExclusions: ["private.md"]);

        Assert.True(map.Contains("src/app.cs: AppRunner, Start", StringComparison.Ordinal), $"Map output: {map}");
        Assert.Contains("src/view.ts: renderPage, ViewModel, appState", map);
        Assert.DoesNotContain("secret-value", map);
        Assert.DoesNotContain("private.md", map);
    }

    [Fact]
    public async Task Selected_files_scope_the_map_and_cancellation_is_honored()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "one.cs"), "class One {}");
        await File.WriteAllTextAsync(Path.Combine(_root, "two.cs"), "class Two {}");

        var map = await RepoMapBuilder.BuildAsync(_root, ["one.cs"]);
        Assert.Contains("one.cs: One", map);
        Assert.DoesNotContain("two.cs", map);

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RepoMapBuilder.BuildAsync(_root, cancellationToken: cancellation.Token));
    }

    [Fact]
    public async Task Output_remains_within_the_map_character_budget()
    {
        for (var index = 0; index < RepoMapBuilder.MaxFiles; index++)
            await File.WriteAllTextAsync(Path.Combine(_root, $"{index:D3}-{new string('f', 60)}.cs"), $"public class Symbol{index:D3} {{ }}");

        var map = await RepoMapBuilder.BuildAsync(_root);

        Assert.True(map.Length <= RepoMapBuilder.MaxCharacters, $"Map was {map.Length} characters.");
        Assert.True(map.Contains("truncated", StringComparison.Ordinal), $"Map output length {map.Length}: {map}");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { }
    }
}
