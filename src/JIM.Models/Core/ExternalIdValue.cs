// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Globalization;

namespace JIM.Models.Core;

/// <summary>
/// The canonical string form of an external ID value, for the several places that build the
/// Connected System Object lookup cache key and must agree on it exactly.
/// </summary>
/// <remarks>
/// A Decimal anchor is the reason this exists (#1283). Oracle's <c>NUMBER</c> is discovered as
/// <see cref="AttributeDataType.Decimal"/> because it genuinely exceeds <c>long</c> at high precision
/// and carries a scale. That scale is part of the value's representation but not of its identity:
/// <c>123.40m</c> and <c>123.4m</c> compare equal and hash equal, yet render as different strings.
/// Keying the cache on the raw rendering would mean an imported object failing to match the object
/// JIM already holds for that row, and a duplicate being created on every import, reported as nothing.
///
/// Where a lookup is keyed on the <c>decimal</c> itself rather than on its string form, no
/// normalisation is needed: the runtime already guarantees that equal decimals hash equally,
/// regardless of scale.
/// </remarks>
public static class ExternalIdValue
{
    /// <summary>
    /// Renders a decimal external ID value in the one form every caller must use as a cache key.
    /// Trailing zeros carried by the value's scale are dropped, so two equal values always produce
    /// the same key, and the culture is pinned to invariant, so a key does not depend on the culture
    /// of whichever thread happened to build it (a comma decimal separator would silently produce a
    /// second, non-matching key for the same object).
    /// </summary>
    /// <param name="value">The decimal external ID value.</param>
    /// <returns>The canonical key form of <paramref name="value"/>.</returns>
    public static string ToCanonicalString(decimal value)
    {
        // "G29" is the widest precision a decimal can carry, so nothing is lost; the G format
        // drops the trailing zeros that the value's scale would otherwise render.
        return value.ToString("G29", CultureInfo.InvariantCulture);
    }
}
