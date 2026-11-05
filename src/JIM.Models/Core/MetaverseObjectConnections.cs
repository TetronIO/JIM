// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Staging;

namespace JIM.Models.Core;

/// <summary>
/// Which Connected System Object Types each of a set of Metaverse Objects is joined to, as a connection scoping criterion
/// reads it (#2021). It holds an answer only for the Metaverse Objects, and the Object Types, it was read for: asking about
/// any other returns null, so a caller that never read an object's connections cannot be mistaken for one that found none.
/// </summary>
public sealed class MetaverseObjectConnections
{
    private readonly HashSet<Guid> _read;
    private readonly HashSet<(Guid MetaverseObjectId, int ConnectedSystemObjectTypeId)> _joined;
    private readonly HashSet<int>? _readObjectTypeIds;

    /// <param name="read">Every Metaverse Object whose connections were read, including those joined to nothing.</param>
    /// <param name="joined">Each Metaverse Object and the Object Type of a Connected System Object joined to it.</param>
    /// <param name="readObjectTypeIds">The Object Types the read looked for; null when it looked for every type.</param>
    public MetaverseObjectConnections(
        IEnumerable<Guid> read,
        IEnumerable<(Guid MetaverseObjectId, int ConnectedSystemObjectTypeId)> joined,
        IEnumerable<int>? readObjectTypeIds = null)
    {
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(joined);
        _read = [.. read];
        _joined = [.. joined];
        _readObjectTypeIds = readObjectTypeIds?.ToHashSet();
    }

    /// <summary>
    /// Whether the Metaverse Object is joined to a Connected System Object of the Object Type; null when its connections,
    /// or connections of that type, were not read.
    /// </summary>
    public bool? Has(Guid metaverseObjectId, int connectedSystemObjectTypeId)
    {
        if (!_read.Contains(metaverseObjectId))
            return null;
        if (_readObjectTypeIds != null && !_readObjectTypeIds.Contains(connectedSystemObjectTypeId))
            return null;
        return _joined.Contains((metaverseObjectId, connectedSystemObjectTypeId));
    }

    /// <summary>The Object Types the Metaverse Object is joined to, among those read; empty when its connections were not read.</summary>
    public IReadOnlySet<int> JoinedObjectTypeIds(Guid metaverseObjectId) =>
        _joined.Where(join => join.MetaverseObjectId == metaverseObjectId).Select(join => join.ConnectedSystemObjectTypeId).ToHashSet();

    /// <summary>
    /// A copy in which the Metaverse Object's connections are exactly <paramref name="joinedObjectTypeIds"/>, read as
    /// for the other objects; the others are unchanged. For a preview that has made or broken a join in memory, which
    /// the read cannot see.
    /// </summary>
    public MetaverseObjectConnections WithObject(Guid metaverseObjectId, IEnumerable<int> joinedObjectTypeIds)
    {
        ArgumentNullException.ThrowIfNull(joinedObjectTypeIds);
        return new MetaverseObjectConnections(
            _read.Append(metaverseObjectId),
            _joined.Where(join => join.MetaverseObjectId != metaverseObjectId)
                .Concat(joinedObjectTypeIds.Select(typeId => (metaverseObjectId, typeId))),
            _readObjectTypeIds);
    }

    /// <summary>
    /// The connections of one Metaverse Object loaded with every Connected System Object joined to it. Only the caller
    /// knows that the collection is complete (synchronisation loads only the current page's objects), so this is for
    /// callers that loaded it whole. A Connected System Object whose reference to the Metaverse Object has been cleared
    /// is not joined, whatever its join type still says, and an Obsolete one is not a connection: its Connected System
    /// no longer holds it, and it stays joined only until synchronisation removes it.
    /// </summary>
    public static MetaverseObjectConnections From(MetaverseObject metaverseObject)
    {
        ArgumentNullException.ThrowIfNull(metaverseObject);
        return FromJoined(metaverseObject.Id, metaverseObject.ConnectedSystemObjects);
    }

    /// <summary>
    /// The connections of one Metaverse Object, from every Connected System Object joined to it; as
    /// <see cref="From(MetaverseObject)"/>, for a caller that loaded the joined objects on their own. Objects whose
    /// reference names another Metaverse Object, or none, are ignored.
    /// </summary>
    public static MetaverseObjectConnections FromJoined(Guid metaverseObjectId, IEnumerable<ConnectedSystemObject> joinedObjects)
    {
        ArgumentNullException.ThrowIfNull(joinedObjects);
        return new MetaverseObjectConnections([metaverseObjectId], joinedObjects
            .Where(cso => cso.MetaverseObjectId == metaverseObjectId && cso.Status != ConnectedSystemObjectStatus.Obsolete)
            .Select(cso => (metaverseObjectId, cso.TypeId)));
    }
}
