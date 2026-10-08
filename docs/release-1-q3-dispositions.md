# Release 1 Q3 smoke-test dispositions

Recorded 2026-10-08 against the Release 1 candidate. The numbered rows below refer to the matching `R1-Q3-*` items in [the Avalonia smoke checklist](avalonia-smoke-checklist.md).

**Release 1 decision:** defer each listed full workflow to Release 2. The checklist records partial automated, live-model, or packaged evidence where available, but none of these long multi-step rows has been completed end to end. Partial evidence is not a pass. The 12 rows that had been marked complete despite describing remaining checks are returned to unchecked; their evidence remains in the checklist notes.

**Owner and due date for every deferral:** Codev release maintainers; complete within 3–5 days after Release 1, as set in `DESIGN.md` Q5. Native macOS/Linux and assistive-technology work should be done on those platforms. Hosted-provider rows require valid test credentials and must avoid exposing secrets in evidence.

These deferrals do not waive the Q5 release gates for data loss, unauthorized actions, startup failures, or false success. Existing focused tests and hosted package checks remain required CI gates; any failure in those safety invariants blocks Release 1. Release notes must identify native-platform and accessibility checks that remain unverified.

## Non-waivable release-gate evidence

The code candidate at `2607502` passed local Release validation with .NET SDK 10.0.401: Core 1,029 passed / 2 opt-in live-model tests skipped, Avalonia 64 passed / 12 opt-in live-model tests skipped, and Windows-specific 4/4. The docs-only candidate head `6ac18a1` then passed Windows CI [#37760191541](https://github.com/jay23606/codev/actions/runs/37760191541) and all four package previews [#37760191582](https://github.com/jay23606/codev/actions/runs/37760191582), including packaged UI/CLI smokes for Windows x64, Linux x64, macOS Apple silicon, and macOS Intel. Its tag-only publishing job skipped because PR #1 remains open. The portable and Avalonia suites include regressions for the safety invariants below. These tests are evidence for the guards; they do not count as completing the broader UI smoke rows.

- **Unauthorized command execution:** `ProjectCommandApprovalPolicyTests.Auto_approves_even_protected_compound_commands_without_requesting_approval`, `ProjectCommandApprovalPolicyTests.Exact_deny_blocks_auto_without_requesting_approval`, `CodeTaskToolExecutorTests.Exact_deny_blocks_destructive_command_in_auto_before_execution_or_approval`, `CodeTaskToolExecutorTests.Command_copied_from_project_text_is_flagged_before_approval_and_rejection_runs_nothing`, and `MainWindowTests.Inline_command_review_attributes_an_equivalent_untrusted_command_and_denial_runs_nothing`. The Auto tests verify the selected mode runs without a modal, while exact Deny and explicit Ask denial stop execution.
- **Data loss and stale review:** `GitRepositoryServiceTests.Bulk_revert_discards_all_reviewed_unstaged_files_and_preserves_staged_changes`, `GitRepositoryServiceTests.Bulk_revert_refuses_a_stale_preview_before_changing_any_file`, `GitRepositoryServiceTests.Can_stage_unstage_and_revert_one_hunk_without_touching_other_hunks`, `WorkspaceFileServiceTests.Refuses_to_overwrite_a_file_changed_after_its_review_snapshot`, `ConversationCodeRewindServiceTests.Plan_fails_closed_when_current_file_no_longer_matches_Codev_history`, and `ConversationCodeRewindServiceTests.Apply_rolls_back_earlier_files_if_a_later_file_changed_after_review`.
- **False success:** `OllamaCodeTaskRunnerTests.Corrects_tool_output_mimic_before_publishing_a_real_tool_result`, `OllamaCodeTaskRunnerTests.Suppresses_repeated_tool_output_mimic_when_no_tool_is_executed`, `OpenAiCodeTaskRunnerTests.Rejects_tool_output_mimic_before_publishing_a_real_function_result`, and `CodeTaskToolExecutorTests.Rejected_verification_does_not_execute_or_claim_a_result`.
- **Startup and package launch:** the exact-head preview launches the packaged Avalonia app on Windows, Linux/Xvfb, macOS Apple silicon, and macOS Intel; all four target jobs passed. This hosted smoke does not establish a clean-machine installation or native assistive-technology support.

Keep the full end-to-end data-loss, permission, and hostile-input workflows in their deferred rows. If any listed regression or exact-head platform launch fails, stop the release even if the manual row has a deferral.

| Checklist row | Release 1 disposition | Remaining evidence needed |
| --- | --- | --- |
| R1-Q3-01 | Defer to R2 | Native Linux/macOS keyboard access and persisted compact-header options. |
| R1-Q3-02 | Defer to R2 | Repeat packaged Windows UI automation reliably and capture a complete local Auto transcript; hosted smoke does not resolve local automation flakiness. |
| R1-Q3-03 | Defer to R2 | Native Linux/macOS shortcut, search navigation, Escape, and help-page flow. |
| R1-Q3-04 | Defer to R2 | Full hosted-provider comparison and native cross-platform confirmation; local Qwen and headless warning behavior already have evidence. |
| R1-Q3-05 | Defer to R2 | Full summarize-range lifecycle, export/import boundaries, extension, and rewind interaction. |
| R1-Q3-06 | Defer to R2 | Real model-default comparison, outgoing request values, queued recovery, reset, and native file dialogs. |
| R1-Q3-07 | Defer to R2 | Dark/light visual review, help text, defaults, and saved-preset empty/populated states. |
| R1-Q3-08 | Defer to R2 | Windows visual geometry plus native macOS/Linux menu and persistence interaction; automated geometry and partial Windows persistence already have evidence. |
| R1-Q3-09 | Defer to R2 | Native OS model-picker popup behavior; headless loading, fallback, and nonblank entries already have evidence. |
| R1-Q3-10 | Defer to R2 | Complete Browse-files and Code-task interaction for all listed extensions and binary rejection. |
| R1-Q3-11 | Defer to R2 | Native drag/drop acceptance and rejection across Windows, macOS, and Linux. |
| R1-Q3-12 | Defer to R2 | End-to-end guidance/rule selection, exclusions, untrusted project behavior, and selector-failure recovery. |
| R1-Q3-13 | Defer to R2 | Full token estimate and sent-request snapshot comparison for local and hosted multi-round Code tasks. |
| R1-Q3-14 | Defer to R2 | Real local retrieval/context checks, untrusted-file exclusion, hosted opt-in, and restart/queue persistence. |
| R1-Q3-15 | Defer to R2 | Full trust/revoke workflow, integrations, `/status`, and symlink/junction fail-closed interaction. |
| R1-Q3-16 | Defer to R2 | Parent/child/sibling trust behavior and inherited-scope explanation in the UI. |
| R1-Q3-17 | Defer to R2 | UI verification of bounded default selection and sensitive/outside-project exclusions. |
| R1-Q3-18 | Defer to R2 | File-browser filtering, preview, add-to-context, and exclusion interaction. |
| R1-Q3-19 | Defer to R2 | Visual review of streaming alignment, selectable Markdown, and message controls. |
| R1-Q3-20 | Defer to R2 | Full slash menu keyboard/mouse matrix and command-specific send/confirmation behavior; packaged `/status` and debounce regression have partial evidence. |
| R1-Q3-21 | Defer to R2 | User/project custom-command creation, precedence, malformed-input, quoting, and insertion workflow. |
| R1-Q3-22 | Defer to R2 | Legacy template migration plus add/edit/remove/preview/persist interaction. |
| R1-Q3-23 | Defer to R2 | Cross-format skill discovery, trust and symlink checks, tool-schema enforcement, and argument expansion. |
| R1-Q3-24 | Defer to R2 | Endpoint migration, non-local confirmation, redirect rejection, and active-work guard. |
| R1-Q3-25 | Defer to R2 | OpenAI and Anthropic credential, consent, billing, failure, streaming, and secret-redaction workflows; requires test credentials. |
| R1-Q3-26 | Defer to R2 | OpenAI reasoning/verbosity choices, outgoing payload, queue recovery, and backup round trip; requires eligible model access. |
| R1-Q3-27 | Defer to R2 | `/status` output verification with overrides and secret exclusion. |
| R1-Q3-28 | Defer to R2 | Provider/account-specific GPT model option matrix and invalid saved-value fallback; requires eligible account models. |
| R1-Q3-29 | Defer to R2 | Full local Code-task read/list/search flow and hosted-model restrictions in the UI. |
| R1-Q3-30 | Defer to R2 | OpenAI interrupted-stream behavior, MCP injection, and real edit comparison; local Qwen provenance evidence is partial. |
| R1-Q3-31 | Defer to R2 | Packaged child lifecycle, merge/recovery review, and native platform interaction; live isolated worktree and cancellation tests already have evidence. |
| R1-Q3-32 | Defer to R2 | OpenAI no-folder consent/enable flow and project-context opt-in boundaries; requires hosted credentials. |
| R1-Q3-33 | Defer to R2 | Multi-round token totals and interrupted-response accounting using a hosted OpenAI Code task. |
| R1-Q3-34 | Defer to R2 | Full Ask/Allow exact/Deny persistence and default UI command workflow. |
| R1-Q3-35 | Defer to R2 | Complete destructive Git preview/cancel/stale-diff/limits flow; retain automated assertions for staged-content preservation and stale-state refusal as release gates. |
| R1-Q3-36 | Defer to R2 | Full Auto matrix for ordinary/risk-flagged file operations, commands, chains, and deny rules; exact compound-command package and executor regressions already have evidence. |
| R1-Q3-37 | Defer to R2 | Web-search activity and older imported-transcript compatibility; collapsed multi-action rendering already has packaged and UI-test evidence. |
| R1-Q3-38 | Defer to R2 | Source attribution and inline warning when a hostile instruction does cause a proposal, plus list/search/command-output cases; live Qwen no-action case is partial. Keep unauthorized-action checks blocking. |
| R1-Q3-39 | Defer to R2 | Checklist editing, state persistence, prompt inclusion, and `/clear` behavior in a real Code task. |
| R1-Q3-40 | Defer to R2 | Full file-restore review/cancel/approve/stale-check interaction; checkpoint conflict safety must remain covered by automated tests. |
| R1-Q3-41 | Defer to R2 | End-to-end staged/unstaged hunk and file operations, cancellation, stale diffs, and nested repository controls; data-preservation tests remain release gates. |
| R1-Q3-42 | Defer to R2 | Diff-comment compose, queue, persistence, removal, send-once, and backup flow. |
| R1-Q3-43 | Defer to R2 | Plan mode selection, restart persistence, read-only output, and per-queued-turn capture. |
| R1-Q3-44 | Defer to R2 | Native shortcut cycle across provider/project eligibility and during-generation behavior. |
| R1-Q3-45 | Defer to R2 | `@` suggestion, replacement, context selection, and sensitive-path rejection in the composer. |
| R1-Q3-46 | Defer to R2 | Live-stream queue scheduling and visible composer state; recovered FIFO behavior has real-model evidence. |
| R1-Q3-47 | Defer to R2 | Full rewind cancel/confirm interaction and preserved file history/project selection; retain message-deletion safety tests as release gates. |
| R1-Q3-48 | Defer to R2 | Packaged close/relaunch and visible resume controls; recovered FIFO semantics have real-model evidence. |
| R1-Q3-49 | Defer to R2 | Cancel-before-start queue interaction and proof that canceled work never runs. |
| R1-Q3-50 | Defer to R2 | Escape and stop-button interaction with partial-response retention. |
| R1-Q3-51 | Defer to R2 | Scroll-follow pause/resume interaction during a live stream. |
| R1-Q3-52 | Defer to R2 | User-facing Markdown export content and metadata inspection. |
| R1-Q3-53 | Defer to R2 | Full HTML export safety/redaction/cancel flow; credential masking and inert HTML protections remain security gates. |
| R1-Q3-54 | Defer to R2 | Native keyboard-only traversal, focus order, popup and dialog dismissal. |
| R1-Q3-55 | Defer to R2 | Narrator, VoiceOver, and Orca names, states, and announcements on their native platforms. |
| R1-Q3-56 | Defer to R2 | 200% system scaling and largest-font layout review across all named surfaces. |
| R1-Q3-57 | Defer to R2 | Real model completing all requested action types and web-search/imported-transcript checks; grouped activity display has packaged and UI-test evidence. |
| R1-Q3-58 | Defer to R2 | Full `/review-last-turn` scope, stale-history, and unchanged-project-state workflow. |
| R1-Q3-59 | Defer to R2 | Packaged SSE fallback, HTTP OAuth UI, and native macOS/Linux checks; stdio and Streamable HTTP packaged calls have evidence. |
| R1-Q3-60 | Defer to R2 | Packaged UI readability of tool, prompt, resource, and template activity details; protocol behavior has Core-test evidence. |
| R1-Q3-61 | Defer to R2 | Packaged prompt/resource/template denial and native macOS/Linux UI checks; executor, headless, and packaged stdio-tool safety cases have evidence. |
| R1-Q3-62 | Defer to R2 | Packaged interaction and native screen-reader checks for disabled/invalid/unavailable MCP servers; Core and headless diagnostics have evidence. |
| R1-Q3-63 | Defer to R2 | Complete browser OAuth and restart/forget-token workflow with test server; credential absence from persisted and diagnostic surfaces. |
| R1-Q3-64 | Defer to R2 | Slow/canceled/oversized MCP operation and shutdown bounds while proving the app remains responsive. |
| R1-Q3-65 | Defer to R2 | Headless tests cover malformed JSON, a wrongly typed field, and duplicate IDs, preserving the source through invalid states and confirming reload after repair. Exact-head four-target preview [#37818884753](https://github.com/jay23606/codev/actions/runs/37818884753) now verifies the packaged Windows editor preserves and repairs malformed JSON, wrongly typed entries, and duplicate IDs; a rejected duplicate save leaves the source unchanged. Still needed: native settings interaction and oversized/unreadable-file handling. |
