// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Interfaces;
using JIM.Application.Services;
using JIM.Models.Activities;
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
    /// <summary>
    /// Puts Synchronisation Rules in the one order every decision that names a rule reads them in (#2823): by name,
    /// ignoring case, then by id. Rules load in no defined order, so a decision that takes "the first rule" must take it
    /// from here, or two identical runs can differ.
    /// </summary>
    public static IEnumerable<SyncRule> InNameOrder(IEnumerable<SyncRule> rules) =>
        rules.OrderBy(rule => rule.Name, StringComparer.OrdinalIgnoreCase).ThenBy(rule => rule.Id);

    /// <inheritdoc />
    public ProjectionDecision EvaluateProjection(
        ConnectedSystemObject cso,
        IReadOnlyList<SyncRule> activeSyncRules)
    {
        // Every projecting rule takes part, in name order (#2823): the object projects once, and if the rules disagree
        // on what kind of object it is, nothing is projected rather than the first rule loaded deciding.
        var projectingRules = InNameOrder(activeSyncRules.Where(sr =>
                sr.ProjectToMetaverse == true &&
                sr.ConnectedSystemObjectType?.Id == cso.TypeId &&
                sr.MetaverseObjectType != null))
            .ToList();

        if (projectingRules.Count == 0)
            return ProjectionDecision.NoProjection();

        if (projectingRules.Select(rule => rule.MetaverseObjectType!.Id).Distinct().Count() > 1)
            return ProjectionDecision.Conflict(projectingRules);

        return ProjectionDecision.Project(projectingRules[0].MetaverseObjectType!, projectingRules[0]);
    }

    /// <summary>
    /// The message a Projection Type Conflict records (#2823): each projecting rule and the Metaverse Object Type it
    /// projects into, and what to do about it.
    /// </summary>
    /// <param name="conflictingRules">The projecting rules, by name.</param>
    /// <param name="previewing">Whether a preview is describing what a synchronisation would do.</param>
    public static string DescribeProjectionTypeConflict(IReadOnlyList<SyncRule> conflictingRules, bool previewing = false)
    {
        var rules = string.Join(", ", conflictingRules.Select(rule => $"'{rule.Name}' projects it as {rule.MetaverseObjectType?.Name}"));
        var outcome = previewing ? "a synchronisation would not project it" : "it was not projected";
        return $"More than one import Synchronisation Rule would project this object, into different Metaverse Object Types: " +
               $"{rules}. JIM does not choose what kind of object it is, so {outcome}. Give the rules Scoping Criteria that " +
               "never cover the same object, or have them project into one Metaverse Object Type.";
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
        AttributePriorityContext? priorityContext = null,
        Func<ConnectedSystemObjectAttributeValue, bool>? cannotResolveThisRun = null)
    {
        var errors = new List<AttributeFlowError>();

        if (cso.MetaverseObject == null)
        {
            Log.Error("FlowInboundAttributes: CSO ({CsoId}) has no MVO!", cso.Id);
            return errors;
        }

        // A disabled mapping is skipped without an error: disabling is a deliberate choice (an administrator's,
        // or the schema refresh decision's), so nothing flows and nothing is reported against the object (#1485).
        //
        // A derived mapping (Metaverse-Derived Attribute Flows, #1750: an import expression reading mv["..."]) is
        // skipped whenever the run has a derived flow graph: it is evaluated by the derived pass
        // (EvaluateDerivedLevel), level by level, against the object's effective Metaverse values, never here with a
        // partial view (plan decision 5). Every inbound path routes through this method, so the skip also covers the
        // deferred reference-only pass and contributor re-election re-flows. Without a graph (a context built with
        // none) the mapping flows here, reading nothing from mv.
        var derivedFlowGraph = priorityContext?.DerivedFlowGraph;
        foreach (var syncRuleMapping in syncRule.AttributeFlowRules.Where(m => m.Enabled && derivedFlowGraph?.IsDerived(m) != true))
        {
            if (syncRuleMapping.TargetMetaverseAttribute == null)
                throw new InvalidDataException("SyncRuleMapping.TargetMetaverseAttribute must not be null.");

            ProcessMapping(cso, syncRuleMapping, objectTypes, expressionEvaluator,
                skipReferenceAttributes, onlyReferenceAttributes, isFinalReferencePass,
                cso.ConnectedSystemId, errors, syncRule.MetaverseObjectTypeId, priorityContext,
                cannotResolveThisRun: cannotResolveThisRun);
        }

        return errors;
    }

    /// <inheritdoc />
    public MvoDeletionDecision EvaluateMvoDeletionRule(
        MetaverseObject mvo,
        int disconnectingSystemId,
        IReadOnlyCollection<int> remainingConnectedSystemIds,
        string? disconnectingSystemName = null)
    {
        // One entry per remaining joined CSO, so the count is CSO-level; duplicates per system are
        // deliberate (a system with two joined CSOs contributes two entries).
        var remainingCsoCount = remainingConnectedSystemIds.Count;

        // Prefer the system name in human-readable reasons; fall back to the id when the caller did
        // not resolve a name (keeps the reason meaningful without a database lookup here).
        var systemLabel = string.IsNullOrWhiteSpace(disconnectingSystemName)
            ? $"system ID {disconnectingSystemId}"
            : $"'{disconnectingSystemName}'";

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
                return EvaluateGracePeriod(mvo, "last connector disconnected", CausalReasonCode.LastConnectorDisconnected);

            case MetaverseObjectDeletionRule.WhenAuthoritativeSourceDisconnected:
                var triggerIds = mvo.Type.DeletionTriggerConnectedSystemIds;
                if (triggerIds == null || triggerIds.Count == 0)
                {
                    Log.Warning("EvaluateMvoDeletionRule: MVO {MvoId} has DeletionRule=WhenAuthoritativeSourceDisconnected but no DeletionTriggerConnectedSystemIds configured. " +
                        "Falling back to WhenLastConnectorDisconnected behaviour.", mvo.Id);
                    if (remainingCsoCount == 0)
                        return EvaluateGracePeriod(mvo, "last connector disconnected (no authoritative sources configured)",
                            CausalReasonCode.LastConnectorDisconnectedNoSourcesConfigured);
                    return MvoDeletionDecision.NotDeleted($"{remainingCsoCount} remaining connector(s), no authoritative sources configured");
                }

                if (!triggerIds.Contains(disconnectingSystemId))
                {
                    // Common to both trigger modes: a system that is not a listed source never triggers deletion.
                    Log.Verbose("EvaluateMvoDeletionRule: System ID {SystemId} disconnected from MVO {MvoId} but is not an authoritative source. " +
                        "Authoritative sources: [{AuthSources}]. Not marking for deletion.",
                        disconnectingSystemId, mvo.Id, string.Join(", ", triggerIds));
                    return MvoDeletionDecision.NotDeleted($"System {systemLabel} is not an authoritative source");
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
                        $"All sources mode: authoritative source {systemLabel} disconnected and no sources remain connected",
                        CausalReasonCode.AllAuthoritativeSourcesDisconnected);
                }

                // Specific sources mode: any listed source disconnecting triggers deletion (pre-#119 behaviour).
                Log.Information("EvaluateMvoDeletionRule: Authoritative source (system ID {SystemId}) disconnected from MVO {MvoId} (Specific sources mode). " +
                    "Triggering deletion even though {Count} connector(s) remain.",
                    disconnectingSystemId, mvo.Id, remainingCsoCount);
                return EvaluateGracePeriod(mvo, $"Specific sources mode: authoritative source {systemLabel} disconnected",
                    CausalReasonCode.AuthoritativeSourceDisconnected);

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

        // Each removal takes the first occurrence of the value it names, by reference, in one pass over the values
        // rather than a scan per removal: a group whose membership is replaced stages a removal per member (#2664).
        mvo.AttributeValues.RemoveEach(mvo.PendingAttributeValueRemovals);

        foreach (var addition in mvo.PendingAttributeValueAdditions)
            mvo.AttributeValues.Add(addition);

        mvo.PendingAttributeValueRemovals.Clear();
        mvo.PendingAttributeValueAdditions.Clear();

        // Keep the denormalised CachedDisplayName in sync with the canonical attribute value.
        // This cached column enables efficient sorting at scale without correlated subqueries.
        mvo.CachedDisplayName = ObjectNaming.MetaverseNameFrom(mvo.AttributeValues);

        Log.Verbose("ApplyPendingAttributeChanges: Applied {AddCount} additions and {RemoveCount} removals to MVO {MvoId}",
            addCount, removeCount, mvo.Id);
    }

    /// <summary>
    /// Whether an export rule made the Connected System Object's join, by provisioning it or by matching it on export,
    /// rather than an import rule projecting or matching it (#2021). Synchronisation Rules are isolated: such a join is
    /// the export rule's, so only the export rule's own scope ends it, and an import rule's out-of-scope action decides
    /// only what happens to the values the import rule brought in. A row joined before the join method was recorded still
    /// carries its join type, so a provisioned one is recognised either way.
    /// </summary>
    public static bool IsJoinMadeByExportRule(ConnectedSystemObject connectedSystemObject) =>
        connectedSystemObject.JoinType == ConnectedSystemObjectJoinType.Provisioned
        || connectedSystemObject.JoinMethod is ConnectedSystemObjectJoinMethod.Provisioning or ConnectedSystemObjectJoinMethod.ExportMatching;

    /// <inheritdoc />
    public InboundOutOfScopeAction DetermineOutOfScopeAction(
        ConnectedSystemObject cso,
        IReadOnlyList<SyncRule> activeSyncRules)
    {
        // An object deleted from its Connected System has left every import rule at once (#2823), so the same decision
        // applies as for leaving the last rule's scope.
        var importRules = activeSyncRules
            .Where(sr => sr.Direction == SyncRuleDirection.Import && sr.Enabled && sr.ConnectedSystemObjectTypeId == cso.TypeId)
            .ToList();
        return DecideLastScopeExit(cso, importRules).Action;
    }

    /// <inheritdoc />
    public LastScopeExitDecision DecideLastScopeExit(ConnectedSystemObject cso, IReadOnlyList<SyncRule> lostImportRules)
    {
        ArgumentNullException.ThrowIfNull(cso);
        ArgumentNullException.ThrowIfNull(lostImportRules);

        if (lostImportRules.Count == 0)
            return new LastScopeExitDecision(InboundOutOfScopeAction.Disconnect, null);

        // The rules that held the object: those whose values its Metaverse Object holds from this Connected System, and
        // the rule that made its join. A rule that never held it has nothing to keep. Where nothing is recorded (a join
        // made before rules were recorded, by a since-deleted rule, or by the Object Type's own matching), every rule
        // counts.
        var contributingRuleIds = cso.MetaverseObject?.AttributeValues
            .Where(av => av.ContributedBySystemId == cso.ConnectedSystemId && av.ContributedBySyncRuleId.HasValue)
            .Select(av => av.ContributedBySyncRuleId!.Value)
            .ToHashSet() ?? [];
        var heldBy = lostImportRules
            .Where(rule => contributingRuleIds.Contains(rule.Id) || rule.Id == cso.JoinSyncRuleId)
            .ToList();
        var deciders = InNameOrder(heldBy.Count > 0 ? heldBy : lostImportRules).ToList();

        // Remain Joined wins: one rule that holds the object as "once managed, always managed" keeps it, whichever order
        // the rules load in.
        var decidingRule = deciders.FirstOrDefault(rule => rule.InboundOutOfScopeAction == InboundOutOfScopeAction.RemainJoined)
                           ?? deciders[0];
        return new LastScopeExitDecision(decidingRule.InboundOutOfScopeAction, decidingRule);
    }

    /// <summary>
    /// Evaluates the grace period for an MVO deletion decision.
    /// </summary>
    private static MvoDeletionDecision EvaluateGracePeriod(MetaverseObject mvo, string reason, CausalReasonCode reasonCode)
    {
        var gracePeriod = mvo.Type!.DeletionGracePeriod;

        if (!gracePeriod.HasValue || gracePeriod.Value == TimeSpan.Zero)
        {
            Log.Information("EvaluateMvoDeletionRule: MVO {MvoId} queued for immediate deletion ({Reason}). No grace period configured.",
                mvo.Id, reason);
            return MvoDeletionDecision.DeleteImmediately(reason, reasonCode);
        }

        Log.Information("EvaluateMvoDeletionRule: MVO {MvoId} marked for deletion ({Reason}). Eligible after {GracePeriod}.",
            mvo.Id, reason, gracePeriod.Value);
        return MvoDeletionDecision.ScheduleDeletion(gracePeriod.Value, reason, reasonCode);
    }
}
