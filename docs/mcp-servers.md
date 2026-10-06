# MCP servers

Codev connects configured Model Context Protocol (MCP) servers for Code tasks. A server can provide tools, prompts, resources, or resource templates. Chat mode does not connect MCP servers. Configure servers from **MCP servers…** in the Avalonia sidebar; the editor saves a JSON array to the local Codev data folder, and enabled servers connect the next time a Code task starts.

Codev supports local `Stdio` processes and remote `Http` endpoints. The MCP manager accepts up to 32 servers. Optional startup and catalog timeouts are limited to 1–120 seconds; execution timeouts are limited to 1–600 seconds. Omit timeout fields to use the defaults (30 seconds for startup/catalog and 120 seconds for execution).

## Local process (stdio)

Replace the command and path with an MCP server that you have installed and trust:

```json
[
  {
    "id": "my-local-server",
    "name": "My local server",
    "transport": "Stdio",
    "enabled": true,
    "command": "npx",
    "arguments": ["-y", "<installed-mcp-package>", "<server-argument>"],
    "workingDirectory": "C:\\path\\to\\project",
    "environmentVariables": {
      "SERVICE_API_KEY": "CODEV_MCP_SERVICE_API_KEY"
    }
  }
]
```

`command` is the executable and `arguments` is an argument array; Codev does not interpret them as a shell command line. `workingDirectory` is optional. Stdio processes do not inherit arbitrary environment variables from Codev. Each `environmentVariables` entry maps a variable name expected by the server to the *name* of an environment variable in the Codev process. Set the referenced variable in your operating system or launch environment; its value is not saved in the MCP JSON file. Keep the executable and server package under your control because a stdio server runs as a local process with your account's permissions.

## HTTP server

Remote hosts must use HTTPS. Plain HTTP is accepted only for loopback addresses such as `127.0.0.1` or `localhost`. Do not put credentials, query strings, or fragments in the URL.

HTTP servers use OAuth by default. Codev opens the system browser when the server requests authorization and stores OAuth tokens in the operating system credential vault. `OAuthClientId`, `OAuthScopes`, and `OAuthClientSecretEnvironmentVariable` are optional:

```json
[
  {
    "id": "remote-tools",
    "name": "Remote tools",
    "transport": "Http",
    "enabled": true,
    "url": "https://mcp.example.com/mcp",
    "oauthClientId": "<client-id>",
    "oauthScopes": ["tools"]
  }
]
```

For a server that uses a static header instead, disable OAuth and map the header to an environment variable name:

```json
[
  {
    "id": "internal-tools",
    "name": "Internal tools",
    "transport": "Http",
    "enabled": true,
    "url": "https://mcp.example.com/mcp",
    "oauthEnabled": false,
    "headerEnvironmentVariables": {
      "Authorization": "CODEV_INTERNAL_MCP_AUTHORIZATION"
    }
  }
]
```

Set `CODEV_INTERNAL_MCP_AUTHORIZATION` in the environment before starting Codev. Header values are never written to the configuration file and are not forwarded across HTTP redirects. To clear cached OAuth sign-ins, use **Forget saved OAuth sign-ins** in the MCP manager.

## Permissions and trust

MCP operations follow the selected project permission mode. **Auto** calls configured MCP operations without asking, unless a saved exact project deny rule blocks that server and operation. **Ask every time** requests approval; **Allowlist** uses saved exact server/operation rules. An **Allow** rule is bound to the current server configuration and advertised operation schema, so changing either requires approval again. Explicit Deny rules continue to block the operation. If saved permission data cannot be read or updated, Codev fails closed rather than calling the server.

MCP server instructions, descriptions, prompts, resources, and results come from an external process or service. Treat them as untrusted content. Calls may access local data through a stdio process or affect remote services through an HTTP server. Review the configured executable, endpoint, and permissions before enabling a server; Auto mode does not sandbox external MCP effects. MCP configuration and OAuth tokens are stored separately from project files and conversation backups.

## Troubleshooting

- If a server is unavailable, confirm it is enabled and that its executable/endpoint is reachable. Enabled servers connect when a new Code task starts; changing the list does not reconnect an already-running task.
- If a mapped environment variable is missing, set it in the environment used to launch Codev and restart the app.
- HTTP endpoints on non-loopback hosts must use HTTPS. Remove embedded credentials, query strings, or fragments from the URL and use OAuth or environment-backed headers instead.
- If an MCP operation asks for approval in Auto, check whether the exact server/operation has a saved Deny rule. Also check the selected profile: a profile can hide or deny an MCP tool. A project Deny remains effective in Auto.
- If MCP permission storage is unreadable, Codev requires approval and preserves the unreadable file for recovery; it does not silently grant access.
