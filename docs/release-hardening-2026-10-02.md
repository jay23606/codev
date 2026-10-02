# Avalonia release hardening — 2026-10-02

This report records the pre-release Q3 check for PR #1, branch `avalonia-opencode-parity`, head `d05e527`. It is intentionally a status report: green CI and the checks below do not satisfy the whole-app/manual requirements in `DESIGN.md` Q3.

## Whole-app smoke run — partial

- Windows CI [#37065063403](https://github.com/jay23606/codev/actions/runs/37065063403) passed portable tests and Avalonia headless UI on Windows, Linux, and macOS; native credential-vault checks passed for Windows, Linux Secret Service, and macOS Keychain.
- Desktop preview [#37065063374](https://github.com/jay23606/codev/actions/runs/37065063374) passed self-contained app/CLI package and launch checks for Windows x64, Linux x64, macOS Intel, and Apple silicon.
- These are hosted CI runners, not clean user machines without development tools. Native manual interaction on macOS/Linux remains unverified. The Windows smoke checklist has individual native checks, but the complete Q2 card has not been run end to end.

## Performance budgets — incomplete

- A fresh benchmark run against local `qwen3.6:35b-a3b` completed all nine tasks: 150/167 hidden checks (89.8%), 571.5 seconds total task wall time, median reported generation speed 24.2 tokens/s, and a 27.5-second cold model load on the first task. Raw task results: [`bench/results/q3-release-qwen36-20261002.jsonl`](../bench/results/q3-release-qwen36-20261002.jsonl).
- The run used one attempt, temperature 0, thinking off, and a 16,384-token harness context. It found misses in expression parsing (15/19), filter parsing (43/51), and the hard agent task (24/29); the other six tasks passed.
- The prior checked-in benchmark uses `qwen3-coder:30b`, so model, task-era, and runtime differences prevent a same-model regression conclusion. No old same-model result was available locally.
- Product startup time, first-token latency through Codev, and memory with a long conversation plus a large project are still unmeasured. Do not treat the benchmark wall time as a product performance budget.

## Security review — partial

- The branch-wide added-line secret-pattern scan and the targeted MCP schema/strict-function-schema adversarial review are recorded in `DESIGN.md` Q3. The latter added bounded tool schemas and nullable optional arguments, with 26 focused regressions passing.
- On this head, 101 focused Windows tests passed across command policy/classification, atomic text writes, and conversation backup import/export. In particular, Auto mode approves command proposals unless an exact saved Deny matches; it does not silently switch the project to Ask.
- The full manual review of folder trust, untrusted content, command/background execution, provider credential handling, MCP server behavior, and project-supplied inputs remains open. Automated tests and the targeted review do not close that requirement.

## Data safety — partial

- Existing tests cover atomic replacement/temporary-file cleanup and conversation backup export/import, including omission of runtime permissions, queue execution state, and local checkpoint paths.
- This run did not kill the packaged app mid-write, reopen a previous-release data store in the shipped package, test backup import interactively, or verify uninstall data preservation. Those checks remain open.

## Accessibility and documentation — incomplete

- Automated Avalonia UI coverage and Windows/Linux/macOS headless UI CI passed, but no keyboard-only end-to-end pass, screen-reader spot check, large-text/high-contrast visual check, or native macOS/Linux interaction was performed for this report.
- README download links, platform notes, and the feature/status documentation were reviewed and the platform packaging statement was corrected in `d05e527`. Full README, in-app help, and every DESIGN status-row alignment still needs a deliberate review.

## Release decision

Q3 is **not complete** under Q4: multiple rows are incomplete, and none have been waived. PR #1 is ready and its required CI checks are green, but a public release should wait until the remaining checks are run or explicitly waived with written reasons.
