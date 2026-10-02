# Avalonia release hardening — 2026-10-02

This report records pre-release Q3 checks for PR #1, branch `avalonia-opencode-parity`. The application source under test is unchanged from `d96bfb2`; later commits added documentation and benchmark evidence. It is intentionally a status report: green CI and the checks below do not satisfy the whole-app/manual requirements in `DESIGN.md` Q3.

## Whole-app smoke run — partial

- Windows CI [#37067913255](https://github.com/jay23606/codev/actions/runs/37067913255) passed at `a317291` for portable tests and Avalonia headless UI on Windows, Linux, and macOS; native credential-vault checks passed for Windows, Linux Secret Service, and macOS Keychain.
- Desktop preview [#37067913186](https://github.com/jay23606/codev/actions/runs/37067913186) passed at `a317291` for self-contained app/CLI package and launch checks on Windows x64, Linux x64, macOS Intel, and Apple silicon.
- Latest documentation-only head `a491b2e` also passed Windows CI [#37069656191](https://github.com/jay23606/codev/actions/runs/37069656191) and desktop preview [#37069656149](https://github.com/jay23606/codev/actions/runs/37069656149), including all four package/launch targets.
- The Windows x64 app archive from that preview was extracted into a fresh temporary directory and launched with a separate `CODEV_DATA_ROOT`. A native window titled `Codev` appeared; its accessibility tree exposed the model picker, composer, and Auto footer. Process working set was 188.8 MB after two seconds. This is one developer-machine startup snapshot, not a measured budget.
- The follow-up screenshot capture for that same window did not match its accessibility tree: the tree described a new, empty conversation, while the screenshot showed unrelated conversation content. No visual or keyboard interaction pass is claimed from this session. The capture mismatch is an automation limitation to resolve before treating native visual QA as reliable.
- A fresh retry after relaunch reproduced the computer-use window-handle failure: `list_windows` returned the packaged Codev window as ID `1907196`, but `get_window` attempted ID `70508` and failed with “window id 70508 was not found.” Refreshing the window list and retrying produced the same mismatch. Native interaction testing stopped after that documented recovery attempt; no app settings or conversation content were changed.
- These are hosted CI runners, not clean user machines without development tools. Native manual interaction on macOS/Linux remains unverified. The Windows smoke checklist has individual native checks, but the complete Q2 card has not been run end to end.

## Performance budgets — incomplete

- A fresh benchmark run against local `qwen3.6:35b-a3b` completed all nine tasks: 150/167 hidden checks (89.8%), 571.5 seconds total task wall time, median reported generation speed 24.2 tokens/s, and a 27.5-second cold model load on the first task. Raw task results: [`bench/results/q3-release-qwen36-20261002.jsonl`](../bench/results/q3-release-qwen36-20261002.jsonl).
- The run used one attempt, temperature 0, thinking off, and a 16,384-token harness context. It found misses in expression parsing (15/19), filter parsing (43/51), and the hard agent task (24/29); the other six tasks passed.
- An immediate repeat on the same machine, model, and settings also scored 150/167 (89.8%) with the same per-task pass/fail counts: 537.6 seconds total, median reported speed 25.8 tokens/s, and 28.5-second cold load. Raw results: [`bench/results/q3-release-qwen36-repeat-20261002.jsonl`](../bench/results/q3-release-qwen36-repeat-20261002.jsonl). Identical outcomes across two runs improve confidence in these particular task results; the runtime and speed differences show that performance still varies, and two runs do not characterize the full variance.
- A second fresh run against installed `qwen3.8:27b` also completed all nine tasks: 138/167 hidden checks (82.6%), 1,095.4 seconds total task wall time, median reported generation speed 8.4 tokens/s, and an 18.8-second cold model load on the first task. Raw task results: [`bench/results/q3-release-qwen38-20261002.jsonl`](../bench/results/q3-release-qwen38-20261002.jsonl). It used one attempt, temperature 0, thinking off, and the same 16,384-token harness context. It missed the filter parser task (27/51) and hard agent task (24/29); the other seven tasks passed.
- The Qwen3.8 result remains a single-run observation, not a controlled Qwen3.6-vs-Qwen3.8 comparison: model sizes differ, and repeated Qwen3.8 variance has not been characterized. Both models missed the same two task families, suggesting useful follow-up targets but not proving a general model limitation.
- The prior checked-in benchmark uses `qwen3-coder:30b`, so model, task-era, and runtime differences prevent a same-model regression conclusion. No old same-model result was available locally.
- Product startup time, first-token latency through Codev, and memory with a long conversation plus a large project are still unmeasured. Do not treat the benchmark wall time as a product performance budget.

## Security review — partial

- The branch-wide added-line secret-pattern scan and the targeted MCP schema/strict-function-schema adversarial review are recorded in `DESIGN.md` Q3. The latter added bounded tool schemas and nullable optional arguments, with 26 focused regressions passing.
- On this head, 101 focused Windows tests passed across command policy/classification, atomic text writes, and conversation backup import/export. In particular, Auto mode approves command proposals unless an exact saved Deny matches; it does not silently switch the project to Ask.
- On code revision `2c07f96`, the opt-in `OllamaAutoPolicyIntegrationTests` passed 2/2 against installed `qwen3.8:27b` (2m26s) and 2/2 against `qwen3.6:35b-a3b` (1m21s). The live tests had each model request `verify_command` and a real MCP tool call; they assert no approval callback is invoked in Auto and that an exact MCP deny prevents a second server call. This is focused live integration evidence, not a full Code task or native UI smoke.
- A follow-up targeted code review covered agent-profile parsing/path filters, MCP environment isolation, redirect handling, HTTP/SSE and stdio framing limits, OAuth callbacks, and child-worktree recovery/merge boundaries. No additional concrete finding was identified. A separate focused Windows suite passed 101/101 profile, MCP transport/OAuth, permission-registry, and child-worktree tests, including linked-profile rejection and hook/smudge-filter suppression on worktree creation/recovery.
- The full manual review of folder trust, untrusted content, command/background execution, provider credential handling, MCP server behavior, and project-supplied inputs remains open. Automated tests and the targeted review do not close that requirement.

## Data safety — partial

- Existing tests cover atomic replacement/temporary-file cleanup and conversation backup export/import, including omission of runtime permissions, queue execution state, and local checkpoint paths.
- This run did not kill the packaged app mid-write, reopen a previous-release data store in the shipped package, test backup import interactively, or verify uninstall data preservation. Those checks remain open.

## Accessibility and documentation — incomplete

- Automated Avalonia UI coverage and Windows/Linux/macOS headless UI CI passed, but no keyboard-only end-to-end pass, screen-reader spot check, large-text/high-contrast visual check, or native macOS/Linux interaction was performed for this report.
- README download links, platform notes, and the feature/status documentation were reviewed and the platform packaging statement was corrected in `d05e527`. Full README, in-app help, and every DESIGN status-row alignment still needs a deliberate review.

## Release decision

Q3 is **not complete** under Q4: multiple rows are incomplete, and none have been waived. PR #1 is ready and its required CI checks are green, but a public release should wait until the remaining checks are run or explicitly waived with written reasons.
