// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Globalization;

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

    /// <summary>
    /// Whether two dates are the same date as JIM holds them: both absent, or both present with equal
    /// <see cref="Key"/>s. Use it wherever a held date is compared with a new one, since one read from the database
    /// (Kind Local, to the microsecond) and one made in memory (Kind Utc, perhaps to the 100ns) can name the same date.
    /// </summary>
    public static bool AreEqual(DateTime? a, DateTime? b) =>
        a.HasValue ? b.HasValue && Key(a.Value) == Key(b.Value) : !b.HasValue;

    /// <summary>
    /// The canonical value as invariant text, for merge keys, logs and diagnostic text:
    /// <c>yyyy-MM-ddTHH:mm:ss</c>, then a fraction of up to six digits with trailing zeros trimmed (none for a whole
    /// second), then <c>Z</c>. The same date always gives the same text, whatever its Kind, precision or the host's
    /// culture.
    /// </summary>
    public static string ToRoundTripString(DateTime value) =>
        ToCanonical(value).ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFF'Z'", CultureInfo.InvariantCulture);

    /// <summary>
    /// The value as the portal and other display text show an attribute's date (#2153): <c>dd MMM yyyy HH:mm:ss</c>,
    /// then a fraction of up to six digits with trailing zeros trimmed, only when the value has one, so two dates a
    /// fraction of a second apart never look the same. Culture-invariant. Never converts the time zone: each surface
    /// keeps its own policy, converting before it calls this where it shows local time.
    /// </summary>
    public static string ToDisplayString(DateTime value) => JIM.Models.Utility.AttributeDateDisplay.Format(value);
}
