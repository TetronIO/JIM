// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Interfaces;
using JIM.Application.Services;
using JIM.Models.Core;
using JIM.Models.Interfaces;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Models.Sync;
using JIM.Models.Transactional;
using Serilog;

namespace JIM.Application.Servers;

/// <summary>
/// Pure domain engine for synchronisation decisions.
/// All public methods are synchronous — no I/O, no async, no database access.
/// Takes plain objects in, returns decision records out.
/// </summary>
public partial class SyncEngine : ISyncEngine
{
    /// <inheritdoc />
    public ProjectionDecision EvaluateProjection(
        ConnectedSystemObject cso,
        IReadOnlyList<SyncRule> activeSyncRules)
    {
        var projectionSyncRule = activeSyncRules.FirstOrDefault(sr =>
            sr.ProjectToMetaverse.HasValue && sr.ProjectToMetaverse.Value &&
            sr.ConnectedSystemObjectType.Id == cso.TypeId);

        if (projectionSyncRule == null)
            return ProjectionDecision.NoProjection();

        return ProjectionDecision.Project(projectionSyncRule.MetaverseObjectType!, projectionSyncRule);
    }

    /// <inheritdoc />
    public List<AttributeFlowError> FlowInboundAttributes(
        ConnectedSystemObject cso,
        SyncRule syncRule,
        IReadOnlyList<ConnectedSystemObjectType> objectTypes,
        IExpressionEvaluator? expressionEvaluator = null,
        bool skipReferenceAttributes = false,
        bool onlyReferenceAttributes = false,
        bool isFinalReferencePass = false,
        AttributePriorityContext? priorityContext = null)
    {
        var errors = new List<AttributeFlowError>();

        if (cso.MetaverseObject == null)
        {
            Log.Error("FlowInboundAttributes: CSO ({CsoId}) has no MVO!", cso.Id);
            return errors;
        }

        foreach (var syncRuleMapping in syncRule.AttributeFlowRules)
        {
            if (syncRuleMapping.TargetMetaverseAttribute == null)
                throw new InvalidDataException("SyncRuleMapping.TargetMetaverseAttribute must not be null.");

            ProcessMapping(cso, syncRuleMapping, objectTypes, expressionEvaluator,
                skipReferenceAttributes, onlyReferenceAttributes, isFinalReferencePass,
                cso.ConnectedSystemId, errors, syncRule.MetaverseObjectTypeId, priorityContext);
        }

        return errors;
    }

    /// <inheritdoc />
    public PendingExportConfirmationResult EvaluatePendingExportConfirmation(
        ConnectedSystemObject cso,
        Dictionary<Guid, List<PendingExport>>? pendingExportsByCsoId)
    {
        if (pendingExportsByCsoId == null ||
            !pendingExportsByCsoId.TryGetValue(cso.Id, out var pendingExportsForThisCso) ||
            pendingExportsForThisCso.Count == 0)
        {
            return PendingExportConfirmationResult.None();
        }

        var toDelete = new List<PendingExport>();
        var toUpdate = new List<PendingExport>();

        foreach (var pendingExport in pendingExportsForThisCso.ToList())
        {
            // Skip Pending Exports that have not been exported yet
            if (pendingExport.Status == PendingExportStatus.Pending)
            {
                Log.Verbose("EvaluatePendingExportConfirmation: Skipping Pending Export {PeId} - not yet exported (Status=Pending).", pendingExport.Id);
                continue;
            }

            // Skip Pending Exports awaiting confirmation via confirming import
            if (pendingExport.Status == PendingExportStatus.Exported)
            {
                Log.Verbose("EvaluatePendingExportConfirmation: Skipping Pending Export {PeId} - awaiting confirmation via import (Status=Exported).", pendingExport.Id);
                continue;
            }

            var successfulChanges = new List<PendingExportAttributeValueChange>();
            var failedChanges = new List<PendingExportAttributeValueChange>();

            foreach (var attributeChange in pendingExport.AttributeValueChanges)
            {
                // Use the comprehensive type-aware comparison
                if (IsAttributeChangeConfirmed(cso, attributeChange))
                    successfulChanges.Add(attributeChange);
                else
                    failedChanges.Add(attributeChange);
            }

            if (failedChanges.Count == 0)
            {
                Log.Information("EvaluatePendingExportConfirmation: All changes confirmed for Pending Export {PeId}. Marking for deletion.", pendingExport.Id);
                toDelete.Add(pendingExport);
                pendingExportsForThisCso.Remove(pendingExport);
            }
            else if (successfulChanges.Count > 0)
            {
                Log.Information("EvaluatePendingExportConfirmation: Partial success for Pending Export {PeId}. " +
                    "{SuccessCount} succeeded, {FailCount} failed. Marking for update.",
                    pendingExport.Id, successfulChanges.Count, failedChanges.Count);

                foreach (var successfulChange in successfulChanges)
                    pendingExport.AttributeValueChanges.Remove(successfulChange);

                if (pendingExport.ChangeType == PendingExportChangeType.Create)
                {
                    Log.Information("EvaluatePendingExportConfirmation: Changing Pending Export {PeId} from Create to Update.", pendingExport.Id);
                    pendingExport.ChangeType = PendingExportChangeType.Update;
                }

                pendingExport.ErrorCount++;
                pendingExport.Status = PendingExportStatus.ExportNotConfirmed;
                toUpdate.Add(pendingExport);
            }
            else
            {
                Log.Warning("EvaluatePendingExportConfirmation: Complete failure for Pending Export {PeId}. " +
                    "All {FailCount} attribute changes failed. Marking for update.", pendingExport.Id, failedChanges.Count);

                pendingExport.ErrorCount++;
                pendingExport.Status = PendingExportStatus.ExportNotConfirmed;
                toUpdate.Add(pendingExport);
            }
        }

        return PendingExportConfirmationResult.Create(toDelete, toUpdate);
    }

    /// <inheritdoc />
    public MvoDeletionDecision EvaluateMvoDeletionRule(
        MetaverseObject mvo,
        int disconnectingSystemId,
        IReadOnlyCollection<int> remainingConnectedSystemIds)
    {
        // One entry per remaining joined CSO, so the count is CSO-level; duplicates per system are
        // deliberate (a system with two joined CSOs contributes two entries).
        var remainingCsoCount = remainingConnectedSystemIds.Count;

        if (mvo.Type == null)
        {
            Log.Warning("EvaluateMvoDeletionRule: MVO {MvoId} has no Type set. Cannot determine deletion rule.", mvo.Id);
            return MvoDeletionDecision.NotDeleted("No MVO type set");
        }

        // Only apply to Projected MVOs (Internal MVOs like admin accounts are protected)
        if (mvo.Origin == MetaverseObjectOrigin.Internal)
        {
            Log.Verbose("EvaluateMvoDeletionRule: MVO {MvoId} has Origin=Internal. Protected from automatic deletion.", mvo.Id);
            return MvoDeletionDecision.NotDeleted("Origin=Internal, protected from automatic deletion");
        }

        switch (mvo.Type.DeletionRule)
        {
            case MetaverseObjectDeletionRule.Manual:
                Log.Verbose("EvaluateMvoDeletionRule: MVO {MvoId} has DeletionRule=Manual. No automatic deletion.", mvo.Id);
                return MvoDeletionDecision.NotDeleted("DeletionRule=Manual");

            case MetaverseObjectDeletionRule.WhenLastConnectorDisconnected:
                if (remainingCsoCount > 0)
                {
                    Log.Verbose("EvaluateMvoDeletionRule: MVO {MvoId} has {Count} remaining connector(s). Not marking for deletion yet.",
                        mvo.Id, remainingCsoCount);
                    return MvoDeletionDecision.NotDeleted($"{remainingCsoCount} remaining connector(s)");
                }
                return EvaluateGracePeriod(mvo, "last connector disconnected");

            case MetaverseObjectDeletionRule.WhenAuthoritativeSourceDisconnected:
                var triggerIds = mvo.Type.DeletionTriggerConnectedSystemIds;
                if (triggerIds == null || triggerIds.Count == 0)
                {
                    Log.Warning("EvaluateMvoDeletionRule: MVO {MvoId} has DeletionRule=WhenAuthoritativeSourceDisconnected but no DeletionTriggerConnectedSystemIds configured. " +
                        "Falling back to WhenLastConnectorDisconnected behaviour.", mvo.Id);
                    if (remainingCsoCount == 0)
                        return EvaluateGracePeriod(mvo, "last connector disconnected (no authoritative sources configured)");
                    return MvoDeletionDecision.NotDeleted($"{remainingCsoCount} remaining connector(s), no authoritative sources configured");
                }

                if (!triggerIds.Contains(disconnectingSystemId))
                {
                    // Common to both trigger modes: a system that is not a listed source never triggers deletion.
                    Log.Verbose("EvaluateMvoDeletionRule: System ID {SystemId} disconnected from MVO {MvoId} but is not an authoritative source. " +
                        "Authoritative sources: [{AuthSources}]. Not marking for deletion.",
                        disconnectingSystemId, mvo.Id, string.Join(", ", triggerIds));
                    return MvoDeletionDecision.NotDeleted($"System {disconnectingSystemId} is not an authoritative source");
                }

                if (mvo.Type.DeletionTriggerMode == AuthoritativeSourceTriggerMode.AllSourcesDisconnect)
                {
                    // All sources mode: only trigger once no listed source retains a joined CSO.
                    // A remaining CSO from the disconnecting system itself counts too (its id is still in
                    // the remaining list), so a system with a second joined CSO does not trigger deletion.
                    var remainingSourceCount = remainingConnectedSystemIds.Where(triggerIds.Contains).Distinct().Count();
                    if (remainingSourceCount > 0)
                    {
                        Log.Verbose("EvaluateMvoDeletionRule: Authoritative source (system ID {SystemId}) disconnected from MVO {MvoId}, " +
                            "but {RemainingSourceCount} of {SourceCount} authoritative source(s) remain connected (All sources mode). Not marking for deletion.",
                            disconnectingSystemId, mvo.Id, remainingSourceCount, triggerIds.Count);
                        return MvoDeletionDecision.NotDeleted(
                            $"All sources mode: {remainingSourceCount} of {triggerIds.Count} sources {(remainingSourceCount == 1 ? "remains" : "remain")} connected");
                    }

                    Log.Information("EvaluateMvoDeletionRule: Authoritative source (system ID {SystemId}) disconnected from MVO {MvoId} and no " +
                        "authoritative sources remain connected (All sources mode). Triggering deletion even though {Count} connector(s) remain.",
                        disconnectingSystemId, mvo.Id, remainingCsoCount);
                    return EvaluateGracePeriod(mvo,
                        $"All sources mode: authoritative source (system ID {disconnectingSystemId}) disconnected and no sources remain connected");
                }

                // Specific sources mode: any listed source disconnecting triggers deletion (pre-#119 behaviour).
                Log.Information("EvaluateMvoDeletionRule: Authoritative source (system ID {SystemId}) disconnected from MVO {MvoId} (Specific sources mode). " +
                    "Triggering deletion even though {Count} connector(s) remain.",
                    disconnectingSystemId, mvo.Id, remainingCsoCount);
                return EvaluateGracePeriod(mvo, $"Specific sources mode: authoritative source (system ID {disconnectingSystemId}) disconnected");

            default:
                Log.Warning("EvaluateMvoDeletionRule: Unknown DeletionRule {Rule} for MVO {MvoId}.", mvo.Type.DeletionRule, mvo.Id);
                return MvoDeletionDecision.NotDeleted($"Unknown DeletionRule {mvo.Type.DeletionRule}");
        }
    }

    /// <inheritdoc />
    public bool ShouldCancelScheduledDeletion(MetaverseObject mvo, int rejoiningSystemId)
    {
        if (mvo.Type == null)
        {
            // Consistent with EvaluateMvoDeletionRule's null-Type handling: warn and take the safe path.
            // Cancelling on any rejoin matches the pre-#119 behaviour and errs away from deleting data.
            Log.Warning("ShouldCancelScheduledDeletion: MVO {MvoId} has no Type set. Cannot determine deletion rule; cancelling the scheduled deletion on rejoin.", mvo.Id);
            return true;
        }

        switch (mvo.Type.DeletionRule)
        {
            case MetaverseObjectDeletionRule.Manual:
                // A Manual-rule MVO should never carry a disconnection-scheduled deletion; if one exists
                // the state is inconsistent (for example the rule changed after scheduling), and
                // cancelling on rejoin clears it, matching the pre-#119 cancel-on-any-rejoin behaviour.
                Log.Verbose("ShouldCancelScheduledDeletion: MVO {MvoId} has DeletionRule=Manual. Cancelling the scheduled deletion on rejoin.", mvo.Id);
                return true;

            case MetaverseObjectDeletionRule.WhenLastConnectorDisconnected:
                // A connector now exists, so the "no connectors remain" condition no longer holds.
                return true;

            case MetaverseObjectDeletionRule.WhenAuthoritativeSourceDisconnected:
                var triggerIds = mvo.Type.DeletionTriggerConnectedSystemIds;
                if (triggerIds == null || triggerIds.Count == 0)
                {
                    // With no sources configured, scheduling fell back to WhenLastConnectorDisconnected
                    // semantics, so cancellation follows the same any-rejoin rule.
                    return true;
                }

                if (mvo.DeletionTriggeredBySystemId == null)
                {
                    // Rows marked before the triggering system was recorded (pre-#119): fall back to the
                    // pre-existing cancel-on-any-rejoin behaviour rather than stranding a scheduled deletion.
                    Log.Information("ShouldCancelScheduledDeletion: MVO {MvoId} has no recorded DeletionTriggeredBySystemId (marked pre-upgrade). " +
                        "Falling back to cancel-on-any-rejoin.", mvo.Id);
                    return true;
                }

                if (mvo.Type.DeletionTriggerMode == AuthoritativeSourceTriggerMode.AllSourcesDisconnect)
                {
                    // All sources mode: any listed source rejoining falsifies the "all sources gone" condition.
                    return triggerIds.Contains(rejoiningSystemId);
                }

                // Specific sources mode: only undoing the disconnection that caused the scheduling cancels.
                return rejoiningSystemId == mvo.DeletionTriggeredBySystemId;

            default:
                Log.Warning("ShouldCancelScheduledDeletion: Unknown DeletionRule {Rule} for MVO {MvoId}. Cancelling the scheduled deletion on rejoin.",
                    mvo.Type.DeletionRule, mvo.Id);
                return true;
        }
    }

    /// <inheritdoc />
    public void ApplyPendingAttributeChanges(MetaverseObject mvo)
    {
        var addCount = mvo.PendingAttributeValueAdditions.Count;
        var removeCount = mvo.PendingAttributeValueRemovals.Count;

        if (addCount == 0 && removeCount == 0)
            return;

        foreach (var removal in mvo.PendingAttributeValueRemovals)
            mvo.AttributeValues.Remove(removal);

        foreach (var addition in mvo.PendingAttributeValueAdditions)
            mvo.AttributeValues.Add(addition);

        mvo.PendingAttributeValueRemovals.Clear();
        mvo.PendingAttributeValueAdditions.Clear();

        // Keep the denormalised CachedDisplayName in sync with the canonical attribute value.
        // This cached column enables efficient sorting at scale without correlated subqueries.
        var displayNameAv = mvo.AttributeValues
            .SingleOrDefault(av => av.Attribute?.Name == Constants.BuiltInAttributes.DisplayName);
        mvo.CachedDisplayName = displayNameAv?.StringValue;

        Log.Verbose("ApplyPendingAttributeChanges: Applied {AddCount} additions and {RemoveCount} removals to MVO {MvoId}",
            addCount, removeCount, mvo.Id);
    }

    /// <inheritdoc />
    public InboundOutOfScopeAction DetermineOutOfScopeAction(
        ConnectedSystemObject cso,
        IReadOnlyList<SyncRule> activeSyncRules)
    {
        var importSyncRule = activeSyncRules.FirstOrDefault(sr =>
            sr.Direction == SyncRuleDirection.Import &&
            sr.Enabled &&
            sr.ConnectedSystemObjectTypeId == cso.TypeId);

        if (importSyncRule == null)
            return InboundOutOfScopeAction.Disconnect;

        return importSyncRule.InboundOutOfScopeAction;
    }

    /// <summary>
    /// Evaluates the grace period for an MVO deletion decision.
    /// </summary>
    private static MvoDeletionDecision EvaluateGracePeriod(MetaverseObject mvo, string reason)
    {
        var gracePeriod = mvo.Type!.DeletionGracePeriod;

        if (!gracePeriod.HasValue || gracePeriod.Value == TimeSpan.Zero)
        {
            Log.Information("EvaluateMvoDeletionRule: MVO {MvoId} queued for immediate deletion ({Reason}). No grace period configured.",
                mvo.Id, reason);
            return MvoDeletionDecision.DeleteImmediately(reason);
        }

        Log.Information("EvaluateMvoDeletionRule: MVO {MvoId} marked for deletion ({Reason}). Eligible after {GracePeriod}.",
            mvo.Id, reason, gracePeriod.Value);
        return MvoDeletionDecision.ScheduleDeletion(gracePeriod.Value, reason);
    }
}
