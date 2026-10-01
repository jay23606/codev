# Skills

Codev skills are reusable Markdown guidance. They can be inserted into the composer for review with a slash command, or loaded by the model on demand during a Code task. Type `/` to find a skill by its short description or type `/skills` to open the user and trusted-project skill folders.

Create a folder per skill:

- Codev user skills: `%LOCALAPPDATA%\Codev\skills\<name>\SKILL.md` on Windows. These are available in every conversation.
- Compatible user skills are also discovered from `~/.config/opencode/skills/<name>/SKILL.md`, `~/.claude/skills/<name>/SKILL.md`, and `~/.agents/skills/<name>/SKILL.md`. OpenCode's location honors `XDG_CONFIG_HOME` and `OPENCODE_CONFIG_DIR`.
- Project skills: `<project>/.codev/skills/<name>/SKILL.md`, `<project>/.opencode/skills/<name>/SKILL.md`, `<project>/.claude/skills/<name>/SKILL.md`, and `<project>/.agents/skills/<name>/SKILL.md`. These load only when the attached project is trusted and can be shared through Git. Codev checks for linked folders/files and keeps discovery inside the project root.

The folder name becomes `/skill-<name>`. For example, `review/SKILL.md` appears as `/skill-review`:

```markdown
---
description: Review a project area for correctness and test gaps
arguments: area
user-only: false
---
Review {{area}} for correctness bugs, security issues, edge cases, and missing tests.
Return only actionable findings, prioritized by severity.
```

Names may contain letters, digits, hyphens, and underscores, up to 40 characters, and must start with a letter. OpenCode-compatible `name` frontmatter is optional, but when present it must match the folder name. Codev accepts `description`, and ignores the standard `license`, `compatibility`, and string-map `metadata` fields. For safe, compact slash suggestions, Codev limits descriptions to 180 characters (OpenCode allows longer descriptions). Declare up to eight unique named arguments; every declared argument must have a matching `{{placeholder}}`, and every placeholder must be declared. The body must contain 1–12,000 characters. The complete UTF-8 Markdown file is limited to 16 KB. Invalid frontmatter, invalid UTF-8, symbolic-link folders/files, and oversized files are skipped with a diagnostic. Only top-level skill folders are scanned, with a limit of 64 skills per scope.

Select the skill, fill any named arguments using `name=value` (quote values with spaces), and select it again to insert its expanded prompt into the composer. The prompt stays editable and is not sent automatically. Project skills override user skills with the same name. Within project scopes, `.codev/skills` wins over `.opencode/skills`, `.claude/skills`, then `.agents/skills` when names collide; the Codev user folder similarly takes precedence over shared user folders. Built-in and user slash commands take precedence over a colliding skill name.

Skill suggestions retain only the command name, description, arguments, scope, and file path; Codev reloads the Markdown body when a skill is selected. During a Code task, the selected local Ollama model or opted-in hosted OpenAI model also sees each model-callable skill's name and description as a `load_skill_*` tool. The loader reads the Markdown body only when requested and expands its named arguments at call time. Project skill access rechecks folder trust before reading. Scripts mentioned by skills are not executed; the supported format is prompt text. Project skills take precedence over user skills with the same name; user and project slash-command names take precedence over colliding skills in composer suggestions.

Set `user-only: true` in a skill's frontmatter to keep it available through `/skill-name` while omitting its loader from both local and hosted model tool schemas. Codev also rejects direct calls to that loader. This setting is useful for workflows the user should choose explicitly rather than have the model discover and load. Alternatively, an agent profile can use `tools: load_skill_* = deny` to hide all skill loaders for that profile. Loaded skill text appears as a collapsed guidance item. It is prompt data, subordinate to the user's request and Codev's system rules; it cannot authorize tools, approvals, secret access, or other actions. Skills do not install tools or grant permissions.
