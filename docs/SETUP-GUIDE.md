# Home Memory Setup Guide

## Using the Release (recommended)

Follow the [Quick Start](../README.md#quick-start-windows) in the README for the complete Windows release setup, including download, client registration, and the first query. The release is a self-contained build, with no .NET SDK and no Firebird installation required.

This guide covers custom database locations, schema migration, building from source, and troubleshooting.

## Configuration

### Environment Variables

| Variable | Purpose | Default |
|---|---|---|
| `HOME_MEMORY_DB_PATH` | Path to the database file | `%LOCALAPPDATA%\HomeMemory\homememory.scd` (Windows) · `~/.local/share/HomeMemory/homememory.scd` (Linux) · `~/Library/Application Support/HomeMemory/homememory.scd` (macOS) |
| `HOME_MEMORY_FBCLIENT` | Path to Firebird client library | See search order below |
| `FIREBIRD` | Firebird runtime directory | Set automatically by the server (process-level only, not persistent) |

On first run, Home Memory automatically creates the database directory and seeds it with over 100 editable categories and a default house structure. To use a custom database location, pass `HOME_MEMORY_DB_PATH` via `--env` when registering (see examples below).

### Updating an existing database schema

Normal server startup never changes the schema of an existing database. If startup
reports that the schema is not registered or requires migration, stop every Home Memory
and Smartconstruct process that uses the database. Then run the dedicated command with
the same environment configuration:

```powershell
$env:HOME_MEMORY_DB_PATH = 'C:\path\to\my-home.scd'
& 'C:\HomeMemory\HomeMemoryMCP.exe' --migrate --yes
```

The first migration stage supports Firebird Embedded databases. It requires explicit
confirmation, refuses to run while a cooperating server is active, and creates a
verified backup before changing an existing database. Keep the reported backup until
the updated database has been tested.

### Firebird client library search order

The server looks for the Firebird client library in this order:

| Priority | Source |
|---|---|
| 1 | `HOME_MEMORY_FBCLIENT` environment variable |
| 2 | `fbclient.dll` / `libfbclient.so` / `libfbclient.dylib` next to the executable |
| 3 | Default installation path (Windows: `C:\Program Files\Firebird\Firebird_3_0\fbclient.dll`) |

The server automatically sets the `FIREBIRD` environment variable (process-level only) to the directory containing the client library, so that Firebird Embedded can find its runtime files.

---

## Registering with an AI client

For standard registration, follow [Connect to your AI](../README.md#2-connect-to-your-ai) in the README. The examples below add a custom database location. If Home Memory is already registered, update that entry rather than adding a second server.

### With a custom database location

Replace the executable and database paths with your own. On macOS or Linux, use the paths to your built executable and database.

**Claude Code:**
```powershell
claude mcp add home-memory --scope user --env "HOME_MEMORY_DB_PATH=C:\HomeMemoryData\my-home.scd" -- "C:\HomeMemory\HomeMemoryMCP.exe"
```

**OpenAI Codex CLI:**
```powershell
codex mcp add home-memory --env "HOME_MEMORY_DB_PATH=C:\HomeMemoryData\my-home.scd" -- "C:\HomeMemory\HomeMemoryMCP.exe"
```

### Claude Desktop

Open the configuration as described in the [README](../README.md#2-connect-to-your-ai). Set `HOME_MEMORY_DB_PATH` in the `env` block of the `home-memory` entry inside `mcpServers`:

```json
"home-memory": {
  "command": "C:\\HomeMemory\\HomeMemoryMCP.exe",
  "env": {
    "HOME_MEMORY_DB_PATH": "C:\\HomeMemoryData\\my-home.scd"
  }
}
```

Omit the `env` block to use the default database location (`%LOCALAPPDATA%\HomeMemory\homememory.scd`).

Save and restart Claude Desktop. The README explains which sessions share this configuration.

### Managing registrations

```bash
# Claude Code
claude mcp list
claude mcp remove home-memory --scope user

# Codex CLI
codex mcp list
codex mcp remove home-memory
```

---

## Building from Source

### Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- [Firebird 3.0](https://firebirdsql.org/en/firebird-3-0/)

### Firebird installation by platform

**Windows:** Install via the [Firebird 3.0 Windows installer](https://firebirdsql.org/en/firebird-3-0/). Default path: `C:\Program Files\Firebird\Firebird_3_0\`. No further configuration needed.

**Linux:** Install via package manager or download from [firebirdsql.org](https://firebirdsql.org/en/firebird-3-0/). Typical location: `/usr/lib/x86_64-linux-gnu/firebird/3.0/lib/libfbclient.so`. Set `HOME_MEMORY_FBCLIENT` to point to your `libfbclient.so`.

**macOS:** Install via Homebrew (`brew install firebird`) or download from [firebirdsql.org](https://firebirdsql.org/en/firebird-3-0/). Set `HOME_MEMORY_FBCLIENT` to point to your `libfbclient.dylib`.

### Build and register

```bash
git clone https://github.com/impactjo/home-memory.git
cd home-memory
dotnet publish HomeMemoryMCP -c Release
```

Then follow [Connect to your AI](../README.md#2-connect-to-your-ai), replacing the release executable path with the full path to your built executable. For a custom database location, use the [examples above](#with-a-custom-database-location).

---

## Firebird Embedded Runtime Files

This section is only relevant when building from source. The release ZIP includes all required files.

The Firebird client library alone is not enough. Firebird Embedded loads additional files at startup. All files must be in the same directory as the client library.

### Windows

| File | Size | Purpose |
|---|---|---|
| `fbclient.dll` | 1.8 MB | Database engine |
| `firebird.msg` | 148 KB | Error messages |
| `firebird.conf` | small | Configuration |
| `icudt52l.dat` | 5.4 MB | Unicode data |
| `icuin52.dll` | 1.7 MB | Unicode normalization |
| `icuuc52.dll` | 1.3 MB | Unicode core |

### Linux / macOS

The equivalent shared libraries (`.so` / `.dylib`) plus `firebird.msg` and ICU data files. The exact filenames depend on your Firebird installation; check your Firebird 3 install directory.

Missing runtime files can cause `FbException: operating system directive CreateFile failed` on Windows or a similar I/O error on other platforms. This message alone does not establish the cause; file permissions or a database held by another process can also prevent access. See [Troubleshooting](#troubleshooting).

---

## Troubleshooting

### Home Memory does not appear in the client

Check that `home-memory` is registered and enabled in the client you are using. In Codex or Claude Code, use `/mcp` to inspect the connection. Follow the [client-specific restart instructions](../README.md#2-connect-to-your-ai); if it is still unavailable, start a new session.

Use a local session on the computer where Home Memory is installed. A cloud session does not automatically use that computer's MCP configuration. When connecting from a phone, select the host where you registered Home Memory.

### The server does not start

Check the client's MCP error or server log, then verify:

1. The configured command is the full path to `HomeMemoryMCP.exe`, especially if you moved or updated the installation.
2. The complete release ZIP was extracted. Keep the executable together with its bundled files.
3. The client can run the executable and access its files. If the error names a permission or sandbox restriction, check that client's access settings for the installation and database paths.

If startup explicitly reports that the database schema is not registered or requires migration, follow [Updating an existing database schema](#updating-an-existing-database-schema).

### A tool reports that the database is unavailable

If a tool says "This server cannot currently access the configured database", another application or Home Memory process may still be using the file. With the default embedded database in stdio mode, Home Memory opens the file during each tool call and releases it afterwards.

Wait briefly and retry the call. If the error persists, close the other process using the database. Restarting the AI client is not required. An [HTTP-mode server](HTTP-MODE.md#database-locking) holds the database open for as long as it runs.

### A source-built server reports `CreateFile failed`

Check that Firebird 3 is installed, `HOME_MEMORY_FBCLIENT` points to the correct client library, and the [required runtime files](#firebird-embedded-runtime-files) are present. If those are correct, check file access permissions and whether another process holds the database. Use the underlying error in the server log to narrow down the cause.

### Build errors: DLL locked

If `dotnet build` fails because the output DLL is locked, an MCP server process is still running. Stop it (or close the Claude session) and rebuild.
