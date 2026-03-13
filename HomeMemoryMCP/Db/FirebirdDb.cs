using System.Text.RegularExpressions;

namespace HomeMemory.MCP.Db;

public static class FirebirdDb
{
    /// <summary>
    /// Resolves the Firebird client DLL path and sets the FIREBIRD env var
    /// so the embedded engine finds its runtime files (firebird.msg, ICU DLLs, etc.).
    /// Priority: HOME_MEMORY_FBCLIENT env var → fbclient.dll bundled next to exe → default installation path.
    /// </summary>
    public static string GetClientLibPath()
    {
        var env = Environment.GetEnvironmentVariable("HOME_MEMORY_FBCLIENT");
        if (!string.IsNullOrWhiteSpace(env) && File.Exists(env.Trim()))
            return SetFirebirdHome(env.Trim());

        var bundled = Path.Combine(AppContext.BaseDirectory, "fbclient.dll");
        if (File.Exists(bundled))
            return SetFirebirdHome(bundled);

        return SetFirebirdHome(@"C:\Program Files\Firebird\Firebird_3_0\fbclient.dll");
    }

    private static string SetFirebirdHome(string clientLibPath)
    {
        var dir = Path.GetDirectoryName(clientLibPath);
        if (dir != null && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("FIREBIRD")))
            Environment.SetEnvironmentVariable("FIREBIRD", dir);
        return clientLibPath;
    }

    /// <summary>
    /// Returns the DB path from HOME_MEMORY_DB_PATH env var,
    /// or the default %LOCALAPPDATA%\HomeMemory\homememory.scd.
    /// </summary>
    public static string GetDbPath()
    {
        var env = Environment.GetEnvironmentVariable("HOME_MEMORY_DB_PATH");
        if (!string.IsNullOrWhiteSpace(env))
            return env.Trim();

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(appData, "HomeMemory", "homememory.scd");
    }

    /// <summary>
    /// Path to the template.scd shipped next to the executable.
    /// Used by FirstRunSetup to copy an empty schema to AppData on first run.
    /// </summary>
    public static string GetTemplatePath()
    {
        var exeDir = AppContext.BaseDirectory;
        return Path.Combine(exeDir, "template.scd");
    }

    public static FbConnection OpenConnection()
    {
        var cs = new FbConnectionStringBuilder
        {
            Database      = GetDbPath(),
            UserID        = "SYSDBA",
            Password      = "masterkey",
            Charset       = "UTF8",
            ServerType    = FbServerType.Embedded,
            ClientLibrary = GetClientLibPath(),
            Pooling       = false,   // Embedded: truly release file lock after each tool call
        }.ConnectionString;

        var conn = new FbConnection(cs);
        conn.Open();
        return conn;
    }

    /// <summary>
    /// Executes a SQL query. Positional '?' parameters are replaced with @p0, @p1, etc.
    /// All string values are trimmed. Column name lookup is case-insensitive.
    /// If txn is provided, the transaction is set on the command (required by Firebird when a transaction is open).
    /// </summary>
    public static List<Row> ExecuteQuery(FbConnection conn, string sql, params object?[] args)
        => ExecuteQuery(conn, null, sql, args);

    public static List<Row> ExecuteQuery(FbConnection conn, FbTransaction? txn, string sql, params object?[] args)
    {
        int idx = 0;
        var processedSql = Regex.Replace(sql, @"\?", _ => $"@p{idx++}");

        using var cmd = txn != null
            ? new FbCommand(processedSql, conn, txn)
            : new FbCommand(processedSql, conn);
        for (int i = 0; i < args.Length; i++)
            cmd.Parameters.AddWithValue($"@p{i}", args[i] ?? DBNull.Value);

        using var reader = cmd.ExecuteReader();
        var result = new List<Row>();
        int colCount = reader.FieldCount;
        var cols = new string[colCount];
        for (int i = 0; i < colCount; i++)
            cols[i] = reader.GetName(i);

        while (reader.Read())
        {
            var row = new Row(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < colCount; i++)
            {
                var val = reader.IsDBNull(i) ? null : reader.GetValue(i);
                if (val is string s) val = s.TrimEnd();
                row[cols[i]] = val;
            }
            result.Add(row);
        }
        return result;
    }

    /// <summary>
    /// Converts a Firebird OID (Guid, byte[], string) to a normalized uppercase string key
    /// for dictionary lookups.
    /// </summary>
    public static string OidKey(object? val) => val switch
    {
        null        => "",
        Guid g      => g.ToString().ToUpperInvariant(),
        byte[] b when b.Length == 16 => new Guid(b).ToString().ToUpperInvariant(),
        _           => val.ToString()?.ToUpperInvariant() ?? ""
    };

    /// <summary>
    /// Executes a DML statement (INSERT/UPDATE/DELETE) within a transaction.
    /// Positional '?' parameters are replaced with @p0, @p1, etc.
    /// </summary>
    public static int ExecuteNonQuery(FbConnection conn, FbTransaction txn, string sql, params object?[] args)
    {
        int idx = 0;
        var processedSql = Regex.Replace(sql, @"\?", _ => $"@p{idx++}");
        using var cmd = new FbCommand(processedSql, conn, txn);
        for (int i = 0; i < args.Length; i++)
            cmd.Parameters.AddWithValue($"@p{i}", args[i] ?? DBNull.Value);
        return cmd.ExecuteNonQuery();
    }

    /// <summary>Null-safe string trimmer.</summary>
    public static string Str(object? val) => val switch
    {
        null      => "",
        string s  => s.Trim(),
        _         => val.ToString() ?? ""
    };

    /// <summary>Evaluates a Firebird boolean value (BOOLEAN, CHAR 'T'/'F', SMALLINT).</summary>
    public static bool IsTrue(object? val) => val switch
    {
        bool b    => b,
        string s  => s.Trim().ToUpperInvariant() is "T" or "TRUE" or "Y",
        int i     => i != 0,
        short s2  => s2 != 0,
        _         => false
    };
}
