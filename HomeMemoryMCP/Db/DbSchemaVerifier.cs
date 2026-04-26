namespace HomeMemory.MCP.Db;

/// <summary>
/// Preflight check: verifies the connected database carries the HomeMemory schema.
/// In Server mode (Step 1a) the server-side DB is provisioned out-of-band — either
/// by copying template.scd or by gbak backup→restore — and HM cannot bootstrap an
/// empty server DB itself. A clear error here beats an obscure failure later in
/// the migrator or seeder.
/// </summary>
public static class DbSchemaVerifier
{
    public static void Verify()
    {
        using var conn = FirebirdDb.OpenConnection();

        var required = new[] { "CEntity", "Category", "Element" };
        var missing = new List<string>();
        foreach (var name in required)
        {
            var rows = FirebirdDb.ExecuteQuery(conn,
                "SELECT COUNT(*) AS CNT FROM rdb$relations " +
                "WHERE TRIM(rdb$relation_name) = ? AND rdb$system_flag = 0",
                name);
            if (Convert.ToInt32(rows[0]["CNT"]) == 0)
                missing.Add(name);
        }

        if (missing.Count == 0)
            return;

        throw new InvalidOperationException(
            $"HomeMemory schema not found in database ({DbConfig.Current.DisplayName}). " +
            $"Missing base tables: {string.Join(", ", missing)}. " +
            "The database appears to be empty or wrong. " +
            "Provision it from template.scd (copy the file, or use gbak backup→restore) " +
            "before starting the server. Empty-DB bootstrap on Firebird Server is not supported.");
    }
}
