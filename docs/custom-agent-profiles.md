# Custom agent profiles

Codev includes built-in **Build**, **Plan**, **Code**, **Debug**, **Ask**, and **Orchestrator** agents. Build is the default primary agent. In an active Avalonia Code task, choose Plan from the agent picker or press `Ctrl+Shift+A` to switch between Build and Plan; the selection is saved per conversation and captured by queued turns. Plan can list, read, and search project files, but cannot edit, run commands, call MCP tools, or delegate. Its name is reserved so project or user Markdown cannot weaken that policy. Avalonia also supports Orchestrator child tasks in a root Code task with a trusted Git project. Each child starts immediately in its isolated worktree and can run alongside the parent; its result returns as untrusted context after the parent response is saved. Children cannot delegate. OpenAI delegation also requires hosted Code task and workspace sharing to be enabled before the parent starts. The three-child limit applies per parent conversation. WPF retains basic shared profile compatibility but is not the feature-parity target.

Code task profiles let you reuse instructions and tool restrictions. In Avalonia, choose a profile from the picker beside **Code task** and use **More → Manage agent profiles…** to create or edit user profiles in Codev. The editor validates Markdown before saving. **More → Open agent profiles folder** opens the same local user directory, `%LOCALAPPDATA%\Codev\agents` on Windows. For a trusted project, Codev also loads `.codev/agents/*.md` from that project; manage those project profiles in the repository. Project profiles with the same name take precedence over user and built-in profiles except for reserved **Plan** and **Orchestrator** names.

Conversations saved by older releases that selected the built-in **Code** profile migrate to the default **Build** agent when opened. A user or project profile named **Code** is preserved.

Create a Markdown file whose filename is a short name, such as `reviewer.md`:

```markdown
---
name: Reviewer
description: Inspect changes and focus on correctness and regressions.
default_permission: allow
tools: run_command=ask, verify_command=ask, mcp:*=deny
commands: *=allow, git status*=allow, git push*=deny
max_steps: 6
---
Review the changed code before editing. Look for correctness issues, edge cases,
and missing tests. Explain each finding with a concrete example.
```

Supported fields are `name`, `description`, optional `model`, optional `temperature` (0–2), optional `max_steps` (1–8), `default_permission` (`ask`, `allow`, or `deny`), comma-separated `tools` entries in the form `tool_pattern=ask|allow|deny`, optional `commands` entries in the form `command_pattern=ask|allow|deny`, optional `edit_paths`, and optional `deny_edit_paths`. Tool and command patterns support `*` and `?`, with later matching rules taking precedence. Command patterns apply to `run_command` and `verify_command`; whitespace is normalized before matching, and command chains are checked clause by clause. An omitted default permission means `ask`. Use `mcp:*` to apply a rule to every MCP tool.

`edit_paths` optionally limits `create_file`, `write_file`, and `apply_patch` to comma-separated project-relative glob patterns. `deny_edit_paths` adds exclusions that always win. `*` matches within one path segment and `**` may cross directories. For example, `edit_paths: src/**, index.html` and `deny_edit_paths: src/generated/**` permits edits under `src` and to `index.html`, except generated files. Omit both fields to leave edit paths unrestricted. Patterns cannot be absolute, contain `..`, or use backslashes; use `/` separators. These patterns govern Codev's file tools; shell commands can still modify files, so profiles that need a file-only boundary should also deny `run_command` and `verify_command`.

For example, `commands: *=allow, git *=ask, git push *=deny` keeps the project mode's normal behavior for other commands, asks before Git commands outside Auto, and always denies Git pushes. In Auto, `ask` rules are automatically approved like other requests; use `deny` for commands that must remain blocked.

MCP tools can be controlled per server using their generated function name, `mcp_<server-id>_<tool-name>_<stable-suffix>`. For example, `tools: mcp_github_*=deny, mcp_github_search_*=allow` denies GitHub MCP tools except search tools, while leaving other servers alone. Use `mcp:*=deny` to hide every MCP tool for an agent. Denied MCP tools are removed from both local and hosted model schemas and remain blocked if a model tries to invoke one directly.

Discovered user skills and trusted-project skills are exposed as on-demand `load_skill_*` tools. Use a matching pattern such as `tools: load_skill_* = deny` to make a profile user-invocation-only; denied loaders disappear from Ollama and OpenAI schemas and direct calls are blocked. A permitted loader rereads the Markdown prompt at call time, applies its bounded named arguments, and returns collapsed guidance. Project skill loading rechecks folder trust. Skill Markdown is prompt data only, subordinate to the user request and Codev policy; scripts mentioned in it are not executed.

`deny` hides the tool from the model and blocks it at runtime. `ask` requires a one-call confirmation outside Auto mode; in Auto mode, it follows the project's Auto policy. `allow` defers to Codev's selected project permission mode, so a profile cannot override a project deny rule or grant access that the project mode would ask about. File review and command permission rules still apply. Profile instructions are task guidance and cannot replace the user's request or Codev's safety rules. User-level profiles are loaded from the local app data folder; project profiles are loaded only when that folder is trusted.

A profile model override is used for that Code task. A profile temperature override is currently applied to local Ollama requests; hosted OpenAI requests report that the override is not applied. The built-in profiles are read-only; create a custom profile to adapt their behavior.

## Manual profile checks

- In Avalonia, select a user profile, send a Code task, and verify its instructions are present in the request context. Switch conversations and restart; verify each selection persists. Queue a turn with one profile, change the picker, and verify the queued turn keeps the captured profile. Check WPF compatibility only when validating the legacy Windows build.
- Add a profile that denies `run_command` and `write_file`; verify those tools are absent from the model schema and direct tool requests are rejected. Add an `ask` rule and verify one call is confirmed outside Auto; verify Auto uses the configured project policy. Confirm explicit project Deny still blocks the call.
- Add `tools: mcp_github_*=deny, mcp_github_search_*=allow`; verify GitHub search tools remain available while its other tools disappear from local and hosted schemas, unrelated server tools remain available, and a direct denied-tool request is blocked. Confirm `mcp:*=deny` blocks every server.
- In a Code task, ask the model to use a discovered user skill and a trusted-project skill; confirm only skill metadata is in the initial tool schema, prompt text is reloaded only when called, arguments expand, and the result is collapsed as guidance. Verify user/project scope precedence, reject project loading immediately after trust is revoked, and use `tools: load_skill_* = deny` to confirm schemas omit skill loaders and direct invocation is blocked. Confirm skill scripts never execute.
- Set `edit_paths: src/**` and `deny_edit_paths: src/generated/**`; verify file proposals outside `src` and under `src/generated` are rejected before review, while other `src` edits still follow normal file review.
- Set `commands: *=allow, git *=ask, git push *=deny`; verify a Git push is blocked even in Auto, other Git commands follow the selected mode, and later matching rules override earlier ones.
- Add a trusted project profile and verify it appears only for that trusted project. Untrust the folder or switch projects and confirm project-only profiles disappear and cannot be invoked.
- Set `max_steps` below the default and verify both local and OpenAI Code tasks stop at that cap. Set a model override and verify the request uses it. Set temperature and verify it applies to local Ollama while hosted OpenAI reports the limitation.
- In Avalonia, use **More → Manage agent profiles…** to create a profile, save it, and confirm it appears in the Code task picker. Reopen it, edit and save; verify the updated instructions are used by a new task. Try invalid frontmatter, duplicate display names, the reserved Orchestrator name, an oversized UTF-8 document, and a symbolic-link profile path; each must be rejected without changing the existing file. Verify **Revert** discards unsaved editor text and switching profiles is blocked until changes are saved or reverted.
- In a trusted project Code task, confirm **Build** is the default. Switch to **Plan** with the picker and `Ctrl+Shift+A`; verify both choices persist with the conversation and queued turn. Ask Plan to inspect files and propose a plan, then request an edit, command, MCP call, or delegation and confirm each denied tool is absent from the model schema and blocked if invoked directly. Confirm an untrusted project profile named Plan cannot override the reserved built-in policy.
