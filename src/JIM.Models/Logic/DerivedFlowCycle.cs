// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Logic;

/// <summary>
/// A dependency cycle among Metaverse-Derived Attribute Flows of one Metaverse Object Type (#1750): a set of derived
/// flows that read each other's output, directly or through others, so no evaluation order exists. A flow reading
/// its own target attribute is a cycle of one.
/// </summary>
public sealed class DerivedFlowCycle
{
    /// <summary>
    /// The Metaverse Object Type the cycle belongs to.
    /// </summary>
    public int MetaverseObjectTypeId { get; }

    /// <summary>
    /// The cycle as a closed path: each member reads the attribute the next member writes, and the last reads the
    /// attribute the first writes.
    /// </summary>
    public IReadOnlyList<DerivedFlowCycleMember> Members { get; }

    /// <summary>
    /// Further derived flows caught in the same knot of dependencies but not on <see cref="Members"/>' path (only
    /// present when several cycles share attributes). Empty for a simple cycle.
    /// </summary>
    public IReadOnlyList<DerivedFlowCycleMember> AdditionalMembers { get; }

    public DerivedFlowCycle(int metaverseObjectTypeId, IReadOnlyList<DerivedFlowCycleMember> members, IReadOnlyList<DerivedFlowCycleMember> additionalMembers)
    {
        MetaverseObjectTypeId = metaverseObjectTypeId;
        Members = members;
        AdditionalMembers = additionalMembers;
    }

    /// <summary>
    /// Whether <paramref name="mapping"/> is any part of this cycle, on its path or among the additional members.
    /// Compared by reference, since a proposal being validated may hold mappings not yet saved (id 0).
    /// </summary>
    public bool Involves(SyncRuleMapping mapping) =>
        Members.Any(m => ReferenceEquals(m.Mapping, mapping)) || AdditionalMembers.Any(m => ReferenceEquals(m.Mapping, mapping));
}
