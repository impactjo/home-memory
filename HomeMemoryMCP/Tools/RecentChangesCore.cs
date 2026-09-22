using HomeMemory.MCP.Db;

namespace HomeMemory.MCP.Tools;

/// <summary>
/// A created or updated item that can participate in the shared recent-changes view.
/// <see cref="ItemName"/> is the complete display label supplied by its source.
/// </summary>
public sealed record RecentChangeEntry(
    string ItemType,
    string ItemName,
    DateTime? CreatedOn,
    DateTime? UpdatedOn);

/// <summary>
/// Supplies one or more item types for the shared recent-changes view.
/// </summary>
public interface IRecentChangeSource
{
    IReadOnlyCollection<string> SupportedTypes { get; }

    /// <summary>
    /// Returns at most <paramref name="limit"/> entries with the newest
    /// <c>COALESCE(UpdatedOn, CreatedOn)</c> values for the requested type.
    /// An empty requested type includes every type supported by this source.
    /// Each source opens and owns its database connection.
    /// </summary>
    IReadOnlyList<RecentChangeEntry> LoadRecentChanges(string requestedType, int limit);
}

/// <summary>
/// Merges and formats recent changes from Home Memory and optional composed sources.
/// </summary>
public static class RecentChangesCore
{
    public static string Execute(
        int limit,
        string? type,
        params IRecentChangeSource[] sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        if (sources.Length == 0)
            throw new ArgumentException("At least one recent-change source is required.", nameof(sources));

        type = (type ?? "").Trim().ToLowerInvariant();
        limit = Math.Clamp(limit, 1, 200);

        var supportedTypes = sources
            .SelectMany(source => source.SupportedTypes)
            .Select(value => value.Trim().ToLowerInvariant())
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (type.Length > 0 && !supportedTypes.Contains(type, StringComparer.OrdinalIgnoreCase))
            return $"Error: 'type' must be {FormatAllowedTypes(supportedTypes)}.";

        try
        {
            var selectedSources = type.Length == 0
                ? sources
                : sources.Where(source => source.SupportedTypes.Contains(type, StringComparer.OrdinalIgnoreCase));
            var entries = selectedSources
                .SelectMany(source => source.LoadRecentChanges(type, limit))
                .OrderByDescending(ChangeTimestamp)
                .Take(limit)
                .ToList();

            if (entries.Count == 0)
                return type.Length == 0
                    ? "No items found in the database."
                    : $"No {type} items found.";

            var typeLabel = type.Length == 0 ? "" : $", type={type}";
            var lines = new List<string> { $"Recent changes ({entries.Count}{typeLabel}, newest first):\n" };
            foreach (var entry in entries)
            {
                var timestamp = ChangeTimestamp(entry);
                var timestampText = timestamp?.ToString("yyyy-MM-dd HH:mm") ?? "                ";
                // Legacy records imported from external tools may not have CreatedOn.
                // Treat a present UpdatedOn as an update when creation time is unavailable.
                var label = entry.UpdatedOn.HasValue
                    && (!entry.CreatedOn.HasValue || entry.UpdatedOn != entry.CreatedOn)
                    ? "[updated]"
                    : "[created]";
                lines.Add($"{timestampText}  {label,-10}  {entry.ItemType,-12}  {entry.ItemName}");
            }
            return string.Join("\n", lines);
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    private static DateTime? ChangeTimestamp(RecentChangeEntry entry) =>
        entry.UpdatedOn ?? entry.CreatedOn;

    private static string FormatAllowedTypes(IReadOnlyList<string> types) => types.Count switch
    {
        0 => "a supported item type",
        1 => $"'{types[0]}'",
        2 => $"'{types[0]}' or '{types[1]}'",
        _ => string.Join(", ", types.Take(types.Count - 1).Select(value => $"'{value}'"))
            + $", or '{types[^1]}'",
    };
}

/// <summary>
/// Supplies elements, connections, and categories from the Home Memory schema.
/// </summary>
public sealed class HomeMemoryRecentChangeSource : IRecentChangeSource
{
    public static HomeMemoryRecentChangeSource Instance { get; } = new();

    public IReadOnlyCollection<string> SupportedTypes { get; } =
        ["element", "line", "category"];

    private HomeMemoryRecentChangeSource()
    {
    }

    public IReadOnlyList<RecentChangeEntry> LoadRecentChanges(string requestedType, int limit)
    {
        using var conn = FirebirdDb.OpenConnection();
        var includeElements = requestedType.Length == 0 || requestedType == "element";
        var includeConnections = requestedType.Length == 0 || requestedType == "line";
        var includeCategories = requestedType.Length == 0 || requestedType == "category";

        var cteParts = new List<string>();
        // Firebird uses one WITH RECURSIVE keyword followed by comma-separated CTE definitions.
        if (includeElements) cteParts.Add("ETREE AS (" + SqlQueries.EtreeCteBody + ")");
        if (includeCategories) cteParts.Add("CAT_TREE AS (" + SqlQueries.CatCteBody + ")");

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
                    'line' AS ITEM_TYPE,
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
        var rows = FirebirdDb.ExecuteQuery(conn, $"""
            {ctePrefix}SELECT * FROM (
                {string.Join("\n                UNION ALL\n", unions)}
            ) items
            ORDER BY CHANGE_TS DESC NULLS LAST
            ROWS 1 TO {limit}
            """);

        return rows.Select(row => new RecentChangeEntry(
                row.Str("ITEM_TYPE"),
                row.Str("ITEM_NAME"),
                NullableDate(row, "CreatedOn"),
                NullableDate(row, "UpdatedOn")))
            .ToList();
    }

    private static DateTime? NullableDate(Row row, string column)
    {
        var value = row.GetValueOrDefault(column);
        return value is null or DBNull ? null : Convert.ToDateTime(value);
    }
}
