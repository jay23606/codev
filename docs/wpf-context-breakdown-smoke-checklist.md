# WPF request-context breakdown smoke checklist

Run these checks in the WPF app. Hosted-provider checks make external requests and may incur API charges; use disposable prompts and do not include private project data.

- [ ] Send a local Ollama Chat request. Click the input/context usage label and verify the view shows provider, model, context limit, normalized messages, component character counts, rough token estimate, exact JSON body, and Ollama-reported prompt tokens.
- [ ] Attach a trusted project with a small source file and send a Chat request. Verify project guidance and trusted source excerpts have their own component entries and appear in the exact body.
- [ ] Select one project file explicitly and send a Chat request. Verify the selected excerpt is identified separately from conversation history.
- [ ] Enable local Code task and ask it to inspect a file. After the tool round, reopen the view and verify it represents the latest request, including available tool schemas, conversation/tool results, exact body, and latest reported usage.
- [ ] Connect OpenAI and send a disposable Chat request. Verify the request viewer contains the exact outgoing JSON but no API key, and shows provider-reported input tokens when the API returns them.
- [ ] Enable OpenAI Code task with workspace-sharing consent and make a harmless list/read request. Verify each subsequent round replaces the snapshot and the latest view includes the tool schema plus prior tool calls/results.
- [ ] Restart the app and verify request bodies are not persisted; a new request repopulates the view.
