# Changelog

Notable changes per release. Download builds from the [releases page](https://github.com/impactjo/home-memory/releases).

## v0.4.0 — Category & connection details (2026-06-13)

### New tool: get_category_details

Returns the full details of a category — path, parent, item counts,
purpose, note, description, and user manual. Rounds out the read side to
match `get_element_details` and `get_connection_details`.

### New

Richer metadata on categories and connections:

- Categories now store `purpose`, `note`, and `user_manual` in addition to
  `description`. All four are settable on create/update and shown in
  `get_category_details`.
- Connections now store `status` and `user_manual`. Both are settable on
  create/update and shown in `get_connection_details`. `get_connections`
  gains a `status` filter, and `searchAllFields` now also searches the
  user manual.
- `list_categories` shows compact flags for which of these fields are
  populated.
- `update_category` now guides the assistant to check existing content
  before overwriting a text field, matching `update_element` and
  `update_connection`.

### Improvements

- `find_element` orders results by their position in the tree, then by name,
  so siblings stay grouped under their parent.
- `delete_status` reports connection usage in addition to element usage when
  a status is still assigned.

### Fixes

- The `Existing` status filter in `find_element` and `get_connections` now
  also matches records that have no status set — the normal case for things
  that are simply present.
- Path filters (`under`) escape `%` and `_` so they are matched literally
  instead of as wildcards.
- `update_element` and `update_connection` reject calls that provide no
  fields to update instead of silently bumping the change timestamp.
- An `update_status` call that only changes the note no longer rewrites the
  status name with the casing used in the lookup.
- An empty short name on update is rejected instead of being stored blank;
  use the explicit clear option to remove it.
- `get_connections` no longer prints a duplicate source header when two
  sources share a name prefix.
- Connection and category lookups fold case in the database instead of in
  the application, so case-insensitive matching behaves the same across all
  tools (relevant for non-ASCII names).

### Updated

- Server instructions describe the element hierarchy and clarify the scope
  of connections.
- README and tool descriptions: clearer guidance on field choice and element
  naming.

## v0.3.0 — HTTP transport for LAN, Firebird Server mode (2026-05-15)

### New

#### HTTP transport mode (optional, LAN use)

Home Memory can now run as a local HTTP service (Streamable HTTP via
`ModelContextProtocol.AspNetCore`) in addition to stdio. Stdio remains the
default. HTTP mode is opt-in and intended for LAN use, not the public
internet.

Enable with `HOME_MEMORY_TRANSPORT=http`. Configuration:
- `HOME_MEMORY_BIND` (default `127.0.0.1`)
- `HOME_MEMORY_PORT` (default `5100`)
- `HOME_MEMORY_API_KEY` (optional Bearer token, constant-time compare)
- `HOME_MEMORY_HTTP_DIAGNOSTICS=1` for opt-in request logging

Connection pooling adapts to the transport: HTTP keeps a small pool
(`MaxPoolSize=8`), stdio behavior is unchanged.

In HTTP mode with the default embedded database, the server holds the
database file open for its lifetime. For shared multi-process access, use
Firebird Server mode (see below).

Setup details: [docs/HTTP-MODE.md](https://github.com/impactjo/home-memory/blob/main/docs/HTTP-MODE.md).

#### Firebird Server connection mode

Home Memory can now connect to a Firebird Server instance instead of the
embedded database. Useful for shared or concurrent multi-process access.

Enable with `HOME_MEMORY_DB_MODE=Server`. Required env vars:
`HOME_MEMORY_DB_HOST`, `HOME_MEMORY_DB_PORT`, `HOME_MEMORY_DB_PATH`,
`HOME_MEMORY_DB_USER`, `HOME_MEMORY_DB_PASSWORD`.

#### Other

- `HOME_MEMORY_DB_POOLING` env var for explicit pool control, with clearer
  startup error messages when configuration is incomplete.

### Improvements

- Category lookup in `get_by_category`, `find_element`, and `get_connections`
  now prefers exact Name/ShortName matches before falling back to partial
  text matches.

### Fixes

- Ambiguous partial category matches in `get_by_category`, `find_element`,
  and `get_connections` now return an error instead of silently picking
  one (#3).

### Updated

- README: phone access via Claude Code Remote Control, YouTube demo
  thumbnail, intro and footer polish.
- HTTP mode docs: Codex App setup, clarity passes on bind defaults and
  Bearer-header quoting on Windows.

## v0.2.0 — Full-text search, recent changes tool (2026-04-12)

### New tool: get_recent_changes

Shows recently created or updated elements, connections, and categories —
newest first. Handy in many situations, e.g. when resuming after a break:
"What was last changed?"
Optional type filter (`element`, `connection`, `category`) and configurable limit.

### New

- `find_element`: new `searchAllFields` parameter — searches Purpose, Note,
  Description, UserManual, and Position in addition to name; shows a field
  hint when the match is outside the name
- `get_connections`: new `searchAllFields` parameter — searches Purpose, Note,
  Description, and Route; shows a field hint on match
- Browse tools: result limits and truncation warnings

### Fixes

- Category matching: exact name match is preferred over partial match

## v0.1.3 — Category filter for find_element and get_connections (2026-03-29)

### New

- Category filter for find_element and get_connections: filter by name, short name, or full path; includes all subcategories automatically

### Fixes

- Bool parameters accept both JSON booleans and string form ("true"/"false")
- Status filter: exact name match before partial match

### Updated

- README: Code tab recommendation for Claude Desktop

## v0.1.2 — Category filter, path fixes (2026-03-22)

### Fixes

- Consistent path normalization across all tools
- Clearer error messages when elements can't be found
- list_categories: single tree output, is_structural_area filter

### Updated

- ModelContextProtocol SDK 1.1.0, Microsoft.Extensions.Hosting 10.0.5
- Improved Quick Start in README, added setup instructions for Codex

## v0.1.1 — 22 MCP tools, Firebird Embedded (2026-03-14)

First public release.

22 MCP tools (9 read, 13 write) for elements, connections, categories, and statuses. Track cables, pipes, and ducts: where they run and what they connect.

- Firebird Embedded database: single file, zero config
- 112 categories, 3 statuses, 15 default areas as starting point
- Self-contained Windows build (includes .NET runtime and Firebird Embedded)
