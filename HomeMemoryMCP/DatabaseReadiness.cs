using ModelContextProtocol.Protocol;

namespace HomeMemory.MCP;

/// <summary>
/// Keeps the MCP server reachable when the database is temporarily held by another
/// process at startup. Schema verification is repeated before every tool call until it
/// succeeds once; no tool runs before that. A failed attempt is never stored as success,
/// so a later call recovers without restarting the client.
/// </summary>
internal sealed class DatabaseReadiness(Action verifySchema)
{
    private readonly SemaphoreSlim verificationLock = new(1, 1);
    private bool schemaVerified;

    public void VerifyNow()
    {
        if (Volatile.Read(ref schemaVerified))
            return;

        verifySchema();
        Volatile.Write(ref schemaVerified, true);
    }

    public async ValueTask<CallToolResult?> EnsureVerifiedAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref schemaVerified))
            return null;

        await verificationLock.WaitAsync(cancellationToken);
        try
        {
            if (Volatile.Read(ref schemaVerified))
                return null;

            try
            {
                verifySchema();
                Volatile.Write(ref schemaVerified, true);
                return null;
            }
            catch (FirebirdSql.Data.FirebirdClient.FbException ex)
            {
                // Temporary: the file may be free again on the next call.
                Console.Error.WriteLine(
                    $"[HomeMemory] Database is currently unavailable during tool call: {ex.Message}");
                return ErrorResult(
                    "Home Memory cannot currently access the configured database. " +
                    "Another application or another Home Memory process may still be using it. " +
                    "Wait briefly or close the other user of the database, then retry this tool call. " +
                    "Restarting the AI client is not required.");
            }
            catch (InvalidOperationException ex)
            {
                // Permanent: the schema itself is wrong, so report what it is.
                Console.Error.WriteLine($"[HomeMemory] Database schema validation failed: {ex.Message}");
                return ErrorResult(ex.Message);
            }
        }
        finally
        {
            verificationLock.Release();
        }
    }

    private static CallToolResult ErrorResult(string message) => new()
    {
        IsError = true,
        Content = [new TextContentBlock { Text = $"Error: {message}" }],
    };
}
