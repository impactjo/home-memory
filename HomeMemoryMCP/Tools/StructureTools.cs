using System.ComponentModel;
using System.Text;
using ModelContextProtocol.Server;
using HomeMemory.MCP.Db;

namespace HomeMemory.MCP.Tools;

[McpServerToolType]
public static class StructureTools
{
    [McpServerTool(Name = "get_structure_overview")]
    [Description(
        "Entry point: shows the building skeleton. " +
        "Elements are physical items (installed equipment, appliances, furniture, fixtures, tools, " +
        "structural components) organised in a location hierarchy (building → floor → room → wall → item). " +
        "Default (structuralAreasOnly=true): only structural area elements – i.e. elements whose category is marked " +
        "as structural area category (building, floor, room, outdoor area, etc.). " +
        "Structural area elements are navigable containers, not devices or components. " +
        "Returns ~60 elements, ideal as a first overview or to find the right path for " +
        "subsequent find_element/list_elements calls. " +
        "With structuralAreasOnly=false and 'under': preferred way to browse all content of a specific area " +
        "(e.g. 'What is in the basement?') – no result limit, full hierarchical tree, " +
        "more efficient than multiple find_element calls. " +
        "With 'under': restrict tree to a sub-path, e.g. 'House/GF/Office' – " +
        "shows only elements within that area, depth relative to that root. " +
        "Elements with status Planned or Removed are marked with their status name.")]
    public static string GetStructureOverview(
        [Description("Show only structural area elements (structural area category). Default: true.")] bool structuralAreasOnly = true,
        [Description("Maximum depth (relative to 'under' if specified). Default: auto – 3 for building overview; unlimited (full tree) when browsing area content (under + structuralAreasOnly=false).")] int maxDepth = 0,
        [Description("Restrict tree to this sub-path, e.g. 'House/GF/Office'. Empty = entire building.")] string under = "")
    {
        under = under.Trim().TrimEnd('/');
        int depthOffset = under.Length == 0 ? 0 : under.Count(c => c == '/');

        try
        {
            using var conn = FirebirdDb.OpenConnection();

            if (!string.IsNullOrEmpty(under))
            {
                var resolved = QueryHelpers.ResolveElementFullName(conn, under);
                if (resolved is null)
                    return $"Error: element '{under}' not found. Call get_structure_overview or find_element to find the correct path.";
                under = resolved;
                depthOffset = under.Count(c => c == '/');
            }

            List<Row> rows;
            string title;

            if (structuralAreasOnly)
            {
                var sql = new StringBuilder($"""
                    {SqlQueries.EtreeCte}
                    SELECT et.FULLNAME, et."Name", et."ShortName", et.DEPTH, cat."Name" AS CATNAME,
                           s."Name" AS STATUSNAME, s."StatusType" AS STATUSTYPE
                    FROM ETREE et
                    JOIN "CItem"    ci  ON ci."Oid"  = et."Oid"
                    JOIN "Category" cat ON cat."Oid" = ci."Category"
                    LEFT JOIN "CEntity" ce ON ce."Oid" = et."Oid"
                    LEFT JOIN "Status"  s  ON s."Oid"  = ce."Status"
                    WHERE cat."IsAreaCategory" = True
                    """);
                var paramList = new List<object?>();
                if (!string.IsNullOrEmpty(under))
                {
                    sql.Append(" AND (UPPER(et.FULLNAME) LIKE UPPER(?) OR UPPER(et.FULLNAME) = UPPER(?))");
                    paramList.Add(under + "/%");
                    paramList.Add(under);
                }
                sql.Append(" ORDER BY et.SORTPATH");
                rows  = FirebirdDb.ExecuteQuery(conn, sql.ToString(), paramList.ToArray());
                title = $"Building structure (areas{(under.Length > 0 ? $" under '{under}'" : "")}):";
            }
            else
            {
                int effectiveMaxDepth = maxDepth <= 0
                    ? (under.Length > 0 ? 99 : 3)
                    : maxDepth;
                int absMaxDepth = depthOffset + effectiveMaxDepth;
                var sql = new StringBuilder($"""
                    {SqlQueries.EtreeCte}
                    SELECT et.FULLNAME, et."Name", et."ShortName", et.DEPTH, NULL AS CATNAME,
                           s."Name" AS STATUSNAME, s."StatusType" AS STATUSTYPE
                    FROM ETREE et
                    LEFT JOIN "CEntity" ce ON ce."Oid" = et."Oid"
                    LEFT JOIN "Status"  s  ON s."Oid"  = ce."Status"
                    WHERE et.DEPTH <= {absMaxDepth}
                    """);
                var paramList = new List<object?>();
                if (!string.IsNullOrEmpty(under))
                {
                    sql.Append(" AND (UPPER(et.FULLNAME) LIKE UPPER(?) OR UPPER(et.FULLNAME) = UPPER(?))");
                    paramList.Add(under + "/%");
                    paramList.Add(under);
                }
                sql.Append(" ORDER BY et.SORTPATH");
                rows  = FirebirdDb.ExecuteQuery(conn, sql.ToString(), paramList.ToArray());
                var depthLabel = maxDepth <= 0 && under.Length > 0 ? "full tree" : $"depth {effectiveMaxDepth}";
                title = $"Building structure ({depthLabel}{(under.Length > 0 ? $" under '{under}'" : "")}):";
            }

            if (rows.Count == 0)
                return "No elements found.";

            var lines = new List<string> { $"{title}\n" };
            foreach (var row in rows)
            {
                int relDepth = Convert.ToInt32(row["DEPTH"]) - depthOffset;
                var indent = new string(' ', relDepth * 2);
                var icon   = relDepth == 0 ? "[]" : (relDepth == 1 ? "+-" : " -");
                var label  = row.Str("Name");
                var sn     = row.Str("ShortName");
                if (!string.IsNullOrEmpty(sn) && sn != label)
                    label += $" ({sn})";
                if (structuralAreasOnly && row.Str("CATNAME") == "E-Verteiler")
                    label += "  [E-Verteiler]";
                var st = row.GetValueOrDefault("STATUSTYPE");
                if (st is not null and not DBNull && Convert.ToInt32(st) is 1 or 2)
                    label += $"  {{{row.Str("STATUSNAME")}}}";
                lines.Add($"{indent}{icon} {label}");
            }

            lines.Add("");
            lines.Add(structuralAreasOnly
                ? $"Total: {rows.Count} areas"
                : $"Total: {rows.Count} elements");
            return string.Join("\n", lines);
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    [McpServerTool(Name = "find_element")]
    [Description(
        "Searches ALL elements by name or full path (partial text, case-insensitive). Up to 100 results. " +
        "Elements are physical items at a location: installed equipment (socket, boiler, radiator), " +
        "appliances (washing machine, fridge), furniture (sofa, wardrobe), fixtures, tools, " +
        "and structural area containers (building, floor, room, wall). " +
        "With 'status': filter by status type name (Existing / Planned / Removed – language-independent) " +
        "or by status name (exact match first, partial match as fallback). " +
        "Call list_statuses for available status names. " +
        "With 'under': restrict search to a sub-tree – more efficient and token-saving " +
        "than a global search. At least one of searchTerm, under, status, or category must be provided. " +
        "With 'searchAllFields=true': also searches in Purpose, Note, Description, UserManual, and Position – " +
        "useful when a keyword appears in a field but not in the element name. " +
        "Not suitable for browsing all content of an area – for that use get_structure_overview(under=..., structuralAreasOnly=false) which returns a complete tree without a result limit. " +
        "Note: physical lines (pipes, cables, conduits) are often documented as connections, not elements – " +
        "also call get_connections with searchTerm when searching for cables, pipes, or conduits.")]
    public static string FindElement(
        [Description("Search term, e.g. 'socket'. Empty = all elements (only useful with under, status, or category).")] string searchTerm = "",
        [Description("Path filter: only elements below this path, e.g. 'House/FF' or 'House/GF/Office'.")] string under = "",
        [Description("Status filter: 'Existing'/'Planned'/'Removed' for type-based filter; otherwise exact name match, partial match as fallback. Call list_statuses for names.")] string status = "",
        [Description("Category filter: exact name or short name (e.g. 'Socket'), full path with '/' (e.g. 'Electrical/Lighting'). Partial match as fallback; if ambiguous, an error lists full paths. Includes all subcategories.")] string category = "",
        [Description("Also search in Purpose, Note, Description, UserManual, and Position. Default: false (name/path only).")] bool? searchAllFields = null)
    {
        searchTerm = searchTerm.Trim();
        under      = under.Trim().TrimEnd('/');
        status     = status.Trim();
        category   = category.Trim();

        if (string.IsNullOrEmpty(searchTerm) && string.IsNullOrEmpty(under) && string.IsNullOrEmpty(status) && string.IsNullOrEmpty(category))
            return "Error: provide at least one of searchTerm, under, status, or category.";

        try
        {
            using var conn = FirebirdDb.OpenConnection();

            if (!string.IsNullOrEmpty(under))
            {
                var resolved = QueryHelpers.ResolveElementFullName(conn, under);
                if (resolved is null)
                    return $"Error: element '{under}' not found. Call get_structure_overview or find_element to find the correct path.";
                under = resolved;
            }

            var conditions = new List<string>();
            var paramList  = new List<object?>();

            if (!string.IsNullOrEmpty(searchTerm))
            {
                var term = $"%{searchTerm.ToUpperInvariant()}%";
                if (searchAllFields == true)
                {
                    conditions.Add("(UPPER(et.\"Name\") LIKE ? OR UPPER(et.FULLNAME) LIKE ?" +
                                   " OR UPPER(ce.\"Purpose\") LIKE ? OR UPPER(ce.\"Note\") LIKE ?" +
                                   " OR UPPER(ce.\"Description\") LIKE ? OR UPPER(ce.\"UserManual\") LIKE ?" +
                                   " OR UPPER(et.\"Position\") LIKE ?)");
                    paramList.Add(term); paramList.Add(term);
                    paramList.Add(term); paramList.Add(term);
                    paramList.Add(term); paramList.Add(term); paramList.Add(term);
                }
                else
                {
                    conditions.Add("(UPPER(et.\"Name\") LIKE ? OR UPPER(et.FULLNAME) LIKE ?)");
                    paramList.Add(term);
                    paramList.Add(term);
                }
            }
            if (!string.IsNullOrEmpty(under))
            {
                conditions.Add("UPPER(et.FULLNAME) LIKE UPPER(?)");
                paramList.Add(under + "/%");
            }
            if (!string.IsNullOrEmpty(status))
            {
                // Priority: 1) English type keywords, 2) exact status name, 3) LIKE fallback.
                // Type keywords first: "Removed" always finds all Removed-type statuses regardless of language.
                // Exact before LIKE: prevents partial bleed when names overlap (e.g. "Geplant" vs "Geplant - Maybe").
                int? statusTypeFilter = status.ToLowerInvariant() switch
                {
                    "existing"                    => 0,
                    "planned"                     => 1,
                    "removed" or "decommissioned" => 2,
                    _                             => (int?)null
                };
                if (statusTypeFilter.HasValue)
                {
                    conditions.Add("s.\"StatusType\" = ?");
                    paramList.Add(statusTypeFilter.Value);
                }
                else
                {
                    var exactCount = FirebirdDb.CountResult(
                        FirebirdDb.ExecuteQuery(conn,
                            """SELECT COUNT(*) AS CNT FROM "Status" WHERE UPPER("Name") = UPPER(?)""",
                            status));
                    if (exactCount > 0)
                    {
                        conditions.Add("UPPER(s.\"Name\") = UPPER(?)");
                        paramList.Add(status);
                    }
                    else
                    {
                        conditions.Add("UPPER(s.\"Name\") LIKE UPPER(?)");
                        paramList.Add($"%{status.ToUpperInvariant()}%");
                    }
                }
            }

            HashSet<string>? catOids = null;
            if (!string.IsNullOrEmpty(category))
            {
                var (catResolution, catError) = QueryHelpers.ResolveCategoryOidsWithDescendants(conn, category);
                if (catError is not null) return catError;
                catOids = catResolution?.Oids;
                if (catOids is null)
                {
                    var desc2   = !string.IsNullOrEmpty(searchTerm) ? $"'{searchTerm}'" : "all elements";
                    var scope2  = !string.IsNullOrEmpty(under)  ? $" under '{under}'"        : "";
                    var stFilt2 = !string.IsNullOrEmpty(status) ? $" with status '{status}'" : "";
                    return $"No elements found for {desc2}{scope2}{stFilt2} in category '{category}'.";
                }
            }

            // When filtering by category, fetch with CAT_OID and apply in-memory filter (OIDs are binary in Firebird).
            // When not filtering by category, use FIRST 101 for efficient truncation detection.
            var catSelect     = catOids is not null ? """, ci."Category" AS CAT_OID""" : "";
            var catJoin       = catOids is not null ? """LEFT JOIN "CItem" ci ON ci."Oid" = et."Oid" """ : "";
            var detailsSelect = searchAllFields == true
                ? ", ce.\"Purpose\", ce.\"Note\", ce.\"Description\", ce.\"UserManual\""
                : "";
            var firstClause   = catOids is null ? "FIRST 101 " : "";
            var where = conditions.Count > 0 ? string.Join(" AND ", conditions) : "1=1";
            var sql   = $"""
                {SqlQueries.EtreeCte}
                SELECT {firstClause}et.FULLNAME, et."Name", et."Position", s."Name" AS STATUSNAME{catSelect}{detailsSelect}
                FROM ETREE et
                {catJoin}
                LEFT JOIN "CEntity" ce ON ce."Oid" = et."Oid"
                LEFT JOIN "Status"  s  ON s."Oid"  = ce."Status"
                WHERE {where}
                ORDER BY et.FULLNAME
                """;
            var allRows = FirebirdDb.ExecuteQuery(conn, sql, paramList.ToArray());
            var rows = catOids is not null
                ? allRows.Where(r => catOids.Contains(FirebirdDb.OidKey(r.GetValueOrDefault("CAT_OID")))).ToList()
                : allRows;
            var totalCount = rows.Count;
            var truncated = totalCount > 100;
            if (truncated) rows = rows.Take(100).ToList();

            var desc   = !string.IsNullOrEmpty(searchTerm) ? $"'{searchTerm}'" : "all elements";
            var scope  = !string.IsNullOrEmpty(under)    ? $" under '{under}'"        : "";
            var stFilt = !string.IsNullOrEmpty(status)   ? $" with status '{status}'" : "";
            var catFilt= !string.IsNullOrEmpty(category) ? $" in category '{category}'" : "";

            if (rows.Count == 0)
                return $"No elements found for {desc}{scope}{stFilt}{catFilt}.";

            // totalCount is exact when category filter is active (in-memory filter);
            // with FIRST 101 (no category filter) we only know there are "more than 100".
            var countLabel = truncated
                ? (catOids is not null ? $"100 of {totalCount}" : "100+")
                : $"{rows.Count}";
            var lines = new List<string> { $"{countLabel} result(s) for {desc}{scope}{stFilt}{catFilt}:\n" };

            string? currentParent = null;
            foreach (var row in rows)
            {
                var fullname       = row.Str("FULLNAME");
                var (parent, name) = QueryHelpers.SplitParentAndName(fullname);

                if (parent != currentParent)
                {
                    lines.Add(!string.IsNullOrEmpty(parent) ? $"  {parent}" : "");
                    currentParent = parent;
                }

                var indent     = !string.IsNullOrEmpty(parent) ? "    " : "  ";
                var pos        = row.Str("Position");
                var statusName = row.Str("STATUSNAME");
                var suffix     = "";
                if (!string.IsNullOrEmpty(pos))        suffix += $"  [{pos}]";
                if (!string.IsNullOrEmpty(statusName)) suffix += $"  {{{statusName}}}";
                lines.Add($"{indent}{name}{suffix}");

                if (searchAllFields == true && !string.IsNullOrEmpty(searchTerm))
                {
                    var termUp = searchTerm.ToUpperInvariant();
                    if (!name.ToUpperInvariant().Contains(termUp) && !fullname.ToUpperInvariant().Contains(termUp))
                    {
                        var matchField =
                            row.Str("Purpose").ToUpperInvariant().Contains(termUp)     ? ("Purpose",     row.Str("Purpose"))     :
                            row.Str("Note").ToUpperInvariant().Contains(termUp)        ? ("Note",        row.Str("Note"))        :
                            row.Str("Position").ToUpperInvariant().Contains(termUp)    ? ("Position",    row.Str("Position"))    :
                            row.Str("Description").ToUpperInvariant().Contains(termUp) ? ("Description", row.Str("Description")) :
                            row.Str("UserManual").ToUpperInvariant().Contains(termUp)  ? ("UserManual",  row.Str("UserManual"))  :
                            default;
                        if (matchField != default)
                        {
                            var snippet = matchField.Item2.Length > 80
                                ? matchField.Item2[..80].TrimEnd() + "…"
                                : matchField.Item2;
                            lines.Add($"{indent}  ↳ {matchField.Item1}: {snippet}");
                        }
                    }
                }
            }
            if (truncated)
                lines.Add("\n(Showing first 100 results – refine your search or use get_by_category / get_structure_overview for complete results.)");
            return string.Join("\n", lines);
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    [McpServerTool(Name = "list_elements")]
    [Description(
        "Lists the direct child elements of an element – both structural area elements " +
        "(rooms, areas) and devices/components. Shows exactly one level of children. " +
        "Best for: 'What is in the kitchen?' or 'What rooms are on the ground floor?' " +
        "when the exact parent path is already known. " +
        "Unlike get_structure_overview (hierarchical tree) or find_element (search by name), " +
        "this returns a flat list of immediate children only. " +
        "Elements with status Planned or Removed are marked with their status name.")]
    public static string ListElements(
        [Description("Full name of the parent element (e.g. 'House/GF/Kitchen'). Empty = top-level.")] string parentFullname = "")
    {
        try
        {
            using var conn = FirebirdDb.OpenConnection();

            if (!string.IsNullOrWhiteSpace(parentFullname))
            {
                var resolved = QueryHelpers.ResolveElementFullName(conn, parentFullname.Trim());
                if (resolved is null)
                    return $"Error: element '{parentFullname.Trim()}' not found. Call get_structure_overview or find_element to find the correct path.";
                parentFullname = resolved;
            }

            List<Row> rows;
            string title;

            if (string.IsNullOrWhiteSpace(parentFullname))
            {
                var sql = $"""
                    {SqlQueries.EtreeCte}
                    SELECT et.FULLNAME, et."Name", et."ShortName", et."Position",
                           s."Name" AS STATUSNAME, s."StatusType" AS STATUSTYPE
                    FROM ETREE et
                    LEFT JOIN "CEntity" ce ON ce."Oid" = et."Oid"
                    LEFT JOIN "Status"  s  ON s."Oid"  = ce."Status"
                    WHERE et.DEPTH = 0
                    ORDER BY et."SortIndex" NULLS LAST, et."Name"
                    """;
                rows  = FirebirdDb.ExecuteQuery(conn, sql);
                title = "Top-level elements";
            }
            else
            {
                var sql = $"""
                    {SqlQueries.EtreeCte}
                    SELECT child.FULLNAME, child."Name", child."ShortName", child."Position",
                           s."Name" AS STATUSNAME, s."StatusType" AS STATUSTYPE
                    FROM ETREE parent
                    JOIN ETREE child ON child."PartOfElement" = parent."Oid"
                    LEFT JOIN "CEntity" ce ON ce."Oid" = child."Oid"
                    LEFT JOIN "Status"  s  ON s."Oid"  = ce."Status"
                    WHERE UPPER(parent.FULLNAME) = UPPER(?)
                    ORDER BY child."SortIndex" NULLS LAST, child."Name"
                    """;
                rows  = FirebirdDb.ExecuteQuery(conn, sql, parentFullname.Trim());
                title = $"Children of '{parentFullname}'";
            }

            if (rows.Count == 0)
                return "No elements found.";

            var lines = new List<string> { $"{title} ({rows.Count} items):\n" };
            foreach (var row in rows)
            {
                var label = row.Str("Name");
                var sn    = row.Str("ShortName");
                if (!string.IsNullOrEmpty(sn) && sn != label)
                    label += $" ({sn})";
                var st = row.GetValueOrDefault("STATUSTYPE");
                var statusSuffix = st is not null and not DBNull && Convert.ToInt32(st) is 1 or 2
                    ? $"  {{{row.Str("STATUSNAME")}}}"
                    : "";
                lines.Add($"  - {row.Str("FULLNAME")}  [{label}]{statusSuffix}");
                var pos = row.Str("Position");
                if (!string.IsNullOrEmpty(pos))
                    lines.Add($"      Position: {pos}");
            }
            return string.Join("\n", lines);
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    // ── Tool: get_recent_changes ────────────────────────────────────────────

    [McpServerTool(Name = "get_recent_changes")]
    [Description(
        "Shows recently created or updated items across elements, connections, and categories – " +
        "newest first. Useful after a documentation session to review what was added or changed. " +
        "With 'type': restrict to 'element', 'connection', or 'category'.")]
    public static string GetRecentChanges(
        [Description("Maximum number of results (1–200). Default: 20.")] int limit = 20,
        [Description("Restrict to item type: 'element', 'connection', or 'category'. Empty = all.")] string type = "")
    {
        type = type.Trim().ToLowerInvariant();
        if (limit < 1) limit = 1;
        if (limit > 200) limit = 200;
        if (!string.IsNullOrEmpty(type) && type is not "element" and not "connection" and not "category")
            return "Error: 'type' must be 'element', 'connection', or 'category'.";

        try
        {
            using var conn = FirebirdDb.OpenConnection();

            var includeElements    = string.IsNullOrEmpty(type) || type == "element";
            var includeConnections = string.IsNullOrEmpty(type) || type == "connection";
            var includeCategories  = string.IsNullOrEmpty(type) || type == "category";

            // Firebird CTE syntax: WITH RECURSIVE cte1 AS (...), cte2 AS (...)
            // — single WITH RECURSIVE keyword, then comma-separated CTE definitions.
            var cteParts = new List<string>();
            if (includeElements)    cteParts.Add("ETREE AS (" + SqlQueries.EtreeCteBody + ")");
            if (includeCategories)  cteParts.Add("CAT_TREE AS (" + SqlQueries.CatCteBody + ")");

            var unions = new List<string>();
            if (includeElements)
                unions.Add("""
                    SELECT
                        et.FULLNAME AS ITEM_NAME,
                        'element' AS ITEM_TYPE,
                        ce."CreatedOn",
                        ce."UpdatedOn",
                        COALESCE(ce."UpdatedOn", ce."CreatedOn") AS CHANGE_TS
                    FROM "Element" e
                    JOIN "CEntity" ce ON ce."Oid" = e."Oid"
                    JOIN ETREE et ON et."Oid" = e."Oid"
                    """);
            if (includeConnections)
                unions.Add("""
                    SELECT
                        c."Name" AS ITEM_NAME,
                        'connection' AS ITEM_TYPE,
                        ce."CreatedOn",
                        ce."UpdatedOn",
                        COALESCE(ce."UpdatedOn", ce."CreatedOn") AS CHANGE_TS
                    FROM "Connection" c
                    JOIN "CEntity" ce ON ce."Oid" = c."Oid"
                    """);
            if (includeCategories)
                unions.Add("""
                    SELECT
                        ct.CAT_FULLNAME AS ITEM_NAME,
                        'category' AS ITEM_TYPE,
                        ce."CreatedOn",
                        ce."UpdatedOn",
                        COALESCE(ce."UpdatedOn", ce."CreatedOn") AS CHANGE_TS
                    FROM "Category" cat
                    JOIN "CEntity" ce ON ce."Oid" = cat."Oid"
                    JOIN CAT_TREE ct ON ct."Oid" = cat."Oid"
                    """);

            var ctePrefix = cteParts.Count > 0
                ? "WITH RECURSIVE " + string.Join(", ", cteParts) + " "
                : "";

            var sql = $"""
                {ctePrefix}SELECT * FROM (
                    {string.Join("\n                    UNION ALL\n", unions)}
                ) items
                ORDER BY CHANGE_TS DESC NULLS LAST
                ROWS 1 TO {limit}
                """;

            var rows = FirebirdDb.ExecuteQuery(conn, sql);

            if (rows.Count == 0)
                return string.IsNullOrEmpty(type)
                    ? "No items found in the database."
                    : $"No {type} items found.";

            var typeLbl = string.IsNullOrEmpty(type) ? "" : $", type={type}";
            var lines = new List<string> { $"Recent changes ({rows.Count}{typeLbl}, newest first):\n" };

            foreach (var row in rows)
            {
                var ts = row.GetValueOrDefault("CHANGE_TS");
                var tsStr = ts is DateTime dt ? dt.ToString("yyyy-MM-dd HH:mm") : "                ";
                var createdOn = row.GetValueOrDefault("CreatedOn");
                var updatedOn = row.GetValueOrDefault("UpdatedOn");
                // Legacy records imported from external tools may have CreatedOn = NULL.
                // [updated] whenever UpdatedOn is set and differs from CreatedOn (or CreatedOn is absent).
                var noCreatedOn = createdOn is null or DBNull;
                var label = (updatedOn is not null and not DBNull &&
                             (noCreatedOn || !updatedOn.Equals(createdOn)))
                    ? "[updated]"
                    : "[created]";
                var itemType = row.Str("ITEM_TYPE");
                var itemName = row.Str("ITEM_NAME");
                lines.Add($"{tsStr}  {label,-10}  {itemType,-12}  {itemName}");
            }
            return string.Join("\n", lines);
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }
}
