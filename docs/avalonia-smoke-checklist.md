# Avalonia UI smoke checklist

Use this checklist for the Avalonia desktop app while it is the active UI-port target. The Windows WPF app has a separate feature set and must not be used as evidence that an Avalonia interaction works. Use a disposable conversation and local Ollama only. Do not send project data to a non-local endpoint.

## Windows smoke run

- [x] Launch `Codev.Avalonia` and verify the window opens responsively in the persisted theme.
- [ ] Toggle dark/light mode, close and relaunch, and verify the chosen theme persists.
- [x] Confirm the installed-model picker lists local Ollama tags and refreshes when opened, with no blank entries.
- [x] Close and relaunch with a known conversation open; verify Codev reopens that conversation and its installed model is visibly selected. With no Ollama models installed, verify a clear empty-state label appears and the model selector is hidden; while loading or when Ollama is unavailable, verify a status label appears instead of blank options.
- [ ] Change context size, switch conversations, and verify context size is stored per conversation and capped for the selected model.
- [x] Attach a disposable project folder, select a supported source file, and verify the local response uses its bounded read-only context.
- [ ] Verify the composer shows an approximate project-file token estimate, updates when files are selected/cleared, excludes history from its wording, and reports that hosted project files stay local until opted in.
- [ ] Attach an untrusted project; verify first-use choices, automatic context is off, chat/browsing and explicit file selection still work, trust persists after restart, `/status` reports trust, and revoking trust disables automatic context again.
- [ ] Trust a parent folder; verify a child project inherits trust, a sibling outside that parent does not, and the child UI explains inherited scope.
- [ ] Clear selection and verify the bounded default file set is used; confirm `.env`, unsupported files, and paths outside the project are excluded in the UI flow.
- [ ] Browse project files, filter and preview a source file read-only, add it to context, and verify unsupported, sensitive, excluded and outside-project files are not listed.
- [x] In a new disposable conversation, send a short prompt; verify streaming, right-aligned user bubble, left-aligned assistant response without role labels or Copy buttons, and selectable Markdown.
- [ ] Send `/status`; verify the report describes the current provider/model, context, mode, project and queue without invoking the model or disclosing an API key.
- [ ] Open Settings; verify the Ollama endpoint is restored after restart and an older theme-only settings file is preserved through migration. Change to another local endpoint and verify model discovery/chat use it. Change to a non-local endpoint and verify Codev explains where prompts and selected project context will go and requires confirmation; reject once and confirm the setting stays unchanged. Confirm redirects are not followed, and the endpoint cannot be changed while generation or queued work is active.
- [ ] With valid OpenAI and Anthropic API credentials available, connect each provider, verify model discovery and a streamed reply, then verify disabling hosted requests blocks further hosted turns while local Ollama remains available; confirm keys never appear in settings, chat history, backups, or /status, and project files remain excluded until separately opted in.
- [ ] In a trusted disposable project, enable Code task mode with a local Ollama model; verify read/list/search tools work, hosted-model selection is blocked, new-file and replacement proposals require review, rejecting leaves files unchanged, approving a replacement creates a checkpoint, and each shell command requires approval with a visible timeout/output result. Revoke trust during a queued Code task and verify it refuses to resume tools.
- [ ] After an approved file change, open Files history, select the entry, review the complete current and checkpoint versions, and cancel once to verify the file is unchanged. Approve restore and verify the file returns to the checkpoint and a new undo entry appears; change the file during review in a second run and verify the restore refuses to overwrite it. Verify Files is unavailable during active or queued turns and for imported change records without local checkpoints.
- [ ] In a disposable Git repository, open Git status, inspect staged/unstaged/untracked file diffs, select diff lines and ask Codev about them; verify they are appended to the composer without being sent. Stage and unstage a selection, verify branch changes are disabled while dirty and confirmed when clean, review a staged diff, and create a local commit; confirm Codev does not push and detects staged changes made after review.
- [ ] In the Git diff viewer, select lines and add a comment; verify the comment chip appears above the composer, remains unsent while editing the prompt, survives switching conversations and restarting, can be removed, and is included exactly once in the sent user message with its path and selected diff. Verify comments alone can be sent, queued behind a running reply, and included in a backup/import.
- [ ] Toggle Plan mode; verify it is visibly selected, persists after restart, produces a read-only ordered plan, and is captured per queued turn.
- [ ] Type `@` in the composer with a project attached; verify safe project-relative suggestions appear, selecting one adds it to context and replaces the mention, and sensitive/excluded/outside-project files never appear.
- [ ] Send a follow-up while a response is running; verify it queues, the first answer completes, and the follow-up then runs.
- [ ] Start another response, queue a follow-up, close and relaunch the app, verify the turn is paused, then explicitly resume it.
- [ ] Queue a follow-up and cancel it before it starts; verify it is labeled canceled and does not run.
- [ ] Stop a response with Escape and with the stop button; verify partial output is retained.
- [ ] Verify Enter sends, Shift+Enter inserts a newline, Ctrl/Cmd+N creates a conversation, Ctrl/Cmd+F focuses search, and Ctrl/Cmd+L focuses the composer.
- [ ] Scroll upward during streaming and verify auto-follow pauses; return to the bottom and verify it resumes.
- [ ] Pin a conversation, find it through search, archive it, and restore it.
- [ ] Open a conversation's context menu, rename it, and verify the new title survives restart.
- [ ] Archive and restore a conversation from its context menu; verify the active conversation switches to an available chat.
- [ ] Confirm permanent deletion, verify the selected conversation disappears from recents, pins, and search, and verify another chat becomes active.
- [x] Export the active conversation as Markdown and verify its content and metadata.
- [ ] Export all chats to a JSON backup and verify the selected file is written.
- [x] Import a synthetic JSON backup and verify it adds a conversation with a new ID while keeping existing chats; confirm rollback checkpoint paths are not restored.
- [ ] Delete the disposable conversation and verify it no longer appears in recents, pins, or search.

## Not yet supported in Avalonia

Turn-level rewind, richer hunk navigation and staging/revert, worktree merge/recovery, and the remaining WPF feature-parity workflows are not implemented in Avalonia yet. Code task mode, per-conversation checkpoint history with reviewed file restore, project file browsing, Git status and basic local change management (including selected-diff questions and unsent comments), custom Ollama endpoint settings, and read-only Plan mode are implemented but still need the manual interaction checks above. Keep the Windows WPF release checklist separate until its remaining workflows are ported. Conversation rename, archive/restore, and permanent deletion are implemented in Avalonia but still need manual interaction verification.

## Run record

| Date | Commit | OS | Model / context | Result | Notes |
|---|---|---|---|---|---|
| 2026-09-28 | working tree after `b180f46` | Windows 11 | Qwen3.6 35B-A3B | Partial pass | Avalonia restored the active conversation/model and had no blank model entries. New chats inherited Qwen3.6. Attached a disposable project, selected one JS file and verified the local response returned its exact test value; Ollama CPU inference took about two minutes. Remaining checklist items are unverified. |
| 2026-09-28 | working tree after `a34594e` | Windows 11 | Qwen3.6 35B-A3B | Partial pass | Exported a disposable empty conversation as Markdown and verified its title/model metadata. Imported a synthetic backup through the native file picker; it received a new ID and existing conversations remained present. Core backup tests verify checkpoint paths are stripped. Full-history JSON export and remaining checklist items are unverified. |
