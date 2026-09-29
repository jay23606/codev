# WPF conversation compaction smoke checklist

Use a disposable conversation. Model calls may incur API charges when using a hosted provider.

- [ ] Create at least seven complete user/assistant turns. Right-click a user or assistant message at a complete-turn boundary and choose **Summarize up to here**. Confirm Codev proposes a concise summary using the selected model and shows an editable review before applying it.
- [ ] Apply the summary, then verify the visible transcript, Markdown export, and conversation backup still contain the original messages. Confirm the header identifies summarized history and future prompts retain messages outside the selected range.
- [ ] On a conversation without an accepted summary, right-click an older user prompt and choose **Summarize from here**. Verify messages before that prompt and messages after the selected end remain available in future prompt history.
- [ ] Extend a summary by summarizing a later complete boundary. Confirm Codev incorporates the prior summary with the newly selected messages and keeps the same summary start.
- [ ] Choose **Restore full history** from a message context menu. Verify future prompts again use the full transcript and the header summary marker disappears.
- [ ] Rewind by editing a user prompt inside the summarized range. Confirm the stale summary is cleared, the transcript rewinds, and the prompt is restored to the composer.
- [ ] Export and back up a summarized conversation, restart Codev, and verify the accepted summary and exact range persist. Import the backup and verify the range is retained only when valid for the imported transcript.
- [ ] With Ollama and an explicitly selected context size, cross 80% usage in a disposable long conversation. Confirm Codev offers compaction only after the reply finishes and only applies a summary after review and approval. Declining leaves history unchanged.
- [ ] Use Ollama's model-default context setting. Verify usage shows the token count without inventing a context denominator and tells the user the default context size is unknown; choose an explicit context size or use a message-level summary action.
- [ ] Send a hosted chat and OpenAI Code task when the provider reports input usage. Verify the usage label says provider-reported input tokens and never displays a local context denominator.
