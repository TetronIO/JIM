// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Utilities;

/// <summary>
/// The single source of truth for the canonical form of a date and time attribute value (#2153): JIM holds a date
/// and time to the microsecond, in UTC. Wherever two dates are compared, deduplicated or keyed, compare their
/// <see cref="Key"/>, never <c>==</c> on raw <see cref="DateTime"/> values (which ignores <see cref="DateTime.Kind"/>)
/// and never a <see cref="DateTime.Kind"/>-dependent string.
/// </summary>
public static class DateTimeAttributeValue
{
    /// <summary>
    /// The canonical form of a date: a <see cref="DateTimeKind.Local"/> value is converted to UTC, an
    /// <see cref="DateTimeKind.Unspecified"/> value is taken as UTC, and the result is floored to a whole microsecond
    /// with <see cref="DateTimeKind.Utc"/>. Flooring, never rounding, means a value never moves into the next second,
    /// minute or day. <see cref="DateTime.MinValue"/> and <see cref="DateTime.MaxValue"/> pass through unchanged.
    /// </summary>
    public static DateTime ToCanonical(DateTime value)
    {
        if (value == DateTime.MinValue || value == DateTime.MaxValue)
            return value;

        var utc = value.Kind switch
        {
            DateTimeKind.Local => value.ToUniversalTime(),
            DateTimeKind.Unspecified => DateTime.SpecifyKind(value, DateTimeKind.Utc),
            _ => value
        };

        return new DateTime(utc.Ticks - utc.Ticks % TimeSpan.TicksPerMicrosecond, DateTimeKind.Utc);
    }

    /// <summary>
    /// The canonical ticks of a date, for comparisons, sets, hashes and indexes: two values are the same date
    /// exactly when their keys are equal.
    /// </summary>
    public static long Key(DateTime value) => ToCanonical(value).Ticks;
}
