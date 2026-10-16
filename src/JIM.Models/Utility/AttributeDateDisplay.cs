// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Globalization;

namespace JIM.Models.Utility;

/// <summary>
/// The one display form of an attribute's date (#2153), here so that the models' own text forms (a value's
/// <c>ToString</c>, the change history DTOs) can use it. Everything else reaches it through
/// <c>DateTimeAttributeValue.ToDisplayString</c> in JIM.Utilities.
/// </summary>
public static class AttributeDateDisplay
{
    /// <summary>
    /// <c>dd MMM yyyy HH:mm:ss</c>, then a fraction of up to six digits with trailing zeros trimmed, only when the
    /// value has one, so two dates a fraction of a second apart never look the same. Floored to the microsecond JIM
    /// holds. Culture-invariant. Never converts the time zone: each surface keeps its own policy.
    /// </summary>
    public static string Format(DateTime value)
    {
        if (value == DateTime.MinValue || value == DateTime.MaxValue)
            return value.ToString("dd MMM yyyy HH:mm:ss", CultureInfo.InvariantCulture);

        // FFFFFF omits the fraction, and the point before it, when it is zero.
        var floored = new DateTime(value.Ticks - value.Ticks % TimeSpan.TicksPerMicrosecond, value.Kind);
        return floored.ToString("dd MMM yyyy HH:mm:ss.FFFFFF", CultureInfo.InvariantCulture);
    }
}
