# Codev

[![Windows CI](https://github.com/jay23606/codev/actions/workflows/windows-ci.yml/badge.svg?branch=main)](https://github.com/jay23606/codev/actions/workflows/windows-ci.yml)

## Why

Codev brings a chat-first desktop workflow for local and hosted coding models. OpenCode is the primary detailed coding-agent reference because its implementation and documentation are open; Codex and Claude are additional UX references. Codev keeps Ollama as the local default and does not depend on an editor.

## Download

Download the latest installable Avalonia build for [Windows x64](https://github.com/jay23606/codev/releases/latest/download/Codev-Avalonia-preview-win-x64.zip), [Linux x64](https://github.com/jay23606/codev/releases/latest/download/Codev-Avalonia-preview-linux-x64.zip), [macOS Apple silicon](https://github.com/jay23606/codev/releases/latest/download/Codev-Avalonia-preview-osx-arm64.zip), or [macOS Intel](https://github.com/jay23606/codev/releases/latest/download/Codev-Avalonia-preview-osx-x64.zip). See [all releases](https://github.com/jay23606/codev/releases) for the newest versions. These archives are self-contained; Ollama is optional for hosted models and must be installed separately for local models. Linux hosted API key storage requires Secret Service in the desktop session. Desktop and CLI packages are not code-signed; Windows SmartScreen or macOS Gatekeeper may require manual approval.

To try it on Windows, download the Windows x64 ZIP, extract the entire archive to a folder, and run `Codev.Avalonia.exe`. Connect an OpenAI or Anthropic API key in the app for hosted models, or install Ollama separately for local models.

For the standalone headless CLI, the current tagged release does not yet include CLI archives. Until the next CLI-enabled release is published, use the CLI ZIP in the `Codev-win-x64`, `Codev-linux-x64`, or macOS artifact from the [latest successful desktop preview workflow](https://github.com/jay23606/codev/actions/workflows/release-desktop.yml?query=branch%3Amain). Extract it and run `Codev.Cli.exe --help` on Windows or `./Codev.Cli --help` on macOS/Linux. The package includes the .NET runtime. Ollama is required for local models; OpenAI uses a key already saved by the desktop app in the operating system's credential store and requires `--allow-hosted-data` because prompts and project tool results are sent to OpenAI. Remote Ollama endpoints also require explicit consent. Runs are read-only by default; file edits and shell commands require separate explicit flags. Shell commands run unsandboxed with your account permissions.

For the latest main-branch build before a tagged release, open the [desktop preview workflow](https://github.com/jay23606/codev/actions/workflows/release-desktop.yml?query=branch%3Amain), choose its newest successful run, and download the `Codev-win-x64`, `Codev-linux-x64`, or macOS artifact. The artifacts include both desktop and CLI ZIPs. For Windows desktop, run `Codev.Avalonia.exe`; for Linux or macOS, run `Codev.Avalonia` from a desktop session. Pushing a `v*` tag runs the desktop workflow and publishes Avalonia and CLI archives for Windows, Linux, and macOS to a GitHub Release. The manual Avalonia build archives workflow only creates temporary Actions artifacts; it does not create or update a GitHub Release. Packaged interaction smoke runs on GitHub-hosted Windows, Linux, Apple-silicon macOS, and Intel macOS runners. Physical-keyboard and spoken screen-reader validation on Linux/macOS remain open; the current Avalonia 11.3.22 build does not expose a Linux AT-SPI tree.

Codev is a standalone, cross-platform desktop coding workspace for local and hosted language models. Avalonia is its sole desktop front end. Codev connects directly to Ollama on `127.0.0.1` by default; a different server can be configured explicitly in Settings, with a warning before using a non-local endpoint. No VS Code dependency is required.

Avalonia Code tasks include reusable built-in and Markdown agent profiles. See [custom agent profiles](docs/custom-agent-profiles.md) for the profile format and permission behavior.

Code tasks can also use external MCP tools, prompts, and resources. See [MCP server setup](docs/mcp-servers.md) for transport configuration, credential handling, and project permission behavior.

Trusted Avalonia projects can opt into formatters that run after accepted Code task file changes. See [project formatters](docs/project-formatters.md) for setup, command permissions, and limits.

## First preview

- A chat-first desktop layout, with dark mode as the default and light mode available
- Independent persistent conversations with per-chat autosaved composer drafts and a local-save indicator, pinning, and a debounced sidebar filter over conversation titles and messages; all query words can match across a conversation, and the filter searches conversations across projects within the selected archived or active view
- Find within the active conversation with message excerpts and direct navigation (`Ctrl/Cmd+Shift+F`)
- Optional completion toasts for responses that finish while you are away from their conversation
- Conversation rename and archive/restore, with permanent deletion available from the archive view
- Regenerate the latest answer, edit/resend the last prompt, and branch a new conversation from a message
- Continue an explicitly stopped answer from its partial text
- Avalonia keyboard shortcuts include new conversation, sidebar conversation search, in-conversation find, composer focus, mode and primary-agent cycling, and stopping a response; press F1 for the in-app shortcut reference.
- Save reusable local prompt templates and insert them into the composer for editing before sending
- Dark and light themes, with dark as the default and the choice remembered locally
- Per-conversation Ollama model selection, with friendly quick choices for the original three and automatic discovery of other installed models
- Optional OpenAI API and Anthropic API (Claude) chat models, with model discovery and streaming; connect from the model bar
- Explicit `/compact` flow in Avalonia: review and edit a summary of older turns, keep the full transcript, and restore full-history prompts at any time
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
- Trusted projects can include bounded `AGENTS.md` guidance plus `.codev/rules/*.md` path rules when matching source files enter context. Untrusted projects do not load hidden project instructions.
- Git status, per-file staged/unstaged diff review, sending selected diff lines to the chat composer, whole-file and selected-hunk stage/unstage/revert actions, stage/unstage all, and preview-first discard of unstaged changes; reviewed local commits and local branch creation/switching are also available, branch operations require a clean tree, and commits are never pushed automatically
- Optional project-folder context: selected source/config files are read locally and sent to the local Ollama server as bounded context
- Choose up to 24 project-relative source files for a conversation; right-click Add files to remove one file or clear the selection
- Browse project text files, filter the list, preview them read-only, and add a selected file directly to chat context; the shared source allowlist covers common .NET, JavaScript/TypeScript, Python, Java, Go, Rust, C/C++, Kotlin, Swift, PHP, Ruby, Dart, and web component files while excluding binaries and archives
- Drop supported project source/text files onto the composer to add them to local context; files outside the active project, secret files, excluded files, and binary/unsupported types are ignored
- Explicit Code task mode with project-scoped list/read/search tools and file creation/replacement governed by the project permission mode; Auto applies proposals with rollback checkpoints, while instruction-risk matches remain advisory. OpenAI-hosted Code tasks use strict function schemas backed by local argument validation. Changes create local recovery checkpoints and can be reviewed, restored, or undone/redone through Files
- Optional MCP integrations for Code tasks over stdio or HTTP, including tools, prompts, resources, environment-backed credentials, browser-based OAuth, and project-scoped permissions; see [MCP server setup](docs/mcp-servers.md)
- Optional Avalonia semantic project search for trusted projects: build or incrementally update an Ollama embedding index in Settings, then enable **Use semantic search** per conversation. Indexing and vectors stay local; install the selected embedding model separately. Retrieved excerpts are sent to a hosted provider only when the existing workspace-sharing option is enabled. The feature is off by default, and its index can be deleted in Settings.
- Conversation history stays in `%LOCALAPPDATA%\Codev\conversations.json` on Windows (other systems use the per-user local application data folder, under `Codev`) and is written atomically; unreadable history is preserved before Codev switches to a separate recovery file
- Right-click complete conversation turns to propose and edit a summary for a selected range; summaries affect future prompts only, stay in backups, and can be cleared to restore full history. Ollama nearing an explicitly selected context limit offers compaction for review; model-default context usage is shown without guessing its size.
- Create a local JSON backup of all conversations and import one additively; imports receive new conversation IDs and do not replace current history

Code task mode can create or edit supported source, text, and configuration files under the project permission mode. Ask and Allowlist modes review changes; Auto applies file proposals with rollback checkpoints; instruction-risk matches are advisory and do not interrupt Auto. The Files button shows changes and lets you review or restore checkpoints, including undo/redo of file creation. Code task mode can request shell commands through PowerShell on Windows, or `$SHELL` (falling back to Bash) on macOS and Linux; `CODEV_SHELL` can override the executable. Ask mode requires approval, Allowlist uses exact saved rules, and Auto runs commands unless the exact text has a saved deny rule. Commands show elapsed progress, have a three-minute timeout, use your account permissions without a project sandbox, and can be stopped with the active-turn control. Avalonia groups tool activity under one collapsed summary that names the actions in the turn (for example, **Edited a file, ran commands, searched files**); expand it to inspect each action and its output. Ordinary chat remains read-only.

In the Avalonia app, project rules live in `.codev/rules/` and are read only for trusted projects. By default, a rule is included when a matching source file is in context. Set `activation: always` to include a rule in every trusted project request; that form does not need `globs`. Set `activation: model` to ask the selected model to choose relevant rules before the main reply. That short preflight sends the task, included file paths, rule descriptions, and optional glob patterns, while rule bodies are sent only for selected rules. A hosted preflight is an additional API request and is available only when project-context sharing is enabled; it may add API usage charges. If selection fails, Codev continues without model-activated rules. You can also type `@rule:` in the composer and choose a rule to apply it to one request. For example, `javascript.md` can start with:

```markdown
---
description: JavaScript style
globs: **/*.js, **/*.ts
---
Prefer named exports and keep functions small.
```

## Hosted models

Choose **Connect hosted models…**. Select OpenAI or Anthropic (Claude) and enter an API key the first time, or leave the field blank to reuse a saved key. Codev waits for the OS credential-store restore before showing the dialog and identifies when a saved key is ready. Each session still requires you to enable hosted requests and load models; you do not need to enter a saved key again. Codev discovers the text models available to that API account and streams hosted chat replies. Connecting asks you to acknowledge that chat is sent to the provider and API usage may be billed; API access and billing are separate from ChatGPT and Claude subscriptions. Manually entered keys are saved after model discovery succeeds in the operating system credential store (Windows Credential Manager, macOS Keychain, or Linux Secret Service) and remain available across sessions until removed or replaced. Environment variables take precedence and are never copied into the credential store. Use **Remove saved key** to delete a stored credential; **Disable this session** only clears the current session. Keys are never written in settings, conversations, or backups. Hosted chat does not automatically include project files or local project instructions. OpenAI Code task is available after a separate **Allow OpenAI Code task** acknowledgement; a private conversation workspace works without project sharing, while an attached folder must be trusted and shared before the hosted agent can access its files or instructions. Anthropic remains chat and Plan only.

## Run

Codev's supported desktop front end is Avalonia, built on the shared cross-platform core:

| Front end | Platforms | Status |
|---|---|---|
| Avalonia desktop (`Codev.Avalonia`) | Self-contained release archives for Windows x64, Linux x64, macOS Apple silicon and macOS Intel | Cross-platform desktop app and parity target; packaged interaction smoke runs on all four RIDs; physical-keyboard and screen-reader checks remain open |

Build from source with the .NET 10 SDK. Ollama at `http://127.0.0.1:11434` is needed for local models and optional for hosted-only use.


The Avalonia app stores conversations under `avalonia-*.json`. Existing conversation data from older Codev builds remains on disk but is not automatically migrated. A manual test list is in [docs/avalonia-smoke-checklist.md](docs/avalonia-smoke-checklist.md).

```powershell
dotnet run --project .\Codev.Avalonia\Codev.Avalonia.csproj
```

It currently provides a Claude/Codex-inspired dark shell, locally persisted conversations, restores the last active conversation and model, sidebar conversation search, pin/archive, rename and permanent delete from each chat’s context menu, draft saving, installed-model discovery (including newly installed models when the picker opens), per-conversation context-size and output-style selection, an **Advanced** local-model dialog for per-chat temperature, top-p/top-k, presence/repeat penalties and a maximum generated-token limit; blank values keep Ollama defaults, and Load model defaults… reads values declared by the model when available, right-aligned user bubbles and left-aligned assistant responses, streaming chat through local Ollama, and a serial follow-up queue. The More menu groups secondary conversation/model actions, and the compact Context menu keeps project/file controls out of the main composer toolbar. Chat and read-only Plan modes are available per conversation; Plan mode requests a concise ordered implementation plan and cannot edit files or run commands. Avalonia Code task supports trusted attached folders or a dedicated per-conversation workspace, using loopback Ollama or OpenAI after its separate hosted project-context opt-in: the local Ollama model can list, read, and search project files, propose new files, complete replacements, or strict unified-diff patches for review, and request shell commands. Patches require exact context matches, show the patch and complete resulting file, and follow the same project permission mode, checkpoint, and concurrency checks as replacements. Code task can request optional test/lint verification; the selected command permission mode applies, its exit status and bounded output are shown, and failures allow at most two repair attempts before task edits and commands are blocked. Attached projects start in Ask mode, while private per-conversation workspaces start in Auto. Ask mode prompts before every change and command; Auto applies ordinary file changes with checkpoints and runs every shell command without approval unless its exact text has a saved deny rule. Commands use your account permissions and are not sandboxed to this project. Instruction-risk matches remain advisory and do not interrupt Auto mode. Codev provides per-conversation file history with reviewed restore and rewind, and runs approved commands with bounded output and a three-minute timeout. Type `/status` in the composer for a local snapshot of the provider, model, context, last matching-model request token use, conversation-summary state, project instruction files from the last request, mode, project, and queue state; it does not call a model and identifies remote Ollama endpoints. When a project is attached, Codev estimates bounded source-file context separately from chat history; after any Chat, Plan, or Code task request, Codev provides an in-memory view of the exact outgoing JSON request body, normalized messages, component sizes, rough token estimate, and provider-reported input and output token counts when available. Use **More → View last request context**. Attaching an untrusted project asks whether to trust that folder, trust its parent and descendants, or keep it untrusted; until trusted, automatic source context is off, though browsing and explicitly selected files remain available. Trust decisions live in a separate local JSON file, appear in `/status`, and can be revoked. Type `@` in the composer to find supported files in the attached project; selecting a result adds it to this conversation's context and inserts its project-relative path into the draft. Use the arrow keys and Enter or Tab to choose a suggestion, or Escape to dismiss it. **Browse files** filters and previews supported project text files read-only before adding a file to context. OpenAI and Anthropic API keys can be entered and persisted securely in the OS credential store or supplied through `OPENAI_API_KEY` / `ANTHROPIC_API_KEY`; environment keys take precedence. Codev discovers text models and streams replies. Hosted API usage may have separate charges from ChatGPT or Claude subscriptions. An explicit acknowledgement is required before connecting, and project files/tool results have a separate opt-in for hosted Code task. API keys are never written to settings, chat history, or backups. OpenAI Responses requests set `store:false`, but provider retention policies still apply. Settings also support a custom Ollama HTTP(S) server; remote servers require confirmation, and redirects are disabled. Remote Ollama is limited to chat; local Code task requires loopback Ollama, while OpenAI Code task sends requested tool context and results under the separate hosted opt-in. The model status changes to Ready when Ollama reports the selected model loaded, and shows elapsed load time, a timeout, or a connection error otherwise. The Memory view lists loaded Ollama models and server-reported size/VRAM use, with a confirmed unload action when the request queue is idle. New conversations inherit the active model and context size. Trust a local project folder to include bounded source excerpts automatically, or leave it untrusted and explicitly select up to 24 supported files; sensitive, excluded, unsupported, and outside-project files are rejected. Export a chat as Markdown, or export and additively import all chats as a portable JSON backup. Backup files include conversation text and project names/paths but exclude rollback checkpoint contents. Empty model options are filtered out. You can send another prompt while a response is running; each queued turn captures its model and context settings, queued turns survive restart and wait behind an explicit resume action, and unsent queued turns can be canceled. With an empty composer, the send button stops generation. The shell also follows output while streaming (paused when you scroll up), supports Enter-to-send with Shift+Enter for new lines, Ctrl/Cmd+N for a new conversation, Ctrl/Cmd+F to focus search, Ctrl/Cmd+L to focus the composer, Ctrl/Cmd+Shift+M to cycle Chat, Plan, and eligible Code task modes, Escape to stop generation, selectable Markdown responses without per-message Copy buttons, and a persistent dark/light theme toggle. Remaining work includes closing documented Avalonia/OpenCode coding-agent gaps, completing the manual review of Git and child-worktree actions, and native macOS/Linux interaction checks. Packaged desktop and CLI builds exist for Windows, Linux, macOS Apple silicon, and macOS Intel; the current public tag still lacks CLI archives. Child-worktree merge and recovery are implemented in Avalonia.

New conversations using an OpenAI model start in Code task with a private per-conversation workspace. Before the first Code task request, Codev asks permission to send prompts and tool/command results to OpenAI; attached project sharing remains a separate choice. File proposals and commands follow the selected project permission mode, so Auto applies file changes with checkpoints and runs commands without approval unless an exact saved deny rule matches. Commands use your account permissions and are not sandboxed to the project folder. Existing conversations retain their selected mode.

In Avalonia, queued prompts have compact per-message controls to prioritize one for the next run, edit or cancel it, open a side chat with the prompt returned to the composer, and turn queuing on or off for that conversation. The task checklist opens from **More** instead of occupying a permanent row. Saved hosted API keys are restored from the OS credential store at startup; the model picker reports when a saved key is available, while the separate hosted-request acknowledgement still resets each session. Connecting with a blank key field reuses the saved key.

The Advanced settings dialog also exposes optional OpenAI reasoning effort and response verbosity for supported GPT-5+ and o-series models. GPT-5.6+ and GPT-5.2 Pro also expose the documented pro reasoning mode. Controls default to the model setting and are stored per conversation, including queued turns. Available effort levels are selected by model family from OpenAI's published API guidance.

Reasoning-effort choices follow the selected model’s supported range; for example, GPT-5 offers Minimal, GPT-5.1 adds None, and newer families add xhigh/max where documented. The pro mode selector appears only for models that support it.

For supported OpenAI models, `/status` reports the active effort and verbosity overrides without making a model request.

Completed local Ollama replies in Avalonia show first-token latency, generated tokens per second, output token count, and model load time when the server reports it. Hosted model replies do not show locally estimated performance stats.

OpenAI Code task assistant replies show provider-reported input and output token totals summed across completed model requests in that turn. If the provider omits usage for any completed request, the affected total is labeled as a lower bound. The last-request context view continues to show usage for that individual request.

Avalonia also supports user and trusted-project Markdown prompt commands with named arguments, and manages saved prompt templates through `/template-…` suggestions and the **Prompt templates** button; see [custom slash commands](docs/custom-slash-commands.md) or type `/commands` in the composer.

User and trusted-project Markdown skills are available through slash suggestions and as on-demand tools in Code tasks. Codev also discovers standard OpenCode, Claude, and Agents `SKILL.md` locations; type `/skills` for the folders and see [the skills guide](docs/skills.md).

Fork an idle Avalonia conversation from its sidebar context menu to continue in a separate chat. The fork copies its history, settings, project link, and Codev-managed rollback checkpoints; busy conversations must finish and clear their queue first.

When you attach a folder that already has conversations, Codev offers to resume the most recent one for that project, attach the folder to the current chat, or start a new conversation.

For local Ollama models, the **Think** control requests a separate model thinking trace when supported. It appears collapsed beneath the answer, is saved with the message, and is excluded from later prompts. Each queued turn retains the conversation’s Think setting.

The Avalonia Git review dialog also supports selected textual-hunk stage/unstage/revert, stage/unstage all, and a preview-first discard of unstaged changes capped at 40 paths and 40,000 diff characters. It rechecks Git status and displayed diffs before applying and preserves staged content. The dialog also lets you attach an unsent comment to selected diff lines. Comments persist with the conversation, appear above the composer, can be removed before sending, and are included with the diff excerpt in the sent prompt and conversation backups. Child-worktree merge and recovery are implemented in Avalonia; manual interaction checks remain.

Type `/compact` or choose **Compact** in the conversation bar to ask the selected model to summarize older complete exchanges. **Summarize up to here** lets you choose a boundary: on a prompt, that prompt and later messages stay verbatim; on a reply, that reply is included in the summary and later messages stay verbatim. **Summarize from here** starts the range at a user prompt while preserving earlier exchanges. Review and edit the draft before applying; Codev preserves the original visible transcript and backups, and future prompts use the accepted summary plus the four newest exchanges (or your selected boundary). When Ollama reports that a request used at least 80% of an explicitly selected context limit, Codev offers compaction after the response; it never summarizes without review. If Ollama uses its model-default context, Codev warns that the context limit is unknown and points to manual compaction. **Full history** removes the summary from future prompts. These controls are available in Codev.

For compact project orientation, enable **Include repo map** beside the project context controls. Codev adds a bounded outline of safe project files and common declarations (up to 160 files and 8,000 characters), scoped to the trusted folder or explicitly selected files. It follows hosted-context consent, persists per conversation and queued turn, and shows an approximate maximum token cost before sending.

Each user prompt also has a **Rewind** action. It asks before removing that prompt and all later messages, restores the prompt to the composer, and leaves project files untouched; use Files history separately when you want to review a file restore.

Use **More → Export conversation…** to save Markdown or a standalone HTML transcript. The HTML file includes rendered Markdown/code/tables, its own offline styling, reviewed-file summaries and bounded Codev-managed diffs; it omits full local paths and checkpoint locations. Before saving, Codev offers a review of masked matches for common secret patterns and can redact the selected matches. Secret detection is heuristic and can miss credentials.

Use **More → Reading width** to choose 640, 800, or 960 pixels for the prompt composer and conversation content, or switch to **Full width**. The selected width persists across sessions. The default is 800 pixels.

Build a self-contained Windows app:

```powershell
dotnet publish .\Codev.Avalonia\Codev.Avalonia.csproj -c Release -r win-x64 --self-contained true
```

Run a one-shot headless Code task from a terminal (Ollama is the default provider):

```powershell
dotnet run --project .\Codev.Cli\Codev.Cli.csproj -c Release -- -p "Summarize the project structure" --model qwen3.6:35b-a3b --workspace .
```

Headless mode supports Ollama and OpenAI. OpenAI uses the key already saved in Codev's operating-system credential store and requires `--allow-hosted-data` for per-run consent. Non-loopback Ollama endpoints require `--allow-remote-endpoint`. Runs are read-only by default; pass `--allow-edits` to permit file changes and `--allow-commands` to permit shell commands. Shell commands run unsandboxed with the current user's account permissions. `--allow` enables both. Use `--help` for all options, or pipe a prompt to `codev` after publishing the CLI executable.

## Tests

```bash
dotnet test Codev.Tests/Codev.Tests.csproj                    # portable tests: any operating system
dotnet test Codev.Windows.Tests/Codev.Windows.Tests.csproj    # Windows-only PowerShell command tests
dotnet build Codev.Cli/Codev.Cli.csproj -c Release           # headless CLI: any operating system
```

GitHub Actions builds `Codev.Core`, the headless CLI and the Avalonia UI, and runs the portable tests on Windows, Linux and macOS for every push and pull request. The desktop workflow also publishes and launch-smokes self-contained Windows x64, Linux x64, macOS Apple silicon, and macOS Intel packages on pull requests and manual dispatch, without creating a release; only `v*` tag pushes publish the archives as a GitHub Release. Windows-only PowerShell command tests run on Windows. Packaged keyboard and UI interactions run on Linux and both macOS architectures; physical-keyboard and spoken screen-reader behavior remain unverified, and Linux AT-SPI support needs a compatible Avalonia version and Markdown renderer.

To run Codev with a separate local profile, set `CODEV_DATA_ROOT` to an absolute directory before starting the app. Codev stores its settings, conversations, projects, workspaces, and checkpoints under `<directory>/Codev`; hosted API keys and MCP OAuth tokens use a separate OS credential-vault namespace for that profile. Removing the retired WPF app does not delete its data, but WPF conversation history is stored separately and is not automatically migrated into Avalonia. For example, in PowerShell set `$env:CODEV_DATA_ROOT = 'C:\Codev-test-profile'` before launching `Codev.Avalonia.exe`.

## Repository layout

| Path | What it is |
|---|---|
| `Codev.Core/` | Shared logic with no UI dependency: conversations and persistence, Ollama endpoint checks and request options, Git integration, diffs, project context, the command runner and its shell selection |
| `Codev.Cli/` | One-shot headless Code task CLI for Ollama and OpenAI |
| `Codev.Tests/` | Portable tests for the core (plain .NET; runs on every OS) |
| `Codev.Windows.Tests/` | Windows-only tests for PowerShell command execution |
| `Codev.Avalonia/` | The cross-platform Avalonia desktop app |
| `docs/` | User guides and manual test checklists |
| `bench/` | A local-model coding benchmark (nine tasks checked against hidden tests) and its results; see [bench/README.md](bench/README.md) |
| `DESIGN.md` | The interaction model, roadmap and feature backlog |

## Local models

Codev discovers installed models from Ollama at startup and refreshes the picker when it opens. Two models from the original setup receive friendly labels; other installed Ollama tags, including Qwen3.8-27B, are listed automatically:

- `qwen3-coder-next-q2-24k`
- `qwen3-coder:30b`

Legacy Ollama tags for the same weights are recognized and shown under the same friendly model name.

For a measured comparison of these and other models on this project's kind of tasks (accuracy, speed and agent behaviour, run on one CPU-only laptop), see [comparison.html](comparison.html).

## Privacy

Prompts and project excerpts are sent to the configured Ollama endpoint; localhost is the default, and Codev confirms before switching to a non-local server. Chat mode reads a bounded set of common source/config files (up to 24 files and roughly 32,000 characters total), excluding build output, dependency folders, `.git`, and common secret files. Code task mode can read and search files within the selected workspace and apply a replacement according to the project permission mode; it saves a checkpoint first and refuses to overwrite files that changed after review. Symbolic links and junctions are excluded. Shell commands are not sandboxed and can access resources available to your user account.

## Design

See [DESIGN.md](DESIGN.md) for the interaction model and roadmap.

## License

MIT. See [LICENSE](LICENSE).
