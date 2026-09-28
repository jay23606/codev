# Avalonia UI smoke checklist

Use this checklist for the Avalonia desktop app while it is the active UI-port target. The Windows WPF app has a separate feature set and must not be used as evidence that an Avalonia interaction works. Use a disposable conversation and local Ollama only. Do not send project data to a non-local endpoint.

## Windows smoke run

- [x] Launch `Codev.Avalonia` and verify the window opens responsively in the persisted theme.
- [ ] Toggle dark/light mode, close and relaunch, and verify the chosen theme persists.
- [x] Confirm the installed-model picker lists local Ollama tags and refreshes when opened, with no blank entries.
- [x] Close and relaunch with a known conversation open; verify Codev reopens that conversation and its installed model is visibly selected. With no Ollama models installed, verify a clear empty-state label appears and the model selector is hidden; while loading or when Ollama is unavailable, verify a status label appears instead of blank options.
- [ ] Change context size, switch conversations, and verify context size is stored per conversation and capped for the selected model.
- [x] Attach a disposable project folder, select a supported source file, and verify the local response uses its bounded read-only context.
- [ ] Clear selection and verify the bounded default file set is used; confirm `.env`, unsupported files, and paths outside the project are excluded in the UI flow.
- [x] In a new disposable conversation, send a short prompt; verify streaming, right-aligned user bubble, left-aligned assistant response without role labels or Copy buttons, and selectable Markdown.
- [ ] Send a follow-up while a response is running; verify it queues, the first answer completes, and the follow-up then runs.
- [ ] Start another response, queue a follow-up, close and relaunch the app, verify the turn is paused, then explicitly resume it.
- [ ] Queue a follow-up and cancel it before it starts; verify it is labeled canceled and does not run.
- [ ] Stop a response with Escape and with the stop button; verify partial output is retained.
- [ ] Verify Enter sends, Shift+Enter inserts a newline, Ctrl/Cmd+N creates a conversation, Ctrl/Cmd+F focuses search, and Ctrl/Cmd+L focuses the composer.
- [ ] Scroll upward during streaming and verify auto-follow pauses; return to the bottom and verify it resumes.
- [ ] Pin a conversation, find it through search, archive it, and restore it.
- [ ] Delete the disposable conversation and verify it no longer appears in recents, pins, or search.

## Not yet supported in Avalonia

Project file browsing and previews, Plan and Code task modes, reviewed edits, command approval, Git, export, conversation backup/import, endpoint settings, and WPF feature-parity workflows are not covered by this checklist. Keep the Windows WPF release checklist separate until those workflows are ported.

## Run record

| Date | Commit | OS | Model / context | Result | Notes |
|---|---|---|---|---|---|
| 2026-09-28 | working tree after `b180f46` | Windows 11 | Qwen3.6 35B-A3B | Partial pass | Avalonia restored the active conversation/model and had no blank model entries. New chats inherited Qwen3.6. Attached a disposable project, selected one JS file and verified the local response returned its exact test value; Ollama CPU inference took about two minutes. Remaining checklist items are unverified. |
