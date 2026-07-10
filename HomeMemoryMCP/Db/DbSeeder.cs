using System.Reflection;
using System.Text.Json;
using HomeMemory.MCP.Tools;

namespace HomeMemory.MCP.Db;

/// <summary>
/// Seeds a fresh HomeMemory database with default categories, statuses, and area elements.
/// Reads data from embedded JSON files (SeedData/*.json).
/// Does nothing if the database already contains data.
/// </summary>
public static class DbSeeder
{
    public static void SeedIfEmpty()
    {
        using var conn = FirebirdDb.OpenConnection();

        // Check if any categories exist — if yes, DB is already seeded
        var existing = FirebirdDb.ExecuteQuery(conn,
            "SELECT COUNT(*) AS CNT FROM \"Category\"");
        var count = Convert.ToInt32(existing[0]["CNT"]);
        if (count > 0)
            return;

        Console.Error.WriteLine("[HomeMemory] Seeding default data...");

        using var txn = conn.BeginTransaction();
        try
        {
            SeedStatuses(conn, txn);
            var categoryPathToOid = SeedCategories(conn, txn);
            SeedElements(conn, txn, categoryPathToOid);
            txn.Commit();
            Console.Error.WriteLine("[HomeMemory] Seed complete.");
        }
        catch
        {
            txn.Rollback();
            throw;
        }
    }

    // -------------------------------------------------------------------------
    // Statuses
    // -------------------------------------------------------------------------

    private static void SeedStatuses(FbConnection conn, FbTransaction txn)
    {
        var json = ReadEmbedded("SeedData.statuses.json");
        var statuses = JsonSerializer.Deserialize<StatusSeed[]>(json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];

        foreach (var s in statuses)
        {
            var oid = NewOid();
            FirebirdDb.ExecuteNonQuery(conn, txn,
                "INSERT INTO \"Status\" (\"Oid\", \"Name\", \"StatusType\", \"OptimisticLockField\")" +
                " VALUES (?, ?, ?, 0)",
                oid, s.Name, s.Type);
        }
    }

    // -------------------------------------------------------------------------
    // Categories (recursive tree)
    // -------------------------------------------------------------------------

    /// <summary>
    /// Inserts the full category tree and returns a map of each category's canonical path
    /// (canonical ShortName-or-Name segments separated by '/', e.g. "HVAC/Heating") to its OID.
    /// The map is the only way elements resolve their category — see <see cref="SeedElements"/>.
    /// </summary>
    private static Dictionary<string, string> SeedCategories(FbConnection conn, FbTransaction txn)
    {
        var json = ReadEmbedded("SeedData.categories.json");
        var categories = JsonSerializer.Deserialize<CategorySeed[]>(json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];

        // Canonical category path → OID. Case-insensitive resolution, mirroring the
        // production tools. Segments use the same rule as the CAT-CTE (SqlQueries):
        // trimmed ShortName when present, otherwise Name — e.g. "HVAC/Heating".
        var pathToOid = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var cat in categories)
            InsertCategory(conn, txn, cat, parentOid: null, parentPath: null, pathToOid);

        return pathToOid;
    }

    /// <summary>
    /// Canonical path segment for a category: trimmed ShortName when non-empty, else Name.
    /// Matches COALESCE(NULLIF(TRIM("ShortName"), ''), "Name") used by the CAT-CTE.
    /// </summary>
    private static string CategorySegment(CategorySeed cat)
    {
        var shortName = cat.ShortName?.Trim();
        return string.IsNullOrEmpty(shortName) ? cat.Name : shortName;
    }

    internal static void InsertCategory(FbConnection conn, FbTransaction txn,
        CategorySeed cat, string? parentOid, string? parentPath,
        Dictionary<string, string> pathToOid)
    {
        var oid = NewOid();

        var lengthError = Validate.Length(cat.Purpose, "purpose", 200);
        if (lengthError != null)
            throw new InvalidOperationException($"Seed error for category '{cat.Name}': {lengthError}");

        // CEntity base row
        FirebirdDb.ExecuteNonQuery(conn, txn,
            "INSERT INTO \"CEntity\" (\"Oid\", \"ObjectType\", \"OptimisticLockField\", \"CreatedOn\", \"CreatedBy\", \"Purpose\")" +
            " VALUES (?, ?, 0, ?, ?, ?)",
            oid, XPObjectTypes.Category, DateTime.UtcNow, "HomeMemory",
            cat.Purpose ?? (object)DBNull.Value);

        // Category row
        FirebirdDb.ExecuteNonQuery(conn, txn,
            "INSERT INTO \"Category\" (\"Oid\", \"Name\", \"ShortName\", \"IsAreaCategory\", \"ParentCategory\")" +
            " VALUES (?, ?, ?, ?, ?)",
            oid,
            cat.Name,
            cat.ShortName ?? (object)DBNull.Value,
            cat.IsPrimaryArea,
            parentOid ?? (object)DBNull.Value);

        var segment = CategorySegment(cat);
        var path = parentPath != null ? $"{parentPath}/{segment}" : segment;
        if (!pathToOid.TryAdd(path, oid))
        {
            var existingOid = pathToOid[path];
            var existingRows = FirebirdDb.ExecuteQuery(conn, txn,
                "SELECT \"Name\" FROM \"Category\" WHERE \"Oid\" = ?", existingOid);
            var existingName = existingRows.Count > 0 ? FirebirdDb.Str(existingRows[0]["Name"]) : "?";
            throw new InvalidOperationException(
                $"Seed error: two categories resolve to the same canonical path '{path}': " +
                $"existing '{existingName}' (OID {existingOid}), conflicting '{cat.Name}' (OID {oid}). " +
                "Canonical category paths (ShortName-or-Name segments) must be unique.");
        }

        foreach (var child in cat.Children ?? [])
            InsertCategory(conn, txn, child, oid, path, pathToOid);
    }

    // -------------------------------------------------------------------------
    // Area elements (flat list; parent resolved by FullName path)
    // -------------------------------------------------------------------------

    private static void SeedElements(FbConnection conn, FbTransaction txn,
        IReadOnlyDictionary<string, string> categoryPathToOid)
    {
        var json = ReadEmbedded("SeedData.elements.json");
        var elements = JsonSerializer.Deserialize<ElementSeed[]>(json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];

        // Build OID map keyed by FullName for parent-lookup during insert
        var oidByFullName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var el in elements)
            InsertElement(conn, txn, el, oidByFullName, categoryPathToOid);
    }

    /// <summary>
    /// Resolves a category by its full path (e.g. "Area/Building/House") via the map built
    /// while seeding categories. Only exact full paths resolve — short leaf names do not,
    /// so same-named leaves under different parents stay unambiguous.
    /// </summary>
    internal static string ResolveCategoryOid(string categoryPath,
        IReadOnlyDictionary<string, string> categoryPathToOid)
    {
        if (!categoryPathToOid.TryGetValue(categoryPath, out var oid))
            throw new InvalidOperationException(
                $"Seed error: category path '{categoryPath}' not found. " +
                "Use the full category path with '/'-separated segments, e.g. 'Area/Building/House'.");
        return oid;
    }

    private static void InsertElement(FbConnection conn, FbTransaction txn,
        ElementSeed el, Dictionary<string, string> oidByFullName,
        IReadOnlyDictionary<string, string> categoryPathToOid)
    {
        // Resolve category by full path only – no short-name fallback (would reintroduce
        // ambiguity for same-named leaves under different parents).
        var catOid = ResolveCategoryOid(el.Category, categoryPathToOid);

        // Resolve parent OID (if any)
        string? parentOid = null;
        if (el.Parent != null)
        {
            if (!oidByFullName.TryGetValue(el.Parent, out parentOid))
                throw new InvalidOperationException(
                    $"Seed error: parent element '{el.Parent}' not yet inserted (check ordering in elements.json).");
        }

        var oid = NewOid();

        // CEntity base row
        FirebirdDb.ExecuteNonQuery(conn, txn,
            "INSERT INTO \"CEntity\" (\"Oid\", \"ObjectType\", \"OptimisticLockField\", \"CreatedOn\", \"CreatedBy\")" +
            " VALUES (?, ?, 0, ?, ?)",
            oid, XPObjectTypes.Element, DateTime.UtcNow, "HomeMemory");

        // CItem row (category link)
        FirebirdDb.ExecuteNonQuery(conn, txn,
            "INSERT INTO \"CItem\" (\"Oid\", \"Category\") VALUES (?, ?)",
            oid, catOid);

        // Part row (required by schema, PartType nullable)
        FirebirdDb.ExecuteNonQuery(conn, txn,
            "INSERT INTO \"Part\" (\"Oid\", \"PartType\") VALUES (?, ?)",
            oid, DBNull.Value);

        // Element row
        FirebirdDb.ExecuteNonQuery(conn, txn,
            "INSERT INTO \"Element\" (\"Oid\", \"Name\", \"ShortName\", \"PartOfElement\", \"Position\", \"SortIndex\")" +
            " VALUES (?, ?, ?, ?, ?, ?)",
            oid,
            el.Name,
            el.ShortName ?? (object)DBNull.Value,
            parentOid ?? (object)DBNull.Value,
            el.Position ?? (object)DBNull.Value,
            el.SortIndex);

        // Register for child lookups
        var fullName = el.Parent != null ? $"{el.Parent}/{el.Name}" : el.Name;
        oidByFullName[fullName] = oid;
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static string NewOid() => Guid.NewGuid().ToString("D");

    private static string ReadEmbedded(string resourceSuffix)
    {
        var assembly = Assembly.GetExecutingAssembly();
        // Full resource name: <RootNamespace>.<path>
        var resourceName = $"HomeMemory.MCP.{resourceSuffix}";
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded resource '{resourceName}' not found.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    // -------------------------------------------------------------------------
    // Seed model types (parsed from JSON)
    // -------------------------------------------------------------------------

    private record StatusSeed(string Name, int Type);

    internal record CategorySeed(
        string Name,
        string? ShortName,
        bool IsPrimaryArea,
        string? Purpose,
        CategorySeed[]? Children);

    private record ElementSeed(
        string Name,
        string Category,
        string? Parent,
        string? ShortName,
        string? Position,
        int SortIndex = 0);
}
