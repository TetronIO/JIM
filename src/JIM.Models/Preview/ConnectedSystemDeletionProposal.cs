// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Preview;

/// <summary>
/// A proposal to delete a Connected System with its Synchronised Deprovisioning, as the deletion preview adapter
/// receives it (#134). It carries nothing: the system travels as the preview's target, and there is only one
/// deletion to preview, deprovisioning through synchronisation.
/// </summary>
/// <remarks>
/// The other deletion mode, deleting immediately and keeping contributed data, has no per-object consequences
/// beyond eligibility for deletion, so it is stated against this preview rather than previewed separately. A field
/// choosing between the two would describe a preview nobody can run.
/// </remarks>
public sealed record ConnectedSystemDeletionProposal;
