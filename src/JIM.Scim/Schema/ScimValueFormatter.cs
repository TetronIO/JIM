// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Globalization;
using System.Text.Json.Nodes;
using JIM.Models.Core;
using JIM.Utilities;

namespace JIM.Scim.Schema;

/// <summary>
/// Converts values as JIM holds them into the JSON forms SCIM defines, shared by everything that writes
/// to a service provider so a date or a decimal is spelled the same way whichever request carries it.
/// </summary>
public static class ScimValueFormatter
{
    /// <summary>The xsd:dateTime form RFC 7643 section 2.3.5 requires, at the precision providers publish.</summary>
    private const string InstantFormat = "yyyy-MM-dd'T'HH:mm:ss'Z'";

    /// <summary>
    /// Converts one value to the JSON node SCIM expects for the given attribute type.
    /// </summary>
    public static JsonNode? ToNode(object? value, AttributeDataType type)
    {
        if (value == null)
            return null;

        return type switch
        {
            AttributeDataType.Boolean => value is bool boolean ? JsonValue.Create(boolean) : JsonValue.Create(ToText(value)),
            AttributeDataType.Number => value is int number ? JsonValue.Create(number) : Numeric(value),
            AttributeDataType.LongNumber => value is long longNumber ? JsonValue.Create(longNumber) : Numeric(value),
            // Routed through DecimalAttributeValue so a decimal never passes through double on the way
            // out, which is the same guarantee the reader gives on the way in.
            AttributeDataType.Decimal => value is decimal decimalValue
                ? JsonNode.Parse(DecimalAttributeValue.ToCanonicalString(decimalValue))
                : Numeric(value),
            AttributeDataType.DateTime => JsonValue.Create(ToText(value)),
            AttributeDataType.Binary => value is byte[] bytes ? JsonValue.Create(Convert.ToBase64String(bytes)) : JsonValue.Create(ToText(value)),
            _ => JsonValue.Create(ToText(value))
        };
    }

    /// <summary>
    /// The value's text form, used where the CLR type is not the one the attribute's SCIM type implies.
    /// Providers accept a number sent as a string far more readily than they accept nothing.
    /// </summary>
    public static string? ToText(object? value)
    {
        return value switch
        {
            null => null,
            string text => text,
            // JIM holds every instant in UTC, and SCIM wants it said so explicitly.
            DateTime dateTime => dateTime.ToUniversalTime().ToString(InstantFormat, CultureInfo.InvariantCulture),
            DateTimeOffset dateTimeOffset => dateTimeOffset.ToUniversalTime().ToString(InstantFormat, CultureInfo.InvariantCulture),
            decimal decimalValue => DecimalAttributeValue.ToCanonicalString(decimalValue),
            byte[] bytes => Convert.ToBase64String(bytes),
            bool boolean => boolean ? "true" : "false",
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString()
        };
    }

    private static JsonNode? Numeric(object? value)
    {
        var text = ToText(value);
        if (text == null)
            return null;

        return JsonNode.Parse(text) ?? JsonValue.Create(text);
    }
}
