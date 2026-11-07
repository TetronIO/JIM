// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Staging;

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
    /// #2705: whether the database can be asked for every stored value a key names, rather than the whole lookup being
    /// loaded for it. Always for a case-exact attribute, whose keys are the values themselves. Ignoring case, only for a
    /// key in ASCII: the database folds with <c>lower()</c>, which agrees with <see cref="IdentifierKey"/> on every value
    /// that folds to an ASCII key (given <see cref="DependsOnDatabaseFolding"/>, #2669), and on nothing else for certain
    /// (PostgreSQL lowers U+A7CB where .NET does not, and a C collation folds ASCII only).
    /// </summary>
    public static bool CanBeLookedUpInDatabase(bool caseExact, string valueKey) =>
        caseExact || System.Text.Ascii.IsValid(valueKey);

    /// <summary>
    /// #2705: whether a key ignoring case is found in the database only where <c>lower()</c> folds as JIM does
    /// (<c>ISyncRepository.DoesDatabaseFoldCaseLikeIdentifierKeyAsync</c>). Every database folds ASCII, so an ASCII key
    /// depends on it only where it holds a letter that a character outside ASCII also folds to (today <c>i</c>, from the
    /// dotted capital I, and <c>k</c>, from the Kelvin sign): a stored value spelt with that character keys the same, and
    /// a database that does not fold it would not find it. A GUID or a number never does, so a Delta Import anchored on
    /// one looks up page by page on any database.
    /// </summary>
    public static bool DependsOnDatabaseFolding(bool caseExact, string valueKey) =>
        !caseExact && valueKey.AsSpan().ContainsAny(LettersFoldedFromOutsideAscii.Value);

    private static readonly Lazy<System.Buffers.SearchValues<char>> LettersFoldedFromOutsideAscii = new(() =>
        System.Buffers.SearchValues.Create(IdentifierKey.For(string.Concat(IdentifierKey.NonAsciiCharactersFoldedToAscii), caseExact: false)));

    /// <summary>
    /// #2705: refuses a set of probes any one of which the database cannot be asked about
    /// (<see cref="CanBeLookedUpInDatabase"/>), so a lookup by External ID never answers "no such object" for a key it
    /// could not search for properly.
    /// </summary>
    /// <exception cref="ArgumentException">A probe's attribute ignores case and its key has characters outside ASCII.</exception>
    public static void EnsureCanBeLookedUpInDatabase(IEnumerable<CsoExternalIdProbe> probes)
    {
        var unanswerable = probes
            .Where(probe => !CanBeLookedUpInDatabase(probe.CaseExact, probe.ValueKey))
            .Select(probe => (CsoExternalIdProbe?)probe)
            .FirstOrDefault();
        if (unanswerable is { } probe)
            throw new ArgumentException(
                $"An External ID of attribute {probe.AttributeId}, which ignores case, has characters outside ASCII, so only the whole External ID lookup can answer for it.",
                nameof(probes));
    }

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
