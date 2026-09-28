# Skills

Avalonia supports manually invoked Markdown skills. Type `/` to find a skill by its short description or type `/skills` to open the user and trusted-project skill folders.

Create a folder per skill:

- User skills: `%LOCALAPPDATA%\Codev\skills\<name>\SKILL.md` on Windows. These are available in every conversation.
- Project skills: `<project>/.codev/skills/<name>/SKILL.md`. These load only when the attached project is trusted and can be shared through Git.

The folder name becomes `/skill-<name>`. For example, `review/SKILL.md` appears as `/skill-review`:

```markdown
---
description: Review a project area for correctness and test gaps
arguments: area
---
Review {{area}} for correctness bugs, security issues, edge cases, and missing tests.
Return only actionable findings, prioritized by severity.
```

Select the skill, fill any named arguments using `name=value` (quote values with spaces), and select it again to insert its expanded prompt into the composer. The prompt stays editable and is not sent automatically. Project skills override user skills with the same name. Built-in and user slash commands take precedence over a colliding skill name.

Skill suggestions retain only the command name, description, arguments, scope, and file path; Codev reloads the Markdown body when a skill is selected. Files are limited to 16 KB, prompts to 12,000 characters, and each scope to 64 skills. Symbolic links and untrusted project skill folders are skipped. Markdown is treated as prompt text only: this implementation does not execute scripts or let the model invoke skills.
