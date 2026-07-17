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

try
{
    if (isHttp)
    {
        if (!IPAddress.TryParse(httpBind, out httpBindAddress))
            throw new InvalidOperationException("HOME_MEMORY_BIND must be an IPv4 or IPv6 address.");

        McpServerSetup.ValidateHttpSecurity(httpBindAddress, httpApiKey, httpAllowedHosts);
    }

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
catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
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
