using System.ComponentModel;
using ModelContextProtocol.Server;
using HomeMemory.MCP.Db;

namespace HomeMemory.MCP.Tools;

[McpServerToolType]
public static class ElementTools
{

    // ── get_element_details ───────────────────────────────────────────────────

    [McpServerTool(Name = "get_element_details", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description(
        "Full details of a single element: properties (category, status, part type, " +
        "purpose, note, description, user manual), direct child elements, " +
        "and all incoming and outgoing connections (physical lines: pipes, cables, ducts). " +
        "Identify the element by exact full path or OID. Use when you need details or connections. " +
        "An element is any physical item in the building: installed equipment, appliance, " +
        "furniture, fixture, tool, or location container (room, floor, etc.). " +
        "Returns the stable OID, version, creation metadata and, when available, last-update metadata; records imported from external tools may lack audit data.")]
    public static string GetElementDetails(
        [Description("Full name path, e.g. 'House/GF/Kitchen/South-Wall/Socket'. Optional when oid is provided.")] string fullname = "",
        [Description("Stable element OID returned by create_element or this tool. Optional when fullname is provided.")] string? oid = null)
    {
        var fn = fullname?.Trim() ?? "";

        try
        {
            using var conn = FirebirdDb.OpenConnection();

            var (allElements, byOid, byFullName) = QueryHelpers.LoadEtree(conn);
            var resolution = QueryHelpers.ResolveElementSelector(byOid, byFullName, fn, oid);
            if (resolution.error != null)
                return string.IsNullOrWhiteSpace(oid)
                    && resolution.error.Contains("not found", StringComparison.OrdinalIgnoreCase)
                    ? resolution.error + " Use find_element to search for the correct path."
                    : resolution.error;
            fn = resolution.canonicalFullName;

            allElements = FirebirdDb.ExecuteQuery(conn,
                $"{SqlQueries.EtreeCte} SELECT \"Oid\", FULLNAME, \"Name\", \"ShortName\", \"Position\" FROM ETREE");

            var oidToRow = allElements.ToDictionary(
                r => FirebirdDb.OidKey(r["Oid"]),
                r => r,
                StringComparer.OrdinalIgnoreCase);

            var targetOidKey = FirebirdDb.OidKey(resolution.row!["Oid"]);
            var target = allElements.FirstOrDefault(r =>
                FirebirdDb.OidKey(r["Oid"]).Equals(targetOidKey, StringComparison.OrdinalIgnoreCase));

            if (target is null)
                return $"Error: element '{fn}' not found. Use find_element to search for the correct path.";

            var targetOid = target["Oid"];
            var oidKey = FirebirdDb.OidKey(targetOid);
            var lines  = new List<string> { $"Element: {target.Str("FULLNAME")}\n" };

            lines.Add($"  OID         : {FirebirdDb.Str(targetOid)}");
            lines.Add($"  Name        : {target.Str("Name")}");
            var sn = target.Str("ShortName");
            if (!string.IsNullOrEmpty(sn)) lines.Add($"  Short name  : {sn}");
            var pos = target.Str("Position");
            if (!string.IsNullOrEmpty(pos)) lines.Add($"  Position    : {pos}");

            var ext = FirebirdDb.ExecuteQuery(conn, """
                SELECT
                    ce."Purpose",
                    ce."Note",
                    ce."Description",
                    ce."UserManual",
                    ce."CreatedOn",
                    ce."CreatedBy",
                    ce."UpdatedOn",
                    ce."UpdatedBy",
                    ce."OptimisticLockField",
                    s."Name"   AS StatusName,
                    cat."Name" AS CategoryName,
                    pt."Name"  AS PartTypeName
                FROM "CEntity" ce
                LEFT JOIN "Status"   s   ON s."Oid"   = ce."Status"
                LEFT JOIN "CItem"    ci  ON ci."Oid"   = ce."Oid"
                LEFT JOIN "Category" cat ON cat."Oid"  = ci."Category"
                LEFT JOIN "Part"     p   ON p."Oid"    = ce."Oid"
                LEFT JOIN "PartType" pt  ON pt."Oid"   = p."PartType"
                WHERE ce."Oid" = ?
                """, targetOid);

            if (ext.Count > 0)
            {
                var e = ext[0];
                lines.Add($"  Version     : {e.Int("OptimisticLockField")}");
                if (!string.IsNullOrEmpty(e.Str("CategoryName")))
                    lines.Add($"  Category    : {e.Str("CategoryName")}");
                if (!string.IsNullOrEmpty(e.Str("StatusName")))
                    lines.Add($"  Status      : {e.Str("StatusName")}");
                if (!string.IsNullOrEmpty(e.Str("PartTypeName")))
                    lines.Add($"  Part type   : {e.Str("PartTypeName")}");
                if (!string.IsNullOrEmpty(e.Str("Purpose")))
                    lines.Add($"  Purpose     : {e.Str("Purpose")}");
                if (!string.IsNullOrEmpty(e.Str("Note")))
                    lines.Add($"  Note        : {e.Str("Note")}");
                if (!string.IsNullOrEmpty(e.Str("Description")))
                    lines.Add($"  Description : {e.Str("Description")}");
                if (!string.IsNullOrEmpty(e.Str("UserManual")))
                    lines.Add($"  User manual : {e.Str("UserManual")}");

                lines.AddRange(QueryHelpers.FormatAuditLines(e, 12));
            }

            var children = FirebirdDb.ExecuteQuery(conn, """
                SELECT "Oid", "Name", "SortIndex"
                FROM "Element"
                WHERE "PartOfElement" = ?
                ORDER BY "SortIndex" NULLS LAST, "Name"
                """, targetOid);

            var fnPrefix = fn.TrimEnd('/') + "/";
            int totalDescendants = allElements.Count(r =>
                r.Str("FULLNAME").StartsWith(fnPrefix, StringComparison.OrdinalIgnoreCase));

            if (children.Count > 0)
            {
                var suffix = totalDescendants > children.Count ? $", {totalDescendants} total" : "";
                lines.Add($"\n  Child elements ({children.Count} direct{suffix}):");
                foreach (var c in children)
                {
                    var childFull = oidToRow.TryGetValue(FirebirdDb.OidKey(c["Oid"]), out var cr)
                        ? cr.Str("FULLNAME")
                        : c.Str("Name");
                    lines.Add($"    +-- {childFull}");
                }
            }

            var connOut = FirebirdDb.ExecuteQuery(conn, """
                SELECT "Name", "Destination", "Route", "Length"
                FROM "Connection"
                WHERE "Source" = ?
                ORDER BY "Name"
                """, targetOid);

            if (connOut.Count > 0)
            {
                lines.Add($"\n  Outgoing connections ({connOut.Count}):");
                foreach (var c in connOut)
                {
                    var destFull = oidToRow.TryGetValue(FirebirdDb.OidKey(c.GetValueOrDefault("Destination")), out var dr)
                        ? dr.Str("FULLNAME")
                        : c.Str("Destination");
                    var length = c.GetValueOrDefault("Length");
                    var route  = c.Str("Route");
                    var line   = $"    --> {c.Str("Name")}  =>  {destFull}";
                    if (length is not null and not DBNull) line += $"  ({length} m)";
                    lines.Add(line);
                    if (!string.IsNullOrEmpty(route)) lines.Add($"      Route: {route}");
                }
            }

            var connIn = FirebirdDb.ExecuteQuery(conn, """
                SELECT "Name", "Source", "Route", "Length"
                FROM "Connection"
                WHERE "Destination" = ?
                ORDER BY "Name"
                """, targetOid);

            if (connIn.Count > 0)
            {
                lines.Add($"\n  Incoming connections ({connIn.Count}):");
                foreach (var c in connIn)
                {
                    var srcFull = oidToRow.TryGetValue(FirebirdDb.OidKey(c.GetValueOrDefault("Source")), out var sr)
                        ? sr.Str("FULLNAME")
                        : c.Str("Source");
                    var length = c.GetValueOrDefault("Length");
                    var route  = c.Str("Route");
                    var line   = $"    <-- {srcFull}  <=  {c.Str("Name")}";
                    if (length is not null and not DBNull) line += $"  ({length} m)";
                    lines.Add(line);
                    if (!string.IsNullOrEmpty(route)) lines.Add($"      Route: {route}");
                }
            }

            if (children.Count == 0 && connOut.Count == 0 && connIn.Count == 0)
                lines.Add("\n  (No child elements or connections)");

            return string.Join("\n", lines);
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    // ── create_element ────────────────────────────────────────────────────────

    [McpServerTool(Name = "create_element", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description(
        "Creates a new element. An element is any physical item in the building: " +
        "installed equipment (socket, boiler, radiator, circuit breaker), appliances (washing machine, fridge), " +
        "furniture (sofa, wardrobe), fixtures, tools, or location containers (room, floor, garage, outdoor area, wall area). " +
        "Primary area elements (the main location containers shown in the default structure overview, e.g. room, floor, garage, outdoor area) are also created with this tool – " +
        "simply choose a category marked [primary area] in list_categories (is_primary_area=true). " +
        "Required fields: name, category. " +
        "IMPORTANT – category workflow: call list_categories first to find the best matching category " +
        "by name or context (e.g. 'Heating' for a boiler, 'Electrical' for a socket, 'Room' for a room). " +
        "If no suitable category exists, call create_category first, then use the new category here. " +
        "IMPORTANT – parent path: if the parent element's full path is not known exactly, " +
        "call get_structure_overview or find_element first to find the correct path. Do not guess paths. " +
        "Use the parent path for location; do not repeat parent context in the element name. " +
        "Optional: parent (full path of the parent element), " +
        "short_name, status, purpose, note, description, user_manual, position. " +
        "If both this element and its parent have explicit statuses, a conflicting child status is reported as an advisory. " +
        "Field choice: short temporary to-do -> note (single line, 200 chars); permanent technical info -> description (multiline, 4000 chars, use paragraph breaks for multi-section content); end-user instructions -> user_manual (multiline, 4000 chars, use paragraph breaks for multi-section content). " +
        "Forbidden characters in name/short_name: $*[{}]|\\<>?\"/;: and tab.")]
    public static string CreateElement(
        [Description("Concise name for the element, typically the object/function type, e.g. 'Socket left', 'Boiler', 'Sofa'. Brand, model, dimensions, and purchase details usually do not belong in the name; put them in description when no other field already covers them. Keep brand/model in the name only when they are the common identifier, e.g. 'Hue Bridge'. Keep a short sibling differentiator such as 'left'/'right' or a number when siblings would otherwise be ambiguous.")] string name,
        [Description("Object category: name, short name, or full path (e.g. 'Electrical', 'Heating', 'Furniture', or 'Electrical/Cable' when the name is ambiguous). Required!")] string category,
        [Description("Full path of the parent element, e.g. 'House/GF/Kitchen/South-Wall'. Empty = top-level.")] string? parent = null,
        [Description("Optional concise path segment, mainly for location containers (e.g. 'W-SW' for 'West-Southwest Wall'). Must be unique among siblings; omit when it would be the same as name.")] string? short_name = null,
        [Description("Status name (optional). Most elements need no status – omit for normal existing items. Only set when the user explicitly mentions a status like 'planned' or 'removed'. Call list_statuses for options.")] string? status = null,
        [Description("Intended use, when not self-evident from the name (optional). Only fill with information the user explicitly provided.")] string? purpose = null,
        [Description("Short temporary to-do during planning/construction (optional). For permanent technical info use description instead. Only fill with information the user explicitly provided.")] string? note = null,
        [Description("Permanent technical information for professionals (default for longer-lived info): installation specifics, maintenance history, test results, brand, model, dimensions, purchase info — anything not already covered by other fields (optional). Only fill with information the user explicitly provided — do not generate or infer.")] string? description = null,
        [Description("User-facing information: operating instructions, feature overview, maintenance schedule (what/when/how), troubleshooting tips (optional). Only fill with information the user explicitly provided.")] string? user_manual = null,
        [Description("Fine-grained location within the parent, such as 'under sink' or '136 cm from left' (optional). Use this instead of putting precise positional details into the name. Only fill with information the user explicitly provided.")] string? position = null)
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
        position    = Validate.NormalizeSingleline(position);

        var lenErr = Validate.Length(name, "name", 100)
                  ?? Validate.Length(short_name, "short_name", 50)
                  ?? Validate.Length(purpose?.Trim(), "purpose", 200)
                  ?? Validate.Length(note?.Trim(), "note", 200, "For permanent or longer information, use description instead.")
                  ?? Validate.Length(description?.Trim(), "description", 4000)
                  ?? Validate.Length(user_manual?.Trim(), "user_manual", 4000)
                  ?? Validate.Length(position?.Trim(), "position", 200);
        if (lenErr != null) return lenErr;

        try
        {
            using var conn = FirebirdDb.OpenConnection();
            var (_, _, byFullName) = QueryHelpers.LoadEtree(conn);

            string? parentOid = null;
            string? parentFullName = null;
            if (!string.IsNullOrWhiteSpace(parent))
            {
                if (!QueryHelpers.TryResolveElementRow(byFullName, parent, out var parentRow, out var canonicalParent))
                    return $"Error: parent element '{parent}' not found.";
                parentOid      = parentRow.Str("Oid");
                parentFullName = canonicalParent;
            }

            category = category?.Trim() ?? "";
            if (string.IsNullOrEmpty(category))
                return "Error: 'category' is required.";
            var (categoryOid, catError) = QueryHelpers.ResolveCategoryOid(conn, category);
            if (catError != null) return catError;
            if (categoryOid == null)
                return $"Error: category '{category}' not found. Call list_categories for available categories.";

            var segment     = string.IsNullOrEmpty(short_name) ? name : short_name;
            var newFullName = parentFullName != null ? $"{parentFullName}/{segment}" : segment;
            if (byFullName.ContainsKey(newFullName))
                return $"Error: an element with full name '{newFullName}' already exists.";

            var siblingError = QueryHelpers.CheckSiblingUniqueness(conn, name, short_name, parentOid);
            if (siblingError != null) return siblingError;

            string? statusOid = null;
            if (!string.IsNullOrWhiteSpace(status))
            {
                statusOid = QueryHelpers.ResolveStatusOid(conn, status);
                if (statusOid == null)
                    return $"Error: status '{status}' not found. Call list_statuses for available statuses.";
            }

            var parentStatusAdvisory = QueryHelpers.ElementParentStatusAdvisory(conn, statusOid, parentOid);

            var sortIndex = QueryHelpers.NextSortIndex(conn, parentOid);
            var oid = Guid.NewGuid().ToString("D");
            var now = DateTime.UtcNow;

            return FirebirdDb.RunInTransaction(conn, txn =>
            {
                FirebirdDb.ExecuteNonQuery(conn, txn, """
                    INSERT INTO "CEntity" ("Oid", "OptimisticLockField", "ObjectType", "CreatedOn", "CreatedBy",
                                          "Status", "Purpose", "Note", "Description", "UserManual")
                    VALUES (?, 0, ?, ?, ?, ?, ?, ?, ?, ?)
                    """,
                    oid, XPObjectTypes.Element, now, "HomeMemory",
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
                    INSERT INTO "Element" ("Oid", "Name", "ShortName", "PartOfElement", "Position", "SortIndex")
                    VALUES (?, ?, ?, ?, ?, ?)
                    """,
                    oid, name,
                    (object?)short_name          ?? DBNull.Value,
                    (object?)parentOid           ?? DBNull.Value,
                    (object?)position?.Trim()    ?? DBNull.Value,
                    sortIndex);

                var result = $"✓ Element '{newFullName}' created (OID: {oid}).";
                if (parentStatusAdvisory != null)
                    result += $"\n  Advisory: {parentStatusAdvisory}.";
                return result;
            });
        }
        catch (Exception ex)
        {
            return $"Error: failed to create element: {ex.Message}";
        }
    }

    // ── update_element ────────────────────────────────────────────────────────

    [McpServerTool(Name = "update_element", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false)]
    [Description(
        "Updates an existing element identified by exact full path or stable OID. Use find_element if the path is unknown. " +
        "Only provided fields are changed; omitted fields stay untouched. " +
        "Pass 'CLEAR' to empty an optional field (status, purpose, note, description, user_manual, position, short_name). " +
        "When changing category: call list_categories first. When changing status: call list_statuses first. " +
        "If both this element and its parent have explicit statuses, a conflicting child status is reported as an advisory. " +
        "CAUTION: changing 'name' or 'short_name' changes the full path of this element and all its descendants. " +
        "Use the parent path for location; do not repeat parent context in the element name. " +
        "IMPORTANT: ALWAYS call get_element_details before updating purpose, description, note, or user_manual. " +
        "If the field already has content, inform the user and ask whether to replace or extend. " +
        "Field choice: short temporary to-do -> note (single line, 200 chars); permanent technical info -> description (multiline, 4000 chars, use paragraph breaks for multi-section content); end-user instructions -> user_manual (multiline, 4000 chars, use paragraph breaks for multi-section content). " +
        "Pass expected_version from get_element_details to prevent overwriting a concurrent change. " +
        "Forbidden characters in name/short_name: $*[{}]|\\<>?\"/;: and tab.")]
    public static string UpdateElement(
        [Description("Full name of the element. Optional when oid is provided.")] string fullname = "",
        [Description("New name (optional). Changes the full path! Concise name for the element, typically the object/function type. Brand, model, dimensions, and purchase details usually do not belong in the name; put them in description when no other field already covers them. Keep brand/model in the name only when they are the common identifier, e.g. 'Hue Bridge'. Keep a short sibling differentiator such as 'left'/'right' or a number when siblings would otherwise be ambiguous.")] string? name = null,
        [Description("New short name (optional, 'CLEAR' to remove). Changes the full path! Optional concise path segment, mainly for location containers. Must be unique among siblings; omit when it would be the same as name.")] string? short_name = null,
        [Description("New category: name, short name, or full path (e.g. 'Electrical/Cable' when the name is ambiguous). Cannot be cleared – required field.")] string? category = null,
        [Description("New status name (optional). Set only when user explicitly mentions a status (e.g. 'Planned', 'Removed'). 'CLEAR' removes a previously set status. Call list_statuses for options.")] string? status = null,
        [Description("Intended use, when not already self-evident from the name ('CLEAR' to remove)")] string? purpose = null,
        [Description("Short temporary to-do during planning/construction. For permanent technical info use description instead ('CLEAR' to remove)")] string? note = null,
        [Description("Permanent technical information for professionals (default for longer-lived info): installation specifics, maintenance history, test results, brand, model, dimensions, purchase info — anything not already covered by other fields ('CLEAR' to remove)")] string? description = null,
        [Description("User-facing information: operating instructions, feature overview, maintenance schedule (what/when/how), troubleshooting tips ('CLEAR' to remove)")] string? user_manual = null,
        [Description("Fine-grained location within the parent, such as 'under sink' or '136 cm from left' ('CLEAR' to remove). Use this instead of putting precise positional details into the name. Only fill with information the user explicitly provided.")] string? position = null,
        [Description("Stable element OID. Optional when fullname is provided. When both are supplied, they must identify the same element.")] string? oid = null,
        [Description("Version returned by get_element_details. When supplied, the update fails if the element changed meanwhile.")] int? expected_version = null)
    {
        fullname = fullname?.Trim() ?? "";
        var versionError = QueryHelpers.ValidateExpectedVersion(expected_version);
        if (versionError != null) return versionError;

        if (name == null && short_name == null && category == null && status == null
            && purpose == null && note == null && description == null
            && user_manual == null && position == null)
            return "Error: provide at least one of name, short_name, category, status, purpose, note, description, user_manual, position.";

        short_name  = Validate.NormalizeClear(short_name);
        status      = Validate.NormalizeClear(status);
        purpose     = Validate.NormalizeClear(purpose);
        note        = Validate.NormalizeClear(note);
        description = Validate.NormalizeClear(description);
        user_manual = Validate.NormalizeClear(user_manual);
        position    = Validate.NormalizeClear(position);

        purpose     = Validate.NormalizeSingleline(purpose);
        note        = Validate.NormalizeSingleline(note);
        description = Validate.NormalizeMultiline(description);
        user_manual = Validate.NormalizeMultiline(user_manual);
        position    = Validate.NormalizeSingleline(position);

        try
        {
            using var conn = FirebirdDb.OpenConnection();
            var (_, byOid, byFullName) = QueryHelpers.LoadEtree(conn);
            var resolution = QueryHelpers.ResolveElementSelector(byOid, byFullName, fullname, oid);
            if (resolution.error != null) return resolution.error;
            var targetRow = resolution.row!;
            fullname = resolution.canonicalFullName;

            var targetOid = targetRow.Str("Oid");
            var now = DateTime.UtcNow;

            if (name != null)
            {
                name = Validate.NormalizeSingleline(name)?.Trim();
                if (string.IsNullOrEmpty(name))
                    return "Error: 'name' cannot be empty.";
                if (Validate.InvalidChars.IsMatch(name))
                    return "Error: name contains invalid characters ($*[{}]|\\<>?\"/;: or tab).";
            }
            if (short_name != null && short_name != "CLEAR")
            {
                short_name = Validate.NormalizeSingleline(short_name)?.Trim();
                if (string.IsNullOrEmpty(short_name))
                    return "Error: 'short_name' cannot be empty – use 'CLEAR' to remove it.";
                if (Validate.InvalidChars.IsMatch(short_name))
                    return "Error: short_name contains invalid characters ($*[{}]|\\<>?\"/;: or tab).";
            }

            var lenErr = Validate.Length(name, "name", 100)
                      ?? Validate.Length(short_name != "CLEAR" ? short_name : null, "short_name", 50)
                      ?? Validate.Length(purpose     is not null and not "CLEAR" ? purpose.Trim()     : null, "purpose", 200)
                      ?? Validate.Length(note        is not null and not "CLEAR" ? note.Trim()        : null, "note", 200, "For permanent or longer information, use description instead.")
                      ?? Validate.Length(description is not null and not "CLEAR" ? description.Trim() : null, "description", 4000)
                      ?? Validate.Length(user_manual is not null and not "CLEAR" ? user_manual.Trim() : null, "user_manual", 4000)
                      ?? Validate.Length(position    is not null and not "CLEAR" ? position.Trim()    : null, "position", 200);
            if (lenErr != null) return lenErr;

            if (name != null || short_name != null)
            {
                var curRows = FirebirdDb.ExecuteQuery(conn,
                    """SELECT "Name", "ShortName", "PartOfElement" FROM "Element" WHERE "Oid" = ?""", targetOid);
                if (curRows.Count == 0) return "Error: element not found in Element table.";
                var cur = curRows[0];

                var newName      = name      ?? cur.Str("Name");
                var newShortName = short_name == "CLEAR" ? null
                                 : short_name != null    ? short_name
                                 : (string?)cur.Str("ShortName").NullIfEmpty();
                var segment      = string.IsNullOrEmpty(newShortName) ? newName : newShortName;

                var (parentPrefix, _) = QueryHelpers.SplitParentAndName(fullname);
                var newFullName       = parentPrefix + segment;

                if (!string.Equals(newFullName, fullname, StringComparison.OrdinalIgnoreCase)
                    && byFullName.TryGetValue(newFullName, out var conflictingRow)
                    && !string.Equals(conflictingRow.Str("Oid"), targetOid, StringComparison.OrdinalIgnoreCase))
                    return $"Error: full name '{newFullName}' is already taken.";

                var parentOidRaw = cur.GetValueOrDefault("PartOfElement");
                var parentOid    = parentOidRaw != null && parentOidRaw != DBNull.Value
                    ? FirebirdDb.Str(parentOidRaw)
                    : null;
                var siblingError = QueryHelpers.CheckSiblingUniqueness(conn, newName, newShortName, parentOid, targetOid);
                if (siblingError != null) return siblingError;
            }

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

            var relationshipRows = FirebirdDb.ExecuteQuery(conn, """
                SELECT e."PartOfElement", ce."Status"
                FROM "Element" e
                JOIN "CEntity" ce ON ce."Oid" = e."Oid"
                WHERE e."Oid" = ?
                """, targetOid);
            var currentParentOid = relationshipRows[0].Str("PartOfElement").NullIfEmpty();
            var effectiveStatusOid = updateStatus
                ? status == "CLEAR" ? null : statusOid
                : relationshipRows[0].Str("Status").NullIfEmpty();
            var parentStatusAdvisory = QueryHelpers.ElementParentStatusAdvisory(
                conn, effectiveStatusOid, currentParentOid);

            var overwriteAdvisories = QueryHelpers.CollectOverwriteAdvisories(
                conn, targetOid, description, note, purpose, user_manual);

            return FirebirdDb.RunInTransaction(conn, txn =>
            {
                if (!QueryHelpers.TouchCEntity(
                        conn, txn, targetOid, now, "HomeMemory", expected_version))
                    return expected_version.HasValue
                        ? QueryHelpers.VersionConflict(
                            "element", expected_version.Value, "get_element_details")
                        : "Error: element no longer exists. Call get_element_details again.";

                if (updateStatus)
                    FirebirdDb.ExecuteNonQuery(conn, txn,
                        """UPDATE "CEntity" SET "Status" = ? WHERE "Oid" = ?""",
                        status == "CLEAR" ? DBNull.Value : (object)statusOid!, targetOid);

                QueryHelpers.SetCEntityField(conn, txn, targetOid, "Purpose",     purpose);
                QueryHelpers.SetCEntityField(conn, txn, targetOid, "Note",        note);
                QueryHelpers.SetCEntityField(conn, txn, targetOid, "Description", description);
                QueryHelpers.SetCEntityField(conn, txn, targetOid, "UserManual",  user_manual);

                if (updateCategory)
                    FirebirdDb.ExecuteNonQuery(conn, txn,
                        """UPDATE "CItem" SET "Category" = ? WHERE "Oid" = ?""",
                        (object?)categoryOid ?? DBNull.Value, targetOid);

                if (name != null)
                    FirebirdDb.ExecuteNonQuery(conn, txn,
                        """UPDATE "Element" SET "Name" = ? WHERE "Oid" = ?""",
                        name, targetOid);

                if (short_name != null)
                    FirebirdDb.ExecuteNonQuery(conn, txn,
                        """UPDATE "Element" SET "ShortName" = ? WHERE "Oid" = ?""",
                        short_name == "CLEAR" ? DBNull.Value : (object)short_name, targetOid);

                if (position != null)
                    FirebirdDb.ExecuteNonQuery(conn, txn,
                        """UPDATE "Element" SET "Position" = ? WHERE "Oid" = ?""",
                        position == "CLEAR" ? DBNull.Value : (object)position.Trim(), targetOid);

                var result = $"✓ Element '{fullname}' updated.";
                foreach (var adv in overwriteAdvisories)
                    result += $"\n  Advisory: {adv}.";
                if (parentStatusAdvisory != null)
                    result += $"\n  Advisory: {parentStatusAdvisory}.";
                return result;
            });
        }
        catch (Exception ex)
        {
            return $"Error: failed to update element: {ex.Message}";
        }
    }

    // ── delete_element ────────────────────────────────────────────────────────

    [McpServerTool(Name = "delete_element", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false)]
    [Description(
        "Deletes an element from the database. " +
        "Fails if the element has child elements, connections, or attached documents. " +
        "When deletion fails for this reason, treat it as a stop signal: report the affected scope to the user and ask for explicit confirmation – never cascade-delete by removing children or connections first on your own. " +
        "Identify by exact full path or stable OID. Pass expected_version from get_element_details to prevent deleting a concurrently changed element. " +
        "Deletes from all 4 database tables (Element, Part, CItem, CEntity) in a single transaction.")]
    public static string DeleteElement(
        [Description("Full name of the element. Optional when oid is provided.")] string fullname = "",
        [Description("Stable element OID. Optional when fullname is provided. When both are supplied, they must identify the same element.")] string? oid = null,
        [Description("Version returned by get_element_details. When supplied, deletion fails if the element changed meanwhile.")] int? expected_version = null)
    {
        fullname = fullname?.Trim() ?? "";
        var versionError = QueryHelpers.ValidateExpectedVersion(expected_version);
        if (versionError != null) return versionError;

        try
        {
            using var conn = FirebirdDb.OpenConnection();
            var (_, byOid, byFullName) = QueryHelpers.LoadEtree(conn);
            var resolution = QueryHelpers.ResolveElementSelector(byOid, byFullName, fullname, oid);
            if (resolution.error != null) return resolution.error;
            fullname = resolution.canonicalFullName;

            var targetOid = resolution.row!.Str("Oid");

            var children   = FirebirdDb.ExecuteQuery(conn,
                """SELECT COUNT(*) AS CNT FROM "Element" WHERE "PartOfElement" = ?""", targetOid);
            var childCount = FirebirdDb.CountResult(children);
            if (childCount > 0)
                return $"Error: element has {childCount} child element(s). Report this to the user and ask for explicit confirmation before removing any of them.";

            var connections = FirebirdDb.ExecuteQuery(conn,
                """SELECT COUNT(*) AS CNT FROM "Connection" WHERE "Source" = ? OR "Destination" = ?""",
                targetOid, targetOid);
            var connCount = FirebirdDb.CountResult(connections);
            if (connCount > 0)
                return $"Error: element has {connCount} connection(s). Report this to the user and ask for explicit confirmation before removing any of them.";

            var docError = QueryHelpers.CheckDocumentsAttached(conn, targetOid, "element");
            if (docError != null) return docError;

            var advisories = QueryHelpers.CollectDeleteAdvisories(conn, targetOid, "element");

            return FirebirdDb.RunInTransaction(conn, txn =>
            {
                if (expected_version.HasValue
                    && !QueryHelpers.TouchCEntity(
                        conn, txn, targetOid, DateTime.UtcNow, "HomeMemory", expected_version))
                    return QueryHelpers.VersionConflict(
                        "element", expected_version.Value, "get_element_details");

                FirebirdDb.ExecuteNonQuery(conn, txn, """DELETE FROM "Element"        WHERE "Oid"   = ?""", targetOid);
                FirebirdDb.ExecuteNonQuery(conn, txn, """DELETE FROM "Part"           WHERE "Oid"   = ?""", targetOid);
                // Remove image associations before CItem (FK has no CASCADE).
                // Table may not exist in all DB versions – skip silently (mirrors CollectDeleteAdvisories read-path).
                try { FirebirdDb.ExecuteNonQuery(conn, txn, """DELETE FROM "ImagesToCItems" WHERE "CItem" = ?""", targetOid); }
                catch (FbException ex) when (ex.ErrorCode is 335544580 or 335544569) { /* table absent – skip */ }
                FirebirdDb.ExecuteNonQuery(conn, txn, """DELETE FROM "CItem"          WHERE "Oid"   = ?""", targetOid);
                FirebirdDb.ExecuteNonQuery(conn, txn, """DELETE FROM "CEntity"        WHERE "Oid"   = ?""", targetOid);

                var result = $"✓ Element '{fullname}' deleted.";
                if (advisories.Count > 0)
                    result += $"\n  Advisory: {string.Join("; ", advisories)}.";
                return result;
            });
        }
        catch (Exception ex)
        {
            return $"Error: failed to delete element: {ex.Message}";
        }
    }

    // ── move_element ──────────────────────────────────────────────────────────

    [McpServerTool(Name = "move_element", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false)]
    [Description(
        "Moves an element to a different parent (or to top-level). " +
        "The element's full name and all descendant full names change automatically. " +
        "Fails if the new full name would conflict with an existing element, " +
        "if a sibling at the new parent has the same name or short name, " +
        "or if new_parent is a descendant of the element (circular reference). " +
        "If both the moved element and its new parent have explicit statuses, a conflicting element status is reported as an advisory. " +
        "Identify the element and optionally its new parent by stable OID. Pass expected_version from get_element_details to detect concurrent changes. " +
        "The element is appended at the end of the new parent's children.")]
    public static string MoveElement(
        [Description("Full name of the element to move. Optional when oid is provided.")] string fullname = "",
        [Description("Full name of the new parent element. Empty = move to top-level unless new_parent_oid is provided.")] string new_parent = "",
        [Description("Stable OID of the element to move. Optional when fullname is provided.")] string? oid = null,
        [Description("Stable OID of the new parent. Optional; when combined with new_parent both must identify the same element.")] string? new_parent_oid = null,
        [Description("Version returned by get_element_details for the element being moved. The move fails if it changed meanwhile.")] int? expected_version = null)
    {
        fullname   = fullname?.Trim()   ?? "";
        new_parent = new_parent?.Trim().TrimEnd('/') ?? "";
        var versionError = QueryHelpers.ValidateExpectedVersion(expected_version);
        if (versionError != null) return versionError;

        try
        {
            using var conn = FirebirdDb.OpenConnection();
            var (_, byOid, byFullName) = QueryHelpers.LoadEtree(conn);
            var resolution = QueryHelpers.ResolveElementSelector(byOid, byFullName, fullname, oid);
            if (resolution.error != null) return resolution.error;
            var targetRow = resolution.row!;
            fullname = resolution.canonicalFullName;

            var targetOid = targetRow.Str("Oid");

            string? newParentOid      = null;
            string? newParentFullName = null;
            if (!string.IsNullOrEmpty(new_parent) || !string.IsNullOrWhiteSpace(new_parent_oid))
            {
                var parentResolution = QueryHelpers.ResolveElementSelector(
                    byOid, byFullName, new_parent, new_parent_oid);
                if (parentResolution.error != null)
                    return parentResolution.error;
                var parentRow = parentResolution.row!;
                new_parent = parentResolution.canonicalFullName;

                newParentOid      = parentRow.Str("Oid");
                newParentFullName = parentResolution.canonicalFullName;

                var elementPrefix = fullname.TrimEnd('/') + "/";
                if (new_parent.StartsWith(elementPrefix, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(new_parent, fullname, StringComparison.OrdinalIgnoreCase))
                    return $"Error: cannot move '{fullname}' into itself or one of its descendants.";
            }

            var (_, segmentName) = QueryHelpers.SplitParentAndName(fullname);
            var newFullName = newParentFullName != null
                ? $"{newParentFullName}/{segmentName}"
                : segmentName;

            if (!string.Equals(newFullName, fullname, StringComparison.OrdinalIgnoreCase)
                && byFullName.ContainsKey(newFullName))
                return $"Error: an element with full name '{newFullName}' already exists.";

            var elemRows = FirebirdDb.ExecuteQuery(conn,
                """SELECT "Name", "ShortName", "PartOfElement" FROM "Element" WHERE "Oid" = ?""", targetOid);
            if (elemRows.Count == 0) return "Error: element data not found.";
            var elemName      = elemRows[0].Str("Name");
            var elemShortName = elemRows[0].Str("ShortName").NullIfEmpty();
            var currentParentOid = elemRows[0].Str("PartOfElement").NullIfEmpty();

            if (string.Equals(FirebirdDb.OidKey(currentParentOid), FirebirdDb.OidKey(newParentOid), StringComparison.OrdinalIgnoreCase))
            {
                if (expected_version.HasValue
                    && !QueryHelpers.HasExpectedVersion(conn, targetOid, expected_version.Value))
                    return QueryHelpers.VersionConflict(
                        "element", expected_version.Value, "get_element_details");
                return $"✓ Element '{fullname}' already has the requested parent.";
            }

            var siblingError = QueryHelpers.CheckSiblingUniqueness(conn, elemName, elemShortName, newParentOid, targetOid);
            if (siblingError != null) return siblingError;

            var statusRows = FirebirdDb.ExecuteQuery(conn,
                """SELECT "Status" FROM "CEntity" WHERE "Oid" = ?""", targetOid);
            var elementStatusOid = statusRows[0].Str("Status").NullIfEmpty();
            var parentStatusAdvisory = QueryHelpers.ElementParentStatusAdvisory(
                conn, elementStatusOid, newParentOid);

            var sortIndex = QueryHelpers.NextSortIndex(conn, newParentOid);
            var now       = DateTime.UtcNow;

            return FirebirdDb.RunInTransaction(conn, txn =>
            {
                if (!QueryHelpers.TouchCEntity(
                        conn, txn, targetOid, now, "HomeMemory", expected_version))
                    return expected_version.HasValue
                        ? QueryHelpers.VersionConflict(
                            "element", expected_version.Value, "get_element_details")
                        : "Error: element no longer exists. Call get_element_details again.";

                FirebirdDb.ExecuteNonQuery(conn, txn, """
                    UPDATE "Element" SET "PartOfElement" = ?, "SortIndex" = ?
                    WHERE "Oid" = ?
                    """,
                    (object?)newParentOid ?? DBNull.Value,
                    sortIndex,
                    targetOid);

                var result = $"✓ Element moved: '{fullname}' → '{newFullName}'.";
                if (parentStatusAdvisory != null)
                    result += $"\n  Advisory: {parentStatusAdvisory}.";
                return result;
            });
        }
        catch (Exception ex)
        {
            return $"Error: failed to move element: {ex.Message}";
        }
    }
}
