// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Staging;

/// <summary>
/// Records that a Metaverse Object was joined to a Connected System Object at the moment its Connector
/// Space was cleared (#1605): a Connector Space clear hard-deletes Connected System Objects without
/// obsoletion, so there is otherwise no durable evidence of who was joined before the clear once the
/// Connected System Objects themselves are gone. Written as step zero of the clear's own transaction
/// (<c>ConnectedSystemRepository.DeleteAllConnectedSystemObjectsAndDependenciesAsync</c>, raw SQL
/// <c>INSERT ... SELECT</c> against the about-to-be-deleted Connected System Objects), one row per joined
/// object; a re-clear before the next sweep replaces the set (delete then insert) rather than accumulating.
/// <para>
/// Consumed by the post-clear reconciliation sweep, which reads the recorded set to compute the re-join
/// shortfall and to evaluate Deletion Rules for objects that did not return, then deletes the system's rows
/// once it completes. Also deleted as a step of Connected System deletion, ahead of the system row itself.
/// </para>
/// <para>
/// Deliberately carries no navigation properties: it is written and read entirely via raw SQL on the
/// worker/application hot path, exactly like the entities it derives from. There is no foreign key to
/// <c>MetaverseObjects</c> (a Metaverse Object deleted between the clear and the sweep must not block on
/// this record; the sweep simply finds it absent and skips it), but there is a cascading foreign key to
/// <c>ConnectedSystems</c> as belt and braces alongside the explicit deletion step in the delete sequence.
/// </para>
/// </summary>
public class ConnectorSpaceClearJoinRecord
{
    /// <summary>
    /// The Connected System whose clear recorded this join. Part of the composite key.
    /// </summary>
    public int ConnectedSystemId { get; set; }

    /// <summary>
    /// The Metaverse Object that was joined to one of the cleared Connected System's Connected System
    /// Objects at the moment of the clear. Part of the composite key.
    /// </summary>
    public Guid MetaverseObjectId { get; set; }

    /// <summary>
    /// The UTC time of the clear that wrote this record, matching the Connected System's
    /// <c>StrandedValueSweepArmedAt</c> at the moment the clear ran.
    /// </summary>
    public DateTime ClearedAt { get; set; }
}
