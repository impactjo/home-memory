<p align="center">
  <h1 align="center">Home Memory</h1>
  <p align="center"><strong>Ask your home.</strong></p>
  <p align="center">Your AI assistant's memory for everything in and around your home.</p>
</p>

<p align="center">
  <a href="LICENSE"><img src="https://img.shields.io/badge/license-AGPL--v3-blue.svg" alt="AGPL-3.0 License"></a>
  <img src="https://img.shields.io/badge/.NET-10-purple.svg" alt=".NET 10">
  <img src="https://img.shields.io/badge/MCP_tools-23-green.svg" alt="23 MCP Tools">
</p>

<p align="center">
  <a href="https://www.youtube.com/watch?v=J9ziKLE8NO8">
    <img src="https://img.youtube.com/vi/J9ziKLE8NO8/maxresdefault.jpg" width="600" alt="Watch the Home Memory demo on YouTube">
  </a>
  <br>
  <a href="https://www.youtube.com/watch?v=J9ziKLE8NO8">▶ Watch the 1-minute demo on YouTube</a>
</p>

---

Home Memory is an [MCP server](https://modelcontextprotocol.io/) that gives your AI assistant structured, persistent knowledge about your home: every room, every device, every pipe and cable, every item you own. It plugs into any AI client on your computer that supports local MCP servers (Claude Desktop, Claude Code, Codex App, Codex CLI, and others) and turns natural conversation into living, queryable documentation of your home and everything in it.

Document what exists, plan what's coming, and keep track of what's been removed.

No app to learn. No forms to fill out. Your home data stays in a single file on your machine, and the AI *is* your interface.

Tell your AI about your heat pump, your car, your power tools, or your wine collection, and it extracts the relevant details and stores them as structured data in your local database. Snap a photo of a device or hand it an invoice. Same result. Ask "What's in the basement?" or "When is my car due for inspection?" and get real answers from real data, not hallucinations.

<p align="center">
  <img src="docs/demo.png" width="700" alt="Demo in Claude Desktop: One sentence creates two elements. The AI finds the right categories, creates a missing room, and documents everything.">
  <br>
  <em>Claude Desktop (Anthropic)</em>
</p>

<p align="center">
  <img src="docs/demo-codex.png" width="700" alt="Demo in Codex App: Same prompt, same result, works across AI clients.">
  <br>
  <em>Codex App (OpenAI)</em>
</p>

## What you can do

**Document anything by just talking:**
> "I have a Daikin Altherma heat pump in the utility room."

Your AI finds the right category, resolves the location, and creates the element. No manual data entry.

> "My car is a 2023 Toyota Corolla Hybrid, next inspection is due in March."

Not just building infrastructure: vehicles, tools, appliances, valuables, anything that belongs to you.

**Ask questions about your home:**
> "What's in the basement?" &middot; "Show me all planned purchases." &middot; "Where is my washing machine?"

**Upload a photo and let your AI identify it:**
> *(attach a photo of a device)* "What is this? Add it to the utility room."

Vision-capable AIs recognize the device and create the element via MCP.

**Read an invoice and extract devices:**
> *(attach a PDF invoice)* "Extract the installed devices and add them to my home."

**Track connections between elements:**
> "The circuit breaker panel feeds the kitchen outlet via NYM-J 3x1.5."

Cable routes, pipe runs, duct paths, all documented as connections between elements.

**Plan renovations:**
> "We're planning a PV system on the roof." &middot; "The old oil heater was removed last year."

Track what's planned, what exists, and what's been removed.

## Quick Start (Windows)

The release ZIP is self-contained. No .NET, no Firebird, no other software to install.

### 1. Download & Extract

1. Download the latest release ZIP from [GitHub Releases](../../releases)
2. Extract to a folder, e.g. `C:\HomeMemory\`

### 2. Connect to your AI

Choose **one** of the following clients:

<details>
<summary><strong>Codex App (OpenAI)</strong></summary>

1. Open the Codex App
2. Click **File > Settings**, then select **MCP servers** on the left
3. Click **+ Add server**
4. **Name:** `home-memory`
5. **Command to launch:** `C:\HomeMemory\HomeMemoryMCP.exe`
6. Leave transport on **STDIO** (default)
7. Click **Save**, then restart the app if needed

Or via Codex CLI:
```bash
codex mcp add home-memory -- "C:\HomeMemory\HomeMemoryMCP.exe"
```

</details>

<details>
<summary><strong>Claude Desktop</strong></summary>

1. Open Claude Desktop
2. Click the **Claude menu** → **Settings** → **Developer** → **Edit Config**
3. This opens the config folder with `claude_desktop_config.json` selected. Open it in any text editor
4. Add `home-memory` inside the `"mcpServers"` object:

```json
{
  "mcpServers": {
    "home-memory": {
      "command": "C:\\HomeMemory\\HomeMemoryMCP.exe"
    }
  }
}
```

If you already have other MCP servers configured, add the `"home-memory"` entry next to them inside the existing `"mcpServers"` block.

5. Save the file and restart Claude Desktop

Home Memory is available in both the Chat tab and the Code tab. The **Code tab is recommended**: it runs in agentic mode with no tool-call limits, which works much better for MCP-heavy workflows.

</details>

<details>
<summary><strong>Claude Code (CLI)</strong></summary>

```bash
claude mcp add home-memory --scope user -- "C:\HomeMemory\HomeMemoryMCP.exe"
```

</details>

### 3. Try it

On first launch, Home Memory automatically creates a local database with over 100 categories and a default house structure (floors, rooms, garage, outdoor areas). No setup wizard needed.

**Try these prompts in order:**

> "Show me the structure of my home."

You should see your default house structure: ground floor, upper floor, basement, each with rooms. This confirms everything is working.

> "I have a Bosch washing machine in the basement."

Your AI creates the element, finds the right category, and places it in the basement, all in one step. Ask "What's in the basement?" to verify.

> "We're planning to install a heat pump in the utility room."

Creates a planned element, so you can track what exists and what's coming.

**If it works, you're done.** Everything from here is just talking to your AI. Add rooms, rename floors, document your electrical panel, upload a photo of a device. The AI handles the rest.

### Build from Source (advanced)

Requires [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) and [Firebird 3.0](https://firebirdsql.org/en/firebird-3-0/).

```bash
git clone https://github.com/impactjo/home-memory.git
cd home-memory
dotnet publish HomeMemoryMCP -c Release
```

See [Setup Guide](docs/SETUP-GUIDE.md) for details on Firebird configuration and environment variables.

## How it works

```
You ──── AI Assistant ──── Home Memory MCP ──── Local Database
              (natural language)      (20+ tools)       (Firebird, single file)
```

Home Memory implements the [Model Context Protocol (MCP)](https://modelcontextprotocol.io/), an open standard that lets AI assistants use external tools. When you talk to your AI about your home, it calls Home Memory's tools behind the scenes to read, create, update, and search your home data.

**Your data stays local.** The database is a single file on your machine. Nothing is sent anywhere except to the AI you're already talking to.

## Features

### 23 MCP Tools

| | Tools | What they do |
|---|---|---|
| **Explore** | `get_structure_overview`, `find_element`, `list_elements`, `get_element_details`, `get_recent_changes` | Browse your home, search by name/path/status, get full details |
| **Manage Elements** | `create_element`, `update_element`, `delete_element`, `move_element` | Add devices, furniture, fixtures, or entire rooms and floors |
| **Connections** | `get_connections`, `get_connection_details`, `create_connection`, `update_connection`, `delete_connection` | Document physical lines: cables, pipes, ducts, conduits |
| **Categories** | `list_categories`, `get_by_category`, `create_category`, `update_category`, `delete_category` | Over 100 built-in, editable categories across all domains |
| **Status** | `list_statuses`, `create_status`, `update_status`, `delete_status` | Track what's existing, planned, or removed |

### Covers every domain, fully customizable to your needs

Electrical (circuits, PV, wallbox, home automation) &middot; HVAC &middot; Plumbing &middot; IT & Communications &middot; Security (alarm, fire, surveillance) &middot; Building Materials &middot; Landscaping (garden, pool, irrigation) &middot; Household (appliances, furniture, valuables) &middot; Vehicles &middot; Tools &middot; Health &middot; Sports & Leisure

Every category, every element, every status is editable. Rename, add, or remove anything to match how you think about your home.

### Smart defaults

- **Over 100 categories** organized by trade and domain, from circuit breakers to garden sprinklers to vehicles
- **Default house structure** with floors, rooms, garage, and outdoor areas, customizable by talking to your AI
- **Auto-setup** on first run, no manual database creation needed
- **Flexible naming**: your AI can use "Ground Floor" or "GF", the server resolves both

## Compatibility

| Client | Status |
|---|---|
| Codex App (OpenAI) | Tested |
| Claude Desktop (Chat tab + Code tab) | Tested |
| Claude Code (CLI) | Tested |
| Codex CLI (OpenAI) | Tested |
| Any MCP-compatible client | Should work (stdio transport) |

The release ZIP is a self-contained Windows build with all dependencies included (no .NET or Firebird installation required). On macOS and Linux, you can build from source with .NET 10 and Firebird 3; see the [Setup Guide](docs/SETUP-GUIDE.md) for details.

## Configuration

| Environment Variable | Purpose | Default |
|---|---|---|
| `HOME_MEMORY_DB_PATH` | Database file location | `%LOCALAPPDATA%\HomeMemory\homememory.scd` (Windows) · `~/.local/share/HomeMemory/homememory.scd` (Linux) · `~/Library/Application Support/HomeMemory/homememory.scd` (macOS) |
| `HOME_MEMORY_FBCLIENT` | Path to Firebird client library | Bundled with release / Firebird installation |

## Architecture

- **.NET 10** with [ModelContextProtocol SDK](https://github.com/modelcontextprotocol/csharp-sdk)
- **Firebird Embedded**: local database engine, single-file storage
- **Raw SQL** with recursive CTEs, no ORM overhead, transparent and auditable

## Contributing

Home Memory is early-stage. Feedback is welcome. Right now, the best way to contribute is:

- **Open an issue** for bug reports, feature ideas, or questions
- **Share your use case**: how are you using Home Memory? What's missing?
- **Spread the word** if you find it useful

If you'd like to build and explore the code locally, see the [Setup Guide](docs/SETUP-GUIDE.md).

## Background

Home Memory builds on a proven data model from [Smartconstruct](https://smartconstruct.io), a desktop application for documenting physical assets, refined through real-world use in residential construction projects.

## License

[AGPL-3.0](LICENSE)

---

<p align="center">
  <a href="https://home-memory.com">home-memory.com</a>
</p>
