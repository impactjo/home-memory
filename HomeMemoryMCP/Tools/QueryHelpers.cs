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
            r => FirebirdDb.Str(r["FULLNAME"]),
            r => r,
            StringComparer.OrdinalIgnoreCase);
        // Add long-name paths as fallback for when a model passes full name instead of short name
        // (e.g. 'House/Ground Floor' instead of 'House/GF'). Only adds where not already present.
        foreach (var row in all)
        {
            var longName = FirebirdDb.Str(row["LONGNAME"]);
            if (!byFullName.ContainsKey(longName))
                byFullName[longName] = row;
        }
        return (all, byOid, byFullName);
    }

    internal static string NormalizePath(string path)
        => path.Trim().TrimEnd('/');

    internal static bool TryResolveElementRow(
        FbConnection conn,
        Dictionary<string, Row> byFullName,
        string path,
        [NotNullWhen(true)] out Row? row,
        out string canonicalFullName)
    {
        row = null;
        canonicalFullName = "";

        var resolved = ResolveElementFullName(conn, path);
        if (resolved is null)
            return false;

        canonicalFullName = resolved;
        return byFullName.TryGetValue(resolved, out row);
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
        return rows.Count > 0 ? FirebirdDb.Str(rows[0]["FULLNAME"]) : null;
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
        var upper = NormalizePath(category).ToUpper();

        if (upper.Contains('/'))
        {
            // Contains slash → treat as full category path (e.g. 'Electrical/Cable')
            var pathRows = FirebirdDb.ExecuteQuery(conn, $"""
                {SqlQueries.CatCte}
                SELECT "Oid" FROM CAT_TREE WHERE UPPER(CAT_FULLNAME) = ?
                """, upper);
            return pathRows.Count > 0 ? (FirebirdDb.Str(pathRows[0]["Oid"]), null) : (null, null);
        }

        // No slash → resolve by Name or ShortName; use CTE to get full paths for ambiguity report
        var rows = FirebirdDb.ExecuteQuery(conn, $"""
            {SqlQueries.CatCte}
            SELECT "Oid", CAT_FULLNAME FROM CAT_TREE
            WHERE UPPER("Name") = ? OR UPPER("ShortName") = ?
            """, upper, upper);

        if (rows.Count == 0) return (null, null);
        if (rows.Count == 1) return (FirebirdDb.Str(rows[0]["Oid"]), null);

        // Multiple matches — return an error listing all full paths
        var paths = string.Join(", ", rows.Select(r => $"'{FirebirdDb.Str(r["CAT_FULLNAME"])}'"));
        return (null, $"Error: category '{category.Trim()}' is ambiguous ({rows.Count} matches). " +
                      $"Use the full path instead: {paths}.");
    }

    internal static string? ResolveStatusOid(FbConnection conn, string? status)
    {
        if (string.IsNullOrWhiteSpace(status)) return null;
        var rows = FirebirdDb.ExecuteQuery(conn,
            """SELECT "Oid" FROM "Status" WHERE UPPER("Name") = UPPER(?)""", status.Trim());
        return rows.Count > 0 ? FirebirdDb.Str(rows[0]["Oid"]) : null;
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

        // -- Name + PartOfElement must be unique (SkipNullOrEmptyValues = false) --
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
        var nameCount = nameRows.Count > 0 ? Convert.ToInt64(nameRows[0].GetValueOrDefault("CNT") ?? 0L) : 0L;
        if (nameCount > 0)
            return $"Error: a sibling element with name '{name}' already exists under the same parent.";

        // -- ShortName + PartOfElement must be unique (SkipNullOrEmptyValues = true) --
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
            var snRows  = FirebirdDb.ExecuteQuery(conn, snSql, snArgs.ToArray());
            var snCount = snRows.Count > 0 ? Convert.ToInt64(snRows[0].GetValueOrDefault("CNT") ?? 0L) : 0L;
            if (snCount > 0)
                return $"Error: a sibling element with short name '{shortName}' already exists under the same parent.";
        }
        return null;
    }

    /// <summary>
    /// Checks whether a document is attached to the element/connection (blocks deletion).
    /// Returns error message or null if OK.
    /// </summary>
    internal static string? CheckDocumentsAttached(FbConnection conn, string oid)
    {
        try
        {
            var rows  = FirebirdDb.ExecuteQuery(conn,
                """SELECT COUNT(*) AS CNT FROM "Document" WHERE "ConstructionEntity" = ?""", oid);
            var count = rows.Count > 0 ? Convert.ToInt64(rows[0].GetValueOrDefault("CNT") ?? 0L) : 0L;
            return count > 0
                ? $"Error: element has {count} attached document(s). Remove them first."
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
    internal static List<string> CollectDeleteAdvisories(FbConnection conn, string oid)
    {
        var advisories = new List<string>();

        // ConstructionEntity_DeleteWarningIfDescriptionOrUserManualExists
        try
        {
            var rows = FirebirdDb.ExecuteQuery(conn,
                """SELECT "Description", "UserManual" FROM "CEntity" WHERE "Oid" = ?""", oid);
            if (rows.Count > 0)
            {
                var desc = FirebirdDb.Str(rows[0].GetValueOrDefault("Description"));
                var um   = FirebirdDb.Str(rows[0].GetValueOrDefault("UserManual"));
                if (!string.IsNullOrEmpty(desc) || !string.IsNullOrEmpty(um))
                    advisories.Add("element had a description and/or user manual (data is lost)");
            }
        }
        catch { /* ignore */ }

        // ConstructionItem_DeleteWarningIfImagesAssignedExists
        // intermediate table: ImagesToCItems (FK: CItem → CItem.Oid, no CASCADE)
        try
        {
            var rows = FirebirdDb.ExecuteQuery(conn,
                """SELECT COUNT(*) AS CNT FROM "ImagesToCItems" WHERE "CItem" = ?""", oid);
            var count = rows.Count > 0 ? Convert.ToInt64(rows[0].GetValueOrDefault("CNT") ?? 0L) : 0L;
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
        CheckOverwrite(advisories, "description",  FirebirdDb.Str(old.GetValueOrDefault("Description")), description);
        CheckOverwrite(advisories, "note",          FirebirdDb.Str(old.GetValueOrDefault("Note")),        note);
        CheckOverwrite(advisories, "purpose",       FirebirdDb.Str(old.GetValueOrDefault("Purpose")),     purpose);
        CheckOverwrite(advisories, "user_manual",   FirebirdDb.Str(old.GetValueOrDefault("UserManual")),  userManual);

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

        var count = rows.Count > 0 ? Convert.ToInt64(rows[0].GetValueOrDefault("CNT") ?? 0L) : 0L;
        return count > 0
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
            var count = rows.Count > 0 ? Convert.ToInt64(rows[0].GetValueOrDefault("CNT") ?? 0L) : 0L;
            return count > 0
                ? $"\n  Note: {count} other connection(s) with the same source, destination, and category already exist."
                : "";
        }
        catch { return ""; }
    }
}
