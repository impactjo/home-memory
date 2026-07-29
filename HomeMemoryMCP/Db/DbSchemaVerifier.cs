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
    private static readonly SchemaComponentRequirement[] HomeMemoryManifest =
    [
        new("core", 1, 1),
    ];

    public static void Verify()
    {
        using var conn = FirebirdDb.OpenConnection();
        VerifyLegacyOperation(conn);
        if (SchemaRegistry.Exists(conn))
        {
            SchemaRegistry.Verify(conn, HomeMemoryManifest, DbMigrator.GetBaselineEntries());
            return;
        }

        VerifyHomeMemoryLegacyMarker(conn);
        Console.Error.WriteLine(
            "[HomeMemory] Using recognized Home Memory legacy schema as virtual core/1; no database changes were made.");
    }

    public static void VerifyLegacyOperation(FbConnection conn)
    {
        VerifyRequiredTables(conn, ["CEntity", "Category", "Element"]);
    }

    public static void VerifyLegacyBaseline(FbConnection conn)
    {
        VerifyRequiredTables(
            conn,
            [
                "CEntity", "CItem", "Category", "Company", "Connection", "Document",
                "Element", "FileData", "FileSystemLinkObject", "Image", "ImagesToCItems",
                "Part", "PartType", "Status", "XPObjectType",
            ]);
        VerifyForeignKey(conn, "CItem", "Oid", "CEntity", "Oid");
        VerifyForeignKey(conn, "Element", "Oid", "Part", "Oid");
        VerifyForeignKey(conn, "Connection", "Oid", "Part", "Oid");
        VerifyForeignKey(conn, "Part", "Oid", "CItem", "Oid");
        VerifyForeignKey(conn, "PartType", "Oid", "CItem", "Oid");
        VerifyForeignKey(conn, "Document", "Entity", "CEntity", "Oid");
    }

    private static void VerifyRequiredTables(FbConnection conn, IReadOnlyCollection<string> required)
    {
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

        if (missing.Count > 0)
        {
            throw new InvalidOperationException(
                $"HomeMemory schema not found in database ({DbConfig.Current.DisplayName}). " +
                $"Missing base tables: {string.Join(", ", missing)}. " +
                "The database appears to be empty or wrong. " +
                "Provision it from template.scd (copy the file, or use gbak backup to restore) " +
                "before starting the server. Empty database bootstrap on Firebird Server is not supported.");
        }
    }

    public static void VerifyHomeMemoryLegacyMarker(FbConnection connection)
    {
        long markerCount = FirebirdDb.CountResult(FirebirdDb.ExecuteQuery(connection, """
            SELECT COUNT(*) AS CNT
            FROM RDB$RELATIONS
            WHERE TRIM(RDB$RELATION_NAME) = 'HM_MIGRATION'
              AND COALESCE(RDB$SYSTEM_FLAG, 0) = 0
            """));
        if (markerCount != 1)
            throw new InvalidOperationException(
                "Database schema is not registered and is not a recognized Home Memory legacy database.");

        long baselineCount = FirebirdDb.CountResult(FirebirdDb.ExecuteQuery(connection, """
            SELECT COUNT(*) AS CNT
            FROM HM_MIGRATION
            WHERE ID = 1 AND NAME = 'InitialSchema'
            """));
        if (baselineCount != 1)
            throw new InvalidOperationException(
                "Database schema is not registered and its Home Memory legacy marker is not recognized.");
    }

    private static void VerifyForeignKey(
        FbConnection connection,
        string sourceTable,
        string sourceColumn,
        string targetTable,
        string targetColumn)
    {
        long count = FirebirdDb.CountResult(FirebirdDb.ExecuteQuery(connection, """
            SELECT COUNT(*) AS CNT
            FROM RDB$RELATION_CONSTRAINTS fk
            JOIN RDB$REF_CONSTRAINTS ref
              ON ref.RDB$CONSTRAINT_NAME = fk.RDB$CONSTRAINT_NAME
            JOIN RDB$RELATION_CONSTRAINTS target
              ON target.RDB$CONSTRAINT_NAME = ref.RDB$CONST_NAME_UQ
            JOIN RDB$INDEX_SEGMENTS source_segment
              ON source_segment.RDB$INDEX_NAME = fk.RDB$INDEX_NAME
            JOIN RDB$INDEX_SEGMENTS target_segment
              ON target_segment.RDB$INDEX_NAME = target.RDB$INDEX_NAME
             AND target_segment.RDB$FIELD_POSITION = source_segment.RDB$FIELD_POSITION
            WHERE TRIM(fk.RDB$RELATION_NAME) = ?
              AND TRIM(fk.RDB$CONSTRAINT_TYPE) = 'FOREIGN KEY'
              AND TRIM(source_segment.RDB$FIELD_NAME) = ?
              AND TRIM(target.RDB$RELATION_NAME) = ?
              AND TRIM(target_segment.RDB$FIELD_NAME) = ?
            """, sourceTable, sourceColumn, targetTable, targetColumn));

        if (count != 1)
            throw new InvalidOperationException(
                $"HomeMemory schema is incomplete. Missing foreign key: " +
                $"{sourceTable}.{sourceColumn} -> {targetTable}.{targetColumn}.");
    }
}
