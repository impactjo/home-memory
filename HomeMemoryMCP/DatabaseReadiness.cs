using HomeMemory.MCP.Db;
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
            catch (DatabaseUnavailableException)
            {
                // Temporary: the file may be free again on the next call. The Firebird error
                // is already logged where the connection failed, so it is not repeated here.
                Console.Error.WriteLine(
                    "[HomeMemory] Database is currently unavailable during tool call.");
                return ErrorResult(DatabaseUnavailableException.Explanation);
            }
            catch (FirebirdSql.Data.FirebirdClient.FbException ex)
            {
                // A Firebird failure that is not the known file-in-use case still keeps the
                // server reachable, because it can be just as temporary.
                Console.Error.WriteLine(
                    $"[HomeMemory] Database is currently unavailable during tool call: {ex.Message}");
                return ErrorResult(DatabaseUnavailableException.Explanation);
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
