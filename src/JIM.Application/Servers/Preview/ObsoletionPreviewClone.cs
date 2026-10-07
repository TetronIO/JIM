// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Staging;

namespace JIM.Application.Servers.Preview;

/// <summary>
/// Preview-owned copies of the objects an obsoletion acts on, so the run's own obsoletion core
/// (<see cref="Services.ConnectedSystemObjectObsoletionService"/>) can be driven read-only: it breaks the join and recalls
/// values by mutating the objects it is handed, and a preview may mutate nothing it did not create. Shared by the two
/// previews that drive it: deleting a Connected System (#134) and a Full Synchronisation tearing down obsolete objects
/// (#1530).
/// </summary>
internal static class ObsoletionPreviewClone
{
    /// <summary>
    /// A copy of a Connected System Object joined to a copy of its Metaverse Object. The Metaverse Object copy is shared
    /// through <paramref name="mvoClones"/>, so two objects joined to the same identity act on the same working copy, as
    /// they act on the same instance in the run. Value instances are shared, not copied: the core adds and removes list
    /// entries but never mutates a value in place.
    /// </summary>
    /// <param name="cso">The object to copy.</param>
    /// <param name="metaverseObject">The Metaverse Object the copy is joined to, or null to copy the object's own
    /// navigation.</param>
    /// <param name="mvoClones">The Metaverse Object copies made so far, keyed by id.</param>
    internal static ConnectedSystemObject Of(ConnectedSystemObject cso, MetaverseObject? metaverseObject, Dictionary<Guid, MetaverseObject> mvoClones)
    {
        metaverseObject ??= cso.MetaverseObject;
        MetaverseObject? mvoClone = null;
        if (metaverseObject != null && !mvoClones.TryGetValue(metaverseObject.Id, out mvoClone))
        {
            mvoClone = Of(metaverseObject);
            mvoClones[mvoClone.Id] = mvoClone;
        }

        var clone = new ConnectedSystemObject
        {
            Id = cso.Id,
            Created = cso.Created,
            LastUpdated = cso.LastUpdated,
            Type = cso.Type,
            TypeId = cso.TypeId,
            ConnectedSystem = cso.ConnectedSystem,
            ConnectedSystemId = cso.ConnectedSystemId,
            PartitionId = cso.PartitionId,
            ExternalIdAttributeId = cso.ExternalIdAttributeId,
            SecondaryExternalIdAttributeId = cso.SecondaryExternalIdAttributeId,
            AttributeValues = [.. cso.AttributeValues],
            Status = cso.Status,
            MetaverseObject = mvoClone,
            MetaverseObjectId = cso.MetaverseObjectId,
            JoinType = cso.JoinType,
            DateJoined = cso.DateJoined
        };
        mvoClone?.ConnectedSystemObjects.Add(clone);
        return clone;
    }

    /// <summary>
    /// A copy of a Metaverse Object carrying what the obsoletion core and the Deletion Rule read: identity, type, origin,
    /// values, and any deletion already pending.
    /// </summary>
    internal static MetaverseObject Of(MetaverseObject mvo)
    {
        var clone = new MetaverseObject
        {
            Id = mvo.Id,
            Type = mvo.Type,
            Origin = mvo.Origin,
            Created = mvo.Created,
            CachedDisplayName = mvo.CachedDisplayName,
            LastConnectorDisconnectedDate = mvo.LastConnectorDisconnectedDate,
            DeletionTriggeredBySystemId = mvo.DeletionTriggeredBySystemId
        };
        foreach (var attributeValue in mvo.AttributeValues)
            clone.AttributeValues.Add(attributeValue);
        return clone;
    }
}
