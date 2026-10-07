# October staged release plan

Status checked 2026-10-07. This is the proposed sequence for the next public Codev release and its early follow-up. It does not authorize merging PR #1, waiving release checks, or publishing a tag.

## Release 1: early preview

Publish the current Avalonia candidate as a clearly labeled early preview once PR #1 reaches `main` and every Q3 release check in `DESIGN.md` is either completed or has a written, release-specific waiver. Use the repository's existing `v*` tag workflow, which publishes self-contained desktop and CLI archives for Windows x64, Linux x64, macOS Apple silicon, and macOS Intel. The README's `/releases/latest/download/` links already point users to the newest tag, with Windows first.

The latest package-workflow candidate is PR code head `1739bd6`. Exact-head Windows/Linux/macOS CI [#37686576047](https://github.com/jay23606/codev/actions/runs/37686576047) and the four-target package preview [#37686576062](https://github.com/jay23606/codev/actions/runs/37686576062) passed. The latter completed packaged app and CLI checks for Windows x64, Linux x64, macOS Apple silicon, and macOS Intel, including the Linux interaction smoke after increasing the bounded apt setup timeout. Later commits only refreshed validation documentation. The GitHub release-publishing job was skipped because PR #1 remains open. Automated release checks are green on this candidate; no release tag has been created.

The full self-contained native Windows packaged smoke passed locally in an isolated profile, and current-source Qwen3.6 Auto verification and file-edit live tests passed on Windows. PR #1 remains open; no release tag has been created.

Before publishing, finish or explicitly waive the remaining Q3 checks. The smoke checklist currently has 65 unchecked cases, including native macOS/Linux desktop interaction, assistive-technology and 200% scaling checks, and manual workflows for project context, tools, permissions and recovery. A clean-machine installation is also open (this Windows host does not have Windows Sandbox). Do not describe hosted runner interaction as physical-keyboard or clean-machine validation. Put any accepted limitations in the release notes.

**Timing:** the exact-head automated gates are now green, but the remaining Q3 checks still need to be completed or have explicit, release-specific written waivers before setting a date for Release 1. Once those gates are met, ship on the next release window after final review, keeping the candidate commit fixed during final verification. On publication, announce Release 2 for **Day 4** (within the 3–5 day window below); move it only to address a release-blocking report.

## Release 2: early stabilization

Target the next release **4 days after Release 1**, based on the latest `main` (acceptable window: Day 3–5). Treat Release 1 as Day 0; publish the follow-up on Day 4 so users have a few days to try the install while the follow-up remains scheduled. During that interval, gather installation failures and actionable user reports, fix any release blockers first, and select small usability or reliability fixes for the follow-up. Re-run Windows/Linux/macOS CI, all four packaged desktop/CLI checks, and the Windows packaged smoke on the exact candidate commit. Update the release notes and verify the latest README download links resolve to all expected archives.

If testing finds a data-loss, unauthorized-action, startup-crash, or false-success defect, delay Release 2 until it is fixed and verified. If no release-blocking issue appears, publish the tested follow-up tag on schedule and carry remaining non-blocking Q3 limitations forward with clear notes.

## Release sequence

1. Resolve Q3 rows or obtain written waivers for specific rows and reasons.
2. Review and merge PR #1 to `main`; confirm its exact-head checks are green.
3. Create a `v*` tag on the merged commit and verify that all eight expected archives are attached to the GitHub Release.
4. Confirm the README latest-download links resolve, then announce the early preview.
5. Collect reports for 3–5 days, fix blockers, rerun the exact-head checks, and publish Release 2 from `main`.
