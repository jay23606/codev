# Codev design and roadmap

## Product direction

Codev is a standalone coding workspace for local models. Today it ships as a Windows desktop app; macOS and Linux are planned through an Avalonia UI port (see roadmap section 7). It takes inspiration from the clear conversation experience of Claude Desktop and the project/thread, coding-agent, and review workflows of Codex, while keeping inference on the user's Ollama installation. It does not depend on VS Code.

The goal is to make project work feel organized and reviewable: each task has its own conversation and working state; the agent can inspect a chosen project, propose changes, run approved checks, and show exactly what happened; the user remains in control of file changes, commands, network access, and concurrent work.

This roadmap describes Codev's intended product scope. It does **not** claim feature parity with Claude or Codex, and features from their separate subscription/cloud services are out of scope unless they can be provided locally or through an explicitly configured integration.

## Current design

- **Left rail:** Codev identity, new conversation, search, pinned conversations, recents, settings, and local model status.
- **Top bar:** active conversation title, local privacy hint, model selector, and pin control.
- **Conversation canvas:** focused welcome state, task suggestions, and readable message history.
- **Composer:** multiline prompt, project-folder context control, and send action.
- **Visual language:** warm terracotta action color, rounded quiet controls, spacious conversation column, and low-noise navigation. Dark mode is the default; light mode is also available and remembered locally.

## Feature status

| Capability | Status in current build |
|---|---|
| Standalone Windows desktop app, no editor dependency | Done |
| Dark/light appearance with persistent preference | Done |
| Ollama model discovery and per-conversation model choice | Done for the three configured coding models |
| Ollama endpoint, per-conversation context size, and temperature | Configurable HTTP(S) endpoint defaults to localhost, warns before a changed non-local server; context choices run through the model's max and temperature is optional (0–2), preserving Ollama's model default when unset |
| Context use visibility | Last request's prompt/history token count from Ollama and selected context limit; bounded source-context estimate shown before sending |
| Streaming chat and independent persistent conversations | History and drafts use detached snapshots and flushed atomic writes; unreadable stores are preserved and recovery uses a separate file |
| Unsent composer drafts | Each conversation retains its own draft across chat switches and restarts; drafts are debounced into local history and included in portable backups |
| Find in the active conversation | Search message text, inspect matching excerpts, and jump directly to a result with Ctrl+Shift+F |
| Pinning and conversation search | Title and message text search, matching excerpts, project/archive scope, and debounced input are implemented |
| Rename, archive, and restore conversations | Done; permanently delete is available from the archive view |
| Choose a project folder and include bounded source/config excerpts | Select up to 24 project-relative chat context files, remove individual selections, or use safe bounded excerpts; token estimates and sent context use the same file limit |
| Attach local source files | Project files can be dragged onto the composer and added as context; attachment bytes are not sent until the user sends the prompt |
| File explorer and preview | Project context menu opens a safe text-file browser with filtering, read-only preview, and Add to chat context |
| Project list, project-specific instructions, and reusable knowledge | Project switcher, pinning, saved instructions, and bounded reusable project knowledge included in local model context; project metadata writes are atomic and unreadable JSON is preserved |
| Agent reads/searches files on request and edits files | Code task mode supports bounded list/read/search and reviewed create/replace operations on supported text files |
| Diff review, approve/reject, checkpoints, and undo | Whole-file before/after review, automatic checkpoints, per-conversation changed-file history grouped by path, scoped rollback/review, and unified text diffs; created files can be undone with a reviewed delete and redone from a checkpoint |
| Terminal commands, tests, and build output | Approval-gated PowerShell in project folder with 3-minute timeout, bounded output, elapsed progress, active-turn stop, and process-tree termination; no sandbox |
| Plan/progress view and stop/resume/retry controls | Read-only Plan mode, stop with partial output retained, tool status, retry/edit-resend, branch-from-message, and between-turn queue pause/resume implemented; request resume and richer plans remain |
| Conversation request scheduling | Serial Ollama queue with per-conversation running/queued indicators and pause/resume; true parallel agents and isolated worktrees remain pending |
| Git status, branch, staging, and review workflow | Local status, branch creation/switching, per-file staged/unstaged diff review, staging, unstaging, selected-diff questions, and reviewed local commits implemented; worktree merge/recovery remains |
| Markdown/code rendering, attachments, and session export | Markdown tables, lightweight syntax coloring, clickable web links, copyable code blocks, Markdown export, and JSON conversation backup/import; file/image/PDF attachments remain pending |
| Skills, MCP tools, recurring tasks, and notifications | Not implemented |

So far, we have finished the **foundation milestone**, including the dark/light theme addition. The larger design is still ahead.

## Roadmap

### 0. Foundation — complete

- Native standalone WPF shell; Ollama model list and streaming responses.
- Persistent conversations, per-chat model choice, sidebar search, and pins.
- Dark default and light theme, with the selection stored locally.
- Explicit folder selection and bounded read-only project excerpts sent to the local Ollama endpoint.

### 1. Projects and context — in progress

- [x] Project/workspace switcher with recent and pinned projects; each workspace maps to a chosen local folder.
- [x] Multiple conversations belonging to each project, plus ungrouped quick chats.
- [x] Project instructions, reusable knowledge notes, and context exclusions persist locally; instructions and knowledge are applied to project chat context, and knowledge is capped at 20,000 characters. Root `AGENTS.md` is also included through safe project-bounded access with a 20,000-character cap. User-selected source lists remain.
- [x] Deliberately select project-relative files for chat context; if none are selected, use the existing bounded safe excerpt.
- [x] Project file browser with filtering, safe read-only previews, and direct add-to-context; richer tree navigation and file diffs remain.
- [x] Approximate source-context token count versus the selected model context limit, with tooltip distinguishing it from Ollama's actual prompt count.
- [x] Per-project context exclusions for relative folders/files and filename patterns; these do not limit Code task file access.
- [x] Literal content search across chat-context-visible project files, with matching lines and selective add-to-context.
- Attach text files, images, and PDFs when the selected local model supports them; clearly show unsupported inputs.

### 2. Coding agent and review loop — in progress

- Separate **Chat**, read-only **Plan**, and **Code task** modes; plan-first workflows for larger tasks.
- [x] Explicit Code task mode with bounded agent tools for listing, reading, searching, and proposing new or updated supported text files.
- [x] Review proposed whole-file replacements side by side; approve or reject before applying.
- [x] Local checkpoints before edits and deletes; per-conversation changed-file history grouped by path and restore, including undo/redo of file creation or deletion. The review names the exact file replacement or delete before approval. Whole-file unified diff and side-by-side review are available; richer history browsing remains.
- [x] Agent can request a PowerShell command in the selected project; show the exact command and current-user access warning, require per-call approval, enforce a 3-minute timeout, capture bounded output, show elapsed progress, and stop through the active-turn control. Final verification summary remains.
- Commands are not sandboxed and may access anything available to the Windows account. Every call prompts; do not silently claim an edit, test, or command succeeded.

### 3. Sessions and parallel work

- [x] Hardware-aware serial Ollama request queue; each queued turn keeps its conversation, model, context size, temperature, mode, project, and selected context files, with running/queued indicators in the sidebar.
- [x] Sidebar session summaries show each conversation's model, latest prompt/context use, project, changed-file count, and last activity.
- [x] Conversation list serves as a session switcher; the active request can be stopped and queued requests can be canceled per conversation.
- [x] Pause/resume queued requests between model turns; an in-progress local generation completes before the queue pauses.
- More than one independent agent task at once, subject to available RAM and model-load limits; communicate when local hardware serializes inference.
- Optional Git worktree per task so parallel edits cannot collide in the same checkout.
- Per-session model, context use, project, progress, changed files, and last activity visible from the workspace/session list.
- [x] Optional completion toasts for responses that finish while the user is away from that conversation; toast clicks return to the conversation.
- [x] Queued turns and conversation history use flushed same-directory atomic JSON replacement; on restart queued turns stay paused behind **Resume saved queue**. A turn is removed from the recovery journal only after Codev confirms the removal was saved and just before execution, so an in-progress model/tool action is marked interrupted rather than replayed after a crash. Normal close waits for active work to cancel and saves the remaining queue for recovery.

### 4. Git and change management

- [x] Git status, local branch creation and switching from the project menu, with staged/working-tree indicators; both branch actions require a clean tree, and switching confirms before updating files.
- [x] Review staged/unstaged diffs per file; send a selected diff excerpt to the composer; commit review shows the exact staged tree and rechecks it before committing. Persistent comments on diff hunks remain pending.
- [x] Stage/unstage selected files and create a local commit after review and confirmation; Codev never pushes automatically.
- Compare and merge a task worktree into the chosen branch, or discard/recover it through a clearly reviewable action.
- Show recent checkpoints and make rollback scope explicit before restoring files.

### 5. Everyday assistant usability

- [x] Markdown headings, emphasis, lists, quotes, inline code, safe clickable HTTP(S) links, fenced code blocks with Copy and lightweight syntax coloring for common languages, scrollable tables, and message-level Copy available while a response is running.
- [x] Read-only Plan mode, stop generation while keeping partial response, visible agent-tool status, continue from an explicitly stopped answer, retry/edit-resend, and branch from an earlier message. Richer plans remain.
- [x] Shortcuts include Ctrl+N new conversation, Ctrl+F search, Ctrl+L focus composer, Ctrl+, settings, F2 rename, Esc stop, and F1 help. Adjustable chat/composer text size is available in Settings. Accessible focus order and broader shortcut customization remain.
- [x] Conversation rename, archive, restore, explicit permanent delete, Markdown export, and additive JSON conversation backup/import. JSON backups omit rollback checkpoint file paths and do not contain project files.
- [x] Per-conversation temperature (0–2) is stored with each conversation and queued request; unset leaves Ollama's model default untouched. Context size and selected source files are also per conversation.
- Ollama endpoint changes are validated, non-local destinations require confirmation, and HTTP redirects are disabled to prevent silently following requests to another host.

### 6. Extensibility and automation

- [x] User-level personal instructions plus root project `AGENTS.md`, project instructions, and knowledge notes provide user and project scope; executable skill workflows remain pending.
- MCP-compatible tools with per-server enablement, visible permissions, and logs; keep local-only use straightforward.
- [x] Reusable local prompt templates with bounded name/prompt lengths; choosing a template inserts it into the composer for review before sending.
- Optional recurring local jobs with an approval/review queue.
- Notification controls are available for conversation completions; a run history for background tasks remains.
- Optional cloud connectors only as opt-in integrations, clearly separated from the local-only default.

### 7. Cross-platform (macOS and Linux) — planned

The desktop UI currently targets `net9.0-windows` with WPF, which only runs on Windows. Most non-UI code now lives in `Codev.Core`, a plain `net9.0` library referenced by the Windows app. `Codev.Tests` also targets plain `net9.0`; WPF rendering and PowerShell command tests stay in `Codev.Windows.Tests`. A GitHub Actions matrix now builds the core and runs its tests on Windows, Linux, and macOS; hosted results will establish the portability baseline before any UI work.

Chosen approach: port the UI to [Avalonia UI](https://avaloniaui.net/), which is XAML-based (closest to WPF), MIT-licensed and free for commercial use, and has first-class Linux support including Wayland. Alternatives considered: Uno Platform (free, also covers web/mobile, but WinUI-style XAML is further from WPF), .NET MAUI (no official Linux support), Eto.Forms (native controls; harder to match Codev's custom Markdown/code rendering), and a web/Electron front end (a full UI rewrite). Avalonia's paid products (Accelerate tooling, XPF, premium support) are optional and not required; XPF is irrelevant because the UI is being rewritten rather than run as-is.

- [ ] Verify portability first: confirm the new `Codev.Tests` CI matrix passes on Linux and macOS before any UI work, and record any platform-specific failures.
- [x] Split non-UI code into a shared `Codev.Core` library targeting plain `net9.0`; the WPF app and future Avalonia app both reference it.
- [ ] Replace the hard-coded `powershell.exe` command tool with a shell abstraction: PowerShell on Windows, the user's `$SHELL` (or `bash`/`sh`) on macOS and Linux, and a configurable override. The approval, timeout, bounded-output and process-tree-kill behavior stays identical on every OS, and the prompt must name the shell actually used.
- [ ] Use `Environment.SpecialFolder.LocalApplicationData` for the data folder instead of `%LOCALAPPDATA%\Codev`, and migrate nothing silently: an existing Windows folder is left where it is.
- [ ] Make the "open folder" action per-OS (`explorer`/`open`/`xdg-open`) and reword the Git-not-found message so it is not Windows-specific.
- [ ] Port the UI to Avalonia. The riskiest parts are the rich Markdown renderer with copy buttons, syntax coloring, scrollable tables, drag-and-drop of files onto the composer, and theming (dark default, light option).
- [x] Add macOS and Linux core-test jobs to CI alongside Windows. Per-OS desktop publishing (`win-x64`, `osx-arm64`/`osx-x64`, `linux-x64`) remains pending the Avalonia port.
- [ ] Decide whether to keep the WPF app alongside Avalonia during the transition or retire it once Avalonia reaches parity.

### 8. Claude-inspired features worth adding

These come from a review of Claude Code and the Claude apps (checked 2026-09-27; see references below). Only features that work with a local Ollama model, or can be done locally, are listed; cloud and subscription services (Cowork, Remote Control, cloud routines, Slack integration) are out of scope. Grouped by how much work they should take. Effort is a rough guess, not a measured estimate.

**Small (mostly composer/prompt plumbing):**

- Slash commands in the composer (`/plan`, `/code`, `/clear`, `/model`, `/compact`, `/export`), with autocomplete. Mirrors Claude Code's built-in commands and reuses existing actions.
- Permission modes for the command tool: ask every time (today's behavior, the default), auto-approve read-only commands, or a per-project allowlist of exact commands. Never auto-approve file writes without review; keep the "not sandboxed" warning visible. Design notes from Claude Code's [permission system](https://code.claude.com/docs/en/permissions): evaluate rules in the order deny, then ask, then allow, so a deny always wins; match compound commands (`a && b`, pipes) piece by piece, so an allowed `git status` cannot smuggle in a second command; treat output redirects (`> file`, `tee`) as file writes and check their targets; resolve symlinks before judging a path; keep `.git` and Codev's own data folder as protected paths that are never auto-approved. Claude Code itself notes that command-text rules can be bypassed (for example by `sh -c`), so real enforcement needs an OS-level sandbox, which Codev does not have; say so in the UI.
- Path-scoped project instructions: like Claude Code's `.claude/rules/` files with path filters, let a project instruction apply only when files matching a pattern (for example `*.cs`) are in context, so it does not cost context on unrelated work.
- Queue a follow-up message while a response is running. Codev already has a request queue; this is mostly a composer change. Follow Claude Code's rule that a message joining a running turn is not its own rewind point.
- Edit format choice for small local models: let the agent propose a diff/patch or a whole-file replacement, whichever is cheaper in tokens for the change. Aider supports [several edit formats](https://aider.chat/docs/) for this reason. Today Codev only reviews whole-file replacements, which costs many tokens for small edits.
- Output styles: saved system-prompt presets (concise, explanatory, code-only) chosen per conversation, like Claude's styles.
- `@`-mention project files in the composer to add them to context, as an alternative to the file picker and drag-and-drop.
- Fuller context breakdown: show what is using the context window (system prompt, instructions, knowledge, files, history) rather than one token count.

**Medium:**

- Context compaction: summarize older turns in place when a conversation nears the model's context limit (like `/compact`), showing the summary and keeping the original messages recoverable in the archive/export.
- Rewind: restore a conversation and its files to an earlier message. Builds on existing checkpoints and branch-from-message. Modeled on Claude Code's [checkpointing](https://code.claude.com/docs/en/checkpointing): one checkpoint per prompt that starts a turn, and a menu offering *restore code and conversation*, *restore conversation only*, *restore code only*, and *summarize from here* / *summarize up to here* (context compaction targeted at part of the history; the original messages stay in the saved conversation and files on disk are untouched). Keep a bounded number of checkpoints (Claude Code keeps 100 per session and sweeps old ones after about 30 days) and say clearly when a restore fails because old snapshots were cleaned up. Documented limits to copy honestly: changes made by shell commands are not tracked, edits made outside Codev are not tracked, symlinked and hard-linked files are skipped with a visible warning, and checkpoints are not a substitute for Git.
- Skills: folders of markdown instructions (optionally with scripts) that the model loads on demand, for user and project scope. This is the shape of the pending "executable skill workflows" item; scripts run through the same approval-gated command tool. Design notes from Claude Code's [extension guide](https://code.claude.com/docs/en/features-overview): only each skill's name and short description sit in context until it is used, so many skills stay cheap (this matters more on small local context windows); a skill can be marked user-invoke-only (`/name`) so the model never triggers one with side effects on its own; the same name at project and user scope resolves to one winner by a fixed priority. Keep always-loaded instructions short (Claude Code suggests under about 200 lines) and move reference material into skills.
- Local codebase search with embeddings: index project files with an Ollama embedding model so "find where X is handled" retrieves relevant files without the user hand-picking them, complementing the literal search that exists today. Index stays on disk under the data folder, is rebuilt incrementally, and is opt-in because it needs an embedding model and disk/CPU time.
- Repo map: a compact outline of a project's files and top-level symbols placed in context, as [Aider's repository map](https://aider.chat/docs/) does, so a small model knows the layout without reading every file. Start with a simple file tree plus per-language symbol extraction; language-server integration (Claude Code's "code intelligence") is a larger later step.
- Test/lint loop: after an approved edit, optionally run a configured test or lint command and feed failures back to the model for another attempt, capped at a small number of rounds, with each command still going through approval (or an allowlist entry). Aider does this automatically; Codev should show each round and never claim success without a passing run.
- Structured tool calls: use Ollama's native tool calling and JSON-schema structured outputs ([docs](https://docs.ollama.com/capabilities/structured-outputs)) instead of parsing free text where the model supports them, so plans, tool arguments and summaries are validated before use, with a text fallback for models without support.
- Loaded-model awareness: show which models Ollama currently has in memory and their RAM/VRAM use, and offer to unload one, to back up the "hardware-aware concurrency" principle with real numbers instead of guesses.
- Auto-suggested memory: after a task, propose short notes to save into project knowledge (accepted or rejected by the user), rather than saving anything silently.
- Show model reasoning for Ollama models that support a thinking mode, as a collapsible block, and let the user turn it off per conversation.
- Hooks: user-configured commands that run before or after tool actions (for example, format after each approved edit, run tests before a commit). Each hook is visible in settings, logged in the run history, and disabled by default.
- Headless mode (`codev -p "prompt"` with piped input) once the shared core library exists, for scripting and CI. Read-only unless explicitly allowed.
- Preview for HTML/SVG/Markdown code blocks, shown in a sandboxed pane with scripts and network access off (a local, simplified take on Claude's artifacts).

**Large / later:**

- Custom subagents with their own instructions and tool limits, run through the serial queue; depends on the parallel-session and worktree work in section 3. Claude Code's [subagents](https://code.claude.com/docs/en/sub-agents) are markdown files with a small header (name, description, allowed tools, model, maximum turns, optional isolated worktree) whose body is the system prompt, plus read-only built-ins for exploring and planning. A local version fits well: a cheap small model can do read-only exploration and return a summary to the main conversation, keeping the big model's limited context clean. Because local inference is serialized, subagents would run one after another; say so instead of implying parallel speed-up.
- Code intelligence through language servers (go to definition, references, live type errors after an edit) as an opt-in per-language add-on; a larger job than the repo map but it lets a small model check its own edits.
- Plugins: a way to bundle skills, hooks, subagents and MCP servers into one installable folder, with names scoped per plugin so they cannot collide. Only worth it after those pieces exist.
- MCP client support (already listed in section 6), including per-server enablement, visible permissions and logs.
- Opt-in web search/fetch tool, clearly marked as leaving the machine and off by default, consistent with the local-first principle.

**Voice (medium, fully local):** dictation into the composer and optional spoken replies do not need a hosted service. Two routes, and the best design probably uses both behind one small `ISpeechToText` / `ITextToSpeech` interface so the composer does not care which is active:

1. *Built-in OS speech* (no download, smallest footprint): Windows has `System.Speech` and `Windows.Media.SpeechRecognition` (Windows-only APIs; accuracy is usually lower than Whisper, and some reports mention lag); macOS has `SFSpeechRecognizer` and the system speech synthesizer, which can run on-device only if Dictation is enabled and the language is downloaded ([Apple docs](https://developer.apple.com/documentation/speech/sfspeechrecognizer)); Linux has no comparable built-in recognizer (its speech synthesis is normally `speech-dispatcher`, which would need checking per distribution). The OS voice-typing shortcuts (for example Win+H on Windows) can also type into Codev's composer for free, but whether they stay on the machine depends on the OS and its settings, so Codev should not describe them as private without checking. Codev must confirm per platform that a given built-in engine is actually offline before labeling it "stays on this machine".
2. *A bundled local model* (same behavior on every OS, better accuracy, costs a download and RAM): speech-to-text through [whisper.cpp](https://github.com/ggml-org/whisper.cpp), which has a .NET binding ([Whisper.net](https://github.com/sandrohanea/whisper.net)) and builds for Windows, macOS and Linux; text-to-speech can run offline through [Piper](https://github.com/rhasspy/piper) or the operating system's own speech engine. The trade-offs are a model download (opt-in, with the size shown before anything is downloaded), CPU/RAM competing with the Ollama model, and per-OS microphone access. Start with push-to-talk dictation that fills the composer for review before sending; spoken replies and hands-free mode come later. The audio never leaves the machine.

**Deliberately not planned:** cloud-hosted agents and phone/browser remote control, because they need a hosted service Codev does not have.

### 9. Codex- and OpenCode-inspired features worth adding

A second review pass, this time of OpenAI's Codex (CLI and desktop app) and the open-source [OpenCode](https://opencode.ai/docs/) agent (checked 2026-09-27; sources below). Items already in sections 1 to 8 are not repeated; where these tools sharpen an existing item, the item is refined here. As before, cloud-hosted parts (Codex cloud tasks, OpenCode's hosted share links) are out of scope, and effort ratings are rough guesses.

**From Codex**

- *Richer review pane* (medium). Codex's [review pane](https://learn.chatgpt.com/docs/code-review?surface=app) works at three levels (whole diff, per file, per hunk) for stage, unstage and revert; Codev's diff review is per file today. It also lets you pick what to review: uncommitted changes, the assistant's **last turn**, a branch against a base, or a single commit. And you can attach a comment to a diff line, then send a follow-up message so the model treats those comments as guidance. This turns the "persistent comments on diff hunks remain pending" item in section 4 into a concrete design: comments live with the conversation, are listed in the composer before sending, and are cleared once addressed.
- *A dedicated `/review` pass* (small to medium). Run a separate, read-only model pass over the uncommitted changes, a commit or a branch and list findings by priority, before the user commits. It should use a review-specific prompt and never edit files. Quality depends on the local model; label it as a second opinion, not a guarantee.
- *Separate sandbox level from approval policy* (large, research first). Codex treats these as two settings: what the process is physically allowed to do (read-only, workspace-write, full access) and when it must ask. Codev only has the second half (every command prompts) and no sandbox, and its roadmap already says commands are not sandboxed. Codex's own [Windows sandbox write-up](https://openai.com/index/building-codex-windows-sandbox/) shows this is hard to get right on Windows, and macOS and Linux each need a different mechanism, so treat any sandbox as a research item per platform and never describe a mode as safe until it is tested. One rule worth adopting regardless: refuse the combination "never ask" plus "full access", and let the user lock that out in settings.
- *`/init` to draft project instructions* (small). Ask the model to inspect the project and propose an `AGENTS.md` (build and test commands, layout, conventions), shown as a reviewed file creation like any other edit, never written silently. Pairs with the existing root `AGENTS.md` support.
- *`/status`* (small). One command that prints the active model, context size, temperature, mode, project, queue state, instruction files loaded, and any permission mode in force, so "why did it do that?" has a visible answer. Extends the existing context display.
- *Automations refined* (large, after worktrees). [Codex automations](https://codex.danielvaughan.com/2026/04/08/codex-desktop-automations/) can be triggered on a schedule, manually, or by webhook, run each time in a dedicated Git worktree so unattended runs cannot collide with the user's checkout, and deliver results into a triage inbox: a run with findings appears as a reviewable item to approve, request changes on or reject, and a run with nothing to report archives itself. Copy the worktree-per-run and the inbox for the existing "recurring local jobs" item; skip webhooks at first, since a listening server on the user's machine is a security surface (localhost only, with a token, if ever added). Local models make unattended runs slow, so show queue position and expected wait.
- *Resume by project and fork a whole conversation* (small). `codex resume` reopens a recent session for the current repository, and sessions can be forked to try another approach. Codev already has per-project conversation lists and branch-from-message; add "continue the most recent conversation for this project" on opening a project, and a "fork this conversation" action that copies it and its checkpoints.

**From OpenCode**

- *Agent definition files* (medium). [OpenCode agents](https://opencode.ai/docs/agents/) are markdown files (global `~/.config/opencode/agents/` or per-project `.opencode/agents/`) with a header for model, temperature, a maximum number of steps, and a per-tool permission of ask, allow or deny; the body is the system prompt. Use one such format for both Codev's future custom subagents (section 8) and its Plan and Code modes, so a mode is just an agent with permissions. The step limit is especially useful on local models, which can loop.
- *Loop detection* (small). OpenCode's permission list includes a `doom_loop` key (from its config docs as summarized in search results; I have not read what it does exactly, so verify before copying). The idea worth testing in Codev is a guard that stops or asks when the model repeats the same tool call with the same arguments several times, since small local models get stuck this way, together with the step limit above.
- *Child sessions for delegated work* (medium, with subagents). In OpenCode a delegated task runs as its own child session with fresh context, returns a result to the parent, and can be opened, inspected and resumed, with keys to move between parent and child. Show a Codev subagent the same way: a collapsible child conversation under the parent, not an invisible side effect. Keep the depth limit low (OpenCode's default is 1 level, meaning subagents cannot start more subagents).
- *Small helper model for housekeeping* (small). OpenCode runs hidden system agents for compaction, titles and summaries. Codev can let the user pick a small, fast Ollama model to write conversation titles and compaction summaries, so the large coding model is not loaded or interrupted for chores. On limited RAM, offer "use the same model" as the safe default.
- *Read-only external research helper* (later). OpenCode ships an "Explore" subagent for the codebase and a "Scout" for external documentation. The first maps to Codev's read-only exploration in section 8; the second only makes sense together with the opt-in web tool and must be clearly marked as leaving the machine.
- *Custom commands as files* (small). Reusable prompts stored as markdown, per user or per project, with named argument placeholders, invoked as `/name`. Codev's saved prompt templates are the same idea; add file-based project templates (shareable through Git) and argument placeholders, and list them in the slash-command menu.
- *Tab to switch Plan and Code* (small). OpenCode toggles its Plan and Build agents with one key. Codev already has the modes; a single keyboard shortcut and a clear visible indicator are cheap and reduce accidental edits.
- *Themes and key bindings* (small to medium). Custom themes and rebindable keys; Codev has dark and light plus fixed shortcuts, and section 5 already lists broader shortcut customization as remaining.
- *Formatter after edits* (small, part of hooks). OpenCode can run a configured code formatter after it edits a file. In Codev this is the simplest, safest first hook: run one named formatter on the changed file, show its output, and record it in the checkpoint so undo covers it.
- *Local server and multiple front ends* (large, architectural). OpenCode runs its agent as a local server that the terminal UI, desktop app, IDE extensions and an SDK all talk to over HTTP, and it can run headless or be attached to remotely. For Codev this could be how the Avalonia app, a future command-line mode and an editor extension share one core, but it adds a network surface (bind to localhost only, require a token) and a lot of design work. Decide after the shared `Codev.Core` split in section 7 shows what actually needs sharing.
- *Self-contained conversation export* (small). OpenCode's `/share` makes a hosted link, which Codev will not do. The local equivalent is exporting a conversation as a single HTML file (rendered Markdown, code blocks, diffs) that can be emailed or committed, alongside the existing Markdown and JSON exports; strip file paths and offer to redact secrets first.

**Deliberately not planned:** Codex cloud tasks and hosted share links, both of which need a service Codev does not run.

Sources for this section: Codex's [CLI reference](https://learn.chatgpt.com/docs/codex/cli), [review pane](https://learn.chatgpt.com/docs/code-review?surface=app), [automations write-up](https://codex.danielvaughan.com/2026/04/08/codex-desktop-automations/) (a third-party article, not OpenAI documentation) and [app overview](https://openai.com/index/introducing-the-codex-app/); OpenCode's [docs](https://opencode.ai/docs/), [agents page](https://opencode.ai/docs/agents/) and [config page](https://opencode.ai/docs/config/).

## Product principles

- **Local first:** the default inference endpoint is Ollama on localhost; show clearly if a request would leave the machine.
- **Review before change:** show a diff and make rollback possible before and after file writes.
- **Bounded by workspace:** tools operate within the user-selected project unless an explicit permission changes that boundary.
- **Visible work:** expose plans, tool calls, command output, changed files, and completion evidence.
- **Honest capability:** distinguish simple chat from agent tasks, unsupported model features, queued work, and completed work.
- **Hardware-aware concurrency:** parallel sessions must not imply that several large models can fit in RAM simultaneously; schedule/swap safely and keep GPU acceleration off unless the user enables it.

## Reference workflows

The feature comparison is based on official product documentation checked on 2026-09-27:

- OpenAI describes Codex desktop threads grouped by projects, parallel agents, diff review, worktrees, skills, and scheduled automations in its [Codex app overview](https://openai.com/index/introducing-the-codex-app/). Windows execution also needs careful workspace boundaries and approvals; see [Codex's Windows sandbox design](https://openai.com/index/building-codex-windows-sandbox/) and [Codex safety controls](https://openai.com/index/running-codex-safely/).
- Anthropic describes Claude Desktop projects with separate histories, reusable project knowledge and instructions, and starring/archive organization in its [Projects guide](https://support.anthropic.com/en/articles/9517075-what-are-projects) and [project management guide](https://support.anthropic.com/en/articles/9519177-how-can-i-create-and-manage-projects).
- Anthropic's current Windows desktop supports Claude Code and Cowork on eligible plans; Cowork's visual agent can work with local files and long-running/parallel tasks in an isolated VM. See [Claude Desktop availability and Cowork](https://support.claude.com/en/articles/10065433-install-claude-desktop) and [Windows deployment requirements](https://support.claude.com/en/articles/12622703-deploy-claude-desktop-for-windows).
- Claude Desktop also exposes local extensions and connected tools; see Anthropic's [connector overview](https://support.anthropic.com/en/articles/11817150-connect-your-tools-to-unlock-a-smarter-more-capable-ai-companion).

- Additional references for section 8: Claude Code's [checkpointing](https://code.claude.com/docs/en/checkpointing), [permissions](https://code.claude.com/docs/en/permissions), [memory](https://code.claude.com/docs/en/memory) and [subagents](https://code.claude.com/docs/en/sub-agents) pages; [Aider](https://aider.chat/docs/) for repo maps, edit formats and the lint/test loop; [Ollama's structured outputs](https://docs.ollama.com/capabilities/structured-outputs) and API for tool calling, thinking, vision and embeddings; and [whisper.cpp](https://github.com/ggml-org/whisper.cpp) plus [Piper](https://github.com/rhasspy/piper) for local voice. Continue.dev, another local-model coding tool, was reported discontinued in June 2026, so it is not used as a reference.
- Claude Code's [overview](https://code.claude.com/docs/en/overview) and [extension guide](https://code.claude.com/docs/en/features-overview) describe the memory files, skills, hooks, subagents, checkpoints and rewind, permission modes, MCP, and scheduled tasks that section 8 draws from.
- Avalonia's licensing is described on its [GitHub page](https://github.com/AvaloniaUI/Avalonia) and [pricing page](https://avaloniaui.net/pricing); the framework is MIT-licensed.

We use these products as workflow references, not as an exhaustive checklist or a promise to reproduce paid/cloud features. Product capabilities change, so revisit these references when planning each milestone.

## Implementation choices

- C# and modern .NET WPF provide a native Windows desktop app independent of VS Code. Avalonia UI is the planned path to macOS and Linux (section 7); shared logic should move into a UI-free core library so both front ends use it.
- Ollama's HTTP API provides local model discovery and inference, reusing the user's existing model files and CPU-only runtime configuration.
- Conversations and UI preferences are stored locally under `%LOCALAPPDATA%\Codev` on Windows (the per-user local application data folder on other platforms once ported); move from JSON to versioned SQLite when project/task state needs transactions, indexing, or migrations.
