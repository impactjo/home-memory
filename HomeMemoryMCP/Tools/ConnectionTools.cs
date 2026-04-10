using System.ComponentModel;
using System.Text;
using ModelContextProtocol.Server;
using HomeMemory.MCP.Db;

namespace HomeMemory.MCP.Tools;

[McpServerToolType]
public static class ConnectionTools
{

    [McpServerTool(Name = "get_connections")]
    [Description(
        "All connections of a category, grouped by source element. " +
        "A connection is a physical line (pipe, cable, duct, conduit) running from one element to another – " +
        "it has a source element, a destination element, an optional route description, and an optional length. " +
        "Examples: get_connections('Pipe') → all pipelines; " +
        "get_connections('Cable', under='House/GF') → all cables on the ground floor. " +
        "Without category: all connections under 'under'. " +
        "Category: partial name (e.g. 'Cable'), full path (e.g. 'Electrical/Cable'), or short name – includes all subcategories. " +
        "If the exact category name is uncertain, use searchTerm first – " +
        "results show the category of each match, which you can then pass as the category parameter. " +
        "searchTerm filters by connection name (partial, case-insensitive) – " +
        "use this to find connections by keyword, e.g. searchTerm='conduit' or searchTerm='leerrohr'. " +
        "With searchAllFields=true, searchTerm also matches Route, Purpose, Note, and Description – " +
        "useful when a keyword appears in a field other than the connection name. " +
        "Returns up to 100 connections; refine with category, under, or searchTerm for complete results. " +
        "Note: conduit/cable endpoints may also be documented as elements – " +
        "combine with find_element(searchTerm) for a complete picture.")]
    public static string GetConnections(
        [Description("Connection category: partial name (e.g. 'Cable'), full path with '/' (e.g. 'Electrical/Cable'), or short name. Includes all subcategories. Empty = all. Call list_categories for names.")] string category = "",
        [Description("Spatial filter: connections whose source or destination is under this path.")] string under = "",
        [Description("Filter by connection name (partial match, case-insensitive), e.g. 'conduit' or 'leerrohr'.")] string searchTerm = "",
        [Description("Also search in Route, Purpose, Note, and Description. Default: false (name only).")] bool? searchAllFields = null)
    {
        category   = category.Trim();
        under      = under.Trim().TrimEnd('/');
        searchTerm = searchTerm.Trim();

        if (string.IsNullOrEmpty(category) && string.IsNullOrEmpty(under) && string.IsNullOrEmpty(searchTerm))
            return "Error: provide at least one of category, searchTerm, or under.";

        try
        {
            using var conn = FirebirdDb.OpenConnection();

            var allElements = FirebirdDb.ExecuteQuery(conn,
                $"{SqlQueries.EtreeCte} SELECT \"Oid\", FULLNAME FROM ETREE");
            var oidToFn = allElements.ToDictionary(
                r => FirebirdDb.OidKey(r["Oid"]),
                r => r.Str("FULLNAME"),
                StringComparer.OrdinalIgnoreCase);

            HashSet<string>? catOids = null;
            if (!string.IsNullOrEmpty(category))
            {
                catOids = QueryHelpers.ResolveCategoryOidsWithDescendants(conn, category);
                if (catOids is null || catOids.Count == 0)
                    return $"Error: category '{category}' not found. Call list_categories for available category names.";
            }

            var detailsSelect = searchAllFields == true
                ? """, ce."Purpose", ce."Note", ce."Description" """
                : "";
            var ceJoin = searchAllFields == true
                ? """LEFT JOIN "CEntity" ce ON ce."Oid" = c."Oid" """
                : "";
            var sql = new StringBuilder($"""
                SELECT c."Name", c."Source", c."Destination", c."Route", c."Length",
                       cat."Name" AS CATNAME, ci."Category" AS CAT_OID{detailsSelect}
                FROM "Connection" c
                JOIN "CItem"    ci  ON ci."Oid"  = c."Oid"
                JOIN "Category" cat ON cat."Oid" = ci."Category"
                {ceJoin}WHERE 1=1
                """);
            var paramList = new List<object?>();
            if (!string.IsNullOrEmpty(searchTerm))
            {
                var term = $"%{searchTerm.ToUpperInvariant()}%";
                if (searchAllFields == true)
                {
                    sql.Append(" AND (UPPER(c.\"Name\") LIKE ?" +
                               " OR UPPER(c.\"Route\") LIKE ?" +
                               " OR UPPER(ce.\"Purpose\") LIKE ?" +
                               " OR UPPER(ce.\"Note\") LIKE ?" +
                               " OR UPPER(ce.\"Description\") LIKE ?)");
                    paramList.Add(term); paramList.Add(term);
                    paramList.Add(term); paramList.Add(term); paramList.Add(term);
                }
                else
                {
                    sql.Append(" AND UPPER(c.\"Name\") LIKE ?");
                    paramList.Add(term);
                }
            }
            sql.Append(" ORDER BY c.\"Source\", c.\"Name\"");

            var fetched = FirebirdDb.ExecuteQuery(conn, sql.ToString(), paramList.ToArray());
            var raw = catOids is not null
                ? fetched.Where(r => catOids.Contains(FirebirdDb.OidKey(r.GetValueOrDefault("CAT_OID")))).ToList()
                : fetched;

            if (!string.IsNullOrEmpty(under))
            {
                var resolved = QueryHelpers.ResolveElementFullName(conn, under);
                if (resolved is null)
                    return $"Error: element '{under}' not found. Call get_structure_overview or find_element to find the correct path.";
                under = resolved;
            }

            var prefix = !string.IsNullOrEmpty(under) ? under.TrimEnd('/') + "/" : "";
            var results = new List<(Row row, string srcFn, string dstFn)>();
            foreach (var r in raw)
            {
                var srcFn = oidToFn.GetValueOrDefault(FirebirdDb.OidKey(r.GetValueOrDefault("Source")), "");
                var dstFn = oidToFn.GetValueOrDefault(FirebirdDb.OidKey(r.GetValueOrDefault("Destination")), "");
                if (!string.IsNullOrEmpty(under) &&
                    !srcFn.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                    !dstFn.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    continue;
                results.Add((r, srcFn, dstFn));
            }

            results.Sort((a, b) => string.Compare(
                a.srcFn + a.row.Str("Name"),
                b.srcFn + b.row.Str("Name"),
                StringComparison.OrdinalIgnoreCase));

            var totalCount = results.Count;
            var truncated = totalCount > 100;
            if (truncated) results = results.Take(100).ToList();

            var scope   = !string.IsNullOrEmpty(under)      ? $" under '{under}'"        : "";
            var catLbl  = !string.IsNullOrEmpty(category)   ? $"'{category}'"            : "all categories";
            var termLbl = !string.IsNullOrEmpty(searchTerm)
                ? (searchAllFields == true ? $" ~'{searchTerm}'" : $" name~'{searchTerm}'")
                : "";

            if (results.Count == 0)
            {
                var tip = !string.IsNullOrEmpty(category) ? " Tip: call list_categories for available category names." : "";
                return $"No connections found for {catLbl}{termLbl}{scope}.{tip}";
            }

            var lines = new List<string> { $"Connections {catLbl}{termLbl}{scope} ({results.Count}{(truncated ? $" of {totalCount}" : "")}):\n" };

            string? currentSrc = null;
            foreach (var (r, srcFn, dstFn) in results)
            {
                if (srcFn != currentSrc)
                {
                    var (srcParent, srcName) = QueryHelpers.SplitParentAndName(srcFn);
                    lines.Add($"\n  {srcParent}{srcName}:");
                    currentSrc = srcFn;
                }

                var dst    = !string.IsNullOrEmpty(dstFn) ? dstFn : r.Str("Destination");
                var name   = r.Str("Name");
                var length = r.GetValueOrDefault("Length");
                var route  = r.Str("Route");
                var detail = (length is not null and not DBNull) ? $"  ({length} m)" : "";
                lines.Add($"    --> {dst}  [{name}]{detail}");
                if (!string.IsNullOrEmpty(route))
                    lines.Add($"        Route: {route}");

                if (searchAllFields == true && !string.IsNullOrEmpty(searchTerm))
                {
                    var termUp = searchTerm.ToUpperInvariant();
                    if (!name.ToUpperInvariant().Contains(termUp))
                    {
                        // Route is already shown above — skip it for the hint, show only non-visible fields
                        var matchField =
                            r.Str("Purpose").ToUpperInvariant().Contains(termUp)     ? ("Purpose",     r.Str("Purpose"))     :
                            r.Str("Note").ToUpperInvariant().Contains(termUp)        ? ("Note",        r.Str("Note"))        :
                            r.Str("Description").ToUpperInvariant().Contains(termUp) ? ("Description", r.Str("Description")) :
                            default;
                        if (matchField != default)
                        {
                            var snippet = matchField.Item2.Length > 80
                                ? matchField.Item2[..80].TrimEnd() + "…"
                                : matchField.Item2;
                            lines.Add($"        ↳ {matchField.Item1}: {snippet}");
                        }
                    }
                }
            }
            if (truncated)
                lines.Add($"\n(Showing first 100 of {totalCount} connections – narrow with category, under, or searchTerm.)");
            return string.Join("\n", lines);
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    // ── Tool: get_connection_details ─────────────────────────────────────────

    [McpServerTool(Name = "get_connection_details")]
    [Description(
        "Full details of a single connection: category, source, destination, route, length, " +
        "purpose, note, and description. " +
        "Identify by name, optionally narrowed by source or destination when multiple connections share the same name. " +
        "Use before update_connection when you need to read and extend existing text fields.")]
    public static string GetConnectionDetails(
        [Description("Connection name to look up")] string name,
        [Description("Full path of the source element – narrows search if name is ambiguous (optional)")] string? source = null,
        [Description("Full path of the destination element – narrows search if name is ambiguous (optional)")] string? destination = null)
    {
        name = name?.Trim() ?? "";
        if (string.IsNullOrEmpty(name))
            return "Error: 'name' is required.";

        source = string.IsNullOrWhiteSpace(source) ? null : QueryHelpers.NormalizePath(source);
        destination = string.IsNullOrWhiteSpace(destination) ? null : QueryHelpers.NormalizePath(destination);

        try
        {
            using var conn = FirebirdDb.OpenConnection();

            var (allElements, _, byFullName) = QueryHelpers.LoadEtree(conn);
            var oidToFn = allElements.ToDictionary(
                r => FirebirdDb.OidKey(r["Oid"]),
                r => r.Str("FULLNAME"),
                StringComparer.OrdinalIgnoreCase);

            string? srcOid = null, dstOid = null;
            if (source != null)
            {
                if (!QueryHelpers.TryResolveElementRow(byFullName, source, out var srcRow, out _))
                    return $"Error: source element '{source}' not found.";
                srcOid = srcRow.Str("Oid");
            }
            if (destination != null)
            {
                if (!QueryHelpers.TryResolveElementRow(byFullName, destination, out var dstRow, out _))
                    return $"Error: destination element '{destination}' not found.";
                dstOid = dstRow.Str("Oid");
            }

            var findSql  = """SELECT c."Oid" FROM "Connection" c WHERE UPPER(c."Name") = ?""";
            var findArgs = new List<object?> { name.ToUpper() };
            if (srcOid != null) { findSql += """ AND c."Source" = ?""";      findArgs.Add(srcOid); }
            if (dstOid != null) { findSql += """ AND c."Destination" = ?"""; findArgs.Add(dstOid); }

            var matches = FirebirdDb.ExecuteQuery(conn, findSql, findArgs.ToArray());
            if (matches.Count == 0)
                return $"Error: connection '{name}' not found. " +
                       "Tip: provide source and/or destination to narrow the search.";
            if (matches.Count > 1)
                return $"Error: {matches.Count} connections named '{name}' found. " +
                       "Provide source and/or destination to narrow the search.";

            var oid = matches[0].Str("Oid");

            var detail = FirebirdDb.ExecuteQuery(conn, """
                SELECT c."Name", c."Source", c."Destination", c."Route", c."Length",
                       ce."Purpose", ce."Note", ce."Description",
                       cat."Name" AS CategoryName,
                       pt."Name"  AS PartTypeName
                FROM "Connection" c
                JOIN "CEntity"  ce  ON ce."Oid"  = c."Oid"
                JOIN "CItem"    ci  ON ci."Oid"   = c."Oid"
                JOIN "Category" cat ON cat."Oid"  = ci."Category"
                LEFT JOIN "Part"     p  ON p."Oid"   = c."Oid"
                LEFT JOIN "PartType" pt ON pt."Oid"  = p."PartType"
                WHERE c."Oid" = ?
                """, oid);

            if (detail.Count == 0)
                return $"Error: could not load details for connection '{name}'.";

            var d      = detail[0];
            var srcFn  = oidToFn.GetValueOrDefault(FirebirdDb.OidKey(d.GetValueOrDefault("Source")), "");
            var dstFn  = oidToFn.GetValueOrDefault(FirebirdDb.OidKey(d.GetValueOrDefault("Destination")), "");
            var length = d.GetValueOrDefault("Length");

            var lines = new List<string> { $"Connection: {d.Str("Name")}\n" };
            lines.Add($"  Category    : {d.Str("CategoryName")}");
            var partType = d.Str("PartTypeName");
            if (!string.IsNullOrEmpty(partType)) lines.Add($"  Part type   : {partType}");
            lines.Add($"  Source      : {srcFn}");
            lines.Add($"  Destination : {dstFn}");
            if (length is not null and not DBNull) lines.Add($"  Length      : {length} m");

            var route = d.Str("Route");
            if (!string.IsNullOrEmpty(route))       lines.Add($"  Route       : {route}");

            var purpose = d.Str("Purpose");
            if (!string.IsNullOrEmpty(purpose))     lines.Add($"  Purpose     : {purpose}");

            var note = d.Str("Note");
            if (!string.IsNullOrEmpty(note))        lines.Add($"  Note        : {note}");

            var desc = d.Str("Description");
            if (!string.IsNullOrEmpty(desc))        lines.Add($"  Description : {desc}");

            return string.Join("\n", lines);
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    // ── Tool: create_connection ───────────────────────────────────────────────

    [McpServerTool(Name = "create_connection")]
    [Description(
        "Creates a new connection between two elements. " +
        "A connection is a physical line (pipe, cable, duct, conduit) that runs from a source element " +
        "to a destination element. It has a name, a category, and optionally a route description and length. " +
        "Connections are a power feature – use only when the physical line itself is worth documenting " +
        "(e.g. to track cable routes, pipe runs, or duct paths). " +
        "Required fields: name, category, source (full path of source element), " +
        "destination (full path of destination element). " +
        "IMPORTANT – category workflow: call list_categories first; if no suitable connection " +
        "category exists (e.g. 'Electrical Cable', 'Pipe', 'Ventilation Duct'), call create_category first. " +
        "Source and destination must be exact full paths – use find_element to look them up if unsure. " +
        "Direction convention: source should be the supply/distribution side, " +
        "destination the consumer/endpoint – following the flow direction " +
        "(e.g. circuit breaker → socket, boiler → radiator, main pipe → tap). " +
        "Optional: route (text description of the physical path), length (in meters), " +
        "purpose, note, description. " +
        "Forbidden characters in name: *|<>?\" and tab.")]
    public static string CreateConnection(
        [Description("Connection name, e.g. 'NYM-J 3x1.5 Lighting circuit' or 'Cold water supply bathroom'")] string name,
        [Description("Object category: name or short name, e.g. 'Electrical Cable', 'Pipe'. Required!")] string category,
        [Description("Full path of the source element (where the line originates), e.g. 'House/GF/Distribution/Circuit-L1'")] string source,
        [Description("Full path of the destination element (where the line ends), e.g. 'House/GF/Living/Ceiling-Light'")] string destination,
        [Description("Route / physical path description, e.g. 'along north wall, through ceiling void' (optional)")] string? route = null,
        [Description("Length in meters (optional), e.g. 4.5")] decimal? length = null,
        [Description("Intended use, when not self-evident from the name (optional). Only fill with information the user explicitly provided.")] string? purpose = null,
        [Description("Temporary note or to-do during planning/construction – not for permanent records (optional). Only fill with information the user explicitly provided.")] string? note = null,
        [Description("Permanent technical information: material, specifications, installation details (optional). Only fill with information the user explicitly provided — do not generate or infer.")] string? description = null)
    {
        name        = Validate.NormalizeSingleline(name)?.Trim() ?? "";
        category    = category?.Trim()    ?? "";
        source      = string.IsNullOrWhiteSpace(source) ? "" : QueryHelpers.NormalizePath(source);
        destination = string.IsNullOrWhiteSpace(destination) ? "" : QueryHelpers.NormalizePath(destination);

        if (string.IsNullOrEmpty(name))        return "Error: 'name' is required.";
        if (string.IsNullOrEmpty(category))    return "Error: 'category' is required.";
        if (string.IsNullOrEmpty(source))      return "Error: 'source' is required.";
        if (string.IsNullOrEmpty(destination)) return "Error: 'destination' is required.";
        if (Validate.InvalidCharsConnection.IsMatch(name))
            return "Error: name contains invalid characters (*|<>?\" or tab).";

        route       = Validate.NormalizeMultiline(route);
        purpose     = Validate.NormalizeSingleline(purpose);
        note        = Validate.NormalizeSingleline(note);
        description = Validate.NormalizeMultiline(description);

        var lenErr = Validate.Length(name, "name", 150)
                  ?? Validate.Length(route?.Trim(), "route", 1000)
                  ?? Validate.Length(purpose?.Trim(), "purpose", 200)
                  ?? Validate.Length(note?.Trim(), "note", 200)
                  ?? Validate.Length(description?.Trim(), "description", 4000);
        if (lenErr != null) return lenErr;

        try
        {
            using var conn = FirebirdDb.OpenConnection();
            var (_, _, byFullName) = QueryHelpers.LoadEtree(conn);

            if (!QueryHelpers.TryResolveElementRow(byFullName, source, out var srcRow, out var sourceFullName))
                return $"Error: source element '{source}' not found.";
            if (!QueryHelpers.TryResolveElementRow(byFullName, destination, out var dstRow, out var destinationFullName))
                return $"Error: destination element '{destination}' not found.";

            var srcOid = srcRow.Str("Oid");
            var dstOid = dstRow.Str("Oid");

            var (categoryOid, catError) = QueryHelpers.ResolveCategoryOid(conn, category);
            if (catError != null) return catError;
            if (categoryOid == null)
                return $"Error: category '{category}' not found. Call list_categories for available categories.";

            var combError = QueryHelpers.CheckConnectionCombinationUniqueness(conn, name, categoryOid, srcOid, dstOid);
            if (combError != null) return combError;

            var hint = QueryHelpers.ConnectionSameSrcDstCategoryHint(conn, srcOid, dstOid, categoryOid);

            var oid = Guid.NewGuid().ToString("D");
            var now = DateTime.UtcNow;

            return FirebirdDb.RunInTransaction(conn, txn =>
            {
                FirebirdDb.ExecuteNonQuery(conn, txn, """
                    INSERT INTO "CEntity" ("Oid", "OptimisticLockField", "ObjectType", "CreatedOn", "CreatedBy",
                                          "Purpose", "Note", "Description")
                    VALUES (?, 0, ?, ?, ?, ?, ?, ?)
                    """, oid, XPObjectTypes.Connection, now, "HomeMemory",
                    (object?)purpose?.Trim()     ?? DBNull.Value,
                    (object?)note?.Trim()        ?? DBNull.Value,
                    (object?)description?.Trim() ?? DBNull.Value);

                FirebirdDb.ExecuteNonQuery(conn, txn, """
                    INSERT INTO "CItem" ("Oid", "Category") VALUES (?, ?)
                    """, oid, categoryOid);

                FirebirdDb.ExecuteNonQuery(conn, txn, """
                    INSERT INTO "Part" ("Oid", "PartType") VALUES (?, NULL)
                    """, oid);

                FirebirdDb.ExecuteNonQuery(conn, txn, """
                    INSERT INTO "Connection" ("Oid", "Name", "Source", "Destination", "Route", "Length")
                    VALUES (?, ?, ?, ?, ?, ?)
                    """,
                    oid, name, srcOid, dstOid,
                    (object?)route?.Trim() ?? DBNull.Value,
                    (object?)length        ?? DBNull.Value);

                return $"✓ Connection '{name}' created: {source} → {destination} (OID: {oid}).{hint}";
            });
        }
        catch (Exception ex)
        {
            return $"Error creating connection: {ex.Message}";
        }
    }

    // ── Tool: update_connection ───────────────────────────────────────────────

    [McpServerTool(Name = "update_connection")]
    [Description(
        "Updates an existing connection. Only provided fields are changed. " +
        "Identify the connection by its current name, optionally narrowed by source/destination " +
        "(required when multiple connections share the same name). " +
        "Pass 'CLEAR' to empty route, length, purpose, note, or description. " +
        "IMPORTANT: ALWAYS call get_connection_details before updating description, note, or purpose. " +
        "If the field already has content, inform the user and ask whether to replace or extend.")]
    public static string UpdateConnection(
        [Description("Current name of the connection to update")] string name,
        [Description("Full path of the current source element – narrows search if name is ambiguous (optional)")] string? source = null,
        [Description("Full path of the current destination element – narrows search if name is ambiguous (optional)")] string? destination = null,
        [Description("New name (optional). Forbidden characters: *|<>?\" and tab.")] string? new_name = null,
        [Description("New category: name or short name (cannot be cleared – required field)")] string? category = null,
        [Description("New source element: full path (optional)")] string? new_source = null,
        [Description("New destination element: full path (optional)")] string? new_destination = null,
        [Description("New route description ('CLEAR' to remove)")] string? route = null,
        [Description("New length in meters ('CLEAR' to remove), e.g. 4.5")] string? length = null,
        [Description("Intended use, when not self-evident from the name ('CLEAR' to remove)")] string? purpose = null,
        [Description("Temporary note or to-do during planning/construction – not for permanent records ('CLEAR' to remove)")] string? note = null,
        [Description("Permanent technical information: material, specifications, installation details ('CLEAR' to remove)")] string? description = null)
    {
        name = name?.Trim() ?? "";
        if (string.IsNullOrEmpty(name))
            return "Error: 'name' is required to identify the connection.";

        source = string.IsNullOrWhiteSpace(source) ? null : QueryHelpers.NormalizePath(source);
        destination = string.IsNullOrWhiteSpace(destination) ? null : QueryHelpers.NormalizePath(destination);

        route       = Validate.NormalizeClear(route);
        length      = Validate.NormalizeClear(length);
        purpose     = Validate.NormalizeClear(purpose);
        note        = Validate.NormalizeClear(note);
        description = Validate.NormalizeClear(description);

        route       = Validate.NormalizeMultiline(route);
        purpose     = Validate.NormalizeSingleline(purpose);
        note        = Validate.NormalizeSingleline(note);
        description = Validate.NormalizeMultiline(description);

        if (new_name != null)
        {
            new_name = Validate.NormalizeSingleline(new_name)?.Trim();
            if (string.IsNullOrEmpty(new_name))
                return "Error: 'new_name' cannot be empty.";
            if (Validate.InvalidCharsConnection.IsMatch(new_name))
                return "Error: new_name contains invalid characters (*|<>?\" or tab).";
        }

        var lenErr = Validate.Length(new_name, "new_name", 150)
                  ?? Validate.Length(route is not null and not "CLEAR" ? route.Trim() : null, "route", 1000)
                  ?? Validate.Length(purpose is not null and not "CLEAR" ? purpose.Trim() : null, "purpose", 200)
                  ?? Validate.Length(note is not null and not "CLEAR" ? note.Trim() : null, "note", 200)
                  ?? Validate.Length(description is not null and not "CLEAR" ? description.Trim() : null, "description", 4000);
        if (lenErr != null) return lenErr;

        try
        {
            using var conn = FirebirdDb.OpenConnection();

            string? srcOid = null, dstOid = null;
            if (source != null || destination != null)
            {
                var (_, _, byFullName) = QueryHelpers.LoadEtree(conn);
                if (source != null)
                {
                    if (!QueryHelpers.TryResolveElementRow(byFullName, source, out var srcRow, out _))
                        return $"Error: source element '{source}' not found.";
                    srcOid = srcRow.Str("Oid");
                }
                if (destination != null)
                {
                    if (!QueryHelpers.TryResolveElementRow(byFullName, destination, out var dstRow, out _))
                        return $"Error: destination element '{destination}' not found.";
                    dstOid = dstRow.Str("Oid");
                }
            }

            var findSql  = """SELECT "Oid" FROM "Connection" WHERE UPPER("Name") = ?""";
            var findArgs = new List<object?> { name.ToUpper() };
            if (srcOid != null) { findSql += """ AND "Source" = ?""";      findArgs.Add(srcOid); }
            if (dstOid != null) { findSql += """ AND "Destination" = ?"""; findArgs.Add(dstOid); }

            var matches = FirebirdDb.ExecuteQuery(conn, findSql, findArgs.ToArray());
            if (matches.Count == 0)
                return $"Error: connection '{name}' not found. " +
                       "Tip: provide source and/or destination to narrow the search.";
            if (matches.Count > 1)
                return $"Error: {matches.Count} connections named '{name}' found. " +
                       "Provide source and/or destination to narrow the search.";

            var oid = matches[0].Str("Oid");
            var now = DateTime.UtcNow;

            string? categoryOid = null;
            bool updateCategory = false;
            if (category != null)
            {
                if (category == "CLEAR")
                    return "Error: 'category' is required and cannot be cleared.";
                updateCategory = true;
                var (resolvedCatOid, catErr) = QueryHelpers.ResolveCategoryOid(conn, category.Trim());
                if (catErr != null) return catErr;
                if (resolvedCatOid == null)
                    return $"Error: category '{category}' not found. Call list_categories for available categories.";
                categoryOid = resolvedCatOid;
            }

            string? newSrcOid = null, newDstOid = null;
            if (new_source != null || new_destination != null)
            {
                var (_, _, byFullName2) = QueryHelpers.LoadEtree(conn);
                if (new_source != null)
                {
                    if (!QueryHelpers.TryResolveElementRow(byFullName2, new_source, out var row, out _))
                        return $"Error: new source element '{new_source}' not found.";
                    newSrcOid = row.Str("Oid");
                }
                if (new_destination != null)
                {
                    if (!QueryHelpers.TryResolveElementRow(byFullName2, new_destination, out var row, out _))
                        return $"Error: new destination element '{new_destination}' not found.";
                    newDstOid = row.Str("Oid");
                }
            }

            bool needsCombCheck = new_name != null || updateCategory || newSrcOid != null || newDstOid != null;
            if (needsCombCheck)
            {
                var curConnRows = FirebirdDb.ExecuteQuery(conn, """
                    SELECT c."Name", c."Source", c."Destination", ci."Category"
                    FROM "Connection" c
                    JOIN "CItem" ci ON ci."Oid" = c."Oid"
                    WHERE c."Oid" = ?
                    """, oid);
                if (curConnRows.Count > 0)
                {
                    var cur       = curConnRows[0];
                    var checkName = new_name    ?? cur.Str("Name");
                    var checkCat  = categoryOid ?? cur.Str("Category");
                    var checkSrc  = newSrcOid   ?? cur.Str("Source");
                    var checkDst  = newDstOid   ?? cur.Str("Destination");
                    var combError = QueryHelpers.CheckConnectionCombinationUniqueness(conn, checkName, checkCat, checkSrc, checkDst, oid);
                    if (combError != null) return combError;
                }
            }

            decimal? newLength  = null;
            bool     updateLength = false;
            if (length != null)
            {
                updateLength = true;
                if (length != "CLEAR")
                {
                    if (!decimal.TryParse(length.Trim(),
                            System.Globalization.NumberStyles.Number,
                            System.Globalization.CultureInfo.InvariantCulture,
                            out var parsed))
                        return $"Error: '{length}' is not a valid number for length.";
                    newLength = parsed;
                }
            }

            var overwriteAdvisories = QueryHelpers.CollectOverwriteAdvisories(
                conn, oid, description, note, purpose);

            return FirebirdDb.RunInTransaction(conn, txn =>
            {
                FirebirdDb.ExecuteNonQuery(conn, txn, """
                    UPDATE "CEntity" SET
                        "OptimisticLockField" = COALESCE("OptimisticLockField", 0) + 1,
                        "UpdatedOn" = ?,
                        "UpdatedBy" = ?
                    WHERE "Oid" = ?
                    """, now, "HomeMemory", oid);

                if (updateCategory)
                    FirebirdDb.ExecuteNonQuery(conn, txn,
                        """UPDATE "CItem" SET "Category" = ? WHERE "Oid" = ?""",
                        (object?)categoryOid ?? DBNull.Value, oid);

                if (new_name != null)
                    FirebirdDb.ExecuteNonQuery(conn, txn,
                        """UPDATE "Connection" SET "Name" = ? WHERE "Oid" = ?""",
                        new_name, oid);

                if (newSrcOid != null)
                    FirebirdDb.ExecuteNonQuery(conn, txn,
                        """UPDATE "Connection" SET "Source" = ? WHERE "Oid" = ?""",
                        newSrcOid, oid);

                if (newDstOid != null)
                    FirebirdDb.ExecuteNonQuery(conn, txn,
                        """UPDATE "Connection" SET "Destination" = ? WHERE "Oid" = ?""",
                        newDstOid, oid);

                if (route != null)
                    FirebirdDb.ExecuteNonQuery(conn, txn,
                        """UPDATE "Connection" SET "Route" = ? WHERE "Oid" = ?""",
                        route == "CLEAR" ? DBNull.Value : (object)route.Trim(), oid);

                if (updateLength)
                    FirebirdDb.ExecuteNonQuery(conn, txn,
                        """UPDATE "Connection" SET "Length" = ? WHERE "Oid" = ?""",
                        newLength.HasValue ? (object)newLength.Value : DBNull.Value, oid);

                if (purpose != null)
                    FirebirdDb.ExecuteNonQuery(conn, txn,
                        """UPDATE "CEntity" SET "Purpose" = ? WHERE "Oid" = ?""",
                        purpose == "CLEAR" ? DBNull.Value : (object)purpose.Trim(), oid);

                if (note != null)
                    FirebirdDb.ExecuteNonQuery(conn, txn,
                        """UPDATE "CEntity" SET "Note" = ? WHERE "Oid" = ?""",
                        note == "CLEAR" ? DBNull.Value : (object)note.Trim(), oid);

                if (description != null)
                    FirebirdDb.ExecuteNonQuery(conn, txn,
                        """UPDATE "CEntity" SET "Description" = ? WHERE "Oid" = ?""",
                        description == "CLEAR" ? DBNull.Value : (object)description.Trim(), oid);

                var displayName = new_name ?? name;
                var result = $"✓ Connection '{displayName}' updated.";
                foreach (var adv in overwriteAdvisories)
                    result += $"\n  Advisory: {adv}.";
                return result;
            });
        }
        catch (Exception ex)
        {
            return $"Error updating connection: {ex.Message}";
        }
    }

    // ── Tool: delete_connection ───────────────────────────────────────────────

    [McpServerTool(Name = "delete_connection")]
    [Description(
        "Deletes a connection. Search is by name, optionally narrowed by source or destination " +
        "(use when multiple connections share the same name).")]
    public static string DeleteConnection(
        [Description("Connection name, e.g. 'NYM-J 3x1.5 Lighting'")] string name,
        [Description("Full path of the source element to narrow the search (optional)")] string? source = null,
        [Description("Full path of the destination element to narrow the search (optional)")] string? destination = null)
    {
        name = name?.Trim() ?? "";
        if (string.IsNullOrEmpty(name))
            return "Error: 'name' is required.";

        source = string.IsNullOrWhiteSpace(source) ? null : QueryHelpers.NormalizePath(source);
        destination = string.IsNullOrWhiteSpace(destination) ? null : QueryHelpers.NormalizePath(destination);

        try
        {
            using var conn = FirebirdDb.OpenConnection();

            string? srcOid = null, dstOid = null;
            if (source != null || destination != null)
            {
                var (_, _, byFullName) = QueryHelpers.LoadEtree(conn);
                if (source != null)
                {
                    if (!QueryHelpers.TryResolveElementRow(byFullName, source, out var srcRow, out _))
                        return $"Error: source element '{source}' not found.";
                    srcOid = srcRow.Str("Oid");
                }
                if (destination != null)
                {
                    if (!QueryHelpers.TryResolveElementRow(byFullName, destination, out var dstRow, out _))
                        return $"Error: destination element '{destination}' not found.";
                    dstOid = dstRow.Str("Oid");
                }
            }

            var sql  = """SELECT "Oid" FROM "Connection" WHERE UPPER("Name") = ?""";
            var args = new List<object?> { name.ToUpper() };
            if (srcOid != null) { sql += """ AND "Source" = ?""";      args.Add(srcOid); }
            if (dstOid != null) { sql += """ AND "Destination" = ?"""; args.Add(dstOid); }

            var matches = FirebirdDb.ExecuteQuery(conn, sql, args.ToArray());
            if (matches.Count == 0)
                return $"Error: connection '{name}' not found. " +
                       "Tip: provide source and/or destination to narrow the search.";
            if (matches.Count > 1)
                return $"Error: {matches.Count} connections named '{name}' found. " +
                       "Provide source and/or destination to narrow the search.";

            var oid = matches[0].Str("Oid");

            var docError = QueryHelpers.CheckDocumentsAttached(conn, oid);
            if (docError != null) return docError.Replace("element", "connection");

            return FirebirdDb.RunInTransaction(conn, txn =>
            {
                FirebirdDb.ExecuteNonQuery(conn, txn, """DELETE FROM "Connection"     WHERE "Oid"   = ?""", oid);
                FirebirdDb.ExecuteNonQuery(conn, txn, """DELETE FROM "Part"           WHERE "Oid"   = ?""", oid);
                // Remove image associations before CItem (FK has no CASCADE).
                // Table may not exist in all DB versions – skip silently (mirrors CollectDeleteAdvisories read-path).
                try { FirebirdDb.ExecuteNonQuery(conn, txn, """DELETE FROM "ImagesToCItems" WHERE "CItem" = ?""", oid); }
                catch (FbException ex) when (ex.ErrorCode is 335544580 or 335544569) { /* table absent – skip */ }
                FirebirdDb.ExecuteNonQuery(conn, txn, """DELETE FROM "CItem"          WHERE "Oid"   = ?""", oid);
                FirebirdDb.ExecuteNonQuery(conn, txn, """DELETE FROM "CEntity"        WHERE "Oid"   = ?""", oid);
                return $"✓ Connection '{name}' deleted.";
            });
        }
        catch (Exception ex)
        {
            return $"Error deleting connection: {ex.Message}";
        }
    }
}
