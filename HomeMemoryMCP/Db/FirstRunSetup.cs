namespace HomeMemory.MCP.Db;

/// <summary>
/// Ensures the HomeMemory database exists in its configured location.
/// On first run, copies the blank template.scd from the application directory.
/// </summary>
public static class FirstRunSetup
{
    public static void EnsureDatabase()
    {
        if (!DbConfig.Current.RequiresLocalTemplate)
            return;

        var dbPath = FirebirdDb.GetDbPath();

        if (File.Exists(dbPath))
            return;

        var templatePath = FirebirdDb.GetTemplatePath();
        if (!File.Exists(templatePath))
            throw new FileNotFoundException(
                $"Database not found at '{dbPath}' and no template.scd found at '{templatePath}'. " +
                "Please place template.scd next to the executable or set HOME_MEMORY_DB_PATH.");

        var dbDir = Path.GetDirectoryName(dbPath)!;
        Directory.CreateDirectory(dbDir);
        File.Copy(templatePath, dbPath);

        Console.Error.WriteLine($"[HomeMemory] Created new database at: {dbPath}");
    }
}
