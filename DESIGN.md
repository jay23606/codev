# Codev design and roadmap

## Product direction

Codev is a standalone coding workspace for local models, with optional hosted API providers for people who want to use them. Today it ships as a Windows desktop app; macOS and Linux are planned through an Avalonia UI port (see roadmap section 7). It takes inspiration from the clear conversation experience of Claude Desktop and the project/thread, coding-agent, and review workflows of Codex, while keeping Ollama as the local default. It does not depend on VS Code.

The goal is to make project work feel organized and reviewable: each task has its own conversation and working state; the agent can inspect a chosen project, propose changes, run approved checks, and show exactly what happened; the user remains in control of file changes, commands, network access, and concurrent work.

This roadmap describes Codev's intended product scope. It does **not** claim feature parity with Claude or Codex. Hosted model APIs are optional, explicitly configured integrations; they are separate products from ChatGPT or Claude subscriptions and may incur separate usage charges.

## Current design

- **Left rail:** Codev identity, new conversation, search, pinned conversations, recents, settings, and local model status.
- **Top bar:** active conversation title, local privacy hint, model selector, and pin control.
- **Provider choice:** Ollama is the default local provider; OpenAI and Anthropic API models are optional, clearly marked hosted choices with separate data-sharing consent. OpenAI Code task is available without attaching a project and provisions a private conversation workspace; project files and guidance require a separate per-conversation sharing choice. Anthropic remains chat and Plan only.
- **Conversation canvas:** focused welcome state, task suggestions, right-aligned user bubbles, and left-aligned assistant messages without role labels.
- **Composer:** multiline prompt, project-folder context control, and send action.
- **Visual language:** warm terracotta action color, rounded quiet controls, spacious conversation column, and low-noise navigation. Dark mode is the default; light mode is also available and remembered locally.

## Feature status

| Capability | Status in current build |
|---|---|
| Standalone Windows desktop app, no editor dependency | Done |
| Dark/light appearance with persistent preference | Done |
| Ollama model discovery and per-conversation model choice | Done; curated labels for the original three plus automatic discovery of non-empty installed tags, refreshed when the model picker opens; Avalonia restores the last active conversation/model and shows loading/empty status instead of a blank selector |
| Avalonia conversation layout and context size | User messages use right-aligned bubbles; assistant messages stay left aligned without role labels or per-message Copy buttons; Markdown remains selectable; per-chat context choices are model-capped and persisted |
| Avalonia read-only project context | Trusted folders include bounded source excerpts automatically; untrusted folders remain available for chat and browsing, with only explicitly selected files entering context; up to 24 supported files, with sensitive, excluded, unsupported, and outside-project files rejected |
| Avalonia folder trust | Trust choices are stored locally and revocable; untrusted folders stay usable for chat/browsing and explicit file selection, while automatic project context requires trust; hosted context keeps its separate opt-in |
| Avalonia conversation portability | Export one conversation as Markdown or standalone offline HTML; export all chats to JSON and additively import a backup without replacing existing history or restoring checkpoint contents |
| Avalonia conversation organization | Rename and permanently delete chats from their sidebar context menu; archive and restore from that menu; active requests and queued turns block archive/delete |
| Avalonia queued follow-ups | Prompts can be queued during generation; each turn snapshots its model/context, queued turns persist and restore paused with explicit resume, and queued turns can be canceled |
| Avalonia conversation mode shortcut | `Ctrl+Shift+M` cycles Chat, Plan, and eligible Code task modes; Code task creates a trusted conversation workspace when needed and supports loopback Ollama or opted-in OpenAI, with all existing approvals in effect |
| Avalonia `/status` | A local status report shows provider/model (including whether Ollama is remote), context window, temperature, mode, project/context scope, folder trust, queue state, and hosted-request state without sending the command to a model |
| Avalonia `/review` | A read-only second opinion on bounded staged, unstaged, and untracked Git changes using the selected local model; requires a trusted project and loopback Ollama, and exposes no tools; commit/branch review scopes remain pending |
| Avalonia project context estimate | Shows an approximate token cost for safely selected or automatically bounded project files before sending, states that history is excluded, makes the hosted-context opt-in visible, and provides an in-memory last-request context view for Chat/Plan |
| Ollama endpoint, per-conversation context size, and temperature | WPF and Avalonia validate configurable HTTP(S) endpoints, default to localhost, and require confirmation before sending to a changed non-local server; Avalonia persists endpoint with backward-compatible theme settings and disables redirects; context choices run through the model's max and temperature is optional (0–2), preserving Ollama's model default when unset |
| Context use visibility | Provider-reported input-token count when available and selected local context limit; bounded source-context estimate shown before sending; exact request JSON body, normalized messages, and rough component estimate can be inspected in Avalonia |
| Streaming chat and independent persistent conversations | History and drafts use detached snapshots and flushed atomic writes; unreadable stores are preserved and recovery uses a separate file |
| Unsent composer drafts | Each conversation retains its own draft across chat switches and restarts; drafts are debounced into local history and included in portable backups |
| Find in the active conversation | Search message text, inspect matching excerpts, and jump directly to a result with Ctrl+Shift+F |
| Pinning and conversation search | Title and message text search, matching excerpts, project/archive scope, and debounced input are implemented |
| Rename, archive, and restore conversations | Done; permanently delete is available from the archive view |
| Choose a project folder and include bounded source/config excerpts | Select up to 24 project-relative chat context files, remove individual selections, or use safe bounded excerpts; token estimates and sent context use the same file limit |
| Attach local source files | Project files can be dragged onto the composer and added as context; attachment bytes are not sent until the user sends the prompt |
| File explorer and preview | Project context menu opens a safe text-file browser with filtering, read-only preview, and Add to chat context |
| Project list, project-specific instructions, and reusable knowledge | Project switcher, pinning, saved instructions, and bounded reusable project knowledge included in local model context; project metadata writes are atomic and unreadable JSON is preserved |
| Agent reads/searches files on request and edits files | WPF Code task supports bounded project tools; Avalonia Code task supports loopback Ollama and opted-in OpenAI with bounded list/read/search, reviewed create/replace/strict-patch proposals, checkpointed writes, and individually approved shell commands |
| Diff review, approve/reject, checkpoints, and undo | WPF has per-conversation change history, rollback, and unified text diffs; Avalonia shows side-by-side proposals, patch text plus resulting-file review, checkpoint history, reviewed single-file restore, and conversation-only rewind before a user prompt; reviewed code rewind, diff navigation, and hunk review remain |
| Terminal commands, tests, and build output | WPF approval-gated PowerShell and Avalonia individually approved local-shell commands use a 3-minute timeout and bounded output; commands are not sandboxed |
| Plan/progress view and stop/resume/retry controls | Read-only Plan mode, stop with partial output retained, tool status, retry/edit-resend, branch-from-message, and between-turn queue pause/resume implemented; request resume and richer plans remain |
| Conversation request scheduling | Serial Ollama queue with per-conversation running/queued indicators and pause/resume; true parallel agents and isolated worktrees remain pending |
| Compact repository context | Avalonia can add an opt-in bounded project file/symbol outline, scoped to trusted or explicitly selected files and the same hosted-context consent |
| Git status, branch, staging, and review workflow | WPF and Avalonia show local status and per-file diffs, append selected diff lines to the composer for questions, and stage/unstage selected files; Avalonia also supports persistent comments attached to selected diff excerpts, staged-diff commit review, and clean-tree branch operations; worktree merge/recovery and hunk-level stage/revert remain |
| Markdown/code rendering, attachments, and session export | Markdown tables, lightweight syntax coloring, clickable web links, copyable code blocks, Markdown export, and JSON conversation backup/import; file/image/PDF attachments remain pending |
| Skills, MCP tools, recurring tasks, and notifications | Not implemented |
| Patch-based agent file edits | Avalonia Code task can apply strict unified-diff hunks, review the patch and resulting file, and use the same approval, checkpoint, and concurrency protection as full replacements |
| Per-conversation output styles | Avalonia offers balanced, concise, explanatory, and code-only response styles; the choice persists with the conversation and queued turns and does not alter permissions |
| Ollama loaded-model memory view | Avalonia reads the server's running-model list, shows reported model size and VRAM allocation, and offers a confirmed unload while no response or queued request is active |
| Ollama generation stats | Avalonia displays first-token latency, generated tokens per second, output token count, and server-reported model load time beneath completed local replies |
| Avalonia Code task checklist | Multi-step work can maintain an editable, reorderable conversation checklist with pending/in-progress/done states; it is stored in conversation history/backups and injected into later Code task prompts after compaction |
| Avalonia Code task untrusted-output boundary | Code task prompts identify project/search/command output as untrusted data; tool output is JSON-enveloped, and approval dialogs list project/search/command sources shown to the model and warn that proposals may reflect embedded instructions |
| Avalonia project resume and conversation fork | Attaching a folder with prior chats offers resume recent, attach to current, or start fresh; the sidebar can fork a saved chat with separate copies of Codev-managed rollback checkpoints |
| Ollama thinking display | A per-conversation Think toggle requests or suppresses the optional Ollama thinking field; returned thinking is kept separate from the reply, collapsed by default, and excluded from follow-up prompts |
| Hosted providers | WPF and Avalonia can connect to OpenAI and Anthropic APIs and discover available text models. Both stream chat; OpenAI Code task uses Responses function calls with prompt/tool-result disclosure explicitly acknowledged per conversation, while project files and instructions require a distinct per-conversation workspace-sharing choice. Workspace sharing can be revoked during a turn, which stops the active request. Codev provisions a conversation workspace when needed; a private conversation workspace can run with workspace sharing off, while an attached project requires trust and separate sharing consent before the hosted agent can access its files or instructions. File review, checkpoints, command approval, and cancellation controls still apply. Hosted data-sharing approvals are local and reset on portable backup import. Anthropic remains chat/Plan only. Manually entered keys persist in the OS credential store; WPF loads saved credentials and refreshes models at startup, environment keys take precedence, and users can remove saved keys. Connecting requires explicit cloud/data and billing acknowledgement. Hosted chat keeps project files and local instructions out of requests. Keys are not written to settings, chat history, or backups. Cross-platform credential-store and manual interaction checks remain pending. |

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

- [x] Separate **Chat**, read-only **Plan**, and **Code task** modes in Avalonia; Code task provisions a dedicated workspace when no project is attached and supports loopback Ollama or consented OpenAI, while plan-first workflows remain available.
- [x] Explicit Code task mode with bounded agent tools for listing, reading, searching, and proposing new or updated supported text files.
- [x] Avalonia Code task mode uses Ollama or OpenAI Responses function calls, creates and trusts a dedicated per-conversation workspace when no folder is attached, rechecks project trust before each operation, and snapshots its mode into queued turns. OpenAI requires separate hosted project/tool-content consent; Anthropic remains chat and Plan only. Existing attached folders still require explicit trust. Each file proposal has an explicit review dialog; approved replacements use checkpoints and concurrency checks; every shell command has a separate approval dialog, a three-minute timeout, and bounded output. Loop detection stops after repeated identical operations unless the user allows one more.
- [x] Review proposed whole-file replacements side by side; approve or reject before applying.
- [x] Local checkpoints before edits and deletes; per-conversation changed-file history grouped by path and restore, including undo/redo of file creation or deletion. The review names the exact file replacement or delete before approval. Whole-file unified diff and side-by-side review are available; richer history browsing remains.
- [x] Avalonia Files history lists a conversation's checkpointed changes newest-first and supports undo through a full current-versus-restored-file review. Restore rechecks the current file hash, saves a rollback checkpoint before writing or deleting, and records the restore so it can itself be undone; turn-level rewind remains.
- [x] Avalonia can rewind conversation history to before any user prompt, after confirmation, and puts that prompt back in the composer; it blocks while turns are active/queued and explicitly leaves project files unchanged. Reviewed code rewind remains future work.
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
- [x] Review staged/unstaged diffs per file; send selected diff lines to the composer without sending them; commit review shows the exact staged tree and rechecks it before committing. Avalonia supports selected-diff questions and persistent unsent comments attached to selected excerpts; manual UI smoke remains pending.
- [x] Avalonia can attach an opt-in bounded repo map of safe project files and common declarations to chat; it is scoped to trusted/selected files, persisted per conversation, captured per queued turn, and obeys hosted-context consent.
- [x] Stage/unstage selected files and create a local commit after review and confirmation; Codev never pushes automatically.
- [x] Avalonia project Git dialog shows branch/upstream status and staged, unstaged, or untracked files; it previews per-file diffs, stages/unstages selections, creates or switches local branches only from a clean tree with confirmation, and rechecks the staged index against the reviewed diff before a confirmed local commit. It never pushes.
- [x] Avalonia settings can change the Ollama HTTP(S) endpoint; it warns before connecting to a changed remote host, keeps the default on localhost, persists endpoint alongside theme, migrates old theme-only settings, and blocks redirects.
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

**Where things stand.** The desktop UI targets `net9.0-windows` with WPF, which only runs on Windows. Most non-UI code now lives in `Codev.Core`, a plain `net9.0` library that the Windows app references. `Codev.Tests` also targets plain `net9.0`; WPF rendering and PowerShell command tests stay in `Codev.Windows.Tests`. The hosted CI matrix builds the portable suite on Windows, Linux and macOS, plus Windows-only renderer and PowerShell tests. The Avalonia UI has the dark Codev-style shell, local conversation list/search/pin/archive/rename/delete/drafts, Ollama model discovery (including arbitrary installed tags), configurable Ollama endpoint settings with remote-host confirmation, session persistence and redirect blocking, optional OpenAI and Anthropic API model discovery and streamed text chat, OS-vault API keys/environment lookup with separate project-context consent, read-only Plan mode and trusted-project Code task mode per conversation, approval-gated file proposals and commands, safe `@` file mentions, a filtered read-only project file browser and preview, per-conversation context size, right-aligned user bubbles, left-aligned assistant messages without role labels or per-message Copy buttons, selectable Markdown, streaming local chat with follow-to-latest, Enter-to-send, queued follow-ups with per-turn model/context/mode snapshots and paused restart recovery, conversation-only rewind before a selected user prompt, basic new/search/focus/stop keyboard shortcuts, persistent dark/light theme, conversation export and backup/import, basic local Git status/change/branch/commit workflows, selected diff questions and persistent unsent comments attached to selected Git diff excerpts, opt-in bounded repository maps, and checkpoint history with reviewed single-file restore. It still lacks reviewed file-and-conversation rewind, richer hunk navigation/staging/revert, worktree merge/recovery, broader settings, and several WPF workflows. Keep WPF as the usable app until Avalonia reaches feature and interaction parity; do not present the Avalonia prototype as a replacement.

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
| X6 | Avalonia UI port. Match the established Codev layout, then port core chat and workflows in parity gates | In progress; local chats, search/pin/archive/rename/delete, conversation Markdown export and additive backup/import, Ollama discovery/context/streaming/stop, restart-safe queued follow-ups, read-only project-folder context and file selection, chat-bubble layout, composer and basic keyboard shortcuts, and dark/light theme build in the OS matrix; feature parity is not reached | Each parity gate builds on all supported OSes and has an explicit interaction checklist before WPF is retired |
| X7 | CI matrix and per-OS publishing (`win-x64`, `osx-arm64`, `osx-x64`, `linux-x64`) | Core-test matrix done; per-OS app publishing waits on X6 | Each build starts and passes tests on its OS |

**Open decision:** keep the WPF app alongside Avalonia during the transition, or retire it at parity. Recommended: keep it until Avalonia passes the same manual checklist, then retire it, so there is never a release with a regression on Windows.

### 8. Feature backlog

This section is the single backlog for features drawn from studying Claude Code and the Claude apps, OpenAI Codex, OpenCode, Aider, Cline, Gemini CLI, Zed, Cursor, Roo Code, Goose and local chat apps such as Msty (checked 2026-09-27; see Sources). Local Ollama remains the default; hosted API features are identified separately and must always be opt-in. Sizes are rough guesses: **S** is a few files, **M** is a feature with its own UI and tests, **L** is a multi-part project.

### 8a. Optional hosted model providers

Ollama continues to work without an account, API key, or network connection. Users who want stronger hosted models can connect directly to the official OpenAI API or Anthropic API, choose from models their key can access, and use those models for chat. OpenAI additionally supports Code task through Responses function calling without requiring an attached project; prompts and tool results go to OpenAI, while sharing project context requires a separate opt-in. Anthropic remains chat and Plan only. These API credentials and charges are separate from ChatGPT Plus/Pro and Claude subscriptions; a subscription login is not an API key.

Both desktop apps implement model discovery and streamed text chat for both providers. This rollout makes the experience reliable, understandable, and safe before treating it as a fully supported feature:

| # | Step | Status | Done when |
|---|---|---|---|
| P1 | Provider and privacy UX | Implemented in WPF and Avalonia; manual smoke pending | Ollama is visibly local/default; hosted models are labeled by provider; connecting names the provider, data sent, and possible API billing; OpenAI Code task explains prompt/tool-result disclosure and keeps project files behind a separate per-conversation workspace-sharing opt-in; hosted chat keeps project files and instructions out of requests; switching back to Ollama is immediate |
| P2 | API credential handling | OS credential vault and environment-variable lookup implemented in both apps; typed keys persist only after model discovery succeeds | Keys never enter conversation/settings/backup files or logs; environment keys take precedence, saved keys can be removed, and vault failures leave pasted keys usable for the session |
| P3 | Reliable provider clients | Pagination, empty lists, HTTP rate-limit details, request cancellation, API error bodies, truncated replies, and incomplete SSE streams now have deterministic tests; auth/live-provider and mid-stream cancellation QA remain | Cover pagination, empty model lists, authentication errors, rate limits, timeouts, cancellation, interrupted SSE streams, API error bodies, and provider/model compatibility with deterministic tests |
| P4 | Hosted model experience | Basic text chat implemented; capability differences remain | Show provider-specific model names and request state; make context/output limits clear when known; explain that API use is billed separately from consumer subscriptions; do not infer local tokens/sec or Ollama memory stats for cloud replies |
| P5 | Hosted coding-agent support | OpenAI Responses function calling is implemented in WPF and Avalonia behind explicit Code task and separate per-conversation workspace-context consent. Both apps provision a private conversation workspace without requiring an attached project; project files and instructions are sent only after a separate opt-in. WPF trust checks and Avalonia trust controls gate workspace operations; existing per-file review, checkpoint, command approval, and cancellation controls remain. Anthropic remains chat-only. Automated API-shape tests pass; manual live-key workflow testing remains | Manually validate OpenAI tool calls, consent, approvals, cancellation, and recovery in both apps; never silently send project files, and keep project-context consent distinct from consent to hosted prompts/tool results. Consider Anthropic after OpenAI QA |
| P6 | Hosted-provider manual QA and release | Pending | Verify connect, model selection, chat, errors, reconnect after restart, environment keys, persistence boundaries, and return to offline Ollama in both apps; verify Avalonia project-context consent and that WPF sends no automatic project context; publish clear supported-provider notes |

**Credential handling.** Manually entered keys are saved only after model discovery succeeds, using Windows Credential Manager, macOS Keychain, or Linux Secret Service through the Git Credential Manager secure-store backend. Environment variables take precedence and are never copied into the vault. Users can remove a stored key or disable it for the current session without deleting it. Keys never enter Codev settings, chat history, or backups. Linux needs an unlocked Secret Service provider in the desktop session; vault failures are shown and leave pasted keys usable only for the current session.

**OpenAI data handling.** The Responses API request sets `store:false` to avoid retaining response application state. This does not mean “zero data retention”: OpenAI documents that abuse-monitoring logs may still contain prompts and responses and are retained up to 30 days by default; account-level retention controls and exceptions apply. The hosted-provider consent must keep saying data leaves the device and is governed by the selected provider's API policies. See [OpenAI API data controls](https://developers.openai.com/api/docs/guides/your-data).

**Capability boundary.** Hosted chat and read-only planning are available for both providers. OpenAI Code task uses Responses function calls to invoke Codev's existing bounded project tools; file changes still need review, commands still follow project command approval, and trust is rechecked before each tool. Anthropic tool calling remains future work. Neither provider's API key grants access to the matching consumer subscription.

**Rules for every item below**

1. Follow the product principles: local first, review before change, visible work, honest capability. In particular, nothing may claim an edit, command or test succeeded without evidence, and no feature may send data off the machine unless the user turned on an explicit opt-in that says so.
2. Anything that writes files or runs commands goes through the existing review, checkpoint and approval paths. No item adds a silent write.
3. Anything downloaded (models, voices) is opt-in and shows its size first.
4. New behavior ships with tests in `Codev.Tests` where it is pure logic, and with an updated row in the feature status table.
5. Small local models have small context windows: prefer loading things on demand over always-on context, and show what each feature costs.
6. Meet the definition of done in section 10 (Q1) before marking an item Done; mark rows that do not apply as not applicable instead of skipping them.

**Recommended build order**

| Phase | Items | Why first |
|---|---|---|
| 0. Validate | V1, V2, V4 | Cheap, and the results change what the rest should be (section 9) |
| 1. Cheap, high value | X1, D1, A1, A6, B1, B4, C5 | Small changes that make the agent safer and easier to see into |
| 2. Recover and review | C3, A2, C1, C2, B3 | Rewind, compaction and better review make long agent tasks survivable |
| 3. Smarter context | A3, A5, A8, B2, B6, B9, D2 | Cuts wasted tokens; skills and structured calls make agents more reliable, and folder trust (B9) must be in place before any project-provided skill, hook, agent or command file is read |
| 4. Extend | D3, D4, D6, B12, A9, E4, E6, E7 | Extension points, once the basics are solid |
| 5. Parallel and unattended | D5, D8, B7, E8, D10 | Depends on worktrees, the shared core, and sandbox research |
| Anytime | A4, A7, A10, A11, A12, B10, B11, C6, C7, D7, D9, E1, E3, E4, E5, E9, E11 | Independent; pick up between phases (D7 needs D2 to D6) |
| 6. Harden and release | Q3 (section 10) | After phases 1 to 5, and again before every release; each item has already met Q1 |

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
| A13 | Prompt-prefix stability | S | none | Later |

- **A1 Context breakdown (implemented in both apps; manual smoke pending).** The composer estimates bounded project-file context separately from conversation history; Avalonia also estimates its optional repo map. The last-request view shows character counts for system and personal instructions, project guidance/knowledge, selected or trusted source excerpts, conversation history, tool schemas, and tool results, plus a clearly labeled rough token estimate, provider/model/context limit, and normalized messages. It captures the exact outgoing JSON request body (without HTTP headers) for Chat, Plan, and every Code task round; input-token counts are shown when Ollama or a hosted provider reports them. WPF opens this view from the context-usage label, while Avalonia offers it under More. Snapshots are memory-only and capped to eight conversations. Saved prompt parts from user settings, provider-specific tokenization accuracy for estimates, and cross-platform personal/project knowledge settings remain future work.
- **A2 Compaction and summarize (implemented in WPF and Avalonia; manual smoke pending).** Both apps offer message-level **Summarize up to here** and user-message **Summarize from here** actions, with a reviewable, editable proposal from the current provider/model. The original visible transcript and exported/backup messages are preserved; future prompts use the accepted summary plus messages outside its selected range. When Ollama reports actual prompt usage at or above 80% of an explicitly selected context limit, Codev offers compaction after the turn finishes. When Ollama uses its model-default context, the WPF usage display no longer invents a denominator and warns that context size is unknown, pointing to the explicit size picker or manual summary actions. It never summarizes automatically. **Restore full history** removes the summary. Compaction state survives persistence and backups; clearing history resets it, and rewinding into summarized messages clears the summary. Summarization excludes project files and tools. Never compact silently. Modeled on Claude Code's [checkpointing and `/compact`](https://code.claude.com/docs/en/checkpointing).
- **A3 Path-scoped instructions (partially implemented).** Both apps load root and path-applicable `AGENTS.md` guidance through bounded project reads. Avalonia additionally loads `.codev/rules/*.md` for trusted projects: `activation: files` (default) loads when a `globs` entry matches an included path, `activation: always` loads in every eligible trusted-project request, `activation: model` uses a short metadata-only preflight with the selected model, and `@rule:<name>` loads only the named rule for that request. The preflight receives the current task, included file paths, and rule descriptions; rule bodies enter only the main request after selection. It is bounded to 32 candidates and 256 output tokens. Failure skips model-activated rules but does not fail the main turn. Hosted preflights are an additional API request and run only when project context is opted in. The Avalonia composer offers valid rules after typing `@rule:`. Reads reject links, path escapes, excluded files, invalid or oversized definitions; the combined instruction and source context stays within 32,000 characters. Untrusted projects do not load Avalonia path rules. Rules use `description`, optional `activation`, and (for `files`) comma-separated `globs` frontmatter, followed by Markdown guidance. Nested `AGENTS.md` files currently combine parent to child without a more-specific override. Keep rules short and reference files rather than copying their contents, so they do not go stale.
- **A4 Suggested memory notes.** After a task, propose short notes for project knowledge (build command, convention, correction the user made). The user accepts, edits or rejects each; nothing is saved silently. Respects the 20,000-character knowledge cap. An optional structured layout is possible, as in [Cline's Memory Bank](https://docs.cline.bot/features/memory-bank) (project brief, tech context, active context, progress). Cline reads every one of those files at the start of every task, which is costly on a small local context window, so Codev should load only a short summary, pull the rest on demand, and show the cost in A1.
- **A5 `/init`.** The model inspects the project and proposes an `AGENTS.md` (build and test commands, layout, conventions). It arrives as a normal reviewed file creation. Pairs with existing root `AGENTS.md` support.
- **A6 `/status` (implemented in both apps).** Sending `/status` prints the active provider/model, context window, last matching-model request's input-token use (with a percentage only when the local context limit is known), conversation-summary state, project instruction file names from the last captured request, temperature, mode, project and selected-context scope, queue state, and hosted-request state locally without a model call. It also reports the current project command-permission mode and rule counts. WPF treats an explicitly attached existing folder as the user's selected project and distinguishes modes where automatic source context is off. Manual UI smoke remains.
- **A7 `@`-mentions (implemented in both apps).** Typing `@` in the composer completes safe, supported project-relative file names; choosing one adds it to the conversation's selected context and inserts the path into the draft. Suggestions use the shared file allowlist for common .NET, JavaScript/TypeScript, Python, Java, Go, Rust, C/C++, Kotlin, Swift, PHP, Ruby, Dart, and web component source, plus common text/configuration formats; binaries and archives remain excluded from context and agent file tools. The suggestion service honors project context exclusions and the 24-file selection limit. WPF and Avalonia both support arrow-key selection, Enter/Tab completion, and Escape dismissal.
- **A8 Repo map (implemented in Avalonia, initial version).** An opt-in compact outline of safe project files and common declarations that can be included in context so a small model knows the layout without reading every source file. It is capped at 160 files and 8,000 characters, uses the same trusted-folder or explicitly-selected-file scope and hosted-context consent as project excerpts, and captures the choice per queued turn. The composer estimates a maximum of about 2,000 tokens. Language-aware top-level symbol parsing, integration in the full A1 context breakdown, and the broader WPF port remain future work. Modeled on [Aider's repository map](https://aider.chat/docs/).
- **A9 Embeddings search.** Opt-in index of project files using an Ollama embedding model, so "where is X handled?" finds relevant files without hand-picking. Index lives under the data folder, updates incrementally, and can be deleted from settings. Complements the existing literal search. Needs an embedding model installed; say so instead of failing.
- **A10 Helper model.** Let the user pick a small Ollama model for conversation titles and compaction summaries so the main coding model is not reloaded or interrupted for chores (OpenCode does this with hidden system agents). Default is "use the same model", the safe choice on limited RAM.
- **A11 Conversation recall.** Let the model search the user's past conversations, using the existing conversation search, scoped to the current project by default. The search appears as a visible tool call. Off by default, because it moves old text into the context. Modeled on Goose's Chat Recall extension.
- **A12 Multi-folder projects.** A project can list extra folders (for example a web app and its API) as additional roots. Extra roots are read-only until the user grants write access to each one, and the same bounds, exclusions and secret-file rules apply to every root. Gemini CLI's `includeDirectories` setting and Cline's multi-root workspaces do the same.
- **A13 Prompt-prefix stability.** Order every request so its start stays identical from turn to turn (system instructions, project instructions and knowledge, `AGENTS.md`, tool definitions) and only the end changes (history, the new message, tool results). Ollama can reuse its already-processed copy of a matching start (confirm this on the installed version), which should make later turns and agent steps faster, especially on a CPU where reading a prompt is slow (about 20 to 130 tokens per second in the benchmark). Measure before and after on the same conversation with E10's prompt-reading speed and time to first token. Watch for things that silently change the start: a timestamp, a rotating tip, a reordered file list, a changed set of selected files. Do not reorder anything that could change the model's behavior without re-running the benchmark.

#### B. Agent quality and safety

| ID | Feature | Size | Depends on | Tier |
|---|---|---|---|---|
| B1 | Permission modes and allowlist | M | none | Core · in progress (shared WPF/Avalonia rules + conservative read-only classifier) |
| B2 | Structured tool calls | M | none | Later |
| B3 | Diff edits alongside whole-file edits | M | none | Done |
| B4 | Step limit and loop detection | S | none | Core |
| B5 | Test and lint loop | M | B1 | Done |
| B6 | Show model reasoning | S | none | Done |
| B7 | Sandbox research | L | X3, per OS | Speculative |
| B8 | Loaded-model awareness | S | none | Done |
| B9 | Folder trust | M | none; must land before D2, D3, D4 and D6 read project files | Core |
| B10 | Untrusted content handling | S–M | B1 | Later |
| B11 | Task checklist | S–M | none | In progress (Avalonia and WPF) |
| B12 | Model manager: recommend, download, upgrade, remove | L | B8, V3 results | Later |
| B13 | Advanced model settings (thinking, sampling, token budget) | M | B6 | In progress |

- **B1 Permission modes and allowlist (partially implemented in WPF and Avalonia).** Code task commands default to *ask every time*. A trusted project can opt into exact command allow rules; choosing **Allow exact + run** adds that exact command to a local per-project list and enables allowlist mode. Exact allows skip the command dialog only in that mode; commands not on the list still ask. A separate *read-only commands* mode recognizes only simple `pwd`/location and directory-listing commands on PowerShell and common Unix shells. Explicit file reads are auto-approved only on Windows, where safe-handle checks reject hard links, reparse files, and paths outside the project; Unix file-content reads continue to require approval until equivalent checks exist. The classifier rejects shell operators, redirects, quoting, wrappers, options, absolute/traversing/hidden paths, symlinked paths, sensitive filenames, ignored build/dependency folders, project-excluded paths, Git, and Codev app data; unrecognized syntax still prompts. Recognized operations execute through bounded .NET filesystem APIs, not a shell; reads cap files at 256 KiB, listings at 200 entries, and tool output at 8,000 characters. Verification commands always require approval. This classifier is deliberately incomplete and is not a sandbox; directory enumeration and broader filesystem-race risks still need security review. Exact deny rules always block, even in Ask mode, and take precedence over both auto-approval modes. Both desktop clients share local command rules, provide removal controls, and keep command policy separate from file edit approval. A corrupt or unwritable permission file cannot enable auto-approval. Remaining before B1 is complete: broaden syntax and indirect-path analysis with security-focused tests, address remaining directory-enumeration/path-race risks, and complete manual security review. Allowlisted commands can run with the user's full account privileges; commands are not sandboxed. This follows the deny-first principle in Claude Code's [permission system](https://code.claude.com/docs/en/permissions).
- **B2 Structured tool calls (partially implemented in Avalonia Code task).** Avalonia uses Ollama's native function calls with JSON-schema tool definitions. OpenAI Responses tools are converted to strict function schemas: object fields are required, nested objects disallow extra fields, and Codev's runtime argument validator retains the authoritative length/count limits. Every project tool rejects non-object arguments, non-string or missing fields, unknown fields, and values beyond per-field limits before file review or command approval; checklist arguments follow the same closed-field and size contract. Still to do: use [JSON-schema structured outputs](https://docs.ollama.com/capabilities/structured-outputs) for local plans and summaries where supported, keep a validated fallback for models without support, and record the path used in tool status.
- **B3 Diff edits (implemented for Avalonia Code task).** The agent can propose a strict, single-file unified-hunk patch or a whole-file replacement. Patches are applied in memory with exact line/context checks and no fuzzy matching; malformed, mismatched, or file-header patches fail before review and leave the workspace unchanged. Valid patches show the original, proposed patch, and complete resulting file in one review, then use the same approval, checkpoint, and hash concurrency check as replacement edits. [Aider supports several edit formats](https://aider.chat/docs/) for the same token-efficiency motivation.
- **B4 Step limit and loop detection (implemented for both the WPF and Avalonia Code task runners).** Codev caps a task at eight tool rounds and now pauses for a user decision when the same tool name and canonicalized arguments repeat three consecutive times. Choosing Yes permits that call once and restarts the detector; No stops the task with an explanation. Tests cover reordered JSON properties, changed calls, and the continue-once reset. OpenCode's `doom_loop` permission fires when the same tool call repeats 3 times with identical input and defaults to *ask* ([permissions docs](https://opencode.ai/docs/permissions/)).
- **B5 Test and lint loop (implemented in Avalonia Code task).** The model can propose a test or lint command through a distinct `verify_command` tool. Codev shows a verification-specific approval dialog for every run, reports the exit code and bounded output in the transcript, and only identifies exit code 0 as a pass. Failure output returns to the model; up to two reviewed repair cycles are allowed, after which file edits and commands are blocked for that task. Verification is optional, not silently selected or run.
- **B6 Show model thinking (implemented in Avalonia).** The per-conversation Think control sends Ollama's boolean `think` setting, captured with queued turns and backups. Returned `message.thinking` deltas are saved separately from the assistant answer and displayed in a collapsed, selectable Markdown section. Thinking text is omitted from subsequent prompts; hosted providers do not show it. Models that do not support a boolean thinking control follow Ollama's documented model default.
- **B7 Sandbox research.** Codex separates *what a process may physically do* (read-only, workspace-write, full access) from *when it must ask*; Codev has only the second. Real sandboxing needs a different mechanism on each OS, and [Codex's own Windows sandbox write-up](https://openai.com/index/building-codex-windows-sandbox/) shows it is hard to get right. This item is research first: write down what each OS offers, prototype, and only then add a mode. No mode is described as safe until tested. The simplest cross-platform candidate to prototype first is running commands in a Docker or Podman container with the project mounted, which is what Gemini CLI offers as its sandbox option. It needs a container runtime installed, so make it optional and detect it.
- **B8 Loaded-model awareness (implemented in Avalonia).** A Memory view reads Ollama's live `/api/ps` result, showing loaded model names, server-reported size, VRAM allocation, context length when supplied, and expiry time. The user can confirm unloading an installed model through the documented `keep_alive: 0` API action. Unload is refused during generation or while requests are queued, and sending is briefly held during the unload. These API values are labeled as server stats, not a complete system RAM measurement.
- **B9 Folder trust (implemented for Avalonia chat; project integrations remain future work).** Folder trust is stored in a separate readable local JSON file, can be revoked, and appears in `/status`. The first attachment dialog offers the current folder, its parent and descendants, or keeping it untrusted. Untrusted folders still allow chat, browsing, and explicitly selected files; automatic bounded project excerpts require trust. Hosted context still requires its separate opt-in. Avalonia chat does not currently load project instructions, skills, commands, hooks, or MCP settings; if any are added, trust must gate them before they are read or executed. A corrupt trust file stays untouched and leaves projects untrusted. The broader security behavior is informed by Gemini CLI's [trusted folders](https://geminicli.com/docs/cli/trusted-folders/) and Claude Code's [security model](https://code.claude.com/docs/en/security).
- **B10 Untrusted content handling (partially implemented in Avalonia Code task).** Project file listings, file contents, search results, and approved command/verification output are JSON-enveloped with an `untrusted_tool_output` marker. The Code task system prompt says this material is evidence, never authority to override the user, change goals, disclose secrets, or broaden access. File and command approval dialogs warn that proposals can reflect embedded instructions and show a bounded list of project context, search, file, and command sources exposed so far in the task. When a proposed command matches a line in previously read/search/command output, Codev flags the match and names the source before approval. A heuristic also flags common instruction-override, secret-disclosure, safeguard-bypass, concealment, and impersonation language in proposed file and command text. For common shell command families, equivalent command spellings are also flagged when they share a concrete file, URL, or sensitive-variable target; mismatched targets are tested as negatives. These checks only warn, never block, and may produce false positives. Remaining: extend the boundary to future tool surfaces such as web and MCP, and run the manual red-team smoke check. This is a defense-in-depth prompt and review aid, not a sandbox or a guarantee against prompt injection.
- **B11 Task checklist (implemented in Avalonia and WPF Code task).** For multi-step tasks the agent keeps a visible checklist (pending, in progress, done) that the user can edit or reorder. It is stored with the conversation and re-inserted into later Code task requests after compaction (A2); clearing chat history clears its checklist too. Checklist entries are structured task data and never grant tools or permissions. The checklist is visible in Plan mode when the conversation already has entries; richer structured plans and manual QA remain. This is the concrete design for the "richer plans remain" item in the feature status. Goose has a Todo extension and Claude Code a todo tool for the same purpose.
- **B12 Model manager: recommend, download, upgrade, remove.** Help the user keep a good, right-sized set of models without leaving Codev, and clean up old ones. Motivation: models change every few weeks, and a machine can end up with many multi-gigabyte downloads, several of which are aliases of the same files (the same digest under different names shares disk space, so a naive "size" total overstates what deleting one frees).
  - *Budget.* The user sets a size budget (the largest model they are willing to hold, and a disk reserve to keep free). Codev detects total and free RAM, free disk and, where it can, GPU memory, and shows an estimate of what a model needs: its file size plus context memory plus headroom for the operating system. It warns when a model would not fit or would leave the machine slow. Hardware detection is per OS (see section 7), and integrated graphics that share system memory must not be counted as separate video memory.
  - *Recommendations come from a catalog, not from guessing.* Ollama's API can list, pull, inspect and delete models you have (`GET /api/tags`, `POST /api/pull`, `POST /api/show`, `DELETE /api/delete`, checked against its [API reference](https://github.com/ollama/ollama/blob/main/docs/api.md) on 2026-09-27), but none of the endpoints I reviewed browses the online library or says which model is newest (confirm this before building). So Codev needs a small curated catalog (a JSON file shipped with the app and refreshable from a URL only if the user opts in). Each entry records the family, size at each quantization, parameters and *active* parameters (a mixture-of-experts model with few active parameters is much faster on CPU-only machines), context length, capabilities (tools, thinking, vision), license, release date, and a tier such as *coding agent* or *general*. The catalog is the part that needs maintenance, so it must be small and easy to update, and it must say when it was last checked.
  - *Newer is not automatically better.* Benchmark scores are self-reported, depend on the wrapper the model ran in, and often come from different setups (see the V3 findings). So an upgrade is a *suggestion with a test*: offer to run Codev's own small task suite (V3) on the candidate and show the old and new models side by side (speed, tool-call validity, task pass rate) before the user decides. Never switch a conversation's model automatically.
  - *Download.* Opt-in, per model, showing the download size, the estimated fit and the licence first, with progress, cancel and resume (Ollama resumes cancelled pulls and shares progress between repeated calls). Say plainly that this contacts the model registry and that nothing from the project is sent. This is the only part of Codev that reaches the internet by design, so it belongs behind its own setting, off by default, like the other opt-in network features.
  - *Updates.* Re-pulling a tag fetches a newer build of the same model; detect it on request ("check for updates") rather than in the background, and show the size before downloading.
  - *Removal.* Never delete automatically. Suggest models that are clearly superseded (same family, older generation, or unused for a long time), list how much space would actually be freed (accounting for shared files), and show which conversations and project defaults use each model so a removal does not break them; offer to move those to a chosen replacement first. Always confirm, and never remove a model that is currently loaded or queued.
  - *Reporting.* Show what is installed, what is loaded now (B8), sizes, last used, and the measured speed from E10, so decisions are based on the user's own machine.
  - *Depends on:* B8 (what is loaded), E10 (speed measurements) and the V3 test suite; also X-steps for cross-platform hardware detection. Start small: a read-only "installed models" view with sizes, aliases and last-used, then a safe remove, then catalog-based recommendations, then guided upgrades.
- **B13 Advanced model settings (partially implemented in Avalonia).** The per-conversation **Advanced** dialog now exposes temperature, top_p, top_k, presence_penalty, repeat_penalty, and num_predict (the combined reasoning and answer token limit). Blank fields send no override; values are validated, saved in conversations/backups, captured for queued turns, applied to local chat and Code task calls, and shown by /status. The **Think** toggle remains separate. Named user presets can now be saved, updated, removed, imported, and exported; they are stored locally, validated, and do not change settings until explicitly applied and saved to the conversation. Design still to do:
  - *The default is always "model default".* Send nothing unless the user overrides a value, so each model's own Ollama defaults apply. The Advanced dialog's **Load model defaults…** action reads declared values from Ollama's `POST /api/show` parameters field; an unlisted value is labeled as not declared because Ollama's built-in runtime value may not be discoverable there. The request format is checked against Ollama's [API reference](https://github.com/ollama/ollama/blob/main/docs/api.md#show-model-information). The official Qwen3.6 build can declare `presence_penalty 1.5`, so users can now inspect model-declared values without applying an override.
  - *Where it lives.* Avalonia exposes an **Advanced** section from the conversation's **More** menu, with a reset-to-default action, a per-field **Default** action, and a visible marker whenever anything is overridden (also shown by `/status`, A6). A Settings-page entry remains future work.
  - *Plain-language help.* Avalonia gives each setting a concise description of its effect and practical trade-offs, alongside the accepted range.
  - *Validated ranges.* Reject values outside what the setting accepts, and show only the settings the chosen backend supports (hosted providers expose different parameters from Ollama).
  - *Held fixed for comparisons.* B12's "test before upgrade" must hold these settings fixed between builds (see the V3 finding on sampling settings).
  - *No recommended preset yet.* Which values help is being measured in `bench/` (presence penalty 0, 0.5, 1.0 and 1.5 on Qwen3.6-35B-A3B). Do not ship a default that differs from the model's own until repeated runs show a difference beyond the noise, since scores varied by up to 33 points between runs of one task.

#### C. Review, Git and recovery

| ID | Feature | Size | Depends on | Tier |
|---|---|---|---|---|
| C1 | Richer review pane | M | none | Core |
| C2 | `/review` pass | M | D1 | In progress (working tree, commit, and branch review in Avalonia) |
| C3 | Rewind | M | none | Core |
| C4 | Resume by project, fork a conversation | S | none | Done |
| C5 | Queue a follow-up while running | S | none | Core |
| C6 | Edit any earlier message | S–M | C3 | Done (Avalonia) |
| C7 | Whole-tree snapshot before commands | M–L (prototype first) | C3 | Speculative |
| C8 | Security review pass | S–M | C2 | In progress (working tree, commit, and branch review in Avalonia) |
| C9 | Draft commit messages and PR descriptions | S | none | In progress (staged commit-message draft in Avalonia) |

- **C1 Richer review pane (partially implemented).** Per [Codex's review pane](https://learn.chatgpt.com/docs/code-review?surface=app): stage, unstage and revert at three levels (whole diff, per file, **per hunk**; Codev is per file today); choose the scope to review (uncommitted changes, **the assistant's last turn**, a branch against a base, one commit); and attach a comment to selected diff excerpts. Avalonia comments stay in the conversation and composer until sent, then travel with the user message as guidance; they are included in conversation persistence and backups. Hunk-level stage/revert and broader review scopes remain future work.
- **C2 `/review` (partially implemented in Avalonia).** Separate read-only passes review bounded staged, unstaged, untracked, selected commit, and selected branch changes through the selected local Ollama model, only for a trusted project and loopback endpoint. Each input caps at 40 files and 40,000 diff characters; changed content is marked untrusted, and the model receives no tools. Findings appear in a selectable report dialog labeled a second opinion; output quality depends on the model. Manual QA remains.
- **C8 `/security-review` (partially implemented in Avalonia).** Runs a bounded scan of uncommitted Git changes, a selected commit, or current branch changes against a selected local base branch in an attached trusted project. A deterministic local scan checks added diff lines for common credential patterns without retaining or displaying matched values. If an installed local Ollama model is selected, it also runs a security-focused read-only second opinion; remote providers are never sent the diff. This is a heuristic and cannot prove a change is secure. Manual QA remains.
- **C3 Rewind (partially implemented).** Avalonia can restore the conversation to before a selected user prompt, after confirmation, and place that prompt back in the composer. This mode deliberately leaves project files unchanged. The remaining implementation must connect each Codev-managed file checkpoint to its turn and offer reviewed *restore code and conversation*, *conversation only*, and *code only* choices; commands and edits made outside Codev remain outside the checkpoint history. Keep a bounded number of checkpoints, report missing snapshots, skip symlinks/hard links with a visible warning, and state that checkpoints are not a replacement for Git. After a restore, re-present the original pending action (an edit or a command) so the user can re-run, change or reject it, as Gemini CLI's `/restore` does. See C7 for covering changes made by shell commands.
- **C4 Resume and fork (implemented in Avalonia).** When a user attaches a folder with prior chats, Codev offers to resume the most recent non-archived chat for that normalized path, attach the folder to the current chat, or start fresh. A sidebar action forks an idle conversation with its messages, model/context/mode/output settings, project association, diff comments, and independent copies of Codev-managed file checkpoints. Active or queued conversations cannot be forked; missing, oversized, linked, or out-of-scope checkpoint files fail the operation and clean up any partial copy.
- **C5 Queue a follow-up.** Let the user type and queue a message while a response is running. The WPF queue and Avalonia port support this; an enqueued message is its own saved user/assistant pair, and queued turns use the model/context captured at enqueue time. A message that joins a running turn is not its own rewind point.
- **C6 Edit any earlier message (implemented in Avalonia).** Each user prompt has an **Edit prompt** action. The user revises it in a dialog, then confirms; Codev removes that prompt and all later conversation messages, puts the revised text in the composer, and leaves project files unchanged. The user sends it when ready. This uses C3's conversation rewind behavior and makes the consequence visible before applying the edit.
- **C7 Whole-tree snapshot before commands.** C3's main gap is that shell-made changes are not tracked. Gemini CLI stores its [checkpoints](https://geminicli.com/docs/cli/checkpointing/) as commits in a separate "shadow" Git repository outside the project (together with the conversation and the tool call that triggered it), so they never touch the project's own history. Codev could use the same approach for file edits and also snapshot the whole project tree (respecting ignore rules) before an approved command, so undo covers shell changes too. That extension is an inference, not something either tool documents. Prototype it on a large repository and measure time, disk use, and behavior with ignored, binary, huge and symlinked files before committing to it.
- **C8 Security review pass.** A read-only model pass over uncommitted changes, a commit or a branch that looks specifically for security problems (unvalidated input, committed secrets, unsafe file or command handling, missing authorization checks) and returns findings by priority with the file and line. Same rules as C2: it never edits, and it is labeled a second opinion. Claude Code has an on-demand [`/security-review`](https://code.claude.com/docs/en/security) for the same purpose. Ship it as a review mode of C2 with its own prompt, plus a check that needs no model (a simple secret-pattern scan of the diff) so it is useful even when the model is weak.
- **C9 Draft commit messages and PR descriptions (partially implemented in Avalonia).** After staging, offer to draft a commit message from the bounded staged diff and recent local commit subjects through loopback Ollama; show it in the commit dialog for the user to edit. Drafting rechecks that staging did not change, and never creates the commit automatically. `/pr-description` drafts a title and description from a local branch diff against a selected base branch, shown as selectable text; it rechecks the branch diff and does not create or send a PR. Creating the pull request itself is D13. Manual UI QA remains. Claude Code and Codex both write commit messages and open pull requests ([Claude Code overview](https://code.claude.com/docs/en/overview)).

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
| D11 | Background commands | M | B1 | Later |
| D12 | Testing a running app | L | B1, D11, a vision model | Speculative |
| D13 | CI and GitHub integration | M | E7 | Speculative |

- **D1 Slash commands and file-based commands (partially implemented in both apps).** Typing `/` opens keyboard-navigable suggestions from the shared catalog. WPF runs `/plan`, `/code`, `/clear`, `/model`, `/export`, `/status`, `/compact`, and `/commands`; `/init`, saved prompt templates, and user Markdown commands insert editable prompts and do not send until the user submits them. Named command arguments expand from bounded, validated Markdown files in the user command folder; WPF refreshes the folder as the composer is used. `/clear` asks for confirmation and preserves project selection and reviewed file-change history. Avalonia additionally supports `/skills`, read-only local Git review and Markdown user/project commands with named arguments, bounds, validation, and folder guidance; project commands can be shared through Git. Avalonia imports existing WPF settings-backed prompt templates once, manages them locally, and exposes them as `/template-…` suggestions. WPF project command files remain unavailable until its project-trust workflow is implemented. Agent/tool constraints remain. A command file may later name an agent (D3) and limit its tools, allowing saved tasks to be run from the menu, headlessly (E7), or on a schedule (D8); Goose calls these recipes (its recipe format was not reviewed).
- **D2 Skills (partially implemented in Avalonia).** User and trusted-project folders of Markdown skills are discoverable through slash suggestions and `/skills`. Only metadata is retained in suggestions; selecting `/skill-name` reloads the prompt, expands named arguments, and inserts it into the composer for review. Project definitions override user skills; built-in and user slash commands take precedence over colliding skill names. This first stage is explicitly user-invoked and treats Markdown as data. Remaining work: optional scripts through the approval-gated command tool, skill metadata for agent invocation, and richer migration from existing custom commands. Design, from Claude Code's [extension guide](https://code.claude.com/docs/en/features-overview): keep only each skill's name and short description in model context until used, and allow skills to opt into user-invocation-only behavior.
- **D3 Agent definition files.** A markdown file per agent, global or per project, with a header for name, description, model, temperature, maximum steps and an ask/allow/deny permission per tool, and a body that is the system prompt ([OpenCode's agent format](https://opencode.ai/docs/agents/)). Use it for Plan and Code modes too, so a mode is just an agent with permissions. Include a one-key Plan/Code toggle (E2). Ship these built-in agents, following [Roo Code's modes](https://roocodeinc.github.io/Roo-Code/basic-usage/using-modes): *Ask* (read and MCP tools only, no edits or commands), *Architect/Plan* (read plus edits limited to markdown files), *Code* (full access), *Debug* (systematic troubleshooting: reproduce, form a hypothesis, instrument, confirm before fixing) and *Orchestrator* (no direct tools; delegates to other agents, see D5). Two more ideas worth copying: a per-agent **edit path restriction** (a pattern such as `*.md` that the agent may write, enforced by the same path checks as B1), and a per-agent **remembered model**, so planning can use a larger model and coding a coder model, with a visible notice when a switch means loading a different model (see B8). Zed's agent "profiles" are the same idea: a named set of enabled tools per conversation.
- **D4 Hooks.** User-configured commands that run before or after tool actions. The first and safest hook is a **formatter after edits**: run one named formatter on the changed file, show its output, and record the result in the checkpoint so undo covers it. All hooks are visible in settings, logged in run history, and off by default; a hook is not a guardrail unless it can block the action, and blocking hooks come later.
- **D5 Subagents with child sessions.** A subagent is an agent definition (D3) run in its own context. Show it as a collapsible child conversation under the parent that can be opened and resumed, not an invisible side effect. A cheap small model can do read-only exploration and return a summary, keeping the main model's context clean. Keep the depth limit at 1 (subagents cannot start subagents) and the concurrency limit at the serial queue: local inference is serialized, so say so instead of implying a speed-up. See [Claude Code subagents](https://code.claude.com/docs/en/sub-agents) and OpenCode's child sessions. The Orchestrator agent (Roo Code calls it "boomerang" mode) is a planner whose only tool is "delegate this task to agent X"; on local hardware its subtasks still run one after another.
- **D6 MCP client.** MCP-compatible tools with per-server enablement, visible permissions and logs; local-only use stays simple. Tool schemas load on demand so idle servers cost little context.
- **D7 Plugins.** One installable folder that bundles skills, agents, hooks and MCP servers, with names scoped per plugin. Only after those pieces exist.
- **D8 Automations and review inbox.** Scheduled or manual runs that deliver results to an inbox. Per [Codex's scheduled tasks page](https://learn.chatgpt.com/docs/automations?surface=app), each run in a Git repository can use *worktree mode* (changes stay isolated from the user's checkout) or *local mode* (edits the main checkout directly); Codev should default to a worktree per run and require an explicit choice for local mode. Schedules are time-based, and Codex also offers event triggers (Gmail, Slack, GitHub) on some plans; it does not mention webhooks. Skip event triggers and webhooks at first, since they need connectors or a listening server, which is a security surface (localhost only with a token, if ever). Codex's Scheduled view is an inbox of active, paused and completed tasks with an unread indicator for runs that need attention; copy that. Unattended runs use the default sandbox settings, so any tool call that needs more than the sandbox allows simply fails; until B7 exists, Codev has no sandbox, so unattended runs should be read-only by default and every write should wait in the inbox for approval. Codex's desktop app must stay running for tasks that touch local files; say the same. Local models are slow, so show queue position and expected wait. (An earlier third-party description said runs with nothing to report archive themselves; the official page does not say that, so treat it as unconfirmed.)
- **D9 Opt-in web search and fetch.** Off by default, clearly labeled as leaving the machine, per-domain permission, and never sends project content to a search query without showing it. Consistent with local-first.
- **D10 Code intelligence.** Opt-in per-language language-server support: go to definition, find references, and live type errors after an edit, so a small model can check its own work. Larger than A8; do A8 first.
- **D11 Background commands.** Let the agent start a long-running command (a dev server, a file watcher, a slow test run) without waiting for it to finish, keep working, read its output so far, and stop it. Claude Code's shell tool has a background mode for this; Codev's command tool today has a three-minute timeout and nothing else. Rules: each start is approved like any command (B1) and shows the exact command; a visible list of running background commands, each with how long it has run and a Stop button; output is bounded and read on request; every background process ends when the conversation closes or Codev exits; a hard limit on how many can run at once; and the approval prompt flags commands that open a port or run indefinitely.
- **D12 Testing a running app.** An opt-in tool that lets the agent open a page from the user's own running app in a controlled browser, take a screenshot, read console and network errors, and click through a flow, so it can check its own UI work. Claude Code does this through [a Chrome integration](https://code.claude.com/docs/en/overview). It needs strict limits: local addresses only (localhost and the project's own dev server) unless the user allows a host; a separate browser profile with no saved sign-ins; screenshots go only to a vision-capable model the user has chosen, shown to the user first; off by default. It depends on D11 (a dev server to open), B1 and a vision model, so treat it as research first.
- **D13 CI and GitHub integration.** A recipe and, if it proves useful, a GitHub Action that runs Codev's headless mode (E7) in continuous integration, for example to review a pull request's diff or draft release notes, plus opening a pull request from the current branch through the user's own `gh` login. Claude Code and Codex both offer GitHub Actions or pull-request review ([Claude Code overview](https://code.claude.com/docs/en/overview)). Off by default, it needs the user's explicit token or login, and a local model must be reachable from the CI runner, so it is mostly useful with self-hosted runners.

#### E. Everyday use and input/output

| ID | Feature | Size | Depends on | Tier |
|---|---|---|---|---|
| E1 | Output styles | S | none | Done |
| E2 | Plan/Code mode shortcut | S | none | Done |
| E3 | Themes and key bindings | S–M | none | Later |
| E4 | Self-contained HTML export | S | none | In progress |
| E5 | Preview for HTML/SVG code blocks | M | none | Speculative |
| E6 | Local voice | M | X-steps for OS-specific parts | Later |
| E7 | Headless mode | M | X2 | Later |
| E8 | Local server for multiple front ends | L | X2, decision | Speculative |
| E9 | Compare models side by side | M | B8 helpful | Speculative |
| E10 | Generation stats | S | none | Done |
| E11 | Follow the agent | S–M | none | Later |
| E12 | Terminal pane | M | D11 helpful | Later |

- **E1 Output styles (implemented in Avalonia).** A per-conversation selector offers balanced, concise, explanatory, and code-only styles. The selection is stored with conversations and captured in each queued turn; it changes response presentation only, never tools or permissions. Even code-only responses must disclose failures, caveats, and unverified work.
- **E2 Plan/Code toggle (implemented in Avalonia).** `Ctrl+Shift+M` cycles Chat → Plan → Code task → Chat. The current mode remains visible on the mode controls and in the provider/mode status. Code task creates and trusts a dedicated per-conversation workspace when no folder is attached; an attached existing folder still needs explicit trust. Local mode requires loopback Ollama; hosted mode currently supports OpenAI only after cloud and project-context consent. The shortcut never bypasses per-file review or command approval, and mode changes are blocked while a response is running. Broader rebindable shortcuts remain under E3.
- **E3 Themes and key bindings.** Custom themes and rebindable shortcuts; section 5 already lists broader shortcut customization as remaining.
- **E4 HTML export (implemented in Avalonia).** Export a conversation as one self-contained offline HTML transcript with embedded responsive/print styling, rendered Markdown/code/tables, model/date metadata, reviewed-file summary, and bounded Codev-managed file diffs. Raw HTML is disabled in Markdown, unsafe link schemes are inert, and a content policy blocks scripts, external styles and images. Full local paths and checkpoint locations are omitted. Before saving, a review lists masked matches for common credential patterns in the transcript and included diffs; the user can redact selected matches or export without redaction. Detection is heuristic and warns that it can miss secrets. Replaces the hosted share links other tools offer, which Codev will not do.
- **E5 Preview.** Render HTML, SVG and Markdown code blocks in a sandboxed pane with scripts and network turned off (a small local take on Claude's artifacts).
- **E6 Local voice.** Dictation into the composer, and optionally spoken replies, without any hosted service. Put both routes behind small `ISpeechToText` / `ITextToSpeech` interfaces so the composer does not care which is active: (1) *built-in OS speech*, no download: `System.Speech` and `Windows.Media.SpeechRecognition` on Windows (Windows-only, accuracy usually lower than Whisper), `SFSpeechRecognizer` on macOS (on-device only if Dictation is enabled and its language downloaded, per [Apple's docs](https://developer.apple.com/documentation/speech/sfspeechrecognizer)), and nothing comparable built in for recognition on Linux (speech output there is normally `speech-dispatcher`, to be checked per distribution); (2) *a bundled local model*, same behavior everywhere: [whisper.cpp](https://github.com/ggml-org/whisper.cpp) through its .NET binding [Whisper.net](https://github.com/sandrohanea/whisper.net) for speech to text, and [Piper](https://github.com/rhasspy/piper) for speech output, at the cost of a download and RAM competing with the Ollama model. Start with push-to-talk dictation that fills the composer for review before sending. Never label an engine "stays on this machine" until it has been confirmed offline on that platform. Findings, checked 2026-09-27: Windows voice typing (Win+H) streams audio to Microsoft's cloud speech service and needs an internet connection, so it is *not* private; Windows Voice Access runs on-device and works offline ([Microsoft's speech privacy page](https://support.microsoft.com/en-us/windows/privacy/speech-voice-activation-inking-typing-and-privacy) and third-party guides; confirm Voice Access's engine before relying on it for Codev's own dictation, since it is a whole-PC control feature, not an API). On Apple-silicon Macs, Dictation is processed on the device for supported languages, and declining the prompt to send dictation to Apple keeps it offline. whisper.cpp model sizes ([project README](https://github.com/ggml-org/whisper.cpp)): tiny 75 MiB on disk and about 273 MB of RAM, base 142 MiB and about 388 MB, small 466 MiB and about 852 MB, medium 1.5 GiB and about 2.1 GB, large 2.9 GiB and about 3.9 GB; it supports CPU-only use and integer-quantized models. A small or base model is a reasonable default for dictation, and its RAM competes with the Ollama model.
- **E7 Headless mode.** `codev -p "prompt"` with piped input for scripting and CI, read-only unless explicitly allowed, reusing `Codev.Core`. Modeled on `codex exec` and `claude -p`.
- **E8 Local server.** OpenCode runs its agent as a local server that the terminal UI, desktop app and editor extensions all talk to over HTTP. It could let Codev's Avalonia app, a command-line mode and an editor extension share one core, but it adds a network surface (localhost only, token required) and a lot of design work. Decide after X2 shows what actually needs sharing; do not build speculatively. Zed's open Agent Client Protocol (ACP) lets an editor host any compatible agent; Codev could one day speak ACP, either to host external agents or to be hosted by an editor. That is speculative and only worth studying if E8 goes ahead. Basics from its [introduction page](https://agentclientprotocol.com/overview/introduction): it standardizes communication between code editors and coding agents, uses JSON-RPC over stdio for local agents, and lists HTTP or WebSocket for remote agents as work in progress. That page did not state who created it, its version or its capabilities, so those remain to be read.
- **E9 Compare models side by side.** Send one prompt to two or more installed models and show the answers next to each other, run one after another on local hardware, and let the user keep one as the conversation's answer. Helps choose between local models. Read-only chat only, never Code mode. Msty's split chat does this.
- **E10 Generation stats (implemented in Avalonia for local Ollama replies).** The assistant message footer displays time to first non-empty response token (measured by Codev), generated tokens per second, output token count, and model load time from the final streaming response. Throughput and load duration use Ollama's documented `eval_count`, `eval_duration`, and `load_duration` fields; stats persist with the conversation. Hosted replies do not show locally inferred performance numbers.
- **E11 Follow the agent.** While the agent reads or edits a file, highlight that file in the project browser and preview pane, with a toggle. [Zed's agent panel](https://zed.dev/docs/ai/agent-panel) has a "follow the agent" mode. Fits the "visible work" principle.
- **E12 Terminal pane.** An interactive terminal in the app, rooted in the project, that the user types into like any terminal. A button sends the last command and its output (or a selection) to the conversation as context, and the agent's approved commands can be shown in the same pane so the user sees them run. Claude's desktop app has a terminal panel for the same reason. Rules: it runs as the user, so nothing typed there needs agent approval, and nothing the agent runs bypasses approval; output shared with the model is bounded and shown to the user first (B10 applies); one shell per project, ended when the project closes.

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
| Core | A1, A2, A6, A7, B1, B4, B9, C1, C3, C5, D1 |
| Later | A3, A4, A5, A8, A10, A12, B2, B10, B12, B13, C2, C6, D2, D3, D4, D6, A13, D11, E12, E3, E4, E6, E7, E11, and X6 and X7 |
| Speculative | A9, A11, B7, C7, D5, D7, D8, D9, D10, D12, D13, E5, E8, E9 |

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

**First results (2026-09-28).** Measured on an AMD Ryzen AI 9 HX 370 laptop with 62 GB of RAM and no usable GPU (Ollama ran on the CPU), using Ollama 0.34.4. Nine JavaScript tasks, each checked automatically against hidden tests: six routine and three hard (a search-filter parser, a behaviour-preserving refactor, and an agent fixing a four-module library with seven bugs, two of them visible as failing tests). Models: qwen3.6:35b-a3b, qwen3.8:27b, qwen3-coder-next (Q2) and qwen3-coder:30b, with thinking on and off for the first two. Full tables and the verdict are in [comparison.html](comparison.html). The test harness and the raw results are in [bench/](bench/README.md), so the numbers can be reproduced and other models can be added.

| Question | Result |
|---|---|
| Does native tool calling work, and how often are arguments invalid? | Yes. Across 23 agent runs on four models there were 0 invalid tool calls, 0 loops and 0 failed edits (B2, B4). |
| Does a patch or a whole-file rewrite apply correctly more often? | Not measured. The agents had both a whole-file write and an exact-text replace tool, and how often each was used was not analyzed (B3). |
| How often does the model repeat itself? | Never in these runs. The runs were short and the tools simple, so keep the loop guard as cheap insurance (B4). |
| Does a task survive compaction? | Not measured (A2, B11). |
| Do a repo map or retrieved files help? | Not measured (A8, A9). |
| Speed on typical hardware | About 26 tokens per second for Qwen3.6-35B-A3B, 17 to 21 for the Qwen3-Coder models, and 6 to 9 for the dense Qwen3.8-27B. Reading the prompt ran at 100 to 144 tokens per second for the mixture-of-experts models against 23 for the dense one (A1, B8, E10). |
| Does thinking mode help? | Rarely. It made runs about 3 to 8 times longer. Both Qwen models used up an 8,000-token budget on the hard filter task (and Qwen3.6 on a medium one), so their answers were cut off. It helped once: Qwen3.8 solved the hard agent task completely with thinking on (29 of 29 tests, against 24 of 29 with it off) (B6). |

Findings that change the backlog:

- **Default thinking off (B6).** Offer it as a per-conversation toggle, show the token budget, and say plainly when thinking ran out before an answer was written.
- **One run is not enough.** The same model varied by up to 33 points between runs of one task (Qwen3.6 on the filter task: 51% to 84%). B12's "test before upgrade" must use several runs before it recommends a switch.
- **Passing the visible tests is not proof.** Every model that fixed only some of the bugs reported that everything was fixed, because the tests it could see passed. A completion message should say which checks were run and that passing them does not prove correctness (the "visible work" principle, and B5).
- **Prompt reading is where dense models lose on a CPU.** The mixture-of-experts models read prompts 4 to 6 times faster, which makes adding a lot of context (A8, A9) cheap for them and expensive for dense models.
- **A bigger thinking budget helps a little, not enough.** Raising the limit to 16,000 tokens per reply (with a 32,000-token context) let Qwen3.6 score 28, 24 and 25 of 29 on the hard agent task over three runs, against 24 of 29 three times with thinking off. It still never matched Qwen3.8 with thinking (29 of 29, one run), and on the filter task it used all 16,000 tokens without writing an answer. If B6 offers a thinking toggle, it should also show that the budget can run out.
- **Smaller was not faster.** Unsloth's UD-Q3_K_M build of Qwen3.6-35B-A3B (16.6 GB), run with the same sampling settings as the official Q4_K_M build (22.1 GB), scored about the same on routine tasks (98% against 97%) and on the hard agent task (24 of 29 both), lower on the filter task (51% against 71%, and that task is noisy), and ran about 43% slower on this CPU (14.7 against 25.9 tokens per second). Estimating speed from file size was wrong here, so B12 must measure speed on the user's machine.
- **Sampling settings differ between builds.** Each Ollama build ships its own defaults (the official Qwen3.6 build sets `presence_penalty 1.5`, `top_k 20` and `top_p 0.95`; an imported build may set none). Any comparison between builds, and B12's "test before upgrade", must hold them fixed (`BENCH_EXTRA_OPTIONS` in bench/).
- **Small samples.** Qwen3.8 has one run per task and the thinking-on runs have one each, so a gap of a few points between Qwen3.6 and Qwen3.8 is not established.

#### V4. Test what exists

**Outcome:** the current features are checked on every supported platform, and real friction feeds back into the backlog.

- **V4a. CI baseline.** Recorded in section 7: the hosted matrix passes the portable suite on Windows, Linux and macOS. Keep it green, and record any new platform-specific failure with its cause instead of skipping the test.
- **V4b. Manual smoke checklist.** Commit and run `docs/avalonia-smoke-checklist.md` for the active Avalonia UI on Windows; run the same checklist on Linux and macOS as those builds become available. Keep WPF-only release checks separate, including the hosted-provider, context-breakdown, file-mention, `/status`, and compaction checks in `docs/wpf-hosted-provider-smoke-checklist.md`, `docs/wpf-context-breakdown-smoke-checklist.md`, `docs/wpf-file-mentions-smoke-checklist.md`, `docs/wpf-status-smoke-checklist.md`, and `docs/wpf-compaction-smoke-checklist.md`. The Avalonia checklist currently covers streaming, chat layout and selection, model/context choice, queue/restart recovery, stop, keyboard input, scroll-follow, theme persistence, and conversation organization. Project context, reviewed edits, approved commands, basic local Git workflows (including selected diff questions), export, backup/import, hosted-provider connection, and custom Ollama endpoint settings need manual interaction checks; Git hunk comments and worktree merge/recovery remain future work.
- **V4c. Real-use log.** Use Codev on a real project for a stretch of time and write down every point of friction, wrong answer and confusing moment. These notes feed V1 and are usually more valuable than another comparison with a competitor.

**Done when:** V4a stays green, the checklist is committed and has been run at least once, and the first real-use log has been read and its items triaged.


### 10. Quality gates and release readiness

Section 8 says what to build and section 9 checks the plan. This section says what "finished" means. It works in two layers: every feature meets a definition of done as it is built, and a hardening pass runs once the features exist and before every release. Doing only the second layer lets problems pile up, and polish is harder to do well long after the code was written.

#### Q1. Definition of done for every feature

An item is not marked Done until each row below is true, or is marked not applicable with a reason.

| # | Check | What it means |
|---|---|---|
| 1 | Behavior is proven | A test that would fail without the feature (pure logic goes in `Codev.Tests`); anything only a person can check goes on the manual checklist (Q2) |
| 2 | Failure states are handled | A clear message and a way forward when Ollama is unreachable, the model is missing or unloaded, a request times out or is cancelled, the disk is full or a file is locked, the project is empty, huge or not a Git repository, a path is outside the project, or a data file is unreadable |
| 3 | Every state exists | First run, empty, loading and error states all look intentional, and nothing blocks the window while it waits |
| 4 | Usable without a mouse | Everything reachable by keyboard in a sensible order, and controls have accessible names for screen readers |
| 5 | Looks right everywhere | Light and dark themes, larger text sizes, a narrow window, and no clipped or overlapping text |
| 6 | Works on every platform | Passes the smoke checklist on Windows, macOS and Linux once the Avalonia port has reached the feature |
| 7 | Safe by default | No new silent write, command or network call; approval prompts show exactly what will happen; secrets and file contents are not written to logs |
| 8 | Data survives | New stored data is versioned and survives an upgrade and a crash mid-write (atomic writes, unreadable files preserved), following the existing pattern |
| 9 | Cost is stated | What the feature costs in context tokens, memory and time, visible where the user can see it (A1) |
| 10 | Documented | The README and in-app help match what the feature does, the feature status row is updated, and any setting's default is written down |

#### Q2. A short smoke card per feature

Each shipped feature adds a short manual checklist to `docs/`, extending `docs/avalonia-smoke-checklist.md` or a file beside it: the steps, the expected result, and a dated log line for each run (build tested, operating system, pass or fail). Keep each card under ten lines. Dated log lines are a record of real runs and are never rewritten later.

#### Q3. The hardening pass

Run after phases 1 to 5 and again before every release, as its own change set.

- **Whole-app smoke run** on Windows, macOS and Linux from a clean install, using the Q2 cards. Test on a machine or virtual machine without development tools installed.
- **Performance budgets.** Set numbers from measurement, then keep to them: startup time, time to first token, and memory with a long conversation open and a large project loaded.
- **Security review** of the parts that can do harm: folder trust (B9), untrusted content handling (B10), the command runner and any background commands (D11), hosted-provider keys (section 8a), MCP servers, and anything a project can bring with it. Include the security review pass (C8) run on Codev's own changes.
- **Data safety.** Open data files written by the previous release; kill the app during a write and reopen it; export and re-import a backup; check that uninstalling leaves the user's data alone.
- **Benchmark re-run.** Run `bench/` on the shipped default settings and models and compare with the previous results, so a regression in agent behavior shows up before users see it.
- **Accessibility and text.** A keyboard-only pass through the main flows, a screen-reader spot check, and large-text and high-contrast checks.
- **Documentation.** The README, in-app help and DESIGN status rows match what the app actually does.

#### Q4. Exit rule and ownership

- **The pass is finished when every Q3 row is ticked or waived with a written reason,** not when nothing could be improved. New findings that are not release blockers become backlog items instead of ad hoc fixes.
- **Release blockers:** data loss, an action taken without the user's approval, a crash on startup, or a wrong claim of success.
- **Ownership.** Whoever builds a feature does Q1 and Q2 for it in the same change. The Q3 pass is a separate change set, recorded in `docs/` with the date, the build and the results.


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
