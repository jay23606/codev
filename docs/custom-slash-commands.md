# Custom slash commands

Codev reads Markdown prompt files from two folders:

- User commands, available in every chat: `%LOCALAPPDATA%\Codev\commands` on Windows, or `Codev/commands` under the platform's local application-data folder.
- Project commands, available only when the attached project is trusted: `<project>/.codev/commands`. These files can be shared in Git with the project.

Type `/` in the Avalonia composer to search built-in and custom commands. User commands are labeled **USER** and project commands **PROJECT**. A project command takes precedence over a user command with the same name; built-in names are reserved. Use `/commands` to open the folders and see this format.

The filename without `.md` is the command name. Use lowercase letters, digits, hyphens, or underscores, up to 40 characters. Each file starts with frontmatter containing a short description and optional required named arguments:

```markdown
---
description: Review a named area of the project
arguments: area, file
---
Review {{area}} in {{file}}. Find correctness bugs and missing tests. Return actionable findings only.
```

Choose `/inspect-auth`, then provide every declared argument as `name=value`. Quote values containing spaces, for example `/inspect-auth area="login flow" file=src/Auth.cs`. Codev replaces the named placeholders and puts the completed prompt into the composer for you to review; it does not send it automatically. Commands with no `arguments` frontmatter can be invoked directly from the menu and are also inserted into the composer for review.

Codev loads only top-level `.md` files, up to 64 files per scope and 16 KB per file. The prompt body is limited to 12,000 characters, with at most eight named arguments. Invalid files and symbolic links are skipped with a visible composer status message. Project command files are not read from untrusted folders. Markdown is prompt text only: command files cannot run scripts, grant tools, or override Codev approvals.
