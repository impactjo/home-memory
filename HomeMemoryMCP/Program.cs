using HomeMemory.MCP;
using HomeMemory.MCP.Db;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

var transport = Environment.GetEnvironmentVariable("HOME_MEMORY_TRANSPORT")?.Trim() ?? "stdio";
var isHttp = string.Equals(transport, "http", StringComparison.OrdinalIgnoreCase);

try
{
    FirstRunSetup.EnsureDatabase();
    DbSchemaVerifier.Verify();
    DbMigrator.MigrateDatabase();
    DbSeeder.SeedIfEmpty();
}
catch (FirebirdSql.Data.FirebirdClient.FbException ex)
{
    Console.Error.WriteLine($"[HomeMemory] Failed to open database ({DbConfig.Current.DisplayName}): {ex.Message}");
    return;
}
catch (InvalidOperationException ex)
{
    Console.Error.WriteLine($"[HomeMemory] {ex.Message}");
    return;
}

var version = typeof(Program).Assembly
    .GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>()
    ?.InformationalVersion ?? "unknown";

Console.Error.WriteLine($"[HomeMemory] Home Memory {version} starting...");
Console.Error.WriteLine($"[HomeMemory] Database: {DbConfig.Current.DisplayName}");

if (isHttp)
{
    var bind = Environment.GetEnvironmentVariable("HOME_MEMORY_BIND")?.Trim() ?? "127.0.0.1";
    var portEnv = Environment.GetEnvironmentVariable("HOME_MEMORY_PORT")?.Trim();
    var port = int.TryParse(portEnv, out var p) ? p : 5100;
    var apiKey = Environment.GetEnvironmentVariable("HOME_MEMORY_API_KEY");

    var builder = WebApplication.CreateBuilder(args);
    builder.Logging.ClearProviders();
    builder.WebHost.UseUrls($"http://{bind}:{port}");
    McpServerSetup.AddHomeMcpServer(builder.Services, version).WithHttpTransport();

    var app = builder.Build();

    if (!string.IsNullOrEmpty(apiKey))
        McpServerSetup.UseBearerAuth(app, apiKey);

    app.MapMcp("/mcp");

    Console.Error.WriteLine($"[HomeMemory] Transport: HTTP");
    Console.Error.WriteLine($"[HomeMemory] Listening: http://{bind}:{port}/mcp");
    Console.Error.WriteLine($"[HomeMemory] Auth: {(!string.IsNullOrEmpty(apiKey) ? "Bearer token active" : "none (set HOME_MEMORY_API_KEY to enable)")}");
    if (bind == "0.0.0.0" && string.IsNullOrEmpty(apiKey))
        Console.Error.WriteLine($"[HomeMemory] Warning: bound to 0.0.0.0 without an API key - consider setting HOME_MEMORY_API_KEY");
    Console.Error.WriteLine($"[HomeMemory] Note: HTTP mode - this process owns the database file");

    await app.RunAsync();
}
else
{
    var builder = Host.CreateApplicationBuilder(args);
    builder.Logging.ClearProviders();
    McpServerSetup.AddHomeMcpServer(builder.Services, version).WithStdioServerTransport();

    await builder.Build().RunAsync();
}
