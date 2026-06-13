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
        "With searchAllFields=true, searchTerm also matches Route, Purpose, Note, Description, and UserManual – " +
        "useful when a keyword appears in a field other than the connection name. " +
        "With status: filter by status type name (Existing / Planned / Removed – language-independent) " +
        "or by status name (exact match first, partial match as fallback). " +
        "'Existing' also includes connections with no status set, since most existing things carry no explicit status. " +
        "Returns up to 100 connections; refine with category, under, searchTerm, or status for complete results. " +
        "Connections with status Planned or Removed are marked with their status name. " +
        "Note: conduit/cable endpoints may also be documented as elements – " +
        "combine with find_element(searchTerm) for a complete picture.")]
    public static string GetConnections(
        [Description("Connection category: exact name or short name (e.g. 'Cable'), full path with '/' (e.g. 'Electrical/Cable'). Partial match as fallback; if ambiguous, an error lists full paths. Includes all subcategories.")] string category = "",
        [Description("Spatial filter: connections whose source or destination is under this path.")] string under = "",
        [Description("Filter by connection name (partial match, case-insensitive), e.g. 'conduit' or 'leerrohr'.")] string searchTerm = "",
        [Description("Also search in Route, Purpose, Note, Description, and UserManual. Default: false (name only).")] bool? searchAllFields = null,
        [Description("Filter by status type name (Existing / Planned / Removed) or status name. 'Existing' also includes connections with no status set. Call list_statuses for options.")] string status = "")
    {
        category   = category.Trim();
        under      = under.Trim().TrimEnd('/');
        searchTerm = searchTerm.Trim();
        status     = status.Trim();

        if (string.IsNullOrEmpty(category) && string.IsNullOrEmpty(under) && string.IsNullOrEmpty(searchTerm) && string.IsNullOrEmpty(status))
            return "Error: provide at least one of category, searchTerm, under, or status.";

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
                var (catResolution, catError) = QueryHelpers.ResolveCategoryOidsWithDescendants(conn, category);
                if (catError is not null) return catError;
                catOids = catResolution?.Oids;
                if (catOids is null)
                    return $"Error: category '{category}' not found. Call list_categories for available category names.";
            }

            var detailsSelect = searchAllFields == true
                ? """, ce."Purpose", ce."Note", ce."Description", ce."UserManual" """
                : "";
            var sql = new StringBuilder($"""
                SELECT c."Name", c."Source", c."Destination", c."Route", c."Length",
                       cat."Name" AS CATNAME, ci."Category" AS CAT_OID,
                       s."Name" AS STATUSNAME, s."StatusType" AS STATUSTYPE{detailsSelect}
                FROM "Connection" c
                JOIN "CItem"    ci  ON ci."Oid"  = c."Oid"
                JOIN "Category" cat ON cat."Oid" = ci."Category"
                LEFT JOIN "CEntity" ce ON ce."Oid" = c."Oid"
                LEFT JOIN "Status"  s  ON s."Oid"  = ce."Status"
                WHERE 1=1
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
                               " OR UPPER(ce.\"Description\") LIKE ?" +
                               " OR UPPER(ce.\"UserManual\") LIKE ?)");
                    paramList.Add(term); paramList.Add(term);
                    paramList.Add(term); paramList.Add(term);
                    paramList.Add(term); paramList.Add(term);
                }
                else
                {
                    sql.Append(" AND UPPER(c.\"Name\") LIKE ?");
                    paramList.Add(term);
                }
            }
            if (!string.IsNullOrEmpty(status))
            {
                // Priority: 1) English type keywords, 2) exact status name, 3) LIKE fallback. Mirrors find_element.
                int? statusTypeFilter = status.ToLowerInvariant() switch
                {
                    "existing"                    => 0,
                    "planned"                     => 1,
                    "removed" or "decommissioned" => 2,
                    _                             => (int?)null
                };
                if (statusTypeFilter.HasValue)
                {
                    // "Existing" (type 0) also matches connections with no status set: a missing status
                    // is the normal case for things that simply exist (ce."Status" IS NULL via LEFT JOIN).
                    // Planned/Removed still require an explicit status.
                    sql.Append(statusTypeFilter.Value == 0
                        ? " AND (ce.\"Status\" IS NULL OR s.\"StatusType\" = ?)"
                        : " AND s.\"StatusType\" = ?");
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
                        sql.Append(" AND UPPER(s.\"Name\") = UPPER(?)");
                        paramList.Add(status);
                    }
                    else
                    {
                        sql.Append(" AND UPPER(s.\"Name\") LIKE UPPER(?)");
                        paramList.Add($"%{status.ToUpperInvariant()}%");
                    }
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

            // Sort by source first, then name. Concatenating srcFn + name would interleave
            // sources whose paths are prefixes of one another (e.g. "Office" vs "Office2"),
            // which then prints the same source header more than once.
            results.Sort((a, b) =>
            {
                var bySrc = string.Compare(a.srcFn, b.srcFn, StringComparison.OrdinalIgnoreCase);
                return bySrc != 0
                    ? bySrc
                    : string.Compare(a.row.Str("Name"), b.row.Str("Name"), StringComparison.OrdinalIgnoreCase);
            });

            var totalCount = results.Count;
            var truncated = totalCount > 100;
            if (truncated) results = results.Take(100).ToList();

            var scope   = !string.IsNullOrEmpty(under)      ? $" under '{under}'"        : "";
            var catLbl  = !string.IsNullOrEmpty(category)   ? $"'{category}'"            : "all categories";
            var termLbl = !string.IsNullOrEmpty(searchTerm)
                ? (searchAllFields == true ? $" ~'{searchTerm}'" : $" name~'{searchTerm}'")
                : "";
            var stLbl   = !string.IsNullOrEmpty(status) ? $" with status '{status}'" : "";

            if (results.Count == 0)
            {
                var tip = !string.IsNullOrEmpty(category) ? " Tip: call list_categories for available category names." : "";
                return $"No connections found for {catLbl}{termLbl}{stLbl}{scope}.{tip}";
            }

            var lines = new List<string> { $"Connections {catLbl}{termLbl}{stLbl}{scope} ({results.Count}{(truncated ? $" of {totalCount}" : "")}):\n" };

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
                var st     = r.GetValueOrDefault("STATUSTYPE");
                var stHint = st is not null and not DBNull && Convert.ToInt32(st) is 1 or 2
                    ? $"  {{{r.Str("STATUSNAME")}}}"
                    : "";
                lines.Add($"    --> {dst}  [{name}]{detail}{stHint}");
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
                            r.Str("UserManual").ToUpperInvariant().Contains(termUp)  ? ("UserManual",  r.Str("UserManual"))  :
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
                lines.Add($"\n(Showing first 100 of {totalCount} connections – narrow with category, under, searchTerm, or status.)");
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
        "Full details of a single connection: category, status, source, destination, route, length, " +
        "purpose, note, description, and user manual. " +
        "Identify by name, optionally narrowed by source or destination when multiple connections share the same name. " +
        "update_connection requires calling this first before modifying description, note, purpose, or user_manual.")]
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

            var findSql  = """SELECT c."Oid" FROM "Connection" c WHERE UPPER(c."Name") = UPPER(?)""";
            var findArgs = new List<object?> { name };
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
                       ce."Purpose", ce."Note", ce."Description", ce."UserManual",
                       cat."Name" AS CategoryName,
                       s."Name"   AS StatusName,
                       pt."Name"  AS PartTypeName
                FROM "Connection" c
                JOIN "CEntity"  ce  ON ce."Oid"  = c."Oid"
                JOIN "CItem"    ci  ON ci."Oid"   = c."Oid"
                JOIN "Category" cat ON cat."Oid"  = ci."Category"
                LEFT JOIN "Status"   s  ON s."Oid"   = ce."Status"
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
            var status = d.Str("StatusName");
            if (!string.IsNullOrEmpty(status))      lines.Add($"  Status      : {status}");
            var partType = d.Str("PartTypeName");
            if (!string.IsNullOrEmpty(partType))    lines.Add($"  Part type   : {partType}");
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

            var userManual = d.Str("UserManual");
            if (!string.IsNullOrEmpty(userManual))  lines.Add($"  User manual : {userManual}");

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
        "status (e.g. 'Planned', 'Removed' – call list_statuses), purpose, note, description, user_manual. " +
        "Field choice: short temporary to-do -> note (single line, 200 chars); permanent technical info -> description (multiline, 4000 chars, use paragraph breaks for multi-section content); end-user instructions -> user_manual (multiline, 4000 chars). " +
        "Forbidden characters in name: *|<>?\" and tab.")]
    public static string CreateConnection(
        [Description("Connection name, e.g. 'NYM-J 3x1.5 Lighting circuit' or 'Cold water supply bathroom'")] string name,
        [Description("Object category: name, short name, or full path (e.g. 'Electrical Cable', 'Pipe', or 'Electrical/Cable' when the name is ambiguous). Required!")] string category,
        [Description("Full path of the source element (where the line originates), e.g. 'House/GF/Distribution/Circuit-L1'")] string source,
        [Description("Full path of the destination element (where the line ends), e.g. 'House/GF/Living/Ceiling-Light'")] string destination,
        [Description("Route / physical path description, e.g. 'along north wall, through ceiling void' (optional)")] string? route = null,
        [Description("Length in meters (optional), e.g. 4.5")] decimal? length = null,
        [Description("Status name (optional). Most connections need no status – omit for normal existing lines. Set only when the user mentions a status like 'Planned' (new line not yet pulled) or 'Removed' (decommissioned). Call list_statuses for options.")] string? status = null,
        [Description("Intended use, when not self-evident from the name (optional). Only fill with information the user explicitly provided.")] string? purpose = null,
        [Description("Short temporary to-do during planning/construction (optional). For permanent technical info use description instead. Only fill with information the user explicitly provided.")] string? note = null,
        [Description("Permanent technical information (default for longer-lived info): material, specifications, installation details (optional). Only fill with information the user explicitly provided — do not generate or infer.")] string? description = null,
        [Description("User-facing information: operating instructions, maintenance schedule, troubleshooting tips (optional). Only fill with information the user explicitly provided.")] string? user_manual = null)
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
        user_manual = Validate.NormalizeMultiline(user_manual);

        var lenErr = Validate.Length(name, "name", 150)
                  ?? Validate.Length(route?.Trim(), "route", 1000)
                  ?? Validate.Length(purpose?.Trim(), "purpose", 200)
                  ?? Validate.Length(note?.Trim(), "note", 200, "For permanent or longer information, use description instead.")
                  ?? Validate.Length(description?.Trim(), "description", 4000)
                  ?? Validate.Length(user_manual?.Trim(), "user_manual", 4000);
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

            string? statusOid = null;
            if (!string.IsNullOrWhiteSpace(status))
            {
                statusOid = QueryHelpers.ResolveStatusOid(conn, status);
                if (statusOid == null)
                    return $"Error: status '{status}' not found. Call list_statuses for available statuses.";
            }

            var hint = QueryHelpers.ConnectionSameSrcDstCategoryHint(conn, srcOid, dstOid, categoryOid);

            var oid = Guid.NewGuid().ToString("D");
            var now = DateTime.UtcNow;

            return FirebirdDb.RunInTransaction(conn, txn =>
            {
                FirebirdDb.ExecuteNonQuery(conn, txn, """
                    INSERT INTO "CEntity" ("Oid", "OptimisticLockField", "ObjectType", "CreatedOn", "CreatedBy",
                                          "Status", "Purpose", "Note", "Description", "UserManual")
                    VALUES (?, 0, ?, ?, ?, ?, ?, ?, ?, ?)
                    """, oid, XPObjectTypes.Connection, now, "HomeMemory",
                    (object?)statusOid           ?? DBNull.Value,
                    (object?)purpose?.Trim()     ?? DBNull.Value,
                    (object?)note?.Trim()        ?? DBNull.Value,
                    (object?)description?.Trim() ?? DBNull.Value,
                    (object?)user_manual?.Trim() ?? DBNull.Value);

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
        "Pass 'CLEAR' to empty route, length, status, purpose, note, description, or user_manual. " +
        "IMPORTANT: ALWAYS call get_connection_details before updating description, note, purpose, or user_manual. " +
        "If the field already has content, inform the user and ask whether to replace or extend. " +
        "Field choice: short temporary to-do -> note (single line, 200 chars); permanent technical info -> description (multiline, 4000 chars, use paragraph breaks for multi-section content); end-user instructions -> user_manual (multiline, 4000 chars).")]
    public static string UpdateConnection(
        [Description("Current name of the connection to update")] string name,
        [Description("Full path of the current source element – narrows search if name is ambiguous (optional)")] string? source = null,
        [Description("Full path of the current destination element – narrows search if name is ambiguous (optional)")] string? destination = null,
        [Description("New name (optional). Forbidden characters: *|<>?\" and tab.")] string? new_name = null,
        [Description("New category: name, short name, or full path (e.g. 'Electrical/Cable' when the name is ambiguous). Cannot be cleared – required field.")] string? category = null,
        [Description("New source element: full path (optional)")] string? new_source = null,
        [Description("New destination element: full path (optional)")] string? new_destination = null,
        [Description("New route description ('CLEAR' to remove)")] string? route = null,
        [Description("New length in meters ('CLEAR' to remove), e.g. 4.5")] string? length = null,
        [Description("New status name ('CLEAR' to remove). Set only when the user mentions a status like 'Planned' or 'Removed'. Call list_statuses for options.")] string? status = null,
        [Description("Intended use, when not self-evident from the name ('CLEAR' to remove)")] string? purpose = null,
        [Description("Short temporary to-do during planning/construction. For permanent technical info use description instead ('CLEAR' to remove)")] string? note = null,
        [Description("Permanent technical information (default for longer-lived info): material, specifications, installation details ('CLEAR' to remove)")] string? description = null,
        [Description("User-facing information: operating instructions, maintenance schedule, troubleshooting tips ('CLEAR' to remove)")] string? user_manual = null)
    {
        name = name?.Trim() ?? "";
        if (string.IsNullOrEmpty(name))
            return "Error: 'name' is required to identify the connection.";

        source = string.IsNullOrWhiteSpace(source) ? null : QueryHelpers.NormalizePath(source);
        destination = string.IsNullOrWhiteSpace(destination) ? null : QueryHelpers.NormalizePath(destination);
        new_source = string.IsNullOrWhiteSpace(new_source) ? null : QueryHelpers.NormalizePath(new_source);
        new_destination = string.IsNullOrWhiteSpace(new_destination) ? null : QueryHelpers.NormalizePath(new_destination);

        route       = Validate.NormalizeClear(route);
        length      = string.IsNullOrWhiteSpace(length) ? null : Validate.NormalizeClear(length);
        status      = Validate.NormalizeClear(status);
        purpose     = Validate.NormalizeClear(purpose);
        note        = Validate.NormalizeClear(note);
        description = Validate.NormalizeClear(description);
        user_manual = Validate.NormalizeClear(user_manual);

        // source/destination only narrow the lookup; they are not updates. Require at least one real change.
        if (new_name == null && category == null && new_source == null && new_destination == null
            && route == null && length == null && status == null && purpose == null
            && note == null && description == null && user_manual == null)
            return "Error: provide at least one of new_name, category, new_source, new_destination, route, length, status, purpose, note, description, user_manual.";

        route       = Validate.NormalizeMultiline(route);
        purpose     = Validate.NormalizeSingleline(purpose);
        note        = Validate.NormalizeSingleline(note);
        description = Validate.NormalizeMultiline(description);
        user_manual = Validate.NormalizeMultiline(user_manual);

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
                  ?? Validate.Length(note is not null and not "CLEAR" ? note.Trim() : null, "note", 200, "For permanent or longer information, use description instead.")
                  ?? Validate.Length(description is not null and not "CLEAR" ? description.Trim() : null, "description", 4000)
                  ?? Validate.Length(user_manual is not null and not "CLEAR" ? user_manual.Trim() : null, "user_manual", 4000);
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

            var findSql  = """SELECT "Oid" FROM "Connection" WHERE UPPER("Name") = UPPER(?)""";
            var findArgs = new List<object?> { name };
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

            var curConnRows = FirebirdDb.ExecuteQuery(conn, """
                SELECT c."Name", c."Source", c."Destination", ci."Category"
                FROM "Connection" c
                JOIN "CItem" ci ON ci."Oid" = c."Oid"
                WHERE c."Oid" = ?
                """, oid);
            if (curConnRows.Count == 0)
                return "Error: connection data not found.";
            var curConn = curConnRows[0];

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
                var checkName = new_name    ?? curConn.Str("Name");
                var checkCat  = categoryOid ?? curConn.Str("Category");
                var checkSrc  = newSrcOid   ?? curConn.Str("Source");
                var checkDst  = newDstOid   ?? curConn.Str("Destination");
                var combError = QueryHelpers.CheckConnectionCombinationUniqueness(conn, checkName, checkCat, checkSrc, checkDst, oid);
                if (combError != null) return combError;
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

            string? statusOid = null;
            bool updateStatus = false;
            if (status != null)
            {
                updateStatus = true;
                if (status != "CLEAR")
                {
                    statusOid = QueryHelpers.ResolveStatusOid(conn, status.Trim());
                    if (statusOid == null)
                        return $"Error: status '{status}' not found. Call list_statuses for available statuses.";
                }
            }

            var overwriteAdvisories = QueryHelpers.CollectOverwriteAdvisories(
                conn, oid, description, note, purpose, user_manual);

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

                QueryHelpers.SetCEntityField(conn, txn, oid, "Purpose",     purpose);
                QueryHelpers.SetCEntityField(conn, txn, oid, "Note",        note);
                QueryHelpers.SetCEntityField(conn, txn, oid, "Description", description);
                QueryHelpers.SetCEntityField(conn, txn, oid, "UserManual",  user_manual);

                if (updateStatus)
                    FirebirdDb.ExecuteNonQuery(conn, txn,
                        """UPDATE "CEntity" SET "Status" = ? WHERE "Oid" = ?""",
                        status == "CLEAR" ? DBNull.Value : (object)statusOid!, oid);

                var displayName = new_name ?? curConn.Str("Name");
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
        "(use when multiple connections share the same name). " +
        "Blocked if documents are attached – detach them first.")]
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

            var sql  = """SELECT "Oid" FROM "Connection" WHERE UPPER("Name") = UPPER(?)""";
            var args = new List<object?> { name };
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

            var advisories = QueryHelpers.CollectDeleteAdvisories(conn, oid)
                .Select(a => a.Replace("element", "connection")).ToList();

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
                var result = $"✓ Connection '{name}' deleted.";
                if (advisories.Count > 0)
                    result += $"\n  Advisory: {string.Join("; ", advisories)}.";
                return result;
            });
        }
        catch (Exception ex)
        {
            return $"Error deleting connection: {ex.Message}";
        }
    }
}
