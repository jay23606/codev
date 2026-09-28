# WPF hosted-provider smoke checklist

Use a disposable conversation. These checks call external APIs and may incur charges. Do not use private project data.

- [ ] Open **Connect** beside the model picker. Verify the dialog distinguishes OpenAI API and Anthropic API (Claude), explains separate API billing, says the conversation history is sent, and confirms project files/instructions are not attached.
- [ ] Attempt to connect without checking the acknowledgement; verify the key is not used. Cancel and verify the current model and conversation remain unchanged.
- [ ] Connect with an OpenAI API key and separately with an Anthropic API key; verify available text models appear with the provider name and a streamed reply completes.
- [ ] Verify missing and invalid keys show useful errors without displaying the key. Attempt a failed reconnect while connected and confirm the working key remains active.
- [ ] Select a hosted model and send a disposable prompt. Verify the header identifies the hosted provider, hosted chat is read-only, project context/temperature controls are disabled, and project files/instructions are omitted.
- [ ] Switch the same conversation back to an Ollama model and verify the header returns to local status and local project-context behavior works as before.
- [ ] Queue a hosted request, close Codev before it starts, reopen, and resume it. Verify Codev asks you to reconnect because the API key was intentionally not persisted; reconnect and retry.
- [ ] Inspect settings, conversation history, and a conversation backup; verify no API key was written. Close and relaunch, verify hosted conversations retain their provider/model selection with a reconnect-key placeholder, and verify Ollama remains available offline.
- [ ] Set `OPENAI_API_KEY` or `ANTHROPIC_API_KEY`, leave the key field blank, and verify the selected environment key can connect.
