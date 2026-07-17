using System.ComponentModel;
using ModelContextProtocol.Server;
using HomeMemory.MCP.Db;

namespace HomeMemory.MCP.Tools;

[McpServerToolType]
public static class StatusTools
{

    private static string StatusTypeName(object? val) => Convert.ToInt32(val ?? 0) switch
    {
        0 => "Existing",
        1 => "Planned",
        2 => "Removed",
        _ => "Unknown"
    };

    // ── Tool: list_statuses ───────────────────────────────────────────────────

    [McpServerTool(Name = "list_statuses", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description(
        "Lists all available statuses with reference counts, grouped by status type. " +
        "Counts are shown separately for elements, connections, part types, and other records (when non-zero). " +
        "Status types: Existing (0), Planned (1), Removed (2). " +
        "Use status names from this list in create_element, update_element, create_connection, update_connection, find_element, and get_connections. " +
        "create/update tools require the exact status name (case-insensitive). " +
        "find_element and get_connections also accept partial name matches and language-independent type keywords (Existing/Planned/Removed); " +
        "the 'Existing' keyword additionally matches records with no status set. " +
        "If no suitable status exists, use create_status.")]
    public static string ListStatuses()
    {
        try
        {
            using var conn = FirebirdDb.OpenConnection();

            var rows = FirebirdDb.ExecuteQuery(conn, """
                SELECT s."Oid", s."Name", s."StatusType", s."Note",
                       COUNT(e."Oid")  AS ELEM_COUNT,
                       COUNT(cn."Oid") AS CONN_COUNT,
                       COUNT(pt."Oid") AS PT_COUNT,
                       COUNT(ce."Oid") AS CE_COUNT
                FROM "Status" s
                LEFT JOIN "CEntity"    ce ON ce."Status" = s."Oid"
                LEFT JOIN "Element"    e  ON e."Oid"     = ce."Oid"
                LEFT JOIN "Connection" cn ON cn."Oid"    = ce."Oid"
                LEFT JOIN "PartType"   pt ON pt."Oid"    = ce."Oid"
                GROUP BY s."Oid", s."Name", s."StatusType", s."Note"
                ORDER BY s."StatusType", s."Name"
                """);

            if (rows.Count == 0)
                return "No statuses found. Use create_status to add one.";

            var lines = new List<string> { $"Statuses ({rows.Count}):\n" };

            int? currentType = null;
            foreach (var row in rows)
            {
                var sType = row.Int("StatusType");
                if (sType != currentType)
                {
                    lines.Add($"\n  {StatusTypeName(sType)} (type {sType}):");
                    currentType = sType;
                }

                var name       = row.Str("Name");
                var note       = row.Str("Note");
                var elemCount  = row.Long("ELEM_COUNT");
                var connCount  = row.Long("CONN_COUNT");
                var ptCount    = row.Long("PT_COUNT");
                var ceCount    = row.Long("CE_COUNT");
                var otherCount = ceCount - elemCount - connCount - ptCount;

                var parts = new List<string>();
                if (elemCount  > 0) parts.Add($"{elemCount} elem.");
                if (connCount  > 0) parts.Add($"{connCount} conn.");
                if (ptCount    > 0) parts.Add($"{ptCount} part type{(ptCount == 1 ? "" : "s")}");
                if (otherCount > 0) parts.Add($"{otherCount} other");
                var detail  = parts.Count > 0 ? $"  ({string.Join(" / ", parts)})" : "";
                var noteStr = !string.IsNullOrEmpty(note) ? $"  – {note}" : "";
                lines.Add($"    - {name}{detail}{noteStr}");
            }

            lines.Add("\n  Use exact name (case-insensitive) in create/update_element and create/update_connection. find_element and get_connections also accept partial matches plus type keywords (Existing/Planned/Removed); 'Existing' additionally matches records with no status set.");
            return string.Join("\n", lines);
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    // ── Tool: update_status ───────────────────────────────────────────────────

    [McpServerTool(Name = "update_status", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false)]
    [Description(
        "Updates an existing status: rename, change type, or update the note. " +
        "Required: name (current name to find the status). " +
        "Optional: new_name, new_status_type ('existing', 'planned', or 'removed'), " +
        "note (CLEAR to remove). " +
        "At least one optional field must be provided. " +
        "Forbidden characters in new_name: $*[{}|\\<>?\"/;: and tab.")]
    public static string UpdateStatus(
        [Description("Current name of the status to update (case-insensitive).")] string name,
        [Description("New name (optional).")] string? new_name = null,
        [Description("New status type: 'existing', 'planned', or 'removed' (optional).")] string? new_status_type = null,
        [Description("New note (optional). Use 'CLEAR' to remove.")] string? note = null)
    {
        name = name?.Trim() ?? "";
        if (string.IsNullOrEmpty(name))
            return "Error: 'name' is required.";

        if (new_name == null && new_status_type == null && note == null)
            return "Error: provide at least one of new_name, new_status_type, note.";

        // Validate new_name
        if (new_name != null)
        {
            new_name = Validate.NormalizeSingleline(new_name)?.Trim();
            if (string.IsNullOrEmpty(new_name))
                return "Error: 'new_name' cannot be empty.";
            if (Validate.InvalidChars.IsMatch(new_name))
                return "Error: new_name contains invalid characters ($*[{}|\\<>?\"/;: or tab).";
        }

        // Parse new_status_type
        int statusTypeInt = -1;
        if (new_status_type != null)
        {
            statusTypeInt = new_status_type.Trim().ToLowerInvariant() switch
            {
                "installed" or "existing" or "0" => 0,
                "planned"   or "1"               => 1,
                "removed"   or "decommissioned"  or "2" => 2,
                _ => -1
            };
            if (statusTypeInt < 0)
                return "Error: 'new_status_type' must be 'existing', 'planned', or 'removed'.";
        }

        note = Validate.NormalizeClear(note);
        bool clearNote = note == "CLEAR";
        note = Validate.NormalizeSingleline(note);

        // Field length validation
        var lenErr = Validate.Length(new_name, "new_name", 100)
                  ?? Validate.Length(!clearNote ? note?.Trim() : null, "note", 200);
        if (lenErr != null) return lenErr;

        try
        {
            using var conn = FirebirdDb.OpenConnection();

            // Find the status by name
            var findRows = FirebirdDb.ExecuteQuery(conn,
                """SELECT "Oid", "Name", "StatusType", "Note" FROM "Status" WHERE UPPER("Name") = UPPER(?)""",
                name);
            if (findRows.Count == 0)
                return $"Error: status '{name}' not found. Call list_statuses to see available statuses.";

            var row         = findRows[0];
            var oid         = row.Str("Oid");
            var currentST   = row.Int("StatusType");
            var currentNote = row.Str("Note");

            // Effective values. Fall back to the stored name (not the case-insensitive lookup
            // input) so a note-only update never silently rewrites the canonical name casing.
            var effectiveName = new_name ?? row.Str("Name");
            var effectiveST   = new_status_type != null ? statusTypeInt : currentST;
            string? effectiveNote;
            if (clearNote)
                effectiveNote = null;
            else if (note != null)
                effectiveNote = note.Trim();
            else
                effectiveNote = string.IsNullOrEmpty(currentNote) ? null : currentNote;

            // Check name uniqueness (excluding self)
            if (new_name != null)
            {
                var existRows = FirebirdDb.ExecuteQuery(conn,
                    """SELECT COUNT(*) AS CNT FROM "Status" WHERE UPPER("Name") = UPPER(?) AND "Oid" <> ?""",
                    effectiveName, oid);
                if (FirebirdDb.CountResult(existRows) > 0)
                    return $"Error: a status named '{effectiveName}' already exists.";
            }

            return FirebirdDb.RunInTransaction(conn, txn =>
            {
                FirebirdDb.ExecuteNonQuery(conn, txn, """
                    UPDATE "Status"
                    SET "OptimisticLockField" = COALESCE("OptimisticLockField", 0) + 1,
                        "Name"               = ?,
                        "StatusType"         = ?,
                        "Note"               = ?
                    WHERE "Oid" = ?
                    """,
                    effectiveName,
                    effectiveST,
                    (object?)effectiveNote ?? DBNull.Value,
                    oid);

                var changes = new List<string>();
                if (new_name != null)        changes.Add($"name → '{effectiveName}'");
                if (new_status_type != null) changes.Add($"type → {StatusTypeName(effectiveST)}");
                if (note != null)            changes.Add(clearNote ? "note → (removed)" : "note updated");

                return $"✓ Status '{effectiveName}' updated: {string.Join(", ", changes)}.";
            });
        }
        catch (Exception ex)
        {
            return $"Error: failed to update status: {ex.Message}";
        }
    }

    // ── Tool: create_status ───────────────────────────────────────────────────

    [McpServerTool(Name = "create_status", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description(
        "Creates a new status value. " +
        "Required: name, status_type ('existing', 'planned', or 'removed'). " +
        "Optional: note (short description of what this status means). " +
        "Name must be globally unique. " +
        "Forbidden characters: $*[{}|\\<>?\"/;: and tab. " +
        "After creating, use the name in create_element or update_element.")]
    public static string CreateStatus(
        [Description("Status name, e.g. 'Existing', 'Under Construction', 'Decommissioned'")] string name,
        [Description("Status type: 'existing' (present in the building), 'planned' (not yet built), 'removed' (decommissioned)")] string status_type,
        [Description("Short note describing when to use this status (optional)")] string? note = null)
    {
        name        = Validate.NormalizeSingleline(name)?.Trim() ?? "";
        status_type = status_type?.Trim() ?? "";
        note        = Validate.NormalizeSingleline(note);

        if (string.IsNullOrEmpty(name))
            return "Error: 'name' is required.";
        if (Validate.InvalidChars.IsMatch(name))
            return "Error: name contains invalid characters ($*[{}|\\<>?\"/;: or tab).";
        // Field length validation
        var lenErr = Validate.Length(name, "name", 100)
                  ?? Validate.Length(note?.Trim(), "note", 200);
        if (lenErr != null) return lenErr;

        // StatusType values: 0=Existing, 1=Planned, 2=Removed
        int statusTypeInt = status_type.ToLowerInvariant() switch
        {
            "installed" or "existing" or "0" => 0,
            "planned"   or "1"               => 1,
            "removed"   or "decommissioned"  or "2" => 2,
            _ => -1
        };
        if (statusTypeInt < 0)
            return "Error: 'status_type' must be 'existing', 'planned', or 'removed'.";

        try
        {
            using var conn = FirebirdDb.OpenConnection();

            // Name must be globally unique
            var existRows = FirebirdDb.ExecuteQuery(conn,
                """SELECT COUNT(*) AS CNT FROM "Status" WHERE UPPER("Name") = UPPER(?)""", name);
            if (FirebirdDb.CountResult(existRows) > 0)
                return $"Error: a status named '{name}' already exists.";

            var oid = Guid.NewGuid().ToString("D");

            return FirebirdDb.RunInTransaction(conn, txn =>
            {
                FirebirdDb.ExecuteNonQuery(conn, txn, """
                    INSERT INTO "Status" ("Oid", "OptimisticLockField", "StatusType", "Name", "Note")
                    VALUES (?, 0, ?, ?, ?)
                    """,
                    oid, statusTypeInt, name,
                    (object?)note?.Trim() ?? DBNull.Value);

                return $"✓ Status '{name}' created (type: {StatusTypeName(statusTypeInt)}, OID: {oid}).";
            });
        }
        catch (Exception ex)
        {
            return $"Error: failed to create status: {ex.Message}";
        }
    }

    // ── Tool: delete_status ───────────────────────────────────────────────────

    [McpServerTool(Name = "delete_status", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false)]
    [Description(
        "Permanently deletes a status value. " +
        "Required: name (current status name, case-insensitive). " +
        "Blocked if any record (element, connection, part type, or other) references this status. " +
        "Treat this as a stop signal: report the references and ask for explicit confirmation before " +
        "reassigning or clearing the status on those records. Never modify the references on your own. " +
        "If the status name is not known, call list_statuses first. " +
        "Note: the three default statuses (Existing, Planned, Removed) should rarely be deleted.")]
    public static string DeleteStatus(
        [Description("Name of the status to delete (case-insensitive). Call list_statuses if unsure.")] string name)
    {
        name = name?.Trim() ?? "";
        if (string.IsNullOrEmpty(name))
            return "Error: 'name' is required.";

        try
        {
            using var conn  = FirebirdDb.OpenConnection();
            var findRows    = FirebirdDb.ExecuteQuery(conn,
                """SELECT "Oid" FROM "Status" WHERE UPPER("Name") = UPPER(?)""", name);
            if (findRows.Count == 0)
                return $"Error: status '{name}' not found. Call list_statuses to see available statuses.";

            var oid = findRows[0].Str("Oid");

            // Blocking check: CEntity references, split by subclass
            var usageRows  = FirebirdDb.ExecuteQuery(conn, """
                SELECT
                    COUNT(e."Oid")  AS ELEM_COUNT,
                    COUNT(cn."Oid") AS CONN_COUNT,
                    COUNT(pt."Oid") AS PT_COUNT,
                    COUNT(ce."Oid") AS CE_COUNT
                FROM "CEntity" ce
                LEFT JOIN "Element"    e  ON e."Oid"  = ce."Oid"
                LEFT JOIN "Connection" cn ON cn."Oid" = ce."Oid"
                LEFT JOIN "PartType"   pt ON pt."Oid" = ce."Oid"
                WHERE ce."Status" = ?
                """, oid);
            var elemCount  = usageRows[0].Long("ELEM_COUNT");
            var connCount  = usageRows[0].Long("CONN_COUNT");
            var ptCount    = usageRows[0].Long("PT_COUNT");
            var ceCount    = usageRows[0].Long("CE_COUNT");
            var otherCount = ceCount - elemCount - connCount - ptCount;

            if (ceCount > 0)
            {
                var parts = new List<string>();
                if (elemCount  > 0) parts.Add($"{elemCount} element{(elemCount == 1 ? "" : "s")}");
                if (connCount  > 0) parts.Add($"{connCount} connection{(connCount == 1 ? "" : "s")}");
                if (ptCount    > 0) parts.Add($"{ptCount} part type{(ptCount == 1 ? "" : "s")}");
                if (otherCount > 0) parts.Add($"{otherCount} other record{(otherCount == 1 ? "" : "s")}");
                var breakdown = string.Join(", ", parts);

                var hints = new List<string>();
                if (elemCount  > 0) hints.Add($"call find_element with status='{name}' to locate the elements");
                if (connCount  > 0) hints.Add($"call get_connections with status='{name}' to locate the connections");
                if (ptCount    > 0) hints.Add("part type status is not currently manageable via MCP");
                if (otherCount > 0) hints.Add("other referenced records are not currently manageable via MCP");

                return $"Error: status '{name}' is assigned to {ceCount} record{(ceCount == 1 ? "" : "s")} ({breakdown}). " +
                       string.Join("; ", hints) + ".";
            }

            return FirebirdDb.RunInTransaction(conn, txn =>
            {
                FirebirdDb.ExecuteNonQuery(conn, txn,
                    """DELETE FROM "Status" WHERE "Oid" = ?""", oid);

                return $"✓ Status '{name}' deleted.";
            });
        }
        catch (Exception ex)
        {
            return $"Error: failed to delete status: {ex.Message}";
        }
    }
}
