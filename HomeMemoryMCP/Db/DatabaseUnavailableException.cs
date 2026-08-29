namespace HomeMemory.MCP.Db;

/// <summary>
/// The configured database exists but could not be opened for this call, which in
/// embedded mode means another process is holding the file. Carries the explanation as
/// its message so the tool catch blocks, which return <c>ex.Message</c> as tool text,
/// report a cause and a way out instead of a raw Firebird I/O error. The original
/// <see cref="FbException"/> stays available as InnerException and belongs in the log.
/// </summary>
public sealed class DatabaseUnavailableException(Exception innerException)
    : Exception(Explanation, innerException)
{
    /// <summary>
    /// Shared by Home Memory and Smartconstruct MCP, so the wording names no product.
    /// Under ServerMode=Super only one process can hold the file at a time, which is the
    /// cause behind every occurrence observed so far.
    /// </summary>
    public const string Explanation =
        "This server cannot currently access the configured database. " +
        "Another application or another server process may still be using it. " +
        "Wait briefly or close the other user of the database, then retry this tool call. " +
        "Restarting the AI client is not required.";
}
