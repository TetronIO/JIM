// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Transactional;

/// <summary>
/// One provisioning cancellation recorded by an export evaluation run: a Connected System Object that was still
/// <c>PendingProvisioning</c> with an unsent Create Pending Export had its provisioning withdrawn outright,
/// because a Metaverse Object deletion or a scope exit caught it before anything was ever exported. Immutable,
/// and recorded on <see cref="ExportEvaluationWorkingSet"/> only after the underlying deletes (the Pending Export
/// and the Connected System Object) have succeeded, so a failed batch write cannot leave a cancellation on record
/// that never actually happened.
/// </summary>
/// <param name="ConnectedSystemObjectId">The id of the Connected System Object whose provisioning was cancelled.</param>
/// <param name="ConnectedSystemId">The Connected System the cancelled provisioning targeted.</param>
/// <param name="MetaverseObjectId">The Metaverse Object the cancelled Connected System Object was joined to.</param>
public sealed record CancelledProvisioning(Guid ConnectedSystemObjectId, int ConnectedSystemId, Guid MetaverseObjectId);
