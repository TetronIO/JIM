// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Staging;
using JIM.Web.Causality;

namespace JIM.Web.Tests;

/// <summary>
/// Shared builders for causality test scenarios. Constructs Run Profile Execution Items with
/// outcome trees shaped like the three PRD scenarios (new joiner, leaver, export failure) plus
/// the edge cases (no change, legacy null attribution, empty outcomes).
/// </summary>
public static class CausalityTestData
{
    public static readonly Guid MvoId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    public static readonly Guid CsoId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    public static readonly Guid ProvisionedCsoId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    public static readonly Guid PendingExportId = Guid.Parse("44444444-4444-4444-4444-444444444444");

    public static CausalityPageContext NewJoinerContext() => new(
        ConnectedSystemId: 1,
        ConnectedSystemName: "Yellowstone APAC",
        RunProfileName: "Full Synchronisation",
        CsoId: CsoId,
        CsoConnectedSystemId: 1,
        CsoConnectedSystemName: "Yellowstone APAC",
        CsoDisplayName: "Liam Allen",
        CsoExternalId: "S8-287551",
        CsoObjectTypeName: "person",
        MvoTypeName: "Person",
        MvoTypePluralName: "People");

    public static CausalityPageContext ExportContext() => new(
        ConnectedSystemId: 2,
        ConnectedSystemName: "Glitterband EMEA",
        RunProfileName: "Export",
        CsoId: CsoId,
        CsoConnectedSystemId: 2,
        CsoConnectedSystemName: "Glitterband EMEA",
        CsoDisplayName: "Liam Allen",
        CsoExternalId: "S8-287551",
        CsoObjectTypeName: "person",
        MvoTypeName: "Person",
        MvoTypePluralName: "People");

    /// <summary>
    /// A cross-system cascade: the Run Profile executed against Yellowstone APAC, but this
    /// particular Run Profile Execution Item's record lives on Glitterband EMEA (e.g. a Full
    /// Synchronisation on Yellowstone APAC provisioned or exported to a Connected System Object on
    /// Glitterband EMEA). <see cref="CausalityPageContext.ConnectedSystemId"/> and
    /// <see cref="CausalityPageContext.CsoConnectedSystemId"/> are deliberately different systems, so
    /// tests built on this context prove that run-scoped and record-scoped consumers each pick up
    /// their own identity rather than sharing one.
    /// </summary>
    public static CausalityPageContext CascadeContext() => new(
        ConnectedSystemId: 1,
        ConnectedSystemName: "Yellowstone APAC",
        RunProfileName: "Full Synchronisation",
        CsoId: CsoId,
        CsoConnectedSystemId: 2,
        CsoConnectedSystemName: "Glitterband EMEA",
        CsoDisplayName: "Liam Allen",
        CsoExternalId: "S8-287551",
        CsoObjectTypeName: "person",
        MvoTypeName: "Person",
        MvoTypePluralName: "People");

    public static CausalityPageContext EmptyContext() => new(
        ConnectedSystemId: null,
        ConnectedSystemName: null,
        RunProfileName: null,
        CsoId: null,
        CsoConnectedSystemId: null,
        CsoConnectedSystemName: null,
        CsoDisplayName: null,
        CsoExternalId: null,
        CsoObjectTypeName: null,
        MvoTypeName: null,
        MvoTypePluralName: null);

    /// <summary>
    /// Scenario 1 (PRD): a Full Synchronisation processes a new record; the Identity is created,
    /// 11 attributes flow, a CSO is provisioned to Glitterband EMEA and an export of 11 changes
    /// is queued for it.
    /// </summary>
    public static ActivityRunProfileExecutionItem NewJoinerItem()
    {
        var item = new ActivityRunProfileExecutionItem { Id = Guid.NewGuid() };

        var projected = AddOutcome(item, ActivityRunProfileExecutionItemSyncOutcomeType.Projected,
            parent: null, ordinal: 0, targetEntityId: MvoId, targetEntityDescription: "Liam Allen",
            syncRuleId: 5, syncRuleName: "Yellowstone People - Inbound");

        var attributeFlow = AddOutcome(item, ActivityRunProfileExecutionItemSyncOutcomeType.AttributeFlow,
            parent: projected, ordinal: 0, detailCount: 11);

        var provisioned = AddOutcome(item, ActivityRunProfileExecutionItemSyncOutcomeType.Provisioned,
            parent: attributeFlow, ordinal: 0, targetEntityId: ProvisionedCsoId,
            targetEntityDescription: "Glitterband EMEA", detailMessage: "2|person",
            syncRuleId: 9, syncRuleName: "Glitterband People - Outbound");

        var pendingExport = AddOutcome(item, ActivityRunProfileExecutionItemSyncOutcomeType.PendingExportCreated,
            parent: provisioned, ordinal: 0, targetEntityId: PendingExportId,
            targetEntityDescription: "Glitterband EMEA", detailCount: 11, detailMessage: "2");
        pendingExport.ConnectedSystemObjectChange = BuildCsoChangeSnapshot();

        return item;
    }

    /// <summary>
    /// Scenario 2 (PRD): a record leaves the scope of its Synchronisation Rule; the Identity's last
    /// authoritative source disconnects and the Identity is deleted, with deprovisioning queued for
    /// two downstream systems.
    /// </summary>
    public static ActivityRunProfileExecutionItem LeaverItem()
    {
        var item = new ActivityRunProfileExecutionItem { Id = Guid.NewGuid() };

        var outOfScope = AddOutcome(item, ActivityRunProfileExecutionItemSyncOutcomeType.DisconnectedOutOfScope,
            parent: null, ordinal: 0, targetEntityId: MvoId, targetEntityDescription: "Erin Byrne",
            detailCount: 4, syncRuleId: 7, syncRuleName: "Yellowstone People - Inbound");

        AddOutcome(item, ActivityRunProfileExecutionItemSyncOutcomeType.AttributeFlow,
            parent: outOfScope, ordinal: 0, detailCount: 4);

        var mvoDeleted = AddOutcome(item, ActivityRunProfileExecutionItemSyncOutcomeType.MvoDeleted,
            parent: outOfScope, ordinal: 1, targetEntityId: MvoId, targetEntityDescription: "Erin Byrne",
            detailMessage: "Deleted immediately: last authoritative source disconnected");

        // The deprovisioning Pending Exports hang off MvoDeleted, not off the disconnection: the deletion is what
        // queues them, and #1044 made the worker emit them nested this way (both from the sync path and from the
        // Metaverse Object Housekeeping batch). They were siblings here until that landed, which no longer models
        // any shape production can produce.
        // DeprovisionQueued, not PendingExportCreated: these stage Pending Exports whose change type is Delete.
        // Their single attribute value change is the target's secondary external ID (the DN), carried so the
        // connector can still find the entry after the Connected System Object is disconnected; it is how the
        // target is identified, not an attribute being written.
        var glitterbandDeprovision = AddOutcome(item, ActivityRunProfileExecutionItemSyncOutcomeType.DeprovisionQueued,
            parent: mvoDeleted, ordinal: 0, targetEntityId: Guid.NewGuid(),
            targetEntityDescription: "Glitterband EMEA", detailCount: 1, detailMessage: "2");
        glitterbandDeprovision.ConnectedSystemObjectChange = BuildDeprovisionTargetSnapshot();

        AddOutcome(item, ActivityRunProfileExecutionItemSyncOutcomeType.DeprovisionQueued,
            parent: mvoDeleted, ordinal: 1, targetEntityId: Guid.NewGuid(),
            targetEntityDescription: "Contoso AD", detailCount: 1, detailMessage: "3");

        return item;
    }

    /// <summary>
    /// Scenario 3 (PRD): an Export run's write is rejected by the Connected System; the attempted
    /// export carries a failed child event with the connector error verbatim.
    /// </summary>
    public static ActivityRunProfileExecutionItem ExportFailureItem()
    {
        var item = new ActivityRunProfileExecutionItem { Id = Guid.NewGuid() };

        var exported = AddOutcome(item, ActivityRunProfileExecutionItemSyncOutcomeType.Exported,
            parent: null, ordinal: 0, detailCount: 3);

        AddOutcome(item, ActivityRunProfileExecutionItemSyncOutcomeType.ExportFailed,
            parent: exported, ordinal: 0, detailCount: 3,
            detailMessage: "LDAP error 50: insufficient access rights");

        return item;
    }

    /// <summary>
    /// Adds an outcome to the item's flat SyncOutcomes list, mirroring how EF materialises the
    /// tree (flat list with ParentSyncOutcomeId set; Children navigation populated by fixup).
    /// </summary>
    public static ActivityRunProfileExecutionItemSyncOutcome AddOutcome(
        ActivityRunProfileExecutionItem item,
        ActivityRunProfileExecutionItemSyncOutcomeType outcomeType,
        ActivityRunProfileExecutionItemSyncOutcome? parent,
        int ordinal,
        Guid? targetEntityId = null,
        string? targetEntityDescription = null,
        int? detailCount = null,
        string? detailMessage = null,
        int? syncRuleId = null,
        string? syncRuleName = null)
    {
        var outcome = new ActivityRunProfileExecutionItemSyncOutcome
        {
            Id = Guid.NewGuid(),
            ActivityRunProfileExecutionItemId = item.Id,
            ParentSyncOutcomeId = parent?.Id,
            OutcomeType = outcomeType,
            TargetEntityId = targetEntityId,
            TargetEntityDescription = targetEntityDescription,
            DetailCount = detailCount,
            DetailMessage = detailMessage,
            SyncRuleId = syncRuleId,
            SyncRuleName = syncRuleName,
            Ordinal = ordinal
        };
        parent?.Children.Add(outcome);
        item.SyncOutcomes.Add(outcome);
        return outcome;
    }

    /// <summary>
    /// Builds a CSO change snapshot with three single-valued Text attribute additions, as persisted
    /// for PendingExportCreated outcomes.
    /// </summary>
    public static ConnectedSystemObjectChange BuildCsoChangeSnapshot()
    {
        var change = new ConnectedSystemObjectChange { Id = Guid.NewGuid() };
        foreach (var (name, value) in new[] { ("displayName", "Liam Allen"), ("mail", "liam.allen@example.com"), ("title", "Analyst") })
        {
            var attribute = new ConnectedSystemObjectChangeAttribute
            {
                Id = Guid.NewGuid(),
                AttributeName = name,
                AttributeType = AttributeDataType.Text,
                Attribute = new ConnectedSystemObjectTypeAttribute { Name = name, AttributePlurality = AttributePlurality.SingleValued }
            };
            attribute.ValueChanges.Add(new ConnectedSystemObjectChangeAttributeValue
            {
                ValueChangeType = ValueChangeType.Add,
                StringValue = value
            });
            change.AttributeChanges.Add(attribute);
        }

        return change;
    }

    /// <summary>
    /// The snapshot a delete Pending Export carries: the target's secondary external ID, staged so the
    /// connector can still resolve the entry once the Connected System Object is disconnected. One row,
    /// and it identifies the object rather than changing it.
    /// </summary>
    public static ConnectedSystemObjectChange BuildDeprovisionTargetSnapshot()
    {
        var change = new ConnectedSystemObjectChange { Id = Guid.NewGuid() };
        var attribute = new ConnectedSystemObjectChangeAttribute
        {
            Id = Guid.NewGuid(),
            AttributeName = "distinguishedName",
            AttributeType = AttributeDataType.Text,
            Attribute = new ConnectedSystemObjectTypeAttribute
            {
                Name = "distinguishedName", AttributePlurality = AttributePlurality.SingleValued
            }
        };
        attribute.ValueChanges.Add(new ConnectedSystemObjectChangeAttributeValue
        {
            ValueChangeType = ValueChangeType.Add,
            StringValue = "uid=erin.byrne,ou=People,dc=glitterband,dc=local"
        });
        change.AttributeChanges.Add(attribute);

        return change;
    }
}
