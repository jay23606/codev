namespace Codev;

public static class OllamaModelDisplayName
{
    public static string Format(string modelName)
    {
        var name = modelName.Trim();
        if (name.StartsWith("hf.co/", StringComparison.OrdinalIgnoreCase))
            name = name[(name.LastIndexOf('/') + 1)..];
        if (name.EndsWith(":latest", StringComparison.OrdinalIgnoreCase)) name = name[..^7];
        var parts = name.Split(':', 2);
        var model = parts[0].Replace("-GGUF", "", StringComparison.OrdinalIgnoreCase).Replace('-', ' ').Replace('_', ' ').Trim();
        if (model.Length > 0) model = char.ToUpperInvariant(model[0]) + model[1..];
        if (parts.Length == 1 || string.IsNullOrWhiteSpace(parts[1])) return model;
        return $"{model} · {parts[1].ToUpperInvariant()}";
    }
}
