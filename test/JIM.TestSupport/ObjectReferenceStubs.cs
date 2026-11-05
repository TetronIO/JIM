// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.History;
using JIM.Models.Staging;

namespace JIM.TestSupport;

/// <summary>
/// Stands in for the reference resolution batch reads (#2669) in tests that mock the repositories: builds the
/// <see cref="ResolvedObjectReference"/> the database read would return for an object a test holds in memory, live or as
/// a deletion record. What the database read itself returns is covered against real PostgreSQL.
/// </summary>
public static class ObjectReferenceStubs
{
    /// <summary>
    /// Resolves each id against live Connected System Objects, then deletion records, as the batch read does. Every id
    /// has an entry.
    /// </summary>
    public static Dictionary<Guid, ResolvedObjectReference> ResolveConnectedSystemObjects(
        IEnumerable<Guid> ids,
        IReadOnlyDictionary<Guid, ConnectedSystemObject> live,
        IReadOnlyDictionary<Guid, ConnectedSystemObjectChange>? deletionRecords = null,
        IReadOnlyDictionary<int, string>? connectedSystemNames = null)
    {
        return ids.Distinct().ToDictionary(id => id, id =>
        {
            if (live.TryGetValue(id, out var cso))
                return Live(cso);
            if (deletionRecords != null && deletionRecords.TryGetValue(id, out var record))
                return Deleted(id, record, connectedSystemNames?.GetValueOrDefault(record.ConnectedSystemId));
            return ResolvedObjectReference.NotHeld(id);
        });
    }

    /// <summary>
    /// Resolves each id against live Metaverse Objects, then deletion records, as the batch read does. Every id has an
    /// entry.
    /// </summary>
    public static Dictionary<Guid, ResolvedObjectReference> ResolveMetaverseObjects(
        IEnumerable<Guid> ids,
        IReadOnlyDictionary<Guid, MetaverseObject> live,
        IReadOnlyDictionary<Guid, MetaverseObjectChange>? deletionRecords = null)
    {
        return ids.Distinct().ToDictionary(id => id, id =>
        {
            if (live.TryGetValue(id, out var mvo))
                return Live(mvo);
            if (deletionRecords != null && deletionRecords.TryGetValue(id, out var record))
                return Deleted(id, record);
            return ResolvedObjectReference.NotHeld(id);
        });
    }

    /// <summary>A live Connected System Object, as the batch read describes one.</summary>
    public static ResolvedObjectReference Live(ConnectedSystemObject cso) => new()
    {
        Id = cso.Id,
        State = ObjectReferenceState.Live,
        Label = cso.NameOrId,
        DisplayName = cso.Name,
        ExternalId = cso.ExternalIdAttributeValue?.ToStringNoName(),
        ObjectTypeId = cso.TypeId,
        ObjectTypeName = cso.Type?.Name,
        ConnectedSystemId = cso.ConnectedSystemId,
        ConnectedSystemName = cso.ConnectedSystem?.Name
    };

    /// <summary>A live Metaverse Object, as the batch read describes one.</summary>
    public static ResolvedObjectReference Live(MetaverseObject mvo) => new()
    {
        Id = mvo.Id,
        State = ObjectReferenceState.Live,
        Label = mvo.Name,
        DisplayName = mvo.Name,
        ObjectTypeId = mvo.Type?.Id,
        ObjectTypeName = mvo.Type?.Name,
        ObjectTypePluralName = mvo.Type?.PluralName
    };

    /// <summary>A deleted Connected System Object, as the batch read describes one from its deletion record.</summary>
    public static ResolvedObjectReference Deleted(Guid id, ConnectedSystemObjectChange record, string? connectedSystemName = null) => new()
    {
        Id = id,
        State = ObjectReferenceState.Deleted,
        Label = !string.IsNullOrEmpty(record.DeletedObjectDisplayName) ? record.DeletedObjectDisplayName : record.DeletedObjectExternalId,
        DisplayName = record.DeletedObjectDisplayName,
        ExternalId = record.DeletedObjectExternalId,
        ObjectTypeId = record.DeletedObjectType?.Id,
        ObjectTypeName = record.DeletedObjectType?.Name,
        ConnectedSystemId = record.ConnectedSystemId,
        ConnectedSystemName = connectedSystemName,
        DeletedAt = record.ChangeTime
    };

    /// <summary>A deleted Metaverse Object, as the batch read describes one from its deletion record.</summary>
    public static ResolvedObjectReference Deleted(Guid id, MetaverseObjectChange record) => new()
    {
        Id = id,
        State = ObjectReferenceState.Deleted,
        Label = record.DeletedObjectDisplayName,
        DisplayName = record.DeletedObjectDisplayName,
        ObjectTypeId = record.DeletedObjectTypeId ?? record.DeletedObjectType?.Id,
        ObjectTypeName = record.DeletedObjectType?.Name,
        ObjectTypePluralName = record.DeletedObjectType?.PluralName,
        DeletedAt = record.ChangeTime
    };
}
