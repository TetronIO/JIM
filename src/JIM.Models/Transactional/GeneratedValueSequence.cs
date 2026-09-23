// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Staging;

namespace JIM.Models.Transactional;

/// <summary>
/// The forward-only counter behind a <see cref="GeneratedValueTokenKind.Sequence"/> token, scoped to one target
/// attribute (Unique Value Generation, #242, plan decision 3). Exactly one of <see cref="MetaverseAttributeId"/>
/// and <see cref="ConnectedSystemObjectTypeAttributeId"/> is populated, matching whether the generated mapping
/// that owns the attribute is an import or export flow.
/// <para>
/// The counter is never deleted when a mapping or its Synchronisation Rule is deleted, and is never reset by
/// ordinary configuration changes: it only ever moves upward (or, via "Start again", back to a flow's configured
/// start value; plan decision 3, FR 33). It is deleted only when the attribute itself is deleted (cascade), so a
/// counter genuinely outlives any one flow that draws from it.
/// </para>
/// </summary>
public class GeneratedValueSequence
{
    public int Id { get; set; }

    /// <summary>
    /// The Metaverse attribute this counter serves, for an import (generated-into-Metaverse) flow. Exactly one of
    /// this and <see cref="ConnectedSystemObjectTypeAttributeId"/> is set.
    /// </summary>
    public int? MetaverseAttributeId { get; set; }

    public MetaverseAttribute? MetaverseAttribute { get; set; }

    /// <summary>
    /// The Connected System Object Type attribute this counter serves, for an export (generated-into-connector)
    /// flow. Exactly one of this and <see cref="MetaverseAttributeId"/> is set.
    /// </summary>
    public int? ConnectedSystemObjectTypeAttributeId { get; set; }

    public ConnectedSystemObjectTypeAttribute? ConnectedSystemObjectTypeAttribute { get; set; }

    /// <summary>
    /// The next number this counter will issue. Only ever increases (or is deliberately moved back to a flow's
    /// start value by "Start again"); an issued number is reserved with an atomic <c>UPDATE ... RETURNING</c> that
    /// advances this value, so it is never re-issued.
    /// </summary>
    public long NextValue { get; set; }

    /// <summary>
    /// How many numbers this counter has issued in total. Display only; does not gate anything.
    /// </summary>
    public long AssignedCount { get; set; }

    /// <summary>
    /// When a generated mapping's configured start value last raised this counter above its current position
    /// (UTC). Null if the counter has never been moved this way.
    /// </summary>
    public DateTime? LastMovedAt { get; set; }

    /// <summary>
    /// Which Synchronisation Rule mapping's start value last raised this counter. Set null (never cascade-deleted
    /// with the mapping) so the counter's own history survives the mapping being removed; the counter itself is
    /// unaffected either way.
    /// </summary>
    public int? LastMovedBySyncRuleMappingId { get; set; }

    public SyncRuleMapping? LastMovedBySyncRuleMapping { get; set; }

    public DateTime Created { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// When the counter was last modified (UTC). Null if never modified after creation.
    /// </summary>
    public DateTime? LastUpdated { get; set; }
}
