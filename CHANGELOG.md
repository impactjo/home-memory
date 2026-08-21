# Changelog

Notable changes per release. Download builds from the [releases page](https://github.com/impactjo/home-memory/releases).

## v0.5.0: Stable IDs and safer database handling (2026-08-21)

### Stable IDs and conflict-safe writes

Elements, connections, categories, and statuses now have stable IDs that stay the
same when a name or a path changes. An AI client can address a record by its ID
instead of its current name, and pass the version it read along with the change.

If the record changed in the meantime, Home Memory reports a conflict instead of
overwriting the newer data.

- The detail tools and `list_statuses` report the `oid` and a version number. The
  create tools return the `oid` of the new record.
- Every update, delete, and move tool accepts `oid` as an alternative to the name
  or path, plus an optional `expected_version`. When a name and an ID are given
  together, they must identify the same record.

### Breaking changes

- `get_structure_overview`: the parameter `structuralAreasOnly` is now called
  `primaryAreasOnly`. The old name is not rejected, it is ignored, so a client
  that still sends `structuralAreasOnly=false` quietly gets the primary-area view
  instead of the full content tree. Rename it wherever it is stored. The wording
  "primary area" is used consistently in tool descriptions and category output.
- HTTP mode requires `HOME_MEMORY_API_KEY` when `HOME_MEMORY_BIND` is not a
  loopback address. The server now refuses to start instead of printing a
  warning. Requests are additionally checked against the expected `Host`, and
  requests carrying an `Origin` header are rejected, because Home Memory has no
  browser client.
- `create_connection` and `update_connection` require `length` to be greater than
  zero, and parse it strictly. Use a decimal point, for example `4.5`. Thousands
  separators are no longer accepted.

### Safer database handling

Normal startup no longer changes the schema of an existing database. Recognized
Home Memory databases continue to open unchanged.

If Home Memory reports that a migration is required, stop every process using the
database and run:

```
HomeMemoryMCP.exe --migrate --yes
```

The command refuses to run while the database is in use and writes a verified
backup before it changes anything. The backup path and its checksum are printed.
Seed data is applied only to a database that Home Memory created itself.

### New

- `get_structure_overview` takes `metrics="counts"` and then appends per-node
  rollups `[b… c… x…]`: elements below the node, connections touching it, and
  connections crossing its boundary. Rollups always cover the full subtree,
  independent of `primaryAreasOnly`, `maxDepth`, and `under`.
- `list_categories` and `get_category_details` count items across all
  subcategories, matching what `get_by_category` and `get_connections` return.
  Where a count covers both, the directly classified share is shown as
  `; N direct`.
- The detail tools show who created a record and when, and the same for the last
  change. Records imported from other tools may not carry that data.
- All tools carry MCP behavior annotations, so clients can tell reads from writes
  without parsing descriptions.
- Writes report non-blocking advisories: an element whose status is in an earlier
  lifecycle phase than its parent's status, or a new connection that reuses an
  existing connection name.

### Improvements

- The stdio server stays available when another process holds the database at
  startup. The tools remain listed and the check is repeated on every tool call
  until it succeeds, so the client does not have to be restarted.
- Faster count rollups on larger structures.
- Database commands time out after 30 seconds, so a blocked query returns an
  error instead of leaving the client waiting.
- `get_structure_overview` no longer shows misleading paths when an area sits
  below an element that is not a primary area. The ancestors needed to reach it
  are marked with `[context]` and counted separately in the total line.
- Tool failures are returned as MCP errors instead of plain result text, so
  clients can tell a failed call from a successful one.
- Blocked deletions are described as stop signals throughout. Attached documents,
  child categories, category usage, and status references are reported for a
  decision instead of being cleared by the assistant.
- Startup errors, including permission and IO problems, print a clear message and
  exit with a non-zero exit code.
- Upgraded to `ModelContextProtocol` 1.4.1.

### Fixes

- Search terms containing `%` or `_` are matched literally in `find_element` and
  `get_connections`, across all searched fields.
- `get_by_category` with an unknown category returns an explicit error naming the
  category instead of reporting an empty result.
- `get_connections(under=...)` now includes connections whose source or
  destination is exactly that element, not only those below it.
- `maxDepth` is applied to the primary-area overview as well, and the returned
  title states the depth that was used.
- Renaming an element to a name it already has is no longer rejected as a
  conflict with itself.
- The check for attached documents queries the correct column, so deletions are
  blocked as documented.

### Updated

- Server instructions and tool descriptions were tightened: the model is stated
  once at the server level, and the details sit in the tool and parameter
  descriptions where they are read in context.
- Seed data: `Fire Detector` replaces `Smoke Detector`, a `Flue System` category
  was added under HVAC, cable and wastewater categories were refined, and seeded
  categories carry canonical paths and purposes. Seed data applies to newly
  created databases only.
- Setup guide: client registration examples and a section on updating the schema
  of an existing database.

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
