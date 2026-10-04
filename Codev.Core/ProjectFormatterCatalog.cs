using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Codev;

public sealed record ProjectFormatterDefinition(string Name, IReadOnlyList<string> Extensions,
    string Executable, IReadOnlyList<string> Arguments);
public sealed record ProjectFormatterLoadResult(IReadOnlyList<ProjectFormatterDefinition> Formatters, string? Warning);
public sealed record ProjectFormatterRunResult(bool Ran, bool Succeeded, string Message, string Output);

/// <summary>Loads and runs explicit trusted-project formatters after accepted file changes.</summary>
public static partial class ProjectFormatterCatalog
{
    public const string RelativeConfigPath = ".codev/formatters.json";
    public const string EmptyConfiguration = "{\n  \"formatters\": []\n}\n";
    public const int MaxConfigBytes = 32 * 1024;
    public const int MaxFormatters = 32;
    public const int MaxOutputCharacters = 8_000;
    public static readonly TimeSpan FormatterTimeout = TimeSpan.FromSeconds(30);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        MaxDepth = 8,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    [GeneratedRegex("^[a-z][a-z0-9_-]{0,39}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SafeName();

    public static async Task<ProjectFormatterLoadResult> LoadAsync(string projectRoot, bool isTrusted,
        CancellationToken cancellationToken = default)
    {
        if (!isTrusted) return new([], null);
        try
        {
            if (!Directory.Exists(projectRoot) || !HasNoLinkedSegments(projectRoot))
                return new([], "Project formatter configuration was ignored because the trusted project path contains a symbolic link.");
            var codevDirectory = Path.Combine(Path.GetFullPath(projectRoot), ".codev");
            var path = Path.Combine(codevDirectory, "formatters.json");
            if (!Directory.Exists(codevDirectory) || !File.Exists(path)) return new([], null);
            if (IsLink(codevDirectory) || IsLink(path))
                return new([], "Project formatter configuration was ignored because .codev or formatters.json is a symbolic link.");
            var boundaryRoot = FileHardLinkInspector.GetCanonicalDirectoryPath(projectRoot);
            await using var stream = FileHardLinkInspector.OpenSingleLinkReadStream(path, RelativeConfigPath, boundaryRoot);
            if (stream.Length > MaxConfigBytes) return new([], $"Project formatter configuration exceeds the {MaxConfigBytes / 1024} KB limit.");
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
            var bytes = buffer.ToArray();
            return ValidateJson(Encoding.UTF8.GetString(bytes), out var validated, out var error)
                ? new(validated, null) : new([], error);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException)
        {
            return new([], "Project formatter configuration could not be read or is invalid JSON.");
        }
    }

    public static bool ValidateJson(string json, out IReadOnlyList<ProjectFormatterDefinition> formatters, out string error)
    {
        formatters = [];
        error = "Formatter configuration must be a JSON object with a formatters array.";
        if (string.IsNullOrWhiteSpace(json) || Encoding.UTF8.GetByteCount(json) > MaxConfigBytes)
        {
            error = $"Formatter configuration must contain 1–{MaxConfigBytes / 1024} KB of JSON.";
            return false;
        }
        try
        {
            var document = JsonSerializer.Deserialize<FormatterDocument>(json, JsonOptions);
            if (document?.Formatters is null || document.Formatters.Count > MaxFormatters)
            {
                error = $"Formatter configuration must contain a formatters array with at most {MaxFormatters} entries.";
                return false;
            }
            var validated = Validate(document.Formatters, out var validationError);
            if (validated is null) { error = validationError ?? "Formatter configuration is invalid."; return false; }
            formatters = validated;
            error = "";
            return true;
        }
        catch (JsonException) { error = "Formatter configuration is invalid JSON or contains an unsupported field."; return false; }
    }

    public static async Task SaveAsync(string projectRoot, string json, bool isTrusted, CancellationToken cancellationToken = default)
    {
        if (!isTrusted) throw new InvalidOperationException("Trust the project before configuring formatters.");
        if (!ValidateJson(json, out _, out var error)) throw new InvalidDataException(error);
        if (!HasNoLinkedSegments(projectRoot)) throw new UnauthorizedAccessException("Project formatter configuration cannot follow symbolic links.");
        var codevDirectory = Path.Combine(Path.GetFullPath(projectRoot), ".codev");
        if (Directory.Exists(codevDirectory) && IsLink(codevDirectory))
            throw new UnauthorizedAccessException("Project formatter configuration cannot be saved through a symbolic link.");
        Directory.CreateDirectory(codevDirectory);
        if (!HasNoLinkedSegments(projectRoot) || IsLink(codevDirectory))
            throw new UnauthorizedAccessException("Project formatter configuration cannot be saved through a symbolic link.");
        var path = Path.Combine(codevDirectory, "formatters.json");
        if (File.Exists(path) && IsLink(path)) throw new UnauthorizedAccessException("Project formatter configuration cannot replace a symbolic link.");
        await AtomicTextFile.WriteAsync(path, json, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<string> ReadConfigurationTextAsync(string projectRoot, bool isTrusted,
        CancellationToken cancellationToken = default)
    {
        if (!isTrusted) throw new InvalidOperationException("Trust the project before managing formatters.");
        if (!Directory.Exists(projectRoot) || !HasNoLinkedSegments(projectRoot))
            throw new UnauthorizedAccessException("Project formatter configuration cannot follow symbolic links.");
        var codevDirectory = Path.Combine(Path.GetFullPath(projectRoot), ".codev");
        var path = Path.Combine(codevDirectory, "formatters.json");
        if (!Directory.Exists(codevDirectory) || !File.Exists(path)) return EmptyConfiguration;
        if (IsLink(codevDirectory) || IsLink(path)) throw new UnauthorizedAccessException("Project formatter configuration cannot follow symbolic links.");
        var boundaryRoot = FileHardLinkInspector.GetCanonicalDirectoryPath(projectRoot);
        await using var stream = FileHardLinkInspector.OpenSingleLinkReadStream(path, RelativeConfigPath, boundaryRoot);
        if (stream.Length > MaxConfigBytes) throw new InvalidDataException($"Formatter configuration exceeds {MaxConfigBytes / 1024} KB.");
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        return new UTF8Encoding(false, true).GetString(buffer.ToArray());
    }

    public static ProjectFormatterDefinition? ForPath(IReadOnlyList<ProjectFormatterDefinition> formatters, string relativePath)
    {
        var extension = Path.GetExtension(relativePath);
        return formatters.FirstOrDefault(formatter => formatter.Extensions.Contains(extension, StringComparer.OrdinalIgnoreCase));
    }

    public static ProcessStartInfo CreateStartInfo(ProjectFormatterDefinition formatter, string fullPath, string workingDirectory)
    {
        var start = new ProcessStartInfo
        {
            FileName = formatter.Executable,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in formatter.Arguments)
            start.ArgumentList.Add(argument == "$FILE" ? fullPath : argument);
        return start;
    }

    public static string DisplayCommand(ProjectFormatterDefinition formatter, string relativePath)
    {
        var arguments = formatter.Arguments.Select(argument => argument == "$FILE" ? QuoteForDisplay(relativePath) : QuoteForDisplay(argument));
        return string.Join(' ', new[] { QuoteForDisplay(formatter.Executable) }.Concat(arguments));
    }

    public static async Task<ProjectFormatterRunResult> RunAsync(ProjectFormatterDefinition formatter, string fullPath,
        string relativePath, string projectRoot, Func<CodeTaskCommandProposal, Task<CommandApprovalOutcome>> approve,
        Action<string>? status, CancellationToken cancellationToken = default, Func<bool>? isStillTrusted = null)
    {
        ArgumentNullException.ThrowIfNull(formatter);
        ArgumentNullException.ThrowIfNull(approve);
        var display = DisplayCommand(formatter, relativePath);
        if (isStillTrusted is not null && !isStillTrusted())
            return new(false, false, "Formatter was not run because project trust was revoked.", "");
        var approval = await approve(new CodeTaskCommandProposal(display, projectRoot, "formatter process"));
        if (approval != CommandApprovalOutcome.Approved)
            return new(false, false, approval == CommandApprovalOutcome.Denied ? "Formatter blocked by a saved command deny rule." : "Formatter was not run because command approval was declined.", "");
        if (isStillTrusted is not null && !isStillTrusted())
            return new(false, false, "Formatter was not run because project trust was revoked.", "");

        using var process = new Process { StartInfo = CreateStartInfo(formatter, fullPath, projectRoot), EnableRaisingEvents = true };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(FormatterTimeout);
        status?.Invoke($"Formatting {relativePath}…");
        try
        {
            if (!process.Start()) return new(true, false, "Formatter process could not be started.", "");
            var stdout = ReadBoundedAsync(process.StandardOutput, MaxOutputCharacters / 2, cancellationToken);
            var stderr = ReadBoundedAsync(process.StandardError, MaxOutputCharacters / 2, cancellationToken);
            try { await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                try { await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false); } catch { }
                await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
                return new(true, false, $"Formatter timed out after {FormatterTimeout.TotalSeconds:0} seconds.", JoinOutput(await stdout, await stderr));
            }
            var output = JoinOutput(await stdout, await stderr);
            return process.ExitCode == 0
                ? new(true, true, "Formatter completed.", output)
                : new(true, false, $"Formatter exited with code {process.ExitCode}.", output);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            throw;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            return new(true, false, $"Formatter could not be started ({ex.GetType().Name}).", "");
        }
        finally { status?.Invoke("Code task · Thinking…"); }
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, int limit, CancellationToken cancellationToken)
    {
        var result = new StringBuilder(Math.Min(limit, 1024));
        var buffer = new char[1024];
        var truncated = false;
        while (true)
        {
            var count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (count == 0) break;
            var keep = Math.Min(count, limit - result.Length);
            if (keep > 0) result.Append(buffer, 0, keep);
            if (keep < count) truncated = true;
        }
        if (truncated) result.Append("\n[formatter output truncated]");
        return result.ToString();
    }

    private static string JoinOutput(string stdout, string stderr) =>
        string.Join("\n", new[] { string.IsNullOrWhiteSpace(stdout) ? null : stdout, string.IsNullOrWhiteSpace(stderr) ? null : "STDERR: " + stderr }.Where(value => value is not null));

    private static string QuoteForDisplay(string value) => "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    private static IReadOnlyList<ProjectFormatterDefinition>? Validate(IReadOnlyList<FormatterDto> input, out string? error)
    {
        error = null;
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<ProjectFormatterDefinition>();
        foreach (var formatter in input)
        {
            if (formatter is null || string.IsNullOrWhiteSpace(formatter.Name) || !SafeName().IsMatch(formatter.Name) || !names.Add(formatter.Name) ||
                string.IsNullOrWhiteSpace(formatter.Executable) || formatter.Executable.Length > 260 || formatter.Executable.Any(char.IsControl) ||
                formatter.Extensions is not { Count: > 0 and <= 40 } || formatter.Arguments is not { Count: > 0 and <= 40 } ||
                formatter.Arguments.Count(argument => argument == "$FILE") != 1 ||
                formatter.Arguments.Any(argument => argument is null || argument.Length > 1000 || argument.Any(char.IsControl)))
            {
                error = "Each formatter needs a unique simple name, executable, 1–40 extensions, and 1–40 arguments containing exactly one $FILE argument.";
                return null;
            }
            var localExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var extension in formatter.Extensions)
            {
                if (string.IsNullOrWhiteSpace(extension) || extension.Length is > 16 or < 2 || extension[0] != '.' ||
                    extension.Skip(1).Any(character => !char.IsAsciiLetterOrDigit(character)) ||
                    !localExtensions.Add(extension) || !extensions.Add(extension))
                {
                    error = "Formatter extensions must be unique simple extensions such as .cs or .ts.";
                    return null;
                }
            }
            result.Add(new ProjectFormatterDefinition(formatter.Name, localExtensions.ToArray(), formatter.Executable,
                formatter.Arguments.ToArray()));
        }
        return result;
    }

    private static bool HasNoLinkedSegments(string path)
    {
        try
        {
            var current = Path.GetFullPath(path);
            while (true)
            {
                if ((Directory.Exists(current) || File.Exists(current)) && IsLink(current)) return false;
                var parent = Path.GetDirectoryName(current);
                if (string.IsNullOrEmpty(parent) || string.Equals(parent, current,
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) return true;
                current = parent;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return false; }
    }

    private static bool IsLink(string path)
    {
        try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return true; }
    }

    private sealed class FormatterDocument { public List<FormatterDto>? Formatters { get; set; } }
    private sealed class FormatterDto
    {
        public string Name { get; set; } = "";
        public List<string> Extensions { get; set; } = [];
        public string Executable { get; set; } = "";
        public List<string> Arguments { get; set; } = [];
    }
}
