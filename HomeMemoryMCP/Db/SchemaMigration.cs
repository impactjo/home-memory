using System.Security.Cryptography;

namespace HomeMemory.MCP.Db;

public sealed record SchemaComponentRequirement(
    string Component,
    int MinimumVersion,
    int MaximumVersion);

public sealed record SchemaMigrationEntry(
    string Component,
    int MigrationId,
    string Name,
    string? Checksum = null);

public sealed record DatabaseBackup(
    string Path,
    long Length,
    string Sha256);

public sealed record DatabaseMigrationResult(
    bool Changed,
    DatabaseBackup? Backup);

/// <summary>
/// Reads and validates the shared component migration registry. Unknown components
/// are tolerated. Known components are evaluated according to the host manifest.
/// </summary>
public static class SchemaRegistry
{
    public const string TableName = "SCHEMA_MIGRATION";

    public static bool Exists(FbConnection connection) =>
        FirebirdDb.CountResult(FirebirdDb.ExecuteQuery(connection, """
            SELECT COUNT(*) AS CNT
            FROM RDB$RELATIONS
            WHERE TRIM(RDB$RELATION_NAME) = ?
              AND COALESCE(RDB$SYSTEM_FLAG, 0) = 0
            """, TableName)) == 1;

    public static void Verify(
        FbConnection connection,
        IReadOnlyCollection<SchemaComponentRequirement> requirements,
        IReadOnlyCollection<SchemaMigrationEntry>? knownMigrations = null)
    {
        if (!Exists(connection))
            throw new InvalidOperationException(
                "Database schema is not registered. Run the dedicated migration command before starting the server.");

        VerifyStructure(connection);
        var rows = Read(connection);
        VerifySequences(rows);

        if (knownMigrations != null)
            VerifyKnownMigrations(rows, knownMigrations);

        foreach (var requirement in requirements)
        {
            var componentRows = rows
                .Where(row => string.Equals(row.Component, requirement.Component, StringComparison.Ordinal))
                .OrderBy(row => row.MigrationId)
                .ToArray();

            if (componentRows.Length == 0)
            {
                throw new InvalidOperationException(
                    $"Required schema component '{requirement.Component}' is missing. Run the dedicated migration command.");
            }

            int current = componentRows[^1].MigrationId;
            if (current < requirement.MinimumVersion)
                throw new InvalidOperationException(
                    $"Schema component '{requirement.Component}' is too old at version {current}. " +
                    $"Minimum supported version is {requirement.MinimumVersion}.");
            if (current > requirement.MaximumVersion)
                throw new InvalidOperationException(
                    $"Schema component '{requirement.Component}' is newer than this program supports " +
                    $"({current} > {requirement.MaximumVersion}). Update the program before using this database.");
        }
    }

    public static IReadOnlyList<SchemaMigrationEntry> Read(FbConnection connection)
    {
        var rows = FirebirdDb.ExecuteQuery(connection, """
            SELECT COMPONENT, MIGRATION_ID, NAME, CHECKSUM
            FROM SCHEMA_MIGRATION
            ORDER BY COMPONENT, MIGRATION_ID
            """);

        return rows.Select(row => new SchemaMigrationEntry(
            row.Str("COMPONENT"),
            row.Int("MIGRATION_ID"),
            row.Str("NAME"),
            row.GetValueOrDefault("CHECKSUM") is null ? null : row.Str("CHECKSUM")))
            .ToArray();
    }

    internal static void Create(FbConnection connection)
    {
        using var transaction = connection.BeginTransaction();
        try
        {
            FirebirdDb.ExecuteNonQuery(connection, transaction, """
                CREATE TABLE SCHEMA_MIGRATION (
                    COMPONENT           VARCHAR(100) CHARACTER SET UTF8 NOT NULL,
                    MIGRATION_ID        INTEGER      NOT NULL,
                    NAME                VARCHAR(200) CHARACTER SET UTF8 NOT NULL,
                    APPLIED_AT          TIMESTAMP    NOT NULL,
                    APPLIED_BY          VARCHAR(100) CHARACTER SET UTF8 NOT NULL,
                    APPLICATION_VERSION VARCHAR(50)  CHARACTER SET UTF8 NOT NULL,
                    CHECKSUM            VARCHAR(100) CHARACTER SET UTF8,
                    CONSTRAINT PK_SCHEMA_MIGRATION
                        PRIMARY KEY (COMPONENT, MIGRATION_ID),
                    CONSTRAINT CK_SCHEMA_MIGRATION_ID
                        CHECK (MIGRATION_ID > 0)
                )
                """);
            transaction.Commit();
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    internal static void Insert(
        FbConnection connection,
        FbTransaction transaction,
        SchemaMigrationEntry entry,
        string appliedBy,
        string applicationVersion)
    {
        FirebirdDb.ExecuteNonQuery(connection, transaction, """
            INSERT INTO SCHEMA_MIGRATION
                (COMPONENT, MIGRATION_ID, NAME, APPLIED_AT, APPLIED_BY, APPLICATION_VERSION, CHECKSUM)
            VALUES (?, ?, ?, ?, ?, ?, ?)
            """,
            entry.Component,
            entry.MigrationId,
            entry.Name,
            DateTime.UtcNow,
            appliedBy,
            applicationVersion,
            entry.Checksum);
    }

    internal static void Drop(FbConnection connection)
    {
        using var transaction = connection.BeginTransaction();
        try
        {
            FirebirdDb.ExecuteNonQuery(connection, transaction, "DROP TABLE SCHEMA_MIGRATION");
            transaction.Commit();
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    private static void VerifyStructure(FbConnection connection)
    {
        (string Name, int Type, int? Length, int? CharacterSet, bool Nullable)[] expectedColumns =
        [
            ("COMPONENT", 37, 100, 4, false),
            ("MIGRATION_ID", 8, null, null, false),
            ("NAME", 37, 200, 4, false),
            ("APPLIED_AT", 35, null, null, false),
            ("APPLIED_BY", 37, 100, 4, false),
            ("APPLICATION_VERSION", 37, 50, 4, false),
            ("CHECKSUM", 37, 100, 4, true),
        ];
        var columns = FirebirdDb.ExecuteQuery(connection, """
            SELECT TRIM(rf.RDB$FIELD_NAME) AS FIELD_NAME,
                   f.RDB$FIELD_TYPE AS "FIELD_TYPE",
                   f.RDB$CHARACTER_LENGTH AS "CHAR_LENGTH",
                   f.RDB$CHARACTER_SET_ID AS "CHARACTER_SET_ID",
                   rf.RDB$NULL_FLAG AS "NULL_FLAG"
            FROM RDB$RELATION_FIELDS rf
            JOIN RDB$FIELDS f ON f.RDB$FIELD_NAME = rf.RDB$FIELD_SOURCE
            WHERE TRIM(rf.RDB$RELATION_NAME) = 'SCHEMA_MIGRATION'
            ORDER BY rf.RDB$FIELD_POSITION
            """);

        if (columns.Count != expectedColumns.Length)
            throw new InvalidOperationException("SCHEMA_MIGRATION has an unexpected column layout. The schema is inconsistent.");

        for (int index = 0; index < expectedColumns.Length; index++)
        {
            var expected = expectedColumns[index];
            var actual = columns[index];
            int? length = actual.GetValueOrDefault("CHAR_LENGTH") is null
                ? null
                : Convert.ToInt32(actual["CHAR_LENGTH"]);
            int? characterSet = actual.GetValueOrDefault("CHARACTER_SET_ID") is null
                ? null
                : Convert.ToInt32(actual["CHARACTER_SET_ID"]);
            bool nullable = actual.GetValueOrDefault("NULL_FLAG") is null;
            if (!string.Equals(actual.Str("FIELD_NAME"), expected.Name, StringComparison.Ordinal)
                || actual.Int("FIELD_TYPE") != expected.Type
                || length != expected.Length
                || characterSet != expected.CharacterSet
                || nullable != expected.Nullable)
            {
                throw new InvalidOperationException(
                    $"SCHEMA_MIGRATION column {index + 1} does not match '{expected.Name}'. The schema is inconsistent.");
            }
        }

        long primaryKeyCount = FirebirdDb.CountResult(FirebirdDb.ExecuteQuery(connection, """
            SELECT COUNT(*) AS CNT
            FROM RDB$RELATION_CONSTRAINTS
            WHERE TRIM(RDB$RELATION_NAME) = 'SCHEMA_MIGRATION'
              AND TRIM(RDB$CONSTRAINT_TYPE) = 'PRIMARY KEY'
            """));
        if (primaryKeyCount != 1)
            throw new InvalidOperationException("SCHEMA_MIGRATION has no valid primary key. The schema is inconsistent.");

        var primaryKeyColumns = FirebirdDb.ExecuteQuery(connection, """
            SELECT TRIM(segment.RDB$FIELD_NAME) AS FIELD_NAME
            FROM RDB$RELATION_CONSTRAINTS constraint_row
            JOIN RDB$INDEX_SEGMENTS segment
              ON segment.RDB$INDEX_NAME = constraint_row.RDB$INDEX_NAME
            WHERE TRIM(constraint_row.RDB$RELATION_NAME) = 'SCHEMA_MIGRATION'
              AND TRIM(constraint_row.RDB$CONSTRAINT_TYPE) = 'PRIMARY KEY'
            ORDER BY segment.RDB$FIELD_POSITION
            """).Select(row => row.Str("FIELD_NAME")).ToArray();
        if (!primaryKeyColumns.SequenceEqual(["COMPONENT", "MIGRATION_ID"], StringComparer.Ordinal))
            throw new InvalidOperationException("SCHEMA_MIGRATION has an unexpected primary key. The schema is inconsistent.");

        long checkCount = FirebirdDb.CountResult(FirebirdDb.ExecuteQuery(connection, """
            SELECT COUNT(*) AS CNT
            FROM RDB$RELATION_CONSTRAINTS
            WHERE TRIM(RDB$RELATION_NAME) = 'SCHEMA_MIGRATION'
              AND TRIM(RDB$CONSTRAINT_NAME) = 'CK_SCHEMA_MIGRATION_ID'
              AND TRIM(RDB$CONSTRAINT_TYPE) = 'CHECK'
            """));
        if (checkCount != 1)
            throw new InvalidOperationException("SCHEMA_MIGRATION has no valid migration ID check. The schema is inconsistent.");
    }

    private static void VerifySequences(IReadOnlyList<SchemaMigrationEntry> rows)
    {
        if (rows.Count == 0)
            throw new InvalidOperationException("SCHEMA_MIGRATION is empty. The schema is inconsistent.");

        foreach (var group in rows.GroupBy(row => row.Component, StringComparer.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(group.Key))
                throw new InvalidOperationException("SCHEMA_MIGRATION contains an empty component ID. The schema is inconsistent.");

            int expected = 1;
            foreach (var row in group.OrderBy(row => row.MigrationId))
            {
                if (string.IsNullOrWhiteSpace(row.Name))
                    throw new InvalidOperationException(
                        $"Schema component '{group.Key}' contains an empty migration name. The schema is inconsistent.");
                if (row.MigrationId != expected)
                    throw new InvalidOperationException(
                        $"Schema component '{group.Key}' has a migration gap at {expected}. The schema is inconsistent.");
                expected++;
            }
        }
    }

    private static void VerifyKnownMigrations(
        IReadOnlyCollection<SchemaMigrationEntry> actualRows,
        IReadOnlyCollection<SchemaMigrationEntry> knownMigrations)
    {
        foreach (var expected in knownMigrations)
        {
            var actual = actualRows.SingleOrDefault(row =>
                string.Equals(row.Component, expected.Component, StringComparison.Ordinal)
                && row.MigrationId == expected.MigrationId);
            if (actual == null)
                continue;

            if (!string.Equals(actual.Name, expected.Name, StringComparison.Ordinal)
                || !string.Equals(actual.Checksum, expected.Checksum, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Schema migration '{expected.Component}'/{expected.MigrationId} does not match its canonical definition.");
            }
        }
    }
}

/// <summary>
/// Provides the cooperative process lease used by normal servers and dedicated
/// Embedded database maintenance commands.
/// </summary>
public static class DatabaseLifecycleLease
{
    public static IDisposable? AcquireServing()
    {
        if (DbConfig.Current.Mode != DbMode.Embedded)
            return null;

        string leasePath = GetLeasePath(FirebirdDb.GetDbPath());
        Directory.CreateDirectory(Path.GetDirectoryName(leasePath)!);
        try
        {
            return new FileStream(leasePath, FileMode.OpenOrCreate, FileAccess.Read, FileShare.Read);
        }
        catch (IOException exception)
        {
            throw new InvalidOperationException(
                "Database maintenance is active. Wait for it to finish before starting the server.", exception);
        }
    }

    internal static IDisposable AcquireMaintenance(string databasePath)
    {
        string leasePath = GetLeasePath(databasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(leasePath)!);
        try
        {
            return new FileStream(leasePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException exception)
        {
            throw new InvalidOperationException(
                "The database is in use by a cooperating server process. Stop all MCP servers and retry the migration.",
                exception);
        }
    }

    internal static string GetLeasePath(string databasePath) =>
        Path.GetFullPath(databasePath) + ".schema-maintenance.lock";
}

public sealed class DatabaseMaintenanceSession : IDisposable
{
    private readonly IDisposable lease;

    private DatabaseMaintenanceSession(string databasePath, IDisposable lease)
    {
        DatabasePath = databasePath;
        this.lease = lease;
    }

    public string DatabasePath { get; }

    internal void ValidateCurrentDatabase()
    {
        string currentPath = Path.GetFullPath(FirebirdDb.GetDbPath());
        if (!string.Equals(currentPath, DatabasePath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "The configured database changed while a maintenance session was active.");
    }

    public static DatabaseMaintenanceSession Acquire()
    {
        if (DbConfig.Current.Mode != DbMode.Embedded)
            throw new InvalidOperationException("Schema migration currently supports Firebird Embedded only.");

        string databasePath = Path.GetFullPath(FirebirdDb.GetDbPath());
        var lease = DatabaseLifecycleLease.AcquireMaintenance(databasePath);
        FbConnection.ClearAllPools();
        return new DatabaseMaintenanceSession(databasePath, lease);
    }

    public void Dispose()
    {
        FbConnection.ClearAllPools();
        lease.Dispose();
    }
}

/// <summary>
/// Initializes the shared registry on a verified markerless Embedded database.
/// Registry DDL and initial rows use separate transactions because Firebird does
/// not reliably expose newly created metadata to DML before the DDL commit.
/// </summary>
public static class SchemaMigrationEngine
{
    public static DatabaseMigrationResult BackfillMarkerlessDatabase(
        IReadOnlyCollection<SchemaMigrationEntry> entries,
        string appliedBy,
        string applicationVersion,
        Action<FbConnection> verifyLegacyDatabase,
        bool createBackup,
        DatabaseMaintenanceSession? maintenanceSession = null)
    {
        using var ownedSession = maintenanceSession is null ? DatabaseMaintenanceSession.Acquire() : null;
        var session = maintenanceSession ?? ownedSession!;
        session.ValidateCurrentDatabase();
        string databasePath = session.DatabasePath;
        ValidateMetadata(appliedBy, applicationVersion);
        ValidateBackfillEntries(entries);

        using (var connection = FirebirdDb.OpenConnection(commandTimeoutSeconds: 0))
        {
            if (SchemaRegistry.Exists(connection))
                return new DatabaseMigrationResult(false, null);
            verifyLegacyDatabase(connection);
        }

        DatabaseBackup? backup = createBackup
            ? CreateVerifiedBackup(databasePath, verifyLegacyDatabase)
            : null;

        bool registryCreated = false;
        try
        {
            using var connection = FirebirdDb.OpenConnection(commandTimeoutSeconds: 0);
            SchemaRegistry.Create(connection);
            registryCreated = true;

            using var transaction = connection.BeginTransaction();
            try
            {
                foreach (var entry in entries)
                    SchemaRegistry.Insert(connection, transaction, entry, appliedBy, applicationVersion);
                transaction.Commit();
            }
            catch
            {
                transaction.Rollback();
                throw;
            }

            using var verificationConnection = FirebirdDb.OpenConnection(commandTimeoutSeconds: 0);
            verifyLegacyDatabase(verificationConnection);
            SchemaRegistry.Verify(verificationConnection, Array.Empty<SchemaComponentRequirement>(), entries);

            return new DatabaseMigrationResult(true, backup);
        }
        catch
        {
            if (registryCreated)
            {
                try
                {
                    using var connection = FirebirdDb.OpenConnection(commandTimeoutSeconds: 0);
                    SchemaRegistry.Drop(connection);
                }
                catch (Exception compensationException)
                {
                    throw new InvalidOperationException(
                        "Schema registry initialization failed and its compensation also failed. " +
                        "The database must be treated as inconsistent and restored from backup.",
                        compensationException);
                }
            }
            throw;
        }
    }

    public static DatabaseMigrationResult ApplyComponentMigration(
        SchemaMigrationEntry entry,
        string appliedBy,
        string applicationVersion,
        Func<FbConnection, bool> targetExists,
        Action<FbConnection> verifyBeforeMigration,
        Action<FbConnection, FbTransaction> apply,
        Action<FbConnection, FbTransaction> verifyAfterMigration,
        bool createBackup = true,
        DatabaseMaintenanceSession? maintenanceSession = null,
        bool commitBeforeVerification = false,
        Action<FbConnection, FbTransaction>? compensate = null,
        Action<FbConnection, FbTransaction>? migrateData = null)
    {
        using var ownedSession = maintenanceSession is null ? DatabaseMaintenanceSession.Acquire() : null;
        var session = maintenanceSession ?? ownedSession!;
        session.ValidateCurrentDatabase();
        string databasePath = session.DatabasePath;
        ValidateMetadata(appliedBy, applicationVersion);
        ValidateEntry(entry);

        if (commitBeforeVerification && compensate == null)
            throw new ArgumentException(
                "A migration that commits before verification requires a compensation action.",
                nameof(compensate));

        bool exists;
        using (var connection = FirebirdDb.OpenConnection(commandTimeoutSeconds: 0))
        {
            SchemaRegistry.Verify(connection, Array.Empty<SchemaComponentRequirement>(), [entry]);
            var applied = SchemaRegistry.Read(connection)
                .Where(row => string.Equals(row.Component, entry.Component, StringComparison.Ordinal))
                .OrderBy(row => row.MigrationId)
                .ToArray();

            var current = applied.LastOrDefault();
            if (current != null && current.MigrationId >= entry.MigrationId)
            {
                if (current.MigrationId > entry.MigrationId)
                    throw new InvalidOperationException(
                        $"Schema component '{entry.Component}' is newer than migration {entry.MigrationId}.");

                using var verificationTransaction = connection.BeginTransaction();
                try
                {
                    verifyAfterMigration(connection, verificationTransaction);
                    verificationTransaction.Commit();
                }
                catch
                {
                    verificationTransaction.Rollback();
                    throw;
                }
                return new DatabaseMigrationResult(false, null);
            }

            int expectedPrevious = entry.MigrationId - 1;
            int actualPrevious = current?.MigrationId ?? 0;
            if (actualPrevious != expectedPrevious)
                throw new InvalidOperationException(
                    $"Schema component '{entry.Component}' expected predecessor {expectedPrevious}, " +
                    $"but found {actualPrevious}.");

            verifyBeforeMigration(connection);
            exists = targetExists(connection);
            // Only the registry entry proves that data migration committed. Existing
            // schema can also be the result of an interrupted, separately committed DDL.
            if (exists && migrateData != null)
                throw new InvalidOperationException(
                    $"Schema migration '{entry.Component}'/{entry.MigrationId} has an existing target " +
                    "but no completion entry. Data migration completion cannot be established. " +
                    "Restore the database from the pre-migration backup before retrying.");
        }

        DatabaseBackup? backup = createBackup
            ? CreateVerifiedBackup(databasePath, verifyBeforeMigration)
            : null;

        bool targetCreated = false;
        try
        {
            if (!exists && commitBeforeVerification)
            {
                using var applyConnection = FirebirdDb.OpenConnection(commandTimeoutSeconds: 0);
                using var applyTransaction = applyConnection.BeginTransaction();
                try
                {
                    apply(applyConnection, applyTransaction);
                    applyTransaction.Commit();
                    targetCreated = true;
                }
                catch
                {
                    applyTransaction.Rollback();
                    throw;
                }
            }

            using var connection = FirebirdDb.OpenConnection(commandTimeoutSeconds: 0);
            using var transaction = connection.BeginTransaction();
            try
            {
                if (!exists && !commitBeforeVerification)
                    apply(connection, transaction);
                // Data migration runs after committed DDL, when new metadata is visible,
                // and commits atomically with verification and the registry entry.
                migrateData?.Invoke(connection, transaction);
                verifyAfterMigration(connection, transaction);
                SchemaRegistry.Insert(connection, transaction, entry, appliedBy, applicationVersion);
                transaction.Commit();
            }
            catch
            {
                transaction.Rollback();
                throw;
            }
        }
        catch
        {
            if (targetCreated && compensate != null)
            {
                try
                {
                    using var compensationConnection = FirebirdDb.OpenConnection(commandTimeoutSeconds: 0);
                    using var compensationTransaction = compensationConnection.BeginTransaction();
                    try
                    {
                        compensate(compensationConnection, compensationTransaction);
                        compensationTransaction.Commit();
                    }
                    catch
                    {
                        compensationTransaction.Rollback();
                        throw;
                    }
                }
                catch (Exception compensationException)
                {
                    throw new InvalidOperationException(
                        $"Schema migration '{entry.Component}'/{entry.MigrationId} failed and its compensation also failed. " +
                        "The database must be treated as inconsistent and restored from backup.",
                        compensationException);
                }
            }
            throw;
        }

        using (var verificationConnection = FirebirdDb.OpenConnection(commandTimeoutSeconds: 0))
        {
            SchemaRegistry.Verify(
                verificationConnection,
                Array.Empty<SchemaComponentRequirement>(),
                [entry]);
            using var verificationTransaction = verificationConnection.BeginTransaction();
            verifyAfterMigration(verificationConnection, verificationTransaction);
            verificationTransaction.Commit();
        }

        return new DatabaseMigrationResult(true, backup);
    }

    private static void ValidateBackfillEntries(IReadOnlyCollection<SchemaMigrationEntry> entries)
    {
        if (entries.Count == 0)
            throw new ArgumentException("A schema backfill requires at least one migration entry.", nameof(entries));

        foreach (var entry in entries)
            ValidateEntry(entry);

        foreach (var group in entries.GroupBy(entry => entry.Component, StringComparer.Ordinal))
        {
            int expected = 1;
            foreach (var entry in group.OrderBy(entry => entry.MigrationId))
            {
                if (entry.MigrationId != expected)
                    throw new ArgumentException(
                        $"Backfill component '{group.Key}' has a migration gap at {expected}.",
                        nameof(entries));
                expected++;
            }
        }
    }

    private static void ValidateEntry(SchemaMigrationEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (string.IsNullOrWhiteSpace(entry.Component) || entry.Component.Length > 100)
            throw new ArgumentException("Schema component IDs must contain 1 to 100 characters.", nameof(entry));
        if (entry.MigrationId <= 0)
            throw new ArgumentException("Schema migration IDs must be positive.", nameof(entry));
        if (string.IsNullOrWhiteSpace(entry.Name) || entry.Name.Length > 200)
            throw new ArgumentException("Schema migration names must contain 1 to 200 characters.", nameof(entry));
        if (entry.Checksum?.Length > 100)
            throw new ArgumentException("Schema migration checksums must not exceed 100 characters.", nameof(entry));
    }

    private static void ValidateMetadata(string appliedBy, string applicationVersion)
    {
        if (string.IsNullOrWhiteSpace(appliedBy) || appliedBy.Length > 100)
            throw new ArgumentException("Applied-by identifiers must contain 1 to 100 characters.", nameof(appliedBy));
        if (string.IsNullOrWhiteSpace(applicationVersion) || applicationVersion.Length > 50)
            throw new ArgumentException(
                "Application versions must contain 1 to 50 characters.",
                nameof(applicationVersion));
    }

    private static DatabaseBackup CreateVerifiedBackup(
        string databasePath,
        Action<FbConnection> verifyLegacyDatabase)
    {
        string timestamp = DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffZ");
        string backupPath = databasePath + ".pre-migration-" + timestamp + ".bak";

        FbConnection.ClearAllPools();
        using (var source = new FileStream(databasePath, FileMode.Open, FileAccess.Read, FileShare.None))
        using (var destination = new FileStream(backupPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            source.CopyTo(destination);

        try
        {
            var builder = new FbConnectionStringBuilder(DbConfig.Current.ConnectionString)
            {
                Database = backupPath,
                Pooling = false,
            };
            using var backupConnection = new FbConnection(builder.ConnectionString);
            backupConnection.Open();
            verifyLegacyDatabase(backupConnection);
        }
        catch
        {
            try { File.Delete(backupPath); } catch { }
            throw;
        }

        var backupFile = new FileInfo(backupPath);
        string hash;
        using (var stream = new FileStream(backupPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            hash = Convert.ToHexString(SHA256.HashData(stream));

        // Reported before any change, so a later failure still names the restore point.
        Console.Error.WriteLine($"[Database] Verified backup before migration: {backupPath}");
        Console.Error.WriteLine($"[Database] Backup SHA-256: {hash}");
        return new DatabaseBackup(backupPath, backupFile.Length, hash);
    }
}
