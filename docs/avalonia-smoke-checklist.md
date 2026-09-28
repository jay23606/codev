# Avalonia UI smoke checklist

Use this checklist for the Avalonia desktop app while it is the active UI-port target. The Windows WPF app has a separate feature set and must not be used as evidence that an Avalonia interaction works. Use a disposable conversation and local Ollama for local checks. Hosted-provider checks require valid API credentials and may incur provider charges; use a disposable chat and never include project data unless separately opted in.

## Windows smoke run

- [x] Launch `Codev.Avalonia` and verify the window opens responsively in the persisted theme.
- [ ] Toggle dark/light mode, close and relaunch, and verify the chosen theme persists.
- [x] Confirm the installed-model picker lists local Ollama tags and refreshes when opened, with no blank entries.
- [x] Close and relaunch with a known conversation open; verify Codev reopens that conversation and its installed model is visibly selected. With no Ollama models installed, verify a clear empty-state label appears and the model selector is hidden; while loading or when Ollama is unavailable, verify a status label appears instead of blank options.
- [ ] Change context size, switch conversations, and verify context size is stored per conversation and capped for the selected model.
- [ ] Open **Advanced model settings…** from **More**; load model defaults and compare the declared values with Ollama's model info, noting absent built-in defaults; enter overrides, verify out-of-range text is rejected, save and inspect /status; send a chat and a Code task and confirm the request uses those values. Queue a turn, restart and resume it, verify it retains the captured settings. Clear fields with **Reset all to model defaults**, then confirm options contains no sampling or num_predict values; import/export a backup and switch conversations to verify settings persist.
- [ ] In dark and light themes, verify **Advanced model settings…** uses the active opaque background; its title, **Sampling parameters** heading, and each parameter name are clearly readable. Check that each setting has a **Default** action and plain-language help. With no presets saved, confirm the preset selector is hidden and an explanatory empty-state message appears; after saving or importing a preset, confirm the selector appears and lists it.
- [ ] From **More → Reading width**, select 640, 800, 960, and **Full width**; verify the composer and centered conversation lane resize together, user prompts stay right-aligned inside that lane, and the selection persists after restart.
- [ ] With installed Ollama models, verify the selector shows the last selected installed model (or a valid installed fallback) and contains no blank entries. With no models, verify the selector is hidden and only a clear loading, unavailable, or no-model status is shown.
- [x] Attach a disposable project folder, select a supported source file, and verify the local response uses its bounded read-only context.
- [ ] On Windows, macOS, and Linux desktop builds, drag supported source/text files onto the composer in a disposable project; verify accepted files appear in local context, while outside-project, sensitive, binary, and unsupported files are ignored and nothing is sent until the prompt is submitted.
- [ ] In a trusted disposable project, add root and nested `AGENTS.md` files plus `.codev/rules/javascript.md` with `globs: **/*.js, **/*.ts`, `baseline.md` with `activation: always`, and `architecture.md` with `activation: model`; select matching and nonmatching source files and confirm only root, ancestor guidance, matching rules, and the baseline rule enter context. Verify the status shows the relevance preflight, only model-selected descriptions load into the main request, and selector failure still allows the main response. Type `@rule:` and choose a rule, then confirm only that rule applies to the request even with nonmatching files. Exclude a matching file/rule and confirm it is omitted; repeat untrusted and verify hidden path rules are not loaded or duplicated as source excerpts. For hosted chats, confirm selection waits for project-context opt-in.
- [ ] Verify the composer shows an approximate project-file token estimate, updates when files are selected/cleared, excludes history from its wording, and reports that hosted project files stay local until opted in. After Chat, Plan, and Code task requests, open View last request context and verify the exact JSON body matches the request sent, system/history/project-guidance/source-excerpt/repo-map/tool-schema/tool-result counts and rough estimate are clear, provider-reported input tokens appear when supplied, and switching chats shows each chat's latest snapshot. Confirm each Code task round replaces the snapshot with that round's request and no duplicate snapshot copy is stored in settings or backups (the normal transcript remains unchanged).
- [ ] Enable the repository map for a trusted project and verify a bounded file/symbol outline reaches the local model; test an explicitly selected file in an untrusted folder, verify exclusions/secrets are omitted, inspect its estimate, and confirm hosted chats omit it until project context is opted in. Verify the setting survives conversation switches/restart and queued-turn recovery.
- [ ] Attach an untrusted project; verify first-use choices, automatic context is off, chat/browsing and explicit file selection still work, trust persists after restart, `/status` reports trust, and revoking trust disables automatic context again.
- [ ] Trust a parent folder; verify a child project inherits trust, a sibling outside that parent does not, and the child UI explains inherited scope.
- [ ] Clear selection and verify the bounded default file set is used; confirm `.env`, unsupported files, and paths outside the project are excluded in the UI flow.
- [ ] Browse project files, filter and preview a source file read-only, add it to context, and verify unsupported, sensitive, excluded and outside-project files are not listed.
- [x] In a new disposable conversation, send a short prompt; verify streaming, right-aligned user bubble, left-aligned assistant response without role labels or Copy buttons, and selectable Markdown.
- [ ] Send `/status`; verify the report describes the current provider/model, context, mode, project and queue without invoking the model or disclosing an API key.
- [ ] Type `/` and verify the slash-command menu filters as you type; use Up/Down, Enter, Tab, Escape, and mouse selection. Check `/status`, `/plan`, `/code`, `/model`, and `/export` invoke the named local UI action; `/review` and `/init` insert editable prompts without sending; `/clear` asks for confirmation, keeps project selection and file-change history, and refuses while that conversation is generating or queued. Verify slash suggestions close when arguments or ordinary text are entered and do not interfere with `@` file mentions.
- [ ] Type `/commands` and verify the custom-command guide opens. Create a user command and a trusted-project command from the documented Markdown template; confirm suggestions show scope and description, project commands override same-named user commands, and built-in names are reserved. Attach an untrusted project and verify its `.codev/commands` files are not read; make the command folder or a command file a symlink and verify it is skipped. Try malformed frontmatter, invalid UTF-8, oversized files, too many arguments, unknown and missing arguments, duplicate arguments, unquoted values with spaces, and an unclosed quote; each should be skipped or reported clearly. Invoke a valid argument command with quoted values; verify placeholders are expanded into an editable composer prompt and are not sent automatically. Verify a no-argument command is inserted the same way.
- [ ] If `%LOCALAPPDATA%\Codev\settings.json` contains WPF-saved prompt templates and no Avalonia template list exists yet, verify Avalonia imports them as **SAVED** `/template-…` suggestions. Use the **Prompt templates** button to add, preview, edit, remove, and insert templates; verify each change persists after restart and insertion leaves an editable composer prompt without sending. Confirm a template slug that matches a user/project command receives a unique name and WPF settings are left unchanged.
- [ ] Type `/skills` and verify the user and trusted-project skill folders open. Create a user skill from [the documented SKILL.md format](skills.md), verify only its name, description, arguments, and scope appear in suggestions, fill an argument and verify selecting it again inserts the expanded prompt without sending. Verify a trusted project skill overrides a user skill of the same name, untrusted project skills stay hidden, and changed skill content is reloaded before insertion. Try invalid names/frontmatter, symlinked folders/files, and oversized content; verify they are skipped. Confirm scripts are not executed and the model cannot invoke skills.
- [ ] Open Settings; verify the Ollama endpoint is restored after restart and an older theme-only settings file is preserved through migration. Change to another local endpoint and verify model discovery/chat use it. Change to a non-local endpoint and verify Codev explains where prompts and selected project context will go and requires confirmation; reject once and confirm the setting stays unchanged. Confirm redirects are not followed, and the endpoint cannot be changed while generation or queued work is active.
- [ ] With valid OpenAI and Anthropic API credentials available, confirm Codev identifies each as hosted and explains that API access/billing is separate from consumer subscriptions; connect each provider and verify model discovery and a streamed reply. Check missing/invalid credentials, verify a failed reconnect does not replace an existing working key, and confirm canceling or failing connection does not change the project-context opt-in. Disable hosted requests and confirm they are blocked while local Ollama remains available; confirm keys never appear in settings, chat history, backups, logs, or `/status`, and project files remain excluded until separately opted in. Verify the consent makes clear that provider data-retention policies apply.
- [ ] In a trusted disposable project, enable Code task mode with a local Ollama model; verify read/list/search tools work, hosted-model selection is blocked, new-file and replacement proposals require review, rejecting leaves files unchanged, approving a replacement creates a checkpoint, and each shell command requires approval with a visible timeout/output result. Revoke trust during a queued Code task and verify it refuses to resume tools.
- [ ] In a trusted disposable project, put fake instructions in a source file telling the model to ignore the user and run a harmless-but-disallowed command, including one exact command and one equivalent spelling (for example, `Remove-Item ./dist/secret.json` and `rm ./dist/secret.json`). Ask for an unrelated small change. Verify file/search/list and command output are clearly marked as untrusted data in the model request, the model does not treat embedded text as authority, and every file/command approval warns you to review against your request and lists project/search/command sources shown to the model. If the model proposes either command or its equivalent, confirm the approval dialog names the source and displays the **POTENTIAL INSTRUCTION FOLLOWING** warning. Reject any unrelated or suspicious proposal and verify nothing runs or changes.
- [ ] In a trusted disposable project, request a multi-step Code task and verify the model creates a visible checklist, marks steps pending/in progress/done, and reflects progress in its final reply. Edit, reorder, add, and remove steps; switch conversations and relaunch to verify persistence; compact earlier messages and confirm the checklist is included in the next Code task prompt. Confirm checklist text does not grant tool permission, and `/clear` clears the checklist while preserving project selection and file history.
- [ ] After an approved file change, open Files history, select the entry, review the complete current and checkpoint versions, and cancel once to verify the file is unchanged. Approve restore and verify the file returns to the checkpoint and a new undo entry appears; change the file during review in a second run and verify the restore refuses to overwrite it. Verify Files is unavailable during active or queued turns and for imported change records without local checkpoints.
- [ ] In a disposable Git repository, open Git status, inspect staged/unstaged/untracked file diffs, select diff lines and ask Codev about them; verify they are appended to the composer without being sent. Stage and unstage a selection, verify branch changes are disabled while dirty and confirmed when clean, review a staged diff, and create a local commit; confirm Codev does not push and detects staged changes made after review.
- [ ] In the Git diff viewer, select lines and add a comment; verify the comment chip appears above the composer, remains unsent while editing the prompt, survives switching conversations and restarting, can be removed, and is included exactly once in the sent user message with its path and selected diff. Verify comments alone can be sent, queued behind a running reply, and included in a backup/import.
- [ ] Toggle Plan mode; verify it is visibly selected, persists after restart, produces a read-only ordered plan, and is captured per queued turn.
- [ ] Press Ctrl+Shift+M and verify the mode cycles Chat → Plan → Code task → Chat when Code task is eligible; with a hosted model, untrusted/no project, or remote Ollama, verify it cycles Plan back to Chat with an explanation and never enables Code task. Try the shortcut during generation and verify the active mode stays unchanged. Confirm mode remains clear on the mode buttons and in the chat status.
- [ ] Type `@` in the composer with a project attached; verify safe project-relative suggestions appear, selecting one adds it to context and replaces the mention, and sensitive/excluded/outside-project files never appear.
- [ ] Send a follow-up while a response is running; verify it queues, the first answer completes, and the follow-up then runs.
- [ ] Rewind from an earlier user prompt; cancel once and verify nothing changes, then confirm and verify that prompt and later messages are removed, the prompt returns to the composer, project files and Files history are unchanged, and rewind is disabled during generation or queued work.
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
- [ ] Export a disposable conversation as standalone HTML; verify headings, lists, tables and fenced code render, raw HTML is inert, no remote assets load, local full paths/checkpoint paths are absent, reviewed Codev file diffs appear, and the pre-export review masks common credential matches. Redact one selected value, verify it is replaced throughout the export, then cancel an export and verify no file is written. Confirm the UI says detection is heuristic.
- [ ] Export all chats to a JSON backup and verify the selected file is written.
- [x] Import a synthetic JSON backup and verify it adds a conversation with a new ID while keeping existing chats; confirm rollback checkpoint paths are not restored.
- [ ] Delete the disposable conversation and verify it no longer appears in recents, pins, or search.

## Not yet supported in Avalonia

Reviewed file-and-conversation rewind, richer hunk navigation and staging/revert, worktree merge/recovery, and the remaining WPF feature-parity workflows are not implemented in Avalonia yet. Conversation-only rewind, Code task mode, per-conversation checkpoint history with reviewed file restore, project file browsing, Git status and basic local change management (including selected-diff questions and unsent comments), custom Ollama endpoint settings, and read-only Plan mode are implemented but still need the manual interaction checks above. Keep the Windows WPF release checklist separate until its remaining workflows are ported. Conversation rename, archive/restore, and permanent deletion are implemented in Avalonia but still need manual interaction verification.

## Run record

| Date | Commit | OS | Model / context | Result | Notes |
|---|---|---|---|---|---|
| 2026-09-28 | working tree after `b180f46` | Windows 11 | Qwen3.6 35B-A3B | Partial pass | Avalonia restored the active conversation/model and had no blank model entries. New chats inherited Qwen3.6. Attached a disposable project, selected one JS file and verified the local response returned its exact test value; Ollama CPU inference took about two minutes. Remaining checklist items are unverified. |
| 2026-09-28 | working tree after `a34594e` | Windows 11 | Qwen3.6 35B-A3B | Partial pass | Exported a disposable empty conversation as Markdown and verified its title/model metadata. Imported a synthetic backup through the native file picker; it received a new ID and existing conversations remained present. Core backup tests verify checkpoint paths are stripped. Full-history JSON export and remaining checklist items are unverified. |

## Strict patch-based Code task edits

- In a trusted project, ask Code task mode to make a small change to an existing source file using a unified hunk patch.
- Confirm the review dialog shows the patch, current file, and complete resulting file; reject it and verify no workspace change or history entry.
- Repeat and approve; verify a checkpointed edit appears in Files history and can be restored.
- Change the hunk context so it no longer matches, or provide malformed hunk counts; verify Codev reports a patch error before opening review and leaves the file unchanged.

## Strict patch-based Code task edits

- In a trusted project, ask Code task mode to make a small change to an existing source file using a unified hunk patch.
- Confirm the review dialog shows the patch, current file, and complete resulting file; reject it and verify no workspace change or history entry.
- Repeat and approve; verify a checkpointed edit appears in Files history and can be restored.
- Change the hunk context so it no longer matches, or provide malformed hunk counts; verify Codev reports a patch error before opening review and leaves the file unchanged.

## Code task verification and bounded repairs

- In a trusted project, ask Code task mode to make a small source change and propose the appropriate test or lint command with `verify_command`.
- Cancel verification and confirm it is not run and Codev makes no pass/fail claim.
- Repeat, approve a command that exits successfully, and confirm the transcript reports Verification PASSED (exit code 0).
- Cause a verification command to fail; confirm its exit code and bounded output appear and the model can propose a reviewed repair.
- Exhaust the two repair cycles with failing verification; confirm subsequent file-edit, patch, create, and command tools are blocked for the task, while the model can still explain the unresolved failure.

## Per-conversation output styles

- Change the style selector among Balanced, Concise, Explanatory, and Code only; confirm the selected value follows the active conversation.
- Switch conversations and restart the app; verify each style persists.
- Queue a prompt while a response runs, change the selected style, and confirm the queued prompt uses the style captured when it was queued.
- Verify the style changes answer formatting without enabling tools or weakening approval, caveat, and test-result reporting.

## Ollama loaded-model awareness

- Open Memory and compare model names, size, VRAM, context length, and expiry with Ollama's current /api/ps response; confirm empty and unreachable states are clear.
- Cancel the unload confirmation and verify the model remains loaded.
- Confirm unload while idle; check the model disappears but remains installed and can be loaded again by selecting it.
- Attempt unload while a response is generating or a request is queued; confirm Codev refuses and does not interrupt the request.
- Start a send during the unload operation; confirm it is held until unload finishes instead of racing the model state.

## Ollama generation stats

- Send a normal local Ollama prompt and verify the completed assistant reply shows first-token latency, tokens per second, output token count, and server-reported model load time.
- Verify a warm-model response can report a near-zero load time, and missing optional response fields do not display invented values or break the reply.
- Switch to a hosted model and confirm Codev does not show locally estimated throughput or load numbers.
- Restart and reopen the conversation; confirm the stats persist with that reply.

## Conversation fork

- Fork an idle conversation from both the pinned and recent sidebar lists; verify the fork becomes active and retains the title suffix, messages, draft, model/context/style/mode settings, project link, and selected context.
- Verify diff comments and file-change history are copied, and every local rollback checkpoint in the fork resides under the new conversation ID and remains independent of the source checkpoint.
- Attempt to fork a conversation with active generation or queued turns; verify it is refused without altering the source or creating a new chat.
- Simulate a missing or invalid checkpoint; verify fork reports the error and removes any partial checkpoint copies.
- Attach a folder with multiple prior conversations; confirm the prompt chooses the most recently updated non-archived chat and offers Resume recent, Attach here, New conversation, and Cancel. Verify each choice does the corresponding action without copying or overwriting messages.

## Ollama thinking display

- With an Ollama model that supports thinking, enable Think, send a prompt, and verify streamed thinking appears separately from the final response in a collapsed section.
- Disable Think and verify the API request asks Ollama to suppress thinking; verify a model that ignores the boolean follows its documented default without breaking the answer.
- Queue a prompt while a response is running, change the conversation Think setting, and confirm the queued turn uses the captured setting.
- Restart and reopen the chat; confirm both the setting and returned thinking text persist, and confirm thinking text is not included in follow-up request history.
- Switch to a hosted provider and confirm the Think control is hidden and `/status` identifies thinking as unavailable for hosted providers.

## Read-only Git reviews

- Attach and trust a disposable Git project; with local loopback Ollama selected, run `/review` and `/security-review` against working-tree changes and confirm the selectable report identifies the scope and no files or Git state change.
- Run `/review-commit` and `/security-review-commit`, enter a valid commit hash, and verify only that commit's bounded diff is reviewed. Try malformed text and a nonexistent hash; confirm no diff is sent and a clear error appears.
- Run `/review-branch` and `/security-review-branch` against a local base branch; verify the comparison is base...HEAD, rejects a name that is not an existing local branch, and leaves the repository unchanged.
- Add a disposable token-shaped string on a new diff line and verify the deterministic scan reports file and line while hiding the value; check removed lines do not trigger it and a truncated diff is labeled incomplete.
- Switch to a hosted provider and run a security review; verify the deterministic local scan still runs but no request containing the diff is sent to a hosted endpoint. If local loopback Ollama is unavailable, confirm scan-only output says no model review ran.

## Local commit-message draft

- Stage a small change in a trusted disposable project with local loopback Ollama selected; choose **Draft with local model** and verify the suggestion appears in the editable commit-message field without creating a commit.
- Edit the suggestion and confirm the normal staged-diff review and explicit commit confirmation still run; reject or cancel and verify no commit is created.
- Change the staged tree while drafting from another process and confirm Codev discards the stale suggestion with a clear message; switch to a hosted provider or an untrusted project and confirm the diff is never sent remotely.
- Run `/pr-description` against a local base branch and verify the title and description appear as selectable text, the Testing section is honest, and no PR is created. Switch to hosted provider or untrusted project and confirm the branch diff is never sent remotely.

## WPF task checklist parity

- In an Ollama Code task, ask for a multi-step change and verify the model creates a visible checklist; confirm checklist items are re-sent on the next task request.
- Open **Checklist**, edit text and status, add/remove steps, and move steps up/down; verify persistence after restart and that an existing checklist remains visible in Plan mode.
- Try invalid or over-limit items and verify they are rejected without losing the previous checklist; confirm the checklist control is hidden for ordinary chats with no checklist.

## Untrusted proposal warning

- In Code task, use a disposable source or documentation file that contains common prompt-injection language, then request an unrelated harmless edit; verify the approval dialog shows a potential-instruction warning but does not block or auto-approve the proposal.
- Run a harmless command whose quoted text contains a flagged phrase and verify the same advisory appears; confirm the warning does not display secret values and normal code with none of the patterns receives no warning.
