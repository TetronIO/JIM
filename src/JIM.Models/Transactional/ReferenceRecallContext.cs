// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Transactional;

/// <summary>
/// State captured BEFORE Metaverse Object deletion that reference recall needs AFTER deletion.
/// Deletion nulls the reference FKs on referencing objects and disconnects the deleted object's
/// Connected System Objects, so both the referencing linkage and the per-system resolved
/// reference values (for example the target DN a membership removal must name) are only
/// obtainable up front.
/// </summary>
public class ReferenceRecallContext
{
    /// <summary>
    /// Reference attribute values on other Metaverse Objects that point at the deletion
    /// candidates. Excludes references held by objects that are themselves deletion candidates
    /// (nothing to export for an object that is also going away).
    /// </summary>
    public List<MvoReferenceRecallCandidate> Candidates { get; init; } = [];

    /// <summary>
    /// Per deleted Metaverse Object: the resolved reference value for each Connected System it
    /// was joined to, keyed deletedMvoId -> (connectedSystemId -> value). The value is the
    /// secondary external ID (for example the DN for LDAP) when available, else the primary
    /// external ID: the same preference order export execution uses when resolving references.
    /// Used to pre-resolve membership-removal changes at staging time, because the normal
    /// export-time resolution path cannot resolve a Metaverse Object that no longer exists.
    /// </summary>
    public Dictionary<Guid, Dictionary<int, string>> ResolvedReferenceValuesBySystem { get; init; } = [];

    /// <summary>
    /// Per deleted Metaverse Object: the Connected System Object it was joined to in each
    /// Connected System, keyed deletedMvoId -> (connectedSystemId -> csoId). Captured before
    /// deletion disconnects them; the set-based recall fast path (#1003) matches target-side
    /// reference rows by these CSO ids. A CSO id is recorded even when no reference value could
    /// be resolved for it, so unresolvable matches can be counted rather than silently missed.
    /// </summary>
    public Dictionary<Guid, Dictionary<int, Guid>> DeletedCsoIdsBySystem { get; init; } = [];
}
