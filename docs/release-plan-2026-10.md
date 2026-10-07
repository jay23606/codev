# October staged release plan

Status checked 2026-10-07 against PR head `b7ce8ec`. This is the proposed sequence for the next public Codev release and its early follow-up. It does not authorize merging PR #1, waiving release checks, or publishing a tag.

## Release 1: early preview

Publish the current Avalonia candidate as a clearly labeled early preview once PR #1 reaches `main` and every Q3 release check in `DESIGN.md` is either completed or has a written, release-specific waiver. Use the repository's existing `v*` tag workflow, which publishes self-contained desktop and CLI archives for Windows x64, Linux x64, macOS Apple silicon, and macOS Intel. The README's `/releases/latest/download/` links already point users to the newest tag, with Windows first.

The current candidate is PR head `b7ce8ec`; the app source remains the packaged build from `7473609`, with follow-up changes covering display-layout tests and reliable Linux credential-smoke setup. Exact-head Windows/Linux/macOS CI [#37676708333](https://github.com/jay23606/codev/actions/runs/37676708333) and all four packaged desktop/CLI previews [#37676708328](https://github.com/jay23606/codev/actions/runs/37676708328) passed, including the native Linux MCP OAuth Secret Service check. The full self-contained native Windows packaged smoke passed locally in an isolated profile. PR #1 is open with a clean merge state, and no release tag has been created.

Before publishing, finish or explicitly waive the remaining Q3 checks. The smoke checklist still contains many unchecked feature-specific manual cases, including native macOS/Linux desktop interaction, assistive-technology and 200% scaling checks, and manual workflows for project context, tools, permissions and recovery. A clean-machine installation is also open (this Windows host does not have Windows Sandbox). Do not describe hosted runner interaction as physical-keyboard or clean-machine validation. Put any accepted limitations in the release notes.

## Release 2: early stabilization

Target the next release 3–5 days after Release 1, based on the latest `main`. Treat Release 1 as Day 0 and schedule Release 2 for Day 3–5; that gives early users time to try installation without leaving the follow-up open-ended. During that interval, gather installation failures and actionable user reports, fix any release blockers first, and select small usability or reliability fixes for the follow-up. Re-run Windows/Linux/macOS CI, all four packaged desktop/CLI checks, and the Windows packaged smoke on the exact candidate commit. Update the release notes and verify the latest README download links resolve to all expected archives.

If testing finds a data-loss, unauthorized-action, startup-crash, or false-success defect, delay Release 2 until it is fixed and verified. If no release-blocking issue appears, publish the tested follow-up tag on schedule and carry remaining non-blocking Q3 limitations forward with clear notes.

## Release sequence

1. Resolve Q3 rows or obtain written waivers for specific rows and reasons.
2. Review and merge PR #1 to `main`; confirm its exact-head checks are green.
3. Create a `v*` tag on the merged commit and verify that all eight expected archives are attached to the GitHub Release.
4. Confirm the README latest-download links resolve, then announce the early preview.
5. Collect reports for 3–5 days, fix blockers, rerun the exact-head checks, and publish Release 2 from `main`.
