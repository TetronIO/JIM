// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Core;

/// <summary>
/// The single source of truth for how JIM decides what to call an object: the first present value from
/// an ordered list of candidate name attributes, then an identifier. Both object kinds follow the same
/// policy over their own vocabulary; only the vocabulary and the matching strictness differ.
/// <para>
/// Every display, sort and search path must derive from this type rather than matching an attribute
/// name inline. The rule was previously re-implemented across the model, the repositories, the sync
/// engine and the UI, and the copies disagreed: Connected System Object labels fell through to a raw
/// external id for LDAP groups (which carry <c>cn</c> but no <c>displayName</c>), and two Metaverse
/// queries matched <c>"displayname"</c> against an attribute actually named <c>"Display Name"</c>, so
/// they matched nothing at all.
/// </para>
/// </summary>
public static class ObjectNaming
{
    /// <summary>
    /// Candidate name attributes for a Connected System Object, most preferred first. Matched
    /// case-insensitively: connector schemas belong to the customer's system, not to JIM, and vary in
    /// casing between directory products.
    /// </summary>
    public static IReadOnlyList<string> ConnectedSystemNameAttributes { get; } = ["displayName", "cn", "name"];

    /// <summary>
    /// Candidate name attributes for a Metaverse Object, most preferred first. Matched exactly:
    /// Metaverse attribute names are curated by JIM (see <see cref="BuiltInMetaverseSchema"/>), so
    /// there is no casing ambiguity to absorb. Common Name covers Group objects, which commonly carry
    /// a Common Name and no Display Name.
    /// </summary>
    public static IReadOnlyList<string> MetaverseNameAttributes { get; } =
        [Constants.BuiltInAttributes.DisplayName, Constants.BuiltInAttributes.CommonName];

    /// <summary>
    /// The preference rank of a Connected System attribute name, or -1 when it is not a name candidate.
    /// Lower ranks win.
    /// </summary>
    public static int ConnectedSystemNameRank(string? attributeName)
    {
        return RankOf(ConnectedSystemNameAttributes, attributeName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The preference rank of a Metaverse attribute name, or -1 when it is not a name candidate.
    /// Lower ranks win.
    /// </summary>
    public static int MetaverseNameRank(string? attributeName)
    {
        return RankOf(MetaverseNameAttributes, attributeName, StringComparison.Ordinal);
    }

    /// <summary>
    /// The first candidate that carries an actual value, treating null, empty and whitespace-only as
    /// absent. This is the "which wins" rule shared by the in-memory model properties and by the SQL
    /// paths that project candidate values per tier and resolve them in memory.
    /// </summary>
    public static string? FirstPresent(params string?[] candidateValues)
    {
        return candidateValues.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
    }

    /// <summary>
    /// Whether a Metaverse attribute participates in naming. Change-detection paths use this to decide
    /// whether an attribute change invalidates the denormalised name cache.
    /// </summary>
    public static bool IsMetaverseNameAttribute(string? attributeName)
    {
        return MetaverseNameRank(attributeName) >= 0;
    }

    /// <summary>
    /// Resolves a Metaverse Object's name from a loose collection of attribute values, for paths that
    /// hold a snapshot rather than the object itself (deletion records capture attribute values before
    /// the object is removed). Returns null when none of the candidates are present.
    /// </summary>
    public static string? MetaverseNameFrom(IEnumerable<MetaverseObjectAttributeValue> attributeValues)
    {
        return BestRanked(attributeValues.Select(av => (av.Attribute?.Name, av.StringValue)), MetaverseNameRank);
    }

    /// <summary>
    /// Resolves a name from (attribute name, value) pairs using the given ranking function. Shared by
    /// the model properties and the snapshot paths so every caller applies one ordering and one
    /// definition of "present".
    /// </summary>
    public static string? BestRanked(IEnumerable<(string? AttributeName, string? Value)> attributeValues, Func<string?, int> rank)
    {
        return attributeValues
            .Where(av => !string.IsNullOrWhiteSpace(av.Value))
            .Select(av => (Rank: rank(av.AttributeName), av.Value))
            .Where(candidate => candidate.Rank >= 0)
            .OrderBy(candidate => candidate.Rank)
            .Select(candidate => candidate.Value)
            .FirstOrDefault();
    }

    private static int RankOf(IReadOnlyList<string> candidates, string? attributeName, StringComparison comparison)
    {
        if (string.IsNullOrEmpty(attributeName))
            return -1;

        for (var i = 0; i < candidates.Count; i++)
        {
            if (string.Equals(candidates[i], attributeName, comparison))
                return i;
        }

        return -1;
    }
}
