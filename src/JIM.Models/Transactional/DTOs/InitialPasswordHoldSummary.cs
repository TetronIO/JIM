// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Transactional.DTOs;

/// <summary>
/// Summary-tier projection of one Pending Export attribute change held until an Initial Password is delivered
/// (<see cref="PendingExportAttributeValueChange.AwaitsInitialPassword"/>, still
/// <see cref="PendingExportAttributeChangeStatus.ExportedPendingConfirmation"/>, #2039), used only by an import's sweep of
/// the held changes it did not reconcile (<c>SyncImportTaskProcessor</c>). Carries just enough to decide, in memory,
/// whether the import returned the account and whether a password is still owed to it: in the ordinary case every
/// held account is still owed one, and the sweep then loads nothing more.
/// </summary>
public class InitialPasswordHoldSummary
{
    /// <summary>
    /// The held attribute change.
    /// </summary>
    public Guid AttributeValueChangeId { get; set; }

    /// <summary>
    /// The Pending Export the held change belongs to.
    /// </summary>
    public Guid PendingExportId { get; set; }

    /// <summary>
    /// The Connected System Object the Pending Export writes to.
    /// </summary>
    public Guid ConnectedSystemObjectId { get; set; }

    /// <summary>
    /// The Metaverse Object the Connected System Object is joined to, which is what a password is owed to; null when it
    /// is joined to none, and then nothing is owed.
    /// </summary>
    public Guid? MetaverseObjectId { get; set; }
}
