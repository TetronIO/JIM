// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Sync;

/// <summary>
/// One Connected System Object that holds a candidate value for a Connected System Object Type attribute, as
/// the Unique Value Generation connector-space gate needs it (#242): which object holds the value and which
/// Metaverse Object it is joined to (saved joins only). The gate decides per request whether the holder is the
/// requesting object's own account (not a collision) or someone else's (a collision), so one lookup per
/// attribute serves a whole batch.
/// </summary>
/// <param name="NormalisedValue">The held text value, lower-cased; null for a numeric lookup.</param>
/// <param name="NumberValue">The held number; null for a text lookup.</param>
/// <param name="ConnectedSystemObjectId">The Connected System Object holding the value.</param>
/// <param name="MetaverseObjectId">The Metaverse Object it is joined to, as persisted; null when not joined.</param>
public sealed record ConnectorSpaceValueHolder(string? NormalisedValue, long? NumberValue, Guid ConnectedSystemObjectId, Guid? MetaverseObjectId);
