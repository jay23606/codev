namespace Codev.Tests;

public sealed class ProjectContextReaderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Codev-context-reader-tests", Guid.NewGuid().ToString("N"));

    public ProjectContextReaderTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task Reads_only_selected_project_files_and_ignores_secrets_and_path_escapes()
    {
        Directory.CreateDirectory(Path.Combine(_root, "src"));
        await File.WriteAllTextAsync(Path.Combine(_root, "src", "app.cs"), "class App { }\n// selected");
        await File.WriteAllTextAsync(Path.Combine(_root, "other.cs"), "class Other { }");
        await File.WriteAllTextAsync(Path.Combine(_root, ".env"), "API_KEY=not-for-context");

        var context = await ProjectContextReader.ReadAsync(_root,
            ["src/app.cs", ".env", "../outside.cs"]);

        Assert.Contains("src/app.cs", context);
        Assert.Contains("// selected", context);
        Assert.DoesNotContain("class Other", context);
        Assert.DoesNotContain("not-for-context", context);
        Assert.Contains("limited to 1 source files", context);
    }

    [Fact]
    public async Task Reads_a_bounded_default_file_set_when_none_was_selected()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "readme.md"), "project overview");
        await File.WriteAllTextAsync(Path.Combine(_root, "image.png"), "binary placeholder");

        var context = await ProjectContextReader.ReadAsync(_root);

        Assert.Contains("readme.md", context);
        Assert.Contains("project overview", context);
        Assert.DoesNotContain("image.png", context);
    }

    [Fact]
    public async Task Loads_root_and_only_path_applicable_nested_agents_instructions_for_trusted_projects()
    {
        Directory.CreateDirectory(Path.Combine(_root, "src"));
        Directory.CreateDirectory(Path.Combine(_root, "docs"));
        await File.WriteAllTextAsync(Path.Combine(_root, "AGENTS.md"), "root instructions");
        await File.WriteAllTextAsync(Path.Combine(_root, "src", "AGENTS.md"), "source-only instructions");
        await File.WriteAllTextAsync(Path.Combine(_root, "docs", "AGENTS.md"), "unrelated instructions");
        await File.WriteAllTextAsync(Path.Combine(_root, "src", "app.cs"), "class App { }");

        var context = await ProjectContextReader.ReadAsync(_root, ["src/app.cs"], includeProjectInstructions: true);

        Assert.Contains("root instructions", context);
        Assert.Contains("source-only instructions", context);
        Assert.DoesNotContain("unrelated instructions", context);
        Assert.Contains("src/app.cs", context);
    }

    [Fact]
    public async Task Untrusted_project_files_are_not_loaded_as_hidden_instructions_and_exclusions_apply()
    {
        Directory.CreateDirectory(Path.Combine(_root, "src"));
        await File.WriteAllTextAsync(Path.Combine(_root, "AGENTS.md"), "hidden root instructions");
        await File.WriteAllTextAsync(Path.Combine(_root, "src", "AGENTS.md"), "excluded nested instructions");
        await File.WriteAllTextAsync(Path.Combine(_root, "src", "app.cs"), "class App { }");

        var untrustedContext = await ProjectContextReader.ReadAsync(_root, ["src/app.cs"]);
        var trustedButExcluded = await ProjectContextReader.ReadAsync(_root, ["src/app.cs"], ["src/AGENTS.md"], includeProjectInstructions: true);

        Assert.DoesNotContain("hidden root instructions", untrustedContext);
        Assert.DoesNotContain("excluded nested instructions", trustedButExcluded);
        Assert.Contains("hidden root instructions", trustedButExcluded);
    }

    [Fact]
    public async Task Includes_only_matching_path_scoped_project_rules_for_trusted_context()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".codev", "rules"));
        Directory.CreateDirectory(Path.Combine(_root, "src", "ui"));
        Directory.CreateDirectory(Path.Combine(_root, "docs"));
        await File.WriteAllTextAsync(Path.Combine(_root, ".codev", "rules", "javascript.md"),
            "---\ndescription: JavaScript conventions\nglobs: **/*.js, **/*.ts\n---\nUse semicolons.");
        await File.WriteAllTextAsync(Path.Combine(_root, ".codev", "rules", "docs.md"),
            "---\ndescription: Documentation conventions\nglobs: docs/**/*.md\n---\nUse short headings.");
        await File.WriteAllTextAsync(Path.Combine(_root, "src", "ui", "app.ts"), "export const app = true;");
        await File.WriteAllTextAsync(Path.Combine(_root, "docs", "guide.md"), "# Guide");

        var context = await ProjectContextReader.ReadAsync(_root, ["src/ui/app.ts"], includeProjectInstructions: true);
        var untrusted = await ProjectContextReader.ReadAsync(_root, ["src/ui/app.ts"]);

        Assert.Contains("Use semicolons.", context);
        Assert.DoesNotContain("Use short headings.", context);
        Assert.DoesNotContain("Use semicolons.", untrusted);
    }

    [Fact]
    public async Task Path_scoped_rules_respect_context_exclusions()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".codev", "rules"));
        Directory.CreateDirectory(Path.Combine(_root, "src"));
        await File.WriteAllTextAsync(Path.Combine(_root, ".codev", "rules", "source.md"),
            "---\ndescription: Source conventions\nglobs: src/**/*.cs\n---\nUse nullable annotations.");
        await File.WriteAllTextAsync(Path.Combine(_root, "src", "app.cs"), "class App { }");

        var context = await ProjectContextReader.ReadAsync(_root, ["src/app.cs"], [".codev/rules"], includeProjectInstructions: true);

        Assert.DoesNotContain("Use nullable annotations.", context);
    }

    [Fact]
    public async Task Explicit_rule_mention_loads_that_rule_even_when_its_glob_does_not_match_context_files()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".codev", "rules"));
        await File.WriteAllTextAsync(Path.Combine(_root, ".codev", "rules", "javascript.md"),
            "---\ndescription: JavaScript conventions\nglobs: **/*.js\n---\nPrefer const declarations.");
        await File.WriteAllTextAsync(Path.Combine(_root, "readme.md"), "Review JavaScript project conventions.");

        var trusted = await ProjectContextReader.ReadAsync(_root, ["readme.md"], includeProjectInstructions: true, manualRuleNames: ["javascript"]);
        var untrusted = await ProjectContextReader.ReadAsync(_root, ["readme.md"], manualRuleNames: ["javascript"]);

        Assert.Contains("Prefer const declarations.", trusted);
        Assert.DoesNotContain("Prefer const declarations.", untrusted);
    }

    [Fact]
    public async Task Always_rules_load_for_trusted_context_without_matching_files_and_are_not_duplicated_as_sources()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".codev", "rules"));
        await File.WriteAllTextAsync(Path.Combine(_root, ".codev", "rules", "baseline.md"),
            "---\ndescription: Baseline guidance\nactivation: always\n---\nKeep generated files untouched.");
        await File.WriteAllTextAsync(Path.Combine(_root, "readme.md"), "A project overview.");

        var context = await ProjectContextReader.ReadAsync(_root, includeProjectInstructions: true);

        Assert.Contains("Keep generated files untouched.", context);
        var sourceSection = context[context.IndexOf("Selected project files", StringComparison.Ordinal)..];
        Assert.DoesNotContain(".codev/rules/baseline.md", sourceSection);
    }

    [Fact]
    public async Task Instructions_and_source_excerpts_share_the_project_context_character_cap()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "AGENTS.md"), new string('g', ProjectAgentInstructions.MaxCharacters));
        await File.WriteAllTextAsync(Path.Combine(_root, "app.cs"), new string('x', WorkspaceFileService.MaxContextFileCharacters));

        var context = await ProjectContextReader.ReadAsync(_root, includeProjectInstructions: true);

        Assert.True(context.Length <= WorkspaceFileService.MaxContextCharacters, $"Context length was {context.Length} characters.");
        Assert.Contains("Project guidance was truncated", context);
        Assert.Contains("app.cs", context);
    }

    [Fact]
    public async Task Respects_project_exclusions_and_file_excerpt_limit()
    {
        Directory.CreateDirectory(Path.Combine(_root, "private"));
        await File.WriteAllTextAsync(Path.Combine(_root, "private", "notes.md"), "excluded notes");
        await File.WriteAllTextAsync(Path.Combine(_root, "large.txt"), new string('x', WorkspaceFileService.MaxContextFileCharacters + 100));

        var context = await ProjectContextReader.ReadAsync(_root,
            ["private/notes.md", "large.txt"], ["private"]);

        Assert.DoesNotContain("excluded notes", context);
        Assert.Contains("[excerpt truncated]", context);
        Assert.True(context.Length <= WorkspaceFileService.MaxContextCharacters, $"Context length was {context.Length} characters.");
    }

    [Fact]
    public async Task Total_context_stays_within_the_shared_character_budget()
    {
        var selected = new List<string>();
        for (var i = 0; i < WorkspaceFileService.MaxContextFiles; i++)
        {
            var name = $"file-{i:D2}.txt";
            await File.WriteAllTextAsync(Path.Combine(_root, name), new string('x', WorkspaceFileService.MaxContextFileCharacters));
            selected.Add(name);
        }

        var context = await ProjectContextReader.ReadAsync(_root, selected);

        Assert.True(context.Length <= WorkspaceFileService.MaxContextCharacters, $"Context length was {context.Length} characters.");
        Assert.Contains("[excerpt truncated]", context);
    }

    [Fact]
    public async Task Honors_cancellation_before_reading_files()
    {
        var source = Path.Combine(_root, "app.cs");
        await File.WriteAllTextAsync(source, "class App {}");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ProjectContextReader.ReadAsync(_root, ["app.cs"], cancellationToken: cancellation.Token));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { }
    }
}
