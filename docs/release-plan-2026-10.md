# October staged release plan

Status checked 2026-10-07. This is the proposed sequence for the next public Codev release and its early follow-up. It does not authorize merging PR #1, waiving release checks, or publishing a tag.

## Release 1: early preview

Publish the current Avalonia candidate as a clearly labeled early preview once PR #1 reaches `main` and every Q3 release check in `DESIGN.md` is either completed or has a written, release-specific waiver. Use the repository's existing `v*` tag workflow, which publishes self-contained desktop and CLI archives for Windows x64, Linux x64, macOS Apple silicon, and macOS Intel. The README's `/releases/latest/download/` links already point users to the newest tag, with Windows first.

The latest package-workflow candidate is PR code head `5b04db4`. Exact-head Windows/Linux/macOS CI [#37689128812](https://github.com/jay23606/codev/actions/runs/37689128812) passed. Four-target app/CLI preview [#37689128843](https://github.com/jay23606/codev/actions/runs/37689128843) passed for Windows x64, Linux x64, macOS Apple silicon, and macOS Intel. The first Windows UI smoke attempt failed to persist the 960-pixel reading width (it remained at 800); rerunning the Windows job on the same commit passed the full packaged UI smoke, upgrade/removal checks, and CLI checks. Linux and both macOS jobs passed on the first attempt. This is evidence of a non-reproducing first-attempt failure, not a product regression. The GitHub release-publishing job was skipped because PR #1 remains open. Automated release checks are green on this candidate; no release tag has been created.

The current latest public release is `v2026.10.01-3196395` (two releases are listed). The four README `/releases/latest/download/` desktop links are present in that release and each returned HTTP 200 when checked on 2026-10-07. The next tagged release will move those stable links to the new Avalonia candidate automatically.

The full self-contained native Windows packaged smoke passed locally in an isolated profile, and current-source Qwen3.6 Auto verification and file-edit live tests passed on Windows. PR #1 remains open; no release tag has been created.

Before publishing, finish or explicitly waive the remaining Q3 checks. The smoke checklist currently has 65 unchecked cases, including native macOS/Linux desktop interaction, assistive-technology and 200% scaling checks, and manual workflows for project context, tools, permissions and recovery. A clean-machine installation is also open (this Windows host does not have Windows Sandbox). Do not describe hosted runner interaction as physical-keyboard or clean-machine validation. Put any accepted limitations in the release notes.

**Target schedule:** aim to publish Release 1 on **Friday, October 9, 2026**, as an early preview. Freeze the candidate and finish the release decision by **Thursday, October 8**. This date is conditional: the remaining Q3 checks must be completed or have a written, release-specific waiver before publication, and the exact candidate must pass its final Windows/Linux/macOS CI and four-target package checks. If those gates are not met, move Release 1 to the first day they are met; do not silently waive them to preserve the date. The Windows packaged UI smoke retry for head `5b04db4` has now passed after the first attempt's 960-pixel reading-width persistence failure did not reproduce.

## Release 2: early stabilization

Target Release 2 for **Tuesday, October 13, 2026**, four days after the October 9 target for Release 1 (acceptable window: Day 3–5). Treat Release 1 as Day 0. During the four-day interval, gather installation failures and actionable user reports, fix any release blockers first, and select small usability or reliability fixes for the follow-up. Re-run Windows/Linux/macOS CI, all four packaged desktop/CLI checks, and the Windows packaged smoke on the exact candidate commit. Update the release notes and verify the latest README download links resolve to all expected archives. If Release 1 moves, move Release 2 with it so the follow-up still lands four days later.

If testing finds a data-loss, unauthorized-action, startup-crash, or false-success defect, delay Release 2 until it is fixed and verified. If no release-blocking issue appears, publish the tested follow-up tag on schedule and carry remaining non-blocking Q3 limitations forward with clear notes.

## Release sequence

1. Resolve Q3 rows or obtain written waivers for specific rows and reasons.
2. Review and merge PR #1 to `main`; confirm its exact-head checks are green.
3. Create a `v*` tag on the merged commit and verify that all eight expected archives are attached to the GitHub Release.
4. Confirm the README latest-download links resolve, then announce the early preview.
5. Collect reports for four days (within the 3–5 day window), fix blockers, rerun exact-head checks, and publish Release 2 from `main` on the target date or four days after a delayed Release 1.
