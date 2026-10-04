using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Codev.Tests;

public sealed class ProjectEmbeddingIndexTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Codev-embedding-tests", Guid.NewGuid().ToString("N"));
    private readonly string _data = Path.Combine(Path.GetTempPath(), "Codev-embedding-data", Guid.NewGuid().ToString("N"));

    public ProjectEmbeddingIndexTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task Builds_searches_incrementally_respects_exclusions_and_deletes_index()
    {
        Directory.CreateDirectory(Path.Combine(_root, "src"));
        await File.WriteAllTextAsync(Path.Combine(_root, "src", "billing.cs"), "class InvoiceCalculator { decimal CalculateTax(decimal amount) => amount * 0.2m; }");
        await File.WriteAllTextAsync(Path.Combine(_root, "private.md"), "SECRET_UNIQUE_CONTENT");
        var handler = new EmbeddingHandler();
        using var http = new HttpClient(handler);
        var client = new OllamaEmbeddingClient(http, new Uri("http://127.0.0.1:11434"), "test-embed");
        var index = new ProjectEmbeddingIndex(_data, new WorkspaceFileService(_root, ["private.md"]), client, "test-embed");

        var count = await index.UpdateAsync();
        var firstCalls = handler.InputCount;
        var results = await index.SearchAsync("how do we work out sales tax?");
        await index.UpdateAsync();

        Assert.Equal(1, count);
        Assert.Single(results);
        Assert.Equal("src/billing.cs", results[0].RelativePath.Replace('\\', '/'));
        Assert.DoesNotContain("SECRET_UNIQUE_CONTENT", results[0].Content);
        Assert.Equal(firstCalls + 1, handler.InputCount); // second update reuses the existing vector; search embeds only its query
        Assert.True(ProjectEmbeddingIndex.HasIndex(_data, _root));
        ProjectEmbeddingIndex.Delete(_data, _root);
        Assert.False(ProjectEmbeddingIndex.HasIndex(_data, _root));
    }

    [Fact]
    public async Task Index_storage_is_private_to_the_current_user_on_unix()
    {
        if (OperatingSystem.IsWindows()) return;
        await File.WriteAllTextAsync(Path.Combine(_root, "source.cs"), "class PrivateSource { }");
        using var http = new HttpClient(new EmbeddingHandler());
        var index = new ProjectEmbeddingIndex(_data, new WorkspaceFileService(_root),
            new OllamaEmbeddingClient(http, new Uri("http://127.0.0.1:11434"), "test-embed"), "test-embed");

        await index.UpdateAsync();

        var path = ProjectEmbeddingIndex.GetIndexPath(_data, _root);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
            File.GetUnixFileMode(Path.GetDirectoryName(path)!));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
    }

    [Fact]
    public async Task Reembeds_only_the_changed_chunk()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "a.cs"), "class First { }");
        await File.WriteAllTextAsync(Path.Combine(_root, "b.cs"), "class Second { }");
        var handler = new EmbeddingHandler();
        using var http = new HttpClient(handler);
        var index = new ProjectEmbeddingIndex(_data, new WorkspaceFileService(_root), new OllamaEmbeddingClient(http, new Uri("http://127.0.0.1:11434"), "test-embed"), "test-embed");
        await index.UpdateAsync();
        var before = handler.InputCount;
        await File.WriteAllTextAsync(Path.Combine(_root, "a.cs"), "class First { void Changed() { } }");
        await index.UpdateAsync();
        Assert.Equal(before + 1, handler.InputCount);
    }

    [Fact]
    public async Task Search_never_returns_chunks_that_are_stale_for_the_current_project_file()
    {
        const string original = "class OriginalImplementation { }";
        const string changed = "class UpdatedImplementation { }";
        await File.WriteAllTextAsync(Path.Combine(_root, "implementation.cs"), original);
        var handler = new EmbeddingHandler();
        using var http = new HttpClient(handler);
        var index = new ProjectEmbeddingIndex(_data, new WorkspaceFileService(_root),
            new OllamaEmbeddingClient(http, new Uri("http://127.0.0.1:11434"), "test-embed"), "test-embed");
        await index.UpdateAsync();
        await File.WriteAllTextAsync(Path.Combine(_root, "implementation.cs"), changed);

        var staleResults = await index.SearchAsync("find implementation");

        Assert.Empty(staleResults);
        Assert.DoesNotContain(handler.Inputs, input => input == "find implementation");
        await index.UpdateAsync();
        var refreshedResults = await index.SearchAsync("find implementation");
        Assert.Single(refreshedResults);
        Assert.Equal(changed, refreshedResults[0].Content);
        Assert.Contains(handler.Inputs, input => input == "find implementation");
    }

    [Fact]
    public async Task Reembeds_a_moved_chunk_so_its_vector_matches_the_new_path()
    {
        const string content = "class MovedImplementation { void FindMe() { } }";
        await File.WriteAllTextAsync(Path.Combine(_root, "old.cs"), content);
        var handler = new EmbeddingHandler();
        using var http = new HttpClient(handler);
        var index = new ProjectEmbeddingIndex(_data, new WorkspaceFileService(_root),
            new OllamaEmbeddingClient(http, new Uri("http://127.0.0.1:11434"), "test-embed"), "test-embed");

        await index.UpdateAsync();
        var inputsBeforeMove = handler.InputCount;
        File.Delete(Path.Combine(_root, "old.cs"));
        await File.WriteAllTextAsync(Path.Combine(_root, "new.cs"), content);
        await index.UpdateAsync();

        var results = await index.SearchAsync("find implementation");

        Assert.Single(results);
        Assert.Equal("new.cs", results[0].RelativePath);
        Assert.Equal(content, results[0].Content);
        Assert.Equal(inputsBeforeMove + 2, handler.InputCount); // re-embed the new path plus the search query
        Assert.Contains(handler.Inputs, input => input == $"File: new.cs\n\n{content}");
    }

    [Fact]
    public async Task Refuses_to_save_a_partial_index_when_the_project_size_limit_is_exceeded()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "a.cs"), "class A {}");
        var handler = new EmbeddingHandler();
        using var http = new HttpClient(handler);
        var client = new OllamaEmbeddingClient(http, new Uri("http://127.0.0.1:11434"), "test-embed");
        var originalIndex = new ProjectEmbeddingIndex(_data, new WorkspaceFileService(_root), client, "test-embed");
        await originalIndex.UpdateAsync();
        var indexPath = ProjectEmbeddingIndex.GetIndexPath(_data, _root);
        var originalContents = await File.ReadAllBytesAsync(indexPath);
        await File.WriteAllTextAsync(Path.Combine(_root, "b.cs"), "class B {}");

        var constrainedIndex = new ProjectEmbeddingIndex(_data, new WorkspaceFileService(_root),
            client, "test-embed", maxIndexedBytes: 12);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => constrainedIndex.UpdateAsync());

        Assert.Contains("byte limit", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("existing index was left unchanged", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(originalContents, await File.ReadAllBytesAsync(indexPath));
        Assert.Equal(1, handler.InputCount);
    }

    [Fact]
    public async Task Does_not_create_an_index_when_the_first_update_exceeds_the_project_size_limit()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "a.cs"), "class A {}");
        await File.WriteAllTextAsync(Path.Combine(_root, "b.cs"), "class B {}");
        var handler = new EmbeddingHandler();
        using var http = new HttpClient(handler);
        var client = new OllamaEmbeddingClient(http, new Uri("http://127.0.0.1:11434"), "test-embed");
        var index = new ProjectEmbeddingIndex(_data, new WorkspaceFileService(_root), client, "test-embed", maxIndexedBytes: 12);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => index.UpdateAsync());

        Assert.Contains("byte limit", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(ProjectEmbeddingIndex.HasIndex(_data, _root));
        Assert.Equal(0, handler.InputCount);
    }

    [Fact]
    public async Task Stops_without_saving_when_project_trust_is_revoked()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "a.cs"), "class First { }");
        var handler = new EmbeddingHandler();
        using var http = new HttpClient(handler);
        var index = new ProjectEmbeddingIndex(_data, new WorkspaceFileService(_root), new OllamaEmbeddingClient(http, new Uri("http://127.0.0.1:11434"), "test-embed"), "test-embed");
        var trusted = false;

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => index.UpdateAsync(canContinue: () => trusted));
        Assert.False(ProjectEmbeddingIndex.HasIndex(_data, _root));
        Assert.Equal(0, handler.InputCount);
    }

    [Fact]
    public async Task Removes_deleted_and_excluded_files_and_reuses_vectors_when_chunks_shift()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "a.cs"), "class Alpha { }\nclass Beta { }\nclass Gamma { }");
        await File.WriteAllTextAsync(Path.Combine(_root, "b.cs"), "class KeepMe { }");
        var handler = new EmbeddingHandler();
        using var http = new HttpClient(handler);
        var index = new ProjectEmbeddingIndex(_data, new WorkspaceFileService(_root), new OllamaEmbeddingClient(http, new Uri("http://127.0.0.1:11434"), "test-embed"), "test-embed");
        await index.UpdateAsync();
        var before = handler.InputCount;

        await File.WriteAllTextAsync(Path.Combine(_root, "a.cs"), "class NewAtStart { }\nclass Alpha { }\nclass Beta { }\nclass Gamma { }");
        File.Delete(Path.Combine(_root, "b.cs"));
        await index.UpdateAsync(additionalExclusions: ["a.cs"]);
        var excludedResults = await index.SearchAsync("find Alpha");

        Assert.Empty(excludedResults);
        Assert.Equal(before, handler.InputCount); // excluded files were not embedded and an empty index needs no query embedding
        await index.UpdateAsync();
        Assert.Equal(before + 1, handler.InputCount); // shifted unchanged content reuses its vector
        File.Delete(Path.Combine(_root, "a.cs"));
        await index.UpdateAsync();
        Assert.Equal(0, await index.CountAsync()); // deleted files are removed from the index
    }

    [Fact]
    public async Task Shared_project_index_applies_context_exclusions_per_searching_turn()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "a.cs"), "class AlphaSecret { }");
        await File.WriteAllTextAsync(Path.Combine(_root, "b.cs"), "class BetaVisible { }");
        var handler = new EmbeddingHandler();
        using var http = new HttpClient(handler);
        var client = new OllamaEmbeddingClient(http, new Uri("http://127.0.0.1:11434"), "test-embed");
        var projectIndex = new ProjectEmbeddingIndex(_data, new WorkspaceFileService(_root), client, "test-embed");
        await projectIndex.UpdateAsync();

        var unrestrictedResults = await projectIndex.SearchAsync("find relevant source");
        var restrictedIndex = new ProjectEmbeddingIndex(_data, new WorkspaceFileService(_root, ["a.cs"]), client, "test-embed");
        var restrictedResults = await restrictedIndex.SearchAsync("find relevant source");

        Assert.Equal(2, unrestrictedResults.Count);
        Assert.Contains(unrestrictedResults, result => result.RelativePath == "a.cs");
        Assert.Contains(unrestrictedResults, result => result.RelativePath == "b.cs");
        Assert.DoesNotContain(restrictedResults, result => result.RelativePath == "a.cs");
        Assert.Contains(restrictedResults, result => result.RelativePath == "b.cs");
    }

    [Fact]
    public async Task Rejects_a_corrupt_or_oversized_index_before_deserializing_it()
    {
        var path = ProjectEmbeddingIndex.GetIndexPath(_data, _root);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, "not json");
        using var http = new HttpClient(new EmbeddingHandler());
        var index = new ProjectEmbeddingIndex(_data, new WorkspaceFileService(_root), new OllamaEmbeddingClient(http, new Uri("http://127.0.0.1:11434"), "test-embed"), "test-embed");
        await Assert.ThrowsAsync<InvalidDataException>(() => index.CountAsync());
    }

    [Fact]
    public async Task Large_embedding_vectors_use_compact_versioned_binary_storage()
    {
        for (var i = 0; i < 128; i++)
            await File.WriteAllTextAsync(Path.Combine(_root, $"file-{i:D3}.cs"), $"class File{i} {{ // {new string('x', 1600)} }}");
        var handler = new EmbeddingHandler(vectorSize: 768);
        using var http = new HttpClient(handler);
        var index = new ProjectEmbeddingIndex(_data, new WorkspaceFileService(_root),
            new OllamaEmbeddingClient(http, new Uri("http://127.0.0.1:11434"), "nomic-embed-text"), "nomic-embed-text");

        Assert.Equal(128, await index.UpdateAsync());

        var path = ProjectEmbeddingIndex.GetIndexPath(_data, _root);
        var bytes = await File.ReadAllBytesAsync(path);
        Assert.Equal("CODEVIDX", Encoding.ASCII.GetString(bytes, 0, 8));
        Assert.True(bytes.Length < 800_000, $"Expected compact storage for 128 768-dimensional vectors, got {bytes.Length:N0} bytes.");
        Assert.Equal(128, await index.CountAsync());
        Assert.Equal(8, (await index.SearchAsync("find a source file")).Count);
    }

    [Fact]
    public async Task Migrates_a_legacy_json_index_to_binary_after_a_successful_update()
    {
        const string content = "class Existing { }";
        await File.WriteAllTextAsync(Path.Combine(_root, "existing.cs"), content);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
        var legacyIndexPath = Path.ChangeExtension(ProjectEmbeddingIndex.GetIndexPath(_data, _root), ".json");
        Directory.CreateDirectory(Path.GetDirectoryName(legacyIndexPath)!);
        await File.WriteAllTextAsync(legacyIndexPath, JsonSerializer.Serialize(new ProjectEmbeddingIndexData("test-embed",
            [new ProjectEmbeddingChunk("existing.cs", 0, content, hash, [1, 0, 0])])));
        var handler = new EmbeddingHandler();
        using var http = new HttpClient(handler);
        var index = new ProjectEmbeddingIndex(_data, new WorkspaceFileService(_root),
            new OllamaEmbeddingClient(http, new Uri("http://127.0.0.1:11434"), "test-embed"), "test-embed");

        Assert.Equal(1, await index.CountAsync());
        Assert.Equal(1, await index.UpdateAsync());

        Assert.True(File.Exists(ProjectEmbeddingIndex.GetIndexPath(_data, _root)));
        Assert.False(File.Exists(legacyIndexPath));
        Assert.Equal(0, handler.InputCount);
    }

    [Fact]
    public async Task Oversized_binary_index_update_preserves_the_previous_index()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "a.cs"), "class A {} ");
        var handler = new EmbeddingHandler(vectorSize: 768);
        using var http = new HttpClient(handler);
        var client = new OllamaEmbeddingClient(http, new Uri("http://127.0.0.1:11434"), "test-embed");
        var index = new ProjectEmbeddingIndex(_data, new WorkspaceFileService(_root), client, "test-embed",
            ProjectEmbeddingIndex.MaxIndexedBytes, maxIndexFileBytes: 50_000);
        await index.UpdateAsync();
        var path = ProjectEmbeddingIndex.GetIndexPath(_data, _root);
        var original = await File.ReadAllBytesAsync(path);
        await File.WriteAllTextAsync(Path.Combine(_root, "a.cs"), new string('x', 50_000));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => index.UpdateAsync());

        Assert.Contains("storage limit", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("existing index was left unchanged", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(original, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task Refuses_to_save_an_index_when_embedding_dimensions_change_between_batches()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "seed.cs"), "class Seed {} ");
        var handler = new EmbeddingHandler(vectorSizeForInput: input => input.Contains("file-032.cs", StringComparison.Ordinal) ? 4 : 3);
        using var http = new HttpClient(handler);
        var index = new ProjectEmbeddingIndex(_data, new WorkspaceFileService(_root),
            new OllamaEmbeddingClient(http, new Uri("http://127.0.0.1:11434"), "test-embed"), "test-embed");
        await index.UpdateAsync();
        var path = ProjectEmbeddingIndex.GetIndexPath(_data, _root);
        var original = await File.ReadAllBytesAsync(path);
        for (var i = 0; i < 33; i++) await File.WriteAllTextAsync(Path.Combine(_root, $"file-{i:D3}.cs"), $"class File{i} {{ }}");

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => index.UpdateAsync());

        Assert.Contains("invalid or oversized entries", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(original, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public void Semantic_tool_is_absent_by_default_and_opt_in_for_both_providers()
    {
        var shell = ShellCommandResolver.ResolveCurrent();
        Assert.DoesNotContain("semantic_search", Names(CodeTaskToolSchemaFactory.CreateOllamaTools(shell)));
        Assert.Contains("semantic_search", Names(CodeTaskToolSchemaFactory.CreateOllamaTools(shell, allowSemanticSearch: true)));
        Assert.Contains("semantic_search", Names(CodeTaskToolSchemaFactory.CreateOpenAiStrictTools(shell, allowSemanticSearch: true)));
    }

    [Fact]
    public async Task Ollama_embedding_client_uses_local_embed_endpoint_and_validates_vector_count()
    {
        var handler = new EmbeddingHandler();
        using var http = new HttpClient(handler);
        var client = new OllamaEmbeddingClient(http, new Uri("http://127.0.0.1:11434"), "nomic-embed-text");
        var vectors = await client.EmbedAsync(["hello", "world"]);
        Assert.Equal(2, vectors.Count);
        Assert.Equal("/api/embed", handler.LastRequest!.RequestUri!.AbsolutePath);
        Assert.Equal(2, handler.LastBatchSize);
    }

    [Fact]
    public void Index_path_is_stable_and_does_not_embed_the_raw_project_path()
    {
        var first = ProjectEmbeddingIndex.GetIndexPath(_data, _root);
        var same = ProjectEmbeddingIndex.GetIndexPath(_data, Path.Combine(_root, "."));
        Assert.Equal(first, same);
        Assert.DoesNotContain(_root, first, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(Path.Combine("Codev", "embeddings"), first);
    }

    private static string[] Names(object[] tools)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(tools, JsonSerializerOptions.Web));
        return document.RootElement.EnumerateArray().Select(tool => tool.TryGetProperty("function", out var function)
            ? function.GetProperty("name").GetString()! : tool.GetProperty("name").GetString()!).ToArray();
    }

    private sealed class EmbeddingHandler(int vectorSize = 3, Func<string, int>? vectorSizeForInput = null) : HttpMessageHandler
    {
        public int InputCount { get; private set; }
        public int LastBatchSize { get; private set; }
        public HttpRequestMessage? LastRequest { get; private set; }
        public List<string> Inputs { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            var input = body.RootElement.GetProperty("input");
            LastBatchSize = input.ValueKind == JsonValueKind.Array ? input.GetArrayLength() : 1;
            InputCount += LastBatchSize;
            if (input.ValueKind == JsonValueKind.Array)
                Inputs.AddRange(input.EnumerateArray().Select(value => value.GetString() ?? ""));
            else Inputs.Add(input.GetString() ?? "");
            var inputValues = input.ValueKind == JsonValueKind.Array
                ? input.EnumerateArray().Select(value => value.GetString() ?? "").ToArray()
                : [input.GetString() ?? ""];
            var embeddings = Enumerable.Range(0, LastBatchSize).Select(index => Enumerable.Range(0, vectorSizeForInput?.Invoke(inputValues[index]) ?? vectorSize)
                .Select(dimension => dimension == 0 ? 1.0 : Math.Sin(dimension) * 0.1).ToArray());
            var response = JsonSerializer.Serialize(new { embeddings });
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(response) };
        }
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
        try { Directory.Delete(_data, recursive: true); } catch { }
    }
}
