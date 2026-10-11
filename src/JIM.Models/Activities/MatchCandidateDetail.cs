// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Activities;

/// <summary>
/// An ambiguous match's candidate as an Activity item's detail shows it (#2052): what was recorded when the ambiguity
/// arose, whether the candidate still exists, and, for a Metaverse Object, the Connected System Objects joined to it
/// now, which is usually what tells two colliding identities apart.
/// </summary>
public class MatchCandidateDetail
{
    /// <summary>The candidate as recorded with the Activity item.</summary>
    public required ActivityRunProfileExecutionItemMatchCandidate Candidate { get; init; }

    /// <summary>
    /// Whether the candidate still exists. A deleted one is shown by its recorded name and not linked.
    /// </summary>
    public bool Exists { get; init; }

    /// <summary>
    /// For a Metaverse Object candidate that still exists, its type's plural name, which a link to it needs.
    /// </summary>
    public string? MetaverseObjectTypePluralName { get; init; }

    /// <summary>
    /// For a Metaverse Object candidate that still exists, the Connected System Objects joined to it now. Empty for a
    /// Connected System Object candidate.
    /// </summary>
    public IReadOnlyList<MatchCandidateJoinedObject> JoinedObjects { get; init; } = [];
}
