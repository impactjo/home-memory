namespace HomeMemory.MCP.Db;

/// <summary>
/// Ensures the HomeMemory database exists in its configured location.
/// On first run, copies the blank template.scd from the application directory to a
/// staging file, registers and seeds it there, and only then moves it to the configured
/// path. An interrupted first run therefore never leaves a partial database behind.
/// </summary>
public static class FirstRunSetup
{
    /// <summary>
    /// Returns true when this process created the database. Returns false when the
    /// database already existed or a concurrent first run created it first.
    /// </summary>
    public static bool EnsureDatabase()
    {
        if (!DbConfig.Current.RequiresLocalTemplate)
            return false;

        var dbPath = FirebirdDb.GetDbPath();

        if (File.Exists(dbPath))
            return false;

        var templatePath = FirebirdDb.GetTemplatePath();
        if (!File.Exists(templatePath))
            throw new FileNotFoundException(
                $"Database not found at '{dbPath}' and no template.scd found at '{templatePath}'. " +
                "Please place template.scd next to the executable or set HOME_MEMORY_DB_PATH.");

        var dbDir = Path.GetDirectoryName(dbPath)!;
        Directory.CreateDirectory(dbDir);

        // Same directory as the target, so the final move is an atomic rename.
        var stagingPath = $"{dbPath}.{Guid.NewGuid():N}.creating";
        try
        {
            File.Copy(templatePath, stagingPath);
            using (FirebirdDb.UseDatabasePath(stagingPath))
            {
                DbMigrator.InitializeNewDatabase();
                DbSeeder.SeedIfEmpty();
            }

            try
            {
                File.Move(stagingPath, dbPath);
            }
            catch (IOException) when (File.Exists(dbPath))
            {
                Console.Error.WriteLine($"[HomeMemory] Using database created by a concurrent first start: {dbPath}");
                return false;
            }
        }
        finally
        {
            TryDelete(stagingPath);
            TryDelete(DatabaseLifecycleLease.GetLeasePath(stagingPath));
        }

        Console.Error.WriteLine($"[HomeMemory] Created new database at: {dbPath}");
        return true;
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
