// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Globalization;
using JIM.Models.Core;
using JIM.Models.Logic;

namespace JIM.Application.Servers.Scoping;

/// <summary>
/// Renders scoping values for explanations (#348). Culture-invariant, so the portal, the REST API and PowerShell show
/// the same text whatever the server's or reader's locale, and dates are always UTC and say so.
/// </summary>
internal static class ScopingValueFormatter
{
    /// <summary>The value a criterion compares against; <paramref name="boundary"/> is a date criterion's resolved date.</summary>
    internal static string? FormatExpected(AttributeDataType type, SyncRuleScopingCriteria criterion, DateTime? boundary) => type switch
    {
        AttributeDataType.Text => criterion.StringValue,
        AttributeDataType.Number => criterion.IntValue?.ToString(CultureInfo.InvariantCulture),
        AttributeDataType.LongNumber => criterion.LongValue?.ToString(CultureInfo.InvariantCulture),
        AttributeDataType.Decimal => criterion.DecimalValue?.ToString(CultureInfo.InvariantCulture),
        AttributeDataType.DateTime => FormatDate(boundary ?? criterion.DateTimeValue),
        AttributeDataType.Boolean => FormatBoolean(criterion.BoolValue),
        AttributeDataType.Guid => criterion.GuidValue?.ToString("D"),
        _ => null
    };

    internal static string? Format(AttributeDataType type, in ScopingValue value) => type switch
    {
        AttributeDataType.Text => value.StringValue,
        AttributeDataType.Number => value.IntValue?.ToString(CultureInfo.InvariantCulture),
        AttributeDataType.LongNumber => value.LongValue?.ToString(CultureInfo.InvariantCulture),
        AttributeDataType.Decimal => value.DecimalValue?.ToString(CultureInfo.InvariantCulture),
        AttributeDataType.DateTime => FormatDate(value.DateTimeValue),
        AttributeDataType.Boolean => FormatBoolean(value.BoolValue),
        AttributeDataType.Guid => value.GuidValue?.ToString("D"),
        _ => null
    };

    /// <summary>
    /// A date as en-GB reads it, in UTC: "4 Oct 2026" at midnight (most date attributes are days, not instants),
    /// otherwise "4 Oct 2026 10:41 UTC", with seconds only when they are not zero.
    /// </summary>
    internal static string? FormatDate(DateTime? date)
    {
        if (!date.HasValue)
            return null;

        var utc = date.GetValueOrDefault();
        if (utc.Kind == DateTimeKind.Local)
            utc = utc.ToUniversalTime();

        if (utc.TimeOfDay == TimeSpan.Zero)
            return utc.ToString("d MMM yyyy", CultureInfo.InvariantCulture);

        return utc.Second == 0 && utc.Millisecond == 0
            ? utc.ToString("d MMM yyyy HH:mm 'UTC'", CultureInfo.InvariantCulture)
            : utc.ToString("d MMM yyyy HH:mm:ss 'UTC'", CultureInfo.InvariantCulture);
    }

    private static string? FormatBoolean(bool? value) => value switch
    {
        true => "True",
        false => "False",
        null => null
    };
}
