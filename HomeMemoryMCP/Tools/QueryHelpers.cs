using System.Diagnostics.CodeAnalysis;
using HomeMemory.MCP.Db;

namespace HomeMemory.MCP.Tools;

/// <summary>
/// Internal query helpers shared by ElementTools and ConnectionTools.
/// Not part of the public MCP tool API.
/// </summary>
internal static class QueryHelpers
{
    internal static (List<Row> all, Dictionary<string, Row> byOid, Dictionary<string, Row> byFullName)
        LoadEtree(FbConnection conn)
    {
        var all = FirebirdDb.ExecuteQuery(conn,
            $"{SqlQueries.EtreeCte} SELECT \"Oid\", FULLNAME, LONGNAME FROM ETREE");
        var byOid = all.ToDictionary(
            r => FirebirdDb.OidKey(r["Oid"]),
            r => r,
            StringComparer.OrdinalIgnoreCase);
        var byFullName = all.ToDictionary(
            r => r.Str("FULLNAME"),
            r => r,
            StringComparer.OrdinalIgnoreCase);
        // Add long-name paths as fallback for when a model passes full name instead of short name
        // (e.g. 'House/Ground Floor' instead of 'House/GF'). Only adds where not already present.
        foreach (var row in all)
        {
            var longName = row.Str("LONGNAME");
            if (!byFullName.ContainsKey(longName))
                byFullName[longName] = row;
        }
        return (all, byOid, byFullName);
    }

    internal static string NormalizePath(string path)
        => path.Trim().TrimEnd('/');

    /// <summary>
    /// Applies a CLEAR-aware text update to a single CEntity column inside an open transaction.
    /// No-op when <paramref name="value"/> is null (field not provided); "CLEAR" sets the column
    /// to NULL; any other value is trimmed and written.
    /// IMPORTANT: <paramref name="column"/> is interpolated into the SQL — pass only fixed,
    /// code-controlled column names (e.g. "Purpose"), never user input.
    /// </summary>
    internal static void SetCEntityField(
        FbConnection conn, FbTransaction txn, string oid, string column, string? value)
    {
        if (value == null) return;
        FirebirdDb.ExecuteNonQuery(conn, txn,
            $"""UPDATE "CEntity" SET "{column}" = ? WHERE "Oid" = ?""",
            value == "CLEAR" ? DBNull.Value : (object)value.Trim(), oid);
    }

    /// <summary>
    /// Formats CreatedOn/CreatedBy/UpdatedOn/UpdatedBy from <paramref name="row"/> into up to two
    /// audit lines ("Created" and "Updated"), padded to <paramref name="labelWidth"/> to match the
    /// surrounding field block. A line is omitted entirely when neither the timestamp nor the user
    /// is present. Treats null and DBNull the same, since some queries reach CreatedOn/CreatedBy via
    /// a LEFT JOIN where the CEntity row itself can be absent.
    /// </summary>
    internal static List<string> FormatAuditLines(Row row, int labelWidth)
    {
        var lines = new List<string>();
        AppendAuditLine(lines, "Created", row.GetValueOrDefault("CreatedOn"), row.Str("CreatedBy"), labelWidth);
        AppendAuditLine(lines, "Updated", row.GetValueOrDefault("UpdatedOn"), row.Str("UpdatedBy"), labelWidth);
        return lines;
    }

    private static void AppendAuditLine(List<string> lines, string verb, object? onValue, string byValue, int labelWidth)
    {
        bool hasOn = onValue is DateTime;
        bool hasBy = !string.IsNullOrEmpty(byValue);
        if (!hasOn && !hasBy) return;

        string text = hasOn && hasBy
            ? $"{(DateTime)onValue!:yyyy-MM-dd HH:mm} UTC by {byValue}"
            : hasOn
                ? $"{(DateTime)onValue!:yyyy-MM-dd HH:mm} UTC"
                : byValue;

        var label = hasOn ? verb : $"{verb} by";
        lines.Add($"  {label.PadRight(labelWidth)}: {text}");
    }

    /// <summary>
    /// Splits a full element path into (parent, name).
    /// The parent segment includes the trailing slash, e.g. "House/GF/" and "Kitchen".
    /// Returns ("", fullname) for top-level elements.
    /// </summary>
    internal static (string parent, string name) SplitParentAndName(string fullname)
    {
        int i = fullname.LastIndexOf('/');
        return i >= 0 ? (fullname[..(i + 1)], fullname[(i + 1)..]) : ("", fullname);
    }

    internal static bool TryResolveElementRow(
        Dictionary<string, Row> byFullName,
        string path,
        [NotNullWhen(true)] out Row? row,
        out string canonicalFullName)
    {
        row = null;
        canonicalFullName = "";
        if (string.IsNullOrWhiteSpace(path)) return false;
        if (!byFullName.TryGetValue(NormalizePath(path), out row)) return false;
        canonicalFullName = row.Str("FULLNAME");
        return true;
    }

    internal static (Row? row, string canonicalFullName, string? error) ResolveElementSelector(
        Dictionary<string, Row> byOid,
        Dictionary<string, Row> byFullName,
        string? fullname,
        string? oid)
    {
        fullname = string.IsNullOrWhiteSpace(fullname) ? null : NormalizePath(fullname);
        oid = string.IsNullOrWhiteSpace(oid) ? null : oid.Trim();
        if (fullname == null && oid == null)
            return (null, "", "Error: provide 'fullname' or 'oid'.");

        Row? pathRow = null;
        if (fullname != null && !TryResolveElementRow(byFullName, fullname, out pathRow, out _))
            return (null, "", $"Error: element '{fullname}' not found.");

        Row? oidRow = null;
        if (oid != null)
        {
            if (!Guid.TryParse(oid, out var parsedOid))
                return (null, "", "Error: 'oid' must be a valid GUID.");
            if (!byOid.TryGetValue(FirebirdDb.OidKey(parsedOid), out oidRow))
                return (null, "", $"Error: element OID '{parsedOid:D}' not found.");
        }

        if (pathRow != null && oidRow != null
            && !FirebirdDb.OidKey(pathRow["Oid"]).Equals(
                FirebirdDb.OidKey(oidRow["Oid"]), StringComparison.OrdinalIgnoreCase))
            return (null, "", "Error: 'fullname' and 'oid' identify different elements.");

        var row = oidRow ?? pathRow!;
        return (row, row.Str("FULLNAME"), null);
    }

    internal static (Row? row, string? error) ResolveConnectionSelector(
        FbConnection conn,
        Dictionary<string, Row> elementsByFullName,
        string? name,
        string? source,
        string? destination,
        string? oid)
    {
        name = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
        source = string.IsNullOrWhiteSpace(source) ? null : NormalizePath(source);
        destination = string.IsNullOrWhiteSpace(destination) ? null : NormalizePath(destination);
        oid = string.IsNullOrWhiteSpace(oid) ? null : oid.Trim();

        if (name == null && oid == null)
            return (null, "Error: provide 'name' or 'oid'.");

        string? sourceOid = null;
        if (source != null)
        {
            if (!TryResolveElementRow(elementsByFullName, source, out var sourceRow, out _))
                return (null, $"Error: source element '{source}' not found.");
            sourceOid = sourceRow.Str("Oid");
        }

        string? destinationOid = null;
        if (destination != null)
        {
            if (!TryResolveElementRow(elementsByFullName, destination, out var destinationRow, out _))
                return (null, $"Error: destination element '{destination}' not found.");
            destinationOid = destinationRow.Str("Oid");
        }

        if (oid != null)
        {
            if (!Guid.TryParse(oid, out var parsedOid))
                return (null, "Error: 'oid' must be a valid GUID.");

            var oidMatches = FirebirdDb.ExecuteQuery(conn, """
                SELECT "Oid", "Name", "Source", "Destination"
                FROM "Connection"
                WHERE "Oid" = ?
                """, parsedOid.ToString("D"));
            if (oidMatches.Count == 0)
                return (null, $"Error: connection OID '{parsedOid:D}' not found.");

            var oidRow = oidMatches[0];
            if (name != null && !oidRow.Str("Name").Equals(name, StringComparison.OrdinalIgnoreCase))
                return (null, "Error: 'name' and 'oid' identify different connections.");
            if (sourceOid != null && !FirebirdDb.OidKey(oidRow["Source"]).Equals(
                    FirebirdDb.OidKey(sourceOid), StringComparison.OrdinalIgnoreCase))
                return (null, "Error: 'source' does not match the connection identified by 'oid'.");
            if (destinationOid != null && !FirebirdDb.OidKey(oidRow["Destination"]).Equals(
                    FirebirdDb.OidKey(destinationOid), StringComparison.OrdinalIgnoreCase))
                return (null, "Error: 'destination' does not match the connection identified by 'oid'.");

            return (oidRow, null);
        }

        var sql = """SELECT "Oid", "Name", "Source", "Destination" FROM "Connection" WHERE UPPER("Name") = UPPER(?)""";
        var args = new List<object?> { name };
        if (sourceOid != null)
        {
            sql += """ AND "Source" = ?""";
            args.Add(sourceOid);
        }
        if (destinationOid != null)
        {
            sql += """ AND "Destination" = ?""";
            args.Add(destinationOid);
        }

        var matches = FirebirdDb.ExecuteQuery(conn, sql, args.ToArray());
        if (matches.Count == 0)
            return (null, $"Error: connection '{name}' not found. " +
                "Tip: provide source, destination, or oid to narrow the search.");
        if (matches.Count > 1)
            return (null, $"Error: {matches.Count} connections named '{name}' found. " +
                "Provide source, destination, or oid to narrow the search.");

        return (matches[0], null);
    }

    internal static string? ValidateExpectedVersion(int? expectedVersion)
        => expectedVersion < 0 ? "Error: 'expected_version' must be zero or greater." : null;

    internal static bool TouchCEntity(
        FbConnection conn,
        FbTransaction txn,
        string oid,
        DateTime updatedOn,
        string updatedBy,
        int? expectedVersion)
    {
        if (expectedVersion.HasValue)
        {
            return FirebirdDb.ExecuteNonQuery(conn, txn, """
                UPDATE "CEntity" SET
                    "OptimisticLockField" = COALESCE("OptimisticLockField", 0) + 1,
                    "UpdatedOn" = ?,
                    "UpdatedBy" = ?
                WHERE "Oid" = ?
                  AND COALESCE("OptimisticLockField", 0) = ?
                """, updatedOn, updatedBy, oid, expectedVersion.Value) == 1;
        }

        return FirebirdDb.ExecuteNonQuery(conn, txn, """
            UPDATE "CEntity" SET
                "OptimisticLockField" = COALESCE("OptimisticLockField", 0) + 1,
                "UpdatedOn" = ?,
                "UpdatedBy" = ?
            WHERE "Oid" = ?
            """, updatedOn, updatedBy, oid) == 1;
    }

    internal static string VersionConflict(string entityKind, int expectedVersion, string detailsTool)
        => $"Error: {entityKind} was modified since it was read (expected version {expectedVersion}). " +
           $"Call {detailsTool} again before retrying.";

    internal static bool HasExpectedVersion(
        FbConnection conn, string oid, int expectedVersion)
    {
        var rows = FirebirdDb.ExecuteQuery(conn, """
            SELECT COUNT(*) AS CNT
            FROM "CEntity"
            WHERE "Oid" = ? AND COALESCE("OptimisticLockField", 0) = ?
            """, oid, expectedVersion);
        return FirebirdDb.CountResult(rows) == 1;
    }

    /// <summary>
    /// Resolves an element path to its canonical short-name FULLNAME,
    /// accepting both short-name paths (e.g. 'House/GF') and long-name paths (e.g. 'House/Ground Floor').
    /// Returns the canonical FULLNAME on success, or null if the element is not found.
    /// </summary>
    internal static string? ResolveElementFullName(FbConnection conn, string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var normalized = NormalizePath(path);
        var rows = FirebirdDb.ExecuteQuery(conn, $"""
            {SqlQueries.EtreeCte}
            SELECT FIRST 1 FULLNAME FROM ETREE
            WHERE UPPER(FULLNAME) = UPPER(?) OR UPPER(LONGNAME) = UPPER(?)
            """, normalized, normalized);
        return rows.Count > 0 ? rows[0].Str("FULLNAME") : null;
    }

    /// <summary>
    /// Resolves a category name or path to its OID.
    /// Returns (Oid, null) on success, (null, null) when not found,
    /// or (null, errorMessage) when the name is ambiguous.
    /// Input with '/' is treated as a full category path (e.g. 'Electrical/Cable').
    /// Input without '/' is matched by Name or ShortName; if multiple categories share
    /// the same name the caller receives an error listing all full paths to disambiguate.
    /// </summary>
    internal static (string? Oid, string? Error) ResolveCategoryOid(FbConnection conn, string? category)
    {
        if (string.IsNullOrWhiteSpace(category)) return (null, null);
        var normalized = NormalizePath(category);

        if (normalized.Contains('/'))
        {
            // Contains slash → treat as full category path (e.g. 'Electrical/Cable')
            var pathRows = FirebirdDb.ExecuteQuery(conn, $"""
                {SqlQueries.CatCte}
                SELECT "Oid" FROM CAT_TREE WHERE UPPER(CAT_FULLNAME) = UPPER(?)
                """, normalized);
            return pathRows.Count > 0 ? (pathRows[0].Str("Oid"), null) : (null, null);
        }

        // No slash → resolve by Name or ShortName; use CTE to get full paths for ambiguity report
        var rows = FirebirdDb.ExecuteQuery(conn, $"""
            {SqlQueries.CatCte}
            SELECT "Oid", CAT_FULLNAME FROM CAT_TREE
            WHERE UPPER("Name") = UPPER(?) OR UPPER("ShortName") = UPPER(?)
            """, normalized, normalized);

        if (rows.Count == 0) return (null, null);
        if (rows.Count == 1) return (rows[0].Str("Oid"), null);

        // Multiple matches — return an error listing all full paths
        var paths = string.Join(", ", rows.Select(r => $"'{r.Str("CAT_FULLNAME")}'"));
        return (null, $"Error: category '{category.Trim()}' is ambiguous ({rows.Count} matches). " +
                      $"Use the full path instead: {paths}.");
    }

    internal sealed record CategoryResolution(HashSet<string> Oids, string? SingleMatchName);

    /// <summary>
    /// Resolves a category search term to OIDs of matching categories plus all their descendants.
    /// Priority: exact Name/ShortName match first; partial text match only as fallback.
    /// Path notation (with '/') = exact path match.
    /// Returns (null, null) if not found, (null, error) if ambiguous, (resolution, null) on success.
    /// </summary>
    internal static (CategoryResolution? Resolution, string? Error) ResolveCategoryOidsWithDescendants(FbConnection conn, string category)
    {
        category = NormalizePath(category);

        var cats = FirebirdDb.ExecuteQuery(conn, $"""
            {SqlQueries.CatCte}
            SELECT "Oid", CAT_FULLNAME, "Name", "ShortName" FROM CAT_TREE ORDER BY CAT_FULLNAME
            """);

        List<Row> matched;
        if (category.Contains('/'))
        {
            matched = cats.Where(c =>
                string.Equals(c.Str("CAT_FULLNAME"), category, StringComparison.OrdinalIgnoreCase)
            ).ToList();
        }
        else
        {
            var upper = category.ToUpperInvariant();
            matched = cats.Where(c =>
                string.Equals(c.Str("Name").ToUpperInvariant(), upper, StringComparison.Ordinal) ||
                string.Equals(c.Str("ShortName").ToUpperInvariant(), upper, StringComparison.Ordinal)
            ).ToList();

            if (matched.Count == 0)
            {
                matched = cats.Where(c =>
                    c.Str("Name").ToUpperInvariant().Contains(upper) ||
                    c.Str("ShortName").ToUpperInvariant().Contains(upper)
                ).ToList();
            }
        }

        if (matched.Count == 0) return (null, null);

        if (matched.Count > 1)
        {
            var paths = string.Join(", ", matched.Select(m => $"'{m.Str("CAT_FULLNAME")}'"));
            return (null, $"Error: category '{category.Trim()}' is ambiguous ({matched.Count} matches). " +
                          $"Use the full path instead: {paths}.");
        }

        var oids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var mFn    = matched[0].Str("CAT_FULLNAME");
        var prefix = mFn + "/";
        foreach (var c in cats)
        {
            var cFn = c.Str("CAT_FULLNAME");
            if (string.Equals(cFn, mFn, StringComparison.OrdinalIgnoreCase) ||
                cFn.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                oids.Add(FirebirdDb.OidKey(c["Oid"]));
        }

        return (new CategoryResolution(oids, matched[0].Str("Name")), null);
    }

    internal static string? ResolveStatusOid(FbConnection conn, string? status)
    {
        if (string.IsNullOrWhiteSpace(status)) return null;
        var rows = FirebirdDb.ExecuteQuery(conn,
            """SELECT "Oid" FROM "Status" WHERE UPPER("Name") = UPPER(?)""", status.Trim());
        return rows.Count > 0 ? rows[0].Str("Oid") : null;
    }

    /// <summary>
    /// Returns a non-blocking warning when an explicitly statused element is in an earlier
    /// lifecycle phase than its explicitly statused parent. A missing status on either side
    /// means "not specified" for validation and is intentionally skipped.
    /// </summary>
    internal static string? ElementParentStatusAdvisory(
        FbConnection conn, string? elementStatusOid, string? parentOid)
    {
        if (string.IsNullOrEmpty(elementStatusOid) || string.IsNullOrEmpty(parentOid))
            return null;

        var rows = FirebirdDb.ExecuteQuery(conn, """
            SELECT childStatus."Name" AS CHILD_NAME,
                   childStatus."StatusType" AS CHILD_TYPE,
                   parentStatus."Name" AS PARENT_NAME,
                   parentStatus."StatusType" AS PARENT_TYPE
            FROM "Status" childStatus
            JOIN "CEntity" parentEntity ON parentEntity."Oid" = ?
            JOIN "Status" parentStatus ON parentStatus."Oid" = parentEntity."Status"
            WHERE childStatus."Oid" = ?
              AND childStatus."StatusType" < parentStatus."StatusType"
            """, parentOid, elementStatusOid);

        if (rows.Count == 0) return null;
        var row = rows[0];
        return $"element status '{row.Str("CHILD_NAME")}' conflicts with parent status '{row.Str("PARENT_NAME")}'";
    }

    internal static int NextSortIndex(FbConnection conn, string? parentOid)
    {
        var sql = parentOid != null
            ? """SELECT COALESCE(MAX("SortIndex"), 0) AS MAXSI FROM "Element" WHERE "PartOfElement" = ?"""
            : """SELECT COALESCE(MAX("SortIndex"), 0) AS MAXSI FROM "Element" WHERE "PartOfElement" IS NULL""";

        var rows = parentOid != null
            ? FirebirdDb.ExecuteQuery(conn, sql, parentOid)
            : FirebirdDb.ExecuteQuery(conn, sql);

        return rows.Count > 0 && rows[0]["MAXSI"] is int maxSi ? maxSi + 1 : 1;
    }

    /// <summary>
    /// Ensures name and short name are unique within the same parent (siblings).
    /// Returns an error message or null if OK.
    /// excludeOid = OID of the element being edited (to exclude self from the uniqueness check).
    /// </summary>
    internal static string? CheckSiblingUniqueness(
        FbConnection conn, string name, string? shortName, string? parentOid, string? excludeOid = null)
    {
        var excludeClause = excludeOid != null ? """ AND "Oid" != ?""" : "";

        // Name must be unique within the same parent, including for top-level elements.
        string nameSql;
        List<object?> nameArgs;
        if (parentOid != null)
        {
            nameSql  = $"""SELECT COUNT(*) AS CNT FROM "Element" WHERE UPPER("Name") = UPPER(?) AND "PartOfElement" = ?{excludeClause}""";
            nameArgs = excludeOid != null
                ? new List<object?> { name, parentOid, excludeOid }
                : new List<object?> { name, parentOid };
        }
        else
        {
            nameSql  = $"""SELECT COUNT(*) AS CNT FROM "Element" WHERE UPPER("Name") = UPPER(?) AND "PartOfElement" IS NULL{excludeClause}""";
            nameArgs = excludeOid != null
                ? new List<object?> { name, excludeOid }
                : new List<object?> { name };
        }
        var nameRows  = FirebirdDb.ExecuteQuery(conn, nameSql, nameArgs.ToArray());
        if (FirebirdDb.CountResult(nameRows) > 0)
            return $"Error: a sibling element with name '{name}' already exists under the same parent.";

        // A non-empty short name must be unique within the same parent.
        if (!string.IsNullOrEmpty(shortName))
        {
            string snSql;
            List<object?> snArgs;
            if (parentOid != null)
            {
                snSql  = $"""SELECT COUNT(*) AS CNT FROM "Element" WHERE UPPER("ShortName") = UPPER(?) AND "PartOfElement" = ?{excludeClause}""";
                snArgs = excludeOid != null
                    ? new List<object?> { shortName, parentOid, excludeOid }
                    : new List<object?> { shortName, parentOid };
            }
            else
            {
                snSql  = $"""SELECT COUNT(*) AS CNT FROM "Element" WHERE UPPER("ShortName") = UPPER(?) AND "PartOfElement" IS NULL{excludeClause}""";
                snArgs = excludeOid != null
                    ? new List<object?> { shortName, excludeOid }
                    : new List<object?> { shortName };
            }
            var snRows = FirebirdDb.ExecuteQuery(conn, snSql, snArgs.ToArray());
            if (FirebirdDb.CountResult(snRows) > 0)
                return $"Error: a sibling element with short name '{shortName}' already exists under the same parent.";
        }
        return null;
    }

    /// <summary>
    /// Checks whether a document is attached to an entity (blocks deletion).
    /// Returns error message or null if OK.
    /// </summary>
    internal static string? CheckDocumentsAttached(FbConnection conn, string oid, string entityKind)
    {
        try
        {
            var rows = FirebirdDb.ExecuteQuery(conn,
                """SELECT COUNT(*) AS CNT FROM "Document" WHERE "Entity" = ?""", oid);
            var count = FirebirdDb.CountResult(rows);
            return count > 0
                ? $"Error: {entityKind} has {count} attached document(s). Remove them first."
                : null;
        }
        catch (FbException ex) when (ex.ErrorCode is 335544580 or 335544569)
        {
            // Table or column may not exist in all DB versions – skip silently
            return null;
        }
    }

    /// <summary>
    /// Collects advisory warnings for delete (non-blocking, shown as info after deletion).
    /// Returns list of advisory strings (empty = no warnings).
    /// </summary>
    internal static List<string> CollectDeleteAdvisories(
        FbConnection conn, string oid, string entityKind)
    {
        var advisories = new List<string>();

        // Warn when a description or user manual will be lost.
        try
        {
            var rows = FirebirdDb.ExecuteQuery(conn,
                """SELECT "Description", "UserManual" FROM "CEntity" WHERE "Oid" = ?""", oid);
            if (rows.Count > 0)
            {
                var desc = rows[0].Str("Description");
                var um   = rows[0].Str("UserManual");
                if (!string.IsNullOrEmpty(desc) || !string.IsNullOrEmpty(um))
                    advisories.Add($"{entityKind} had a description and/or user manual (data is lost)");
            }
        }
        catch { /* ignore */ }

        // Warn when image associations will be removed.
        // intermediate table: ImagesToCItems (FK: CItem → CItem.Oid, no CASCADE)
        try
        {
            var rows = FirebirdDb.ExecuteQuery(conn,
                """SELECT COUNT(*) AS CNT FROM "ImagesToCItems" WHERE "CItem" = ?""", oid);
            var count = FirebirdDb.CountResult(rows);
            if (count > 0)
                advisories.Add($"{count} image association(s) removed");
        }
        catch { /* skip if table does not exist in this DB version */ }

        return advisories;
    }

    /// <summary>
    /// Checks whether any text fields (description, note, purpose, user_manual) are about to be
    /// overwritten with different content. Returns advisory strings with a preview of the old content.
    /// Only reports fields where: old value was non-empty, new value is provided, new value != CLEAR,
    /// and new value differs from old value.
    /// </summary>
    internal static List<string> CollectOverwriteAdvisories(
        FbConnection conn, string oid,
        string? description = null, string? note = null,
        string? purpose = null, string? userManual = null)
    {
        var advisories = new List<string>();

        bool anyUpdate = IsTextUpdate(description) || IsTextUpdate(note)
                      || IsTextUpdate(purpose)     || IsTextUpdate(userManual);
        if (!anyUpdate) return advisories;

        var rows = FirebirdDb.ExecuteQuery(conn,
            """SELECT "Description", "Note", "Purpose", "UserManual" FROM "CEntity" WHERE "Oid" = ?""", oid);
        if (rows.Count == 0) return advisories;

        var old = rows[0];
        CheckOverwrite(advisories, "description",  old.Str("Description"), description);
        CheckOverwrite(advisories, "note",          old.Str("Note"),        note);
        CheckOverwrite(advisories, "purpose",       old.Str("Purpose"),     purpose);
        CheckOverwrite(advisories, "user_manual",   old.Str("UserManual"),  userManual);

        return advisories;
    }

    private static bool IsTextUpdate(string? value) => value != null && value != "CLEAR";

    private static void CheckOverwrite(List<string> advisories, string fieldName, string oldValue, string? newValue)
    {
        if (newValue == null || newValue == "CLEAR") return;
        if (string.IsNullOrEmpty(oldValue)) return;
        if (string.Equals(oldValue, newValue.Trim(), StringComparison.Ordinal)) return;

        var preview = oldValue.Replace("\r", "").Replace("\n", " \\n ");
        if (preview.Length > 80) preview = preview[..80] + "...";
        advisories.Add($"'{fieldName}' was overwritten (previous: {oldValue.Length} chars, started with: \"{preview}\")");
    }

    /// <summary>
    /// Ensures the combination of name, category, source, and destination is unique.
    /// Returns error message or null if OK.
    /// excludeOid = OID of the connection being updated (to exclude self).
    /// </summary>
    internal static string? CheckConnectionCombinationUniqueness(
        FbConnection conn, string name, string categoryOid, string srcOid, string dstOid,
        string? excludeOid = null)
    {
        var excludeClause = excludeOid != null ? """ AND c."Oid" != ?""" : "";
        var args = new List<object?> { name, categoryOid, srcOid, dstOid };
        if (excludeOid != null) args.Add(excludeOid);

        var rows = FirebirdDb.ExecuteQuery(conn, $"""
            SELECT COUNT(*) AS CNT
            FROM "Connection" c
            JOIN "CItem" ci ON ci."Oid" = c."Oid"
            WHERE UPPER(c."Name") = UPPER(?)
              AND ci."Category" = ?
              AND c."Source"      = ?
              AND c."Destination" = ?{excludeClause}
            """, args.ToArray());

        return FirebirdDb.CountResult(rows) > 0
            ? "Error: a connection with the same name, category, source, and destination already exists."
            : null;
    }

    /// <summary>
    /// Returns an info note if another connection with the same source, destination, and category already exists.
    /// Returns empty string if none.
    /// </summary>
    internal static string ConnectionSameSrcDstCategoryHint(
        FbConnection conn, string srcOid, string dstOid, string categoryOid, string? excludeOid = null)
    {
        var excludeClause = excludeOid != null ? """ AND c."Oid" != ?""" : "";
        var args = new List<object?> { srcOid, dstOid, categoryOid };
        if (excludeOid != null) args.Add(excludeOid);

        try
        {
            var rows = FirebirdDb.ExecuteQuery(conn, $"""
                SELECT COUNT(*) AS CNT
                FROM "Connection" c
                JOIN "CItem" ci ON ci."Oid" = c."Oid"
                WHERE c."Source"      = ?
                  AND c."Destination" = ?
                  AND ci."Category"   = ?{excludeClause}
                """, args.ToArray());
            var count = FirebirdDb.CountResult(rows);
            return count > 0
                ? $"\n  Note: {count} other connection(s) with the same source, destination, and category already exist."
                : "";
        }
        catch { return ""; }
    }

    /// <summary>
    /// Returns a warning when another connection uses the same name. The desktop model treats
    /// this as a warning independently of the blocking name/category/source/destination rule.
    /// </summary>
    internal static string ConnectionSameNameAdvisory(
        FbConnection conn, string name, string? excludeOid = null)
    {
        var excludeClause = excludeOid != null ? """ AND "Oid" != ?""" : "";
        var args = excludeOid != null
            ? new object?[] { name, excludeOid }
            : new object?[] { name };
        var rows = FirebirdDb.ExecuteQuery(conn,
            $"""SELECT COUNT(*) AS CNT FROM "Connection" WHERE UPPER("Name") = UPPER(?){excludeClause}""",
            args);
        var count = FirebirdDb.CountResult(rows);
        return count > 0
            ? $"\n  Advisory: {count} other connection(s) with the same name already exist."
            : "";
    }
}
