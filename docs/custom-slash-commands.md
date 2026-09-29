# Custom slash commands

Codev reads Markdown prompt files from two folders:

- User commands, available in every chat: `%LOCALAPPDATA%\Codev\commands` on Windows, or `Codev/commands` under the platform's local application-data folder.
- Project commands, available only when the attached project is trusted: `<project>/.codev/commands`. These files can be shared in Git with the project.

Type `/` in either composer to search built-in and custom commands. User commands are labeled **USER**; Avalonia also labels trusted-project commands **PROJECT**. A project command takes precedence over a user command with the same name; built-in names are reserved. Use `/commands` to open the user command folder. Avalonia additionally offers a separate project-command folder when the attached project is trusted. WPF currently supports user commands only.

Prompt templates saved by the WPF app in `%LOCALAPPDATA%\Codev\settings.json` remain **SAVED** slash suggestions named `/template-<name>` in WPF. Avalonia imports them into its own settings the first time it starts without an Avalonia template list. Use the **Prompt templates** button to add, preview, edit, remove, or insert templates. Inserting always leaves the prompt in the composer for review. Avalonia edits do not write back to the WPF settings file; templates can be moved to Markdown command files when you want them available as user or project commands.

The filename without `.md` is the command name. Use lowercase letters, digits, hyphens, or underscores, up to 40 characters. Each file starts with frontmatter containing a short description and optional required named arguments:

```markdown
---
description: Review a named area of the project
arguments: area, file
---
Review {{area}} in {{file}}. Find correctness bugs and missing tests. Return actionable findings only.
```

Choose `/inspect-auth`, then provide every declared argument as `name=value`. Quote values containing spaces, for example `/inspect-auth area="login flow" file=src/Auth.cs`. Codev replaces the named placeholders and puts the completed prompt into the composer for you to review; it does not send it automatically. Commands with no `arguments` frontmatter can be invoked directly from the menu and are also inserted into the composer for review. When a command needs arguments, choosing it from the menu inserts the command and empty `name=` slots for you to fill.

Codev loads only top-level `.md` files, up to 64 files per scope and 16 KB per file. The prompt body is limited to 12,000 characters, with at most eight named arguments. Invalid files and symbolic links are skipped with a visible composer status message. Project command files are not read from untrusted folders. Markdown is prompt text only: command files cannot run scripts, grant tools, or override Codev approvals.
