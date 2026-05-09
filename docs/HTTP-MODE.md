# HTTP mode (LAN, optional)

Home Memory ships with two transports:

- **stdio** - the default. The AI client launches `HomeMemoryMCP.exe` directly. Recommended for local single-user setups (Claude Desktop, Codex App, Claude Code).
- **HTTP** - runs Home Memory as a long-lived local service on a port. Useful when you want one Home Memory process to serve clients that can't launch a stdio command directly, or when you want the server reachable from another machine on your LAN.

Stdio is enough for almost everyone. HTTP is for advanced setups.

---

## When HTTP mode is the right choice

- You want Home Memory running on a NAS, homelab box, or always-on PC instead of launching it per AI session.
- You want to reach Home Memory from another machine on your LAN.
- You want to experiment with Home Assistant or other automation tools that can connect to an MCP server over HTTP.

HTTP mode is designed for local and LAN use. Do not expose Home Memory's built-in HTTP listener directly to the internet. It is plain HTTP with optional Bearer authentication. For internet-facing access, terminate TLS at a reverse proxy and treat OAuth, multi-user isolation, and audit logs as out of scope for this mode.

## Database locking

In stdio mode, Home Memory opens the default embedded database for each tool call and releases it afterwards. That often leaves room for another local session or application between calls, but it is not coordinated multi-process access.

In HTTP mode with the default embedded database, Home Memory keeps the database file open **for as long as the server runs**. Do not use the same database file from another Home Memory process or another application while the HTTP server is running. If you need deliberate multi-process access, use Firebird Server mode.

## Configuration

| Variable | Default | Purpose |
|---|---|---|
| `HOME_MEMORY_TRANSPORT` | `stdio` | Set to `http` to enable HTTP mode |
| `HOME_MEMORY_BIND` | `127.0.0.1` | IP to bind. Use `0.0.0.0` to expose on the LAN - see security note below |
| `HOME_MEMORY_PORT` | `5100` | TCP port |
| `HOME_MEMORY_API_KEY` | _(unset)_ | If set, requires `Authorization: Bearer <key>` on every request |

The standard env vars (`HOME_MEMORY_DB_PATH`, `HOME_MEMORY_FBCLIENT`) work in HTTP mode too.

## Starting the server

PowerShell:

```powershell
$env:HOME_MEMORY_TRANSPORT = "http"
& "C:\HomeMemory\HomeMemoryMCP.exe"
```

You should see:

```
[HomeMemory] Transport: HTTP
[HomeMemory] Listening: http://127.0.0.1:5100/mcp
[HomeMemory] Auth: none (set HOME_MEMORY_API_KEY to enable)
[HomeMemory] Note: HTTP mode - this process owns the database file
```

The endpoint speaks **Streamable HTTP** (the MCP transport variant). Pointing a browser at it will return a 4xx - that's expected; connect with an MCP client instead.

## Connecting Codex App (native HTTP)

Codex App speaks Streamable HTTP natively. Point it at the URL directly.

1. Open Codex App, **File > Settings**, select **MCP servers**
2. Click **+ Add server**
3. **Name:** `home-memory-http`
4. Choose transport **Streamable HTTP**
5. **URL:** `http://127.0.0.1:5100/mcp`
6. If you started Home Memory without `HOME_MEMORY_API_KEY`, leave **Bearer token env var** and **Headers** empty
7. **Save**, then restart Codex App

For LAN setups, prefer setting `HOME_MEMORY_API_KEY`. In Codex App, **Bearer token env var** expects the **name** of an environment variable, not the token value itself. Set the variable persistently and restart Codex App so it picks up the new environment:

```powershell
setx HOME_MEMORY_API_KEY your-long-random-key
```

Codex CLI uses the same config under `~/.codex/config.toml`:

```toml
[mcp_servers.home-memory-http]
enabled = true
url = "http://127.0.0.1:5100/mcp"
# bearer_token_env_var = "HOME_MEMORY_API_KEY"  # uncomment if API key is set
```

For non-interactive Codex CLI runs, configure tool approval as needed (e.g. `approval_mode = "approve"` under `[mcp_servers.home-memory-http.tools.<tool_name>]`).

## Connecting Claude Desktop via `mcp-remote`

Claude Desktop launches MCP servers via stdio commands. Use the [`mcp-remote`](https://www.npmjs.com/package/mcp-remote) bridge to forward stdio to your local HTTP server. You need [Node.js](https://nodejs.org/) installed; no global `mcp-remote` install is needed - `npx -y` fetches it on demand.

Add to `claude_desktop_config.json`:

```json
{
  "mcpServers": {
    "home-memory-http": {
      "command": "npx",
      "args": [
        "-y",
        "mcp-remote@latest",
        "http://127.0.0.1:5100/mcp",
        "--allow-http",
        "--transport",
        "http-only"
      ]
    }
  }
}
```

`--allow-http` is required because mcp-remote refuses non-HTTPS URLs unless explicitly allowed. `--transport http-only` pins the transport to Streamable HTTP and avoids client-side fallback negotiation.

Restart Claude Desktop. The `home-memory-http` server should appear and respond to tool calls.

## Adding Bearer authentication

Set `HOME_MEMORY_API_KEY` before starting the server:

```powershell
$env:HOME_MEMORY_TRANSPORT = "http"
$env:HOME_MEMORY_API_KEY = "your-long-random-key"
& "C:\HomeMemory\HomeMemoryMCP.exe"
```

Then add the header on the client side. On Windows, pass the value through an env var to avoid argument-quoting pitfalls:

```json
{
  "mcpServers": {
    "home-memory-http": {
      "command": "npx",
      "args": [
        "-y",
        "mcp-remote@latest",
        "http://127.0.0.1:5100/mcp",
        "--allow-http",
        "--transport",
        "http-only",
        "--header",
        "Authorization:${AUTH_HEADER}"
      ],
      "env": {
        "AUTH_HEADER": "Bearer your-long-random-key"
      }
    }
  }
}
```

Without the header (or with the wrong key) the server returns `401 Unauthorized` and the client disconnects.

For Codex App and Codex CLI, use the **Bearer token env var** field (or the `bearer_token_env_var` setting in `config.toml`). It expects the environment variable name, not the token value. See *Connecting Codex App* above.

## Exposing on the LAN

To accept connections from other machines on your LAN, set `HOME_MEMORY_BIND=0.0.0.0`. The server prints a startup warning if you do this without an API key - please set one. Anything reachable on your LAN should be authenticated.

For internet-facing access, terminate TLS at a reverse proxy (Caddy, nginx, Traefik). Home Memory's HTTP listener is plain HTTP and intentionally has no TLS support - that's a separate, larger piece of work.

## What's not in scope

- TLS termination - use a reverse proxy.
- OAuth 2.1 / dynamic client registration - out of scope for this transport.
- Multi-user, audit logs, session isolation - out of scope.
- Linux/macOS native binaries with HTTP - buildable from source, not yet packaged. A Linux/Docker image is on the roadmap, not part of HTTP mode itself.
