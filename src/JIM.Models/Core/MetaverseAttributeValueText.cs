// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Globalization;

namespace JIM.Models.Core;

/// <summary>
/// A Metaverse attribute value as culture-independent text (#3059), the form in which an administrator names a value to
/// remove through the REST API or PowerShell: text as it is, numbers in invariant form, dates as ISO 8601 in UTC, a
/// Guid in its hyphenated form, a Boolean as <c>True</c> or <c>False</c>, binary as Base64, and a reference as the id of
/// the object it points to.
/// </summary>
public static class MetaverseAttributeValueText
{
    /// <summary>The value's text; null for a row holding no value (an asserted null).</summary>
    public static string? Of(MetaverseObjectAttributeValue value)
    {
        ArgumentNullException.ThrowIfNull(value);

        if (value.StringValue != null)
            return value.StringValue;
        if (value.IntValue is { } intValue)
            return intValue.ToString(CultureInfo.InvariantCulture);
        if (value.LongValue is { } longValue)
            return longValue.ToString(CultureInfo.InvariantCulture);
        if (value.DecimalValue is { } decimalValue)
            return decimalValue.ToString(CultureInfo.InvariantCulture);
        if (value.DateTimeValue is { } dateTimeValue)
            return DateTime.SpecifyKind(dateTimeValue, DateTimeKind.Utc).ToString("o", CultureInfo.InvariantCulture);
        if (value.GuidValue is { } guidValue)
            return guidValue.ToString("D");
        if (value.BoolValue is { } boolValue)
            return boolValue ? bool.TrueString : bool.FalseString;
        if (value.ByteValue != null)
            return Convert.ToBase64String(value.ByteValue);
        return value.ReferenceValueId?.ToString("D");
    }

    /// <summary>
    /// Whether <paramref name="text"/> names <paramref name="value"/>: exact for text, ignoring case for a Guid or a
    /// Boolean, whose case carries nothing, and by value for a date written in any ISO 8601 form.
    /// </summary>
    public static bool Names(string text, MetaverseObjectAttributeValue value)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(value);

        if (value.StringValue != null)
            return string.Equals(text, value.StringValue, StringComparison.Ordinal);
        if (value.DateTimeValue is { } dateTimeValue)
            return DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed)
                   && parsed == DateTime.SpecifyKind(dateTimeValue, DateTimeKind.Utc);
        return string.Equals(text, Of(value), StringComparison.OrdinalIgnoreCase);
    }
}
