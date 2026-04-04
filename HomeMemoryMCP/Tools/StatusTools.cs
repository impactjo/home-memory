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

    [McpServerTool(Name = "list_statuses")]
    [Description(
        "Lists all available statuses with element counts, grouped by status type. " +
        "Status types: Existing (0), Planned (1), Removed (2). " +
        "Use status names from this list in create_element, update_element, and find_element. " +
        "create_element and update_element require the exact status name (case-insensitive). " +
        "find_element also accepts partial name matches. " +
        "If no suitable status exists, use create_status.")]
    public static string ListStatuses()
    {
        try
        {
            using var conn = FirebirdDb.OpenConnection();

            var rows = FirebirdDb.ExecuteQuery(conn, """
                SELECT s."Oid", s."Name", s."StatusType", s."Note",
                       COUNT(e."Oid") AS ELEM_COUNT
                FROM "Status" s
                LEFT JOIN "CEntity" ce ON ce."Status" = s."Oid"
                LEFT JOIN "Element"  e  ON e."Oid"    = ce."Oid"
                GROUP BY s."Oid", s."Name", s."StatusType", s."Note"
                ORDER BY s."StatusType", s."Name"
                """);

            if (rows.Count == 0)
                return "No statuses found. Use create_status to add one.";

            var lines = new List<string> { $"Statuses ({rows.Count}):\n" };

            int? currentType = null;
            foreach (var row in rows)
            {
                var sType = Convert.ToInt32(row.GetValueOrDefault("StatusType") ?? 0);
                if (sType != currentType)
                {
                    lines.Add($"\n  {StatusTypeName(sType)} (type {sType}):");
                    currentType = sType;
                }

                var name      = row.Str("Name");
                var note      = row.Str("Note");
                var elemCount = Convert.ToInt64(row.GetValueOrDefault("ELEM_COUNT") ?? 0L);

                var detail = elemCount > 0 ? $"  ({elemCount} elem.)" : "";
                var noteStr = !string.IsNullOrEmpty(note) ? $"  – {note}" : "";
                lines.Add($"    - {name}{detail}{noteStr}");
            }

            lines.Add("\n  Use exact name (case-insensitive) in create_element / update_element. find_element also accepts partial matches.");
            return string.Join("\n", lines);
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    // ── Tool: update_status ───────────────────────────────────────────────────

    [McpServerTool(Name = "update_status")]
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

        bool clearNote = note?.Trim().Equals("CLEAR", StringComparison.OrdinalIgnoreCase) == true;

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
            var currentST   = Convert.ToInt32(row.GetValueOrDefault("StatusType") ?? 0);
            var currentNote = row.Str("Note");

            // Effective values
            var effectiveName = new_name ?? name;
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

            using var txn = conn.BeginTransaction();
            try
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

                txn.Commit();

                var changes = new List<string>();
                if (new_name != null)        changes.Add($"name → '{effectiveName}'");
                if (new_status_type != null) changes.Add($"type → {StatusTypeName(effectiveST)}");
                if (note != null)            changes.Add(clearNote ? "note → (removed)" : "note updated");

                return $"✓ Status '{name}' updated: {string.Join(", ", changes)}.";
            }
            catch
            {
                txn.Rollback();
                throw;
            }
        }
        catch (Exception ex)
        {
            return $"Error updating status: {ex.Message}";
        }
    }

    // ── Tool: create_status ───────────────────────────────────────────────────

    [McpServerTool(Name = "create_status")]
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

            using var txn = conn.BeginTransaction();
            try
            {
                FirebirdDb.ExecuteNonQuery(conn, txn, """
                    INSERT INTO "Status" ("Oid", "OptimisticLockField", "StatusType", "Name", "Note")
                    VALUES (?, 0, ?, ?, ?)
                    """,
                    oid, statusTypeInt, name,
                    (object?)note?.Trim() ?? DBNull.Value);

                txn.Commit();
                return $"✓ Status '{name}' created (type: {StatusTypeName(statusTypeInt)}, OID: {oid}).";
            }
            catch
            {
                txn.Rollback();
                throw;
            }
        }
        catch (Exception ex)
        {
            return $"Error creating status: {ex.Message}";
        }
    }

    // ── Tool: delete_status ───────────────────────────────────────────────────

    [McpServerTool(Name = "delete_status")]
    [Description(
        "Permanently deletes a status value. " +
        "Required: name (current status name, case-insensitive). " +
        "Blocked if any element references this status – reassign or remove their status first " +
        "(call find_element with that status name to locate them). " +
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

            // Blocking check: CEntity references
            var usageRows  = FirebirdDb.ExecuteQuery(conn,
                """SELECT COUNT(*) AS CNT FROM "CEntity" WHERE "Status" = ?""", oid);
            var usageCount = FirebirdDb.CountResult(usageRows);
            if (usageCount > 0)
                return $"Error: status '{name}' is assigned to {usageCount} element{(usageCount == 1 ? "" : "s")}. " +
                       $"Reassign or remove their status first (call find_element with status='{name}' to locate them).";

            using var txn = conn.BeginTransaction();
            try
            {
                FirebirdDb.ExecuteNonQuery(conn, txn,
                    """DELETE FROM "Status" WHERE "Oid" = ?""", oid);

                txn.Commit();
                return $"✓ Status '{name}' deleted.";
            }
            catch
            {
                txn.Rollback();
                throw;
            }
        }
        catch (Exception ex)
        {
            return $"Error deleting status: {ex.Message}";
        }
    }
}
