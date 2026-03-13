namespace HomeMemory.MCP.Tools;

/// <summary>
/// Shared validation helpers for MCP tool input parameters.
/// </summary>
internal static class Validate
{
    /// <summary>
    /// Returns an error message if <paramref name="value"/> exceeds <paramref name="maxLen"/> characters, otherwise null.
    /// </summary>
    public static string? Length(string? value, string fieldName, int maxLen)
    {
        if (value != null && value.Length > maxLen)
            return $"Error: '{fieldName}' exceeds maximum length of {maxLen} characters (got {value.Length}).";
        return null;
    }

    /// <summary>
    /// Normalizes a CLEAR keyword to the canonical uppercase form.
    /// Accepts 'clear', 'Clear', 'CLEAR', etc. Returns "CLEAR" or the original value unchanged.
    /// </summary>
    public static string? NormalizeClear(string? value) =>
        "CLEAR".Equals(value?.Trim(), StringComparison.OrdinalIgnoreCase) ? "CLEAR" : value;
}

/// <summary>
/// String extension helpers used across tool classes.
/// </summary>
internal static class StringExtensions
{
    public static string? NullIfEmpty(this string s) =>
        string.IsNullOrEmpty(s) ? null : s;
}
