// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;

namespace JIM.Utilities;

/// <summary>
/// The key an import uses to find the Connected System Object it already holds for an External ID or Secondary
/// External ID value. The repositories that build the lookup and the import that probes it must agree on it exactly,
/// so both build it here.
/// </summary>
/// <remarks>
/// Text is folded through <see cref="IdentifierKey"/>: ignoring case unless the anchor attribute is case-exact (#2051).
/// The key is therefore already in its comparable form, and dictionaries holding it compare with
/// <see cref="Comparer"/> (ordinal); an ignore-case comparer would undo a case-exact attribute's declaration.
/// </remarks>
public static class CsoLookupKey
{
    /// <summary>
    /// How dictionaries and sets keyed by <see cref="For"/> or <see cref="ValueKey"/> compare.
    /// </summary>
    public static StringComparer Comparer => StringComparer.Ordinal;

    /// <summary>
    /// The lookup key for a value of the given anchor attribute in the given Connected System.
    /// </summary>
    public static string For(int connectedSystemId, int attributeId, string valueKey) =>
        $"cso:{connectedSystemId}:{attributeId}:{valueKey}";

    /// <summary>
    /// The comparable form of an anchor value, from whichever value column is populated, or null when none is.
    /// </summary>
    /// <param name="caseExact">Whether the anchor attribute is declared case-exact.</param>
    public static string? ValueKey(string? stringValue, int? intValue, long? longValue, Guid? guidValue, decimal? decimalValue, bool caseExact)
    {
        if (stringValue != null) return IdentifierKey.For(stringValue, caseExact);
        if (intValue.HasValue) return intValue.Value.ToString();
        if (longValue.HasValue) return longValue.Value.ToString();
        // Canonical rather than raw: a decimal carries its scale, so 4200.00m and 4200m would otherwise key differently
        // despite being the same anchor (#1283).
        if (decimalValue.HasValue) return ExternalIdValue.ToCanonicalString(decimalValue.Value);
        // A GUID's text has one canonical form whatever the attribute says.
        if (guidValue.HasValue) return guidValue.Value.ToString().ToLowerInvariant();
        return null;
    }
}
