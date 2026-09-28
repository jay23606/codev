using System.Text;
using System.Text.RegularExpressions;

namespace Codev;

public sealed record SlashCommandFileLoadResult(IReadOnlyList<SlashCommandDefinition> Commands, IReadOnlyList<string> Warnings);
public sealed record SlashCommandExpansionResult(bool Success, string Prompt, string Error);

/// <summary>Loads user-authored Markdown prompts. These files are data only: Codev never runs scripts from them.</summary>
public static partial class CustomSlashCommandService
{
    public const int MaxCommandsPerScope = 64;
    public const int MaxCommandFileBytes = 16 * 1024;
    public const int MaxPromptCharacters = 12_000;
    public const int MaxArguments = 8;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    [GeneratedRegex(@"\{\{\s*([A-Za-z][A-Za-z0-9_]*)\s*\}\}", RegexOptions.CultureInvariant)]
    private static partial Regex PlaceholderRegex();

    [GeneratedRegex(@"^[a-z][a-z0-9_-]{0,39}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NameRegex();

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9_]{0,39}$", RegexOptions.CultureInvariant)]
    private static partial Regex ArgumentNameRegex();

    public static async Task<SlashCommandFileLoadResult> LoadAsync(
        string userCommandsDirectory,
        string? projectRoot,
        bool includeProjectCommands,
        CancellationToken cancellationToken = default)
    {
        var warnings = new List<string>();
        var user = await LoadScopeAsync(userCommandsDirectory, "user", warnings, cancellationToken).ConfigureAwait(false);
        var project = new List<SlashCommandDefinition>();
        if (includeProjectCommands && !string.IsNullOrWhiteSpace(projectRoot) && Directory.Exists(projectRoot))
        {
            var commandsDirectory = Path.Combine(Path.GetFullPath(projectRoot), ".codev", "commands");
            if (IsSafeProjectCommandDirectory(projectRoot, commandsDirectory))
                project = await LoadScopeAsync(commandsDirectory, "project", warnings, cancellationToken).ConfigureAwait(false);
            else
                warnings.Add("Project commands were skipped because .codev/commands is a symbolic link or resolves outside the project.");
        }

        // Project commands take precedence over user commands with the same name. Built-ins are reserved.
        var merged = new List<SlashCommandDefinition>();
        var names = SlashCommandCatalog.All.Select(command => command.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var command in project.Concat(user))
        {
            if (!names.Add(command.Name))
            {
                if (SlashCommandCatalog.All.Any(builtin => builtin.Name.Equals(command.Name, StringComparison.OrdinalIgnoreCase)))
                    warnings.Add($"Custom command '{command.Name}' was skipped because that name is built in.");
                continue;
            }
            merged.Add(command);
        }
        return new SlashCommandFileLoadResult(merged, warnings);
    }

    public static bool TryParseFile(string fileName, string contents, string scope,
        out SlashCommandDefinition? command, out string error)
    {
        command = null;
        error = "";
        var name = Path.GetFileNameWithoutExtension(fileName);
        if (!fileName.EndsWith(".md", StringComparison.OrdinalIgnoreCase) || !NameRegex().IsMatch(name))
            return Fail("Use a Markdown filename with a command name made of letters, digits, hyphens, or underscores.", out error);
        if (SlashCommandCatalog.All.Any(builtin => builtin.Name.Equals("/" + name, StringComparison.OrdinalIgnoreCase)))
            return Fail("Custom commands cannot replace built-in commands.", out error);
        if (contents.Length > MaxCommandFileBytes)
            return Fail($"Command files may not exceed {MaxCommandFileBytes / 1024} KB.", out error);

        var lines = contents.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        if (lines.Length < 4 || lines[0].Trim() != "---")
            return Fail("Start the Markdown file with frontmatter containing description and optional arguments.", out error);
        var end = Array.FindIndex(lines, 1, line => line.Trim() == "---");
        if (end < 0) return Fail("Frontmatter is missing its closing --- line.", out error);

        string? description = null;
        var arguments = new List<string>();
        foreach (var line in lines.Skip(1).Take(end - 1))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#')) continue;
            var colon = trimmed.IndexOf(':');
            if (colon <= 0) return Fail("Each frontmatter entry must use key: value syntax.", out error);
            var key = trimmed[..colon].Trim();
            var value = trimmed[(colon + 1)..].Trim().Trim('"', '\'');
            if (key.Equals("description", StringComparison.OrdinalIgnoreCase)) description = value;
            else if (key.Equals("arguments", StringComparison.OrdinalIgnoreCase))
            {
                if (value.Length > 0) arguments = value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList();
            }
            else return Fail($"Unsupported frontmatter key '{key}'.", out error);
        }

        if (string.IsNullOrWhiteSpace(description) || description.Length > 180)
            return Fail("Add a short description of at most 180 characters.", out error);
        if (arguments.Count > MaxArguments || arguments.Any(argument => !ArgumentNameRegex().IsMatch(argument)) ||
            arguments.Distinct(StringComparer.OrdinalIgnoreCase).Count() != arguments.Count)
            return Fail($"Declare up to {MaxArguments} unique named arguments using letters, digits, and underscores.", out error);

        var prompt = string.Join('\n', lines.Skip(end + 1)).Trim();
        if (string.IsNullOrWhiteSpace(prompt) || prompt.Length > MaxPromptCharacters)
            return Fail($"The prompt body must contain 1–{MaxPromptCharacters} characters.", out error);

        var declared = arguments.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var referenced = PlaceholderRegex().Matches(prompt).Select(match => match.Groups[1].Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (referenced.Except(declared).Any())
            return Fail("Every {{placeholder}} in the prompt must be declared in the arguments frontmatter.", out error);
        if (declared.Except(referenced).Any())
            return Fail("Every declared argument must appear as a {{placeholder}} in the prompt body.", out error);

        command = new SlashCommandDefinition("/" + name, description, SlashCommandAction.UserPrompt,
            prompt, arguments, scope);
        return true;
    }

    public static SlashCommandExpansionResult Expand(SlashCommandDefinition command, string? invocation)
    {
        if (!command.IsCustom || string.IsNullOrWhiteSpace(command.Prompt))
            return new(false, "", "This is not a custom prompt command.");
        if (!SlashCommandCatalog.TryGetCommandToken(invocation, invocation?.Length, out var token, out var hasArguments) ||
            !token.Equals(command.Name, StringComparison.OrdinalIgnoreCase))
            return new(false, "", $"Type {command.Name} followed by its named arguments.");

        var arguments = command.ArgumentNames ?? [];
        var rawArguments = hasArguments ? invocation![token.Length..].Trim() : "";
        if (!TryParseArguments(rawArguments, out var values, out var error)) return new(false, "", error);
        var allowed = arguments.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unknown = values.Keys.FirstOrDefault(key => !allowed.Contains(key));
        if (unknown is not null) return new(false, "", $"Unknown argument '{unknown}'. Expected: {string.Join(", ", arguments)}.");
        var missing = arguments.FirstOrDefault(argument => !values.ContainsKey(argument));
        if (missing is not null) return new(false, "", $"Missing required argument '{missing}'. Example: {command.Name} {string.Join(" ", arguments.Select(argument => argument + "=<value>"))}.");

        var expanded = PlaceholderRegex().Replace(command.Prompt, match => values[match.Groups[1].Value]);
        return expanded.Length <= MaxPromptCharacters
            ? new(true, expanded, "")
            : new(false, "", "The expanded prompt is too long.");
    }

    private static async Task<List<SlashCommandDefinition>> LoadScopeAsync(string directory, string scope,
        List<string> warnings, CancellationToken cancellationToken)
    {
        var commands = new List<SlashCommandDefinition>();
        try
        {
            if (!Directory.Exists(directory)) return commands;
            var info = new DirectoryInfo(directory);
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                warnings.Add($"The {scope} commands folder was skipped because it is a symbolic link.");
                return commands;
            }

            var paths = Directory.EnumerateFiles(directory, "*.md", SearchOption.TopDirectoryOnly)
                .Take(MaxCommandsPerScope + 1).ToArray();
            if (paths.Length > MaxCommandsPerScope)
                warnings.Add($"Only the first {MaxCommandsPerScope} {scope} command files are loaded.");
            foreach (var path in paths.Take(MaxCommandsPerScope).OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var file = new FileInfo(path);
                    if ((file.Attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        warnings.Add($"Command file '{file.Name}' was skipped because it is a symbolic link.");
                        continue;
                    }
                    if (file.Length > MaxCommandFileBytes)
                    {
                        warnings.Add($"Command file '{file.Name}' exceeds the size limit and was skipped.");
                        continue;
                    }
                    var bytes = await ReadBoundedAsync(path, cancellationToken).ConfigureAwait(false);
                    var text = StrictUtf8.GetString(bytes);
                    if (TryParseFile(file.Name, text, scope, out var command, out var error) && command is not null)
                        commands.Add(command with { FilePath = file.FullName });
                    else warnings.Add($"Command file '{file.Name}' was skipped: {error}");
                }
                catch (DecoderFallbackException)
                {
                    warnings.Add($"Command file '{Path.GetFileName(path)}' is not valid UTF-8 and was skipped.");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
                {
                    warnings.Add($"Command file '{Path.GetFileName(path)}' could not be read and was skipped.");
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            warnings.Add($"The {scope} commands folder could not be read.");
        }
        return commands;
    }

    private static async Task<byte[]> ReadBoundedAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length > MaxCommandFileBytes) throw new IOException("Command file exceeds the size limit.");
        var buffer = new byte[MaxCommandFileBytes + 1];
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total, buffer.Length - total), cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            total += read;
        }
        if (total > MaxCommandFileBytes) throw new IOException("Command file exceeds the size limit.");
        return buffer[..total];
    }

    private static bool IsSafeProjectCommandDirectory(string projectRoot, string commandsDirectory)
    {
        var root = Path.GetFullPath(projectRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(commandsDirectory);
        if (!full.StartsWith(root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) return false;
        var current = Path.GetFullPath(projectRoot);
        foreach (var segment in new[] { ".codev", "commands" })
        {
            current = Path.Combine(current, segment);
            if (!Directory.Exists(current)) continue;
            if ((new DirectoryInfo(current).Attributes & FileAttributes.ReparsePoint) != 0) return false;
        }
        return true;
    }

    private static bool TryParseArguments(string input, out Dictionary<string, string> values, out string error)
    {
        values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        error = "";
        var position = 0;
        while (position < input.Length)
        {
            while (position < input.Length && char.IsWhiteSpace(input[position])) position++;
            if (position >= input.Length) break;
            var keyStart = position;
            while (position < input.Length && input[position] != '=' && !char.IsWhiteSpace(input[position])) position++;
            if (position == keyStart || position >= input.Length || input[position] != '=')
            {
                error = "Arguments use name=value syntax. Quote values containing spaces, for example area=\"login flow\".";
                return false;
            }
            var key = input[keyStart..position];
            position++;
            string value;
            if (position < input.Length && input[position] is '"' or '\'')
            {
                var quote = input[position++];
                var builder = new StringBuilder();
                var closed = false;
                while (position < input.Length)
                {
                    var character = input[position++];
                    if (character == quote) { closed = true; break; }
                    if (character == '\\' && position < input.Length && (input[position] == quote || input[position] == '\\'))
                        character = input[position++];
                    builder.Append(character);
                }
                if (!closed)
                {
                    error = $"Argument '{key}' has an unclosed quote.";
                    return false;
                }
                if (position < input.Length && !char.IsWhiteSpace(input[position]))
                {
                    error = $"Add a space after the quoted value for '{key}'.";
                    return false;
                }
                value = builder.ToString();
            }
            else
            {
                var valueStart = position;
                while (position < input.Length && !char.IsWhiteSpace(input[position])) position++;
                value = input[valueStart..position];
            }
            if (!values.TryAdd(key, value))
            {
                error = $"Argument '{key}' was provided more than once.";
                return false;
            }
        }
        return true;
    }

    private static bool Fail(string message, out string error)
    {
        error = message;
        return false;
    }
}
