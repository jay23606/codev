namespace Codev;

public static class OllamaContextSizes
{
    private static readonly int[] Choices = [0, 8192, 16384, 24576, 32768, 49152, 65536];

    public static int MaximumFor(string modelName) =>
        modelName.Contains("qwen3-coder-next", StringComparison.OrdinalIgnoreCase) ||
        modelName.Contains("Qwen_Qwen3-Coder-Next", StringComparison.OrdinalIgnoreCase)
            ? 24576
            : 65536;

    public static IReadOnlyList<int> ForModel(string modelName) =>
        Choices.Where(value => value == 0 || value <= MaximumFor(modelName)).ToArray();
}
