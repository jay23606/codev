using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Codev;

public sealed record ProjectEmbeddingChunk(string RelativePath, int Chunk, string Content, string Hash, float[] Embedding);
public sealed record ProjectEmbeddingIndexData(string Model, List<ProjectEmbeddingChunk> Chunks);
public sealed record SemanticSearchResult(string RelativePath, int Chunk, double Score, string Content);

/// <summary>Opt-in, per-project local vector index stored under the caller's Codev data directory.</summary>
public sealed class ProjectEmbeddingIndex
{
    public const int ChunkCharacters = 1800;
    public const int ChunkOverlap = 240;
    public const int MaxIndexedFiles = 8000;
    public const long MaxIndexedBytes = 80L * 1024 * 1024;
    public const int MaxSearchResults = 8;
    public const int MaxStoredChunks = 40_000;
    private const long MaxIndexFileBytes = 256L * 1024 * 1024;
    private const int IndexFormatVersion = 1;
    private static readonly byte[] IndexMagic = Encoding.ASCII.GetBytes("CODEVIDX");
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private readonly WorkspaceFileService files;
    private readonly OllamaEmbeddingClient client;
    private readonly string model;
    private readonly string _indexPath;
    private readonly long _maxIndexedBytes;
    private readonly long _maxIndexFileBytes;

    public ProjectEmbeddingIndex(string dataDirectory, WorkspaceFileService files, OllamaEmbeddingClient client, string model)
        : this(dataDirectory, files, client, model, MaxIndexedBytes, MaxIndexFileBytes)
    {
    }

    internal ProjectEmbeddingIndex(string dataDirectory, WorkspaceFileService files, OllamaEmbeddingClient client, string model,
        long maxIndexedBytes, long maxIndexFileBytes = MaxIndexFileBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        if (maxIndexedBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxIndexedBytes));
        if (maxIndexFileBytes <= 0 || maxIndexFileBytes > MaxIndexFileBytes) throw new ArgumentOutOfRangeException(nameof(maxIndexFileBytes));
        this.files = files;
        this.client = client;
        this.model = model;
        _maxIndexedBytes = maxIndexedBytes;
        _maxIndexFileBytes = maxIndexFileBytes;
        _indexPath = GetIndexPath(dataDirectory, files.Root);
    }

    public static string GetIndexPath(string dataDirectory, string projectPath)
    {
        var root = Path.GetFullPath(projectPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var identity = OperatingSystem.IsWindows() ? root.ToUpperInvariant() : root;
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();
        return Path.Combine(dataDirectory, "Codev", "embeddings", key + ".idx");
    }

    public static bool HasIndex(string dataDirectory, string projectPath)
    {
        var path = GetIndexPath(dataDirectory, projectPath);
        return File.Exists(path) || File.Exists(Path.ChangeExtension(path, ".json"));
    }

    public static void Delete(string dataDirectory, string projectPath)
    {
        var path = GetIndexPath(dataDirectory, projectPath);
        if (File.Exists(path)) File.Delete(path);
        var legacyPath = Path.ChangeExtension(path, ".json");
        if (File.Exists(legacyPath)) File.Delete(legacyPath);
    }

    public async Task<int> UpdateAsync(IProgress<(int Done, int Total)>? progress = null, CancellationToken cancellationToken = default,
        IReadOnlyList<string>? additionalExclusions = null, Func<bool>? canContinue = null)
    {
        EnsureCanContinue(canContinue, cancellationToken);
        var previous = await LoadAsync(cancellationToken).ConfigureAwait(false);
        var oldByKey = previous?.Chunks.GroupBy(Key, StringComparer.OrdinalIgnoreCase).ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase)
            ?? new Dictionary<string, ProjectEmbeddingChunk>(StringComparer.OrdinalIgnoreCase);
        var oldByContent = previous?.Model == model
            ? previous.Chunks.GroupBy(ContentKey, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal)
            : new Dictionary<string, ProjectEmbeddingChunk>(StringComparer.OrdinalIgnoreCase);
        var updated = new List<ProjectEmbeddingChunk>();
        var inputs = new List<(string Path, int Chunk, string Content, string Hash)>();
        long totalBytes = 0;
        var candidates = files.ListContextFiles(MaxIndexedFiles).Where(path => !IsExcluded(path, additionalExclusions)).ToArray();
        if (files.ListContextFiles(MaxIndexedFiles + 1).Count > MaxIndexedFiles)
            throw new InvalidOperationException($"This project exceeds the semantic-index limit of {MaxIndexedFiles:N0} files.");
        foreach (var relative in candidates)
        {
            EnsureCanContinue(canContinue, cancellationToken);
            string content;
            long fileLength;
            try
            {
                var full = files.ResolvePath(relative);
                await using var stream = FileHardLinkInspector.OpenSingleLinkReadStream(full, relative, files.BoundaryRoot);
                if (stream.Length > 500_000) continue;
                fileLength = stream.Length;
                using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                content = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
                if (content.Contains('\0')) continue;
            }
            catch (OperationCanceledException) { throw; }
            catch { continue; }

            totalBytes += fileLength;
            if (totalBytes > _maxIndexedBytes)
                throw new InvalidOperationException($"This project exceeds the semantic-index byte limit of {_maxIndexedBytes:N0} bytes. Narrow context exclusions and retry; the existing index was left unchanged.");

            var ordinal = 0;
            foreach (var chunk in Chunk(content))
            {
                var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(chunk)));
                var key = relative + "\0" + ordinal;
                if (previous?.Model == model && oldByKey.TryGetValue(key, out var cached) && cached.Hash == hash && cached.Content == chunk)
                    updated.Add(cached);
                else if (previous?.Model == model && oldByContent.TryGetValue(relative + "\0" + hash + "\0" + chunk, out var moved))
                    updated.Add(moved with { RelativePath = relative, Chunk = ordinal });
                else inputs.Add((relative, ordinal, chunk, hash));
                ordinal++;
            }
        }

        var total = inputs.Count;
        if (updated.Count + inputs.Count > MaxStoredChunks)
            throw new InvalidOperationException($"This project exceeds the semantic-index limit of {MaxStoredChunks:N0} chunks. Narrow context exclusions and retry.");
        progress?.Report((0, total));
        for (var offset = 0; offset < inputs.Count; offset += 32)
        {
            EnsureCanContinue(canContinue, cancellationToken);
            var batch = inputs.Skip(offset).Take(32).ToArray();
            var vectors = await client.EmbedAsync(batch.Select(item => $"File: {item.Path}\n\n{item.Content}").ToArray(), cancellationToken).ConfigureAwait(false);
            for (var index = 0; index < batch.Length; index++)
                updated.Add(new ProjectEmbeddingChunk(batch[index].Path, batch[index].Chunk, batch[index].Content, batch[index].Hash, vectors[index]));
            progress?.Report((Math.Min(offset + batch.Length, total), total));
        }

        EnsureCanContinue(canContinue, cancellationToken);
        updated = updated.OrderBy(item => item.RelativePath, StringComparer.OrdinalIgnoreCase).ThenBy(item => item.Chunk).ToList();
        var indexData = new ProjectEmbeddingIndexData(model, updated);
        ValidateIndex(indexData);
        var serializedBytes = GetSerializedSize(indexData);
        if (serializedBytes > _maxIndexFileBytes)
            throw new InvalidOperationException($"This semantic index would exceed the {_maxIndexFileBytes / (1024 * 1024)} MB storage limit. Narrow context exclusions and retry; the existing index was left unchanged.");
        await AtomicBinaryFile.WriteAsync(_indexPath, (stream, token) => WriteIndexAsync(stream, indexData, token), cancellationToken).ConfigureAwait(false);
        var legacyPath = Path.ChangeExtension(_indexPath, ".json");
        try { if (File.Exists(legacyPath)) File.Delete(legacyPath); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return updated.Count;
    }

    private static void EnsureCanContinue(Func<bool>? canContinue, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (canContinue is not null && !canContinue())
            throw new UnauthorizedAccessException("Project trust was revoked while building the semantic index. No updated index was saved.");
    }

    public async Task<IReadOnlyList<SemanticSearchResult>> SearchAsync(string query, int limit = MaxSearchResults, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query) || query.Length > 1000) throw new ArgumentException("Search query must contain 1–1,000 characters.", nameof(query));
        var index = await LoadAsync(cancellationToken).ConfigureAwait(false);
        if (index is null || index.Chunks.Count == 0) return [];
        if (!string.Equals(index.Model, model, StringComparison.Ordinal)) throw new InvalidOperationException("The index uses a different embedding model. Rebuild it in Settings before searching.");
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var searchableChunks = new List<ProjectEmbeddingChunk>();
        foreach (var pathGroup in index.Chunks.GroupBy(chunk => chunk.RelativePath, comparer))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = await ReadCurrentChunksAsync(pathGroup.Key, cancellationToken).ConfigureAwait(false);
            if (current is null) continue;
            searchableChunks.AddRange(pathGroup.Where(chunk => current.Contains((chunk.Hash, chunk.Content))));
        }
        if (searchableChunks.Count == 0) return [];
        var queryVector = (await client.EmbedAsync([query], cancellationToken).ConfigureAwait(false))[0];
        var indexedDimensions = searchableChunks[0].Embedding.Length;
        if (queryVector.Length != indexedDimensions)
            throw new InvalidOperationException("The embedding model returned a different vector size than the saved index. Rebuild the index in Settings before searching.");
        return searchableChunks.Select(chunk => new SemanticSearchResult(chunk.RelativePath, chunk.Chunk, Cosine(queryVector, chunk.Embedding), chunk.Content))
            .Where(result => double.IsFinite(result.Score)).OrderByDescending(result => result.Score).ThenBy(result => result.RelativePath, StringComparer.OrdinalIgnoreCase)
            .Take(Math.Clamp(limit, 1, MaxSearchResults)).ToArray();
    }

    public async Task<int> CountAsync(CancellationToken cancellationToken = default) => (await LoadAsync(cancellationToken).ConfigureAwait(false))?.Chunks.Count ?? 0;
    public async Task<string> GetModelAsync(CancellationToken cancellationToken = default) => (await LoadAsync(cancellationToken).ConfigureAwait(false))?.Model ?? model;

    private async Task<ProjectEmbeddingIndexData?> LoadAsync(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            var directory = Path.GetDirectoryName(_indexPath)!;
            if (Directory.Exists(directory))
                File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        var path = File.Exists(_indexPath) ? _indexPath : Path.ChangeExtension(_indexPath, ".json");
        if (!File.Exists(path)) return null;
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        var info = new FileInfo(path);
        if (info.Length > MaxIndexFileBytes) throw new InvalidDataException($"The semantic index is larger than the supported {MaxIndexFileBytes / (1024 * 1024)} MB limit.");

        ProjectEmbeddingIndexData index;
        if (string.Equals(Path.GetExtension(path), ".json", StringComparison.OrdinalIgnoreCase))
        {
            var json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            index = JsonSerializer.Deserialize<ProjectEmbeddingIndexData>(json, JsonOptions)
                ?? throw new InvalidDataException("The legacy semantic index is empty or invalid. Delete and rebuild it in Settings.");
        }
        else
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            index = await ReadIndexAsync(stream, cancellationToken).ConfigureAwait(false);
        }
        ValidateIndex(index);
        return index;
    }

    private static long GetSerializedSize(ProjectEmbeddingIndexData index)
    {
        long size = IndexMagic.Length + sizeof(int) + sizeof(int) + Encoding.UTF8.GetByteCount(index.Model) + sizeof(int);
        checked
        {
            foreach (var chunk in index.Chunks)
            {
                size += sizeof(int) + Encoding.UTF8.GetByteCount(chunk.RelativePath);
                size += sizeof(int); // chunk ordinal
                size += sizeof(int) + Encoding.UTF8.GetByteCount(chunk.Hash);
                size += sizeof(int) + Encoding.UTF8.GetByteCount(chunk.Content);
                size += sizeof(int) + (long)chunk.Embedding.Length * sizeof(float);
            }
        }
        return size;
    }

    private static async Task WriteIndexAsync(Stream stream, ProjectEmbeddingIndexData index, CancellationToken cancellationToken)
    {
        await Task.Run(() =>
        {
            using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
            writer.Write(IndexMagic);
            writer.Write(IndexFormatVersion);
            WriteString(writer, index.Model);
            writer.Write(index.Chunks.Count);
            foreach (var chunk in index.Chunks)
            {
                cancellationToken.ThrowIfCancellationRequested();
                WriteString(writer, chunk.RelativePath);
                writer.Write(chunk.Chunk);
                WriteString(writer, chunk.Hash);
                WriteString(writer, chunk.Content);
                writer.Write(chunk.Embedding.Length);
                foreach (var value in chunk.Embedding) writer.Write(value);
            }
            writer.Flush();
        }, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static Task<ProjectEmbeddingIndexData> ReadIndexAsync(Stream stream, CancellationToken cancellationToken) =>
        Task.Run(() => ReadIndex(stream, cancellationToken), cancellationToken);

    private static ProjectEmbeddingIndexData ReadIndex(Stream stream, CancellationToken cancellationToken)
    {
        using var reader = new BinaryReader(stream, StrictUtf8, leaveOpen: true);
        if (!reader.ReadBytes(IndexMagic.Length).AsSpan().SequenceEqual(IndexMagic))
            throw new InvalidDataException("The semantic index has an unknown file signature. Delete and rebuild it in Settings.");
        if (reader.ReadInt32() != IndexFormatVersion)
            throw new InvalidDataException("The semantic index format version is not supported. Delete and rebuild it in Settings.");
        var model = ReadString(reader, maxBytes: 512);
        var count = reader.ReadInt32();
        if (count is < 0 or > MaxStoredChunks) throw new InvalidDataException("The semantic index contains an invalid chunk count.");
        var chunks = new List<ProjectEmbeddingChunk>(count);
        for (var i = 0; i < count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = ReadString(reader, maxBytes: 960);
            var ordinal = reader.ReadInt32();
            var hash = ReadString(reader, maxBytes: 64);
            var content = ReadString(reader, maxBytes: ChunkCharacters * 4);
            var vectorLength = reader.ReadInt32();
            if (ordinal < 0 || vectorLength is < 1 or > 16_384 ||
                (long)vectorLength * sizeof(float) > reader.BaseStream.Length - reader.BaseStream.Position)
                throw new InvalidDataException("The semantic index contains an invalid embedding vector.");
            var vector = new float[vectorLength];
            for (var dimension = 0; dimension < vector.Length; dimension++) vector[dimension] = reader.ReadSingle();
            chunks.Add(new ProjectEmbeddingChunk(path, ordinal, content, hash, vector));
        }
        if (reader.BaseStream.Position != reader.BaseStream.Length)
            throw new InvalidDataException("The semantic index contains unexpected trailing data.");
        return new ProjectEmbeddingIndexData(model, chunks);
    }

    private static void WriteString(BinaryWriter writer, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        writer.Write(bytes.Length);
        writer.Write(bytes);
    }

    private static string ReadString(BinaryReader reader, int maxBytes)
    {
        var byteCount = reader.ReadInt32();
        if (byteCount < 0 || byteCount > maxBytes || byteCount > reader.BaseStream.Length - reader.BaseStream.Position)
            throw new InvalidDataException("The semantic index contains an invalid text field length.");
        var bytes = reader.ReadBytes(byteCount);
        if (bytes.Length != byteCount) throw new EndOfStreamException("The semantic index ended unexpectedly.");
        return StrictUtf8.GetString(bytes);
    }

    private static void ValidateIndex(ProjectEmbeddingIndexData index)
    {
        if (index.Chunks is null || string.IsNullOrWhiteSpace(index.Model) || index.Model.Length > 512 || Encoding.UTF8.GetByteCount(index.Model) > 512 || index.Chunks.Count > MaxStoredChunks ||
            index.Chunks.Any(chunk => chunk is null || string.IsNullOrWhiteSpace(chunk.RelativePath) || chunk.RelativePath.Length > 240 ||
                Encoding.UTF8.GetByteCount(chunk.RelativePath) > 960 ||
                chunk.Chunk < 0 || chunk.Hash is null || chunk.Hash.Length != 64 || chunk.Content is null || chunk.Content.Length > ChunkCharacters ||
                chunk.Embedding is null || chunk.Embedding.Length is 0 or > 16_384 || chunk.Embedding.Any(value => !float.IsFinite(value))) ||
            (index.Chunks.Count > 1 && index.Chunks.Any(chunk => chunk.Embedding.Length != index.Chunks[0].Embedding.Length)))
            throw new InvalidDataException("The semantic index contains invalid or oversized entries. Delete and rebuild it in Settings.");
    }

    private bool IsStillReadable(string relativePath)
    {
        try { return !files.IsContextExcluded(relativePath) && files.IsSupportedContextFile(relativePath) && File.Exists(files.ResolvePath(relativePath)); }
        catch { return false; }
    }

    private async Task<HashSet<(string Hash, string Content)>?> ReadCurrentChunksAsync(string relativePath, CancellationToken cancellationToken)
    {
        try
        {
            if (!IsStillReadable(relativePath)) return null;
            await using var stream = FileHardLinkInspector.OpenSingleLinkReadStream(files.ResolvePath(relativePath), relativePath, files.BoundaryRoot);
            if (stream.Length > 500_000) return null;
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var content = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            if (content.Contains('\0')) return null;
            return Chunk(content).Select(chunk => (Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(chunk))), chunk)).ToHashSet();
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }

    private static bool IsExcluded(string path, IReadOnlyList<string>? exclusions)
    {
        if (exclusions is null) return false;
        var normalized = path.Replace('\\', '/');
        foreach (var raw in exclusions)
        {
            if (!WorkspaceFileService.IsValidContextExclusion(raw)) continue;
            var rule = raw.Trim().Replace('\\', '/').Trim('/');
            if (rule.Contains('*') || rule.Contains('?'))
            {
                if (!rule.Contains('/') && System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(rule, Path.GetFileName(normalized), ignoreCase: true)) return true;
            }
            else if (rule.Contains('/')
                ? normalized.Equals(rule, StringComparison.OrdinalIgnoreCase) || normalized.StartsWith(rule + "/", StringComparison.OrdinalIgnoreCase)
                : normalized.Split('/').Any(segment => segment.Equals(rule, StringComparison.OrdinalIgnoreCase))) return true;
        }
        return false;
    }

    private static string Key(ProjectEmbeddingChunk chunk) => chunk.RelativePath + "\0" + chunk.Chunk;
    private static string ContentKey(ProjectEmbeddingChunk chunk) => chunk.RelativePath + "\0" + chunk.Hash + "\0" + chunk.Content;
    private static IEnumerable<string> Chunk(string content)
    {
        if (string.IsNullOrWhiteSpace(content)) yield break;
        var start = 0;
        while (start < content.Length)
        {
            var end = Math.Min(content.Length, start + ChunkCharacters);
            if (end < content.Length)
            {
                var boundary = content.LastIndexOf('\n', end - 1, end - start);
                if (boundary > start + ChunkCharacters / 2) end = boundary;
            }
            var value = content[start..end].Trim();
            if (value.Length > 0) yield return value;
            if (end == content.Length) break;
            start = Math.Max(start + 1, end - ChunkOverlap);
        }
    }

    private static double Cosine(float[] left, float[] right)
    {
        if (left.Length != right.Length) return double.NaN;
        double dot = 0, leftNorm = 0, rightNorm = 0;
        for (var i = 0; i < left.Length; i++) { dot += left[i] * right[i]; leftNorm += left[i] * left[i]; rightNorm += right[i] * right[i]; }
        return leftNorm == 0 || rightNorm == 0 ? double.NaN : dot / Math.Sqrt(leftNorm * rightNorm);
    }
}
