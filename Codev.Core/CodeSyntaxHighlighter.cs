using System.Text;

namespace Codev;

public enum CodeTokenKind { Plain, Keyword, Type, String, Number, Comment }

public sealed record CodeToken(string Text, CodeTokenKind Kind);

/// <summary>A deliberately small, dependency-free lexer for coloring common fenced code blocks.</summary>
public static class CodeSyntaxHighlighter
{
    private static readonly HashSet<string> SupportedLanguages = new(StringComparer.OrdinalIgnoreCase)
    {
        "c", "cpp", "csharp", "cs", "css", "go", "golang", "java", "javascript", "js", "json", "jsx",
        "bash", "shell", "sh", "kotlin", "kt", "php", "python", "py", "powershell", "ps1", "ruby", "rb", "rust", "rs", "sql",
        "swift", "typescript", "ts", "tsx", "fsharp"
    };

    private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
    {
        "abstract", "as", "async", "await", "break", "case", "catch", "class", "const", "continue", "default",
        "def", "delete", "do", "else", "enum", "export", "extends", "false", "finally", "for", "foreach", "from",
        "func", "function", "if", "implements", "import", "in", "interface", "internal", "is", "let", "match",
        "module", "mut", "namespace", "new", "nil", "null", "of", "open", "override", "package", "pass", "private",
        "protected", "public", "raise", "return", "select", "self", "static", "struct", "super", "switch", "this",
        "throw", "throws", "true", "try", "type", "var", "void", "while", "with", "yield", "def", "elif", "except",
        "None", "and", "or", "not", "lambda", "print", "where", "group", "order", "by", "insert", "update", "into",
        "values", "create", "table", "alter", "drop", "join", "left", "right", "inner", "outer", "on", "as"
    };

    private static readonly HashSet<string> Types = new(StringComparer.Ordinal)
    {
        "bool", "boolean", "byte", "char", "decimal", "double", "float", "int", "int16", "int32", "int64", "long",
        "object", "short", "string", "uint", "ulong", "ushort", "void", "any", "never", "number", "unknown",
        "Array", "Date", "Error", "List", "Map", "Promise", "Set", "String", "Task", "Tuple"
    };

    public static IReadOnlyList<CodeToken> Tokenize(string code, string? language)
    {
        if (string.IsNullOrEmpty(code)) return [];
        if (!SupportedLanguages.Contains(Normalize(language))) return [new CodeToken(code, CodeTokenKind.Plain)];

        var tokens = new List<CodeToken>();
        var plain = new StringBuilder();
        var index = 0;
        while (index < code.Length)
        {
            var start = index;
            var current = code[index];
            if (char.IsWhiteSpace(current))
            {
                while (index < code.Length && char.IsWhiteSpace(code[index])) index++;
                Add(CodeTokenKind.Plain);
                continue;
            }

            if (IsLineCommentStart(code, index, language!, out var commentLength))
            {
                index += commentLength;
                while (index < code.Length && code[index] is not '\r' and not '\n') index++;
                Add(CodeTokenKind.Comment);
                continue;
            }
            if (code.AsSpan(index).StartsWith("/*", StringComparison.Ordinal))
            {
                var end = code.IndexOf("*/", index + 2, StringComparison.Ordinal);
                index = end < 0 ? code.Length : end + 2;
                Add(CodeTokenKind.Comment);
                continue;
            }

            if (current is '\'' or '"' or '`')
            {
                var quote = current;
                var triple = Normalize(language) is "python" or "py" && index + 2 < code.Length && code[index + 1] == quote && code[index + 2] == quote;
                var quoteLength = triple ? 3 : 1;
                index += quoteLength;
                while (index < code.Length)
                {
                    if (code[index] == '\\' && index + 1 < code.Length) { index += 2; continue; }
                    if (triple
                        ? index + 2 < code.Length && code[index] == quote && code[index + 1] == quote && code[index + 2] == quote
                        : code[index] == quote)
                    {
                        index += quoteLength;
                        break;
                    }
                    if (!triple && quote != '`' && code[index] is '\r' or '\n') break;
                    index++;
                }
                Add(CodeTokenKind.String);
                continue;
            }

            if (char.IsLetter(current) || current == '_')
            {
                index++;
                while (index < code.Length && (char.IsLetterOrDigit(code[index]) || code[index] == '_')) index++;
                var word = code[start..index];
                Add(Keywords.Contains(word) ? CodeTokenKind.Keyword : Types.Contains(word) ? CodeTokenKind.Type : CodeTokenKind.Plain);
                continue;
            }

            if (char.IsDigit(current))
            {
                index++;
                while (index < code.Length && (char.IsLetterOrDigit(code[index]) || code[index] is '.' or '_' or '+' or '-'))
                {
                    if (code[index] is '+' or '-' && code[index - 1] is not 'e' and not 'E') break;
                    index++;
                }
                Add(CodeTokenKind.Number);
                continue;
            }

            index++;
            Add(CodeTokenKind.Plain);

            void Add(CodeTokenKind kind)
            {
                if (kind == CodeTokenKind.Plain)
                {
                    plain.Append(code, start, index - start);
                    return;
                }
                FlushPlain();
                tokens.Add(new CodeToken(code[start..index], kind));
            }

            void FlushPlain()
            {
                if (plain.Length == 0) return;
                tokens.Add(new CodeToken(plain.ToString(), CodeTokenKind.Plain));
                plain.Clear();
            }
        }
        if (plain.Length > 0) tokens.Add(new CodeToken(plain.ToString(), CodeTokenKind.Plain));
        return tokens;
    }

    private static string Normalize(string? language) => (language ?? "").Trim().ToLowerInvariant() switch
    {
        "c#" => "csharp", "c++" => "cpp", "f#" => "fsharp", "node" => "javascript", "sh" or "shell" => "bash",
        var value => value
    };

    private static bool IsLineCommentStart(string code, int index, string language, out int length)
    {
        length = 0;
        var normalized = Normalize(language);
        if (code[index] == '#' && normalized is "python" or "py" or "ruby" or "rb" or "powershell" or "ps1" or "bash") length = 1;
        else if (code.AsSpan(index).StartsWith("--", StringComparison.Ordinal) && normalized == "sql") length = 2;
        else if (code.AsSpan(index).StartsWith("//", StringComparison.Ordinal) && normalized is not "sql" and not "python" and not "py" and not "ruby" and not "rb" and not "powershell" and not "ps1") length = 2;
        return length > 0;
    }
}
