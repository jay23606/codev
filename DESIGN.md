# Codev design and roadmap

## Product direction

Codev is a standalone coding workspace for local models. Today it ships as a Windows desktop app; macOS and Linux are planned through an Avalonia UI port (see roadmap section 7). It takes inspiration from the clear conversation experience of Claude Desktop and the project/thread, coding-agent, and review workflows of Codex, while keeping inference on the user's Ollama installation. It does not depend on VS Code.

The goal is to make project work feel organized and reviewable: each task has its own conversation and working state; the agent can inspect a chosen project, propose changes, run approved checks, and show exactly what happened; the user remains in control of file changes, commands, network access, and concurrent work.

This roadmap describes Codev's intended product scope. It does **not** claim feature parity with Claude or Codex, and features from their separate subscription/cloud services are out of scope unless they can be provided locally or through an explicitly configured integration.

## Current design

- **Left rail:** Codev identity, new conversation, search, pinned conversations, recents, settings, and local model status.
- **Top bar:** active conversation title, local privacy hint, model selector, and pin control.
- **Conversation canvas:** focused welcome state, task suggestions, right-aligned user bubbles, and left-aligned assistant messages without role labels.
- **Composer:** multiline prompt, project-folder context control, and send action.
- **Visual language:** warm terracotta action color, rounded quiet controls, spacious conversation column, and low-noise navigation. Dark mode is the default; light mode is also available and remembered locally.

## Feature status

| Capability | Status in current build |
|---|---|
| Standalone Windows desktop app, no editor dependency | Done |
| Dark/light appearance with persistent preference | Done |
| Ollama model discovery and per-conversation model choice | Done; curated labels for the original three plus automatic discovery of all other installed tags, refreshed when the model picker opens |
| Avalonia conversation layout and context size | User messages use right-aligned bubbles; assistant messages stay left aligned without role labels; per-chat context choices are model-capped and persisted |
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

**Goal:** the same Codev on Windows, macOS and Linux, with no behavior differences a user has to know about except where the operating system itself differs (shell, folders, microphone).

**Where things stand.** The desktop UI targets `net9.0-windows` with WPF, which only runs on Windows. Most non-UI code now lives in `Codev.Core`, a plain `net9.0` library that the Windows app references. `Codev.Tests` also targets plain `net9.0`; WPF rendering and PowerShell command tests stay in `Codev.Windows.Tests`. The hosted CI matrix builds the portable suite on Windows, Linux and macOS, plus Windows-only renderer and PowerShell tests. The Avalonia UI has the dark Codev-style shell, local conversation list/search/pin/archive/drafts, Ollama model discovery (including arbitrary installed tags), per-conversation context size, right-aligned user bubbles, left-aligned assistant messages without role labels, streaming local chat with follow-to-latest, Enter-to-send, basic new/search/focus/stop keyboard shortcuts, selectable Markdown and message copy, and persistent dark/light theme. It still lacks project tools, Plan and Code task modes, reviewed edits, Git, export, backups and settings. Keep WPF as the usable app until Avalonia reaches feature and interaction parity; do not present the Avalonia prototype as a replacement.

**Decision: port the UI to [Avalonia UI](https://avaloniaui.net/).** It is XAML-based (closest to WPF, so `MainWindow.xaml` ports with the least change), MIT-licensed and free for commercial use, and has first-class Linux support including Wayland. Its paid products (Accelerate tooling, premium support) are optional and not needed; XPF, which runs existing WPF apps unchanged, is irrelevant because the UI is being rewritten. Alternatives considered and rejected:

| Option | Why not |
|---|---|
| Uno Platform | Free and broad (web, mobile), but WinUI-style XAML is further from WPF |
| .NET MAUI | No official Linux support |
| Eto.Forms | Native controls; hard to match Codev's custom Markdown and code rendering |
| Web/Electron front end | A complete UI rewrite; only worthwhile if a browser version is also wanted |

**Steps, in order.** Each is a separate, reviewable change.

| # | Step | Status | Done when |
|---|---|---|---|
| X1 | Run `Codev.Tests` on Linux and macOS in CI, with the UI project excluded | Done; all 3 OS jobs pass (129 tests each) | CI runs on all three OSes; every failing test is listed in this document with its cause |
| X2 | Move all non-UI code into a `Codev.Core` library targeting plain `net9.0`; the WPF app references it | Done | The WPF app behaves as before; `Codev.Core` has no reference to any UI assembly |
| X3 | Replace the hard-coded `powershell.exe` with a shell abstraction: PowerShell on Windows, the user's `$SHELL` (falling back to Bash/sh) elsewhere, with a `CODEV_SHELL` override | Done; portable shell-selection and command-execution tests pass on all 3 OSes | Approval, 3-minute timeout, bounded output and process-tree kill behave identically on every OS, and the approval prompt names the shell actually used |
| X4 | Use `Environment.SpecialFolder.LocalApplicationData` for the data folder | Done; stores and checkpoints already use the OS-specific LocalApplicationData path, preserving the existing Windows `%LOCALAPPDATA%\Codev` location | Windows keeps its existing folder untouched; nothing is migrated silently |
| X5 | Per-OS "open folder" (`explorer` / `open` / `xdg-open`); reword the Git-not-found message so it is not Windows-specific | Implemented; command selection has portable tests; awaiting hosted matrix for the UI hook | No Windows-only wording or calls remain outside the shell and folder abstractions |
| X6 | Avalonia UI port. Match the established Codev layout, then port core chat and workflows in parity gates | In progress; local chats, search/pin/archive, Ollama discovery/context/streaming/stop, chat-bubble layout, copy, composer and basic keyboard shortcuts, and dark/light theme build in the OS matrix; feature parity is not reached | Each parity gate builds on all supported OSes and has an explicit interaction checklist before WPF is retired |
| X7 | CI matrix and per-OS publishing (`win-x64`, `osx-arm64`, `osx-x64`, `linux-x64`) | Core-test matrix done; per-OS app publishing waits on X6 | Each build starts and passes tests on its OS |

**Open decision:** keep the WPF app alongside Avalonia during the transition, or retire it at parity. Recommended: keep it until Avalonia passes the same manual checklist, then retire it, so there is never a release with a regression on Windows.

### 8. Feature backlog

This section is the single backlog for features drawn from studying Claude Code and the Claude apps, OpenAI Codex, OpenCode, Aider, Cline, Gemini CLI, Zed, Cursor, Roo Code, Goose and local chat apps such as Msty (checked 2026-09-27; see Sources). Only features that work with a local Ollama model, or can be done locally, are included. Sizes are rough guesses: **S** is a few files, **M** is a feature with its own UI and tests, **L** is a multi-part project.

**Rules for every item below**

1. Follow the product principles: local first, review before change, visible work, honest capability. In particular, nothing may claim an edit, command or test succeeded without evidence, and no feature may send data off the machine unless the user turned on an explicit opt-in that says so.
2. Anything that writes files or runs commands goes through the existing review, checkpoint and approval paths. No item adds a silent write.
3. Anything downloaded (models, voices) is opt-in and shows its size first.
4. New behavior ships with tests in `Codev.Tests` where it is pure logic, and with an updated row in the feature status table.
5. Small local models have small context windows: prefer loading things on demand over always-on context, and show what each feature costs.

**Recommended build order**

| Phase | Items | Why first |
|---|---|---|
| 0. Validate | V1, V2, V4 | Cheap, and the results change what the rest should be (section 9) |
| 1. Cheap, high value | X1, D1, E2, A1, A6, B1, B4, C5 | Small changes that make the agent safer and easier to see into |
| 2. Recover and review | C3, A2, C1, C2, B3 | Rewind, compaction and better review make long agent tasks survivable |
| 3. Smarter context | A3, A5, A8, B2, B6, B9, D2 | Cuts wasted tokens; skills and structured calls make agents more reliable, and folder trust (B9) must be in place before any project-provided skill, hook, agent or command file is read |
| 4. Extend | D3, D4, D6, B12, A9, E4, E6, E7 | Extension points, once the basics are solid |
| 5. Parallel and unattended | D5, D8, B7, E8, D10 | Depends on worktrees, the shared core, and sandbox research |
| Anytime | A4, A7, A10, A11, A12, B5, B8, B10, B11, C4, C6, C7, D7, D9, E1, E3, E4, E5, E9, E10, E11 | Independent; pick up between phases (B5 needs B1 and D7 needs D2 to D6) |

X-items are from section 7. Items in phase 1 to 3 do not depend on the Avalonia port; E7 needs `Codev.Core` (X2), and E8 is a decision after X2.

#### A. Context and memory

| ID | Feature | Size | Depends on | Tier |
|---|---|---|---|---|
| A1 | Context breakdown | S | none | Core |
| A2 | Compaction and summarize | M | C3 (shares its UI) | Core |
| A3 | Instruction activation modes (path-scoped) | S | none | Later |
| A4 | Suggested memory notes | M | none | Later |
| A5 | `/init` to draft `AGENTS.md` | S | D1 | Later |
| A6 | `/status` | S | D1 | Core |
| A7 | `@`-mention files in the composer | S | none | Core |
| A8 | Repo map | M | none | Later |
| A9 | Embeddings search | M | A8 helpful, not required | Speculative |
| A10 | Small helper model for chores | S | none | Later |
| A11 | Conversation recall | S | none | Speculative |
| A12 | Multi-folder projects | M | none | Later |

- **A1 Context breakdown.** Replace the single token count with a breakdown of what fills the window: system prompt, personal instructions, project instructions, knowledge notes, `AGENTS.md`, selected files, history. Highest-value part: show it *before* sending. Add a *show the final prompt* view that displays exactly what will be sent to the model once all instructions are combined; Gemini CLI's `/memory show` does this for its instruction files.
- **A2 Compaction and summarize.** When a conversation nears the model's context limit, offer to summarize older turns in place, and offer "summarize from here" / "summarize up to here" on any message. The original messages stay in the saved conversation and exports, files on disk are untouched, and the summary is shown and editable. Never compact silently. Modeled on Claude Code's [checkpointing and `/compact`](https://code.claude.com/docs/en/checkpointing).
- **A3 Path-scoped instructions.** A project instruction can carry a file pattern (for example `*.cs`) and is included only when a matching file is in context. Modeled on Claude Code's path-specific rules; saves context on unrelated work. Generalize this to the four activation modes [Cursor rules](https://cursor.com/docs/rules) use: *always*, *when the model judges the description relevant*, *when a file matching a glob is in context*, and *manual* (only when the user `@`-mentions the rule). Nested `AGENTS.md` files in subdirectories combine with their parents, with the more specific one winning. Keep each rule short (Cursor suggests under about 500 lines) and reference files rather than copying their content, so rules do not go stale.
- **A4 Suggested memory notes.** After a task, propose short notes for project knowledge (build command, convention, correction the user made). The user accepts, edits or rejects each; nothing is saved silently. Respects the 20,000-character knowledge cap. An optional structured layout is possible, as in [Cline's Memory Bank](https://docs.cline.bot/features/memory-bank) (project brief, tech context, active context, progress). Cline reads every one of those files at the start of every task, which is costly on a small local context window, so Codev should load only a short summary, pull the rest on demand, and show the cost in A1.
- **A5 `/init`.** The model inspects the project and proposes an `AGENTS.md` (build and test commands, layout, conventions). It arrives as a normal reviewed file creation. Pairs with existing root `AGENTS.md` support.
- **A6 `/status`.** One place that prints: model, context size and use, temperature, mode, project, queue state, instruction files loaded, permission mode, and whether the endpoint is local. Answers "why did it do that?".
- **A7 `@`-mentions.** Typing `@` in the composer completes project-relative file names and adds the file to context, honoring exclusions and the 24-file limit. An alternative to the picker and drag-and-drop.
- **A8 Repo map.** A compact outline of the project (file tree plus top-level symbols per language) that can be included in context so a small model knows the layout without reading every file. Start with the file tree and simple per-language symbol extraction. Modeled on [Aider's repository map](https://aider.chat/docs/). Bounded size, shown in the A1 breakdown, and respects exclusions.
- **A9 Embeddings search.** Opt-in index of project files using an Ollama embedding model, so "where is X handled?" finds relevant files without hand-picking. Index lives under the data folder, updates incrementally, and can be deleted from settings. Complements the existing literal search. Needs an embedding model installed; say so instead of failing.
- **A10 Helper model.** Let the user pick a small Ollama model for conversation titles and compaction summaries so the main coding model is not reloaded or interrupted for chores (OpenCode does this with hidden system agents). Default is "use the same model", the safe choice on limited RAM.
- **A11 Conversation recall.** Let the model search the user's past conversations, using the existing conversation search, scoped to the current project by default. The search appears as a visible tool call. Off by default, because it moves old text into the context. Modeled on Goose's Chat Recall extension.
- **A12 Multi-folder projects.** A project can list extra folders (for example a web app and its API) as additional roots. Extra roots are read-only until the user grants write access to each one, and the same bounds, exclusions and secret-file rules apply to every root. Gemini CLI's `includeDirectories` setting and Cline's multi-root workspaces do the same.

#### B. Agent quality and safety

| ID | Feature | Size | Depends on | Tier |
|---|---|---|---|---|
| B1 | Permission modes and allowlist | M | none | Core |
| B2 | Structured tool calls | M | none | Later |
| B3 | Diff edits alongside whole-file edits | M | none | Later |
| B4 | Step limit and loop detection | S | none | Core |
| B5 | Test and lint loop | M | B1 | Later |
| B6 | Show model reasoning | S | none | Later |
| B7 | Sandbox research | L | X3, per OS | Speculative |
| B8 | Loaded-model awareness | S | none | Later |
| B9 | Folder trust | M | none; must land before D2, D3, D4 and D6 read project files | Core |
| B10 | Untrusted content handling | S–M | B1 | Later |
| B11 | Task checklist | S–M | none | Later |
| B12 | Model manager: recommend, download, upgrade, remove | L | B8, V3 results | Later |

- **B1 Permission modes and allowlist.** Modes for the command tool: *ask every time* (today's behavior and the default), *auto-approve read-only commands*, and a per-project *allowlist* of exact commands. File writes are never auto-approved. Rules, from Claude Code's [permission system](https://code.claude.com/docs/en/permissions): evaluate deny first, then ask, then allow, so a deny always wins; judge compound commands (`a && b`, pipes) piece by piece; treat output redirects (`>`, `tee`) as file writes and check their targets; resolve symlinks before judging a path; treat `.git` and Codev's own data folder as protected. Because rules on command text can be bypassed (for example through `sh -c`), the UI must keep saying commands are not sandboxed until B7 exists. Refuse the combination "never ask" plus "full access" if B7 ever adds full access.
- **B2 Structured tool calls.** Use Ollama's native tool calling and [JSON-schema structured outputs](https://docs.ollama.com/capabilities/structured-outputs) where the model supports them, so plans, tool arguments and summaries are validated before use. Keep the current text parsing as the fallback for models that lack support, and record which path was used in the tool status.
- **B3 Diff edits.** Let the agent propose either a patch or a whole-file replacement, whichever costs fewer tokens for the change. Today only whole-file replacements exist, which is expensive for small edits on small models. [Aider supports several edit formats](https://aider.chat/docs/) for this reason. Both formats go through the same review and checkpoint; a patch that does not apply cleanly is rejected with a clear message, never guessed at.
- **B4 Step limit and loop detection.** A per-task maximum number of tool rounds, and a guard that stops and asks when the model repeats the same tool call with the same arguments several times. Small local models get stuck this way. OpenCode's `doom_loop` permission fires when the same tool call repeats 3 times with identical input and defaults to *ask* ([permissions docs](https://opencode.ai/docs/permissions/)). Use the same rule as Codev's default: after 3 identical consecutive calls, pause and ask the user whether to continue once or stop the task.
- **B5 Test and lint loop.** After an approved edit, optionally run a configured test or lint command and give failures back to the model for another attempt, capped at a small number of rounds. Each command still needs approval or an allowlist entry, each round is shown, and success is claimed only on a passing run.
- **B6 Show model reasoning.** For Ollama models with a thinking mode, show the reasoning as a collapsible block and let the user turn thinking off per conversation.
- **B7 Sandbox research.** Codex separates *what a process may physically do* (read-only, workspace-write, full access) from *when it must ask*; Codev has only the second. Real sandboxing needs a different mechanism on each OS, and [Codex's own Windows sandbox write-up](https://openai.com/index/building-codex-windows-sandbox/) shows it is hard to get right. This item is research first: write down what each OS offers, prototype, and only then add a mode. No mode is described as safe until tested. The simplest cross-platform candidate to prototype first is running commands in a Docker or Podman container with the project mounted, which is what Gemini CLI offers as its sandbox option. It needs a container runtime installed, so make it optional and detect it.
- **B8 Loaded-model awareness.** Show which models Ollama currently holds in memory and their memory use, and offer to unload one. Backs the hardware-aware principle with real numbers.
- **B9 Folder trust.** Things a project can bring with it that execute or steer the agent (skills with scripts, hooks, agent and command files, MCP servers, and `AGENTS.md`) are ignored until the user marks that folder trusted. Trust is per folder, revocable, and shown in `/status`. An untrusted folder still allows plain chat and reading files. A cloned repository could otherwise carry hidden instructions or scripts. Reference behavior, checked 2026-09-27. Gemini CLI's [trusted folders](https://geminicli.com/docs/cli/trusted-folders/) (off by default there): in an untrusted folder it ignores project settings and `.env` files, does not auto-load local context files, blocks extension changes, will not connect MCP servers, disables project and user custom commands, and always prompts before tools run. On first use in a folder a dialog lists what was found (commands, MCP servers, hooks, skills, setting overrides) with any security warnings, and offers *trust folder*, *trust parent folder* (covers subfolders) or *don't trust*. Decisions are kept in one readable file (`~/.gemini/trustedFolders.json`) and can be revoked from inside the tool. Claude Code requires trust verification for first-time codebase runs and new MCP servers, skips it in non-interactive `-p` mode, and keeps trust for the home directory for the current session only ([security docs](https://code.claude.com/docs/en/security)). For Codev: copy the list of blocked items and the three-way dialog, store decisions in one readable file in the data folder, do not offer an option that trusts every folder, and make headless mode (E7) read-only with project-provided executable items off, instead of adding a flag that bypasses trust (Gemini CLI has one).
- **B10 Untrusted content handling.** Treat text from files, command output, web pages and MCP tools as data, not instructions: present it to the model as quoted tool output. In each approval prompt, show where a proposed command or edit came from, and highlight it when the model appears to be following an instruction found in a file it read (for example a command mentioned in a README). This is a design recommendation from Codev's own principles, not something taken from another tool.
- **B11 Task checklist.** For multi-step tasks the agent keeps a visible checklist (pending, in progress, done) that the user can edit or reorder. It is stored with the conversation and re-inserted after compaction (A2). This is the concrete design for the "richer plans remain" item in the feature status. Goose has a Todo extension and Claude Code a todo tool for the same purpose.
- **B12 Model manager: recommend, download, upgrade, remove.** Help the user keep a good, right-sized set of models without leaving Codev, and clean up old ones. Motivation: models change every few weeks, and a machine can end up with many multi-gigabyte downloads, several of which are aliases of the same files (the same digest under different names shares disk space, so a naive "size" total overstates what deleting one frees).
  - *Budget.* The user sets a size budget (the largest model they are willing to hold, and a disk reserve to keep free). Codev detects total and free RAM, free disk and, where it can, GPU memory, and shows an estimate of what a model needs: its file size plus context memory plus headroom for the operating system. It warns when a model would not fit or would leave the machine slow. Hardware detection is per OS (see section 7), and integrated graphics that share system memory must not be counted as separate video memory.
  - *Recommendations come from a catalog, not from guessing.* Ollama's API can list, pull, inspect and delete models you have (`GET /api/tags`, `POST /api/pull`, `POST /api/show`, `DELETE /api/delete`, checked against its [API reference](https://github.com/ollama/ollama/blob/main/docs/api.md) on 2026-09-27), but none of the endpoints I reviewed browses the online library or says which model is newest (confirm this before building). So Codev needs a small curated catalog (a JSON file shipped with the app and refreshable from a URL only if the user opts in). Each entry records the family, size at each quantization, parameters and *active* parameters (a mixture-of-experts model with few active parameters is much faster on CPU-only machines), context length, capabilities (tools, thinking, vision), license, release date, and a tier such as *coding agent* or *general*. The catalog is the part that needs maintenance, so it must be small and easy to update, and it must say when it was last checked.
  - *Newer is not automatically better.* Benchmark scores are self-reported, depend on the wrapper the model ran in, and often come from different setups (see the V3 findings). So an upgrade is a *suggestion with a test*: offer to run Codev's own small task suite (V3) on the candidate and show the old and new models side by side (speed, tool-call validity, task pass rate) before the user decides. Never switch a conversation's model automatically.
  - *Download.* Opt-in, per model, showing the download size, the estimated fit and the licence first, with progress, cancel and resume (Ollama resumes cancelled pulls and shares progress between repeated calls). Say plainly that this contacts the model registry and that nothing from the project is sent. This is the only part of Codev that reaches the internet by design, so it belongs behind its own setting, off by default, like the other opt-in network features.
  - *Updates.* Re-pulling a tag fetches a newer build of the same model; detect it on request ("check for updates") rather than in the background, and show the size before downloading.
  - *Removal.* Never delete automatically. Suggest models that are clearly superseded (same family, older generation, or unused for a long time), list how much space would actually be freed (accounting for shared files), and show which conversations and project defaults use each model so a removal does not break them; offer to move those to a chosen replacement first. Always confirm, and never remove a model that is currently loaded or queued.
  - *Reporting.* Show what is installed, what is loaded now (B8), sizes, last used, and the measured speed from E10, so decisions are based on the user's own machine.
  - *Depends on:* B8 (what is loaded), E10 (speed measurements) and the V3 test suite; also X-steps for cross-platform hardware detection. Start small: a read-only "installed models" view with sizes, aliases and last-used, then a safe remove, then catalog-based recommendations, then guided upgrades.

#### C. Review, Git and recovery

| ID | Feature | Size | Depends on | Tier |
|---|---|---|---|---|
| C1 | Richer review pane | M | none | Core |
| C2 | `/review` pass | M | D1 | Later |
| C3 | Rewind | M | none | Core |
| C4 | Resume by project, fork a conversation | S | none | Later |
| C5 | Queue a follow-up while running | S | none | Core |
| C6 | Edit any earlier message | S–M | C3 | Later |
| C7 | Whole-tree snapshot before commands | M–L (prototype first) | C3 | Speculative |

- **C1 Richer review pane.** Per [Codex's review pane](https://learn.chatgpt.com/docs/code-review?surface=app): stage, unstage and revert at three levels (whole diff, per file, **per hunk**; Codev is per file today); choose the scope to review (uncommitted changes, **the assistant's last turn**, a branch against a base, one commit); and attach a comment to a diff line. Comments are listed in the composer before sending and cleared once addressed, and they travel with the message as guidance. This is the concrete design for the "persistent comments on diff hunks" item in section 4.
- **C2 `/review`.** A separate, read-only model pass over uncommitted changes, a commit or a branch, returning findings by priority before the user commits. Uses a review-specific prompt and never edits files. Label it a second opinion; quality depends on the model.
- **C3 Rewind.** Restore a conversation and its files to an earlier message. One checkpoint per prompt that starts a turn; the menu offers *restore code and conversation*, *restore conversation only*, *restore code only*, and the A2 summarize actions. Keep a bounded number of checkpoints and report clearly if a restore fails because old snapshots were cleaned up. Limits to state honestly, as Claude Code does: changes made by shell commands are not tracked, edits made outside Codev are not tracked, symlinked and hard-linked files are skipped with a visible warning, and checkpoints are not a replacement for Git. Builds on existing checkpoints and branch-from-message. After a restore, re-present the original pending action (an edit or a command) so the user can re-run, change or reject it, as Gemini CLI's `/restore` does. See C7 for covering changes made by shell commands.
- **C4 Resume and fork.** Opening a project offers "continue the most recent conversation here" (as `codex resume` does), and a "fork this conversation" action copies it, with its checkpoints, into a new one.
- **C5 Queue a follow-up.** Let the user type and queue a message while a response is running. The queue already exists, so this is mostly a composer change. A message that joins a running turn is not its own rewind point.
- **C6 Edit any earlier message.** Click any sent message to revise it and resend, which restores the conversation (and optionally the files) to that point through C3 and puts the original text back in the composer. Codev only edits and resends the last prompt today; [Zed](https://zed.dev/docs/ai/agent-panel) lets you edit any past message.
- **C7 Whole-tree snapshot before commands.** C3's main gap is that shell-made changes are not tracked. Gemini CLI stores its [checkpoints](https://geminicli.com/docs/cli/checkpointing/) as commits in a separate "shadow" Git repository outside the project (together with the conversation and the tool call that triggered it), so they never touch the project's own history. Codev could use the same approach for file edits and also snapshot the whole project tree (respecting ignore rules) before an approved command, so undo covers shell changes too. That extension is an inference, not something either tool documents. Prototype it on a large repository and measure time, disk use, and behavior with ignored, binary, huge and symlinked files before committing to it.

#### D. Extensibility and automation

| ID | Feature | Size | Depends on | Tier |
|---|---|---|---|---|
| D1 | Slash commands and file-based commands | S | none | Core |
| D2 | Skills | M | D1 | Later |
| D3 | Agent definition files | M | B4 | Later |
| D4 | Hooks (formatter first) | M | B1 | Later |
| D5 | Subagents with child sessions | L | D3, section 3 worktrees | Speculative |
| D6 | MCP client | L | B1 | Later |
| D7 | Plugins | L | D2, D3, D4, D6 | Speculative |
| D8 | Automations and review inbox | L | section 3 worktrees, C1 | Speculative |
| D9 | Opt-in web search and fetch | M | B1 | Speculative |
| D10 | Code intelligence (language servers) | L | none | Speculative |

- **D1 Slash commands and file-based commands.** Typing `/` in the composer opens a menu: built-ins (`/plan`, `/code`, `/clear`, `/model`, `/compact`, `/export`, `/status`, `/init`, `/review`) plus user commands. User commands are markdown files, per user or per project (project ones shareable through Git), with named argument placeholders, in the spirit of OpenCode's custom commands. The existing saved prompt templates become the same thing. A command file may also name an agent (D3) and limit its tools, which gives a saved, parameterized task that can be run from the menu, headlessly (E7) or on a schedule (D8); Goose calls these recipes (its recipe format was not reviewed).
- **D2 Skills.** Folders of markdown instructions, optionally with scripts, loaded on demand, at user and project scope; this is the shape of the pending "executable skill workflows" item. Design, from Claude Code's [extension guide](https://code.claude.com/docs/en/features-overview): only each skill's name and short description sit in context until it is used, so many skills stay cheap; a skill can be marked user-invoke-only (`/name`) so the model never triggers one with side effects itself; the same name at project and user scope resolves by a fixed priority; scripts run through the approval-gated command tool.
- **D3 Agent definition files.** A markdown file per agent, global or per project, with a header for name, description, model, temperature, maximum steps and an ask/allow/deny permission per tool, and a body that is the system prompt ([OpenCode's agent format](https://opencode.ai/docs/agents/)). Use it for Plan and Code modes too, so a mode is just an agent with permissions. Include a one-key Plan/Code toggle (E2). Ship these built-in agents, following [Roo Code's modes](https://roocodeinc.github.io/Roo-Code/basic-usage/using-modes): *Ask* (read and MCP tools only, no edits or commands), *Architect/Plan* (read plus edits limited to markdown files), *Code* (full access), *Debug* (systematic troubleshooting: reproduce, form a hypothesis, instrument, confirm before fixing) and *Orchestrator* (no direct tools; delegates to other agents, see D5). Two more ideas worth copying: a per-agent **edit path restriction** (a pattern such as `*.md` that the agent may write, enforced by the same path checks as B1), and a per-agent **remembered model**, so planning can use a larger model and coding a coder model, with a visible notice when a switch means loading a different model (see B8). Zed's agent "profiles" are the same idea: a named set of enabled tools per conversation.
- **D4 Hooks.** User-configured commands that run before or after tool actions. The first and safest hook is a **formatter after edits**: run one named formatter on the changed file, show its output, and record the result in the checkpoint so undo covers it. All hooks are visible in settings, logged in run history, and off by default; a hook is not a guardrail unless it can block the action, and blocking hooks come later.
- **D5 Subagents with child sessions.** A subagent is an agent definition (D3) run in its own context. Show it as a collapsible child conversation under the parent that can be opened and resumed, not an invisible side effect. A cheap small model can do read-only exploration and return a summary, keeping the main model's context clean. Keep the depth limit at 1 (subagents cannot start subagents) and the concurrency limit at the serial queue: local inference is serialized, so say so instead of implying a speed-up. See [Claude Code subagents](https://code.claude.com/docs/en/sub-agents) and OpenCode's child sessions. The Orchestrator agent (Roo Code calls it "boomerang" mode) is a planner whose only tool is "delegate this task to agent X"; on local hardware its subtasks still run one after another.
- **D6 MCP client.** MCP-compatible tools with per-server enablement, visible permissions and logs; local-only use stays simple. Tool schemas load on demand so idle servers cost little context.
- **D7 Plugins.** One installable folder that bundles skills, agents, hooks and MCP servers, with names scoped per plugin. Only after those pieces exist.
- **D8 Automations and review inbox.** Scheduled or manual runs that deliver results to an inbox. Per [Codex's scheduled tasks page](https://learn.chatgpt.com/docs/automations?surface=app), each run in a Git repository can use *worktree mode* (changes stay isolated from the user's checkout) or *local mode* (edits the main checkout directly); Codev should default to a worktree per run and require an explicit choice for local mode. Schedules are time-based, and Codex also offers event triggers (Gmail, Slack, GitHub) on some plans; it does not mention webhooks. Skip event triggers and webhooks at first, since they need connectors or a listening server, which is a security surface (localhost only with a token, if ever). Codex's Scheduled view is an inbox of active, paused and completed tasks with an unread indicator for runs that need attention; copy that. Unattended runs use the default sandbox settings, so any tool call that needs more than the sandbox allows simply fails; until B7 exists, Codev has no sandbox, so unattended runs should be read-only by default and every write should wait in the inbox for approval. Codex's desktop app must stay running for tasks that touch local files; say the same. Local models are slow, so show queue position and expected wait. (An earlier third-party description said runs with nothing to report archive themselves; the official page does not say that, so treat it as unconfirmed.)
- **D9 Opt-in web search and fetch.** Off by default, clearly labeled as leaving the machine, per-domain permission, and never sends project content to a search query without showing it. Consistent with local-first.
- **D10 Code intelligence.** Opt-in per-language language-server support: go to definition, find references, and live type errors after an edit, so a small model can check its own work. Larger than A8; do A8 first.

#### E. Everyday use and input/output

| ID | Feature | Size | Depends on | Tier |
|---|---|---|---|---|
| E1 | Output styles | S | none | Later |
| E2 | One-key Plan/Code toggle | S | none | Core |
| E3 | Themes and key bindings | S–M | none | Later |
| E4 | Self-contained HTML export | S | none | Later |
| E5 | Preview for HTML/SVG code blocks | M | none | Speculative |
| E6 | Local voice | M | X-steps for OS-specific parts | Later |
| E7 | Headless mode | M | X2 | Later |
| E8 | Local server for multiple front ends | L | X2, decision | Speculative |
| E9 | Compare models side by side | M | B8 helpful | Speculative |
| E10 | Generation stats | S | none | Later |
| E11 | Follow the agent | S–M | none | Later |

- **E1 Output styles.** Saved system-prompt presets (concise, explanatory, code-only) chosen per conversation. A style controls *how* the model answers; project instructions control *what it should know*.
- **E2 Plan/Code toggle.** A single shortcut and a clear on-screen indicator for the current mode, since accidental edits are the risk.
- **E3 Themes and key bindings.** Custom themes and rebindable shortcuts; section 5 already lists broader shortcut customization as remaining.
- **E4 HTML export.** Export a conversation as one self-contained HTML file (rendered Markdown, code, diffs) to email or commit, alongside the Markdown and JSON exports. Replaces the hosted share links other tools offer, which Codev will not do. Strip local file paths and offer to redact secrets first.
- **E5 Preview.** Render HTML, SVG and Markdown code blocks in a sandboxed pane with scripts and network turned off (a small local take on Claude's artifacts).
- **E6 Local voice.** Dictation into the composer, and optionally spoken replies, without any hosted service. Put both routes behind small `ISpeechToText` / `ITextToSpeech` interfaces so the composer does not care which is active: (1) *built-in OS speech*, no download: `System.Speech` and `Windows.Media.SpeechRecognition` on Windows (Windows-only, accuracy usually lower than Whisper), `SFSpeechRecognizer` on macOS (on-device only if Dictation is enabled and its language downloaded, per [Apple's docs](https://developer.apple.com/documentation/speech/sfspeechrecognizer)), and nothing comparable built in for recognition on Linux (speech output there is normally `speech-dispatcher`, to be checked per distribution); (2) *a bundled local model*, same behavior everywhere: [whisper.cpp](https://github.com/ggml-org/whisper.cpp) through its .NET binding [Whisper.net](https://github.com/sandrohanea/whisper.net) for speech to text, and [Piper](https://github.com/rhasspy/piper) for speech output, at the cost of a download and RAM competing with the Ollama model. Start with push-to-talk dictation that fills the composer for review before sending. Never label an engine "stays on this machine" until it has been confirmed offline on that platform. Findings, checked 2026-09-27: Windows voice typing (Win+H) streams audio to Microsoft's cloud speech service and needs an internet connection, so it is *not* private; Windows Voice Access runs on-device and works offline ([Microsoft's speech privacy page](https://support.microsoft.com/en-us/windows/privacy/speech-voice-activation-inking-typing-and-privacy) and third-party guides; confirm Voice Access's engine before relying on it for Codev's own dictation, since it is a whole-PC control feature, not an API). On Apple-silicon Macs, Dictation is processed on the device for supported languages, and declining the prompt to send dictation to Apple keeps it offline. whisper.cpp model sizes ([project README](https://github.com/ggml-org/whisper.cpp)): tiny 75 MiB on disk and about 273 MB of RAM, base 142 MiB and about 388 MB, small 466 MiB and about 852 MB, medium 1.5 GiB and about 2.1 GB, large 2.9 GiB and about 3.9 GB; it supports CPU-only use and integer-quantized models. A small or base model is a reasonable default for dictation, and its RAM competes with the Ollama model.
- **E7 Headless mode.** `codev -p "prompt"` with piped input for scripting and CI, read-only unless explicitly allowed, reusing `Codev.Core`. Modeled on `codex exec` and `claude -p`.
- **E8 Local server.** OpenCode runs its agent as a local server that the terminal UI, desktop app and editor extensions all talk to over HTTP. It could let Codev's Avalonia app, a command-line mode and an editor extension share one core, but it adds a network surface (localhost only, token required) and a lot of design work. Decide after X2 shows what actually needs sharing; do not build speculatively. Zed's open Agent Client Protocol (ACP) lets an editor host any compatible agent; Codev could one day speak ACP, either to host external agents or to be hosted by an editor. That is speculative and only worth studying if E8 goes ahead. Basics from its [introduction page](https://agentclientprotocol.com/overview/introduction): it standardizes communication between code editors and coding agents, uses JSON-RPC over stdio for local agents, and lists HTTP or WebSocket for remote agents as work in progress. That page did not state who created it, its version or its capabilities, so those remain to be read.
- **E9 Compare models side by side.** Send one prompt to two or more installed models and show the answers next to each other, run one after another on local hardware, and let the user keep one as the conversation's answer. Helps choose between local models. Read-only chat only, never Code mode. Msty's split chat does this.
- **E10 Generation stats.** Show time to first token, tokens per second and model load time for each reply, since speed is the main practical difference between local models. Ollama responses report durations and token counts; confirm the field names against its API docs.
- **E11 Follow the agent.** While the agent reads or edits a file, highlight that file in the project browser and preview pane, with a toggle. [Zed's agent panel](https://zed.dev/docs/ai/agent-panel) has a "follow the agent" mode. Fits the "visible work" principle.

**Not planned:** cloud-hosted agents (Codex cloud tasks), phone or browser remote control, hosted share links, and organization-wide managed-policy servers, all of which need a service Codev does not run.

#### Open questions: verify before building

- **B7:** decide the mechanism per OS (Windows restricted tokens or job objects, macOS sandbox profiles, Linux namespaces or Landlock) only after prototyping; none of these has been tried.
- **X6:** confirm Avalonia can render Codev's Markdown, tables and copyable code blocks acceptably before committing to the full port.
- **C7:** measure a shadow-repository snapshot on a large project (time, disk use, ignored and binary files) before choosing between whole-tree and edited-files-only snapshots.
- **E8:** read the Agent Client Protocol specification for its capabilities (basics are recorded under E8) before treating it as an option.

### 9. Validation and backlog hygiene

Section 8 is a long list of ideas. These four workstreams keep it honest: they cut it down, replace guesses with facts, check that the features depend on things local models can actually do, and test what already exists. They are process items, not features, and each has an outcome that can be checked. Run them alongside implementation (phase 0 of the build order for V1, V2 and V4).

#### V1. Triage the backlog

**Outcome:** every item in section 8 carries a tier, added as a column in its table: **Core** (helps nearly every local-model user, low risk), **Later** (valuable but depends on other work or on evidence), or **Speculative** (decide only after V3 or V4 produce evidence). Rule of thumb: an item is Core only if it makes the agent safer, makes its work easier to see, or lets a user recover from a mistake, and it needs no new infrastructure.

Proposed first triage. This is a recommendation; the maintainer decides.

| Tier | Items |
|---|---|
| Core | A1, A2, A6, A7, B1, B4, B9, C1, C3, C5, D1, E2 |
| Later | A3, A4, A5, A8, A10, A12, B2, B3, B5, B6, B8, B10, B11, B12, C2, C4, C6, D2, D3, D4, D6, E1, E3, E4, E6, E7, E10, E11, and X6 and X7 |
| Speculative | A9, A11, B7, C7, D5, D7, D8, D9, D10, E5, E8, E9 |

Why these are Speculative: A9, A11 and D10 add retrieval or indexing that small models may not use well (V3 will tell); B7, C7 and D8 are large and need per-OS research; D5, D7 and E8 depend on a lot of unbuilt infrastructure; D9 leaves the machine; E5 and E9 are nice but rarely decisive.

**Status:** the tier column now exists in every section 8 table, copied from the proposal above; if the two ever disagree, the proposal table is the source and the columns must be updated to match. **Done when:** any Speculative item that V3 or V4 supports has been promoted with a note saying why.

#### V2. Close the open questions

**Outcome:** each bullet under "Open questions" becomes either a verified fact with a source and date, or is deleted.

Already verified against the [Ollama API reference](https://github.com/ollama/ollama/blob/main/docs/api.md) on 2026-09-27 (the doc is versioned, so Codev should still check a model's capabilities at run time rather than assume them):

| Need | What the API provides |
|---|---|
| Which models are loaded (B8) | `GET /api/ps` returns, per model, `name`, `model`, `size`, `digest`, `details`, `expires_at` and `size_vram` |
| Unload a model (B8) | Send a request with `"keep_alive": 0` |
| Speed and load time (E10) | The final response of a generate or chat call reports `total_duration`, `load_duration`, `prompt_eval_count`, `prompt_eval_duration`, `eval_count` and `eval_duration` |
| Thinking (B6) | A `think` request parameter, a boolean or a level (low, medium, high, max) |
| Tool calling (B2) | A `tools` array of function definitions in the request; `tool_calls` in the response |
| Structured output (B2) | The `format` field accepts a JSON schema |
| Embeddings (A9) | `POST /api/embed` (`/api/embeddings` is superseded) |
| Manage models (B12) | `POST /api/pull` streams `status`, `digest`, `total` and `completed`, and a cancelled pull resumes; `DELETE /api/delete` takes `model`; `POST /api/show` returns `details` (`parameter_size`, `quantization_level`, `family`), `model_info` and a `capabilities` array; `GET /api/tags` returns `name`, `size`, `digest` and `modified_at`. None of the endpoints reviewed browses the online library. |

Verified from tool documentation on 2026-09-27:

| Question | Answer |
|---|---|
| What OpenCode's `doom_loop` does (B4) | Triggered when the same tool call repeats 3 times with identical input; defaults to *ask* |
| How folder trust works (B9) | Gemini CLI and Claude Code behavior is summarized under B9 |
| Whether OS voice typing is private (E6) | Windows Win+H is cloud-based; Windows Voice Access and Apple-silicon Dictation are on-device; details under E6 |
| Whisper model sizes (A9, E6) | tiny to large, 75 MiB to 2.9 GiB on disk, about 273 MB to 3.9 GB of RAM; table under E6 |
| What Codex automations offer (D8) | Time-based schedules, some event triggers, worktree or local mode, an inbox view; no webhook trigger is mentioned; details under D8 |
| What the Agent Client Protocol is (E8) | Editor-to-agent standard, JSON-RPC over stdio locally; creator, version and capabilities not yet read |

Deferred as harder, with the way to close each: C7 (measure a shadow-repository snapshot on a large project; one attempt on a very large repository was abandoned as too slow to be worth doing now, so there is no result), B7 (prototype a sandbox per OS), X6 (Markdown-rendering spike in Avalonia), and E8 (read the Agent Client Protocol specification for its capabilities).

**Done when:** the "Open questions" list is empty or every remaining bullet has a named way and a date to be closed.

#### V3. Measure what local models can actually do

**Outcome:** a findings table in this document, filled from real runs, that says which backlog items work on the models Codev supports.

Many items assume a model can do something reliably: call tools with valid arguments (B2), produce a patch that applies (B3), notice it is looping (B4), stay on task after its history is summarized (A2), and use a repo map or retrieved files well (A8, A9). Small local models often cannot, and nothing in the research above measured that. Method: keep a small fixed task suite in the repository (for example ten short tasks on a sample project, each with a pass/fail check), run it headlessly (E7) or by hand against each model listed in the README, and record the results.

| Question to answer | Feature it decides |
|---|---|
| Does native tool calling work, and how often are arguments invalid? | B2 |
| Does a patch or a whole-file rewrite apply correctly more often? | B3 |
| How often does the model repeat itself, and after how many steps? | B4 |
| Does a task survive compaction (A2) without losing its goal? | A2, B11 |
| How large a repo map or retrieved context helps, and where it hurts? | A8, A9 |
| Usable context size and speed (tokens per second, load time) on typical hardware | A1, B8, E10 |
| Does thinking mode help or just slow things down? | B6 |

**Done when:** the table is filled for at least the models named in the README, each affected backlog item is annotated with the measured outcome, and any item that fails on those models is demoted in V1.

#### V4. Test what exists

**Outcome:** the current features are checked on every supported platform, and real friction feeds back into the backlog.

- **V4a. CI baseline.** Recorded in section 7: the hosted matrix passes the portable suite on Windows, Linux and macOS. Keep it green, and record any new platform-specific failure with its cause instead of skipping the test.
- **V4b. Manual smoke checklist.** Commit a checklist (for example `docs/manual-checklist.md`) and run it before each release: streaming chat; project context and file selection; a Code task edit, approval and undo; a command approval, timeout and stop; queue pause, resume and crash recovery; backup export and import; Ollama endpoint change warning. Run it on Windows now, and on Linux and macOS once the Avalonia port (X6) exists.
- **V4c. Real-use log.** Use Codev on a real project for a stretch of time and write down every point of friction, wrong answer and confusing moment. These notes feed V1 and are usually more valuable than another comparison with a competitor.

**Done when:** V4a stays green, the checklist is committed and has been run at least once, and the first real-use log has been read and its items triaged.


## Product principles

- **Local first:** the default inference endpoint is Ollama on localhost; show clearly if a request would leave the machine.
- **Review before change:** show a diff and make rollback possible before and after file writes.
- **Bounded by workspace:** tools operate within the user-selected project unless an explicit permission changes that boundary.
- **Visible work:** expose plans, tool calls, command output, changed files, and completion evidence.
- **Honest capability:** distinguish simple chat from agent tasks, unsupported model features, queued work, and completed work.
- **Portable by default:** new code goes in the UI-free core and avoids Windows-only calls unless it sits behind an OS abstraction.
- **Spend context carefully:** local models have small windows; load things on demand and show what each feature costs.
- **Hardware-aware concurrency:** parallel sessions must not imply that several large models can fit in RAM simultaneously; schedule/swap safely and keep GPU acceleration off unless the user enables it.

## Sources

Checked on 2026-09-27. Product capabilities change, so revisit these when planning each milestone. They are workflow references, not a checklist or a promise to reproduce paid or cloud features.

**Claude**

- Desktop and projects: [Projects guide](https://support.anthropic.com/en/articles/9517075-what-are-projects), [project management](https://support.anthropic.com/en/articles/9519177-how-can-i-create-and-manage-projects), [Claude Desktop and Cowork](https://support.claude.com/en/articles/10065433-install-claude-desktop), [Windows deployment](https://support.claude.com/en/articles/12622703-deploy-claude-desktop-for-windows), [connectors](https://support.anthropic.com/en/articles/11817150-connect-your-tools-to-unlock-a-smarter-more-capable-ai-companion).
- Claude Code: [overview](https://code.claude.com/docs/en/overview), [security](https://code.claude.com/docs/en/security), [extension guide](https://code.claude.com/docs/en/features-overview), [checkpointing](https://code.claude.com/docs/en/checkpointing), [permissions](https://code.claude.com/docs/en/permissions), [memory](https://code.claude.com/docs/en/memory), [subagents](https://code.claude.com/docs/en/sub-agents).

**Codex**

- [Codex app overview](https://openai.com/index/introducing-the-codex-app/), [Windows sandbox design](https://openai.com/index/building-codex-windows-sandbox/), [safety controls](https://openai.com/index/running-codex-safely/), [CLI reference](https://learn.chatgpt.com/docs/codex/cli), [review pane](https://learn.chatgpt.com/docs/code-review?surface=app).
- [Scheduled tasks (automations)](https://learn.chatgpt.com/docs/automations?surface=app), and a [third-party write-up](https://codex.danielvaughan.com/2026/04/08/codex-desktop-automations/) that differs from it in places (webhooks, self-archiving runs); prefer the official page.

**Other tools and components**

- OpenCode: [docs](https://opencode.ai/docs/), [agents](https://opencode.ai/docs/agents/), [config](https://opencode.ai/docs/config/), [permissions](https://opencode.ai/docs/permissions/).
- Other agents: Cline ([docs index](https://docs.cline.bot/llms.txt), [Memory Bank](https://docs.cline.bot/features/memory-bank)), Gemini CLI ([checkpointing](https://geminicli.com/docs/cli/checkpointing/), [trusted folders](https://geminicli.com/docs/cli/trusted-folders/), [docs](https://geminicli.com/docs/)), Zed ([agent panel](https://zed.dev/docs/ai/agent-panel)), Cursor ([rules](https://cursor.com/docs/rules)), Roo Code ([modes](https://roocodeinc.github.io/Roo-Code/basic-usage/using-modes)), Goose ([extensions](https://goose-docs.ai/docs/getting-started/using-extensions)).
- Local chat apps: [Open WebUI comparison with Msty](https://docs.openwebui.com/alternatives/msty/) and [with LM Studio](https://docs.openwebui.com/alternatives/lm-studio/), for side-by-side model comparison and knowledge stacks.
- [Aider](https://aider.chat/docs/): repo map, edit formats, lint and test loop.
- Ollama: [structured outputs](https://docs.ollama.com/capabilities/structured-outputs) and the [API reference](https://github.com/ollama/ollama/blob/main/docs/api.md) for tool calling, thinking, vision and embeddings.
- Agent Client Protocol: [introduction](https://agentclientprotocol.com/overview/introduction).
- Voice: [Microsoft speech privacy](https://support.microsoft.com/en-us/windows/privacy/speech-voice-activation-inking-typing-and-privacy), [whisper.cpp](https://github.com/ggml-org/whisper.cpp), [Whisper.net](https://github.com/sandrohanea/whisper.net), [Piper](https://github.com/rhasspy/piper), [Apple SFSpeechRecognizer](https://developer.apple.com/documentation/speech/sfspeechrecognizer).
- Cross-platform UI: [Avalonia on GitHub](https://github.com/AvaloniaUI/Avalonia) (MIT license) and [pricing](https://avaloniaui.net/pricing).
- Continue.dev was reported discontinued in June 2026, so it is not used as a reference.

## Implementation choices

- C# and modern .NET WPF provide a native Windows desktop app independent of VS Code. Avalonia UI is the planned path to macOS and Linux (section 7); shared logic should move into a UI-free core library so both front ends use it.
- Ollama's HTTP API provides local model discovery and inference, reusing the user's existing model files and CPU-only runtime configuration.
- Conversations and UI preferences are stored locally under `%LOCALAPPDATA%\Codev` on Windows (the per-user local application data folder on other platforms once ported); move from JSON to versioned SQLite when project/task state needs transactions, indexing, or migrations.
