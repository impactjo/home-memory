namespace HomeMemory.MCP.Db;

public enum DbMode
{
    Embedded,
    Server
}

/// <summary>
/// Resolved database configuration. Embedded mode owns a local .scd file and
/// can bootstrap from template.scd; Server mode connects to an existing
/// Firebird Server and never creates files locally.
/// </summary>
public sealed record DbConfig(
    DbMode Mode,
    string DisplayName,
    string ConnectionString,
    bool RequiresLocalTemplate)
{
    private static DbConfig? _cached;

    public static DbConfig Current => _cached ??= Build();

    /// <summary>For tests: drop the cached instance so env-var changes take effect.</summary>
    public static void Reset() => _cached = null;

    private static DbConfig Build()
    {
        var modeEnv = Environment.GetEnvironmentVariable("HOME_MEMORY_DB_MODE")?.Trim();
        if (string.IsNullOrEmpty(modeEnv))
            return BuildEmbedded();

        if (string.Equals(modeEnv, "Embedded", StringComparison.OrdinalIgnoreCase))
            return BuildEmbedded();
        if (string.Equals(modeEnv, "Server", StringComparison.OrdinalIgnoreCase))
            return BuildServer();

        throw new InvalidOperationException(
            $"HOME_MEMORY_DB_MODE='{modeEnv}' is not valid. Use 'Embedded' or 'Server'.");
    }

    private static DbConfig BuildEmbedded()
    {
        var dbPath = FirebirdDb.GetDbPath();
        var pooling = Environment.GetEnvironmentVariable("HOME_MEMORY_DB_POOLING")
            is "true" or "1";

        var cs = new FbConnectionStringBuilder
        {
            Database      = dbPath,
            UserID        = "SYSDBA",
            Password      = "masterkey",
            Charset       = "UTF8",
            ServerType    = FbServerType.Embedded,
            ClientLibrary = FirebirdDb.GetClientLibPath(),
            Pooling       = pooling,
        }.ConnectionString;

        return new DbConfig(
            Mode: DbMode.Embedded,
            DisplayName: $"Embedded: {dbPath}",
            ConnectionString: cs,
            RequiresLocalTemplate: true);
    }

    private static DbConfig BuildServer()
    {
        var host = Environment.GetEnvironmentVariable("HOME_MEMORY_DB_HOST")?.Trim();
        if (string.IsNullOrEmpty(host)) host = "localhost";

        var portEnv = Environment.GetEnvironmentVariable("HOME_MEMORY_DB_PORT")?.Trim();
        int port = 3050;
        if (!string.IsNullOrEmpty(portEnv) && !int.TryParse(portEnv, out port))
            throw new InvalidOperationException(
                $"HOME_MEMORY_DB_PORT must be an integer (got '{portEnv}').");

        var dbPath = Environment.GetEnvironmentVariable("HOME_MEMORY_DB_PATH")?.Trim();
        if (string.IsNullOrEmpty(dbPath))
            throw new InvalidOperationException(
                "HOME_MEMORY_DB_MODE=Server requires HOME_MEMORY_DB_PATH " +
                "(server-side path or alias of the existing database).");

        var user = Environment.GetEnvironmentVariable("HOME_MEMORY_DB_USER")?.Trim();
        if (string.IsNullOrEmpty(user))
            throw new InvalidOperationException(
                "HOME_MEMORY_DB_MODE=Server requires HOME_MEMORY_DB_USER " +
                "(the dedicated HM connection user — do not reuse SYSDBA).");

        var password = Environment.GetEnvironmentVariable("HOME_MEMORY_DB_PASSWORD");
        if (string.IsNullOrEmpty(password))
            throw new InvalidOperationException(
                "HOME_MEMORY_DB_MODE=Server requires HOME_MEMORY_DB_PASSWORD.");

        var poolingEnv = Environment.GetEnvironmentVariable("HOME_MEMORY_DB_POOLING")?.Trim();
        var pooling = string.IsNullOrEmpty(poolingEnv)
            || !(string.Equals(poolingEnv, "false", StringComparison.OrdinalIgnoreCase) || poolingEnv == "0");

        var cs = new FbConnectionStringBuilder
        {
            DataSource = host,
            Port       = port,
            Database   = dbPath,
            UserID     = user,
            Password   = password,
            Charset    = "UTF8",
            ServerType = FbServerType.Default,
            Pooling    = pooling,
        }.ConnectionString;

        return new DbConfig(
            Mode: DbMode.Server,
            DisplayName: $"Server: {user}@{host}:{port}/{dbPath}",
            ConnectionString: cs,
            RequiresLocalTemplate: false);
    }
}
