namespace HomeMemory.MCP.Db;

/// <summary>
/// Owns the Home Memory baseline entries in the shared schema registry. Existing
/// databases are changed only through the dedicated migration command. A newly
/// copied baseline may be initialized explicitly before its first normal start.
/// </summary>
public static class DbMigrator
{
    private static readonly SchemaMigrationEntry[] BaselineEntries =
    [
        new("core", 1, "InitialCoreSchema"),
    ];

    public static IReadOnlyCollection<SchemaMigrationEntry> GetBaselineEntries() =>
        BaselineEntries.ToArray();

    public static DatabaseMigrationResult MigrateDatabase()
    {
        using var maintenanceSession = DatabaseMaintenanceSession.Acquire();
        var result = Backfill(createBackup: true, requireLegacyMarker: true, maintenanceSession);
        DbSchemaVerifier.Verify();
        return result;
    }

    public static DatabaseMigrationResult InitializeNewDatabase()
    {
        using var maintenanceSession = DatabaseMaintenanceSession.Acquire();
        var result = Backfill(createBackup: false, requireLegacyMarker: false, maintenanceSession);
        DbSchemaVerifier.Verify();
        return result;
    }

    private static DatabaseMigrationResult Backfill(
        bool createBackup,
        bool requireLegacyMarker,
        DatabaseMaintenanceSession maintenanceSession) =>
        SchemaMigrationEngine.BackfillMarkerlessDatabase(
            BaselineEntries,
            "HomeMemoryMCP/HomeMemory",
            GetMcpVersion(),
            connection =>
            {
                if (requireLegacyMarker)
                    DbSchemaVerifier.VerifyHomeMemoryLegacyMarker(connection);
                DbSchemaVerifier.VerifyLegacyBaseline(connection);
            },
            createBackup,
            maintenanceSession);

    private static string GetMcpVersion() =>
        typeof(DbMigrator).Assembly
            .GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion
        ?? typeof(DbMigrator).Assembly.GetName().Version?.ToString()
        ?? "unknown";
}
