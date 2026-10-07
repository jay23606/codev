# October staged release plan

Status checked 2026-10-07. This is the proposed sequence for the next public Codev release and its early follow-up. It does not authorize merging PR #1, waiving release checks, or publishing a tag.

## Release 1: early preview

Publish the current Avalonia candidate as a clearly labeled early preview once PR #1 reaches `main` and every Q3 release check in `DESIGN.md` is either completed or has a written, release-specific waiver. Use the repository's existing `v*` tag workflow, which publishes self-contained desktop and CLI archives for Windows x64, Linux x64, macOS Apple silicon, and macOS Intel. The README's `/releases/latest/download/` links already point users to the newest tag, with Windows first.

The latest exact-head candidate is PR code head `1adfa95`. Windows/Linux/macOS CI [#37692402366](https://github.com/jay23606/codev/actions/runs/37692402366) passed. Four-target app/CLI preview [#37692402309](https://github.com/jay23606/codev/actions/runs/37692402309) passed for Windows x64, Linux x64, macOS Apple silicon, and macOS Intel; an independent dispatch on the same head [#37692425854](https://github.com/jay23606/codev/actions/runs/37692425854) also passed all four targets. The first Windows job attempt in the PR preview failed its Full width persistence assertion (the saved value remained 960 instead of 0); rerunning that Windows job on the unchanged head passed the packaged UI, upgrade/removal, and CLI checks. Linux and both macOS jobs passed. Treat this as a non-reproducing UI automation failure to monitor, not evidence of a product regression. Release publishing was skipped because PR #1 remains open. Automated exact-head checks are green; no release tag has been created.

The current latest public release is `v2026.10.01-3196395` (two releases are listed). The four README `/releases/latest/download/` desktop links are present in that release and each returned HTTP 200 when checked on 2026-10-07. The next tagged release will move those stable links to the new Avalonia candidate automatically.

The release-page copy is drafted in [`docs/release-notes-draft-2026-10.md`](release-notes-draft-2026-10.md). It corrects the current release description's stale claim that Avalonia lacks Code tasks, Git, and hosted providers, and clearly states Auto-mode command access and the manual validation still open. Review it against the final candidate before publishing.

The full self-contained native Windows packaged smoke passed locally in an isolated profile, and current-source Qwen3.6 Auto verification, file-edit, and parallel-child live tests passed on Windows. The exact candidate's cross-platform CI and four-target package checks now pass. PR #1 remains open; no release tag has been created.

Before publishing, finish or explicitly waive the remaining Q3 checks. The smoke checklist currently has 65 unchecked cases, including native macOS/Linux desktop interaction, assistive-technology and 200% scaling checks, and manual workflows for project context, tools, permissions and recovery. A clean-machine installation is also open (this Windows host does not have Windows Sandbox). Do not describe hosted runner interaction as physical-keyboard or clean-machine validation. Put any accepted limitations in the release notes.

**Target schedule:** aim to publish Release 1 on **Friday, October 9, 2026**, as an early preview. Freeze the candidate and finish the release decision by **Thursday, October 8**. This date is conditional: the remaining Q3 checks must be completed or have a written, release-specific waiver before publication, and the exact candidate must pass its final Windows/Linux/macOS CI and four-target package checks. If those gates are not met, move Release 1 to the first day they are met; do not silently waive them to preserve the date. The latest candidate `1adfa95` has passing exact-head CI and four-target package checks, including a successful Windows packaged-smoke retry after a Full width persistence assertion failed on its first attempt.

## Release 2: early stabilization

Target Release 2 for **Tuesday, October 13, 2026**, four days after the October 9 target for Release 1 (acceptable window: Day 3–5). Treat Release 1 as Day 0. During the four-day interval, gather installation failures and actionable user reports, fix any release blockers first, and select small usability or reliability fixes for the follow-up. Re-run Windows/Linux/macOS CI, all four packaged desktop/CLI checks, and the Windows packaged smoke on the exact candidate commit. Update the release notes and verify the latest README download links resolve to all expected archives. If Release 1 moves, move Release 2 with it so the follow-up still lands four days later.

If testing finds a data-loss, unauthorized-action, startup-crash, or false-success defect, delay Release 2 until it is fixed and verified. If no release-blocking issue appears, publish the tested follow-up tag on schedule and carry remaining non-blocking Q3 limitations forward with clear notes.

## Release sequence

1. Resolve Q3 rows or obtain written waivers for specific rows and reasons.
2. Review and merge PR #1 to `main`; confirm its exact-head checks are green.
3. Create a `v*` tag on the merged commit and verify that all eight expected archives are attached to the GitHub Release.
4. Confirm the README latest-download links resolve, then announce the early preview.
5. Collect reports for four days (within the 3–5 day window), fix blockers, rerun exact-head checks, and publish Release 2 from `main` on the target date or four days after a delayed Release 1.
