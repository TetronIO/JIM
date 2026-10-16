// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;

namespace JIM.Models.Logic;

/// <summary>
/// What an Object Matching Rule's values found in the Metaverse for a tested object (#2055): the same lookup an import
/// join runs, through a read-only repository.
/// </summary>
public sealed class ObjectMatchingLookup
{
    /// <summary>What the lookup found.</summary>
    public ObjectMatchingLookupOutcome Outcome { get; init; }

    /// <summary>The Metaverse Object the values identify; null for no match and for an ambiguous match.</summary>
    public Guid? MetaverseObjectId { get; init; }

    /// <summary>The identified Metaverse Object's display name, when it has one.</summary>
    public string? MetaverseObjectName { get; init; }

    /// <summary>The identified Metaverse Object's type name.</summary>
    public string? MetaverseObjectTypeName { get; init; }

    /// <summary>
    /// For an ambiguous match, the Metaverse Objects the values identify, as a synchronisation would record them: at
    /// most <see cref="ActivityRunProfileExecutionItemMatchCandidate.MaxRecorded"/>, each with its full total.
    /// </summary>
    public IReadOnlyList<ActivityRunProfileExecutionItemMatchCandidate> Candidates { get; init; } = [];

    /// <summary>What a synchronisation would do, in one or two sentences.</summary>
    public string Description { get; init; } = string.Empty;
}
