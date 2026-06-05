using HomeMemory.MCP.Tools;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace HomeMemory.MCP;

/// <summary>
/// Composable MCP server setup. Used by Program.cs and by tests
/// (in-process Kestrel via WebApplication.UseTestServer).
/// </summary>
public static class McpServerSetup
{
    public const string ServerInstructions =
        """
        Home Memory gives your AI assistant persistent memory for everything in and around your home.

        Home Memory stores places and objects as a nested tree, not a flat list: House → Floor or Area → Room → Wall → Device → Component, nested as deep as needed, e.g. a filter inside a pump. It can create the needed places from what the user says, so users can later ask at any level: "what's in the heating room?", "what's on the north wall?", "what's inside this pump?"

        Track physical elements across all domains: rooms, floors & outdoor areas · building materials (walls, windows, flooring, roof) · electrical (circuits, lighting, outlets, PV/solar, wallbox, home automation) · HVAC · plumbing · IT & communications · security (alarm, fire protection, surveillance) · household (appliances, furniture, electronics, valuables) · vehicles (car, motorcycle, e-bike, bicycle, trailer) · tools · landscaping (garden, pool, irrigation) · health · and more.

        Use the tree for location and containment; use connections only for physical lines such as cables, pipes, ducts, and conduits, with source, target and route.
        Organise with flexible categories and statuses. All data persists in a local database across conversations.

        IMPORTANT – destructive operations: When a deletion fails because child elements or connections exist, treat the error as a stop signal. Report the full blocked scope to the user and ask for explicit confirmation. Never cascade-delete by removing children or connections first without user confirmation.
        """;

    public static IMcpServerBuilder AddHomeMcpServer(IServiceCollection services, string version) =>
        services.AddMcpServer(options =>
            {
                options.ServerInfo = new() { Name = "Home Memory", Version = version };
                options.ServerInstructions = ServerInstructions;
            })
            .WithToolsFromAssembly(typeof(McpServerSetup).Assembly, new JsonSerializerOptions
            {
                TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver(),
                Converters = { new FlexBoolJsonConverterFactory() }
            });

    /// <summary>
    /// Bearer-token middleware. Compares the supplied token to <paramref name="apiKey"/>
    /// in constant time after a length pre-check. Only requests under <paramref name="protectedPath"/>
    /// are gated; other endpoints pass through.
    /// </summary>
    public static void UseBearerAuth(IApplicationBuilder app, string apiKey, string protectedPath = "/mcp")
    {
        var keyBytes = Encoding.UTF8.GetBytes(apiKey);
        var pathString = new PathString(protectedPath);

        app.Use(async (ctx, next) =>
        {
            if (ctx.Request.Path.StartsWithSegments(pathString))
            {
                var authHeader = ctx.Request.Headers.Authorization.ToString();
                const string prefix = "Bearer ";
                if (!authHeader.StartsWith(prefix, StringComparison.Ordinal))
                {
                    ctx.Response.StatusCode = 401;
                    return;
                }
                var providedBytes = Encoding.UTF8.GetBytes(authHeader[prefix.Length..]);
                if (providedBytes.Length != keyBytes.Length ||
                    !CryptographicOperations.FixedTimeEquals(providedBytes, keyBytes))
                {
                    ctx.Response.StatusCode = 401;
                    return;
                }
            }
            await next(ctx);
        });
    }
}
