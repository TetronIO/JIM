// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Transactional;

/// <summary>
/// A deleted Metaverse Object's reference in one target Connected System, captured before deletion for reference
/// recall (#908): the Connected System Object it was joined to there, and the value export writes for a reference to
/// that object (for example its distinguished name). Both come from the same Connected System Object, so a removal staged
/// from them names one object by its id and by its value alike.
/// </summary>
/// <param name="ConnectedSystemObjectId">The Connected System Object the deleted Metaverse Object was joined to in the
/// target system. It may itself be deprovisioned, and deleted, before the removal is exported or confirmed: a removal
/// holds it as a soft pointer (<see cref="PendingExportAttributeValueChange.ResolvedReferenceCsoId"/>).</param>
/// <param name="Value">The value a reference to that object is written as: its Secondary External ID when it has one,
/// else its External ID.</param>
public sealed record ResolvedRecallReference(Guid ConnectedSystemObjectId, string Value);
