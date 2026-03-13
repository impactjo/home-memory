namespace HomeMemory.MCP.Db;

/// <summary>
/// Applies pending schema migrations on server startup.
/// Each migration runs exactly once; the HM_MIGRATION table is the single source of truth.
/// New migrations must be appended at the end of the list — never inserted.
/// Migrations that include DDL (CREATE TABLE, ADD COLUMN, …) should guard against
/// already-applied structural changes using Firebird system tables (rdb$relations,
/// rdb$relation_fields) — i.e. write them defensively so they are safe to re-run.
/// </summary>
public static class DbMigrator
{
    private static readonly (int Id, string Name, Action<FbConnection, FbTransaction> Apply)[] Migrations =
    [
        (1, "InitialSchema", Migration_001_InitialSchema),
        // append new migrations here
    ];

    public static void MigrateDatabase()
    {
        using var conn = FirebirdDb.OpenConnection();

        EnsureMigrationTableExists(conn);

        var applied = GetAppliedIds(conn);
        var mcpVersion = GetMcpVersion();

        foreach (var (id, name, apply) in Migrations)
        {
            if (applied.Contains(id))
                continue;

            using var txn = conn.BeginTransaction();
            try
            {
                apply(conn, txn);

                FirebirdDb.ExecuteNonQuery(conn, txn,
                    "INSERT INTO HM_MIGRATION (ID, NAME, APPLIED_AT, MCP_VERSION) VALUES (?, ?, ?, ?)",
                    id, name, DateTime.UtcNow, mcpVersion);

                txn.Commit();
                Console.Error.WriteLine($"[HomeMemory] Migration {id:000} applied: {name}");
            }
            catch
            {
                txn.Rollback();
                throw;
            }
        }
    }

    // -------------------------------------------------------------------------
    // Infrastructure
    // -------------------------------------------------------------------------

    private static void EnsureMigrationTableExists(FbConnection conn)
    {
        var rows = FirebirdDb.ExecuteQuery(conn,
            "SELECT COUNT(*) AS CNT FROM rdb$relations WHERE TRIM(rdb$relation_name) = 'HM_MIGRATION'");

        if (Convert.ToInt32(rows[0]["CNT"]) > 0)
            return;

        using var txn = conn.BeginTransaction();
        try
        {
            FirebirdDb.ExecuteNonQuery(conn, txn, @"
                CREATE TABLE HM_MIGRATION (
                    ID          INTEGER      NOT NULL PRIMARY KEY,
                    NAME        VARCHAR(200) NOT NULL,
                    APPLIED_AT  TIMESTAMP    NOT NULL,
                    MCP_VERSION VARCHAR(50)
                )");
            txn.Commit();
            Console.Error.WriteLine("[HomeMemory] Migration table created.");
        }
        catch
        {
            txn.Rollback();
            throw;
        }
    }

    private static HashSet<int> GetAppliedIds(FbConnection conn)
    {
        var rows = FirebirdDb.ExecuteQuery(conn, "SELECT ID FROM HM_MIGRATION");
        return rows.Select(r => Convert.ToInt32(r["ID"])).ToHashSet();
    }

    private static string GetMcpVersion() =>
        typeof(DbMigrator).Assembly
            .GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion
        ?? typeof(DbMigrator).Assembly.GetName().Version?.ToString()
        ?? "unknown";

    // -------------------------------------------------------------------------
    // Migrations
    // -------------------------------------------------------------------------

    /// <summary>
    /// Baseline marker — the schema is provided by template.scd.
    /// Records that the migration system is active from day one.
    /// </summary>
    private static void Migration_001_InitialSchema(FbConnection conn, FbTransaction txn)
    {
        // No structural changes needed — schema is fully provided by template.scd.
        // This entry solely marks the baseline for future migrations.
    }
}
