// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Collections.Generic;
using System.Linq;
using JIM.Models.Activities;
using JIM.Models.Activities.DTOs;
using JIM.Models.Enums;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using JIM.Web.Causality;

namespace JIM.Web.Tests;

/// <summary>
/// Downward walks for the Lineage's After this run tests (#1811), shaped like the PRD's example: Tina Adams's import into
/// Yellowstone APAC, the Synchronisation that deleted her Metaverse Object, the deprovision of her Glitterband EMEA
/// account and the removal of her from four Groups there, each export, and each confirming import.
/// </summary>
public static class CausalityConsequenceTestData
{
    public const int Yellowstone = 1;
    public const int Glitterband = 2;
    public static readonly DateTime T0 = new(2026, 10, 1, 9, 0, 0, DateTimeKind.Utc);
    public static readonly Guid DeletedMvoId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    public static readonly Guid AccountCsoId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");


    public static CausalityPageContext ImportContext(Guid? deletedMvoId = null) => new(
        ConnectedSystemId: Yellowstone,
        ConnectedSystemName: "Yellowstone APAC",
        RunProfileName: "Delta Import",
        CsoId: Guid.NewGuid(),
        CsoConnectedSystemId: Yellowstone,
        CsoConnectedSystemName: "Yellowstone APAC",
        CsoDisplayName: "S8-99",
        CsoExternalId: "S8-99",
        CsoObjectTypeName: "person",
        MvoTypeName: "User",
        MvoTypePluralName: "Users",
        DeletedMetaverseObjectId: deletedMvoId);

    public static CausalityLineageModel BuildImportLineage(CausalConsequences? consequences, Guid? deletedMvoId = null,
        ICausalityLinks? links = null)
    {
        var item = new ActivityRunProfileExecutionItem { Id = Guid.NewGuid(), ObjectChangeType = ObjectChangeType.Updated };
        CausalityTestData.AddOutcome(item, ActivityRunProfileExecutionItemSyncOutcomeType.CsoUpdated, parent: null, ordinal: 0, detailCount: 1);
        var context = links == null ? ImportContext(deletedMvoId) : ImportContext(deletedMvoId) with { Links = links };
        var model = CausalityModelBuilder.Build(item, context);
        return CausalityLineageModelBuilder.Build(model, chain: null, ObjectChangeType.Updated, consequences);
    }

    public static CausalConsequences Consequences(CausalConsequenceCohort cohort, bool truncatedByDepth = false) => new()
    {
        Cohorts = [cohort],
        IsTruncatedByDepth = truncatedByDepth,
        CheckedAt = T0.AddDays(3)
    };

    public static CausalConsequences Consequences(List<CausalConsequenceEndingCount> endings) => new()
    {
        Endings = endings,
        CheckedAt = T0.AddDays(3)
    };

    public static CausalConsequenceEndingCount Ending(CausalConsequenceEnding ending, int? systemId = null, string? systemName = null) => new()
    {
        Ending = ending,
        MemberCount = 1,
        ConnectedSystemId = systemId,
        ConnectedSystemName = systemName
    };

    /// <summary>
    /// The Synchronisation that acted on the import: the record left scope, its Metaverse Object was deleted, and the
    /// deletion queued a deprovision (or, under a Disconnect action, left the account in place) in Glitterband EMEA.
    /// </summary>
    public static CausalConsequenceCohort SynchronisationCohort(bool disconnect = false, Guid? pendingExportId = null)
    {
        var item = Item(ConnectedSystemRunType.FullSynchronisation, Yellowstone, T0.AddHours(1), "Tina Adams", "S8-99", "person");
        var scopeExit = CausalityTestData.AddOutcome(item, ActivityRunProfileExecutionItemSyncOutcomeType.DisconnectedOutOfScope,
            parent: null, ordinal: 0, targetEntityId: DeletedMvoId, targetEntityDescription: "Tina Adams",
            syncRuleId: 7, syncRuleName: "Yellowstone APAC Inbound Users");
        item.DeletionPolicySnapshotJson = new JIM.Models.Sync.MvoDeletionPolicySnapshot
        {
            DeletionRule = JIM.Models.Core.MetaverseObjectDeletionRule.WhenAuthoritativeSourceDisconnected,
            GracePeriod = TimeSpan.Zero,
            TriggeringSystemId = Yellowstone,
            TriggeringSystemName = "Yellowstone APAC"
        }.ToJson();
        var deleted = CausalityTestData.AddOutcome(item, ActivityRunProfileExecutionItemSyncOutcomeType.MvoDeleted,
            parent: scopeExit, ordinal: 0, targetEntityId: DeletedMvoId, targetEntityDescription: "Tina Adams",
            detailMessage: "Deleted immediately: last authoritative source disconnected");
        if (disconnect)
        {
            CausalityTestData.AddOutcome(item, ActivityRunProfileExecutionItemSyncOutcomeType.TargetDisconnected,
                parent: deleted, ordinal: 0, targetEntityId: AccountCsoId, targetEntityDescription: "Glitterband EMEA",
                detailMessage: $"{Glitterband}|person", syncRuleId: 9, syncRuleName: "Glitterband EMEA Outbound Users");
        }
        else
        {
            CausalityTestData.AddOutcome(item, ActivityRunProfileExecutionItemSyncOutcomeType.DeprovisionQueued,
                parent: deleted, ordinal: 0, targetEntityId: pendingExportId ?? Guid.NewGuid(), targetEntityDescription: "Glitterband EMEA",
                detailCount: 1, detailMessage: $"{Glitterband}|person", syncRuleId: 9, syncRuleName: "Glitterband EMEA Outbound Users");
        }

        return Cohort(CausalConsequenceHopKind.NextSynchronisation, item, memberCount: 1, systemId: Yellowstone,
            systemName: "Yellowstone APAC", objectType: "person");
    }

    public static CausalConsequences CompleteCascade()
    {
        var synchronisation = SynchronisationCohort();

        var accountExport = ExportCohort(CausalReasonCode.ExportDeleteStaged, memberCount: 1, displayName: "tina.adams99", csoId: AccountCsoId);
        accountExport.Consequences.Add(ConfirmationCohort(memberCount: 1, displayName: "tina.adams99"));
        synchronisation.Consequences.Add(accountExport);

        var recall = Item(ConnectedSystemRunType.FullSynchronisation, Yellowstone, T0.AddHours(1), "Project-Catalyst", "cn=Project-Catalyst", "group");
        recall.ConnectedSystemObjectId = Guid.NewGuid();
        CausalityTestData.AddOutcome(recall, ActivityRunProfileExecutionItemSyncOutcomeType.PendingExportCreated, parent: null, ordinal: 0,
            targetEntityId: Guid.NewGuid(), targetEntityDescription: "Glitterband EMEA", detailCount: 1,
            detailMessage: $"{Glitterband}|group", stagedChangeType: PendingExportChangeType.Update);
        var removals = Cohort(CausalConsequenceHopKind.Edge, recall, memberCount: 4, systemId: Yellowstone, systemName: "Yellowstone APAC",
            edgeType: CausalEdgeType.MetaverseObjectDeletionCausedReferenceRemoval, reasonCode: CausalReasonCode.AuthoritativeSourceDisconnected,
            objectType: "group", pluralName: "Groups", attributeName: "Static Members");
        var groupExports = ExportCohort(CausalReasonCode.ExportUpdateStaged, memberCount: 4, displayName: "Project-Catalyst", objectType: "group");
        groupExports.Consequences.Add(ConfirmationCohort(memberCount: 4, displayName: "Project-Catalyst", objectType: "group"));
        removals.Consequences.Add(groupExports);
        synchronisation.Consequences.Add(removals);

        return Consequences(synchronisation);
    }

    public static CausalConsequenceCohort ExportCohort(CausalReasonCode reasonCode, int memberCount, string displayName,
        Guid? csoId = null, string objectType = "person")
    {
        var item = Item(ConnectedSystemRunType.Export, Glitterband, T0.AddHours(2), displayName, $"cn={displayName}", objectType);
        item.ConnectedSystemObjectId = csoId ?? Guid.NewGuid();
        CausalityTestData.AddOutcome(item, reasonCode == CausalReasonCode.ExportDeleteStaged
                ? ActivityRunProfileExecutionItemSyncOutcomeType.Deprovisioned
                : ActivityRunProfileExecutionItemSyncOutcomeType.Exported,
            parent: null, ordinal: 0, detailCount: 1);
        return Cohort(CausalConsequenceHopKind.Edge, item, memberCount, systemId: Glitterband, systemName: "Glitterband EMEA",
            edgeType: CausalEdgeType.PendingExportQueueingCausedExportExecution, reasonCode: reasonCode, objectType: objectType,
            pluralName: objectType == "group" ? "Groups" : null);
    }

    public static CausalConsequenceCohort ConfirmationCohort(int memberCount, string displayName, string objectType = "person")
    {
        var item = Item(ConnectedSystemRunType.DeltaImport, Glitterband, T0.AddHours(3), displayName, $"cn={displayName}", objectType);
        CausalityTestData.AddOutcome(item, ActivityRunProfileExecutionItemSyncOutcomeType.ExportConfirmed, parent: null, ordinal: 0);
        return Cohort(CausalConsequenceHopKind.Confirmation, item, memberCount, systemId: Glitterband, systemName: "Glitterband EMEA",
            edgeType: CausalEdgeType.ExportCausedImportConfirmation, objectType: objectType,
            pluralName: objectType == "group" ? "Groups" : null, ending: CausalConsequenceEnding.Confirmed);
    }

    public static CausalConsequenceCohort OutstandingCohort(PendingExportStatus status, Guid pendingExportId)
    {
        var cohort = Cohort(CausalConsequenceHopKind.OutstandingPendingExport, representative: null, memberCount: 1,
            systemId: Glitterband, systemName: "Glitterband EMEA", pendingExportStatus: status,
            ending: status is PendingExportStatus.Parked
                ? CausalConsequenceEnding.NeedsAttention
                : CausalConsequenceEnding.WaitingForExport);
        cohort.Members.Add(new CausalConsequenceMember { PendingExportId = pendingExportId, DisplayName = "Tina Adams", Occurred = T0.AddHours(1) });
        return cohort;
    }

    public static ActivityRunProfileExecutionItem Item(ConnectedSystemRunType runType, int systemId, DateTime executed,
        string displayName, string externalId, string objectType)
    {
        var activity = new Activity
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = systemId,
            ConnectedSystemRunType = runType,
            Executed = executed,
            TargetName = runType.ToString()
        };
        return new ActivityRunProfileExecutionItem
        {
            Id = Guid.NewGuid(),
            Activity = activity,
            ActivityId = activity.Id,
            DisplayNameSnapshot = displayName,
            ExternalIdSnapshot = externalId,
            ObjectTypeSnapshot = objectType
        };
    }

    public static CausalConsequenceCohort Cohort(CausalConsequenceHopKind hopKind, ActivityRunProfileExecutionItem? representative,
        int memberCount, int? systemId = null, string? systemName = null, CausalEdgeType? edgeType = null,
        CausalReasonCode reasonCode = CausalReasonCode.NotSet, string? objectType = null, string? pluralName = null,
        string? attributeName = null, PendingExportStatus? pendingExportStatus = null, DateTime? dueAt = null,
        CausalConsequenceEnding? ending = null)
    {
        var occurred = representative?.Activity.Executed ?? T0.AddHours(1);
        var members = new List<CausalConsequenceMember>();
        if (representative != null)
        {
            members.Add(new CausalConsequenceMember
            {
                RunProfileExecutionItemId = representative.Id, DisplayName = representative.DisplayNameSnapshot, Occurred = occurred
            });
            members.AddRange(Enumerable.Range(1, memberCount - 1).Select(i => new CausalConsequenceMember
            {
                RunProfileExecutionItemId = Guid.NewGuid(), DisplayName = $"{representative.DisplayNameSnapshot} {i}", Occurred = occurred
            }));
        }

        return new CausalConsequenceCohort
        {
            CohortKey = Guid.NewGuid().ToString("N"),
            HopKind = hopKind,
            EdgeType = edgeType,
            ReasonCode = reasonCode,
            ConnectedSystemId = systemId,
            ConnectedSystemName = systemName,
            ObjectTypeName = objectType,
            ObjectTypePluralName = pluralName,
            AttributeName = attributeName,
            PendingExportStatus = pendingExportStatus,
            MemberCount = memberCount,
            FirstOccurred = occurred,
            LastOccurred = occurred,
            DueAt = dueAt,
            Members = members,
            Ending = ending,
            Representative = representative
        };
    }
}
