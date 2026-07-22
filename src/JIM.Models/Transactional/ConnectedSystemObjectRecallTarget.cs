// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Staging;

namespace JIM.Models.Transactional;

/// <summary>
/// Summary-tier projection of a referencing Metaverse Object's Connected System Object in a
/// recall target system (#1003): the scalars reference recall staging needs to address a
/// membership-removal Pending Export, without materialising the CSO or its attribute values.
/// </summary>
public class ConnectedSystemObjectRecallTarget
{
    public Guid ConnectedSystemObjectId { get; set; }

    public Guid MetaverseObjectId { get; set; }

    public int ConnectedSystemId { get; set; }

    /// <summary>
    /// The CSO status; recall skips PendingProvisioning targets (they have no presence in the
    /// target system yet, so there is nothing to remove and the pending Create export must be
    /// left untouched).
    /// </summary>
    public ConnectedSystemObjectStatus Status { get; set; }
}
