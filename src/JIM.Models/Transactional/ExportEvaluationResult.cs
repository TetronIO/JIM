// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Models.Sync;

namespace JIM.Models.Transactional;

/// <summary>
/// Result of export evaluation including Pending Exports and no-net-change statistics.
/// </summary>
public class ExportEvaluationResult
{
    /// <summary>
    /// List of PendingExports that were created.
    /// </summary>
    public List<PendingExport> PendingExports { get; set; } = [];

    /// <summary>
    /// List of CSOs created for provisioning (when deferSave is true).
    /// These need to be batch-persisted by the caller before the Pending Exports.
    /// </summary>
    public List<ConnectedSystemObject> ProvisioningCsosToCreate { get; set; } = [];

    /// <summary>
    /// The export Synchronisation Rule that caused each provisioning CSO in
    /// <see cref="ProvisioningCsosToCreate"/> to be created, keyed by the provisioning CSO's id.
    /// Enables the worker to attribute Provisioned sync outcome nodes to the causing rule (#1085).
    /// </summary>
    public Dictionary<Guid, SyncRule> ProvisioningSyncRulesByCsoId { get; set; } = [];

    /// <summary>
    /// Count of attributes skipped because the CSO already has the current value.
    /// This represents true no-net-changes where the MVO had updates but the CSO matches.
    /// </summary>
    public int CsoAlreadyCurrentCount { get; set; }

    /// <summary>
    /// Ids of joined, non-PendingProvisioning Connected System Objects whose (Metaverse Object,
    /// export rule) pair passed the scope gate during this evaluation, whether or not any attribute
    /// changes were staged; used by the page flush to cancel stale Delete Pending Exports (#1018).
    /// </summary>
    public HashSet<Guid> InScopeJoinedCsoIds { get; set; } = [];

    /// <summary>
    /// Attribute Flow errors raised during evaluation: a multi-valued Metaverse source attribute held more
    /// than one value but the target Connected System attribute is single-valued (#435). No Pending Export
    /// change was generated for those attributes; the worker surfaces each as a MultiValuedToSingleValued RPEI.
    /// </summary>
    public List<AttributeFlowError> AttributeFlowErrors { get; set; } = [];
}
