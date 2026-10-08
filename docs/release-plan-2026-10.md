# October staged release plan

Status checked 2026-10-07. This is the proposed sequence for the next public Codev release and its early follow-up. It does not authorize merging PR #1, waiving release checks, or publishing a tag.

## Release 1: early preview

Publish the current Avalonia candidate as a clearly labeled early preview once PR #1 reaches `main` and every Q3 release check in `DESIGN.md` is either completed or has a written, release-specific waiver. Use the repository's existing `v*` tag workflow, which publishes self-contained desktop and CLI archives for Windows x64, Linux x64, macOS Apple silicon, and macOS Intel. The README's `/releases/latest/download/` links already point users to the newest tag, with Windows first.

The current PR head is `c83fe09`. Exact-head Windows/Linux/macOS CI [#37714479483](https://github.com/jay23606/codev/actions/runs/37714479483) and all four packaged desktop/CLI targets [#37714479616](https://github.com/jay23606/codev/actions/runs/37714479616) passed. The Windows UI smoke now confirms that MCP connection diagnostics remain visible in the collapsed activity group without changing the summary of user-requested actions; packaged Windows, Linux Xvfb, macOS Apple silicon, and macOS Intel interaction smokes passed. GitHub Release publishing was skipped because PR #1 is still open. The candidate has no release tag.

The current latest public release is `v2026.10.01-3196395` (two releases are listed). The four README `/releases/latest/download/` desktop links are present in that release and each returned HTTP 200 when checked on 2026-10-07. The next tagged release will move those stable links to the new Avalonia candidate automatically.

The release-page copy is drafted in [`docs/release-notes-draft-2026-10.md`](release-notes-draft-2026-10.md). It corrects the current release description's stale claim that Avalonia lacks Code tasks, Git, and hosted providers, and clearly states Auto-mode command access and the manual validation still open. Review it against the final candidate before publishing.

An earlier self-contained native Windows packaged smoke passed in an isolated profile, and current-source Qwen3.6 Auto verification, file-edit, and parallel-child live tests passed on Windows. The exact-head hosted package checks pass, but a recent local full smoke reached the MCP tests and then failed the native backup-import assertion (four conversations expected, two persisted); another local retry lost UI focus to a separate application before reaching the relevant checks. The backup smoke now waits for the app's import-complete status and reads the store only once, to avoid a test-side read/write race. The backup-filtered core and Avalonia suites passed locally on .NET 10, but those tests use an in-memory picker; the native Save/Open dialog smoke still needs a rerun. Do not count either earlier run as a current full local smoke pass. PR #1 remains open; no release tag has been created.

Before publishing, finish or explicitly waive the remaining Q3 checks. The smoke checklist currently has 65 unchecked cases, including native macOS/Linux desktop interaction, assistive-technology and 200% scaling checks, and manual workflows for project context, tools, permissions and recovery. A clean-machine installation is also open (this Windows host does not have Windows Sandbox). Do not describe hosted runner interaction as physical-keyboard or clean-machine validation. Put any accepted limitations in the release notes. Triage the open cases before the October 8 go/no-go: complete the high-value Windows install/update, data-safety, core chat/Code task, and permission checks; write a row-specific waiver for every remaining preview limitation; defer Release 1 if a safety-critical check cannot be completed or responsibly waived.

**Target schedule:** aim to publish Release 1 on **Friday, October 9, 2026**, as a clearly labeled early preview. Use October 7 to triage the open checklist; freeze the candidate by **Thursday, October 8**, with a go/no-go based on exact-head CI, all four packaged desktop/CLI targets, and Windows installation/update, data-safety, core chat/Code task, and permission checks. Resolve each remaining Q3 row or record an explicit waiver naming the row, reason, and user-visible limitation. Do not ship if a hard gate fails; move Release 1 to the first day all gates pass rather than waiving a blocker to keep the date. Exact-head CI and the four-target packaged preview now pass, while 65 manual checklist cases remain open. October 9 is therefore a target, not a guaranteed ship date.

## Release 2: early stabilization

Target Release 2 for **Tuesday, October 13, 2026**, four days after the October 9 target for Release 1 (acceptable window: Day 3–5). Treat the actual Release 1 publication date as Day 0. On Days 1–2, collect install reports, reproduce actionable issues, and fix release blockers; on Day 3, freeze the follow-up candidate and rerun Windows/Linux/macOS CI, all four packaged desktop/CLI checks, and the Windows packaged smoke. Publish on Day 4 only if exact-head checks pass and update the release notes and README download-link checks. If Release 1 moves, move Release 2 to Day 3–5 after it rather than keeping a stale calendar date. Release 2 is a stabilization update, not a promise to add a large feature set; only verified fixes and small improvements should enter its scope.

If testing finds a data-loss, unauthorized-action, startup-crash, or false-success defect, delay Release 2 until it is fixed and verified. If no release-blocking issue appears, publish the tested follow-up tag on schedule and carry remaining non-blocking Q3 limitations forward with clear notes.

## Release sequence

1. **Release 1 go/no-go:** resolve Q3 rows or record explicit release-specific waivers; review the release notes and known limitations.
2. Merge PR #1 to `main`, then confirm CI and the four-target packaged desktop/CLI checks pass on the exact merge commit.
3. Create the first `v*` tag on that tested commit and verify all eight expected archives are attached to the GitHub Release.
4. Confirm the README latest-download links resolve, then announce the early preview with a clear way to report installation problems.
5. Use the next four days to collect reports, fix blockers, and keep Release 2 scope small. Publish the follow-up on Day 3–5 only after its exact-head checks pass; carry forward non-blocking limitations in its notes.
