// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Models.Sync;
using JIM.Models.Transactional;

namespace JIM.Application.Servers;

/// <summary>
/// Outbound (export) evaluation decisions, extracted from <see cref="ExportEvaluationServer"/> as part of the
/// #288 unbraiding: the server keeps orchestration, data access and the apply step; the verdicts live here,
/// pure, so the real run and a preview call one implementation of the semantics.
/// </summary>
public partial class SyncEngine
{
    /// <summary>
    /// Decides whether deleting a Metaverse Object stages a Delete export for one of its joined CSOs, per the
    /// #655 semantics: the matching export Synchronisation Rules' OutboundDeprovisionAction drives the verdict,
    /// Delete wins a conflict, and the one-Pending-Export-per-CSO collision policy chooses reuse, replace or
    /// create. The disconnect itself is unconditional and is the orchestrator's to apply; this decision covers
    /// only the export half.
    /// </summary>
    /// <param name="cso">The joined CSO, carrying its attribute values so the secondary external identifier can
    /// be captured at decision time (the CSO may be gone before the export runs).</param>
    /// <param name="metaverseObjectTypeId">The deleted Metaverse Object's type id, or null when the object
    /// carries no type and no rule can be matched.</param>
    /// <param name="exportRulesByMetaverseObjectTypeId">Enabled export Synchronisation Rules grouped by
    /// Metaverse Object Type id, as the orchestrator's cache holds them.</param>
    /// <param name="existingPendingExport">The Pending Export already attached to the CSO, if any; the caller
    /// resolves this from its batched pre-read or the run's working set, never from a per-object query.</param>
    public MvoDeletionExportDecision DecideMvoDeletionExport(
        ConnectedSystemObject cso,
        int? metaverseObjectTypeId,
        IReadOnlyDictionary<int, List<SyncRule>> exportRulesByMetaverseObjectTypeId,
        PendingExport? existingPendingExport)
    {
        if (metaverseObjectTypeId == null)
            return MvoDeletionExportDecision.DisconnectOnly(MvoDeletionExportReason.NoMetaverseObjectType);

        if (!exportRulesByMetaverseObjectTypeId.TryGetValue(metaverseObjectTypeId.Value, out var typeExportRules))
            return MvoDeletionExportDecision.DisconnectOnly(MvoDeletionExportReason.NoMatchingExportRule);

        // A rule matches on the full (Connected System, Connected System Object Type) pair; matching on the
        // system alone would let a rule for one object type deprovision another's objects.
        var matchingRules = typeExportRules
            .Where(r => r.ConnectedSystemId == cso.ConnectedSystemId && r.ConnectedSystemObjectTypeId == cso.TypeId)
            .ToList();

        if (matchingRules.Count == 0)
            return MvoDeletionExportDecision.DisconnectOnly(MvoDeletionExportReason.NoMatchingExportRule);

        var deleteRule = matchingRules.Find(r => r.OutboundDeprovisionAction == OutboundDeprovisionAction.Delete);
        if (deleteRule == null)
            return MvoDeletionExportDecision.DisconnectOnly(
                MvoDeletionExportReason.MatchingRulesDeclineDeletion, matchingRules.Count);

        // Captured now because the CSO is disconnected immediately after this decision and may be deleted by
        // housekeeping before the export runs; connectors like LDAP need the identifier preserved to delete the
        // right object. A CSO with none still stages: refusing would leave the object undeleted silently.
        var secondaryIdAttributeValue = cso.SecondaryExternalIdAttributeValue;
        var hasSecondaryId = secondaryIdAttributeValue?.Attribute != null &&
                             !string.IsNullOrEmpty(secondaryIdAttributeValue.StringValue);

        var reuseExisting = existingPendingExport?.ChangeType == PendingExportChangeType.Delete;

        return new MvoDeletionExportDecision
        {
            ShouldStageDeleteExport = true,
            Reason = MvoDeletionExportReason.DeleteRuleWon,
            WinningRule = deleteRule,
            MatchingRuleCount = matchingRules.Count,
            RulesConflicted = matchingRules.Count > 1 &&
                              matchingRules.Exists(r => r.OutboundDeprovisionAction != OutboundDeprovisionAction.Delete),
            ExistingPendingExportToReuse = reuseExisting ? existingPendingExport : null,
            MustReplaceExistingPendingExport = existingPendingExport != null && !reuseExisting,
            SecondaryExternalIdValue = hasSecondaryId ? secondaryIdAttributeValue!.StringValue : null,
            SecondaryExternalIdAttribute = hasSecondaryId ? secondaryIdAttributeValue!.Attribute : null
        };
    }

    /// <summary>
    /// Decides what an export Synchronisation Rule's OutboundDeprovisionAction means for a CSO that has fallen
    /// out of the rule's scope: disconnect, stage a Delete export (with the one-Pending-Export-per-CSO collision
    /// policy choosing reuse, replace or create), or nothing at all for an action this engine does not recognise.
    /// An unrecognised action is deliberately not defaulted to disconnect: deprovisioning semantics are never
    /// guessed at, and the orchestrator surfaces the non-action as a warning.
    /// </summary>
    /// <param name="exportRule">The export Synchronisation Rule the CSO fell out of scope for.</param>
    /// <param name="existingPendingExport">The Pending Export already attached to the CSO, if any; the caller
    /// resolves this from the run's working set or the database.</param>
    public OutOfScopeDeprovisioningDecision DecideOutOfScopeDeprovisioning(
        SyncRule exportRule,
        PendingExport? existingPendingExport)
    {
        switch (exportRule.OutboundDeprovisionAction)
        {
            case OutboundDeprovisionAction.Disconnect:
                return new OutOfScopeDeprovisioningDecision { Action = OutOfScopeDeprovisioningAction.Disconnect };

            case OutboundDeprovisionAction.Delete:
                var reuseExisting = existingPendingExport?.ChangeType == PendingExportChangeType.Delete;
                return new OutOfScopeDeprovisioningDecision
                {
                    Action = OutOfScopeDeprovisioningAction.StageDeleteExport,
                    ExistingPendingExportToReuse = reuseExisting ? existingPendingExport : null,
                    MustReplaceExistingPendingExport = existingPendingExport != null && !reuseExisting
                };

            default:
                return new OutOfScopeDeprovisioningDecision { Action = OutOfScopeDeprovisioningAction.UnknownAction };
        }
    }

    /// <summary>
    /// Decides whether a disconnect that removed a Metaverse Object's last connector should stamp
    /// LastConnectorDisconnectedDate, starting the deletion grace period. Ask AFTER removing the disconnected
    /// CSO from the object's collection: no connectors remaining is the collection being empty. Only a Projected
    /// object whose Type's Deletion Rule is WhenLastConnectorDisconnected qualifies; an Internal object, or one
    /// with no Type loaded, is never marked.
    /// </summary>
    /// <param name="mvo">The Metaverse Object the CSO was just disconnected from.</param>
    public bool ShouldMarkLastConnectorDisconnected(MetaverseObject mvo) =>
        mvo.ConnectedSystemObjects.Count == 0 &&
        mvo.Origin == MetaverseObjectOrigin.Projected &&
        mvo.Type?.DeletionRule == MetaverseObjectDeletionRule.WhenLastConnectorDisconnected;
}
