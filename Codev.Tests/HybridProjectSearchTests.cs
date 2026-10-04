using System.Text.Json;

namespace Codev.Tests;

public sealed class HybridProjectSearchTests
{
    [Fact]
    public async Task Hybrid_literal_candidates_reserve_room_for_later_files()
    {
        var root = Path.Combine(Path.GetTempPath(), "Codev-hybrid-search-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await File.WriteAllLinesAsync(Path.Combine(root, "a-repeated.cs"), Enumerable.Repeat("needle", 100));
            await File.WriteAllTextAsync(Path.Combine(root, "b-later.cs"), "needle");

            var matches = await new WorkspaceFileService(root).SearchFileMatchesAsync("needle");

            Assert.Equal(3, matches.Count(match => match.RelativePath == "a-repeated.cs"));
            Assert.Contains(matches, match => match.RelativePath == "b-later.cs");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Semantic_match_survives_a_full_literal_candidate_list()
    {
        var literal = Enumerable.Range(1, 50)
            .Select(index => new FileSearchMatch($"literal-{index:D2}.cs", 1, "needle"))
            .ToArray();
        var semantic = new[] { new SemanticSearchResult("concept.cs", 0, 0.8, "related implementation") };

        var results = HybridProjectSearch.Fuse(literal, semantic);

        Assert.Equal(HybridProjectSearch.ResultLimit, results.Count);
        Assert.Equal("concept.cs", results[0].RelativePath);
        Assert.NotNull(results[0].SemanticMatch);
    }

    [Fact]
    public void One_file_contributes_one_rank_per_source_and_combines_evidence()
    {
        var literal = new[]
        {
            new FileSearchMatch("shared.cs", 2, "needle first"),
            new FileSearchMatch("shared.cs", 5, "needle second"),
            new FileSearchMatch("literal-only.cs", 1, "needle")
        };
        var semantic = new[]
        {
            new SemanticSearchResult("shared.cs", 3, 0.9, "shared semantic chunk"),
            new SemanticSearchResult("shared.cs", 4, 0.8, "duplicate file should not consume a rank"),
            new SemanticSearchResult("semantic-only.cs", 1, 0.7, "another semantic chunk")
        };

        var results = HybridProjectSearch.Fuse(literal, semantic);

        var shared = Assert.Single(results, result => result.RelativePath == "shared.cs");
        Assert.Equal("needle first", shared.LiteralMatch!.LineText);
        Assert.Equal("shared semantic chunk", shared.SemanticMatch!.Content);
        Assert.Equal(1d / 61 + 1d / 61, shared.FusedScore, precision: 12);
        Assert.Contains(results, result => result.RelativePath == "literal-only.cs");
        Assert.Contains(results, result => result.RelativePath == "semantic-only.cs");
    }

    [Fact]
    public async Task Executor_returns_hybrid_results_and_falls_back_to_literal_when_embeddings_fail()
    {
        var root = Path.Combine(Path.GetTempPath(), "Codev-hybrid-search-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            for (var index = 0; index < 20; index++)
                await File.WriteAllTextAsync(Path.Combine(root, $"literal-{index:D2}.cs"), "needle");
            await File.WriteAllTextAsync(Path.Combine(root, "concept.cs"), "related implementation");

            var executor = new CodeTaskToolExecutor(new WorkspaceFileService(root), new Conversation(),
                _ => Task.FromResult(true), _ => Task.FromResult(true),
                semanticSearch: (_, _) => Task.FromResult<IReadOnlyList<SemanticSearchResult>>(
                    [new SemanticSearchResult("concept.cs", 0, 0.8, "related implementation") ]));
            var hybrid = await ExecuteAsync(executor, "search_files", """{"query":"needle"}""");
            using (var document = JsonDocument.Parse(hybrid))
            {
                var envelope = document.RootElement;
                Assert.Equal("hybrid project search results", envelope.GetProperty("source").GetString());
                Assert.Contains("concept.cs", envelope.GetProperty("content").GetString(), StringComparison.Ordinal);
                Assert.Contains("semantic match", envelope.GetProperty("content").GetString(), StringComparison.Ordinal);
            }

            var fallbackExecutor = new CodeTaskToolExecutor(new WorkspaceFileService(root), new Conversation(),
                _ => Task.FromResult(true), _ => Task.FromResult(true),
                semanticSearch: (_, _) => throw new HttpRequestException("embedding service offline"));
            var fallback = await ExecuteAsync(fallbackExecutor, "search_files", """{"query":"needle"}""");
            using var fallbackDocument = JsonDocument.Parse(fallback);
            var fallbackContent = fallbackDocument.RootElement.GetProperty("content").GetString()!;
            Assert.Contains("literal matches only", fallbackContent, StringComparison.Ordinal);
            Assert.Contains("literal-00.cs", fallbackContent, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static async Task<string> ExecuteAsync(CodeTaskToolExecutor executor, string name, string json)
    {
        using var document = JsonDocument.Parse(json);
        return await executor.ExecuteAsync(name, document.RootElement);
    }
}
