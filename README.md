# Codev

[![Windows CI](https://github.com/jay23606/codev/actions/workflows/windows-ci.yml/badge.svg?branch=main)](https://github.com/jay23606/codev/actions/workflows/windows-ci.yml)

Codev is a standalone Windows desktop coding workspace for local language models. It connects directly to Ollama on `127.0.0.1` by default; a different server can be configured explicitly in Settings, with a warning before using a non-local endpoint. No VS Code dependency is required.

## First preview

- A chat-first desktop layout, with dark mode as the default and light mode available
- Independent persistent conversations with per-chat autosaved composer drafts and a local-save indicator, pinning, and debounced title/message search; search can match all query words across a conversation and optionally include all projects
- Find within the active conversation with message excerpts and direct navigation (`Ctrl+Shift+F`)
- Optional completion toasts for responses that finish while you are away from their conversation
- Conversation rename and archive/restore, with permanent deletion available from the archive view
- Regenerate the latest answer, edit/resend the last prompt, and branch a new conversation from a message
- Continue an explicitly stopped answer from its partial text
- Keyboard shortcuts include quick new chat, search, composer focus, Settings, rename, and stop; press F1 for the in-app reference
- Save reusable local prompt templates and insert them into the composer for editing before sending
- Dark and light themes, with dark as the default and the choice remembered locally
- Per-conversation Ollama model selection, with quick choices for the three models in the original setup
- Per-conversation Ollama context selection, limited to each model's configured maximum
- Per-conversation temperature control (0–2), with Ollama's model default preserved unless you set a value
- Last request token count compared with the selected context size, reported by Ollama
- Streaming responses from Ollama
- Hardware-aware serial request queue with pause/resume controls; unsent turns survive app restart and wait for an explicit **Resume saved queue** action
- Assistant Markdown formatting with scrollable tables, clickable HTTP(S) links, and fenced code blocks with syntax coloring and a Copy button
- Copy any message from its context menu, including while another response is running
- Read-only Plan mode, explicit Code task mode, Stop control that retains partial output, and current agent-tool status
- Project switcher with pinned projects, project-specific instructions and reusable knowledge notes, and conversations scoped to each project
- Optional personal instructions apply across chats, plans, and code tasks; they are stored locally and included with requests to the configured Ollama server
- Optional root `AGENTS.md` is read through project-bounded file access and included as capped project guidance in local requests
- Git status, per-file staged/unstaged diff review, sending selected diff lines to the chat composer, staging, unstaging, reviewed local commits, and local branch creation/switching from a project's menu; branch operations require a clean tree, and commits are never pushed automatically
- Optional project-folder context: selected source/config files are read locally and sent to the local Ollama server as bounded context
- Choose up to 24 project-relative source files for a conversation; right-click Add files to remove one file or clear the selection
- Browse project text files, filter the list, preview them read-only, and add a selected file directly to chat context
- Drop supported project source/text files onto the composer to add them to local context; files outside the active project, secret files, excluded files, and binary/unsupported types are ignored
- Explicit Code task mode with project-scoped list/read/search tools and approval-gated file creation/replacement; changes create local recovery checkpoints and can be reviewed, restored, or undone/redone through Files
- Conversation history stays in `%LOCALAPPDATA%\Codev\conversations.json` and is written atomically; unreadable history is preserved before Codev switches to a separate recovery file
- Create a local JSON backup of all conversations and import one additively; imports receive new conversation IDs and do not replace current history

Code task mode can create or edit supported source, text, and configuration files after showing a review and receiving approval. The Files button shows changes and lets you review or restore checkpoints, including undo/redo of file creation. Code task mode can also request an approved shell command: PowerShell on Windows, or `$SHELL` (falling back to Bash) on macOS and Linux. Set `CODEV_SHELL` to override the executable. Every command requires approval, shows elapsed progress, runs with your account permissions in the project folder, has a three-minute timeout, and is not sandboxed. Use the active-turn Stop control to terminate it. Ordinary chat remains read-only.

## Run

Requirements: Windows 10/11, .NET 9 Desktop Runtime, and Ollama running at `http://127.0.0.1:11434` (the default endpoint).

```powershell
dotnet run --project .\Codev.csproj
```

The Avalonia renderer prototype is separate and is not yet a replacement for the full WPF app:

```powershell
dotnet run --project .\Codev.Avalonia\Codev.Avalonia.csproj
```

It currently demonstrates the dark shell and locally persisted conversation browsing. Ollama streaming, project tools, reviewed edits, Git, export, backups, and the remaining WPF workflows are still on the porting roadmap.

Build a self-contained Windows app:

```powershell
dotnet publish .\Codev.csproj -c Release -r win-x64 --self-contained true
```

## Local models

Codev discovers installed models from Ollama and shows the supported coding models below when installed. Duplicate tags and source repository names for the same weights are hidden from the picker:

- `devstral-small-2-64k`
- `qwen3-coder-next-q2-24k`
- `qwen3-coder:30b`

Legacy Ollama tags for the same weights are recognized and shown under the same friendly model name.

## Privacy

Prompts and project excerpts are sent to the configured Ollama endpoint; localhost is the default, and Codev confirms before switching to a non-local server. Chat mode reads a bounded set of common source/config files (up to 24 files and roughly 32,000 characters total), excluding build output, dependency folders, `.git`, and common secret files. Code task mode can read and search files within the selected workspace and apply a replacement only after user approval; it saves a checkpoint first and refuses to overwrite files that changed after review. Symbolic links and junctions are excluded. Approved shell commands are not sandboxed and can access resources available to your Windows account.

## Design

See [DESIGN.md](DESIGN.md) for the interaction model and roadmap.

## License

MIT. See [LICENSE](LICENSE).
