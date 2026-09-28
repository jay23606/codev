# Codev

Codev is a standalone Windows desktop coding workspace for local language models. It talks directly to Ollama on `127.0.0.1`; no VS Code, account, or cloud model is required.

## First preview

- A chat-first desktop layout, with dark mode as the default and light mode available
- Independent persistent conversations, pinning, and debounced search across titles and message text with matching excerpts
- Optional completion toasts for responses that finish while you are away from their conversation
- Conversation rename and archive/restore, with permanent deletion available from the archive view
- Regenerate the latest answer, edit/resend the last prompt, and branch a new conversation from a message
- Dark and light themes, with dark as the default and the choice remembered locally
- Per-conversation Ollama model selection, with quick choices for the three models in the original setup
- Per-conversation Ollama context selection, limited to each model's configured maximum
- Last request token count compared with the selected context size, reported by Ollama
- Streaming responses from Ollama
- Hardware-aware serial request queue with pause/resume controls; unsent turns survive app restart and wait for an explicit **Resume saved queue** action
- Assistant Markdown formatting with scrollable tables, clickable HTTP(S) links, and fenced code blocks with syntax coloring and a Copy button
- Read-only Plan mode, explicit Code task mode, Stop control that retains partial output, and current agent-tool status
- Project switcher with pinned projects, project-specific instructions and reusable knowledge notes, and conversations scoped to each project
- Git status, per-file staged/unstaged diff review, sending selected diff lines to the chat composer, staging, unstaging, reviewed local commits, and local branch creation/switching from a project's menu; branch operations require a clean tree, and commits are never pushed automatically
- Optional project-folder context: selected source/config files are read locally and sent to the local Ollama server as bounded context
- Choose specific project-relative source files for a conversation; right-click Add files to clear the selection
- Browse project text files, filter the list, preview them read-only, and add a selected file directly to chat context
- Explicit Code task mode with project-scoped list/read/search tools and approval-gated replacement of existing files; writes create a local recovery checkpoint
- Conversation history stays in `%LOCALAPPDATA%\Codev\conversations.json`
- Create a local JSON backup of all conversations and import one additively; imports receive new conversation IDs and do not replace current history

Code task mode can create or edit supported source, text, and configuration files after showing a review and receiving approval. The Files button shows changes and lets you review or restore checkpoints, including undo/redo of file creation. Code task mode can also request a PowerShell command; every command requires approval, runs with your Windows account permissions in the project folder, has a three-minute timeout, and is not sandboxed. Ordinary chat remains read-only.

## Run

Requirements: Windows 10/11, .NET 9 Desktop Runtime, and Ollama running at `http://127.0.0.1:11434`.

```powershell
dotnet run --project .\Codev.csproj
```

Build a self-contained Windows app:

```powershell
dotnet publish .\Codev.csproj -c Release -r win-x64 --self-contained true
```

## Local models

Codev discovers installed models from Ollama and shows the supported coding models below when installed. Duplicate tags and source repository names for the same weights are hidden from the picker:

- `devstral-small-2-64k`
- `qwen3-coder-next-q2-24k`
- `qwen3-coder:30b`

Legacy Ollama tags for the same weights are recognized and shown under the same friendly model name.

## Privacy

Prompts and project excerpts are sent only to the configured local Ollama endpoint. Chat mode reads a bounded set of common source/config files (up to 24 files and roughly 32,000 characters total), excluding build output, dependency folders, `.git`, and common secret files. Code task mode can read and search files within the selected workspace and apply a replacement only after user approval; it saves a checkpoint first and refuses to overwrite files that changed after review. Symbolic links and junctions are excluded. Approved shell commands are not sandboxed and can access resources available to your Windows account.

## Design

See [DESIGN.md](DESIGN.md) for the interaction model and roadmap.

## License

MIT. See [LICENSE](LICENSE).
