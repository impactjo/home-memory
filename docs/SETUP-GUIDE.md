# Home Memory — Setup Guide

## Using the Release (recommended)

Download the release ZIP from [GitHub Releases](../../releases) and follow the [Quick Start](../README.md#quick-start-windows) in the README. The release is a self-contained build, with no .NET SDK and no Firebird installation required.

## Configuration

### Environment Variables

| Variable | Purpose | Default |
|---|---|---|
| `HOME_MEMORY_DB_PATH` | Path to the database file | `%LOCALAPPDATA%\HomeMemory\homememory.scd` (Windows) · `~/.local/share/HomeMemory/homememory.scd` (Linux) · `~/Library/Application Support/HomeMemory/homememory.scd` (macOS) |
| `HOME_MEMORY_FBCLIENT` | Path to Firebird client library | See search order below |
| `FIREBIRD` | Firebird runtime directory | Set automatically by the server (process-level only, not persistent) |

On first run, Home Memory automatically creates the database directory and seeds it with over 100 editable categories and a default house structure. To use a custom database location, pass `HOME_MEMORY_DB_PATH` via `--env` when registering (see examples below).

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

**Claude Code:**
```bash
claude mcp add home-memory --scope user -- "/path/to/HomeMemoryMCP.exe"
```

**OpenAI Codex CLI:**
```bash
codex mcp add home-memory -- "/path/to/HomeMemoryMCP.exe"
```

### With a custom database location

**Claude Code:**
```bash
claude mcp add home-memory --scope user --env "HOME_MEMORY_DB_PATH=/path/to/my-home.scd" -- "/path/to/HomeMemoryMCP.exe"
```

**OpenAI Codex CLI:**
```bash
codex mcp add home-memory --env "HOME_MEMORY_DB_PATH=/path/to/my-home.scd" -- "/path/to/HomeMemoryMCP.exe"
```

### Claude Desktop

Open the **Claude menu** → **Settings** → **Developer** → **Edit Config**. This opens the correct config file regardless of how Claude Desktop was installed (Store or direct install).

Add the `home-memory` entry inside `mcpServers` (keep any existing entries):

```json
"home-memory": {
  "command": "C:\\path\\to\\HomeMemoryMCP.exe",
  "args": [],
  "env": {
    "HOME_MEMORY_DB_PATH": "C:\\path\\to\\my-home.scd"
  }
}
```

Omit the `env` block to use the default database location (`%LOCALAPPDATA%\HomeMemory\homememory.scd`).

> **Note:** If you register Home Memory via Claude Code (`claude mcp add`), it may also appear in the Code tab. Avoid defining the same server in both the Desktop config and Claude Code, as the Desktop configuration can override the Code tab in some versions.

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

Then register the built executable with your AI client (see [Registering with an AI client](#registering-with-an-ai-client) above).

---

## Firebird Embedded — Required Runtime Files

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

If any required file is missing, you'll see:
`FbException: operating system directive CreateFile failed` (Windows) or a similar I/O error on other platforms. This error is misleading: it's not about file permissions, but missing Firebird runtime files.

---

## Troubleshooting

### `FbException: operating system directive CreateFile failed`

Almost always a Firebird setup problem:
1. Firebird not installed, or `HOME_MEMORY_FBCLIENT` pointing to the wrong location
2. Firebird runtime files missing from the client library directory
3. Database locked by another process

### Database locked

Home Memory uses Firebird Embedded, which takes an exclusive lock on the database file during each tool call. If another process has the file open (e.g., the Smartconstruct desktop app), you'll get a lock error. Close the other process and try again.

### Build errors: DLL locked

If `dotnet build` fails because the output DLL is locked, an MCP server process is still running. Stop it (or close the Claude session) and rebuild.
