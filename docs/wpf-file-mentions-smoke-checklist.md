# WPF file-mention smoke checklist

Run these checks in the WPF app with a disposable project folder containing a few supported text/source files, an excluded file, and an unsupported binary.

- [ ] Type `@` in the composer and verify only safe, supported project-relative paths appear.
- [ ] Type a partial folder or file name after `@` and verify suggestions filter by that prefix.
- [ ] Use Up/Down and Enter; repeat with Tab and mouse selection. Verify the selected path replaces the active `@` token, is added to this conversation's context, and the caret remains after the path.
- [ ] Press Escape and verify suggestions close without changing the draft or selected context.
- [ ] Verify excluded files, unsupported/binary files, and paths outside the attached project are never suggested or added.
- [ ] Fill the 24-file context limit and choose another suggestion. Verify Codev leaves the draft and selection unchanged and reports why it could not attach the file.
- [ ] Ask local Chat and local Code task to use an explicitly mentioned file; inspect the request context and verify the selected excerpt is included. Repeat with OpenAI Code task only after enabling its workspace-sharing consent, and verify the selected excerpt appears in the outgoing request.
- [ ] Switch conversations while a mention is open and verify stale suggestions cannot attach a file to the wrong conversation.
