# Codev Avalonia preview — Release 2

Codev is a desktop coding workspace for local and hosted language models. This preview includes self-contained desktop and headless CLI packages for Windows x64, Linux x64, Apple silicon macOS, and Intel macOS.

## Downloads

Choose the desktop app for your system:

- Windows x64 — `Codev-Avalonia-preview-win-x64.zip`
- Linux x64 — `Codev-Avalonia-preview-linux-x64.zip`
- macOS Apple silicon — `Codev-Avalonia-preview-osx-arm64.zip`
- macOS Intel — `Codev-Avalonia-preview-osx-x64.zip`

Standalone headless CLI archives are also attached:

- Windows x64 — `Codev-CLI-preview-win-x64.zip`
- Linux x64 — `Codev-CLI-preview-linux-x64.zip`
- macOS Apple silicon — `Codev-CLI-preview-osx-arm64.zip`
- macOS Intel — `Codev-CLI-preview-osx-x64.zip`

On Windows, extract the complete ZIP and run `Codev.Avalonia.exe`. On macOS or Linux, extract the ZIP and run `Codev.Avalonia` from a desktop session. The packages include the .NET runtime. Ollama is optional when using hosted models and must be installed separately for local models. Linux hosted API-key storage requires Secret Service in the desktop session. These builds are not code-signed; Windows may show SmartScreen, and macOS may require a first-launch Gatekeeper override.

## What’s new since Release 1

- OpenCode V2 agent profiles now support ordered permissions for shell commands and resource-scoped file reads and edits, glob/grep searches, delegated tasks, and skills. Codev applies read scopes to direct and automatic project context, hides denied skills and unauthorized delegation tools, and warns or skips permission patterns it cannot enforce.
- MCP settings are easier to recover when the saved file is malformed, oversized, duplicated, or blocked by a directory at the settings path. MCP tool, prompt, resource, and resource-template activity is grouped into readable, expandable transcript details.
- Linux accessibility support is improved through Avalonia 12 and an AT-SPI tree. The sidebar search has an associated accessible label, and the Markdown renderer has been updated.
- Regression coverage now exercises Auto-mode file application and command execution, MCP activity, and scoped profile behavior across hosted Windows, Linux, and macOS checks.
- Auto falls back to Ask when saved project permission data cannot be read or safely written, and the review explains why. Repeated identical tool calls no longer trigger an extra prompt in Auto; the shared per-turn step limit and exact project Deny rules still apply.
- Conversations and child sessions can be dragged to reorder within their sidebar group, with order remembered after restart.

## Included capabilities

- Local Ollama models and hosted OpenAI and Anthropic chat. Code tasks support Ollama and OpenAI; Anthropic is available for Chat and Plan.
- Reusable built-in and Markdown agent profiles, with project-scoped profiles available only for trusted projects.
- Code task tools for bounded project-file operations, reviewable changes, rollback checkpoints, and Git status/review workflows.
- Auto, Ask, and Allowlist command permission modes. Auto runs commands without per-command approval unless an exact saved Deny rule blocks them.
- MCP tools, prompts, and resources over stdio or HTTP, with project-scoped permissions and optional OAuth.
- Up to three parallel child tasks in separate Codev-managed Git worktrees, with results reviewed and merged explicitly.
- Conversation queues and restart recovery, search, compaction, rewind, file-change history, and Markdown/HTML export.
- Optional repository maps, local semantic project search, verification-backed best-of-N attempts, and a one-shot headless CLI.

## Important permission note

Auto mode executes shell commands with your account permissions and does not sandbox them to the project folder. Use it only with projects and commands you trust. Project file edits are checkpointed; shell-command changes are not included in file-history checkpoints. A saved exact Deny rule blocks a matching command in every mode.

Hosted providers use their own API credentials, billing, and usage limits. Project files are not sent to a hosted provider unless workspace sharing is enabled for that conversation.

## Preview limitations

This is an early preview. Manual checks remain open for clean-machine installation, physical accessibility, native macOS/Linux use, and several long project-context, Git-review, and recovery workflows. GitHub-hosted packaged interaction tests pass on all supported targets, but they do not replace those manual checks. Linux AT-SPI tree and focus-event checks pass in hosted sessions, but intelligible screen-reader announcements and physical-device behavior remain unverified. See the [row-by-row release checklist](https://github.com/jay23606/codev/blob/main/docs/release-1-q3-dispositions.md).

For setup and feature details, see the [README](https://github.com/jay23606/codev/blob/main/README.md), [agent profiles guide](https://github.com/jay23606/codev/blob/main/docs/custom-agent-profiles.md), and [MCP server guide](https://github.com/jay23606/codev/blob/main/docs/mcp-servers.md).
