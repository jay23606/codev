# Codev

[![Windows CI](https://github.com/jay23606/codev/actions/workflows/windows-ci.yml/badge.svg?branch=main)](https://github.com/jay23606/codev/actions/workflows/windows-ci.yml)

Codev is a standalone desktop coding workspace for local language models. The full-featured app runs on Windows today; a cross-platform Avalonia version for Windows, macOS and Linux is in progress and shares its core logic (see [Run](#run) and [Repository layout](#repository-layout)). It connects directly to Ollama on `127.0.0.1` by default; a different server can be configured explicitly in Settings, with a warning before using a non-local endpoint. No VS Code dependency is required.

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
- Per-conversation Ollama model selection, with friendly quick choices for the original three and automatic discovery of other installed models
- Per-conversation Ollama context selection, limited to each model's configured maximum
- Per-conversation temperature control (0–2), with Ollama's model default preserved unless you set a value
- Last request token count compared with the selected context size, reported by Ollama
- Streaming responses from Ollama
- Hardware-aware serial request queue with pause/resume controls; unsent turns survive app restart and wait for an explicit **Resume saved queue** action
- Assistant Markdown formatting with scrollable tables, clickable HTTP(S) links, and fenced code blocks with syntax coloring and a Copy button
- Copy any message from its context menu, including while another response is running
- Read-only Plan mode, explicit Code task mode, Stop control that retains partial output, and current agent-tool status
- Code tasks stop after eight tool rounds and ask before a tool call repeats with identical arguments three times in a row
- Project switcher with pinned projects, project-specific instructions and reusable knowledge notes, and conversations scoped to each project
- Optional personal instructions apply across chats, plans, and code tasks; they are stored locally and included with requests to the configured Ollama server
- Optional root `AGENTS.md` is read through project-bounded file access and included as capped project guidance in local requests
- Git status, per-file staged/unstaged diff review, sending selected diff lines to the chat composer, staging, unstaging, reviewed local commits, and local branch creation/switching from a project's menu; branch operations require a clean tree, and commits are never pushed automatically
- Optional project-folder context: selected source/config files are read locally and sent to the local Ollama server as bounded context
- Choose up to 24 project-relative source files for a conversation; right-click Add files to remove one file or clear the selection
- Browse project text files, filter the list, preview them read-only, and add a selected file directly to chat context
- Drop supported project source/text files onto the composer to add them to local context; files outside the active project, secret files, excluded files, and binary/unsupported types are ignored
- Explicit Code task mode with project-scoped list/read/search tools and approval-gated file creation/replacement; changes create local recovery checkpoints and can be reviewed, restored, or undone/redone through Files
- Conversation history stays in `%LOCALAPPDATA%\Codev\conversations.json` on Windows (other systems use the per-user local application data folder, under `Codev`) and is written atomically; unreadable history is preserved before Codev switches to a separate recovery file
- Create a local JSON backup of all conversations and import one additively; imports receive new conversation IDs and do not replace current history

Code task mode can create or edit supported source, text, and configuration files after showing a review and receiving approval. The Files button shows changes and lets you review or restore checkpoints, including undo/redo of file creation. Code task mode can also request an approved shell command: PowerShell on Windows, or `$SHELL` (falling back to Bash) on macOS and Linux. Set `CODEV_SHELL` to override the executable. Every command requires approval, shows elapsed progress, runs with your account permissions in the project folder, has a three-minute timeout, and is not sandboxed. Use the active-turn Stop control to terminate it. Ordinary chat remains read-only.

## Run

Codev has two front ends over one shared, cross-platform core:

| Front end | Platforms | Status |
|---|---|---|
| WPF app (`Codev.csproj`) | Windows 10/11 | The full feature set described above |
| Avalonia prototype (`Codev.Avalonia`) | Windows, macOS and Linux (CI builds it on all three) | Prototype; not yet a replacement for the WPF app |

Both need the .NET 9 SDK to build from source, and Ollama running at `http://127.0.0.1:11434` (the default endpoint). A framework-dependent published WPF build needs the .NET 9 Desktop Runtime; the self-contained publish below needs neither.

WPF app, Windows only:

```powershell
dotnet run --project .\Codev.csproj
```

The Avalonia prototype is separate and is not yet a replacement for the full WPF app. It keeps its own conversation files (`avalonia-*.json`), so it does not share history with the WPF app yet. A manual test list for it is in [docs/avalonia-smoke-checklist.md](docs/avalonia-smoke-checklist.md).

```powershell
dotnet run --project .\Codev.Avalonia\Codev.Avalonia.csproj
```

It currently provides a Claude/Codex-inspired dark shell, locally persisted conversations, restores the last active conversation and model, chat search, pin/archive, rename and permanent delete from each chat’s context menu, draft saving, installed-model discovery (including newly installed models when the picker opens), per-conversation context-size selection, right-aligned user bubbles and left-aligned assistant responses, streaming chat through local Ollama, and a serial follow-up queue. Chat and read-only Plan modes are available per conversation; Plan mode requests a concise ordered implementation plan and cannot edit files or run commands. Avalonia also has a local-only Code task mode for trusted project folders: the local Ollama model can list, read, and search project files, propose new files or complete replacements for review, and request shell commands; Codev asks before every change and every command, saves checkpoints before replacements, provides per-conversation file history with reviewed restore and undo, and runs approved commands with bounded output and a three-minute timeout. Type `/status` in the composer for a local snapshot of the provider, model, context, mode, project, and queue state; it does not call a model and identifies remote Ollama endpoints. When a project is attached, Codev estimates the bounded source-file context cost before sending and clarifies that the estimate excludes chat history; for hosted chats it also indicates when project files are kept local. Attaching an untrusted project asks whether to trust that folder, trust its parent and descendants, or keep it untrusted; until trusted, automatic source context is off, though browsing and explicitly selected files remain available. Trust decisions live in a separate local JSON file, appear in `/status`, and can be revoked. Type `@` in the composer to find supported files in the attached project; selecting a result adds it to this conversation's context. **Browse files** filters and previews supported project text files read-only before adding a file to context. OpenAI and Anthropic API keys can be entered for the current session or supplied through `OPENAI_API_KEY` / `ANTHROPIC_API_KEY`; Codev discovers text models and streams replies. Hosted API usage may have separate charges from ChatGPT or Claude subscriptions. An explicit acknowledgement is required before connecting, and attached project context has a separate opt-in for hosted chats. API keys are never written to settings, chat history, or backups. Settings also support a custom Ollama HTTP(S) server; remote servers require confirmation, and redirects are disabled. Remote Ollama is limited to chat; Code task mode requires loopback Ollama to keep project tools local. The model status changes to Ready when Ollama reports the selected model loaded, and shows elapsed load time, a timeout, or a connection error otherwise. New conversations inherit the active model and context size. Trust a local project folder to include bounded source excerpts automatically, or leave it untrusted and explicitly select up to 24 supported files; sensitive, excluded, unsupported, and outside-project files are rejected. Export a chat as Markdown, or export and additively import all chats as a portable JSON backup. Backup files include conversation text and project names/paths but exclude rollback checkpoint contents. Empty model options are filtered out. You can send another prompt while a response is running; each queued turn captures its model and context settings, queued turns survive restart and wait behind an explicit resume action, and unsent queued turns can be canceled. With an empty composer, the send button stops generation. The shell also follows output while streaming (paused when you scroll up), supports Enter-to-send with Shift+Enter for new lines, Ctrl/Cmd+N for a new conversation, Ctrl/Cmd+F to focus search, Ctrl/Cmd+L to focus the composer, Escape to stop generation, selectable Markdown responses without per-message Copy buttons, and a persistent dark/light theme toggle. Avalonia still needs turn-level rewind, richer diff navigation, worktree merge/recovery, and the remaining WPF workflows.

The Avalonia Git review dialog also lets you attach an unsent comment to selected diff lines. Comments persist with the conversation, appear above the composer, can be removed before sending, and are included with the diff excerpt in the sent prompt. They are preserved in conversation backups; broad hunk staging/revert and worktree merge/recovery remain roadmap items.

For compact project orientation, enable **Include repo map** beside the project context controls. Codev adds a bounded outline of safe project files and common declarations (up to 160 files and 8,000 characters), scoped to the trusted folder or explicitly selected files. It follows hosted-context consent, persists per conversation and queued turn, and shows an approximate maximum token cost before sending.

Each user prompt also has a **Rewind** action. It asks before removing that prompt and all later messages, restores the prompt to the composer, and leaves project files untouched; use Files history separately when you want to review a file restore.

Build a self-contained Windows app:

```powershell
dotnet publish .\Codev.csproj -c Release -r win-x64 --self-contained true
```

## Tests

```bash
dotnet test Codev.Tests/Codev.Tests.csproj                    # portable tests: any operating system
dotnet test Codev.Windows.Tests/Codev.Windows.Tests.csproj    # WPF renderer and PowerShell tests: Windows only
```

GitHub Actions builds `Codev.Core` and the Avalonia prototype and runs the portable tests on Windows, Linux and macOS for every push and pull request. The Windows-only tests, and a self-contained Windows publish, run on Windows.

## Repository layout

| Path | What it is |
|---|---|
| `Codev.Core/` | Shared logic with no UI dependency: conversations and persistence, Ollama endpoint checks and request options, Git integration, diffs, project context, the command runner and its shell selection |
| `Codev.Tests/` | Portable tests for the core (plain .NET; runs on every OS) |
| `Codev.csproj`, `MainWindow.xaml`, `MarkdownRenderer.cs` | The WPF app (Windows) |
| `Codev.Windows.Tests/` | Tests that need Windows: WPF rendering and PowerShell command execution |
| `Codev.Avalonia/` | The cross-platform Avalonia UI prototype |
| `docs/` | Manual test checklists |
| `DESIGN.md` | The interaction model, roadmap and feature backlog |

## Local models

Codev discovers installed models from Ollama at startup and refreshes the picker when it opens. Two models from the original setup receive friendly labels; other installed Ollama tags, including Qwen3.8-27B, are listed automatically:

- `qwen3-coder-next-q2-24k`
- `qwen3-coder:30b`

Legacy Ollama tags for the same weights are recognized and shown under the same friendly model name.

For a measured comparison of these and other models on this project's kind of tasks (accuracy, speed and agent behaviour, run on one CPU-only laptop), see [comparison.html](comparison.html).

## Privacy

Prompts and project excerpts are sent to the configured Ollama endpoint; localhost is the default, and Codev confirms before switching to a non-local server. Chat mode reads a bounded set of common source/config files (up to 24 files and roughly 32,000 characters total), excluding build output, dependency folders, `.git`, and common secret files. Code task mode can read and search files within the selected workspace and apply a replacement only after user approval; it saves a checkpoint first and refuses to overwrite files that changed after review. Symbolic links and junctions are excluded. Approved shell commands are not sandboxed and can access resources available to your user account.

## Design

See [DESIGN.md](DESIGN.md) for the interaction model and roadmap.

## License

MIT. See [LICENSE](LICENSE).
