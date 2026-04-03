using System.ComponentModel;
using System.Text.RegularExpressions;
using ModelContextProtocol.Server;
using HomeMemory.MCP.Db;

namespace HomeMemory.MCP.Tools;

[McpServerToolType]
public static class CategoryTools
{
    // Same forbidden characters as element names (SpecialCharactersNotAllowed)
    private static readonly Regex InvalidChars = new(@"[\$\*\[\{\]\}\|\\<>\?/"";\:\t]");

    // ── Shared Helper ─────────────────────────────────────────────────────────

    private static List<Row> LoadCatTree(FbConnection conn)
    {
        var sql = $"""
            {SqlQueries.CatCte}
            SELECT ct."Oid", ct.CAT_FULLNAME, ct."Name", ct."ShortName",
                   ct.CAT_DEPTH, ct."IsAreaCategory",
                   COUNT(e."Oid")    AS ELEM_COUNT,
                   COUNT(conn."Oid") AS CONN_COUNT,
                   ce."Description"
            FROM CAT_TREE ct
            LEFT JOIN "CItem"      ci   ON ci."Category" = ct."Oid"
            LEFT JOIN "Element"    e    ON e."Oid"    = ci."Oid"
            LEFT JOIN "Connection" conn ON conn."Oid" = ci."Oid"
            LEFT JOIN "CEntity"    ce   ON ce."Oid"  = ct."Oid"
            GROUP BY ct."Oid", ct.CAT_FULLNAME, ct."Name", ct."ShortName",
                     ct.CAT_DEPTH, ct."IsAreaCategory", ce."Description"
            ORDER BY ct.CAT_FULLNAME
            """;
        return FirebirdDb.ExecuteQuery(conn, sql);
    }

    // ── Tools ─────────────────────────────────────────────────────────────────

    [McpServerTool(Name = "list_categories")]
    [Description(
        "Lists all object categories with element/connection counts. " +
        "Categories classify both elements (physical items: equipment, furniture, fixtures) " +
        "and connections (physical lines: pipes, cables, ducts). " +
        "IMPORTANT: Call this before create_element or create_connection to pick the right category. " +
        "If no suitable category exists, use create_category first. " +
        "Structural area categories (Building, Floor, Room, ...) are marked with [structural area]. " +
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

            var lines = new List<string> { "Object categories:\n" };
            foreach (var r in rows)
            {
                int depth  = Convert.ToInt32(r.GetValueOrDefault("CAT_DEPTH") ?? 0);
                var indent = new string(' ', depth * 2);
                var icon   = depth > 0 ? "+-" : "-";
                var name   = FirebirdDb.Str(r["Name"]);
                var sn     = FirebirdDb.Str(r.GetValueOrDefault("ShortName"));
                var label  = !string.IsNullOrEmpty(sn) && sn != name ? $"{name} ({sn})" : name;
                bool isStructuralArea = FirebirdDb.IsTrue(r.GetValueOrDefault("IsAreaCategory"));
                long e = Convert.ToInt64(r.GetValueOrDefault("ELEM_COUNT") ?? 0L);
                long c = Convert.ToInt64(r.GetValueOrDefault("CONN_COUNT") ?? 0L);
                var parts = new List<string>();
                if (e > 0) parts.Add($"{e} elem.");
                if (c > 0) parts.Add($"{c} conn.");
                var countStr = parts.Count > 0 ? $"  ({string.Join(", ", parts)})" : "";
                var descFlag = !string.IsNullOrEmpty(FirebirdDb.Str(r.GetValueOrDefault("Description"))) ? "  [i]" : "";
                var areaFlag = isStructuralArea ? "  [structural area]" : "";
                // Show explicit path when ShortName changes the path segment (prevents copy-paste errors)
                var catFullname = FirebirdDb.Str(r.GetValueOrDefault("CAT_FULLNAME"));
                var pathHint = !string.IsNullOrEmpty(sn) && sn != name
                    ? $"  [path: {catFullname}]"
                    : "";
                lines.Add($"  {indent}{icon} {label}{areaFlag}{pathHint}{countStr}{descFlag}");
            }
            lines.Add("\n  [i] = description available");
            return string.Join("\n", lines);
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    [McpServerTool(Name = "get_by_category")]
    [Description(
        "All elements of an object category/trade, grouped by location. " +
        "Examples: get_by_category('Socket') → all sockets in the building; " +
        "get_by_category('Lighting', under='House/GF') → all lights on the ground floor. " +
        "Category name: partial text is sufficient (case-insensitive). " +
        "Without '/', partial text may match multiple categories; results include all matched categories and their descendants. " +
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
            var allCats = LoadCatTree(conn);

            List<Row> matched;
            if (category.Contains('/'))
            {
                // Path notation: exact CAT_FULLNAME match (e.g. 'HKL/Heizung')
                matched = allCats.Where(c =>
                    string.Equals(FirebirdDb.Str(c["CAT_FULLNAME"]), category, StringComparison.OrdinalIgnoreCase)
                ).ToList();
            }
            else
            {
                // Partial text match on Name or ShortName
                var searchUpper = category.ToUpperInvariant();
                matched = allCats.Where(c =>
                    FirebirdDb.Str(c["Name"]).ToUpperInvariant().Contains(searchUpper) ||
                    FirebirdDb.Str(c.GetValueOrDefault("ShortName")).ToUpperInvariant().Contains(searchUpper)
                ).ToList();
            }

            if (matched.Count == 0)
                return $"Error: category '{category}' not found. Call list_categories for available categories.";

            var includedOids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var m in matched)
            {
                var mFn    = FirebirdDb.Str(m["CAT_FULLNAME"]);
                var prefix = mFn + "/";
                foreach (var c in allCats)
                {
                    var cFn = FirebirdDb.Str(c["CAT_FULLNAME"]);
                    if (string.Equals(cFn, mFn, StringComparison.OrdinalIgnoreCase) ||
                        cFn.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                        includedOids.Add(FirebirdDb.OidKey(c["Oid"]));
                }
            }

            var paramList = new List<object?>();
            var underClause = "";
            if (!string.IsNullOrEmpty(under))
            {
                var resolvedUnder = QueryHelpers.ResolveElementFullName(conn, under);
                if (resolvedUnder is null)
                    return $"Error: element '{under}' not found. Call get_structure_overview or find_element to find the correct path.";

                underClause = "AND UPPER(et.FULLNAME) LIKE UPPER(?)";
                paramList.Add(resolvedUnder + "/%");
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

            var catLabel = matched.Count == 1 ? FirebirdDb.Str(matched[0]["Name"]) : $"'{category}'";
            bool multiCat = includedOids.Count > 1;
            var lines = new List<string> { $"{catLabel} elements{scope} ({rows.Count}):\n" };

            string? currentParent = null;
            foreach (var row in rows)
            {
                var fullname  = FirebirdDb.Str(row["FULLNAME"]);
                int lastSlash = fullname.LastIndexOf('/');
                string parent, name;
                if (lastSlash >= 0)
                {
                    parent = fullname[..(lastSlash + 1)];
                    name   = fullname[(lastSlash + 1)..];
                }
                else
                {
                    parent = "";
                    name   = fullname;
                }

                if (parent != currentParent)
                {
                    lines.Add(!string.IsNullOrEmpty(parent) ? $"  {parent}" : "");
                    currentParent = parent;
                }

                var indent  = !string.IsNullOrEmpty(parent) ? "    " : "  ";
                var pos     = FirebirdDb.Str(row.GetValueOrDefault("Position"));
                var catHint = multiCat ? $" [{FirebirdDb.Str(row.GetValueOrDefault("CATNAME"))}]" : "";
                var posHint = !string.IsNullOrEmpty(pos) ? $"  [{pos}]" : "";
                var st      = row.GetValueOrDefault("STATUSTYPE");
                var stHint  = st is not null and not DBNull && Convert.ToInt32(st) is 1 or 2
                    ? $"  {{{FirebirdDb.Str(row.GetValueOrDefault("STATUSNAME"))}}}"
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

    // ── Tool: update_category ─────────────────────────────────────────────────

    [McpServerTool(Name = "update_category")]
    [Description(
        "Updates an existing object category: rename, change short name, description, structural area flag, or move to a different parent. " +
        "Required: category (current full path, e.g. 'Electrical/Lighting'). " +
        "Optional: new_name, new_short_name (CLEAR to remove), description (CLEAR to remove), " +
        "is_structural_area ('true' or 'false'), " +
        "new_parent (full category path, e.g. 'Electrical'; CLEAR to move to top-level). " +
        "At least one optional field must be provided. " +
        "Forbidden characters in name/short_name: $*[{}|\\<>?\"/;: and tab. " +
        "Note: renaming/moving a category automatically updates the full path of all child categories " +
        "(FullName is computed dynamically – no stored paths need to be migrated).")]
    public static string UpdateCategory(
        [Description("Current full path of the category to update, e.g. 'Electrical/Lighting'.")] string category,
        [Description("New name (optional).")] string? new_name = null,
        [Description("New short name (optional). Use 'CLEAR' to remove.")] string? new_short_name = null,
        [Description("New description (optional). Use 'CLEAR' to remove.")] string? description = null,
        [Description("Set to true to mark as structural area (navigable container like Room/Floor), false to unmark.")] bool? is_structural_area = null,
        [Description("New parent category full path (optional). Use 'CLEAR' to move to top-level.")] string? new_parent = null)
    {
        category = category?.Trim() ?? "";
        if (string.IsNullOrEmpty(category))
            return "Error: 'category' is required.";

        if (new_name == null && new_short_name == null && description == null && is_structural_area == null && new_parent == null)
            return "Error: provide at least one of new_name, new_short_name, description, is_structural_area, new_parent.";

        // Validate new_name
        if (new_name != null)
        {
            new_name = new_name.Trim();
            if (string.IsNullOrEmpty(new_name))
                return "Error: 'new_name' cannot be empty.";
            if (InvalidChars.IsMatch(new_name))
                return "Error: new_name contains invalid characters ($*[{}|\\<>?\"/;: or tab).";
        }

        // Validate new_short_name
        bool clearShortName = new_short_name?.Trim().Equals("CLEAR", StringComparison.OrdinalIgnoreCase) == true;
        if (!clearShortName && new_short_name != null)
        {
            new_short_name = new_short_name.Trim();
            if (InvalidChars.IsMatch(new_short_name))
                return "Error: new_short_name contains invalid characters ($*[{}|\\<>?\"/;: or tab).";
        }

        bool clearDescription = description?.Trim().Equals("CLEAR", StringComparison.OrdinalIgnoreCase) == true;
        bool clearParent      = new_parent?.Trim().Equals("CLEAR", StringComparison.OrdinalIgnoreCase) == true;

        // Field length validation
        var lenErr = Validate.Length(new_name, "new_name", 100)
                  ?? Validate.Length(!clearShortName ? new_short_name : null, "new_short_name", 50)
                  ?? Validate.Length(!clearDescription ? description?.Trim() : null, "description", 4000);
        if (lenErr != null) return lenErr;

        try
        {
            using var conn  = FirebirdDb.OpenConnection();
            var allCats     = LoadCatTree(conn);
            var byFullName  = allCats.ToDictionary(
                r => FirebirdDb.Str(r["CAT_FULLNAME"]),
                r => r,
                StringComparer.OrdinalIgnoreCase);

            // Find the category to update
            category = QueryHelpers.NormalizePath(category);
            if (!byFullName.TryGetValue(category, out var catRow))
                return $"Error: category '{category}' not found. Call list_categories to see available categories.";

            var oid         = FirebirdDb.Str(catRow["Oid"]);
            var currentName = FirebirdDb.Str(catRow["Name"]);
            var currentSN   = FirebirdDb.Str(catRow.GetValueOrDefault("ShortName"));

            // Load current ParentCategory OID from DB
            var parentRows      = FirebirdDb.ExecuteQuery(conn,
                """SELECT "ParentCategory" FROM "Category" WHERE "Oid" = ?""", oid);
            var currentParentStr = parentRows.Count > 0
                ? FirebirdDb.Str(parentRows[0].GetValueOrDefault("ParentCategory"))
                : null;
            var currentParentOid = string.IsNullOrEmpty(currentParentStr) ? null : currentParentStr;

            // Determine effective values
            var effectiveName = new_name ?? currentName;

            string? effectiveSN;
            if (clearShortName)
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
                var newParentFullName = FirebirdDb.Str(newParentRow["CAT_FULLNAME"]);
                if (string.Equals(newParentFullName, category, StringComparison.OrdinalIgnoreCase))
                    return "Error: a category cannot be its own parent.";
                if (newParentFullName.StartsWith(category + "/", StringComparison.OrdinalIgnoreCase))
                    return "Error: circular reference – the new parent is a descendant of this category.";

                effectiveParentOid = FirebirdDb.Str(newParentRow["Oid"]);
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
            var nameRows  = FirebirdDb.ExecuteQuery(conn, nameCheckSql, nameArgs);
            var nameCount = nameRows.Count > 0 ? Convert.ToInt64(nameRows[0].GetValueOrDefault("CNT") ?? 0L) : 0L;
            if (nameCount > 0)
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
                var snRows  = FirebirdDb.ExecuteQuery(conn, snCheckSql, snArgs);
                var snCount = snRows.Count > 0 ? Convert.ToInt64(snRows[0].GetValueOrDefault("CNT") ?? 0L) : 0L;
                if (snCount > 0)
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
            var segRows  = FirebirdDb.ExecuteQuery(conn, segCheckSql, segArgs);
            var segCount = segRows.Count > 0 ? Convert.ToInt64(segRows[0].GetValueOrDefault("CNT") ?? 0L) : 0L;
            if (segCount > 0)
                return $"Error: another category with path segment '{effectiveSegment}' already exists under the same parent (would create a duplicate category path).";

            var now = DateTime.UtcNow;
            using var txn = conn.BeginTransaction();
            try
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

                if (description != null)
                {
                    var effectiveDesc = clearDescription ? null : description.Trim();
                    FirebirdDb.ExecuteNonQuery(conn, txn, """
                        UPDATE "CEntity"
                        SET "Description" = ?
                        WHERE "Oid" = ?
                        """,
                        (object?)effectiveDesc ?? DBNull.Value,
                        oid);
                }

                var newIsArea = is_structural_area;
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

                txn.Commit();

                var changes = new List<string>();
                if (new_name != null)       changes.Add($"name → '{effectiveName}'");
                if (new_short_name != null) changes.Add(clearShortName ? "short_name → (removed)" : $"short_name → '{effectiveSN}'");
                if (description != null)    changes.Add(clearDescription ? "description → (removed)" : "description updated");
                if (newIsArea.HasValue)     changes.Add($"is_structural_area → {newIsArea.Value.ToString().ToLower()}");
                if (clearParent || new_parent != null)
                    changes.Add(effectiveParentOid == null ? "parent → (top-level)" : $"parent → '{new_parent}'");

                return $"✓ Category '{category}' updated: {string.Join(", ", changes)}.";
            }
            catch
            {
                txn.Rollback();
                throw;
            }
        }
        catch (Exception ex)
        {
            return $"Error updating category: {ex.Message}";
        }
    }

    // ── Tool: create_category ──────────────────────────────────────────────────

    [McpServerTool(Name = "create_category")]
    [Description(
        "Creates a new object category. Use this when no suitable category exists for a new element. " +
        "Required: name. Optional: parent (full category path, e.g. 'Electrical'), " +
        "short_name, description, is_structural_area (default false – structural area categories are " +
        "navigable building containers like Room/Floor, not trades or surface zones). " +
        "Forbidden characters in name/short_name: $*[{}|\\<>?\"/;: and tab. " +
        "After creating, pass the new category name to create_element or create_connection.")]
    public static string CreateCategory(
        [Description("Category name, e.g. 'Pool Technology' or 'Solar'")] string name,
        [Description("Full path of the parent category, e.g. 'Heating' or 'Electrical/Low-Voltage'. Empty = top-level.")] string? parent = null,
        [Description("Short name (optional), used as path segment, e.g. 'Pool'")] string? short_name = null,
        [Description("Description of this category (optional)")] string? description = null,
        [Description("Set to true for structural area categories (Building, Floor, Room). Omit or set to false (default) for trade/device categories or surface zones (Wall Section, Ceiling Section).")] bool? is_structural_area = null)
    {
        name = name?.Trim() ?? "";
        if (string.IsNullOrEmpty(name))
            return "Error: 'name' is required.";
        if (InvalidChars.IsMatch(name))
            return "Error: name contains invalid characters ($*[{}|\\<>?\"/;: or tab).";

        short_name = string.IsNullOrWhiteSpace(short_name) ? null : short_name.Trim();
        if (short_name != null && InvalidChars.IsMatch(short_name))
            return "Error: short_name contains invalid characters ($*[{}|\\<>?\"/;: or tab).";

        // Field length validation
        var lenErr = Validate.Length(name, "name", 100)
                  ?? Validate.Length(short_name, "short_name", 50)
                  ?? Validate.Length(description?.Trim(), "description", 4000);
        if (lenErr != null) return lenErr;

        var isArea = is_structural_area ?? false;

        try
        {
            using var conn = FirebirdDb.OpenConnection();

            // Load all categories to resolve parent and check uniqueness
            var allCats = LoadCatTree(conn);
            var byFullName = allCats.ToDictionary(
                r => FirebirdDb.Str(r["CAT_FULLNAME"]),
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
                parentOid      = FirebirdDb.Str(parentRow["Oid"]);
                parentFullName = parent;
            }

            // Compute new full name and check uniqueness
            var segment     = string.IsNullOrEmpty(short_name) ? name : short_name;
            var newFullName = parentFullName != null ? $"{parentFullName}/{segment}" : segment;
            if (byFullName.ContainsKey(newFullName))
                return $"Error: a category with full name '{newFullName}' already exists.";

            // Check Name + ParentCategory uniqueness (ConstructionCategory_UniqueCombinationOfNameAndParentCategory)
            var nameCheckSql = parentOid != null
                ? """SELECT COUNT(*) AS CNT FROM "Category" WHERE UPPER("Name") = UPPER(?) AND "ParentCategory" = ?"""
                : """SELECT COUNT(*) AS CNT FROM "Category" WHERE UPPER("Name") = UPPER(?) AND "ParentCategory" IS NULL""";
            var nameArgs = parentOid != null
                ? new object?[] { name, parentOid }
                : new object?[] { name };
            var nameRows  = FirebirdDb.ExecuteQuery(conn, nameCheckSql, nameArgs);
            var nameCount = nameRows.Count > 0 ? Convert.ToInt64(nameRows[0].GetValueOrDefault("CNT") ?? 0L) : 0L;
            if (nameCount > 0)
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
                var snRows  = FirebirdDb.ExecuteQuery(conn, snCheckSql, snArgs);
                var snCount = snRows.Count > 0 ? Convert.ToInt64(snRows[0].GetValueOrDefault("CNT") ?? 0L) : 0L;
                if (snCount > 0)
                    return $"Error: a category with short name '{short_name}' already exists under the same parent.";
            }

            var oid = Guid.NewGuid().ToString("D");
            var now = DateTime.UtcNow;

            using var txn = conn.BeginTransaction();
            try
            {
                // CEntity: base row (no CItem/Part for categories)
                FirebirdDb.ExecuteNonQuery(conn, txn, """
                    INSERT INTO "CEntity" ("Oid", "OptimisticLockField", "ObjectType", "CreatedOn", "CreatedBy", "Description")
                    VALUES (?, 0, ?, ?, ?, ?)
                    """,
                    oid, XPObjectTypes.Category, now, "HomeMemory",
                    (object?)description?.Trim() ?? DBNull.Value);

                // Category: the actual category row
                FirebirdDb.ExecuteNonQuery(conn, txn, """
                    INSERT INTO "Category" ("Oid", "Name", "ShortName", "IsAreaCategory", "ParentCategory")
                    VALUES (?, ?, ?, ?, ?)
                    """,
                    oid, name,
                    (object?)short_name ?? DBNull.Value,
                    isArea,
                    (object?)parentOid  ?? DBNull.Value);

                txn.Commit();
                return $"✓ Category '{newFullName}' created (OID: {oid}).";
            }
            catch
            {
                txn.Rollback();
                throw;
            }
        }
        catch (Exception ex)
        {
            return $"Error creating category: {ex.Message}";
        }
    }

    // ── Tool: delete_category ──────────────────────────────────────────────────

    [McpServerTool(Name = "delete_category")]
    [Description(
        "Permanently deletes an empty, unused category. " +
        "Required: category (full path, e.g. 'Electrical/Lighting'). " +
        "Blocked if: (1) the category has child categories – ask the user how to handle them first; " +
        "(2) any element, connection, or part type references this category – " +
        "ask the user how to handle them first (use get_by_category for elements, get_connections for connections; part type references are not directly discoverable via MCP). " +
        "When deletion fails for either reason, treat it as a stop signal: report the blocked scope and ask for explicit confirmation before taking further steps. " +
        "Warning: any description stored on the category will be lost. " +
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
                r => FirebirdDb.Str(r["CAT_FULLNAME"]),
                r => r,
                StringComparer.OrdinalIgnoreCase);

            category = QueryHelpers.NormalizePath(category);
            if (!byFullName.TryGetValue(category, out var catRow))
                return $"Error: category '{category}' not found. Call list_categories to see available categories.";

            var oid = FirebirdDb.Str(catRow["Oid"]);

            // Blocking check 1: child categories
            var childRows  = FirebirdDb.ExecuteQuery(conn,
                """SELECT COUNT(*) AS CNT FROM "Category" WHERE "ParentCategory" = ?""", oid);
            var childCount = childRows.Count > 0 ? Convert.ToInt64(childRows[0].GetValueOrDefault("CNT") ?? 0L) : 0L;
            if (childCount > 0)
                return $"Error: category '{category}' has {childCount} child categor{(childCount == 1 ? "y" : "ies")}. " +
                       "Delete or move child categories first.";

            // Blocking check 2: CItem references (elements, connections, and part types)
            var usageRows  = FirebirdDb.ExecuteQuery(conn,
                """SELECT COUNT(*) AS CNT FROM "CItem" WHERE "Category" = ?""", oid);
            var usageCount = usageRows.Count > 0 ? Convert.ToInt64(usageRows[0].GetValueOrDefault("CNT") ?? 0L) : 0L;
            if (usageCount > 0)
                return $"Error: category '{category}' is used by {usageCount} item{(usageCount == 1 ? "" : "s")} " +
                       "(elements, connections, or part types). Reassign or delete them first. " +
                       $"To find references: call get_by_category('{category}') for elements; " +
                       "use get_connections for connections; " +
                       "part type references cannot be discovered via MCP tools directly.";

            using var txn = conn.BeginTransaction();
            try
            {
                // Delete Category row first (FK to CEntity.Oid), then the CEntity base row
                FirebirdDb.ExecuteNonQuery(conn, txn,
                    """DELETE FROM "Category" WHERE "Oid" = ?""", oid);
                FirebirdDb.ExecuteNonQuery(conn, txn,
                    """DELETE FROM "CEntity" WHERE "Oid" = ?""", oid);

                txn.Commit();
                return $"✓ Category '{category}' deleted.";
            }
            catch
            {
                txn.Rollback();
                throw;
            }
        }
        catch (Exception ex)
        {
            return $"Error deleting category: {ex.Message}";
        }
    }
}
