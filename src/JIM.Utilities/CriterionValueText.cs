// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Globalization;
using JIM.Models.Core;
using JIM.Models.Search;

namespace JIM.Utilities;

/// <summary>
/// Reads a criterion value held as text into the typed slot for an attribute's data type (#2053). Example Data
/// dependencies store their value as text whatever the dependent attribute's type; this is how they compare as that
/// type rather than as text. The forms are culture-invariant: numbers with a full stop, dates in ISO 8601 (treated as
/// UTC unless an offset says otherwise), Booleans as true or false.
/// </summary>
public static class CriterionValueText
{
    /// <summary>
    /// Sets the slot on <paramref name="into"/> for <paramref name="type"/> from <paramref name="text"/>. Returns why the
    /// text is not a value of that type, or null when it is. Null or empty text sets nothing.
    /// </summary>
    public static string? Read(string? text, AttributeDataType type, ICriterionValues into)
    {
        ArgumentNullException.ThrowIfNull(into);
        if (string.IsNullOrEmpty(text))
            return null;

        switch (type)
        {
            case AttributeDataType.Text:
                into.StringValue = text;
                return null;
            case AttributeDataType.Number when int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number):
                into.IntValue = number;
                return null;
            case AttributeDataType.LongNumber when long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var longNumber):
                into.LongValue = longNumber;
                return null;
            case AttributeDataType.Decimal when decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var decimalNumber):
                into.DecimalValue = decimalNumber;
                return null;
            case AttributeDataType.DateTime when DateTime.TryParseExact(text, IsoDateFormats, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var date):
                into.DateTimeValue = date;
                return null;
            case AttributeDataType.Boolean when bool.TryParse(text, out var boolean):
                into.BoolValue = boolean;
                return null;
            case AttributeDataType.Guid when Guid.TryParse(text, out var guid):
                into.GuidValue = guid;
                return null;
            default:
                return $"'{text}' is not a {Describe(type)}.";
        }
    }

    private static readonly string[] IsoDateFormats =
    {
        "yyyy-MM-dd",
        "yyyy-MM-ddTHH:mm",
        "yyyy-MM-ddTHH:mm:ss",
        "yyyy-MM-ddTHH:mm:ss.FFFFFFF",
        "yyyy-MM-ddTHH:mmK",
        "yyyy-MM-ddTHH:mm:ssK",
        "yyyy-MM-ddTHH:mm:ss.FFFFFFFK"
    };

    private static string Describe(AttributeDataType type) => type switch
    {
        AttributeDataType.Number => "whole number",
        AttributeDataType.LongNumber => "whole number",
        AttributeDataType.Decimal => "decimal number (use a full stop, such as 1234.5)",
        AttributeDataType.DateTime => "date in ISO 8601 form (such as 2026-07-01 or 2026-07-01T09:30:00Z)",
        AttributeDataType.Boolean => "Boolean (true or false)",
        AttributeDataType.Guid => "GUID",
        _ => $"value a {type} attribute can hold"
    };
}
