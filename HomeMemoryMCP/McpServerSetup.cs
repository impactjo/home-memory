using HomeMemory.MCP.Tools;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Protocol;
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
        Home Memory is persistent memory for the physical contents of a home. Elements form a nested tree of places and items, nested as deeply as needed, e.g. a filter inside a pump; users can query any level. It spans every domain, from wiring and heating to furniture, tools, vehicles, and the garden. Use lines only for physical runs such as cables, pipes, ducts, and conduits; other physical things are elements. Categories and statuses are user-editable. On blocked deletion, report dependencies and ask first.
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
            })
            .WithRequestFilters(requestFilters =>
            {
                requestFilters.AddCallToolFilter(next => async (context, cancellationToken) =>
                {
                    // Argument names and required fields are checked against the tool's
                    // input schema before the SDK binds them. A misspelled or missing
                    // argument fails inside the binder, where the tool's own message never
                    // runs and the caller only sees "An error occurred invoking '<tool>'.".
                    var argumentError = ValidateArguments(context.Services, context.Params);
                    if (argumentError != null) return argumentError;

                    var result = await next(context, cancellationToken);

                    // Tool methods use a stable "Error:" prefix so direct C# callers
                    // keep receiving plain text. At the MCP boundary, expose the same
                    // text as a structured tool error for clients and models.
                    if (result.IsError is not true && result.Content
                            .OfType<TextContentBlock>()
                            .Any(content => content.Text.StartsWith("Error:", StringComparison.Ordinal)))
                    {
                        result.IsError = true;
                    }

                    return result;
                });
            });

    /// <summary>
    /// Checks the supplied argument names against the tool's input schema. Only names and
    /// required fields are checked, never types: FlexBool suppresses the schema type of
    /// boolean parameters, so a schema-faithful type check would reject valid calls.
    /// </summary>
    private static CallToolResult? ValidateArguments(
        IServiceProvider? services,
        CallToolRequestParams? request)
    {
        if (request?.Name is not { Length: > 0 } toolName) return null;

        var tool = services?.GetServices<McpServerTool>()
            .FirstOrDefault(candidate => candidate.ProtocolTool.Name == toolName);
        // An unrecognized tool is the SDK's own error to report.
        if (tool == null) return null;

        var schema = tool.ProtocolTool.InputSchema;
        if (schema.ValueKind != JsonValueKind.Object) return null;

        var accepted = schema.TryGetProperty("properties", out var properties)
            && properties.ValueKind == JsonValueKind.Object
                ? properties.EnumerateObject().Select(property => property.Name).ToList()
                : [];
        var supplied = request.Arguments;

        var unknown = supplied == null
            ? []
            : supplied.Keys.Where(name => !accepted.Contains(name, StringComparer.Ordinal)).ToList();
        if (unknown.Count > 0)
            return ArgumentError(UnknownArgumentMessage(toolName, unknown, accepted));

        var required = schema.TryGetProperty("required", out var requiredNames)
            && requiredNames.ValueKind == JsonValueKind.Array
                ? requiredNames.EnumerateArray()
                    .Select(item => item.GetString() ?? "")
                    .Where(name => name.Length > 0)
                    .ToList()
                : [];
        var missing = required.Where(name => !HasValue(supplied, name)).ToList();
        if (missing.Count > 0)
            return ArgumentError($"Error: {Quote(missing)} {(missing.Count == 1 ? "is" : "are")} required.");

        return null;
    }

    /// <summary>
    /// An argument passed as JSON null is treated as absent, so a required parameter
    /// reports the same message whether it was omitted or explicitly emptied.
    /// </summary>
    private static bool HasValue(IDictionary<string, JsonElement>? arguments, string name) =>
        arguments != null
        && arguments.TryGetValue(name, out var value)
        && value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined);

    private static string UnknownArgumentMessage(
        string toolName,
        IReadOnlyList<string> unknown,
        IReadOnlyList<string> accepted)
    {
        if (accepted.Count == 0)
            return $"Error: '{toolName}' takes no arguments.";

        var message = unknown.Count == 1
            ? $"Error: '{toolName}' has no argument {Quote(unknown)}."
            : $"Error: '{toolName}' has no arguments {Quote(unknown)}.";

        var correction = unknown.Count == 1
            ? Correction(unknown[0], accepted)
            : null;
        if (correction != null)
            message += $" Use '{correction}' instead.";

        return message + $" Accepted arguments: {string.Join(", ", accepted)}.";
    }

    /// <summary>
    /// Names the intended argument only when the supplied name itself identifies one: the
    /// same name in another casing, or the same name with or without a role prefix, as in
    /// 'document_oid' for 'oid'. Pairing by count instead, such as the single required
    /// argument a call left out, proves no relation between the names and recommends a
    /// wrong one confidently. Everything else is reported with the accepted names only.
    /// </summary>
    private static string? Correction(string unknown, IReadOnlyList<string> accepted)
    {
        var sameName = accepted
            .Where(name => name.Equals(unknown, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (sameName.Count == 1) return sameName[0];

        var affix = accepted
            .Where(name => name.EndsWith("_" + unknown, StringComparison.OrdinalIgnoreCase)
                || unknown.EndsWith("_" + name, StringComparison.OrdinalIgnoreCase))
            .ToList();
        return affix.Count == 1 ? affix[0] : null;
    }

    private static string Quote(IReadOnlyList<string> names) =>
        names.Count switch
        {
            1 => $"'{names[0]}'",
            2 => $"'{names[0]}' and '{names[1]}'",
            _ => string.Join(", ", names.Take(names.Count - 1).Select(name => $"'{name}'"))
                 + $", and '{names[^1]}'",
        };

    private static CallToolResult ArgumentError(string message) => new()
    {
        IsError = true,
        Content = [new TextContentBlock { Text = message }],
    };

    /// <summary>
    /// Adds the Streamable HTTP transport. Clients that open a session through the
    /// initialize handshake keep that session; clients on the 2026-07-28 revision or
    /// later, which no longer has sessions, are served statelessly on the same endpoint.
    /// Configured here so Program.cs and the tests cannot drift apart.
    /// </summary>
    public static IMcpServerBuilder WithHomeHttpTransport(this IMcpServerBuilder builder) =>
        builder.WithHttpTransport(options =>
            options.SessionMode = HttpServerSessionMode.StatefulForInitializeClients);

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
