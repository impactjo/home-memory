using HomeMemory.MCP.Db;
using HomeMemory.MCP.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using System.Text.Json;

FirstRunSetup.EnsureDatabase();
DbMigrator.MigrateDatabase();
DbSeeder.SeedIfEmpty();

var version = typeof(Program).Assembly
    .GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>()
    ?.InformationalVersion ?? "unknown";

Console.Error.WriteLine($"[HomeMemory] Home Memory {version} starting...");
Console.Error.WriteLine($"[HomeMemory] Database: {FirebirdDb.GetDbPath()}");

var builder = Host.CreateApplicationBuilder(args);
builder.Logging.ClearProviders();
builder.Services.AddMcpServer(options =>
    {
        options.ServerInfo = new() { Name = "Home Memory", Version = version };
        options.ServerInstructions =
            """
            Home Memory gives your AI assistant persistent memory for everything in and around your home.

            Track physical elements across all domains: rooms, floors & outdoor areas · building materials (walls, windows, flooring, roof) · electrical (circuits, lighting, outlets, PV/solar, wallbox, home automation) · HVAC · plumbing · IT & communications · security (alarm, fire protection, surveillance) · household (appliances, furniture, electronics, valuables) · vehicles (car, motorcycle, e-bike, bicycle, trailer) · tools · landscaping (garden, pool, irrigation) · health · and more.

            Document physical connections between elements (cables, pipes, ducts, conduits).
            Organise with flexible categories and statuses. All data persists in a local database across conversations.

            IMPORTANT – destructive operations: When a deletion fails because child elements or connections exist, treat the error as a stop signal. Report the full blocked scope to the user and ask for explicit confirmation. Never cascade-delete by removing children or connections first without user confirmation.
            """;
    })
    .WithStdioServerTransport()
    .WithToolsFromAssembly(typeof(Program).Assembly, new JsonSerializerOptions
    {
        TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver(),
        Converters = { new FlexBoolJsonConverterFactory() }
    });

await builder.Build().RunAsync();
