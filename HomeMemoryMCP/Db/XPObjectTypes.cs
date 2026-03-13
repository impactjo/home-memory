namespace HomeMemory.MCP.Db;

/// <summary>
/// Resolves XPO ObjectType discriminator IDs dynamically from the XPObjectType table.
/// These IDs vary between installations and must never be hardcoded.
/// Values are cached after the first lookup — one DB query per type per process lifetime.
/// </summary>
public static class XPObjectTypes
{
    private static int? _element;
    private static int? _connection;
    private static int? _category;

    public static int Element    => _element    ??= Resolve("ConstructionElement");
    public static int Connection => _connection ??= Resolve("ConstructionConnection");
    public static int Category   => _category   ??= Resolve("ConstructionCategory");

    private static int Resolve(string shortClassName)
    {
        using var conn = FirebirdDb.OpenConnection();
        var rows = FirebirdDb.ExecuteQuery(conn,
            "SELECT \"OID\" FROM \"XPObjectType\" WHERE \"TypeName\" LIKE ?",
            $"%{shortClassName}");

        if (rows.Count == 0)
            throw new InvalidOperationException(
                $"XPObjectType entry not found for '{shortClassName}'. " +
                "Ensure the database was created by Smartconstruct.");

        if (rows.Count > 1)
            throw new InvalidOperationException(
                $"Multiple XPObjectType entries found for '{shortClassName}'. " +
                "Cannot determine the correct discriminator ID.");

        return Convert.ToInt32(rows[0]["OID"]);
    }
}
