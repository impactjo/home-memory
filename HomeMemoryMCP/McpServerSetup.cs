using HomeMemory.MCP.Tools;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using System.Net;
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

        Home Memory stores places and items as elements in a nested tree, not a flat list. Places such as buildings, floors, rooms, garages, outdoor areas, or walls can hold items such as devices, furniture, tools, materials, and parts, nested as deeply as needed, e.g. a filter inside a pump. It can create the needed places from what the user says, so users can later ask at any level: "what's in the heating room?", "what's on the north wall?", "what's inside this pump?"

        Use connections only for physical lines between elements, such as cables, pipes, ducts, and conduits, with source, destination, and route. Don't model such a line as an ordinary item when the physical run itself is what matters.

        Track physical elements across all home domains, organized with categories: building materials (walls, windows, flooring, roof) · electrical (circuits, lighting, outlets, PV/solar, wallbox, home automation) · HVAC · plumbing · IT & communications · security (alarm, fire protection, surveillance) · household (appliances, furniture, electronics, valuables) · vehicles (car, motorcycle, e-bike, bicycle, trailer) · tools · landscaping (garden, pool, irrigation) · health · and more. Categories are fully customizable: use the built-in ones, adapt them, or create your own. Statuses are customizable too and track lifecycle state such as existing, planned, or removed. All data persists in a local database across conversations.

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
    /// Validates the authentication boundary for HTTP mode. Loopback may run without
    /// a key; every non-loopback listener requires one.
    /// </summary>
    public static void ValidateHttpSecurity(
        IPAddress bindAddress,
        string? apiKey,
        string? allowedHosts = null)
    {
        ArgumentNullException.ThrowIfNull(bindAddress);

        if (!IPAddress.IsLoopback(bindAddress) && string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException(
                "HOME_MEMORY_API_KEY is required when HOME_MEMORY_BIND is not a loopback address.");
        }

        _ = ParseAllowedHosts(allowedHosts);
    }

    /// <summary>
    /// Applies host, origin, and optional Bearer-token protection to the MCP endpoint.
    /// Native MCP clients normally omit Origin. Browser-originated requests are rejected
    /// because Home Memory does not expose a browser client.
    /// </summary>
    public static void UseHttpSecurity(
        IApplicationBuilder app,
        IPAddress bindAddress,
        string? apiKey,
        string? allowedHosts = null,
        string protectedPath = "/mcp")
    {
        ValidateHttpSecurity(bindAddress, apiKey, allowedHosts);
        var pathString = new PathString(protectedPath);
        var configuredHosts = ParseAllowedHosts(allowedHosts);

        app.Use(async (ctx, next) =>
        {
            if (ctx.Request.Path.StartsWithSegments(pathString))
            {
                if (!IsAllowedHost(ctx.Request.Host, bindAddress, configuredHosts))
                {
                    ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                    return;
                }

                if (ctx.Request.Headers.ContainsKey("Origin"))
                {
                    ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return;
                }
            }

            await next(ctx);
        });

        if (!string.IsNullOrWhiteSpace(apiKey))
            UseBearerAuth(app, apiKey, protectedPath);
    }

    private static HashSet<string> ParseAllowedHosts(string? allowedHosts)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(allowedHosts))
            return result;

        foreach (var entry in allowedHosts.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var host = entry.TrimEnd('.');
            if (host.Contains('*'))
                throw new InvalidOperationException("HOME_MEMORY_ALLOWED_HOSTS does not accept wildcard entries.");
            result.Add(host);
        }

        return result;
    }

    private static bool IsAllowedHost(
        HostString requestHost,
        IPAddress bindAddress,
        HashSet<string> configuredHosts)
    {
        var host = requestHost.Host.TrimEnd('.');
        if (string.IsNullOrWhiteSpace(host))
            return false;

        if (host.Length >= 2 && host[0] == '[' && host[^1] == ']')
            host = host[1..^1];

        if (configuredHosts.Contains(host))
            return true;

        var isWildcard = bindAddress.Equals(IPAddress.Any) || bindAddress.Equals(IPAddress.IPv6Any);

        if (IPAddress.TryParse(host, out var hostAddress))
        {
            if (isWildcard)
                return true;

            return IPAddress.IsLoopback(bindAddress)
                ? IPAddress.IsLoopback(hostAddress)
                : bindAddress.Equals(hostAddress);
        }

        return string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
            && (IPAddress.IsLoopback(bindAddress) || isWildcard);
    }

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
