using System.ComponentModel;
using System.Text.RegularExpressions;
using ModelContextProtocol.Server;
using HomeMemory.MCP.Db;

namespace HomeMemory.MCP.Tools;

[McpServerToolType]
public static class ElementTools
{
    // Forbidden characters for element/short names (from RegExValidation.SpecialCharactersNotAllowed)
    // $*[{}|\<>?/";\: and tab
    private static readonly Regex InvalidCharsElement = new(@"[\$\*\[\{\]\}\|\\<>\?/"";\:\t]");

    // ── get_element_details ───────────────────────────────────────────────────

    [McpServerTool(Name = "get_element_details")]
    [Description(
        "Full details of a single element: properties (category, status, part type, " +
        "purpose, description, user manual), direct child elements, " +
        "and all incoming and outgoing connections (physical lines: pipes, cables, ducts). " +
        "Use when the exact path is known and you need details or connections. " +
        "An element is any physical item in the building: installed equipment, appliance, " +
        "furniture, fixture, tool, or structural container (room, floor, etc.).")]
    public static string GetElementDetails(
        [Description("Full name path, e.g. 'House/GF/Kitchen/South-Wall/Socket'")] string fullname)
    {
        var fn = fullname.Trim();
        if (string.IsNullOrEmpty(fn))
            return "Error: 'fullname' is required.";

        try
        {
            using var conn = FirebirdDb.OpenConnection();

            var resolvedFn = QueryHelpers.ResolveElementFullName(conn, fn);
            if (resolvedFn is null)
                return $"Error: element '{fn}' not found. Use find_element to search for the correct path.";
            fn = resolvedFn;

            var allElements = FirebirdDb.ExecuteQuery(conn,
                $"{SqlQueries.EtreeCte} SELECT \"Oid\", FULLNAME, \"Name\", \"ShortName\", \"Position\" FROM ETREE");

            var oidToRow = allElements.ToDictionary(
                r => FirebirdDb.OidKey(r["Oid"]),
                r => r,
                StringComparer.OrdinalIgnoreCase);

            var target = allElements.FirstOrDefault(r =>
                string.Equals(FirebirdDb.Str(r["FULLNAME"]), fn, StringComparison.OrdinalIgnoreCase));

            if (target is null)
                return $"Error: element '{fn}' not found. Use find_element to search for the correct path.";

            var oid    = target["Oid"];
            var oidKey = FirebirdDb.OidKey(oid);
            var lines  = new List<string> { $"Element: {FirebirdDb.Str(target["FULLNAME"])}\n" };

            lines.Add($"  Name        : {FirebirdDb.Str(target["Name"])}");
            var sn = FirebirdDb.Str(target.GetValueOrDefault("ShortName"));
            if (!string.IsNullOrEmpty(sn)) lines.Add($"  Short name  : {sn}");
            var pos = FirebirdDb.Str(target.GetValueOrDefault("Position"));
            if (!string.IsNullOrEmpty(pos)) lines.Add($"  Position    : {pos}");

            var ext = FirebirdDb.ExecuteQuery(conn, """
                SELECT
                    ce."Purpose",
                    ce."Note",
                    ce."Description",
                    ce."UserManual",
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
                """, oid);

            if (ext.Count > 0)
            {
                var e = ext[0];
                if (!string.IsNullOrEmpty(FirebirdDb.Str(e.GetValueOrDefault("CategoryName"))))
                    lines.Add($"  Category    : {FirebirdDb.Str(e["CategoryName"])}");
                if (!string.IsNullOrEmpty(FirebirdDb.Str(e.GetValueOrDefault("StatusName"))))
                    lines.Add($"  Status      : {FirebirdDb.Str(e["StatusName"])}");
                if (!string.IsNullOrEmpty(FirebirdDb.Str(e.GetValueOrDefault("PartTypeName"))))
                    lines.Add($"  Part type   : {FirebirdDb.Str(e["PartTypeName"])}");
                if (!string.IsNullOrEmpty(FirebirdDb.Str(e.GetValueOrDefault("Purpose"))))
                    lines.Add($"  Purpose     : {FirebirdDb.Str(e["Purpose"])}");
                if (!string.IsNullOrEmpty(FirebirdDb.Str(e.GetValueOrDefault("Note"))))
                    lines.Add($"  Note        : {FirebirdDb.Str(e["Note"])}");
                if (!string.IsNullOrEmpty(FirebirdDb.Str(e.GetValueOrDefault("Description"))))
                    lines.Add($"  Description : {FirebirdDb.Str(e["Description"])}");
                if (!string.IsNullOrEmpty(FirebirdDb.Str(e.GetValueOrDefault("UserManual"))))
                    lines.Add($"  User manual : {FirebirdDb.Str(e["UserManual"])}");
            }

            var children = FirebirdDb.ExecuteQuery(conn, """
                SELECT "Oid", "Name", "SortIndex"
                FROM "Element"
                WHERE "PartOfElement" = ?
                ORDER BY "SortIndex" NULLS LAST, "Name"
                """, oid);

            var fnPrefix = fn.TrimEnd('/') + "/";
            int totalDescendants = allElements.Count(r =>
                FirebirdDb.Str(r["FULLNAME"]).StartsWith(fnPrefix, StringComparison.OrdinalIgnoreCase));

            if (children.Count > 0)
            {
                var suffix = totalDescendants > children.Count ? $", {totalDescendants} total" : "";
                lines.Add($"\n  Child elements ({children.Count} direct{suffix}):");
                foreach (var c in children)
                {
                    var childFull = oidToRow.TryGetValue(FirebirdDb.OidKey(c["Oid"]), out var cr)
                        ? FirebirdDb.Str(cr["FULLNAME"])
                        : FirebirdDb.Str(c["Name"]);
                    lines.Add($"    +-- {childFull}");
                }
            }

            var connOut = FirebirdDb.ExecuteQuery(conn, """
                SELECT "Name", "Destination", "Route", "Length"
                FROM "Connection"
                WHERE "Source" = ?
                ORDER BY "Name"
                """, oid);

            if (connOut.Count > 0)
            {
                lines.Add($"\n  Outgoing connections ({connOut.Count}):");
                foreach (var c in connOut)
                {
                    var destFull = oidToRow.TryGetValue(FirebirdDb.OidKey(c.GetValueOrDefault("Destination")), out var dr)
                        ? FirebirdDb.Str(dr["FULLNAME"])
                        : FirebirdDb.Str(c.GetValueOrDefault("Destination"));
                    var length = c.GetValueOrDefault("Length");
                    var route  = FirebirdDb.Str(c.GetValueOrDefault("Route"));
                    var line   = $"    --> {FirebirdDb.Str(c["Name"])}  =>  {destFull}";
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
                """, oid);

            if (connIn.Count > 0)
            {
                lines.Add($"\n  Incoming connections ({connIn.Count}):");
                foreach (var c in connIn)
                {
                    var srcFull = oidToRow.TryGetValue(FirebirdDb.OidKey(c.GetValueOrDefault("Source")), out var sr)
                        ? FirebirdDb.Str(sr["FULLNAME"])
                        : FirebirdDb.Str(c.GetValueOrDefault("Source"));
                    var length = c.GetValueOrDefault("Length");
                    var route  = FirebirdDb.Str(c.GetValueOrDefault("Route"));
                    var line   = $"    <-- {srcFull}  <=  {FirebirdDb.Str(c["Name"])}";
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

    [McpServerTool(Name = "create_element")]
    [Description(
        "Creates a new element. An element is any physical item in the building: " +
        "installed equipment (socket, boiler, radiator, circuit breaker), appliances (washing machine, fridge), " +
        "furniture (sofa, wardrobe), fixtures, tools, or structural area containers (room, wall, floor). " +
        "Structural area elements (room, floor, ceiling, outdoor area, garage, etc.) are also created with this tool – " +
        "simply choose a category marked [structural area] in list_categories (is_structural_area=true). " +
        "Required fields: name, category. " +
        "IMPORTANT – category workflow: call list_categories first to find the best matching category " +
        "by name or context (e.g. 'Heating' for a boiler, 'Electrical' for a socket, 'Room' for a room). " +
        "If no suitable category exists, call create_category first, then use the new category here. " +
        "IMPORTANT – parent path: if the parent element's full path is not known exactly, " +
        "call get_structure_overview or find_element first to find the correct path. Do not guess paths. " +
        "Optional: parent (full path of the parent element), " +
        "short_name, status, purpose, note, description, user_manual, position. " +
        "Forbidden characters in name/short_name: $*[{}|\\<>?\"/;: and tab.")]
    public static string CreateElement(
        [Description("Element name, e.g. 'Socket left', 'Boiler', 'Sofa'")] string name,
        [Description("Object category: name or short name, e.g. 'Electrical', 'Heating', 'Furniture'. Required!")] string category,
        [Description("Full path of the parent element, e.g. 'House/GF/Kitchen/South-Wall'. Empty = top-level.")] string? parent = null,
        [Description("Short name (optional), e.g. 'W-SW' for 'West-Southwest Wall'")] string? short_name = null,
        [Description("Status name (optional). Most elements need no status – omit for normal existing items. Only set when the user explicitly mentions a status like 'planned' or 'removed'. Call list_statuses for options.")] string? status = null,
        [Description("Intended use, when not self-evident from the name (optional). Only fill with information the user explicitly provided.")] string? purpose = null,
        [Description("Temporary note or to-do during planning/construction – not for permanent records (optional). Only fill with information the user explicitly provided.")] string? note = null,
        [Description("Permanent technical information for professionals: installation specifics, maintenance history, test results, purchase info — anything not already covered by other fields (optional). Only fill with information the user explicitly provided — do not generate or infer.")] string? description = null,
        [Description("User-facing information: operating instructions, feature overview, maintenance schedule (what/when/how), troubleshooting tips (optional). Only fill with information the user explicitly provided.")] string? user_manual = null,
        [Description("Position within the element (optional)")] string? position = null)
    {
        name = name?.Trim() ?? "";
        if (string.IsNullOrEmpty(name))
            return "Error: 'name' is required.";
        if (InvalidCharsElement.IsMatch(name))
            return "Error: name contains invalid characters ($*[{}|\\<>?\"/;: or tab).";

        short_name = string.IsNullOrWhiteSpace(short_name) ? null : short_name.Trim();
        if (short_name != null && InvalidCharsElement.IsMatch(short_name))
            return "Error: short_name contains invalid characters ($*[{}|\\<>?\"/;: or tab).";

        var lenErr = Validate.Length(name, "name", 100)
                  ?? Validate.Length(short_name, "short_name", 50)
                  ?? Validate.Length(purpose?.Trim(), "purpose", 200)
                  ?? Validate.Length(note?.Trim(), "note", 200)
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
                parentOid      = FirebirdDb.Str(parentRow["Oid"]);
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

            var sortIndex = QueryHelpers.NextSortIndex(conn, parentOid);
            var oid = Guid.NewGuid().ToString("D");
            var now = DateTime.UtcNow;

            using var txn = conn.BeginTransaction();
            try
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

                txn.Commit();
                return $"✓ Element '{newFullName}' created (OID: {oid}).";
            }
            catch
            {
                txn.Rollback();
                throw;
            }
        }
        catch (Exception ex)
        {
            return $"Error creating element: {ex.Message}";
        }
    }

    // ── update_element ────────────────────────────────────────────────────────

    [McpServerTool(Name = "update_element")]
    [Description(
        "Updates an existing element. Required: fullname (exact full path – use find_element to look it up if unsure). " +
        "Only provided fields are changed; omitted fields stay untouched. " +
        "Pass 'CLEAR' to empty an optional field (status, purpose, note, description, user_manual, position, short_name). " +
        "When changing category: call list_categories first. When changing status: call list_statuses first. " +
        "CAUTION: changing 'name' or 'short_name' changes the full path of this element and all its descendants. " +
        "IMPORTANT: ALWAYS call get_element_details before updating purpose, description, note, or user_manual. " +
        "If the field already has content, inform the user and ask whether to replace or extend. " +
        "Forbidden characters in name/short_name: $*[{}|\\<>?\"/;: and tab.")]
    public static string UpdateElement(
        [Description("Full name of the element, e.g. 'House/GF/Kitchen/South-Wall/Socket'")] string fullname,
        [Description("New name (optional). Changes the full name!")] string? name = null,
        [Description("New short name (optional, 'CLEAR' to remove). Changes the full name!")] string? short_name = null,
        [Description("New category: name or short name (cannot be cleared – required field)")] string? category = null,
        [Description("New status name (optional). Set only when user explicitly mentions a status (e.g. 'Planned', 'Removed'). 'CLEAR' removes a previously set status. Call list_statuses for options.")] string? status = null,
        [Description("Intended use, when not already self-evident from the name ('CLEAR' to remove)")] string? purpose = null,
        [Description("Temporary note or to-do – use during planning/construction for things to address later. Not for permanent records ('CLEAR' to remove)")] string? note = null,
        [Description("Permanent technical information for professionals: installation specifics, maintenance history, test results, purchase info — anything not already covered by other fields ('CLEAR' to remove)")] string? description = null,
        [Description("User-facing information: operating instructions, feature overview, maintenance schedule (what/when/how), troubleshooting tips ('CLEAR' to remove)")] string? user_manual = null,
        [Description("Position ('CLEAR' to remove)")] string? position = null)
    {
        fullname = fullname?.Trim() ?? "";
        if (string.IsNullOrEmpty(fullname))
            return "Error: 'fullname' is required.";

        short_name  = Validate.NormalizeClear(short_name);
        status      = Validate.NormalizeClear(status);
        purpose     = Validate.NormalizeClear(purpose);
        note        = Validate.NormalizeClear(note);
        description = Validate.NormalizeClear(description);
        user_manual = Validate.NormalizeClear(user_manual);
        position    = Validate.NormalizeClear(position);

        try
        {
            using var conn = FirebirdDb.OpenConnection();
            var (_, _, byFullName) = QueryHelpers.LoadEtree(conn);

            if (!QueryHelpers.TryResolveElementRow(byFullName, fullname, out var targetRow, out var canonicalFullname))
                return $"Error: element '{fullname}' not found.";
            fullname = canonicalFullname;

            var oid = FirebirdDb.Str(targetRow["Oid"]);
            var now = DateTime.UtcNow;

            if (name != null)
            {
                name = name.Trim();
                if (string.IsNullOrEmpty(name))
                    return "Error: 'name' cannot be empty.";
                if (InvalidCharsElement.IsMatch(name))
                    return "Error: name contains invalid characters ($*[{}|\\<>?\"/;: or tab).";
            }
            if (short_name != null && short_name != "CLEAR")
            {
                short_name = short_name.Trim();
                if (string.IsNullOrEmpty(short_name))
                    return "Error: 'short_name' cannot be empty – use 'CLEAR' to remove it.";
                if (InvalidCharsElement.IsMatch(short_name))
                    return "Error: short_name contains invalid characters ($*[{}|\\<>?\"/;: or tab).";
            }

            var lenErr = Validate.Length(name, "name", 100)
                      ?? Validate.Length(short_name != "CLEAR" ? short_name : null, "short_name", 50)
                      ?? Validate.Length(purpose     is not null and not "CLEAR" ? purpose.Trim()     : null, "purpose", 200)
                      ?? Validate.Length(note        is not null and not "CLEAR" ? note.Trim()        : null, "note", 200)
                      ?? Validate.Length(description is not null and not "CLEAR" ? description.Trim() : null, "description", 4000)
                      ?? Validate.Length(user_manual is not null and not "CLEAR" ? user_manual.Trim() : null, "user_manual", 4000)
                      ?? Validate.Length(position    is not null and not "CLEAR" ? position.Trim()    : null, "position", 200);
            if (lenErr != null) return lenErr;

            if (name != null || short_name != null)
            {
                var curRows = FirebirdDb.ExecuteQuery(conn,
                    """SELECT "Name", "ShortName", "PartOfElement" FROM "Element" WHERE "Oid" = ?""", oid);
                if (curRows.Count == 0) return "Error: element not found in Element table.";
                var cur = curRows[0];

                var newName      = name      ?? FirebirdDb.Str(cur["Name"]);
                var newShortName = short_name == "CLEAR" ? null
                                 : short_name != null    ? short_name
                                 : (string?)FirebirdDb.Str(cur.GetValueOrDefault("ShortName")).NullIfEmpty();
                var segment      = string.IsNullOrEmpty(newShortName) ? newName : newShortName;

                var lastSlash    = fullname.LastIndexOf('/');
                var parentPrefix = lastSlash >= 0 ? fullname[..lastSlash] + "/" : "";
                var newFullName  = parentPrefix + segment;

                if (!string.Equals(newFullName, fullname, StringComparison.OrdinalIgnoreCase)
                    && byFullName.ContainsKey(newFullName))
                    return $"Error: full name '{newFullName}' is already taken.";

                var parentOidRaw = cur.GetValueOrDefault("PartOfElement");
                var parentOid    = parentOidRaw != null && parentOidRaw != DBNull.Value
                    ? FirebirdDb.Str(parentOidRaw)
                    : null;
                var siblingError = QueryHelpers.CheckSiblingUniqueness(conn, newName, newShortName, parentOid, oid);
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

            var overwriteAdvisories = QueryHelpers.CollectOverwriteAdvisories(
                conn, oid, description, note, purpose, user_manual);

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

                if (updateStatus)
                    FirebirdDb.ExecuteNonQuery(conn, txn,
                        """UPDATE "CEntity" SET "Status" = ? WHERE "Oid" = ?""",
                        status == "CLEAR" ? DBNull.Value : (object)statusOid!, oid);

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

                if (user_manual != null)
                    FirebirdDb.ExecuteNonQuery(conn, txn,
                        """UPDATE "CEntity" SET "UserManual" = ? WHERE "Oid" = ?""",
                        user_manual == "CLEAR" ? DBNull.Value : (object)user_manual.Trim(), oid);

                if (updateCategory)
                    FirebirdDb.ExecuteNonQuery(conn, txn,
                        """UPDATE "CItem" SET "Category" = ? WHERE "Oid" = ?""",
                        (object?)categoryOid ?? DBNull.Value, oid);

                if (name != null)
                    FirebirdDb.ExecuteNonQuery(conn, txn,
                        """UPDATE "Element" SET "Name" = ? WHERE "Oid" = ?""",
                        name, oid);

                if (short_name != null)
                    FirebirdDb.ExecuteNonQuery(conn, txn,
                        """UPDATE "Element" SET "ShortName" = ? WHERE "Oid" = ?""",
                        short_name == "CLEAR" ? DBNull.Value : (object)short_name, oid);

                if (position != null)
                    FirebirdDb.ExecuteNonQuery(conn, txn,
                        """UPDATE "Element" SET "Position" = ? WHERE "Oid" = ?""",
                        position == "CLEAR" ? DBNull.Value : (object)position.Trim(), oid);

                txn.Commit();
                var result = $"✓ Element '{fullname}' updated.";
                foreach (var adv in overwriteAdvisories)
                    result += $"\n  Advisory: {adv}.";
                return result;
            }
            catch
            {
                txn.Rollback();
                throw;
            }
        }
        catch (Exception ex)
        {
            return $"Error updating element: {ex.Message}";
        }
    }

    // ── delete_element ────────────────────────────────────────────────────────

    [McpServerTool(Name = "delete_element")]
    [Description(
        "Deletes an element from the database. " +
        "Fails if the element has child elements, connections, or attached documents. " +
        "When deletion fails for this reason, treat it as a stop signal: report the affected scope to the user and ask for explicit confirmation – never cascade-delete by removing children or connections first on your own. " +
        "Deletes from all 4 database tables (Element, Part, CItem, CEntity) in a single transaction.")]
    public static string DeleteElement(
        [Description("Full name of the element, e.g. 'House/GF/Kitchen/South-Wall/Socket'")] string fullname)
    {
        fullname = fullname?.Trim() ?? "";
        if (string.IsNullOrEmpty(fullname))
            return "Error: 'fullname' is required.";

        try
        {
            using var conn = FirebirdDb.OpenConnection();
            var (_, _, byFullName) = QueryHelpers.LoadEtree(conn);

            if (!QueryHelpers.TryResolveElementRow(byFullName, fullname, out var targetRow, out var canonicalFullname))
                return $"Error: element '{fullname}' not found.";
            fullname = canonicalFullname;

            var oid = FirebirdDb.Str(targetRow["Oid"]);

            var children = FirebirdDb.ExecuteQuery(conn,
                """SELECT COUNT(*) AS CNT FROM "Element" WHERE "PartOfElement" = ?""", oid);
            var childCount = Convert.ToInt64(children.Count > 0 ? children[0].GetValueOrDefault("CNT") ?? 0L : 0L);
            if (childCount > 0)
                return $"Error: element has {childCount} child element(s). Report this to the user and ask for explicit confirmation before removing any of them.";

            var connections = FirebirdDb.ExecuteQuery(conn,
                """SELECT COUNT(*) AS CNT FROM "Connection" WHERE "Source" = ? OR "Destination" = ?""",
                oid, oid);
            var connCount = Convert.ToInt64(connections.Count > 0 ? connections[0].GetValueOrDefault("CNT") ?? 0L : 0L);
            if (connCount > 0)
                return $"Error: element has {connCount} connection(s). Report this to the user and ask for explicit confirmation before removing any of them.";

            var docError = QueryHelpers.CheckDocumentsAttached(conn, oid);
            if (docError != null) return docError;

            var advisories = QueryHelpers.CollectDeleteAdvisories(conn, oid);

            using var txn = conn.BeginTransaction();
            try
            {
                FirebirdDb.ExecuteNonQuery(conn, txn, """DELETE FROM "Element"        WHERE "Oid"   = ?""", oid);
                FirebirdDb.ExecuteNonQuery(conn, txn, """DELETE FROM "Part"           WHERE "Oid"   = ?""", oid);
                // Remove image associations before CItem (FK has no CASCADE).
                // Table may not exist in all DB versions – skip silently (mirrors CollectDeleteAdvisories read-path).
                try { FirebirdDb.ExecuteNonQuery(conn, txn, """DELETE FROM "ImagesToCItems" WHERE "CItem" = ?""", oid); }
                catch (FbException ex) when (ex.ErrorCode is 335544580 or 335544569) { /* table absent – skip */ }
                FirebirdDb.ExecuteNonQuery(conn, txn, """DELETE FROM "CItem"          WHERE "Oid"   = ?""", oid);
                FirebirdDb.ExecuteNonQuery(conn, txn, """DELETE FROM "CEntity"        WHERE "Oid"   = ?""", oid);
                txn.Commit();

                var result = $"✓ Element '{fullname}' deleted.";
                if (advisories.Count > 0)
                    result += $"\n  Advisory: {string.Join("; ", advisories)}.";
                return result;
            }
            catch
            {
                txn.Rollback();
                throw;
            }
        }
        catch (Exception ex)
        {
            return $"Error deleting element: {ex.Message}";
        }
    }

    // ── move_element ──────────────────────────────────────────────────────────

    [McpServerTool(Name = "move_element")]
    [Description(
        "Moves an element to a different parent (or to top-level). " +
        "The element's full name and all descendant full names change automatically. " +
        "Fails if the new full name would conflict with an existing element, " +
        "if a sibling at the new parent has the same name or short name, " +
        "or if new_parent is a descendant of the element (circular reference). " +
        "The element is appended at the end of the new parent's children.")]
    public static string MoveElement(
        [Description("Full name of the element to move, e.g. 'House/GF/Office/Socket'")] string fullname,
        [Description("Full name of the new parent element, e.g. 'House/FF/Bedroom'. Empty = move to top-level.")] string new_parent = "")
    {
        fullname   = fullname?.Trim()   ?? "";
        new_parent = new_parent?.Trim().TrimEnd('/') ?? "";

        if (string.IsNullOrEmpty(fullname))
            return "Error: 'fullname' is required.";

        try
        {
            using var conn = FirebirdDb.OpenConnection();
            var (_, _, byFullName) = QueryHelpers.LoadEtree(conn);

            if (!QueryHelpers.TryResolveElementRow(byFullName, fullname, out var targetRow, out var canonicalFullname))
                return $"Error: element '{fullname}' not found.";
            fullname = canonicalFullname;

            var oid = FirebirdDb.Str(targetRow["Oid"]);

            string? newParentOid      = null;
            string? newParentFullName = null;
            if (!string.IsNullOrEmpty(new_parent))
            {
                if (!QueryHelpers.TryResolveElementRow(byFullName, new_parent, out var parentRow, out var canonicalNewParent))
                    return $"Error: new parent element '{new_parent}' not found.";
                new_parent = canonicalNewParent;

                newParentOid      = FirebirdDb.Str(parentRow["Oid"]);
                newParentFullName = canonicalNewParent;

                var elementPrefix = fullname.TrimEnd('/') + "/";
                if (new_parent.StartsWith(elementPrefix, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(new_parent, fullname, StringComparison.OrdinalIgnoreCase))
                    return $"Error: cannot move '{fullname}' into itself or one of its descendants.";
            }

            var lastSlash   = fullname.LastIndexOf('/');
            var segmentName = lastSlash >= 0 ? fullname[(lastSlash + 1)..] : fullname;
            var newFullName = newParentFullName != null
                ? $"{newParentFullName}/{segmentName}"
                : segmentName;

            if (!string.Equals(newFullName, fullname, StringComparison.OrdinalIgnoreCase)
                && byFullName.ContainsKey(newFullName))
                return $"Error: an element with full name '{newFullName}' already exists.";

            var elemRows = FirebirdDb.ExecuteQuery(conn,
                """SELECT "Name", "ShortName" FROM "Element" WHERE "Oid" = ?""", oid);
            if (elemRows.Count == 0) return "Error: element data not found.";
            var elemName      = FirebirdDb.Str(elemRows[0]["Name"]);
            var elemShortName = FirebirdDb.Str(elemRows[0].GetValueOrDefault("ShortName")).NullIfEmpty();

            var siblingError = QueryHelpers.CheckSiblingUniqueness(conn, elemName, elemShortName, newParentOid, oid);
            if (siblingError != null) return siblingError;

            var sortIndex = QueryHelpers.NextSortIndex(conn, newParentOid);
            var now       = DateTime.UtcNow;

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
                    UPDATE "Element" SET "PartOfElement" = ?, "SortIndex" = ?
                    WHERE "Oid" = ?
                    """,
                    (object?)newParentOid ?? DBNull.Value,
                    sortIndex,
                    oid);

                txn.Commit();
                return $"✓ Element moved: '{fullname}' → '{newFullName}'.";
            }
            catch
            {
                txn.Rollback();
                throw;
            }
        }
        catch (Exception ex)
        {
            return $"Error moving element: {ex.Message}";
        }
    }
}
