namespace Codev;

public static class ConversationSamplingSettings
{
    public const double MinTemperature = 0;
    public const double MaxTemperature = 2;

    public static double? Normalize(double? temperature) =>
        temperature is double value && double.IsFinite(value) && value is >= MinTemperature and <= MaxTemperature
            ? Math.Round(value, 2)
            : null;
}
