// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace JIM.Scim.Serialisation;

/// <summary>
/// Reads a JSON string, number, or boolean into a <see cref="string"/>, and always writes a JSON string.
/// <para>
/// SCIM defines several members as strings that real service providers emit as bare JSON numbers or
/// booleans; the error response's <c>status</c> is the common example (RFC 7644 section 3.12 requires
/// a string). Being strict on read would turn a readable provider error into an opaque parse failure,
/// so JIM is liberal in what it accepts and strict in what it emits.
/// </para>
/// </summary>
public class ScimFlexibleStringConverter : JsonConverter<string?>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Null:
                return null;
            case JsonTokenType.String:
                return reader.GetString();
            case JsonTokenType.Number:
                // Preserve the provider's exact lexeme rather than round-tripping through a numeric type,
                // which would risk reformatting (and for decimals, precision loss).
                return ReadRawNumber(ref reader);
            case JsonTokenType.True:
                return bool.TrueString.ToLowerInvariant();
            case JsonTokenType.False:
                return bool.FalseString.ToLowerInvariant();
            default:
                throw new JsonException($"Cannot convert a JSON {reader.TokenType} token to a string.");
        }
    }

    public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options)
    {
        if (value == null)
            writer.WriteNullValue();
        else
            writer.WriteStringValue(value);
    }

    private static string ReadRawNumber(ref Utf8JsonReader reader)
    {
        if (reader.TryGetInt64(out var integer))
            return integer.ToString(CultureInfo.InvariantCulture);

        // Non-integral numbers keep full fidelity via decimal where possible; anything outside decimal's
        // range falls back to the raw UTF-8 lexeme so no information is silently dropped.
        if (reader.TryGetDecimal(out var value))
            return value.ToString(CultureInfo.InvariantCulture);

        return reader.HasValueSequence
            ? Encoding.UTF8.GetString(BuffersExtensions.ToArray(reader.ValueSequence))
            : Encoding.UTF8.GetString(reader.ValueSpan);
    }
}
