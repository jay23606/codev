using System.Text;
using System.Text.RegularExpressions;

namespace Codev;

/// <summary>Creates an opt-in, bounded tree and top-level symbol outline using only safe project files.</summary>
public static partial class RepoMapBuilder
{
    public const int MaxFiles = 160;
    public const int MaxCharacters = 8_000;
    public const int MaxSymbolsPerFile = 12;
    private const long MaxSourceFileBytes = 128_000;

    [GeneratedRegex(@"\b(?:class|record|interface|enum|struct)\s+([A-Za-z_]\w*)|\b(?:function|class|interface|type|enum)\s+([A-Za-z_$][\w$]*)|^\s*(?:async\s+)?def\s+([A-Za-z_]\w*)", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex DeclarationRegex();

    [GeneratedRegex(@"\b(?:public|internal|protected)\s+(?:static\s+)?(?:async\s+)?[\w<>,?.\[\]]+\s+([A-Za-z_]\w*)\s*\(", RegexOptions.CultureInvariant)]
    private static partial Regex CSharpMemberRegex();

    [GeneratedRegex(@"\bexport\s+(?:default\s+)?(?:async\s+)?(?:const|let|var)\s+([A-Za-z_$][\w$]*)", RegexOptions.CultureInvariant)]
    private static partial Regex ExportedValueRegex();

    public static async Task<string> BuildAsync(string projectPath, IReadOnlyList<string>? selectedFiles = null,
        IReadOnlyList<string>? contextExclusions = null, CancellationToken cancellationToken = default)
    {
        var service = new WorkspaceFileService(projectPath, contextExclusions);
        var candidates = selectedFiles is { Count: > 0 } ? selectedFiles : service.ListContextFiles(maxEntries: 300);
        var files = candidates.Where(path => !service.IsContextExcluded(path))
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(MaxFiles).ToArray();
        var output = new StringBuilder("Project map (bounded file outline; ask before assuming omitted files do not exist):\n");
        var mapped = 0;

        foreach (var relative in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var fullPath = service.ResolvePath(relative);
                if (new FileInfo(fullPath).Length > MaxSourceFileBytes) continue;
                var content = await service.ReadFileAsync(relative, cancellationToken).ConfigureAwait(false);
                var symbols = ExtractSymbols(relative, content);
                var displayPath = relative.Replace('\\', '/');
                var line = symbols.Count == 0 ? $"- {displayPath}" : $"- {displayPath}: {string.Join(", ", symbols)}";
                if (output.Length + line.Length + 1 > MaxCharacters)
                {
                    const string marker = "… [map truncated at the context limit]";
                    if (output.Length + marker.Length + 1 <= MaxCharacters) output.AppendLine(marker);
                    break;
                }
                output.AppendLine(line);
                mapped++;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or NotSupportedException)
            {
                // A file can disappear, be excluded, or become unsafe after the candidate list is made.
            }
        }

        if (mapped == 0) output.AppendLine("No supported project files were available for the map.");
        if (candidates.Count > files.Length)
        {
            var footer = $"… [showing at most {files.Length} of {candidates.Count} supported files]";
            if (output.Length + footer.Length + 1 <= MaxCharacters) output.AppendLine(footer);
        }
        return output.ToString();
    }

    private static IReadOnlyList<string> ExtractSymbols(string relativePath, string content)
    {
        var extension = Path.GetExtension(relativePath);
        var matches = new List<string>();
        void AddMatches(Regex regex)
        {
            foreach (Match match in regex.Matches(content))
            {
                var name = match.Groups.Cast<Group>().Skip(1).FirstOrDefault(group => group.Success && group.Length > 0)?.Value;
                if (!string.IsNullOrEmpty(name) && !matches.Contains(name, StringComparer.Ordinal)) matches.Add(name);
                if (matches.Count >= MaxSymbolsPerFile) break;
            }
        }

        AddMatches(DeclarationRegex());
        if (extension.Equals(".cs", StringComparison.OrdinalIgnoreCase)) AddMatches(CSharpMemberRegex());
        if (extension is ".js" or ".jsx" or ".ts" or ".tsx") AddMatches(ExportedValueRegex());
        return matches.Take(MaxSymbolsPerFile).ToArray();
    }
}
