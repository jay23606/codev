# Codev design and roadmap

## Product direction

Codev is a standalone Windows coding workspace for local models. It takes inspiration from the clear conversation experience of Claude Desktop and the project/thread, coding-agent, and review workflows of Codex, while keeping inference on the user's Ollama installation. It does not depend on VS Code.

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
| Ollama endpoint and per-conversation context size | Configurable HTTP(S) endpoint defaults to localhost, warns before a changed non-local server; context choices run from Model default through model's configured max and are sent as Ollama `num_ctx` |
| Context use visibility | Last request's prompt/history token count from Ollama and selected context limit; bounded source-context estimate shown before sending |
| Streaming chat and independent persistent conversations | Done |
| Pinning and conversation search | Title and message text search, matching excerpts, project/archive scope, and debounced input are implemented |
| Rename, archive, and restore conversations | Done; permanently delete is available from the archive view |
| Choose a project folder and include bounded source/config excerpts | Select project-relative chat context files or use safe bounded excerpts; Code task mode can list/read/search supported text files |
| File explorer and preview | Project context menu opens a safe text-file browser with filtering, read-only preview, and Add to chat context |
| Project list, project-specific instructions, and reusable knowledge | Project switcher, pinning, saved instructions, and bounded reusable project knowledge included in local model context |
| Agent reads/searches files on request and edits files | Code task mode supports bounded list/read/search and reviewed create/replace operations on supported text files |
| Diff review, approve/reject, checkpoints, and undo | Whole-file before/after review, automatic checkpoints, per-conversation changed-file list, rollback, and unified text diffs implemented |
| Terminal commands, tests, and build output | Approval-gated PowerShell in project folder with 3-minute timeout, bounded output, and process-tree termination; no sandbox |
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
- [x] Local checkpoints before edits and deletes; per-conversation changed-file list and restore, including undo/redo of file creation or deletion. Whole-file unified diff and side-by-side review are available; richer history browsing remains.
- [x] Agent can request a PowerShell command in the selected project; show the exact command and current-user access warning, require per-call approval, enforce a 3-minute timeout, capture bounded output, show elapsed progress, and stop through the active-turn control. Final verification summary remains.
- Commands are not sandboxed and may access anything available to the Windows account. Every call prompts; do not silently claim an edit, test, or command succeeded.

### 3. Sessions and parallel work

- [x] Hardware-aware serial Ollama request queue; each queued turn keeps its conversation, model, context size, mode, project, and selected context files, with running/queued indicators in the sidebar.
- [x] Sidebar session summaries show each conversation's model, latest prompt/context use, project, changed-file count, and last activity.
- [x] Conversation list serves as a session switcher; the active request can be stopped and queued requests can be canceled per conversation.
- [x] Pause/resume queued requests between model turns; an in-progress local generation completes before the queue pauses.
- More than one independent agent task at once, subject to available RAM and model-load limits; communicate when local hardware serializes inference.
- Optional Git worktree per task so parallel edits cannot collide in the same checkout.
- Per-session model, context use, project, progress, changed files, and last activity visible from the workspace/session list.
- [x] Optional completion toasts for responses that finish while the user is away from that conversation; toast clicks return to the conversation.
- [x] Queued turns are persisted with their model, context, mode, project, and enqueue order; on restart they stay paused behind an explicit **Resume saved queue** action. A turn is removed from the recovery journal only after Codev confirms the removal was saved and just before execution, so an in-progress model/tool action is marked interrupted rather than replayed after a crash. Normal app close waits for active work to cancel and saves the remaining queue for recovery.

### 4. Git and change management

- [x] Git status, local branch creation and switching from the project menu, with staged/working-tree indicators; both branch actions require a clean tree, and switching confirms before updating files.
- [x] Review staged/unstaged diffs per file; send a selected diff excerpt to the composer; commit review shows the exact staged tree and rechecks it before committing. Persistent comments on diff hunks remain pending.
- [x] Stage/unstage selected files and create a local commit after review and confirmation; Codev never pushes automatically.
- Compare and merge a task worktree into the chosen branch, or discard/recover it through a clearly reviewable action.
- Show recent checkpoints and make rollback scope explicit before restoring files.

### 5. Everyday assistant usability

- [x] Markdown headings, emphasis, lists, quotes, inline code, safe clickable HTTP(S) links, fenced code blocks with Copy and lightweight syntax coloring for common languages, scrollable tables, and message-level Copy available while a response is running.
- [x] Read-only Plan mode, stop generation while keeping partial response, visible agent-tool status, continue from an explicitly stopped answer, retry/edit-resend, and branch from an earlier message. Richer plans remain.
- [x] Core shortcuts: Ctrl+N new conversation, Ctrl+F search, F2 rename, Esc stop. Adjustable chat/composer text size is available in Settings. Accessible focus order and broader shortcut customization remain.
- [x] Conversation rename, archive, restore, explicit permanent delete, Markdown export, and additive JSON conversation backup/import. JSON backups omit rollback checkpoint file paths and do not contain project files.
- Project and conversation settings for Ollama endpoint, model parameters/context, and included context sources. Endpoint changes are validated, non-local destinations require confirmation, and HTTP redirects are disabled to prevent silently following requests to another host.

### 6. Extensibility and automation

- Root project `AGENTS.md` instructions and saved project guidance are supported; user-level local skills/instructions remain pending.
- MCP-compatible tools with per-server enablement, visible permissions, and logs; keep local-only use straightforward.
- [x] Reusable local prompt templates with bounded name/prompt lengths; choosing a template inserts it into the composer for review before sending.
- Optional recurring local jobs with an approval/review queue.
- Notification controls are available for conversation completions; a run history for background tasks remains.
- Optional cloud connectors only as opt-in integrations, clearly separated from the local-only default.

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

We use these products as workflow references, not as an exhaustive checklist or a promise to reproduce paid/cloud features. Product capabilities change, so revisit these references when planning each milestone.

## Implementation choices

- C# and modern .NET WPF provide a native Windows desktop app independent of VS Code.
- Ollama's HTTP API provides local model discovery and inference, reusing the user's existing model files and CPU-only runtime configuration.
- Conversations and UI preferences are stored locally under `%LOCALAPPDATA%\Codev`; move from JSON to versioned SQLite when project/task state needs transactions, indexing, or migrations.
