// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Transactional.DTOs;

/// <summary>
/// Summary-tier projection of an exported Create Pending Export targeting a Pending Provisioning
/// Connected System Object, used only by a Full Import's unseen exported-Create retry step
/// (<c>SyncImportTaskProcessor.RetryUnconfirmedExportedCreatesAsync</c>). Carries just enough to decide,
/// in memory, whether this run saw the Connected System Object: its own primary External Id value, held
/// as typed nullable columns rather than a materialised
/// <see cref="Staging.ConnectedSystemObjectAttributeValue"/>. Mirrors exactly how
/// <see cref="Staging.ConnectedSystemObject.ExternalIdAttributeValue"/> picks the value (the attribute
/// value row whose AttributeId matches the Connected System Object's own ExternalIdAttributeId); all five
/// typed columns are null when the object has no such value yet.
/// <para>
/// Only candidates this decides are UNSEEN are worth promoting to the full Pending Export /
/// attribute-change / Connected System Object / attribute-value graph load
/// (<c>ISyncRepository.GetExportedCreatePendingExportsForPendingProvisioningCsosAsync</c>): in the
/// ordinary case every candidate was confirmed by the very next Full Import, so this projection is the
/// difference between materialising nothing at all and materialising the full graph of every exported
/// Create in the Connected System.
/// </para>
/// </summary>
public class PendingExportRetryCandidateSummary
{
    public Guid PendingExportId { get; set; }

    public Guid ConnectedSystemObjectId { get; set; }

    public string? ExternalIdStringValue { get; set; }

    public int? ExternalIdIntValue { get; set; }

    public long? ExternalIdLongValue { get; set; }

    public decimal? ExternalIdDecimalValue { get; set; }

    public Guid? ExternalIdGuidValue { get; set; }
}
