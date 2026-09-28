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
| Per-conversation context size | Choices from Model default through model's configured max, sent as Ollama `num_ctx` |
| Context use visibility | Last request's prompt/history token count from Ollama and selected context limit; bounded source-context estimate shown before sending |
| Streaming chat and independent persistent conversations | Done |
| Pinning and title search | Done |
| Rename, archive, and restore conversations | Done; permanently delete is available from the archive view |
| Choose a project folder and include bounded source/config excerpts | Select project-relative chat context files or use safe bounded excerpts; Code task mode can list/read/search supported text files |
| File explorer and preview | Project context menu opens a safe text-file browser with filtering, read-only preview, and Add to chat context |
| Project list, project-specific instructions, and reusable knowledge | Project switcher, pinning, saved instructions, and safe project context implemented; reusable knowledge files pending |
| Agent reads/searches files on request and edits files | Code task mode supports bounded list/read/search and reviewed create/replace operations on supported text files |
| Diff review, approve/reject, checkpoints, and undo | Whole-file before/after review, automatic checkpoints, per-conversation changed-file list, and restore with rollback implemented; unified diff pending |
| Terminal commands, tests, and build output | Approval-gated PowerShell in project folder with 3-minute timeout, bounded output, and process-tree termination; no sandbox |
| Plan/progress view and stop/resume/retry controls | Read-only Plan mode, stop with partial output retained, tool status, retry/edit-resend, and branch-from-message implemented; resume and richer plans remain |
| Conversation request scheduling | Serial Ollama queue with per-conversation running/queued indicators; true parallel agents and isolated worktrees remain pending |
| Git status, branch, staging, and review workflow | Not implemented |
| Markdown/code rendering, attachments, and session export | Basic Markdown and fenced code rendering with clickable web links and one-click code copying; file/image/PDF attachments and export pending |
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
- [x] Project instructions and context exclusions persisted locally and applied to project chat context; reusable knowledge files and user-selected source lists remain.
- [x] Deliberately select project-relative files for chat context; if none are selected, use the existing bounded safe excerpt.
- [x] Project file browser with filtering, safe read-only previews, and direct add-to-context; richer tree navigation and file diffs remain.
- [x] Approximate source-context token count versus the selected model context limit, with tooltip distinguishing it from Ollama's actual prompt count.
- [x] Per-project context exclusions for relative folders/files and filename patterns; these do not limit Code task file access.
- Relevant-file search so large repositories fit smaller local context windows.
- Attach text files, images, and PDFs when the selected local model supports them; clearly show unsupported inputs.

### 2. Coding agent and review loop — in progress

- Separate **Chat**, read-only **Plan**, and **Code task** modes; plan-first workflows for larger tasks.
- [x] Explicit Code task mode with bounded agent tools for listing, reading, searching, and proposing new or updated supported text files.
- [x] Review proposed whole-file replacements side by side; approve or reject before applying.
- [x] Local checkpoints before edits and deletes; per-conversation changed-file list and restore, including undo/redo of file creation or deletion. Whole-file unified diff and side-by-side review are available; richer history browsing remains.
- [x] Agent can request a PowerShell command in the selected project; show the exact command and current-user access warning, require per-call approval, enforce a 3-minute timeout, and capture bounded output. Progress UI, direct stop/retry, and final verification summary remain.
- Commands are not sandboxed and may access anything available to the Windows account. Every call prompts; do not silently claim an edit, test, or command succeeded.

### 3. Sessions and parallel work

- [x] Hardware-aware serial Ollama request queue; each queued turn keeps its conversation, model, context size, mode, project, and selected context files, with running/queued indicators in the sidebar.
- [x] Sidebar session summaries show each conversation's model, latest prompt/context use, project, changed-file count, and last activity.
- Tabs or a session switcher for many conversations, with pause, resume, per-session cancel, and clear busy/queued states.
- More than one independent agent task at once, subject to available RAM and model-load limits; communicate when local hardware serializes inference.
- Optional Git worktree per task so parallel edits cannot collide in the same checkout.
- Per-session model, context use, project, progress, changed files, and last activity visible from the workspace/session list.
- Completion notifications and reliable queued-task resume after app restart remain pending; requests still waiting when Codev closes are marked as not sent.

### 4. Git and change management

- Git status and branch picker for the selected project.
- Review staged/unstaged diffs, inspect changed files, and comment on a selected diff hunk.
- Stage/unstage and commit with user review; no automatic push or publish by default.
- Compare and merge a task worktree into the chosen branch, or discard/recover it through a clearly reviewable action.
- Show recent checkpoints and make rollback scope explicit before restoring files.

### 5. Everyday assistant usability

- [x] Markdown headings, emphasis, lists, quotes, inline code, safe clickable HTTP(S) links, and fenced code blocks with Copy; syntax highlighting and tables remain.
- [x] Read-only Plan mode, stop generation while keeping partial response, visible agent-tool status, retry/edit-resend, and branch from an earlier message. Resume and richer plans remain.
- [x] Core shortcuts: Ctrl+N new conversation, Ctrl+F search, F2 rename, Esc stop. Accessible focus order, adjustable text size, and broader shortcut customization remain.
- [x] Conversation rename, archive, restore, and explicit permanent delete from the archive view; export/import and local backup controls remain.
- Project and conversation settings for Ollama endpoint, model parameters/context, and included context sources.

### 6. Extensibility and automation

- Local skills/instructions for repeatable workflows, with project-level and user-level scope.
- MCP-compatible tools with per-server enablement, visible permissions, and logs; keep local-only use straightforward.
- Reusable task templates and optional recurring local jobs with an approval/review queue.
- Notification controls and a run history for background tasks.
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
