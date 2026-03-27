using System.Text.Json;
using System.Text.Json.Serialization;

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
