using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace HomeMemory.MCP.Tools;

/// <summary>
/// Shared validation helpers for MCP tool input parameters.
/// </summary>
internal static class Validate
{
    // Forbidden characters for element/category/status names (SpecialCharactersNotAllowed)
    // $*[{}|\<>?/";\: and tab
    internal static readonly Regex InvalidChars = new(@"[\$\*\[\{\]\}\|\\<>\?/"";\:\t]");

    // Forbidden characters for connection names (subset – connections allow more characters)
    internal static readonly Regex InvalidCharsConnection = new(@"[\*\|<>?""\t]");

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

    /// <summary>
    /// Normalizes newlines in multiline fields (Description, UserManual, Route) to \r\n
    /// for Smartconstruct compatibility. Unifies standalone \r and \n, then converts to \r\n.
    /// </summary>
    public static string? NormalizeMultiline(string? value) =>
        value is null ? null : value.Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", "\r\n");

    /// <summary>
    /// Normalizes newlines in single-line fields (Name, ShortName, Position, Purpose, Note)
    /// to a space. MCP clients may send \n in text input; Smartconstruct does not support
    /// line breaks in these fields.
    /// </summary>
    public static string? NormalizeSingleline(string? value) =>
        value is null ? null : value.Replace("\r\n", " ").Replace("\r", " ").Replace("\n", " ");
}

/// <summary>
/// String extension helpers used across tool classes.
/// </summary>
internal static class StringExtensions
{
    public static string? NullIfEmpty(this string s) =>
        string.IsNullOrEmpty(s) ? null : s;
}

/// <summary>
/// Accepts boolean parameters from MCP clients as either JSON booleans or common string forms.
/// Keeps tool signatures typed as bool/bool? so the generated schema stays accurate.
/// </summary>
internal sealed class FlexBoolJsonConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert) =>
        typeToConvert == typeof(bool) || typeToConvert == typeof(bool?);

    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options) =>
        typeToConvert == typeof(bool)
            ? new FlexBoolJsonConverter()
            : new NullableFlexBoolJsonConverter();

    private sealed class FlexBoolJsonConverter : JsonConverter<bool>
    {
        public override bool Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            ReadBoolToken(ref reader) ?? throw new JsonException("Cannot convert null to bool.");

        public override void Write(Utf8JsonWriter writer, bool value, JsonSerializerOptions options) =>
            writer.WriteBooleanValue(value);
    }

    private sealed class NullableFlexBoolJsonConverter : JsonConverter<bool?>
    {
        public override bool? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            ReadBoolToken(ref reader);

        public override void Write(Utf8JsonWriter writer, bool? value, JsonSerializerOptions options)
        {
            if (value is null) writer.WriteNullValue();
            else writer.WriteBooleanValue(value.Value);
        }
    }

    private static bool? ReadBoolToken(ref Utf8JsonReader reader) =>
        reader.TokenType switch
        {
            JsonTokenType.True => true,
            JsonTokenType.False => false,
            JsonTokenType.Null => null,
            JsonTokenType.String => reader.GetString()?.Trim().ToLowerInvariant() switch
            {
                "true" or "1" or "yes" => true,
                "false" or "0" or "no" => false,
                null => null,
                var value => throw new JsonException($"Cannot parse '{value}' as boolean.")
            },
            _ => throw new JsonException($"Cannot convert {reader.TokenType} to bool.")
        };
}
