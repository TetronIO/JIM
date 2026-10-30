// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Globalization;
using JIM.Models.Core;

namespace JIM.Models.History;

/// <summary>
/// Turns the value an administrator typed into an attribute condition into a typed <see cref="AttributeChangeValueMatch"/>
/// for the attribute's data type, so a search compares numbers as numbers and dates as dates rather than as text.
/// </summary>
public static class AttributeChangeConditionParser
{
    private static readonly string[] DateOnlyFormats = ["yyyy-MM-dd", "dd/MM/yyyy"];

    /// <summary>
    /// Parses <paramref name="value"/> for an attribute of <paramref name="attributeType"/>. A blank value parses to a
    /// null match, meaning any value. Returns false, with a reason naming the value, when it is not of the attribute's
    /// type.
    /// </summary>
    public static bool TryParse(AttributeDataType attributeType, string? value, out AttributeChangeValueMatch? match, out string? error)
    {
        match = null;
        error = null;

        if (string.IsNullOrWhiteSpace(value))
            return true;

        var text = value.Trim();
        switch (attributeType)
        {
            case AttributeDataType.Text:
            case AttributeDataType.Reference:
                match = new AttributeChangeValueMatch { Text = text, TextHasWildcard = text.Contains('*') };
                return true;

            case AttributeDataType.Number when int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var intValue):
                match = new AttributeChangeValueMatch { IntValue = intValue };
                return true;

            case AttributeDataType.LongNumber when long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var longValue):
                match = new AttributeChangeValueMatch { LongValue = longValue };
                return true;

            case AttributeDataType.Decimal when decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var decimalValue):
                match = new AttributeChangeValueMatch { DecimalValue = decimalValue };
                return true;

            case AttributeDataType.Boolean when bool.TryParse(text, out var boolValue):
                match = new AttributeChangeValueMatch { BoolValue = boolValue };
                return true;

            case AttributeDataType.Guid when Guid.TryParse(text, out var guidValue):
                match = new AttributeChangeValueMatch { GuidValue = guidValue };
                return true;

            case AttributeDataType.DateTime when DateTime.TryParseExact(text, DateOnlyFormats, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var date):
                match = new AttributeChangeValueMatch { DateTimeFrom = date, DateTimeTo = date.AddDays(1) };
                return true;

            case AttributeDataType.DateTime when DateTime.TryParse(text, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dateTime):
                var second = new DateTime(dateTime.Ticks - dateTime.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc);
                match = new AttributeChangeValueMatch { DateTimeFrom = second, DateTimeTo = second.AddSeconds(1) };
                return true;

            case AttributeDataType.Binary:
                error = "Binary values are not recorded in change history, so a Binary attribute can only be matched on any change, without a value.";
                return false;

            default:
                error = $"'{text}' is not a valid {Describe(attributeType)} value.";
                return false;
        }
    }

    private static string Describe(AttributeDataType attributeType) => attributeType switch
    {
        AttributeDataType.Number => "whole number",
        AttributeDataType.LongNumber => "long whole number",
        AttributeDataType.Decimal => "decimal number",
        AttributeDataType.Boolean => "true or false",
        AttributeDataType.Guid => "GUID",
        AttributeDataType.DateTime => "date (yyyy-MM-dd) or date and time",
        _ => attributeType.ToString()
    };
}
