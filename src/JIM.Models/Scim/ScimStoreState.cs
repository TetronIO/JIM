// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Scim;

/// <summary>
/// One Connected System's SCIM store counters. The row is also the store's write lock: every write locks it first and
/// holds it until it commits, so a Connected System's writes commit one at a time, in the order of the change
/// sequences they were given. That is what lets an import read "everything after watermark w" without a write ever
/// committing behind it.
/// </summary>
public class ScimStoreState
{
    /// <summary>
    /// The Connected System whose store this describes.
    /// </summary>
    public int ConnectedSystemId { get; set; }

    /// <summary>
    /// The change sequence of the last committed write (0 before the first).
    /// </summary>
    public long LastChangeSequence { get; set; }

    /// <summary>
    /// The highest change sequence of any tombstone the purge has removed (0 when none has been). A Delta Import whose
    /// watermark is below this may have missed a deletion, so it must fail and ask for a Full Import.
    /// </summary>
    public long TombstonePurgeFloor { get; set; }
}
