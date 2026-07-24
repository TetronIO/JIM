// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Globalization;

namespace JIM.Utilities;

/// <summary>
/// The single source of truth for converting Decimal attribute values to and from strings.
/// Decimal values must always be compared numerically (5.0 equals 5.00); wherever a string form is
/// unavoidable (dedupe/merge keys, snapshots, external comparisons, emitted export values), the
/// canonical form produced by <see cref="ToCanonicalString"/> guarantees that numerically equal
/// values produce identical strings. Display paths (UI, ToString overrides, change history display
/// strings) intentionally differ: they use <c>Value.ToString(CultureInfo.InvariantCulture)</c>
/// directly, preserving the stored scale (5.00 displays as "5.00").
/// Never route decimal values through double/float, and never use a culture-sensitive ToString.
/// </summary>
public static class DecimalAttributeValue
{
    /// <summary>
    /// Renders a decimal in its canonical string form: invariant culture, "G29" format (plain
    /// notation, no trailing zeros, never exponent notation). Numerically equal values (5.0 and
    /// 5.00) produce identical canonical strings. Use for all keys, dedupe/merge keys, snapshots,
    /// external comparisons and emitted export values.
    /// </summary>
    public static string ToCanonicalString(decimal value)
    {
        var canonical = value.ToString("G29", CultureInfo.InvariantCulture);
        if (canonical.IndexOf('E') < 0)
            return canonical;

        // "G29" falls back to exponent notation for magnitudes below 1E-5 (e.g. 0.0000001m renders
        // as "1E-07"). Re-parsing the exponent form recovers the minimal-scale decimal, whose default
        // ToString is always plain notation, preserving the "never exponent" guarantee losslessly.
        return decimal.Parse(canonical, NumberStyles.Float, CultureInfo.InvariantCulture)
            .ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Parses a string into a decimal using invariant culture, accepting plain and exponent
    /// notation (e.g. "1.5E3" parses as 1500). Returns false when the input is null, not a number,
    /// or outside the range of <see cref="decimal"/> (overflow); callers must turn a failure into a
    /// per-object import error, never truncate or round.
    /// </summary>
    public static bool TryParse(string? input, out decimal value)
    {
        return decimal.TryParse(input, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }
}
