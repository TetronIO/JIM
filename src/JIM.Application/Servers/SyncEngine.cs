using JIM.Application.Interfaces;
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

        return ProjectionDecision.Project(projectionSyncRule.MetaverseObjectType!);
    }

    /// <inheritdoc />
    public void FlowInboundAttributes(
        ConnectedSystemObject cso,
        SyncRule syncRule,
        IReadOnlyList<ConnectedSystemObjectType> objectTypes,
        IExpressionEvaluator? expressionEvaluator = null,
        bool skipReferenceAttributes = false,
        bool onlyReferenceAttributes = false,
        bool isFinalReferencePass = false)
    {
        if (cso.MetaverseObject == null)
        {
            Log.Error("FlowInboundAttributes: CSO ({Cso}) has no MVO!", cso);
            return;
        }

        foreach (var syncRuleMapping in syncRule.AttributeFlowRules)
        {
            if (syncRuleMapping.TargetMetaverseAttribute == null)
                throw new InvalidDataException("SyncRuleMapping.TargetMetaverseAttribute must not be null.");

            ProcessMapping(cso, syncRuleMapping, objectTypes, expressionEvaluator,
                skipReferenceAttributes, onlyReferenceAttributes, isFinalReferencePass,
                cso.ConnectedSystemId);
        }
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
            // Skip pending exports that have not been exported yet
            if (pendingExport.Status == PendingExportStatus.Pending)
            {
                Log.Verbose("EvaluatePendingExportConfirmation: Skipping pending export {PeId} - not yet exported (Status=Pending).", pendingExport.Id);
                continue;
            }

            // Skip pending exports awaiting confirmation via PendingExportReconciliationService
            if (pendingExport.Status == PendingExportStatus.Exported)
            {
                Log.Verbose("EvaluatePendingExportConfirmation: Skipping pending export {PeId} - awaiting confirmation via import (Status=Exported).", pendingExport.Id);
                continue;
            }

            var successfulChanges = new List<PendingExportAttributeValueChange>();
            var failedChanges = new List<PendingExportAttributeValueChange>();

            foreach (var attributeChange in pendingExport.AttributeValueChanges)
            {
                var csoAttributeValue = cso.AttributeValues
                    .FirstOrDefault(av => av.AttributeId == attributeChange.AttributeId);

                var changeMatches = attributeChange.ChangeType switch
                {
                    PendingExportAttributeChangeType.Add or
                    PendingExportAttributeChangeType.Update =>
                        csoAttributeValue != null && AttributeValuesMatch(csoAttributeValue, attributeChange),

                    PendingExportAttributeChangeType.Remove or
                    PendingExportAttributeChangeType.RemoveAll =>
                        csoAttributeValue == null || string.IsNullOrEmpty(csoAttributeValue.StringValue),

                    _ => false
                };

                if (changeMatches)
                    successfulChanges.Add(attributeChange);
                else
                    failedChanges.Add(attributeChange);
            }

            if (failedChanges.Count == 0)
            {
                Log.Information("EvaluatePendingExportConfirmation: All changes confirmed for pending export {PeId}. Marking for deletion.", pendingExport.Id);
                toDelete.Add(pendingExport);
                pendingExportsForThisCso.Remove(pendingExport);
            }
            else if (successfulChanges.Count > 0)
            {
                Log.Information("EvaluatePendingExportConfirmation: Partial success for pending export {PeId}. " +
                    "{SuccessCount} succeeded, {FailCount} failed. Marking for update.",
                    pendingExport.Id, successfulChanges.Count, failedChanges.Count);

                foreach (var successfulChange in successfulChanges)
                    pendingExport.AttributeValueChanges.Remove(successfulChange);

                if (pendingExport.ChangeType == PendingExportChangeType.Create)
                {
                    Log.Information("EvaluatePendingExportConfirmation: Changing pending export {PeId} from Create to Update.", pendingExport.Id);
                    pendingExport.ChangeType = PendingExportChangeType.Update;
                }

                pendingExport.ErrorCount++;
                pendingExport.Status = PendingExportStatus.ExportNotConfirmed;
                toUpdate.Add(pendingExport);
            }
            else
            {
                Log.Warning("EvaluatePendingExportConfirmation: Complete failure for pending export {PeId}. " +
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
        int remainingCsoCount)
    {
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

                if (triggerIds.Contains(disconnectingSystemId))
                {
                    Log.Information("EvaluateMvoDeletionRule: Authoritative source (system ID {SystemId}) disconnected from MVO {MvoId}. " +
                        "Triggering deletion even though {Count} connector(s) remain.",
                        disconnectingSystemId, mvo.Id, remainingCsoCount);
                    return EvaluateGracePeriod(mvo, $"authoritative source (system ID {disconnectingSystemId}) disconnected");
                }

                Log.Verbose("EvaluateMvoDeletionRule: System ID {SystemId} disconnected from MVO {MvoId} but is not an authoritative source. " +
                    "Authoritative sources: [{AuthSources}]. Not marking for deletion.",
                    disconnectingSystemId, mvo.Id, string.Join(", ", triggerIds));
                return MvoDeletionDecision.NotDeleted($"System {disconnectingSystemId} is not an authoritative source");

            default:
                Log.Warning("EvaluateMvoDeletionRule: Unknown DeletionRule {Rule} for MVO {MvoId}.", mvo.Type.DeletionRule, mvo.Id);
                return MvoDeletionDecision.NotDeleted($"Unknown DeletionRule {mvo.Type.DeletionRule}");
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

    /// <inheritdoc />
    public bool AttributeValuesMatch(
        ConnectedSystemObjectAttributeValue csoValue,
        PendingExportAttributeValueChange pendingChange)
    {
        if (pendingChange.StringValue != null && csoValue.StringValue != pendingChange.StringValue)
            return false;

        if (pendingChange.IntValue.HasValue && csoValue.IntValue != pendingChange.IntValue)
            return false;

        if (pendingChange.DateTimeValue.HasValue && csoValue.DateTimeValue != pendingChange.DateTimeValue)
            return false;

        if (pendingChange.ByteValue != null && !JIM.Utilities.Utilities.AreByteArraysTheSame(csoValue.ByteValue, pendingChange.ByteValue))
            return false;

        if (pendingChange.UnresolvedReferenceValue != null && csoValue.UnresolvedReferenceValue != pendingChange.UnresolvedReferenceValue)
            return false;

        return true;
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
