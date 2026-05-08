# HTTP mode (LAN, optional)

Home Memory ships with two transports:

- **stdio** — the default. The AI client launches `HomeMemoryMCP.exe` directly. Recommended for local single-user setups (Claude Desktop, Codex App, Claude Code).
- **HTTP** — runs Home Memory as a long-lived local service on a port. Useful when you want one Home Memory process to serve clients that can't launch a stdio command directly, or when you want the server reachable from another machine on your LAN.

Stdio is enough for almost everyone. HTTP is for advanced setups.

---

## When HTTP mode is the right choice

- You want to keep Home Memory running as a persistent local service (e.g. always-on on a NAS or homelab box) instead of spawning it per session.
- You want to use Home Memory from a Claude Desktop, Codex App, or other MCP client that can talk to a local HTTP MCP server through a bridge like [`mcp-remote`](https://www.npmjs.com/package/mcp-remote).
- You want to reach the server from another machine on your LAN.

For internet-facing remote access (OAuth, TLS, multi-user) HTTP mode is **not** the right tool. Put a reverse proxy with TLS in front, or wait for that work to land separately.

## Single-owner constraint

When `HOME_MEMORY_TRANSPORT=http` is set, this Home Memory process **owns the database file** for as long as it runs. Connection pooling is enabled by default to handle parallel requests well.

That means you cannot run a stdio Home Memory and an HTTP Home Memory against the same `.scd` file at the same time — the second one will fail to open the database. Pick one transport per database file.

If you also use the database from another tool (e.g. the original Smartconstruct desktop app), don't run the HTTP mode against it; use stdio instead, or use Firebird Server mode (advanced; ask in an issue if you need it).

## Configuration

| Variable | Default | Purpose |
|---|---|---|
| `HOME_MEMORY_TRANSPORT` | `stdio` | Set to `http` to enable HTTP mode |
| `HOME_MEMORY_BIND` | `127.0.0.1` | IP to bind. Use `0.0.0.0` to expose on the LAN — see security note below |
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

The endpoint speaks **Streamable HTTP** (the MCP transport variant). Pointing a browser at it will return a 4xx — that's expected. Use a real MCP client.

## Connecting Claude Desktop via `mcp-remote`

Claude Desktop launches MCP servers via stdio commands. Use the [`mcp-remote`](https://www.npmjs.com/package/mcp-remote) bridge to forward stdio to your local HTTP server. You need [Node.js](https://nodejs.org/) installed; no global `mcp-remote` install is needed — `npx -y` fetches it on demand.

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

## Exposing on the LAN

To accept connections from other machines on your LAN, set `HOME_MEMORY_BIND=0.0.0.0`. The server prints a startup warning if you do this without an API key — please set one. Anything reachable on your LAN should be authenticated.

For internet-facing access, terminate TLS at a reverse proxy (Caddy, nginx, Traefik). Home Memory's HTTP listener is plain HTTP and intentionally has no TLS support — that's a separate, larger piece of work.

## What's not in scope

- TLS termination — use a reverse proxy.
- OAuth 2.1 / dynamic client registration — out of scope for this transport.
- Multi-user, audit logs, session isolation — out of scope.
- Linux/macOS native binaries with HTTP — buildable from source, not yet packaged. A Linux/Docker image is on the roadmap, not part of HTTP mode itself.
