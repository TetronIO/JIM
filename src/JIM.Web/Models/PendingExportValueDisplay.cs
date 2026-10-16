// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Globalization;
using JIM.Models.Transactional;
using JIM.Utilities;

namespace JIM.Web.Models;

/// <summary>
/// The text a page shows for a value a Pending Export will write, shared by every page listing one (#2153). Three pages
/// each carried their own copy, and every copy fell through to "[null]" for a true or false value, a Long Number, a
/// Decimal or a GUID.
/// </summary>
public static class PendingExportValueDisplay
{
    /// <summary>
    /// The value as text; null where the change holds no value (one that clears the attribute). A reference the page
    /// can name better is the caller's to render; this gives its raw form.
    /// </summary>
    /// <param name="change">The value change.</param>
    /// <param name="localTime">Whether a date is shown in the viewer's time zone rather than UTC, as the calling surface
    /// shows its dates.</param>
    public static string? Text(PendingExportAttributeValueChange change, bool localTime = false)
    {
        if (!string.IsNullOrEmpty(change.UnresolvedReferenceValue))
            return $"[Unresolved Reference: {change.UnresolvedReferenceValue}]";
        if (change.StringValue != null)
            return change.StringValue;
        if (change.DateTimeValue.HasValue)
        {
            var date = change.DateTimeValue.Value;
            return DateTimeAttributeValue.ToDisplayString(localTime ? date.ToLocalTime() : date);
        }

        if (change.IntValue.HasValue)
            return change.IntValue.Value.ToString(CultureInfo.InvariantCulture);
        if (change.LongValue.HasValue)
            return change.LongValue.Value.ToString(CultureInfo.InvariantCulture);
        if (change.DecimalValue.HasValue)
            return DecimalAttributeValue.ToCanonicalString(change.DecimalValue.Value);
        if (change.BoolValue.HasValue)
            return change.BoolValue.Value ? "True" : "False";
        if (change.GuidValue.HasValue)
            return change.GuidValue.Value.ToString();
        if (change.ByteValue != null)
            return $"[Binary data: {change.ByteValue.Length} bytes]";

        return null;
    }
}
