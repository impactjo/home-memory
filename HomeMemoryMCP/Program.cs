using HomeMemory.MCP;
using HomeMemory.MCP.Db;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using System.Net;

var transport = Environment.GetEnvironmentVariable("HOME_MEMORY_TRANSPORT")?.Trim() ?? "stdio";
var isHttp = string.Equals(transport, "http", StringComparison.OrdinalIgnoreCase);
var httpBind = Environment.GetEnvironmentVariable("HOME_MEMORY_BIND")?.Trim() ?? "127.0.0.1";
var httpPortEnv = Environment.GetEnvironmentVariable("HOME_MEMORY_PORT")?.Trim();
var httpPort = int.TryParse(httpPortEnv, out var configuredPort) ? configuredPort : 5100;
var httpApiKey = Environment.GetEnvironmentVariable("HOME_MEMORY_API_KEY");
var httpAllowedHosts = Environment.GetEnvironmentVariable("HOME_MEMORY_ALLOWED_HOSTS");
IPAddress? httpBindAddress = null;
IDisposable? servingLease = null;
bool migrate = args.Any(arg => string.Equals(arg, "--migrate", StringComparison.OrdinalIgnoreCase));
bool confirmed = args.Any(arg => string.Equals(arg, "--yes", StringComparison.OrdinalIgnoreCase));

try
{
    if (migrate)
    {
        if (!confirmed)
            throw new InvalidOperationException(
                "Migration requires explicit confirmation. Stop all MCP servers and run again with --migrate --yes.");
        if (!File.Exists(FirebirdDb.GetDbPath()))
            throw new FileNotFoundException($"Database not found: {FirebirdDb.GetDbPath()}");

        var result = DbMigrator.MigrateDatabase();
        if (result.Changed)
        {
            Console.Error.WriteLine("[HomeMemory] Schema migration completed.");
            Console.Error.WriteLine($"[HomeMemory] Backup: {result.Backup!.Path}");
            Console.Error.WriteLine($"[HomeMemory] Backup SHA-256: {result.Backup.Sha256}");
        }
        else
        {
            Console.Error.WriteLine("[HomeMemory] Schema is already registered. No changes were made.");
        }
        return;
    }

    if (isHttp)
    {
        if (!IPAddress.TryParse(httpBind, out httpBindAddress))
            throw new InvalidOperationException("HOME_MEMORY_BIND must be an IPv4 or IPv6 address.");

        McpServerSetup.ValidateHttpSecurity(httpBindAddress, httpApiKey, httpAllowedHosts);
    }

    bool databaseCreated = FirstRunSetup.EnsureDatabase();
    if (databaseCreated)
        DbMigrator.InitializeNewDatabase();

    servingLease = DatabaseLifecycleLease.AcquireServing();
    DbSchemaVerifier.Verify();
    if (databaseCreated)
        DbSeeder.SeedIfEmpty();
}
catch (FirebirdSql.Data.FirebirdClient.FbException ex)
{
    servingLease?.Dispose();
    Console.Error.WriteLine($"[HomeMemory] Failed to open database ({DbConfig.Current.DisplayName}): {ex.Message}");
    Environment.ExitCode = 1;
    return;
}
catch (InvalidOperationException ex)
{
    servingLease?.Dispose();
    Console.Error.WriteLine($"[HomeMemory] {ex.Message}");
    Environment.ExitCode = 1;
    return;
}
catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
{
    servingLease?.Dispose();
    Console.Error.WriteLine($"[HomeMemory] {ex.Message}");
    Environment.ExitCode = 1;
    return;
}

var version = typeof(Program).Assembly
    .GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>()
    ?.InformationalVersion ?? "unknown";

Console.Error.WriteLine($"[HomeMemory] Home Memory {version} starting...");
Console.Error.WriteLine($"[HomeMemory] Database: {DbConfig.Current.DisplayName}");

try
{
    if (isHttp)
    {
        var diagnostics = string.Equals(
            Environment.GetEnvironmentVariable("HOME_MEMORY_HTTP_DIAGNOSTICS")?.Trim(),
            "1", StringComparison.Ordinal);

        var builder = WebApplication.CreateBuilder(args);
        builder.Logging.ClearProviders();
        if (diagnostics)
        {
            builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
            builder.Logging.SetMinimumLevel(LogLevel.Trace);
        }
        builder.WebHost.ConfigureKestrel(options => options.Listen(httpBindAddress!, httpPort));
        McpServerSetup.AddHomeMcpServer(builder.Services, version).WithHttpTransport();

        var app = builder.Build();

        if (diagnostics)
            app.UseDeveloperExceptionPage();

        McpServerSetup.UseHttpSecurity(app, httpBindAddress!, httpApiKey, httpAllowedHosts);

        app.MapMcp("/mcp");

        var displayHost = httpBindAddress!.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
            ? $"[{httpBindAddress}]"
            : httpBindAddress.ToString();

        Console.Error.WriteLine($"[HomeMemory] Transport: HTTP");
        Console.Error.WriteLine($"[HomeMemory] Listening: http://{displayHost}:{httpPort}/mcp");
        Console.Error.WriteLine($"[HomeMemory] Auth: {(!string.IsNullOrWhiteSpace(httpApiKey) ? "Bearer token active" : "none (loopback only)")}");
        Console.Error.WriteLine($"[HomeMemory] Request security: Host and Origin checks active");
        Console.Error.WriteLine($"[HomeMemory] HTTP mode: this process owns the database file");

        await app.RunAsync();
    }
    else
    {
        var builder = Host.CreateApplicationBuilder(args);
        builder.Logging.ClearProviders();
        McpServerSetup.AddHomeMcpServer(builder.Services, version).WithStdioServerTransport();

        await builder.Build().RunAsync();
    }
}
finally
{
    servingLease?.Dispose();
}
