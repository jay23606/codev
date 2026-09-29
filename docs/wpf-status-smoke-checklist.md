# WPF `/status` smoke checklist

Run in the WPF app. This command should only add a local status response; it must not contact Ollama or a hosted model.

- [ ] Send `/status` in a new conversation and verify the visible user/assistant pair contains provider, model, context, summary, temperature, thinking/sampling, mode, project, selected-context, queue, hosted-request, and project-command permission details.
- [ ] Confirm `/status` leaves the model request count unchanged and does not require an installed model or connected API key.
- [ ] Send a model request, then `/status`; verify last-request input usage appears only for the currently selected provider/model and uses a percentage only when an explicit local context limit is known.
- [ ] Attach an existing project folder and run `/status`; verify it names the project without exposing its full path, and reports automatic or selected project context accurately for Chat, local Code task, and consented OpenAI Code task.
- [ ] Create and then clear a conversation summary; verify the status reports its current summary range or `off`.
- [ ] Run `/status` while a request is active or queued; verify it reports progress/queue state without disrupting the request or queue.
