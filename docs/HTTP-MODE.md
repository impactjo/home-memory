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

HTTP mode is designed for local and LAN use. Do not expose Home Memory's built-in HTTP listener directly to the internet. It uses plain HTTP. Bearer authentication is optional only on loopback and required for every non-loopback bind. For access across an untrusted network, terminate TLS at a reverse proxy and treat OAuth, multi-user isolation, and audit logs as out of scope for this mode.

## Database locking

In stdio mode, Home Memory opens the default embedded database for each tool call and releases it afterwards. That often leaves room for another local session or application between calls, but it is not coordinated multi-process access.

In HTTP mode with the default embedded database, Home Memory keeps the database file open **for as long as the server runs**. Do not use the same database file from another Home Memory process or another application while the HTTP server is running. If you need deliberate multi-process access, use Firebird Server mode.

## Configuration

| Variable | Default | Purpose |
|---|---|---|
| `HOME_MEMORY_TRANSPORT` | `stdio` | Set to `http` to enable HTTP mode |
| `HOME_MEMORY_BIND` | `127.0.0.1` | IP to bind. Use `0.0.0.0` to listen on all interfaces. A non-loopback value requires an API key |
| `HOME_MEMORY_PORT` | `5100` | TCP port |
| `HOME_MEMORY_API_KEY` | _(unset)_ | Optional on loopback. Required for any non-loopback bind. Requires `Authorization: Bearer <key>` on every MCP request |
| `HOME_MEMORY_ALLOWED_HOSTS` | _(unset)_ | Optional comma-separated hostnames accepted in addition to the bind IP. Wildcards are rejected |

The standard env vars (`HOME_MEMORY_DB_PATH`, `HOME_MEMORY_FBCLIENT`) work in HTTP mode too.

## HTTP security model

Home Memory applies the following rules before serving the MCP endpoint:

1. A listener bound only to `127.0.0.1` or `::1` may run without an API key.
2. Every other bind address, including `0.0.0.0`, `::`, and a specific LAN address, requires `HOME_MEMORY_API_KEY`. The process exits before opening the database when the key is missing or blank.
3. The request `Host` must match the configured IP, a loopback name, an IP literal on a wildcard listener, or an explicit entry in `HOME_MEMORY_ALLOWED_HOSTS`.
4. Browser requests carrying an `Origin` header are rejected with `403 Forbidden`. Native MCP clients normally omit this header.

This protects keyless loopback mode against browser-based DNS rebinding and unintended network access. It does not protect against a malicious local process running as the same operating-system user. Such a process may already be able to read the database file directly. Multi-user isolation is not a target of this mode.

A reverse proxy makes a loopback listener reachable beyond the local machine. Always set `HOME_MEMORY_API_KEY` when using a reverse proxy, even when the Home Memory process itself remains bound to loopback.

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
[HomeMemory] Auth: none (loopback only)
[HomeMemory] Request security: Host and Origin checks active
[HomeMemory] HTTP mode: this process owns the database file
```

The endpoint speaks **Streamable HTTP** (the MCP transport variant). Pointing a browser at it will return a 4xx - that's expected; connect with an MCP client instead.

## Connecting Codex App (native HTTP)

Codex App speaks Streamable HTTP natively. Point it at the URL directly.

1. Open Codex App, **File > Settings**, select **MCP servers**
2. Click **+ Add server**
3. **Name:** `home-memory-http`
4. Choose transport **Streamable HTTP**
5. **URL:** `http://127.0.0.1:5100/mcp`
6. For a loopback server started without `HOME_MEMORY_API_KEY`, leave **Bearer token env var** and **Headers** empty
7. **Save**, then restart Codex App

For LAN setups, `HOME_MEMORY_API_KEY` is mandatory. In Codex App, **Bearer token env var** expects the **name** of an environment variable, not the token value itself. Set the variable persistently and restart Codex App so it picks up the new environment:

```powershell
setx HOME_MEMORY_API_KEY your-long-random-key
```

Codex CLI uses the same config under `~/.codex/config.toml`:

```toml
[mcp_servers.home-memory-http]
enabled = true
url = "http://127.0.0.1:5100/mcp"
# bearer_token_env_var = "HOME_MEMORY_API_KEY"  # required for non-loopback servers
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

Bearer authentication is optional for a loopback-only server and mandatory for every LAN bind.

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

To accept connections from other machines on your LAN, set both `HOME_MEMORY_BIND=0.0.0.0` and `HOME_MEMORY_API_KEY`. The server refuses to start if a non-loopback listener has no key. Connect with the server's IP address, for example `http://192.168.1.50:5100/mcp`.

If clients use a DNS name or a reverse proxy forwards its public host name, add the exact host name to `HOME_MEMORY_ALLOWED_HOSTS`. Separate multiple names with commas. Do not include a scheme, port, path, or wildcard.

For access across an untrusted network, set `HOME_MEMORY_API_KEY` and terminate TLS at a reverse proxy such as Caddy, nginx, or Traefik. Home Memory's HTTP listener is plain HTTP and intentionally has no built-in TLS support. The proxy must forward a host name accepted by `HOME_MEMORY_ALLOWED_HOSTS`.

## What's not in scope

- TLS termination - use a reverse proxy.
- OAuth 2.1 / dynamic client registration - out of scope for this transport.
- Multi-user, audit logs, session isolation - out of scope.
- Linux/macOS native binaries with HTTP - buildable from source, not yet packaged. A Linux/Docker image is on the roadmap, not part of HTTP mode itself.
