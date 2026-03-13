using System.Reflection;
using System.Text.Json;

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
            SeedCategories(conn, txn);
            SeedElements(conn, txn);
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

    private static void SeedCategories(FbConnection conn, FbTransaction txn)
    {
        var json = ReadEmbedded("SeedData.categories.json");
        var categories = JsonSerializer.Deserialize<CategorySeed[]>(json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];

        foreach (var cat in categories)
            InsertCategory(conn, txn, cat, parentOid: null);
    }

    private static void InsertCategory(FbConnection conn, FbTransaction txn,
        CategorySeed cat, string? parentOid)
    {
        var oid = NewOid();

        // CEntity base row
        FirebirdDb.ExecuteNonQuery(conn, txn,
            "INSERT INTO \"CEntity\" (\"Oid\", \"ObjectType\", \"OptimisticLockField\", \"CreatedOn\", \"CreatedBy\")" +
            " VALUES (?, ?, 0, ?, ?)",
            oid, XPObjectTypes.Category, DateTime.UtcNow, "HomeMemory");

        // Category row
        FirebirdDb.ExecuteNonQuery(conn, txn,
            "INSERT INTO \"Category\" (\"Oid\", \"Name\", \"ShortName\", \"IsAreaCategory\", \"ParentCategory\")" +
            " VALUES (?, ?, ?, ?, ?)",
            oid,
            cat.Name,
            cat.ShortName ?? (object)DBNull.Value,
            cat.IsStructuralArea,
            parentOid ?? (object)DBNull.Value);

        foreach (var child in cat.Children ?? [])
            InsertCategory(conn, txn, child, oid);
    }

    // -------------------------------------------------------------------------
    // Area elements (flat list; parent resolved by FullName path)
    // -------------------------------------------------------------------------

    private static void SeedElements(FbConnection conn, FbTransaction txn)
    {
        var json = ReadEmbedded("SeedData.elements.json");
        var elements = JsonSerializer.Deserialize<ElementSeed[]>(json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];

        // Build OID map keyed by FullName for parent-lookup during insert
        var oidByFullName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var el in elements)
            InsertElement(conn, txn, el, oidByFullName);
    }

    private static void InsertElement(FbConnection conn, FbTransaction txn,
        ElementSeed el, Dictionary<string, string> oidByFullName)
    {
        // Resolve category OID (must pass txn – Firebird requires it on connections with active transactions)
        var catRows = FirebirdDb.ExecuteQuery(conn, txn,
            "SELECT \"Oid\" FROM \"Category\" WHERE \"Name\" = ?", el.Category);
        if (catRows.Count == 0)
            throw new InvalidOperationException($"Seed error: category '{el.Category}' not found.");
        var catOid = FirebirdDb.Str(catRows[0]["Oid"]);

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

    private record CategorySeed(
        string Name,
        string? ShortName,
        bool IsStructuralArea,
        CategorySeed[]? Children);

    private record ElementSeed(
        string Name,
        string Category,
        string? Parent,
        string? ShortName,
        string? Position,
        int SortIndex = 0);
}
