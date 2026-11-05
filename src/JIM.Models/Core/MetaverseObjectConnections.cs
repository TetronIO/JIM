// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Core;

/// <summary>
/// Which Connected System Object Types each of a set of Metaverse Objects is joined to, as a connection scoping criterion
/// reads it (#2021). It holds an answer only for the Metaverse Objects it was read for: asking about any other one returns
/// null, so a caller that never read an object's connections cannot be mistaken for one that found none.
/// </summary>
public sealed class MetaverseObjectConnections
{
    private readonly HashSet<Guid> _read;
    private readonly HashSet<(Guid MetaverseObjectId, int ConnectedSystemObjectTypeId)> _joined;

    /// <param name="read">Every Metaverse Object whose connections were read, including those joined to nothing.</param>
    /// <param name="joined">Each Metaverse Object and the Object Type of a Connected System Object joined to it.</param>
    public MetaverseObjectConnections(IEnumerable<Guid> read, IEnumerable<(Guid MetaverseObjectId, int ConnectedSystemObjectTypeId)> joined)
    {
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(joined);
        _read = [.. read];
        _joined = [.. joined];
    }

    /// <summary>
    /// Whether the Metaverse Object is joined to a Connected System Object of the Object Type; null when its connections
    /// were not read.
    /// </summary>
    public bool? Has(Guid metaverseObjectId, int connectedSystemObjectTypeId) =>
        _read.Contains(metaverseObjectId) ? _joined.Contains((metaverseObjectId, connectedSystemObjectTypeId)) : null;

    /// <summary>
    /// The connections of one Metaverse Object loaded with every Connected System Object joined to it. Only the caller
    /// knows that the collection is complete (synchronisation loads only the current page's objects), so this is for
    /// callers that loaded it whole. A Connected System Object whose reference to the Metaverse Object has been cleared
    /// is not joined, whatever its join type still says.
    /// </summary>
    public static MetaverseObjectConnections From(MetaverseObject metaverseObject)
    {
        ArgumentNullException.ThrowIfNull(metaverseObject);
        var id = metaverseObject.Id;
        return new MetaverseObjectConnections([id], metaverseObject.ConnectedSystemObjects
            .Where(cso => cso.MetaverseObjectId == id)
            .Select(cso => (id, cso.TypeId)));
    }
}
