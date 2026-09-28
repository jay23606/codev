namespace Codev;

public static class ConversationSamplingSettings
{
    public const double MinTemperature = 0;
    public const double MaxTemperature = 2;
    public const double MinProbability = 0;
    public const double MaxProbability = 1;
    public const double MinPenalty = 0;
    public const double MaxPenalty = 2;
    public const int MinTopK = 1;
    public const int MaxTopK = 1000;
    public const int MinOutputTokens = 1;
    public const int MaxOutputTokens = 131072;

    public static double? NormalizeTemperature(double? value) =>
        Normalize(value, MinTemperature, MaxTemperature);

    public static double? NormalizeProbability(double? value) => Normalize(value, MinProbability, MaxProbability);

    public static double? NormalizePenalty(double? value) => Normalize(value, MinPenalty, MaxPenalty);

    public static int? NormalizeTopK(int? value) => value is >= MinTopK and <= MaxTopK ? value : null;

    public static int? NormalizeOutputTokens(int? value) => value is >= MinOutputTokens and <= MaxOutputTokens ? value : null;

    public static double? Normalize(double? temperature) => NormalizeTemperature(temperature);

    private static double? Normalize(double? value, double min, double max) =>
        value is double finite && double.IsFinite(finite) && finite >= min && finite <= max
            ? Math.Round(finite, 2)
            : null;
}
