# Project formatters

Avalonia can run one opt-in formatter after Code task creates, edits, or patches a file. The project must be trusted. Open **Project → Project formatters…** or **More → Project formatters…** to edit the project's `.codev/formatters.json` configuration.

Formatters are disabled until configured. Each formatter claims one or more unique file extensions and supplies an executable plus an argument array. Put `$FILE` in exactly one argument; Codev replaces that argument with the changed file's full path and launches the executable directly, without a shell. For example:

```json
{
  "formatters": [
    {
      "name": "prettier",
      "extensions": [".js", ".ts", ".json"],
      "executable": "prettier",
      "arguments": ["--write", "$FILE"]
    }
  ]
}
```

The formatter runs after Codev accepts the file change, and the undo checkpoint covers both the proposed edit and the formatter's result. Its command follows the project's Ask, Allowlist, or Auto command policy; an exact saved deny still blocks it. Auto mode can run it without another prompt. Formatters run with the current user's operating-system permissions and are not sandboxed. Only configure projects and executables you trust.

Configuration is limited to 32 KB and 32 formatter entries. Unknown JSON fields, duplicate names/extensions, malformed extensions, invalid argument lists, and symbolic links are rejected. A formatter is limited to 30 seconds; its output is captured as bounded, collapsed untrusted activity. A nonzero exit or timeout is reported but does not roll back the accepted file edit automatically; use Files to restore the checkpoint if needed.

This is the first D4 hook. General before/after hooks and blocking hooks are not implemented yet. WPF retains shared-core compatibility but does not expose the formatter configuration UI or callback.
