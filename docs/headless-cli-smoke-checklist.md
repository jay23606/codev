# Headless CLI smoke card

- Run `Codev.Cli --help` and verify the provider, workspace, consent, edit, and command options are listed.
- In a disposable workspace with a harmless sentinel file, run a local Ollama prompt through the packaged CLI and verify Codev reads the file and returns the expected sentinel.
- Run without `--allow-edits` or `--allow-commands`; verify the model cannot change workspace files or run shell commands.
- Try OpenAI without `--allow-hosted-data` and a remote Ollama endpoint without `--allow-remote-endpoint`; verify both fail before sending a request.
- Repeat provider-request smokes on Windows, macOS, and Linux; use only loopback Ollama endpoints unless remote sharing is explicitly enabled for the test.

## Run record

| Date | Build | OS / model | Result | Notes |
|---|---|---|---|---|
| 2026-10-04 | PR #1 head `77e476e` Windows CLI preview | Windows 11 · Qwen3.6 35B-A3B · local Ollama | Pass | In an isolated temporary workspace, the packaged `Codev.Cli.exe` read `smoke.txt` through Codev's tool loop and returned the exact unique line `FJORD-APPLES-7314`. No edit or command flags were enabled; the workspace still contained only the unchanged fixture. The request used `127.0.0.1`; no project data left the machine. |
| 2026-10-04 | PR #1 head `77e476e` Windows CLI preview | Windows 11 · packaged CLI consent gates | Pass | `--help` listed the expected capability and sharing flags. OpenAI without `--allow-hosted-data` and a non-loopback Ollama endpoint (`192.0.2.1`) without `--allow-remote-endpoint` both exited 2 with the specific consent message before a provider request. |
| 2026-10-04 | PR #1 head `77e476e` Windows CLI preview | Windows 11 · Qwen3.6 35B-A3B · piped stdin | Pass | Piped a prompt through standard input into the packaged CLI; Codev read the unique line `EMBER-PIPE-9062` from the fixture and returned it exactly. |
