# WPF slash-command smoke checklist

- [ ] Type `/` in the composer. Confirm a small suggestion popup appears above the composer without reserving footer space, with only commands supported by WPF and the saved prompt templates.
- [ ] Use Up/Down and Enter to run a built-in command; use the mouse on a suggestion too. Confirm exact `/status` runs locally and displays the current status without an Ollama or hosted API request.
- [ ] Run `/clear`, decline the confirmation, and confirm history remains. Accept it and confirm messages and summary clear while the project, model, and file-change history remain. Confirm the command refuses while this conversation has active or queued requests.
- [ ] Run `/compact` on a conversation with enough complete turns. Confirm it uses the existing review/summary flow and leaves the original transcript available.
- [ ] Run `/plan` and `/code`; confirm their existing mode restrictions and OpenAI workspace-sharing consent still apply. Run `/model` and confirm it opens the model picker.
- [ ] Run `/export` and confirm the existing Markdown export flow opens. Run `/init` and a saved `/template-…`; confirm each inserts editable text and sends nothing until the user sends it.
- [ ] Type `@` and confirm file mention suggestions continue to work. Type an unsupported slash name and confirm it is treated as ordinary user text.
