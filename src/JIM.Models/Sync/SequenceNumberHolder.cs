// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Sync;

/// <summary>
/// One holding of a number inside a run of held sequence numbers (#2031): which number, where it was found, and who
/// holds it, so the caller can decide per object whether the holding is that object's own (and so not a collision
/// for it), exactly as the uniqueness gates decide.
/// </summary>
/// <param name="Number">The held sequence number.</param>
/// <param name="Kind">Where it was found.</param>
/// <param name="ObjectId">The holder: the Metaverse Object (import mode) or Connected System Object (export mode) for
/// <see cref="SequenceNumberHolderKind.AttributeValue"/> and <see cref="SequenceNumberHolderKind.Assignment"/>; the
/// Connected System Object for <see cref="SequenceNumberHolderKind.ConnectorSpace"/>; null for
/// <see cref="SequenceNumberHolderKind.Retired"/>.</param>
/// <param name="MetaverseObjectId">For <see cref="SequenceNumberHolderKind.ConnectorSpace"/>, the Metaverse Object the
/// holding account is joined to, as persisted; otherwise null.</param>
public sealed record SequenceNumberHolder(long Number, SequenceNumberHolderKind Kind, Guid? ObjectId, Guid? MetaverseObjectId);
