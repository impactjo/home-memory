using System.ComponentModel;
using ModelContextProtocol.Server;
using HomeMemory.MCP.Db;

namespace HomeMemory.MCP.Tools;

[McpServerToolType]
public static class CategoryTools
{

    // ── Shared Helper ─────────────────────────────────────────────────────────

    private static List<Row> LoadCatTree(FbConnection conn)
    {
        var sql = $"""
            {SqlQueries.CatCte}
            SELECT ct."Oid", ct.CAT_FULLNAME, ct."Name", ct."ShortName",
                   ct.CAT_DEPTH, ct."IsAreaCategory", ct."ParentCategory",
                   COUNT(e."Oid")    AS ELEM_COUNT,
                   COUNT(conn."Oid") AS CONN_COUNT,
                   COUNT(pt."Oid")   AS PT_COUNT,
                   COUNT(ci."Oid")   AS CI_COUNT,
                   ce."Purpose", ce."Note", ce."Description", ce."UserManual"
            FROM CAT_TREE ct
            LEFT JOIN "CItem"      ci   ON ci."Category" = ct."Oid"
            LEFT JOIN "Element"    e    ON e."Oid"    = ci."Oid"
            LEFT JOIN "Connection" conn ON conn."Oid" = ci."Oid"
            LEFT JOIN "PartType"   pt   ON pt."Oid"  = ci."Oid"
            LEFT JOIN "CEntity"    ce   ON ce."Oid"  = ct."Oid"
            GROUP BY ct."Oid", ct.CAT_FULLNAME, ct."Name", ct."ShortName",
                     ct.CAT_DEPTH, ct."IsAreaCategory", ct."ParentCategory",
                     ce."Purpose", ce."Note", ce."Description", ce."UserManual"
            ORDER BY ct.CAT_FULLNAME
            """;
        return FirebirdDb.ExecuteQuery(conn, sql);
    }

    /// <summary>
    /// Rolls up the direct per-category counts from <see cref="LoadCatTree"/> over each category's
    /// subtree (self + descendant categories), keyed by <see cref="FirebirdDb.OidKey"/>. Because every
    /// item has exactly one category, a subtree sum involves no double counting. These totals match
    /// what get_by_category (elements) and get_connections (connections) return for the category, since
    /// both resolve descendant categories too.
    /// </summary>
    private static Dictionary<string, (long elem, long conn, long pt, long other)> ComputeCategoryRollups(List<Row> rows)
    {
        var direct   = new Dictionary<string, (long elem, long conn, long pt, long other)>(StringComparer.OrdinalIgnoreCase);
        var children = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var allKeys  = new List<string>(rows.Count);
        foreach (var r in rows)
        {
            var oid = FirebirdDb.OidKey(r["Oid"]);
            allKeys.Add(oid);
            long e  = r.Long("ELEM_COUNT");
            long c  = r.Long("CONN_COUNT");
            long pt = r.Long("PT_COUNT");
            long ci = r.Long("CI_COUNT");
            direct[oid] = (e, c, pt, ci - e - c - pt);
            var parent = r.GetValueOrDefault("ParentCategory");
            if (parent is not null and not DBNull)
            {
                var pk = FirebirdDb.OidKey(parent);
                if (!children.TryGetValue(pk, out var list)) children[pk] = list = new();
                list.Add(oid);
            }
        }

        var totals = new Dictionary<string, (long elem, long conn, long pt, long other)>(StringComparer.OrdinalIgnoreCase);
        (long elem, long conn, long pt, long other) Build(string node)
        {
            if (totals.TryGetValue(node, out var cached)) return cached;
            var sum = direct.GetValueOrDefault(node);
            totals[node] = sum; // store before recursion so malformed cyclic data terminates
            if (children.TryGetValue(node, out var kids))
                foreach (var k in kids)
                {
                    var ck = Build(k);
                    sum = (sum.elem + ck.elem, sum.conn + ck.conn, sum.pt + ck.pt, sum.other + ck.other);
                }
            totals[node] = sum;
            return sum;
        }
        foreach (var k in allKeys) Build(k);
        return totals;
    }

    // ── Tools ─────────────────────────────────────────────────────────────────

    [McpServerTool(Name = "list_categories", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description(
        "Lists all object categories with item counts (elements, connections, part types, and other items, shown when non-zero). " +
        "Counts cover the category and its subcategories, matching what get_by_category and get_connections return; " +
        "when a count includes both direct and subcategory items, the directly-classified share is also shown as '; N direct'. " +
        "Categories classify both elements (physical items: equipment, furniture, fixtures) " +
        "and connections (physical lines: pipes, cables, ducts). " +
        "IMPORTANT: Call this before create_element or create_connection to pick the right category. " +
        "If no suitable category exists, use create_category first. " +
        "Primary area categories (Building, Floor, Room, ...) are marked with [primary area]. " +
        "When a category has a short name, its path segment uses that short name " +
        "(e.g. short name 'PV' → path 'PV', not 'Photovoltaics'). " +
        "The exact path to use in update_category and delete_category is shown as [path: ...] " +
        "next to each category that has a short name.")]
    public static string ListCategories()
    {
        try
        {
            using var conn = FirebirdDb.OpenConnection();
            var rows = LoadCatTree(conn);
            var rollups = ComputeCategoryRollups(rows);

            var lines = new List<string> { "Object categories:\n" };
            foreach (var r in rows)
            {
                int depth  = r.Int("CAT_DEPTH");
                var indent = new string(' ', depth * 2);
                var icon   = depth > 0 ? "+-" : "-";
                var name   = r.Str("Name");
                var sn     = r.Str("ShortName");
                var label  = !string.IsNullOrEmpty(sn) && sn != name ? $"{name} ({sn})" : name;
                bool isPrimaryArea = FirebirdDb.IsTrue(r.GetValueOrDefault("IsAreaCategory"));
                // Counts cover the whole subtree (category + subcategories), matching what
                // get_by_category / get_connections return. When a parent also has items of its own,
                // the directly-classified share is appended so the headline never understates a parent.
                long de  = r.Long("ELEM_COUNT");
                long dc  = r.Long("CONN_COUNT");
                long dpt = r.Long("PT_COUNT");
                long dci = r.Long("CI_COUNT");
                long dot = dci - de - dc - dpt;
                var t = rollups[FirebirdDb.OidKey(r["Oid"])];
                var parts = new List<string>();
                if (t.elem  > 0) parts.Add($"{t.elem} elem.");
                if (t.conn  > 0) parts.Add($"{t.conn} conn.");
                if (t.pt    > 0) parts.Add($"{t.pt} part type{(t.pt == 1 ? "" : "s")}");
                if (t.other > 0) parts.Add($"{t.other} other");
                // "direct" qualifier only for buckets genuinely split (some here, more below).
                var directParts = new List<string>();
                if (de  > 0 && de  < t.elem)  directParts.Add($"{de} elem.");
                if (dc  > 0 && dc  < t.conn)  directParts.Add($"{dc} conn.");
                if (dpt > 0 && dpt < t.pt)    directParts.Add($"{dpt} part type{(dpt == 1 ? "" : "s")}");
                if (dot > 0 && dot < t.other) directParts.Add($"{dot} other");
                var directStr = directParts.Count > 0 ? $"; {string.Join(", ", directParts)} direct" : "";
                var countStr = parts.Count > 0 ? $"  ({string.Join(", ", parts)}{directStr})" : "";
                var flagParts = new List<string>();
                if (!string.IsNullOrEmpty(r.Str("Purpose")))     flagParts.Add("p");
                if (!string.IsNullOrEmpty(r.Str("Note")))        flagParts.Add("n");
                if (!string.IsNullOrEmpty(r.Str("Description"))) flagParts.Add("i");
                if (!string.IsNullOrEmpty(r.Str("UserManual")))  flagParts.Add("u");
                var flags = flagParts.Count > 0 ? $"  [{string.Join("", flagParts)}]" : "";
                var areaFlag = isPrimaryArea ? "  [primary area]" : "";
                // Show explicit path when ShortName changes the path segment (prevents copy-paste errors)
                var catFullname = r.Str("CAT_FULLNAME");
                var pathHint = !string.IsNullOrEmpty(sn) && sn != name
                    ? $"  [path: {catFullname}]"
                    : "";
                lines.Add($"  {indent}{icon} {label}{areaFlag}{pathHint}{countStr}{flags}");
            }
            lines.Add("\n  [p] = purpose, [n] = note, [i] = description, [u] = user manual — call get_category_details for content");
            return string.Join("\n", lines);
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    [McpServerTool(Name = "get_by_category", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description(
        "All elements of an object category/trade, grouped by location. " +
        "Examples: get_by_category('Socket') → all sockets in the building; " +
        "get_by_category('Lighting', under='House/GF') → all lights on the ground floor. " +
        "Category name: exact Name/ShortName match wins; partial text only as fallback when no exact match exists. " +
        "If the search term matches multiple categories, an error lists all full paths — use a full path (with '/') to disambiguate. " +
        "With '/' the full category path must match exactly (e.g. 'Electrical/Cable'). " +
        "Available categories: list_categories. " +
        "Elements with status Planned or Removed are marked with their status name.")]
    public static string GetByCategory(
        [Description("Category name or partial text, e.g. 'Socket', 'Pipe', 'Network'.")] string category,
        [Description("Spatial filter: only elements below this path, e.g. 'House/GF'.")] string under = "")
    {
        category = category.Trim().TrimEnd('/');
        under    = under.Trim().TrimEnd('/');

        if (string.IsNullOrEmpty(category))
            return "Error: 'category' is required (e.g. 'Socket'). Call list_categories for available categories.";

        try
        {
            using var conn = FirebirdDb.OpenConnection();

            var (resolution, resolveError) = QueryHelpers.ResolveCategoryOidsWithDescendants(conn, category);
            if (resolveError is not null) return resolveError;
            if (resolution is null)
                return $"Error: category '{category}' not found. Call list_categories for available categories.";

            var includedOids = resolution.Oids;

            var paramList = new List<object?>();
            var underClause = "";
            if (!string.IsNullOrEmpty(under))
            {
                var resolvedUnder = QueryHelpers.ResolveElementFullName(conn, under);
                if (resolvedUnder is null)
                    return $"Error: element '{under}' not found. Call get_structure_overview or find_element to find the correct path.";

                underClause = "AND UPPER(et.FULLNAME) LIKE UPPER(?) ESCAPE '\\'";
                paramList.Add(FirebirdDb.EscapeLike(resolvedUnder) + "/%");
            }

            var allRows = FirebirdDb.ExecuteQuery(conn, $"""
                {SqlQueries.EtreeCte}
                SELECT et.FULLNAME, et."Name", et."Position",
                       cat."Name" AS CATNAME, ci."Category" AS CAT_OID,
                       s."Name" AS STATUSNAME, s."StatusType" AS STATUSTYPE
                FROM ETREE et
                JOIN "CItem"    ci  ON ci."Oid"  = et."Oid"
                JOIN "Category" cat ON cat."Oid" = ci."Category"
                LEFT JOIN "CEntity" ce ON ce."Oid" = et."Oid"
                LEFT JOIN "Status"  s  ON s."Oid"  = ce."Status"
                WHERE 1=1 {underClause}
                ORDER BY et.FULLNAME
                """, paramList.ToArray());

            var rows = allRows
                .Where(r => includedOids.Contains(FirebirdDb.OidKey(r.GetValueOrDefault("CAT_OID"))))
                .ToList();

            var scope = !string.IsNullOrEmpty(under) ? $" under '{under}'" : "";
            if (rows.Count == 0)
                return $"No elements of category '{category}'{scope} found.\nTip: call list_categories for available categories.";

            var catLabel = resolution.SingleMatchName ?? $"'{category}'";
            bool multiCat = includedOids.Count > 1;
            var lines = new List<string> { $"{catLabel} elements{scope} ({rows.Count}):\n" };

            string? currentParent = null;
            foreach (var row in rows)
            {
                var fullname          = row.Str("FULLNAME");
                var (parent, name)    = QueryHelpers.SplitParentAndName(fullname);

                if (parent != currentParent)
                {
                    lines.Add(!string.IsNullOrEmpty(parent) ? $"  {parent}" : "");
                    currentParent = parent;
                }

                var indent  = !string.IsNullOrEmpty(parent) ? "    " : "  ";
                var pos     = row.Str("Position");
                var catHint = multiCat ? $" [{row.Str("CATNAME")}]" : "";
                var posHint = !string.IsNullOrEmpty(pos) ? $"  [{pos}]" : "";
                var st      = row.GetValueOrDefault("STATUSTYPE");
                var stHint  = st is not null and not DBNull && Convert.ToInt32(st) is 1 or 2
                    ? $"  {{{row.Str("STATUSNAME")}}}"
                    : "";
                lines.Add($"{indent}{name}{catHint}{posHint}{stHint}");
            }
            return string.Join("\n", lines);
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    // ── Tool: get_category_details ────────────────────────────────────────────

    [McpServerTool(Name = "get_category_details", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description(
        "Full details of a single object category: path, parent, primary area flag, " +
        "direct and subtree item counts (elements, connections, part types, other; subtree spans subcategories at any depth), " +
        "purpose, note, description, and user manual. " +
        "Identify by full path (e.g. 'Electrical/Cable') or by name/short name when unambiguous. " +
        "update_category requires calling this first before modifying purpose, note, description, or user_manual. " +
        "Returns creation metadata and, when available, last-update metadata; records imported from external tools may lack this data.")]
    public static string GetCategoryDetails(
        [Description("Full category path (e.g. 'Electrical/Cable'), name, or short name. Use full path to disambiguate.")] string category)
    {
        category = category?.Trim() ?? "";
        if (string.IsNullOrEmpty(category))
            return "Error: 'category' is required.";

        try
        {
            using var conn = FirebirdDb.OpenConnection();

            var (catOid, catError) = QueryHelpers.ResolveCategoryOid(conn, category);
            if (catError != null) return catError;
            if (catOid == null)
                return $"Error: category '{category}' not found. Call list_categories to see available categories.";

            var rows = FirebirdDb.ExecuteQuery(conn, $"""
                {SqlQueries.CatCte}
                SELECT ct.CAT_FULLNAME, c."Name", c."ShortName",
                       c."IsAreaCategory", c."ParentCategory",
                       ce."Purpose", ce."Note", ce."Description", ce."UserManual",
                       ce."CreatedOn", ce."CreatedBy", ce."UpdatedOn", ce."UpdatedBy"
                FROM "Category" c
                JOIN CAT_TREE ct ON ct."Oid" = c."Oid"
                LEFT JOIN "CEntity" ce ON ce."Oid" = c."Oid"
                WHERE c."Oid" = ?
                """, catOid);

            if (rows.Count == 0)
                return $"Error: category '{category}' not found.";

            var r = rows[0];
            var fullPath = r.Str("CAT_FULLNAME");
            var name     = r.Str("Name");
            var sn       = r.Str("ShortName");
            var isArea   = FirebirdDb.IsTrue(r.GetValueOrDefault("IsAreaCategory"));

            string? parentPath = null;
            var parentOidObj = r.GetValueOrDefault("ParentCategory");
            if (parentOidObj is not null and not DBNull)
            {
                var parentRows = FirebirdDb.ExecuteQuery(conn, $"""
                    {SqlQueries.CatCte}
                    SELECT CAT_FULLNAME FROM CAT_TREE WHERE "Oid" = ?
                    """, parentOidObj);
                if (parentRows.Count > 0) parentPath = parentRows[0].Str("CAT_FULLNAME");
            }

            var usageRows = FirebirdDb.ExecuteQuery(conn, """
                SELECT
                    COUNT(e."Oid")  AS ELEM_COUNT,
                    COUNT(cn."Oid") AS CONN_COUNT,
                    COUNT(pt."Oid") AS PT_COUNT,
                    COUNT(ci."Oid") AS CI_COUNT
                FROM "CItem" ci
                LEFT JOIN "Element"    e  ON e."Oid"  = ci."Oid"
                LEFT JOIN "Connection" cn ON cn."Oid" = ci."Oid"
                LEFT JOIN "PartType"   pt ON pt."Oid" = ci."Oid"
                WHERE ci."Category" = ?
                """, catOid);
            var elemCount  = usageRows[0].Long("ELEM_COUNT");
            var connCount  = usageRows[0].Long("CONN_COUNT");
            var ptCount    = usageRows[0].Long("PT_COUNT");
            var ciCount    = usageRows[0].Long("CI_COUNT");
            var otherCount = ciCount - elemCount - connCount - ptCount;

            // Descendant categories (whole subtree, excluding self). The subtree usage below spans all
            // of them, not just direct children, so the suffix must count descendants — otherwise a
            // Parent -> Child -> Grandchild tree would total the grandchild yet say "1 child category".
            var descRows = FirebirdDb.ExecuteQuery(conn, """
                WITH RECURSIVE SUBCAT AS (
                    SELECT "Oid" FROM "Category" WHERE "Oid" = ?
                    UNION ALL
                    SELECT c."Oid" FROM "Category" c JOIN SUBCAT s ON c."ParentCategory" = s."Oid"
                )
                SELECT COUNT(*) - 1 AS CNT FROM SUBCAT
                """, catOid);
            var descendantCount = FirebirdDb.CountResult(descRows);

            // Subtree usage: this category plus all descendant categories — matches what
            // get_by_category / get_connections return (both resolve descendants).
            var subtreeRows = FirebirdDb.ExecuteQuery(conn, """
                WITH RECURSIVE SUBCAT AS (
                    SELECT "Oid" FROM "Category" WHERE "Oid" = ?
                    UNION ALL
                    SELECT c."Oid" FROM "Category" c JOIN SUBCAT s ON c."ParentCategory" = s."Oid"
                )
                SELECT
                    COUNT(e."Oid")  AS ELEM_COUNT,
                    COUNT(cn."Oid") AS CONN_COUNT,
                    COUNT(pt."Oid") AS PT_COUNT,
                    COUNT(ci."Oid") AS CI_COUNT
                FROM SUBCAT
                JOIN "CItem" ci ON ci."Category" = SUBCAT."Oid"
                LEFT JOIN "Element"    e  ON e."Oid"  = ci."Oid"
                LEFT JOIN "Connection" cn ON cn."Oid" = ci."Oid"
                LEFT JOIN "PartType"   pt ON pt."Oid" = ci."Oid"
                """, catOid);
            var subElem  = subtreeRows[0].Long("ELEM_COUNT");
            var subConn  = subtreeRows[0].Long("CONN_COUNT");
            var subPt    = subtreeRows[0].Long("PT_COUNT");
            var subCi    = subtreeRows[0].Long("CI_COUNT");
            var subOther = subCi - subElem - subConn - subPt;

            var lines = new List<string> { $"Category: {fullPath}\n" };
            lines.Add($"  Name             : {name}");
            if (!string.IsNullOrEmpty(sn) && sn != name)
                lines.Add($"  Short name       : {sn}");
            lines.Add($"  Parent           : {parentPath ?? "(top-level)"}");
            lines.Add($"  Primary area     : {(isArea ? "yes" : "no")}");

            static string UsageParts(long el, long cn, long p, long ot)
            {
                var parts = new List<string>();
                if (el > 0) parts.Add($"{el} element{(el == 1 ? "" : "s")}");
                if (cn > 0) parts.Add($"{cn} connection{(cn == 1 ? "" : "s")}");
                if (p  > 0) parts.Add($"{p} part type{(p == 1 ? "" : "s")}");
                if (ot > 0) parts.Add($"{ot} other");
                return parts.Count > 0 ? string.Join(", ", parts) : "none";
            }
            lines.Add($"  Direct usage     : {UsageParts(elemCount, connCount, ptCount, otherCount)}");
            var subtreeSuffix = descendantCount > 0
                ? $"  (across {descendantCount} subcategor{(descendantCount == 1 ? "y" : "ies")})"
                : "";
            lines.Add($"  Subtree usage    : {UsageParts(subElem, subConn, subPt, subOther)}{subtreeSuffix}");

            if (!string.IsNullOrEmpty(r.Str("Purpose")))
                lines.Add($"  Purpose          : {r.Str("Purpose")}");
            if (!string.IsNullOrEmpty(r.Str("Note")))
                lines.Add($"  Note             : {r.Str("Note")}");
            if (!string.IsNullOrEmpty(r.Str("Description")))
                lines.Add($"  Description      : {r.Str("Description")}");
            if (!string.IsNullOrEmpty(r.Str("UserManual")))
                lines.Add($"  User manual      : {r.Str("UserManual")}");

            lines.AddRange(QueryHelpers.FormatAuditLines(r, 17));

            return string.Join("\n", lines);
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    // ── Tool: update_category ─────────────────────────────────────────────────

    [McpServerTool(Name = "update_category", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false)]
    [Description(
        "Updates an existing object category: rename, change short name, description, primary area flag, or move to a different parent. " +
        "Required: category (current full path, e.g. 'Electrical/Lighting'). " +
        "Optional: new_name, new_short_name (CLEAR to remove), purpose (CLEAR to remove), note (CLEAR to remove), " +
        "description (CLEAR to remove), user_manual (CLEAR to remove), " +
        "is_primary_area ('true' or 'false'), " +
        "new_parent (full category path, e.g. 'Electrical'; CLEAR to move to top-level). " +
        "At least one optional field must be provided. " +
        "IMPORTANT: ALWAYS call get_category_details before updating purpose, note, description, or user_manual. " +
        "If the field already has content, inform the user and ask whether to replace or extend. " +
        "Field choice: short temporary to-do -> note (single line, 200 chars); permanent technical info -> description (multiline, 4000 chars); end-user/documentation guide -> user_manual (multiline, 4000 chars). " +
        "Forbidden characters in name/short_name: $*[{}]|\\<>?\"/;: and tab. " +
        "Note: renaming/moving a category automatically updates the full path of all subcategories " +
        "(FullName is computed dynamically – no stored paths need to be migrated).")]
    public static string UpdateCategory(
        [Description("Current full path of the category to update, e.g. 'Electrical/Lighting'.")] string category,
        [Description("New name (optional).")] string? new_name = null,
        [Description("New short name (optional). Use 'CLEAR' to remove.")] string? new_short_name = null,
        [Description("Intended use / explanation of what this category is for ('CLEAR' to remove). Only fill with information the user explicitly provided.")] string? purpose = null,
        [Description("Short temporary planning note ('CLEAR' to remove). For permanent technical info use description instead.")] string? note = null,
        [Description("Permanent technical info about this category itself, e.g. trade scope, conventions, references (optional, multiline, 4000 chars). Category-level only – not for individual items in this category. Use 'CLEAR' to remove.")] string? description = null,
        [Description("Documentation guide for this category, e.g. 'how to document items of this trade' ('CLEAR' to remove).")] string? user_manual = null,
        [Description("Set to true to mark as a primary area, false to unmark. Primary areas are the main location containers shown in the default structure overview (e.g. Building, Floor, Room, Garage, Outdoor Area); surface/detail zones like Wall Area or Ceiling Area are not primary areas.")] bool? is_primary_area = null,
        [Description("New parent category full path (optional). Use 'CLEAR' to move to top-level.")] string? new_parent = null)
    {
        category = category?.Trim() ?? "";
        if (string.IsNullOrEmpty(category))
            return "Error: 'category' is required.";

        if (new_name == null && new_short_name == null && purpose == null && note == null
            && description == null && user_manual == null
            && is_primary_area == null && new_parent == null)
            return "Error: provide at least one of new_name, new_short_name, purpose, note, description, user_manual, is_primary_area, new_parent.";

        // Validate new_name
        if (new_name != null)
        {
            new_name = Validate.NormalizeSingleline(new_name)?.Trim();
            if (string.IsNullOrEmpty(new_name))
                return "Error: 'new_name' cannot be empty.";
            if (Validate.InvalidChars.IsMatch(new_name))
                return "Error: new_name contains invalid characters ($*[{}]|\\<>?\"/;: or tab).";
        }

        // Validate new_short_name
        new_short_name = Validate.NormalizeClear(new_short_name);
        if (new_short_name != null && new_short_name != "CLEAR")
        {
            new_short_name = Validate.NormalizeSingleline(new_short_name)!.Trim();
            if (string.IsNullOrEmpty(new_short_name))
                return "Error: 'new_short_name' cannot be empty – use 'CLEAR' to remove it.";
            if (Validate.InvalidChars.IsMatch(new_short_name))
                return "Error: new_short_name contains invalid characters ($*[{}]|\\<>?\"/;: or tab).";
        }

        purpose     = Validate.NormalizeClear(purpose);
        note        = Validate.NormalizeClear(note);
        description = Validate.NormalizeClear(description);
        user_manual = Validate.NormalizeClear(user_manual);
        new_parent  = Validate.NormalizeClear(new_parent);
        bool clearParent = new_parent == "CLEAR";

        purpose     = Validate.NormalizeSingleline(purpose);
        note        = Validate.NormalizeSingleline(note);
        description = Validate.NormalizeMultiline(description);
        user_manual = Validate.NormalizeMultiline(user_manual);

        // Field length validation
        var lenErr = Validate.Length(new_name, "new_name", 100)
                  ?? Validate.Length(new_short_name != "CLEAR" ? new_short_name : null, "new_short_name", 50)
                  ?? Validate.Length(purpose     is not null and not "CLEAR" ? purpose.Trim()     : null, "purpose", 200)
                  ?? Validate.Length(note        is not null and not "CLEAR" ? note.Trim()        : null, "note", 200, "For permanent or longer information, use description instead.")
                  ?? Validate.Length(description is not null and not "CLEAR" ? description.Trim() : null, "description", 4000)
                  ?? Validate.Length(user_manual is not null and not "CLEAR" ? user_manual.Trim() : null, "user_manual", 4000);
        if (lenErr != null) return lenErr;

        try
        {
            using var conn  = FirebirdDb.OpenConnection();
            var allCats     = LoadCatTree(conn);
            var byFullName  = allCats.ToDictionary(
                r => r.Str("CAT_FULLNAME"),
                r => r,
                StringComparer.OrdinalIgnoreCase);

            // Find the category to update
            category = QueryHelpers.NormalizePath(category);
            if (!byFullName.TryGetValue(category, out var catRow))
                return $"Error: category '{category}' not found. Call list_categories to see available categories.";

            var oid         = catRow.Str("Oid");
            var canonicalCategory = catRow.Str("CAT_FULLNAME");
            var currentName = catRow.Str("Name");
            var currentSN   = catRow.Str("ShortName");

            // Load current ParentCategory OID from DB
            var parentRows      = FirebirdDb.ExecuteQuery(conn,
                """SELECT "ParentCategory" FROM "Category" WHERE "Oid" = ?""", oid);
            var currentParentStr = parentRows.Count > 0
                ? parentRows[0].Str("ParentCategory")
                : null;
            var currentParentOid = string.IsNullOrEmpty(currentParentStr) ? null : currentParentStr;

            // Determine effective values
            var effectiveName = new_name ?? currentName;

            string? effectiveSN;
            if (new_short_name == "CLEAR")
                effectiveSN = null;
            else if (new_short_name != null)
                effectiveSN = new_short_name;
            else
                effectiveSN = string.IsNullOrEmpty(currentSN) ? null : currentSN;

            // Determine effective parent OID
            string? effectiveParentOid;
            if (clearParent)
            {
                effectiveParentOid = null;
            }
            else if (new_parent != null)
            {
                new_parent = QueryHelpers.NormalizePath(new_parent);
                if (!byFullName.TryGetValue(new_parent, out var newParentRow))
                    return $"Error: new parent category '{new_parent}' not found. Call list_categories to see available categories.";

                // Circular reference check: new parent must not be self or a descendant of self
                var newParentFullName = newParentRow.Str("CAT_FULLNAME");
                if (string.Equals(newParentFullName, category, StringComparison.OrdinalIgnoreCase))
                    return "Error: a category cannot be its own parent.";
                if (newParentFullName.StartsWith(category + "/", StringComparison.OrdinalIgnoreCase))
                    return "Error: circular reference – the new parent is a descendant of this category.";

                effectiveParentOid = newParentRow.Str("Oid");
            }
            else
            {
                effectiveParentOid = currentParentOid;
            }

            // Check Name + effectiveParent uniqueness (excluding self)
            var nameCheckSql = effectiveParentOid != null
                ? """SELECT COUNT(*) AS CNT FROM "Category" WHERE UPPER("Name") = UPPER(?) AND "ParentCategory" = ? AND "Oid" <> ?"""
                : """SELECT COUNT(*) AS CNT FROM "Category" WHERE UPPER("Name") = UPPER(?) AND "ParentCategory" IS NULL AND "Oid" <> ?""";
            var nameArgs = effectiveParentOid != null
                ? new object?[] { effectiveName, effectiveParentOid, oid }
                : new object?[] { effectiveName, oid };
            var nameRows = FirebirdDb.ExecuteQuery(conn, nameCheckSql, nameArgs);
            if (FirebirdDb.CountResult(nameRows) > 0)
                return $"Error: a category named '{effectiveName}' already exists under the same parent.";

            // Check ShortName + effectiveParent uniqueness (if non-null, excluding self)
            if (!string.IsNullOrEmpty(effectiveSN))
            {
                var snCheckSql = effectiveParentOid != null
                    ? """SELECT COUNT(*) AS CNT FROM "Category" WHERE UPPER("ShortName") = UPPER(?) AND "ParentCategory" = ? AND "Oid" <> ?"""
                    : """SELECT COUNT(*) AS CNT FROM "Category" WHERE UPPER("ShortName") = UPPER(?) AND "ParentCategory" IS NULL AND "Oid" <> ?""";
                var snArgs = effectiveParentOid != null
                    ? new object?[] { effectiveSN, effectiveParentOid, oid }
                    : new object?[] { effectiveSN, oid };
                var snRows = FirebirdDb.ExecuteQuery(conn, snCheckSql, snArgs);
                if (FirebirdDb.CountResult(snRows) > 0)
                    return $"Error: a category with short name '{effectiveSN}' already exists under the same parent.";
            }

            // Check effective path segment uniqueness (cross-collision: Name vs sibling ShortName and vice versa).
            // CAT_FULLNAME uses COALESCE(NULLIF(TRIM(ShortName),''), Name) as the path segment.
            // The Name and ShortName checks above are field-isolated; this catches the cross-field case.
            var effectiveSegment = string.IsNullOrEmpty(effectiveSN) ? effectiveName : effectiveSN;
            var segCheckSql = effectiveParentOid != null
                ? """SELECT COUNT(*) AS CNT FROM "Category" WHERE UPPER(COALESCE(NULLIF(TRIM("ShortName"),''), "Name")) = UPPER(?) AND "ParentCategory" = ? AND "Oid" <> ?"""
                : """SELECT COUNT(*) AS CNT FROM "Category" WHERE UPPER(COALESCE(NULLIF(TRIM("ShortName"),''), "Name")) = UPPER(?) AND "ParentCategory" IS NULL AND "Oid" <> ?""";
            var segArgs = effectiveParentOid != null
                ? new object?[] { effectiveSegment, effectiveParentOid, oid }
                : new object?[] { effectiveSegment, oid };
            var segRows = FirebirdDb.ExecuteQuery(conn, segCheckSql, segArgs);
            if (FirebirdDb.CountResult(segRows) > 0)
                return $"Error: another category with path segment '{effectiveSegment}' already exists under the same parent (would create a duplicate category path).";

            var overwriteAdvisories = QueryHelpers.CollectOverwriteAdvisories(
                conn, oid, description, note, purpose, user_manual);

            var now = DateTime.UtcNow;
            var newIsArea = is_primary_area;
            return FirebirdDb.RunInTransaction(conn, txn =>
            {
                FirebirdDb.ExecuteNonQuery(conn, txn, """
                    UPDATE "CEntity" SET
                        "OptimisticLockField" = COALESCE("OptimisticLockField", 0) + 1,
                        "UpdatedOn" = ?,
                        "UpdatedBy" = ?
                    WHERE "Oid" = ?
                    """, now, "HomeMemory", oid);

                FirebirdDb.ExecuteNonQuery(conn, txn, """
                    UPDATE "Category"
                    SET "Name"           = ?,
                        "ShortName"      = ?,
                        "ParentCategory" = ?
                    WHERE "Oid" = ?
                    """,
                    effectiveName,
                    (object?)effectiveSN       ?? DBNull.Value,
                    (object?)effectiveParentOid ?? DBNull.Value,
                    oid);

                QueryHelpers.SetCEntityField(conn, txn, oid, "Purpose",     purpose);
                QueryHelpers.SetCEntityField(conn, txn, oid, "Note",        note);
                QueryHelpers.SetCEntityField(conn, txn, oid, "Description", description);
                QueryHelpers.SetCEntityField(conn, txn, oid, "UserManual",  user_manual);

                if (newIsArea.HasValue)
                {
                    FirebirdDb.ExecuteNonQuery(conn, txn, """
                        UPDATE "Category"
                        SET "IsAreaCategory" = ?
                        WHERE "Oid" = ?
                        """,
                        newIsArea.Value,
                        oid);
                }

                var changes = new List<string>();
                if (new_name != null)       changes.Add($"name → '{effectiveName}'");
                if (new_short_name != null) changes.Add(new_short_name == "CLEAR" ? "short_name → (removed)" : $"short_name → '{effectiveSN}'");
                if (purpose != null)        changes.Add(purpose == "CLEAR" ? "purpose → (removed)" : "purpose updated");
                if (note != null)           changes.Add(note == "CLEAR" ? "note → (removed)" : "note updated");
                if (description != null)    changes.Add(description == "CLEAR" ? "description → (removed)" : "description updated");
                if (user_manual != null)    changes.Add(user_manual == "CLEAR" ? "user_manual → (removed)" : "user_manual updated");
                if (newIsArea.HasValue)     changes.Add($"is_primary_area → {newIsArea.Value.ToString().ToLower()}");
                if (clearParent || (new_parent != null && new_parent != "CLEAR"))
                    changes.Add(effectiveParentOid == null ? "parent → (top-level)" : $"parent → '{new_parent}'");

                var result = $"✓ Category '{canonicalCategory}' updated: {string.Join(", ", changes)}.";
                foreach (var adv in overwriteAdvisories)
                    result += $"\n  Advisory: {adv}.";
                return result;
            });
        }
        catch (Exception ex)
        {
            return $"Error: failed to update category: {ex.Message}";
        }
    }

    // ── Tool: create_category ──────────────────────────────────────────────────

    [McpServerTool(Name = "create_category", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description(
        "Creates a new object category. Use this when no suitable category exists for a new element or connection. " +
        "Required: name. Optional: parent (full category path, e.g. 'Electrical'), " +
        "short_name, purpose, note, description, user_manual, " +
        "is_primary_area (default false – primary areas are the main location containers " +
        "shown in the default structure overview, like Building/Floor/Room, not trades or surface zones). " +
        "Field choice: short temporary to-do -> note (single line, 200 chars); permanent technical info -> description (multiline, 4000 chars); documentation guide for this category -> user_manual (multiline, 4000 chars). " +
        "Forbidden characters in name/short_name: $*[{}]|\\<>?\"/;: and tab. " +
        "After creating, use the returned category path in create_element or create_connection.")]
    public static string CreateCategory(
        [Description("Category name, e.g. 'Pool Technology' or 'Solar'")] string name,
        [Description("Full path of the parent category, e.g. 'Heating' or 'Electrical/Low-Voltage'. Empty = top-level.")] string? parent = null,
        [Description("Short name (optional), used as path segment, e.g. 'Pool'")] string? short_name = null,
        [Description("Intended use / explanation of what this category is for (optional). Only fill with information the user explicitly provided.")] string? purpose = null,
        [Description("Short temporary planning note (optional). For permanent technical info use description instead. Only fill with information the user explicitly provided.")] string? note = null,
        [Description("Permanent technical info about this category itself, e.g. trade scope, conventions, references (optional, multiline, 4000 chars). Category-level only – not for individual items in this category. Only fill with information the user explicitly provided.")] string? description = null,
        [Description("Documentation guide for this category, e.g. 'how to document items of this trade' (optional). Only fill with information the user explicitly provided.")] string? user_manual = null,
        [Description("Set to true for primary area categories (Building, Floor, Room, Garage, Outdoor Area) shown in the default structure overview. Omit or set to false (default) for trade/device categories or surface/detail zones (Wall Area, Ceiling Area).")] bool? is_primary_area = null)
    {
        name = Validate.NormalizeSingleline(name)?.Trim() ?? "";
        if (string.IsNullOrEmpty(name))
            return "Error: 'name' is required.";
        if (Validate.InvalidChars.IsMatch(name))
            return "Error: name contains invalid characters ($*[{}]|\\<>?\"/;: or tab).";

        short_name = string.IsNullOrWhiteSpace(short_name) ? null : Validate.NormalizeSingleline(short_name)?.Trim();
        if (short_name != null && Validate.InvalidChars.IsMatch(short_name))
            return "Error: short_name contains invalid characters ($*[{}]|\\<>?\"/;: or tab).";

        purpose     = Validate.NormalizeSingleline(purpose);
        note        = Validate.NormalizeSingleline(note);
        description = Validate.NormalizeMultiline(description);
        user_manual = Validate.NormalizeMultiline(user_manual);

        // Field length validation
        var lenErr = Validate.Length(name, "name", 100)
                  ?? Validate.Length(short_name, "short_name", 50)
                  ?? Validate.Length(purpose?.Trim(), "purpose", 200)
                  ?? Validate.Length(note?.Trim(), "note", 200, "For permanent or longer information, use description instead.")
                  ?? Validate.Length(description?.Trim(), "description", 4000)
                  ?? Validate.Length(user_manual?.Trim(), "user_manual", 4000);
        if (lenErr != null) return lenErr;

        var isArea = is_primary_area ?? false;

        try
        {
            using var conn = FirebirdDb.OpenConnection();

            // Load all categories to resolve parent and check uniqueness
            var allCats = LoadCatTree(conn);
            var byFullName = allCats.ToDictionary(
                r => r.Str("CAT_FULLNAME"),
                r => r,
                StringComparer.OrdinalIgnoreCase);

            // Resolve parent category
            string? parentOid      = null;
            string? parentFullName = null;
            if (!string.IsNullOrWhiteSpace(parent))
            {
                parent = QueryHelpers.NormalizePath(parent);
                if (!byFullName.TryGetValue(parent, out var parentRow))
                    return $"Error: parent category '{parent}' not found. Call list_categories to see available categories.";
                parentOid      = parentRow.Str("Oid");
                parentFullName = parent;
            }

            // Compute new full name and check uniqueness
            var segment     = string.IsNullOrEmpty(short_name) ? name : short_name;
            var newFullName = parentFullName != null ? $"{parentFullName}/{segment}" : segment;
            if (byFullName.ContainsKey(newFullName))
                return $"Error: a category with full name '{newFullName}' already exists.";

            // Check name uniqueness within the parent category.
            var nameCheckSql = parentOid != null
                ? """SELECT COUNT(*) AS CNT FROM "Category" WHERE UPPER("Name") = UPPER(?) AND "ParentCategory" = ?"""
                : """SELECT COUNT(*) AS CNT FROM "Category" WHERE UPPER("Name") = UPPER(?) AND "ParentCategory" IS NULL""";
            var nameArgs = parentOid != null
                ? new object?[] { name, parentOid }
                : new object?[] { name };
            var nameRows = FirebirdDb.ExecuteQuery(conn, nameCheckSql, nameArgs);
            if (FirebirdDb.CountResult(nameRows) > 0)
                return $"Error: a category named '{name}' already exists under the same parent.";

            // Check ShortName + ParentCategory uniqueness (skip if empty)
            if (!string.IsNullOrEmpty(short_name))
            {
                var snCheckSql = parentOid != null
                    ? """SELECT COUNT(*) AS CNT FROM "Category" WHERE UPPER("ShortName") = UPPER(?) AND "ParentCategory" = ?"""
                    : """SELECT COUNT(*) AS CNT FROM "Category" WHERE UPPER("ShortName") = UPPER(?) AND "ParentCategory" IS NULL""";
                var snArgs = parentOid != null
                    ? new object?[] { short_name, parentOid }
                    : new object?[] { short_name };
                var snRows = FirebirdDb.ExecuteQuery(conn, snCheckSql, snArgs);
                if (FirebirdDb.CountResult(snRows) > 0)
                    return $"Error: a category with short name '{short_name}' already exists under the same parent.";
            }

            var oid = Guid.NewGuid().ToString("D");
            var now = DateTime.UtcNow;

            return FirebirdDb.RunInTransaction(conn, txn =>
            {
                // CEntity: base row (no CItem/Part for categories)
                FirebirdDb.ExecuteNonQuery(conn, txn, """
                    INSERT INTO "CEntity" ("Oid", "OptimisticLockField", "ObjectType", "CreatedOn", "CreatedBy",
                                          "Purpose", "Note", "Description", "UserManual")
                    VALUES (?, 0, ?, ?, ?, ?, ?, ?, ?)
                    """,
                    oid, XPObjectTypes.Category, now, "HomeMemory",
                    (object?)purpose?.Trim()     ?? DBNull.Value,
                    (object?)note?.Trim()        ?? DBNull.Value,
                    (object?)description?.Trim() ?? DBNull.Value,
                    (object?)user_manual?.Trim() ?? DBNull.Value);

                // Category: the actual category row
                FirebirdDb.ExecuteNonQuery(conn, txn, """
                    INSERT INTO "Category" ("Oid", "Name", "ShortName", "IsAreaCategory", "ParentCategory")
                    VALUES (?, ?, ?, ?, ?)
                    """,
                    oid, name,
                    (object?)short_name ?? DBNull.Value,
                    isArea,
                    (object?)parentOid  ?? DBNull.Value);

                return $"✓ Category '{newFullName}' created (OID: {oid}).";
            });
        }
        catch (Exception ex)
        {
            return $"Error: failed to create category: {ex.Message}";
        }
    }

    // ── Tool: delete_category ──────────────────────────────────────────────────

    [McpServerTool(Name = "delete_category", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false)]
    [Description(
        "Permanently deletes an empty, unused category. " +
        "Required: category (full path, e.g. 'Electrical/Lighting'). " +
        "Blocked if: (1) the category has child categories – ask the user how to handle them first; " +
        "(2) any element, connection, or part type references this category – " +
        "ask the user how to handle them first (use get_by_category for elements, get_connections for connections; part type references are not directly discoverable via MCP). " +
        "When deletion fails for either reason, treat it as a stop signal: report the blocked scope and ask for explicit confirmation before taking further steps. " +
        "Warning: any purpose, note, description, or user manual stored on the category will be lost. " +
        "If the full path is not known, call list_categories first. " +
        "Note: pre-seeded categories should rarely be deleted – consider update_category to rename instead.")]
    public static string DeleteCategory(
        [Description("Full path of the category to delete, e.g. 'Electrical/Lighting'. Call list_categories if unsure.")] string category)
    {
        category = category?.Trim() ?? "";
        if (string.IsNullOrEmpty(category))
            return "Error: 'category' is required.";

        try
        {
            using var conn  = FirebirdDb.OpenConnection();
            var allCats     = LoadCatTree(conn);
            var byFullName  = allCats.ToDictionary(
                r => r.Str("CAT_FULLNAME"),
                r => r,
                StringComparer.OrdinalIgnoreCase);

            category = QueryHelpers.NormalizePath(category);
            if (!byFullName.TryGetValue(category, out var catRow))
                return $"Error: category '{category}' not found. Call list_categories to see available categories.";

            var oid = catRow.Str("Oid");

            // Blocking check 1: child categories
            var childRows  = FirebirdDb.ExecuteQuery(conn,
                """SELECT COUNT(*) AS CNT FROM "Category" WHERE "ParentCategory" = ?""", oid);
            var childCount = FirebirdDb.CountResult(childRows);
            if (childCount > 0)
                return $"Error: category '{category}' has {childCount} child categor{(childCount == 1 ? "y" : "ies")}. " +
                       "Delete or move child categories first.";

            // Blocking check 2: CItem references, split by subclass (Element/Connection/PartType + other)
            var usageRows = FirebirdDb.ExecuteQuery(conn, """
                SELECT
                    COUNT(e."Oid")  AS ELEM_COUNT,
                    COUNT(cn."Oid") AS CONN_COUNT,
                    COUNT(pt."Oid") AS PT_COUNT,
                    COUNT(ci."Oid") AS CI_COUNT
                FROM "CItem" ci
                LEFT JOIN "Element"    e  ON e."Oid"  = ci."Oid"
                LEFT JOIN "Connection" cn ON cn."Oid" = ci."Oid"
                LEFT JOIN "PartType"   pt ON pt."Oid" = ci."Oid"
                WHERE ci."Category" = ?
                """, oid);
            var elemCount  = usageRows[0].Long("ELEM_COUNT");
            var connCount  = usageRows[0].Long("CONN_COUNT");
            var ptCount    = usageRows[0].Long("PT_COUNT");
            var ciCount    = usageRows[0].Long("CI_COUNT");
            var otherCount = ciCount - elemCount - connCount - ptCount;

            if (ciCount > 0)
            {
                var parts = new List<string>();
                if (elemCount  > 0) parts.Add($"{elemCount} element{(elemCount == 1 ? "" : "s")}");
                if (connCount  > 0) parts.Add($"{connCount} connection{(connCount == 1 ? "" : "s")}");
                if (ptCount    > 0) parts.Add($"{ptCount} part type{(ptCount == 1 ? "" : "s")}");
                if (otherCount > 0) parts.Add($"{otherCount} other item{(otherCount == 1 ? "" : "s")}");
                var breakdown = string.Join(", ", parts);

                var hints = new List<string>();
                if (elemCount  > 0) hints.Add($"call get_by_category('{category}') to locate the elements");
                if (connCount  > 0) hints.Add($"call get_connections(category='{category}') to locate the connections");
                if (ptCount    > 0) hints.Add("part type references cannot be discovered via MCP tools directly");
                if (otherCount > 0) hints.Add("other referenced items are not currently manageable via MCP");

                return $"Error: category '{category}' is used by {ciCount} item{(ciCount == 1 ? "" : "s")} ({breakdown}). " +
                       "Reassign or delete them first. " +
                       string.Join("; ", hints) + ".";
            }

            return FirebirdDb.RunInTransaction(conn, txn =>
            {
                // Delete Category row first (FK to CEntity.Oid), then the CEntity base row
                FirebirdDb.ExecuteNonQuery(conn, txn,
                    """DELETE FROM "Category" WHERE "Oid" = ?""", oid);
                FirebirdDb.ExecuteNonQuery(conn, txn,
                    """DELETE FROM "CEntity" WHERE "Oid" = ?""", oid);

                return $"✓ Category '{category}' deleted.";
            });
        }
        catch (Exception ex)
        {
            return $"Error: failed to delete category: {ex.Message}";
        }
    }
}
