// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Collections.Generic;
using System.Linq;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Staging;
using JIM.Models.Sync;
using JIM.Models.Transactional;
using JIM.Web.Causality;
using NUnit.Framework;
using JimUtilities = JIM.Utilities.Utilities;

namespace JIM.Web.Tests;

/// <summary>
/// Tests for <see cref="CausalityTableModelBuilder.Build"/>: the Table view's projection of a
/// <see cref="CausalityModel"/> (#1519 Phase 3, D-S8), for a recorded Run Profile Execution Item and
/// for a speculative Sync Preview alike.
/// </summary>
[TestFixture]
public class CausalityTableModelBuilderTests
{
    [Test]
    public void Build_RecordedModel_UsesBeforeAfterHeadings()
    {
        var model = CausalityModelBuilder.Build(CausalityTestData.NewJoinerItem(), CausalityTestData.NewJoinerContext());

        var table = CausalityTableModelBuilder.Build(model);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(table.IsSpeculative, Is.False);
            Assert.That(table.CurrentHeading, Is.EqualTo("Before"));
            Assert.That(table.NextHeading, Is.EqualTo("After"));
        }
    }

    [Test]
    public void Build_NewJoinerItem_ProjectsJoinExportAndAttributeRowsUnderTheRightObjects()
    {
        var model = CausalityModelBuilder.Build(CausalityTestData.NewJoinerItem(), CausalityTestData.NewJoinerContext());

        var table = CausalityTableModelBuilder.Build(model);

        using (Assert.EnterMultipleScope())
        {
            var identityRow = table.Rows.Single(r => r.ChangeKind == CausalityTableChangeKind.Projection);
            Assert.That(identityRow.ObjectKey, Is.EqualTo("identity"));
            // Before/After are for attribute values only (#1519 Table view fix 7); the Outcome column's
            // "Identity created" label already says what happened.
            Assert.That(identityRow.WouldBe, Is.Null);

            var provisionRow = table.Rows.Single(r => r.ChangeKind == CausalityTableChangeKind.Provision);
            Assert.That(provisionRow.ObjectKey, Does.StartWith("ds:"));
            Assert.That(provisionRow.Attribute, Is.Null, "a Provision row is an object-level fact, not an attribute change");

            var exportRow = table.Rows.Single(r => r.ChangeKind == CausalityTableChangeKind.ExportQueued);
            Assert.That(exportRow.ObjectKey, Is.EqualTo(provisionRow.ObjectKey),
                "the provisioned account and its queued export target the same downstream object");

            var attributeRows = table.Rows.Where(r => r.ChangeKind == CausalityTableChangeKind.AttributeChange).ToList();
            Assert.That(attributeRows, Has.Count.EqualTo(3), "the export's CSO change snapshot carries 3 attributes");
            Assert.That(attributeRows.Select(r => r.ObjectKey), Has.All.EqualTo(provisionRow.ObjectKey));
            Assert.That(attributeRows.Select(r => r.Attribute), Is.EquivalentTo(new[] { "displayName", "mail", "title" }));
        }
    }

    [Test]
    public void Build_JoinedOutcome_IsAJoinRowNotAProjection()
    {
        var item = new ActivityRunProfileExecutionItem { Id = Guid.NewGuid() };
        CausalityTestData.AddOutcome(item, ActivityRunProfileExecutionItemSyncOutcomeType.Joined,
            parent: null, ordinal: 0, targetEntityId: CausalityTestData.MvoId, targetEntityDescription: "Liam Allen",
            syncRuleId: 5, syncRuleName: "Yellowstone People - Inbound");

        var model = CausalityModelBuilder.Build(item, CausalityTestData.NewJoinerContext());
        var table = CausalityTableModelBuilder.Build(model);

        var joinRow = table.Rows.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(joinRow.ChangeKind, Is.EqualTo(CausalityTableChangeKind.Join));
            // Before/After are for attribute values only (#1519 Table view fix 7); the Outcome column's
            // "Joined to Identity" label already says what happened.
            Assert.That(joinRow.WouldBe, Is.Null);
        }
    }

    [Test]
    public void Build_MvoDeletionCancelledOutcome_IsAJoinRow()
    {
        var item = new ActivityRunProfileExecutionItem { Id = Guid.NewGuid() };
        CausalityTestData.AddOutcome(item, ActivityRunProfileExecutionItemSyncOutcomeType.MvoDeletionCancelled,
            parent: null, ordinal: 0, targetEntityId: CausalityTestData.MvoId, targetEntityDescription: "Liam Allen",
            syncRuleId: 5, syncRuleName: "Yellowstone People - Inbound");

        var model = CausalityModelBuilder.Build(item, CausalityTestData.NewJoinerContext());
        var table = CausalityTableModelBuilder.Build(model);

        var joinRow = table.Rows.Single();
        Assert.That(joinRow.ChangeKind, Is.EqualTo(CausalityTableChangeKind.Join),
            "a rejoin that cancels a scheduled deletion is a Join, since the Identity already existed");
    }

    /// <summary>
    /// Before/After are for attribute values only (#1519 Table view fix 7); a scheduled deletion's grace
    /// reasoning is the one thing lost by going silent on them, so it survives as an OutcomeDetail line
    /// instead, under the Outcome column's "Identity deletion scheduled" label.
    /// </summary>
    [Test]
    public void Build_MvoDeletionScheduledOutcome_LeavesBeforeAfterNullAndCarriesTheGraceReasoningAsOutcomeDetail()
    {
        var item = new ActivityRunProfileExecutionItem { Id = Guid.NewGuid() };
        const string graceReasoning = "Deletion Rule: last connector disconnected. Grace period: 7 days. Eligible for deletion: 10 Aug 2026 08:00:57 UTC";
        CausalityTestData.AddOutcome(item, ActivityRunProfileExecutionItemSyncOutcomeType.MvoDeletionScheduled,
            parent: null, ordinal: 0, targetEntityId: CausalityTestData.MvoId, targetEntityDescription: "Liam Allen",
            detailMessage: graceReasoning);

        var model = CausalityModelBuilder.Build(item, CausalityTestData.NewJoinerContext());
        var table = CausalityTableModelBuilder.Build(model);

        var row = table.Rows.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(row.ChangeKind, Is.EqualTo(CausalityTableChangeKind.Delete));
            Assert.That(row.Current, Is.Null);
            Assert.That(row.WouldBe, Is.Null);
            Assert.That(row.OutcomeDetail, Is.EqualTo(graceReasoning));
        }
    }

    /// <summary>
    /// Every other non-attribute row (nothing left to say beyond its Outcome label) carries no
    /// OutcomeDetail at all.
    /// </summary>
    [Test]
    public void Build_ObjectLevelRowsOtherThanMvoDeletionScheduled_CarryNoOutcomeDetail()
    {
        var model = CausalityModelBuilder.Build(CausalityTestData.NewJoinerItem(), CausalityTestData.NewJoinerContext());

        var table = CausalityTableModelBuilder.Build(model);

        var objectLevelRows = table.Rows.Where(r => r.ChangeKind != CausalityTableChangeKind.AttributeChange).ToList();
        Assert.That(objectLevelRows, Has.All.Matches<CausalityTableRow>(r => r.OutcomeDetail == null));
    }

    [Test]
    public void Build_ObjectLevelRowsSortBeforeAttributeRows()
    {
        var model = CausalityModelBuilder.Build(CausalityTestData.NewJoinerItem(), CausalityTestData.NewJoinerContext());

        var table = CausalityTableModelBuilder.Build(model);

        var lastObjectLevelIndex = table.Rows
            .Select((row, index) => (row, index))
            .Last(t => t.row.ChangeKind != CausalityTableChangeKind.AttributeChange).index;
        var firstAttributeIndex = table.Rows
            .Select((row, index) => (row, index))
            .First(t => t.row.ChangeKind == CausalityTableChangeKind.AttributeChange).index;

        Assert.That(lastObjectLevelIndex, Is.LessThan(firstAttributeIndex));
    }

    [Test]
    public void Build_LeaverItem_GroupsEachDeprovisioningTargetAsItsOwnDownstreamObject()
    {
        var model = CausalityModelBuilder.Build(CausalityTestData.LeaverItem(), CausalityTestData.NewJoinerContext());

        var table = CausalityTableModelBuilder.Build(model);

        var downstreamObjects = table.Objects.Where(o => o.Role == CausalityTableObjectRole.Downstream).ToList();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(downstreamObjects, Has.Count.EqualTo(2));
            // A DeprovisionQueued outcome never carries the target's own Connected System Object identity
            // (see CausalityModelBuilder.BuildLinks), so the entry stays on its placeholder name; the
            // Connected System's own name lives in the Subtitle instead (#1519 Table view, D-S8 follow-up).
            Assert.That(downstreamObjects.Select(o => o.DisplayName),
                Is.EquivalentTo(new[] { "New object in Glitterband EMEA", "New object in Contoso AD" }));
            Assert.That(downstreamObjects.Select(o => o.Subtitle),
                Is.EquivalentTo(new[] { "Glitterband EMEA", "Contoso AD" }));
            Assert.That(downstreamObjects.Select(o => o.Href), Has.All.Null);
            // Glitterband's deprovision carries the target's distinguishedName as a recalled attribute
            // row (the connector's resolution key); Contoso's fixture has none, so it stays at 1.
            var contoso = downstreamObjects.Single(o => o.Subtitle == "Contoso AD");
            var glitterband = downstreamObjects.Single(o => o.Subtitle == "Glitterband EMEA");
            Assert.That(contoso.RowCount, Is.EqualTo(1));
            Assert.That(glitterband.RowCount, Is.EqualTo(2));

            var deleteRow = table.Rows.Single(r => r.ChangeKind == CausalityTableChangeKind.Delete);
            Assert.That(deleteRow.ObjectKey, Is.EqualTo("identity"));
            // Before/After are for attribute values only (#1519 Table view fix 7); the Outcome column's
            // "Identity deleted" label already says what happened.
            Assert.That(deleteRow.WouldBe, Is.Null);
            Assert.That(deleteRow.Via, Is.EqualTo("Deleted immediately: last authoritative source disconnected"));
            Assert.That(deleteRow.SyncRuleId, Is.Null, "reasoning text names no Synchronisation Rule to link");
        }
    }

    [Test]
    public void Build_EverythingObject_CoversEveryRowAndTakesTheStrongestTone()
    {
        var model = CausalityModelBuilder.Build(CausalityTestData.LeaverItem(), CausalityTestData.NewJoinerContext());

        var table = CausalityTableModelBuilder.Build(model);

        var everything = table.Objects.First();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(everything.Role, Is.EqualTo(CausalityTableObjectRole.Everything));
            Assert.That(everything.RowCount, Is.EqualTo(table.Rows.Count));
            // MvoDeleted is Error-toned; DisconnectedOutOfScope alone is Warning-toned. The roll-up
            // must pick up the stronger tone even though it comes from a different event.
            Assert.That(everything.Tone, Is.EqualTo(CausalityTone.Error));
        }
    }

    [Test]
    public void Build_ObjectWithNoRows_IsStillListedWithZeroCount()
    {
        // The New Joiner story touches no record-side outcome (CsoAdded etc.), so the object being
        // synchronised carries no rows; it must still appear, since it is always the item's own record.
        var model = CausalityModelBuilder.Build(CausalityTestData.NewJoinerItem(), CausalityTestData.NewJoinerContext());

        var table = CausalityTableModelBuilder.Build(model);

        var source = table.Objects.Single(o => o.Role == CausalityTableObjectRole.Source);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(source.RowCount, Is.EqualTo(0));
            Assert.That(source.Tone, Is.EqualTo(CausalityTone.Secondary));
        }
    }

    // ─── Filter semantics ───

    [Test]
    public void Matches_ScopeAndJoinFilter_MatchesOnlyScopeProjectionAndJoinRows()
    {
        var scopeRow = new CausalityTableRow("identity", CausalityTableChangeKind.Scope, "Import scope", "In scope", "Out of scope", null, null, "l", CausalityTone.Warning);
        var projectionRow = new CausalityTableRow("identity", CausalityTableChangeKind.Projection, "Metaverse Object", null, "New Identity", null, null, "l", CausalityTone.Primary);
        var joinRow = new CausalityTableRow("identity", CausalityTableChangeKind.Join, "Metaverse Object", null, "Joined to existing Identity", null, null, "l", CausalityTone.Primary);
        var deleteRow = new CausalityTableRow("identity", CausalityTableChangeKind.Delete, "Metaverse Object", "Active", "Deleted", null, null, "l", CausalityTone.Error);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(CausalityTableFilters.Matches(scopeRow, CausalityTableFilter.ScopeAndJoin), Is.True);
            Assert.That(CausalityTableFilters.Matches(projectionRow, CausalityTableFilter.ScopeAndJoin), Is.True);
            Assert.That(CausalityTableFilters.Matches(joinRow, CausalityTableFilter.ScopeAndJoin), Is.True);
            Assert.That(CausalityTableFilters.Matches(deleteRow, CausalityTableFilter.ScopeAndJoin), Is.False);
        }
    }

    [Test]
    public void Matches_DestructiveFilter_MatchesDeleteDeprovisionDisconnectAndNoContributor()
    {
        var kinds = new[]
        {
            CausalityTableChangeKind.Delete, CausalityTableChangeKind.Deprovision,
            CausalityTableChangeKind.Disconnect, CausalityTableChangeKind.NoContributor
        };

        foreach (var kind in kinds)
        {
            var row = new CausalityTableRow("identity", kind, "Metaverse Object", "a", "b", null, null, "l", CausalityTone.Error);
            Assert.That(CausalityTableFilters.Matches(row, CausalityTableFilter.Destructive), Is.True, $"{kind} must be destructive");
        }

        var provisionRow = new CausalityTableRow("ds:2", CausalityTableChangeKind.Provision, "connector: X", null, "Account provisioned", null, null, "l", CausalityTone.Primary);
        Assert.That(CausalityTableFilters.Matches(provisionRow, CausalityTableFilter.Destructive), Is.False);
    }

    [Test]
    public void Matches_AttributeAndObjectChangeFilters_ArePartitionsOfEachOther()
    {
        var attributeRow = new CausalityTableRow("identity", CausalityTableChangeKind.AttributeChange, "mail", "old", "new", null, null, "l", CausalityTone.Info);
        var objectRow = new CausalityTableRow("identity", CausalityTableChangeKind.Delete, "Metaverse Object", "Active", "Deleted", null, null, "l", CausalityTone.Error);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(CausalityTableFilters.Matches(attributeRow, CausalityTableFilter.AttributeChanges), Is.True);
            Assert.That(CausalityTableFilters.Matches(attributeRow, CausalityTableFilter.ObjectChanges), Is.False);
            Assert.That(CausalityTableFilters.Matches(objectRow, CausalityTableFilter.AttributeChanges), Is.False);
            Assert.That(CausalityTableFilters.Matches(objectRow, CausalityTableFilter.ObjectChanges), Is.True);
        }
    }

    [Test]
    public void Matches_AllFilter_MatchesEveryRow()
    {
        var row = new CausalityTableRow("identity", CausalityTableChangeKind.ValuesPreserved, "department", "x", "x", null, null, "l", CausalityTone.Warning);

        Assert.That(CausalityTableFilters.Matches(row, CausalityTableFilter.All), Is.True);
    }

    // ─── Speculative (Sync Preview) projection ───

    private static CausalityPageContext PreviewContext() => new(
        ConnectedSystemId: 1,
        ConnectedSystemName: "Yellowstone APAC",
        RunProfileName: "Full Synchronisation",
        CsoId: Guid.NewGuid(),
        CsoConnectedSystemId: 1,
        CsoConnectedSystemName: "Yellowstone APAC",
        CsoDisplayName: "Liam Allen",
        CsoExternalId: "S8-287551",
        CsoObjectTypeName: "person",
        MvoTypeName: "Person",
        MvoTypePluralName: "People");

    [Test]
    public void Build_SpeculativeModel_UsesCurrentWouldBeHeadings()
    {
        var preview = new SyncPreviewResult
        {
            OutcomeTree = [new SyncOutcomeNode { OutcomeType = ActivityRunProfileExecutionItemSyncOutcomeType.Projected }]
        };
        var model = CausalityModelBuilder.BuildSpeculative(preview, PreviewContext());

        var table = CausalityTableModelBuilder.Build(model);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(table.IsSpeculative, Is.True);
            Assert.That(table.CurrentHeading, Is.EqualTo("Current"));
            Assert.That(table.NextHeading, Is.EqualTo("Would be"));
        }
    }

    /// <summary>
    /// The full destructive cascade (#1605/#1519 Phase 1 shape): a joined object leaves scope, its
    /// Identity is deleted, and two downstream targets are deprovisioned. A recalled attribute travels
    /// with the disconnection.
    /// </summary>
    [Test]
    public void Build_SpeculativeCascade_ProjectsScopeDeleteAndTwoDownstreamDeprovisions()
    {
        var mvoId = Guid.NewGuid();
        var deprovisionA = new SyncOutcomeNode
        {
            OutcomeType = ActivityRunProfileExecutionItemSyncOutcomeType.DeprovisionQueued,
            TargetEntityDescription = "Glitterband EMEA",
            DetailMessage = "2",
            StagedChangeType = PendingExportChangeType.Delete
        };
        var deprovisionB = new SyncOutcomeNode
        {
            OutcomeType = ActivityRunProfileExecutionItemSyncOutcomeType.DeprovisionQueued,
            TargetEntityDescription = "Contoso AD",
            DetailMessage = "3",
            StagedChangeType = PendingExportChangeType.Delete
        };
        var attributeFlow = new SyncOutcomeNode
        {
            OutcomeType = ActivityRunProfileExecutionItemSyncOutcomeType.AttributeFlow,
            DetailCount = 1
        };
        var mvoDeleted = new SyncOutcomeNode
        {
            OutcomeType = ActivityRunProfileExecutionItemSyncOutcomeType.MvoDeleted,
            TargetEntityId = mvoId,
            TargetEntityDescription = "Liam Allen",
            Children = [deprovisionA, deprovisionB]
        };
        var root = new SyncOutcomeNode
        {
            OutcomeType = ActivityRunProfileExecutionItemSyncOutcomeType.DisconnectedOutOfScope,
            TargetEntityId = mvoId,
            TargetEntityDescription = "Liam Allen",
            DetailCount = 1,
            Children = [attributeFlow, mvoDeleted]
        };
        var preview = new SyncPreviewResult
        {
            Inbound = new SyncPreviewInboundSummary
            {
                AttributeFlowChanges = [new SyncPreviewAttributeFlowChange { AttributeName = "mail", IsAddition = false, Value = "liam.allen@example.com" }]
            },
            OutcomeTree = [root]
        };

        var model = CausalityModelBuilder.BuildSpeculative(preview, PreviewContext());

        var table = CausalityTableModelBuilder.Build(model);

        using (Assert.EnterMultipleScope())
        {
            var scopeRow = table.Rows.Single(r => r.ChangeKind == CausalityTableChangeKind.Scope);
            Assert.That(scopeRow.ObjectKey, Is.EqualTo("identity"));
            // Before/After are for attribute values only (#1519 Table view fix 7); the Outcome column's
            // "Left scope" label already says what happened.
            Assert.That(scopeRow.WouldBe, Is.Null);

            var deleteRow = table.Rows.Single(r => r.ChangeKind == CausalityTableChangeKind.Delete);
            Assert.That(deleteRow.ObjectKey, Is.EqualTo("identity"));
            Assert.That(deleteRow.OutcomeLabel, Is.EqualTo("The Metaverse Object would be deleted"));

            var deprovisionRows = table.Rows.Where(r => r.ChangeKind == CausalityTableChangeKind.Deprovision).ToList();
            Assert.That(deprovisionRows, Has.Count.EqualTo(2));
            var downstreamKeys = deprovisionRows.Select(r => r.ObjectKey).Distinct().ToList();
            Assert.That(downstreamKeys, Has.Count.EqualTo(2), "each cascade target is its own downstream object");

            var recalledRow = table.Rows.Single(r => r.ChangeKind == CausalityTableChangeKind.AttributeChange);
            Assert.That(recalledRow.ObjectKey, Is.EqualTo("identity"));
            Assert.That(recalledRow.Attribute, Is.EqualTo("mail"));
            Assert.That(recalledRow.Current, Is.EqualTo("liam.allen@example.com"), "a recall removes the value, so it only carries a current reading");
            Assert.That(recalledRow.WouldBe, Is.Null);

            // The Identity object's tone must reflect its strongest row (Error, from MvoDeleted), not
            // merely the first event encountered (Warning, from DisconnectedOutOfScope).
            var identityObject = table.Objects.Single(o => o.Role == CausalityTableObjectRole.Identity);
            Assert.That(identityObject.Tone, Is.EqualTo(CausalityTone.Error));

            var downstreamObjects = table.Objects.Where(o => o.Role == CausalityTableObjectRole.Downstream).ToList();
            Assert.That(downstreamObjects, Has.Count.EqualTo(2));
            Assert.That(downstreamObjects, Has.All.Matches<CausalityTableObject>(o => o.Tone == CausalityTone.Error));
        }
    }

    /// <summary>
    /// A joined object's preview knows its Identity exists but not its name (no Identity-lane event links
    /// it), and the Identity entry must not borrow the object's own name, which read as though the two
    /// were one thing.
    /// </summary>
    [Test]
    public void Build_SpeculativeModelWithNoIdentityLink_NamesTheIdentityEntryIdentity()
    {
        var preview = new SyncPreviewResult
        {
            OutcomeTree =
            [
                new SyncOutcomeNode
                {
                    OutcomeType = ActivityRunProfileExecutionItemSyncOutcomeType.Provisioned,
                    TargetEntityDescription = "Glitterband",
                    SyncRuleId = 42,
                    SyncRuleName = "Export to Glitterband"
                }
            ]
        };
        var model = CausalityModelBuilder.BuildSpeculative(preview, PreviewContext());

        var table = CausalityTableModelBuilder.Build(model);

        var identity = table.Objects.Single(o => o.Role == CausalityTableObjectRole.Identity);
        var source = table.Objects.Single(o => o.Role == CausalityTableObjectRole.Source);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(identity.DisplayName, Is.EqualTo("Metaverse Object"));
            Assert.That(identity.DisplayName, Is.Not.EqualTo(source.DisplayName));
        }
    }

    /// <summary>
    /// A Provisioned node names its target system without an id while its queued-export child carries the
    /// id; both must land on one downstream entry, not one each.
    /// </summary>
    [Test]
    public void Build_ProvisionedNodeAndItsQueuedExportChild_ShareOneDownstreamEntry()
    {
        var preview = new SyncPreviewResult
        {
            OutcomeTree =
            [
                new SyncOutcomeNode
                {
                    OutcomeType = ActivityRunProfileExecutionItemSyncOutcomeType.Provisioned,
                    TargetEntityDescription = "Glitterband",
                    SyncRuleId = 42,
                    SyncRuleName = "Export to Glitterband",
                    Children =
                    [
                        new SyncOutcomeNode
                        {
                            OutcomeType = ActivityRunProfileExecutionItemSyncOutcomeType.PendingExportCreated,
                            TargetEntityDescription = "Glitterband",
                            DetailMessage = "2",
                            StagedChangeType = PendingExportChangeType.Create
                        }
                    ]
                }
            ]
        };
        var model = CausalityModelBuilder.BuildSpeculative(preview, PreviewContext());

        var table = CausalityTableModelBuilder.Build(model);

        var downstream = table.Objects.Where(o => o.Role == CausalityTableObjectRole.Downstream).ToList();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(downstream, Has.Count.EqualTo(1));
            // A preview's Provisioned node never carries a Record link (nothing has been created yet;
            // see CausalityModelBuilder.BuildSpeculativeLinks), so the entry stays on its placeholder name.
            Assert.That(downstream[0].DisplayName, Is.EqualTo("New object in Glitterband"));
            Assert.That(downstream[0].Href, Is.Null);
            Assert.That(downstream[0].RowCount, Is.EqualTo(2), "The provision row and the queued-export row");
        }
    }

    // ─── Object column identity resolution (#1519 Table view fix 1) ───

    /// <summary>
    /// A recorded Provisioned outcome's own Record-kind link (the Connected System Object it created)
    /// upgrades the downstream entry from its "New object in..." placeholder to the object's own
    /// identity, keeping the Connected System's name as the entry's Subtitle rather than its name.
    /// </summary>
    [Test]
    public void Build_ProvisionedEvent_UpgradesTheDownstreamObjectsIdentityAndHref()
    {
        var model = CausalityModelBuilder.Build(CausalityTestData.NewJoinerItem(), CausalityTestData.NewJoinerContext());

        var table = CausalityTableModelBuilder.Build(model);

        var downstream = table.Objects.Single(o => o.Role == CausalityTableObjectRole.Downstream);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(downstream.DisplayName, Is.EqualTo($"person: {CausalityTestData.ProvisionedCsoId}"));
            Assert.That(downstream.Subtitle, Is.EqualTo("Glitterband EMEA"));
            Assert.That(downstream.Href, Is.EqualTo(JimUtilities.GetConnectedSystemObjectHref(2, CausalityTestData.ProvisionedCsoId)));
        }
    }

    /// <summary>
    /// The downstream object's name follows the context's current-name map when the page supplied one,
    /// rather than the recorded run's "type: id" fallback (#1519 Table view fix 5): the Record link built
    /// from the map propagates into the Table view exactly as the recorded fallback already did above.
    /// </summary>
    [Test]
    public void Build_ProvisionedEvent_DisplayNameFollowsTheContextsCurrentNameWhenSupplied()
    {
        var context = CausalityTestData.NewJoinerContext() with
        {
            ConnectedSystemObjectNames = new Dictionary<Guid, string> { [CausalityTestData.ProvisionedCsoId] = "liam.allen" }
        };
        var model = CausalityModelBuilder.Build(CausalityTestData.NewJoinerItem(), context);

        var table = CausalityTableModelBuilder.Build(model);

        var downstream = table.Objects.Single(o => o.Role == CausalityTableObjectRole.Downstream);
        Assert.That(downstream.DisplayName, Is.EqualTo("liam.allen"));
    }

    /// <summary>
    /// The "Everything" object always carries no href of its own: it is the flattened view, not a
    /// single object with a detail page.
    /// </summary>
    [Test]
    public void Build_EverythingObject_CarriesNoHref()
    {
        var model = CausalityModelBuilder.Build(CausalityTestData.NewJoinerItem(), CausalityTestData.NewJoinerContext());

        var table = CausalityTableModelBuilder.Build(model);

        var everything = table.Objects.Single(o => o.Role == CausalityTableObjectRole.Everything);
        Assert.That(everything.Href, Is.Null);
    }

    // ─── Source and Identity links (#1519 Table view fix 6) ───

    /// <summary>
    /// The object being synchronised links to its own Connected System Object page, built from the page
    /// context's record id and the Connected System it lives on.
    /// </summary>
    [Test]
    public void Build_SourceObject_LinksToItsConnectedSystemObjectPage()
    {
        var model = CausalityModelBuilder.Build(CausalityTestData.NewJoinerItem(), CausalityTestData.NewJoinerContext());

        var table = CausalityTableModelBuilder.Build(model);

        var source = table.Objects.Single(o => o.Role == CausalityTableObjectRole.Source);
        Assert.That(source.Href, Is.EqualTo(JimUtilities.GetConnectedSystemObjectHref(1, CausalityTestData.CsoId)));
    }

    /// <summary>
    /// A deleted record (or any context that never resolved the record's own system) renders plain,
    /// rather than a link to a page that would 404.
    /// </summary>
    [Test]
    public void Build_SourceObject_HasNoHrefWhenTheRecordIsUnresolved()
    {
        var model = CausalityModelBuilder.Build(CausalityTestData.NewJoinerItem(), CausalityTestData.EmptyContext());

        var table = CausalityTableModelBuilder.Build(model);

        var source = table.Objects.Single(o => o.Role == CausalityTableObjectRole.Source);
        Assert.That(source.Href, Is.Null);
    }

    /// <summary>
    /// The Identity links to its own Metaverse Object page, taken from the first Identity-kind link with
    /// an href among the model's Identity-lane events (the Projected outcome's own link here).
    /// </summary>
    [Test]
    public void Build_IdentityObject_LinksToItsMetaverseObjectPageFromTheFirstIdentityLinkWithAnHref()
    {
        var model = CausalityModelBuilder.Build(CausalityTestData.NewJoinerItem(), CausalityTestData.NewJoinerContext());

        var table = CausalityTableModelBuilder.Build(model);

        var identity = table.Objects.Single(o => o.Role == CausalityTableObjectRole.Identity);
        Assert.That(identity.Href, Is.EqualTo(JimUtilities.GetMetaverseObjectHref(CausalityTestData.MvoId, "People")));
    }

    /// <summary>
    /// A deleted Identity's link points at its deletion record rather than a live detail page, so it
    /// carries no href of the Identity's own kind: the Table view must render it plain, not linked.
    /// </summary>
    [Test]
    public void Build_IdentityObject_HasNoHrefWhenTheIdentityHasBeenDeleted()
    {
        var model = CausalityModelBuilder.Build(CausalityTestData.LeaverItem(), CausalityTestData.NewJoinerContext());

        var table = CausalityTableModelBuilder.Build(model);

        var identity = table.Objects.Single(o => o.Role == CausalityTableObjectRole.Identity);
        Assert.That(identity.Href, Is.Null);
    }

    // ─── Synchronisation Rule column (#1519 Table view fix 3 & 4) ───

    /// <summary>
    /// An object-level row decided by a Synchronisation Rule carries the rule's id, so the view can
    /// link it; the Provisioned row here (NewJoinerItem) is directly attributed, not inherited.
    /// </summary>
    [Test]
    public void Build_ProvisionRow_CarriesItsOwnSyncRuleId()
    {
        var model = CausalityModelBuilder.Build(CausalityTestData.NewJoinerItem(), CausalityTestData.NewJoinerContext());

        var table = CausalityTableModelBuilder.Build(model);

        var provisionRow = table.Rows.Single(r => r.ChangeKind == CausalityTableChangeKind.Provision);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(provisionRow.Via, Is.EqualTo("Glitterband People - Outbound"));
            Assert.That(provisionRow.SyncRuleId, Is.EqualTo(9));
        }
    }

    /// <summary>
    /// A queued export staged beneath a Provisioned parent carries no Synchronisation Rule of its own
    /// (the engine attributes the decision to the parent), so its object-level row AND its attribute
    /// rows fall back to the parent's effective rule (#1519 Table view fix 4).
    /// </summary>
    /// <summary>
    /// A value change that recorded its own contributing rule names that rule, ahead of anything inherited
    /// from the event: one export can carry values from several rules, and the recorded attribution is
    /// the truth for each row.
    /// </summary>
    [Test]
    public void Build_AttributeRowWithItsOwnRecordedRule_NamesThatRuleAheadOfTheInheritedOne()
    {
        var item = CausalityTestData.NewJoinerItem();
        var exportOutcome = item.SyncOutcomes.First(o => o.OutcomeType == ActivityRunProfileExecutionItemSyncOutcomeType.PendingExportCreated);
        var firstValue = exportOutcome.ConnectedSystemObjectChange!.AttributeChanges.First().ValueChanges.First();
        firstValue.SyncRuleId = 42;
        firstValue.SyncRuleName = "Glitterband Managers - Outbound";
        var attributeName = exportOutcome.ConnectedSystemObjectChange.AttributeChanges.First().AttributeName;

        var table = CausalityTableModelBuilder.Build(CausalityModelBuilder.Build(item, CausalityTestData.NewJoinerContext()));

        var attributeRows = table.Rows.Where(r => r.ChangeKind == CausalityTableChangeKind.AttributeChange).ToList();
        var attributed = attributeRows.Single(r => r.Attribute == attributeName);
        var others = attributeRows.Where(r => r.Attribute != attributeName).ToList();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(attributed.Via, Is.EqualTo("Glitterband Managers - Outbound"));
            Assert.That(attributed.SyncRuleId, Is.EqualTo(42));
            Assert.That(others, Is.Not.Empty);
            Assert.That(others.Select(r => r.SyncRuleId), Has.All.EqualTo(9), "rows with no recorded rule still inherit the Provision decision's");
        }
    }

    /// <summary>
    /// A queued export or deprovision with no rule of its own and no same-lane ancestor to inherit from
    /// (an update export on an existing object, a cascade's deprovision) takes the one rule its value
    /// changes agree on; values from several rules leave the row unattributed rather than guessed.
    /// </summary>
    [Test]
    public void Build_DeprovisionRowWithNoRuleOfItsOwn_TakesTheRuleItsValuesAgreeOn()
    {
        var item = CausalityTestData.LeaverItem();
        var deprovision = item.SyncOutcomes.First(o => o.OutcomeType == ActivityRunProfileExecutionItemSyncOutcomeType.DeprovisionQueued
                                                        && o.ConnectedSystemObjectChange != null);
        foreach (var value in deprovision.ConnectedSystemObjectChange!.AttributeChanges.SelectMany(a => a.ValueChanges))
        {
            value.SyncRuleId = 9;
            value.SyncRuleName = "Glitterband People - Outbound";
        }

        var table = CausalityTableModelBuilder.Build(CausalityModelBuilder.Build(item, CausalityTestData.NewJoinerContext()));

        var attributed = table.Rows.Single(r => r.ChangeKind == CausalityTableChangeKind.Deprovision && r.SyncRuleId != null);
        var unattributed = table.Rows.Single(r => r.ChangeKind == CausalityTableChangeKind.Deprovision && r.SyncRuleId == null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(attributed.Via, Is.EqualTo("Glitterband People - Outbound"));
            Assert.That(attributed.SyncRuleId, Is.EqualTo(9));
            Assert.That(unattributed.Via, Is.Null, "the target with no value changes has nothing to agree on");
        }
    }

    [Test]
    public void Build_DeprovisionRowWhoseValuesDisagree_StaysUnattributed()
    {
        var item = CausalityTestData.LeaverItem();
        var deprovision = item.SyncOutcomes.First(o => o.OutcomeType == ActivityRunProfileExecutionItemSyncOutcomeType.DeprovisionQueued
                                                        && o.ConnectedSystemObjectChange != null);
        var change = deprovision.ConnectedSystemObjectChange!;
        change.AttributeChanges.First().ValueChanges.First().SyncRuleId = 9;
        change.AttributeChanges.First().ValueChanges.First().SyncRuleName = "Glitterband People - Outbound";
        var second = new ConnectedSystemObjectChangeAttribute
        {
            Id = Guid.NewGuid(),
            AttributeName = "memberOf",
            AttributeType = AttributeDataType.Text,
            Attribute = new ConnectedSystemObjectTypeAttribute { Name = "memberOf", AttributePlurality = AttributePlurality.MultiValued }
        };
        second.ValueChanges.Add(new ConnectedSystemObjectChangeAttributeValue
        {
            ValueChangeType = ValueChangeType.Remove,
            StringValue = "cn=leavers",
            SyncRuleId = 12,
            SyncRuleName = "Glitterband Groups - Outbound"
        });
        change.AttributeChanges.Add(second);

        var table = CausalityTableModelBuilder.Build(CausalityModelBuilder.Build(item, CausalityTestData.NewJoinerContext()));

        var rows = table.Rows.Where(r => r.ChangeKind == CausalityTableChangeKind.Deprovision).ToList();
        Assert.That(rows.Select(r => r.SyncRuleId), Has.All.Null);
    }

    [Test]
    public void Build_QueuedExportChildAndItsAttributeRows_InheritTheProvisionedParentsSyncRule()
    {
        var model = CausalityModelBuilder.Build(CausalityTestData.NewJoinerItem(), CausalityTestData.NewJoinerContext());

        var table = CausalityTableModelBuilder.Build(model);

        var exportRow = table.Rows.Single(r => r.ChangeKind == CausalityTableChangeKind.ExportQueued);
        var attributeRows = table.Rows.Where(r => r.ChangeKind == CausalityTableChangeKind.AttributeChange).ToList();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(exportRow.Via, Is.EqualTo("Glitterband People - Outbound"));
            Assert.That(exportRow.SyncRuleId, Is.EqualTo(9));
            Assert.That(attributeRows.Select(r => r.Via), Has.All.EqualTo("Glitterband People - Outbound"));
            Assert.That(attributeRows.Select(r => r.SyncRuleId), Has.All.EqualTo(9));
        }
    }
}
