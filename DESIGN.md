# Codev design

## Product direction

Codev is a calm, standalone coding workspace inspired by the conversational clarity of Claude, the project-aware agent workflow of Codex, and the model flexibility of OpenCode. It has its own Windows interface and connects to the user's local Ollama service.

The app should make many parallel lines of work feel organized: conversations are independent, searchable, persistent, and pinnable. Projects and their conversation history should stay easy to return to without turning the UI into an IDE full of panels.

## First-preview layout

- **Left rail:** Codev identity, new conversation, search, pinned conversations, recents, and a small local-service status card.
- **Top bar:** current conversation title, local privacy hint, model selector, and pin control.
- **Conversation canvas:** a focused welcome state with task starters, then readable role-labelled messages.
- **Composer:** multiline prompt, project-context picker, send action, and a short local-storage note.
- **Visual language:** warm off-white canvas, charcoal navigation, muted sage status, and restrained terracotta actions. Generous spacing and restrained chrome keep attention on the conversation.

## Interaction rules

- New conversation creates a separate history and does not overwrite another chat.
- Pinning is reversible and moves a conversation into a dedicated sidebar section.
- Search filters recent conversation titles.
- Model choice is stored per conversation.
- Project attachment is explicit. This preview reads a bounded set of text source/config files and sends them only to the local Ollama endpoint.
- Generation is streamed. This preview does not edit files or run shell commands.

## Milestones

1. **Foundation (current):** standalone WPF app, Ollama discovery and streaming, conversation persistence, model selection, pinning, search, bounded read-only project context.
2. **Project workspaces:** project list, per-project conversations, visible file tree, relevant-file selection, context budget display.
3. **Safe coding agent:** file read/edit tools, proposed diff review, per-action approval, undo/checkpoints, and command execution behind explicit approval.
4. **Quality of life:** tabs or split sessions, keyboard shortcuts, markdown/code rendering, export/import, and settings for endpoint/context limits.

## Implementation choices

- C# and modern .NET WPF keep Codev independent of VS Code and use a native Windows desktop UI.
- Ollama HTTP API is the inference and model-discovery backend. It preserves the existing model downloads and CPU-only runtime choice.
- Conversation persistence is local JSON under `%LOCALAPPDATA%\Codev` for the preview; a versioned SQLite store can replace it as project/workspace data grows.
