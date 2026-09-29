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
    /// Already-staged Pending Exports (e.g. from drift detection earlier in the same page) that this
    /// evaluation merged its attribute changes into, rather than creating a new row for (Unique Value
    /// Generation, #242). Distinct from <see cref="PendingExports"/>: an entry here already belongs to the
    /// caller's own batch-create list, so it must never be added a second time, but a generated export
    /// mapping's change merged into it still needs resolving (<c>SyncTaskProcessorBase.ResolveExportGeneratedValuesAsync</c>
    /// must scan this list too, not only <see cref="PendingExports"/>), or its marker survives unresolved
    /// into <c>FlushPendingExportOperationsAsync</c>'s integrity guard.
    /// </summary>
    public List<PendingExport> MergedExistingPendingExports { get; set; } = [];

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
    /// Attribute Flow errors raised during evaluation, each costing the object one attribute rather than the
    /// whole export: a multi-valued Metaverse source attribute held more than one value but the target Connected
    /// System attribute is single-valued (#435), or an Expression read an attribute the Metaverse Object has no
    /// value for while its Missing Input Behaviour is FailMapping (#1361). No Pending Export change was generated
    /// for those attributes; the worker surfaces each as the RPEI its <see cref="AttributeFlowError.Kind"/> names.
    /// </summary>
    public List<AttributeFlowError> AttributeFlowErrors { get; set; } = [];

    /// <summary>
    /// Outbound Synchronisation Rules that could not export because the Metaverse Object's one Connected
    /// System Object in the target Connected System is of a different Connected System Object Type than the
    /// Rule targets (#1331). No Pending Export was staged for those Rules; the worker surfaces each as a
    /// CouldNotExportDueToExistingConnectedSystemObject RPEI.
    /// </summary>
    public List<ExportObjectTypeConflict> ObjectTypeConflicts { get; set; } = [];
}
