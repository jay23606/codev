namespace Codev;

internal sealed record HybridProjectSearchResult(string RelativePath, FileSearchMatch? LiteralMatch,
    SemanticSearchResult? SemanticMatch, double FusedScore);

/// <summary>Fuses bounded literal and semantic project search by file, without letting one result list crowd out the other.</summary>
internal static class HybridProjectSearch
{
    public const int ResultLimit = 8;
    private const int ReciprocalRankConstant = 60;

    public static IReadOnlyList<HybridProjectSearchResult> Fuse(IReadOnlyList<FileSearchMatch> literal,
        IReadOnlyList<SemanticSearchResult> semantic, int limit = ResultLimit)
    {
        ArgumentNullException.ThrowIfNull(literal);
        ArgumentNullException.ThrowIfNull(semantic);
        if (limit is < 1 or > ResultLimit) throw new ArgumentOutOfRangeException(nameof(limit));

        var pathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var entries = new Dictionary<string, MutableResult>(pathComparer);
        AddLiteralResults();
        AddSemanticResults();

        return entries.Values
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.SemanticRank)
            .ThenBy(item => item.LiteralRank)
            .ThenBy(item => item.RelativePath, StringComparer.OrdinalIgnoreCase)
            .Take(limit)
            .Select(item => new HybridProjectSearchResult(item.RelativePath, item.LiteralMatch, item.SemanticMatch, item.Score))
            .ToArray();

        void AddLiteralResults()
        {
            var rank = 0;
            foreach (var match in literal)
            {
                if (!entries.TryGetValue(match.RelativePath, out var entry))
                    entries.Add(match.RelativePath, entry = new MutableResult(match.RelativePath));
                if (entry.LiteralMatch is not null) continue;
                rank++;
                entry.LiteralRank = rank;
                entry.LiteralMatch = match;
                entry.Score += 1d / (ReciprocalRankConstant + rank);
            }
        }

        void AddSemanticResults()
        {
            var rank = 0;
            foreach (var match in semantic)
            {
                if (!double.IsFinite(match.Score)) continue;
                if (!entries.TryGetValue(match.RelativePath, out var entry))
                    entries.Add(match.RelativePath, entry = new MutableResult(match.RelativePath));
                if (entry.SemanticMatch is not null) continue;
                rank++;
                entry.SemanticRank = rank;
                entry.SemanticMatch = match;
                entry.Score += 1d / (ReciprocalRankConstant + rank);
            }
        }
    }

    private sealed class MutableResult(string relativePath)
    {
        public string RelativePath { get; } = relativePath;
        public FileSearchMatch? LiteralMatch { get; set; }
        public SemanticSearchResult? SemanticMatch { get; set; }
        public int LiteralRank { get; set; } = int.MaxValue;
        public int SemanticRank { get; set; } = int.MaxValue;
        public double Score { get; set; }
    }
}
