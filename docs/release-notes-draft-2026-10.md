# Codev Avalonia early preview — release notes draft

**Draft only. Do not publish until the release checklist and PR #1 release gates are resolved.**

Codev is a standalone desktop coding workspace for local and hosted language models. This release makes the Avalonia app the primary cross-platform desktop build and includes Windows, Linux, Apple silicon macOS, and Intel macOS packages. The packages are self-contained and include the .NET runtime.

## Downloads

Choose the desktop app for your system:

- Windows x64 — `Codev-Avalonia-preview-win-x64.zip`
- Linux x64 — `Codev-Avalonia-preview-linux-x64.zip`
- macOS Apple silicon — `Codev-Avalonia-preview-osx-arm64.zip`
- macOS Intel — `Codev-Avalonia-preview-osx-x64.zip`

Standalone headless CLI archives are also attached for all four targets as `Codev-CLI-preview-<runtime>.zip`.

On Windows, extract the complete ZIP and run `Codev.Avalonia.exe`. On macOS or Linux, extract the ZIP and run `Codev.Avalonia` from a desktop session. These builds are not code-signed; Windows may show SmartScreen, and macOS may require a first-launch Gatekeeper override. Linux hosted API-key storage requires Secret Service in the desktop session.

## What’s included

- Local Ollama models, plus hosted OpenAI and Anthropic chat. Code tasks support Ollama and OpenAI; Anthropic is available for Chat and Plan.
- Reusable built-in and Markdown agent profiles, with project-scoped profiles available only for trusted projects.
- Code task tools for bounded project-file operations, reviewable changes, rollback checkpoints, and Git status/review workflows.
- Auto, Ask, and Allowlist command permission modes. Auto runs commands without per-command approval unless an exact saved Deny rule blocks them.
- MCP tools, prompts, and resources over stdio or HTTP, with project-scoped permissions and optional OAuth.
- Up to three parallel child tasks in separate Codev-managed Git worktrees, with results reviewed and merged explicitly.
- Conversation queues and restart recovery, search, compaction, rewind, file-change history, and Markdown/HTML export.
- Optional repository maps, local semantic project search, and a one-shot headless CLI.

## Important permission note

Auto mode executes shell commands with your account permissions and does not sandbox them to the project folder. Use it only with projects and commands you trust. Project file edits are checkpointed; shell-command changes are not included in file-history checkpoints. A saved exact Deny rule blocks a matching command in every mode.

Hosted providers use their own API credentials, billing, and usage limits. Project files are not sent to a hosted provider unless workspace sharing is enabled for that conversation.

## Preview limitations

This is an early preview. The current release checklist still has manual interaction checks open, including clean-machine installation, physical accessibility checks, and native manual use on macOS and Linux. GitHub-hosted packaged interaction tests pass on all supported targets, but they do not replace those manual checks. The Linux build does not currently expose an AT-SPI accessibility tree.

For setup and feature details, see the repository [README](../README.md), [agent profiles guide](custom-agent-profiles.md), and [MCP server guide](mcp-servers.md).
