# October staged release plan

Status checked 2026-10-07. This is the proposed sequence for the next public Codev release and its early follow-up. It does not authorize merging PR #1, waiving release checks, or publishing a tag.

## Release 1: early preview

Publish the current Avalonia candidate as a clearly labeled early preview once PR #1 reaches `main` and every Q3 release check in `DESIGN.md` is either completed or has a written, release-specific waiver. Use the repository's existing `v*` tag workflow, which publishes self-contained desktop and CLI archives for Windows x64, Linux x64, macOS Apple silicon, and macOS Intel. The README's `/releases/latest/download/` links already point users to the newest tag, with Windows first.

The current PR head is `96bc6e0`. Exact-head Windows/Linux/macOS CI [#37685736942](https://github.com/jay23606/codev/actions/runs/37685736942) passed. The four-target package preview [#37685736977](https://github.com/jay23606/codev/actions/runs/37685736977) passed on Windows and both macOS architectures, but Linux failed before its packaged UI smoke: Ubuntu's 15 MB `universe` package index did not finish within the workflow's 180-second apt timeout. Release 1 is therefore not yet technically green. Fix or simplify that setup, then rerun the complete preview on the exact candidate commit. Earlier exact-head package validation [#37681854975](https://github.com/jay23606/codev/actions/runs/37681854975) passed all four targets, including the Linux Secret Service OAuth smoke, but it predates the current workflow changes and cannot substitute for the current check.

The full self-contained native Windows packaged smoke passed locally in an isolated profile, and current-source Qwen3.6 Auto verification and file-edit live tests passed on Windows. PR #1 remains open; no release tag has been created.

Before publishing, finish or explicitly waive the remaining Q3 checks. The smoke checklist currently has 65 unchecked cases, including native macOS/Linux desktop interaction, assistive-technology and 200% scaling checks, and manual workflows for project context, tools, permissions and recovery. A clean-machine installation is also open (this Windows host does not have Windows Sandbox). Do not describe hosted runner interaction as physical-keyboard or clean-machine validation. Put any accepted limitations in the release notes.

**Timing:** do not announce a calendar date until the Linux package check is green and the remaining Q3 checks are complete or have explicit, release-specific written waivers. Once those gates are met, Release 1 can ship on the next release window after final review. Keep the candidate commit fixed during final verification. On publication, announce Release 2 for **Day 4** (within the 3–5 day window below); move it only to address a release-blocking report.

## Release 2: early stabilization

Target the next release **4 days after Release 1**, based on the latest `main` (acceptable window: Day 3–5). Treat Release 1 as Day 0; publish the follow-up on Day 4 so users have a few days to try the install while the follow-up remains scheduled. During that interval, gather installation failures and actionable user reports, fix any release blockers first, and select small usability or reliability fixes for the follow-up. Re-run Windows/Linux/macOS CI, all four packaged desktop/CLI checks, and the Windows packaged smoke on the exact candidate commit. Update the release notes and verify the latest README download links resolve to all expected archives.

If testing finds a data-loss, unauthorized-action, startup-crash, or false-success defect, delay Release 2 until it is fixed and verified. If no release-blocking issue appears, publish the tested follow-up tag on schedule and carry remaining non-blocking Q3 limitations forward with clear notes.

## Release sequence

1. Resolve Q3 rows or obtain written waivers for specific rows and reasons.
2. Review and merge PR #1 to `main`; confirm its exact-head checks are green.
3. Create a `v*` tag on the merged commit and verify that all eight expected archives are attached to the GitHub Release.
4. Confirm the README latest-download links resolve, then announce the early preview.
5. Collect reports for 3–5 days, fix blockers, rerun the exact-head checks, and publish Release 2 from `main`.
