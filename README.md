# Codev

Codev is a standalone Windows desktop coding workspace for local language models. It talks directly to Ollama on `127.0.0.1`; no VS Code, account, or cloud model is required.

## First preview

- A chat-first, dark-sidebar/light-canvas desktop layout
- Independent persistent conversations, search, and pinning
- Dark and light themes, with dark as the default and the choice remembered locally
- Per-conversation Ollama model selection, with quick choices for the three models in the original setup
- Streaming responses from Ollama
- Optional project-folder context: selected source/config files are read locally and sent to the local Ollama server as bounded context
- Conversation history stays in `%LOCALAPPDATA%\Codev\conversations.json`

This preview does not edit project files, run commands, or provide an IDE. Those are planned for later milestones with explicit review and approval before changes are applied.

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

Prompts and project excerpts are sent only to the configured local Ollama endpoint. Attaching a project folder reads a bounded set of common source/config files (up to 24 files and roughly 32,000 characters total), excluding build output, dependency folders, and `.git`. No files are changed by this preview.

## Design

See [DESIGN.md](DESIGN.md) for the interaction model and roadmap.

## License

MIT. See [LICENSE](LICENSE).
