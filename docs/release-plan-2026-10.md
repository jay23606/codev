# October staged release plan

Status checked 2026-10-07. This is the proposed sequence for the next public Codev release and its early follow-up. It does not authorize merging PR #1, waiving release checks, or publishing a tag.

## Release 1: early preview

Publish the current Avalonia candidate as a clearly labeled early preview once PR #1 reaches `main` and every Q3 release check in `DESIGN.md` is either completed or has a written, release-specific waiver. Use the repository's existing `v*` tag workflow, which publishes self-contained desktop and CLI archives for Windows x64, Linux x64, macOS Apple silicon, and macOS Intel. The README's `/releases/latest/download/` links already point users to the newest tag, with Windows first.

The current app-source candidate is PR #1 commit `1cea345`; the latest PR head is documentation-only commit `0ba8ceb`. Windows/Linux/macOS CI [#37700542375](https://github.com/jay23606/codev/actions/runs/37700542375) and the four-target desktop/CLI preview [#37700542423](https://github.com/jay23606/codev/actions/runs/37700542423) passed on the latest head. The preview covers Windows x64, Linux x64, macOS Apple silicon, and macOS Intel; the hosted Windows UI smoke also verified font and reading-width persistence across restart and Auto exact-Deny behavior. Release publishing was skipped because PR #1 remains open. Earlier in the same candidate cycle, the Windows packaged smoke had one non-reproducing Full width persistence assertion failure before a successful retry. Keep monitoring that flaky assertion. No release tag has been created.

The current latest public release is `v2026.10.01-3196395` (two releases are listed). The four README `/releases/latest/download/` desktop links are present in that release and each returned HTTP 200 when checked on 2026-10-07. The next tagged release will move those stable links to the new Avalonia candidate automatically.

The release-page copy is drafted in [`docs/release-notes-draft-2026-10.md`](release-notes-draft-2026-10.md). It corrects the current release description's stale claim that Avalonia lacks Code tasks, Git, and hosted providers, and clearly states Auto-mode command access and the manual validation still open. Review it against the final candidate before publishing.

The full self-contained native Windows packaged smoke passed locally in an isolated profile, and current-source Qwen3.6 Auto verification, file-edit, and parallel-child live tests passed on Windows. The exact candidate's cross-platform CI and four-target package checks now pass. PR #1 remains open; no release tag has been created.

Before publishing, finish or explicitly waive the remaining Q3 checks. The smoke checklist currently has 65 unchecked cases, including native macOS/Linux desktop interaction, assistive-technology and 200% scaling checks, and manual workflows for project context, tools, permissions and recovery. A clean-machine installation is also open (this Windows host does not have Windows Sandbox). Do not describe hosted runner interaction as physical-keyboard or clean-machine validation. Put any accepted limitations in the release notes.

**Target schedule:** aim to publish Release 1 on **Friday, October 9, 2026**, as a clearly labeled early preview. Freeze the candidate and make the go/no-go decision by **Thursday, October 8**. Before publishing, merge PR #1, confirm the merged commit's Windows/Linux/macOS CI and all four packaged desktop/CLI targets pass, and resolve every remaining Q3 checklist item or record an explicit waiver naming the row, reason, and release limitation. If a gate is not met, move Release 1 to the first day it is met; don't waive it just to keep the date. The current PR head `0ba8ceb` has green exact-head CI and four-target package checks, but the checklist and merge gates remain open.

## Release 2: early stabilization

Target Release 2 for **Tuesday, October 13, 2026**, four days after the October 9 target for Release 1 (acceptable window: Day 3–5). Treat the actual Release 1 publication date as Day 0. During the interval, gather install reports and actionable user feedback, fix any release blockers, and limit follow-up scope to small usability or reliability changes. Re-run Windows/Linux/macOS CI, all four packaged desktop/CLI checks, and the Windows packaged smoke on the exact candidate commit. Update the release notes and verify the README latest-download links resolve to all expected archives. If Release 1 moves, move Release 2 to Day 3–5 after it rather than keeping a stale calendar date.

If testing finds a data-loss, unauthorized-action, startup-crash, or false-success defect, delay Release 2 until it is fixed and verified. If no release-blocking issue appears, publish the tested follow-up tag on schedule and carry remaining non-blocking Q3 limitations forward with clear notes.

## Release sequence

1. **Release 1 go/no-go:** resolve Q3 rows or record explicit release-specific waivers; review the release notes and known limitations.
2. Merge PR #1 to `main`, then confirm CI and the four-target packaged desktop/CLI checks pass on the exact merge commit.
3. Create the first `v*` tag on that tested commit and verify all eight expected archives are attached to the GitHub Release.
4. Confirm the README latest-download links resolve, then announce the early preview with a clear way to report installation problems.
5. Use the next four days to collect reports, fix blockers, and keep Release 2 scope small. Publish the follow-up on Day 3–5 only after its exact-head checks pass; carry forward non-blocking limitations in its notes.
