namespace Codev;

/// <summary>Parses and validates one-shot headless Code task options without launching a provider request.</summary>
public sealed record HeadlessCodeTaskOptions(string Provider, string Model, Uri Endpoint, string WorkspacePath,
    string Prompt, bool AllowEdits, bool AllowCommands, bool AllowHostedData)
{
    public const string HelpText = """
        Usage: codev -p <prompt> [options]
               echo <prompt> | codev [options]

        Options:
          -p, --print <prompt>   Run one headless Code task (or read the prompt from stdin)
          --provider <name>      ollama (default) or openai
          --model <name>         Model name (or CODEV_MODEL)
          --endpoint <url>       Ollama base URL (default http://127.0.0.1:11434/)
          --allow-remote-endpoint Allow a non-loopback Ollama endpoint to receive project content
          --workspace <path>     Project folder (default current directory)
          --allow-hosted-data    Allow OpenAI to receive the prompt and project tool results
          --allow-edits          Let the model create and edit files for this run
          --allow-commands       Let the model run shell commands unsandboxed for this run
          --allow                Enable both file edits and shell commands
          -h, --help             Show this help

        OpenAI uses the API key saved in Codev's operating-system credential store.
        """;

    public static bool TryParse(IReadOnlyList<string> args, string? defaultProvider, string? defaultModel,
        string defaultWorkspace, out HeadlessCodeTaskOptions options, out string error)
    {
        ArgumentNullException.ThrowIfNull(args);
        string? prompt = null;
        string? provider = null;
        string? model = null;
        string? endpointText = null;
        string? workspace = null;
        var allowEdits = false;
        var allowCommands = false;
        var allowRemoteEndpoint = false;
        var allowHostedData = false;
        error = "";
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (arg is "-p" or "--print")
            {
                if (++i >= args.Count) { error = $"{arg} requires a prompt value."; break; }
                prompt = args[i];
            }
            else if (arg == "--provider") provider = ReadValue(args, ref i, arg, ref error);
            else if (arg == "--model") model = ReadValue(args, ref i, arg, ref error);
            else if (arg == "--endpoint") endpointText = ReadValue(args, ref i, arg, ref error);
            else if (arg == "--workspace") workspace = ReadValue(args, ref i, arg, ref error);
            else if (arg == "--allow-edits") allowEdits = true;
            else if (arg == "--allow-commands") allowCommands = true;
            else if (arg == "--allow-remote-endpoint") allowRemoteEndpoint = true;
            else if (arg == "--allow-hosted-data") allowHostedData = true;
            else if (arg == "--allow") allowEdits = allowCommands = true;
            else { error = $"Unknown option: {arg}"; break; }
            if (error.Length > 0) break;
        }

        provider = (provider ?? defaultProvider ?? "ollama").Trim().ToLowerInvariant();
        model = (model ?? defaultModel ?? "").Trim();
        string workspacePath;
        try { workspacePath = Path.GetFullPath(workspace ?? defaultWorkspace); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            error = $"Workspace path is invalid: {ex.Message}";
            workspacePath = Path.GetFullPath(defaultWorkspace);
        }
        var endpoint = OllamaEndpoint.Default;
        if (provider is not ("ollama" or "openai")) error = "Provider must be 'ollama' or 'openai'.";
        else if (model.Length == 0) error = "Specify --model or set CODEV_MODEL.";
        else if (!Directory.Exists(workspacePath)) error = $"Workspace folder not found: {workspacePath}";
        else if (provider == "ollama" && endpointText is not null && !OllamaEndpoint.TryParse(endpointText, out endpoint, out error)) { }
        else if (provider == "openai" && endpointText is not null) error = "--endpoint is supported only with --provider ollama.";
        else if (provider == "openai" && !allowHostedData) error = "OpenAI sends prompts and project tool results to a hosted service; pass --allow-hosted-data to consent for this run.";
        else if (provider == "ollama" && !OllamaEndpoint.IsLoopback(endpoint) && !allowRemoteEndpoint)
            error = "This Ollama endpoint is not loopback and may receive project content; pass --allow-remote-endpoint to consent for this run.";
        options = new HeadlessCodeTaskOptions(provider, model, endpoint, workspacePath, prompt ?? "", allowEdits, allowCommands, allowHostedData);
        return error.Length == 0;
    }

    private static string? ReadValue(IReadOnlyList<string> args, ref int index, string option, ref string error)
    {
        if (++index >= args.Count || args[index].StartsWith("-", StringComparison.Ordinal))
        {
            error = $"{option} requires a value.";
            return null;
        }
        return args[index];
    }
}
