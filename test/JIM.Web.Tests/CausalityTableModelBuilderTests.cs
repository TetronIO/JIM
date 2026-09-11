// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Linq;
using JIM.Models.Activities;
using JIM.Models.Sync;
using JIM.Models.Transactional;
using JIM.Web.Causality;
using NUnit.Framework;

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
            var identityRow = table.Rows.Single(r => r.ChangeKind == CausalityTableChangeKind.JoinOrProjection);
            Assert.That(identityRow.ObjectKey, Is.EqualTo("identity"));
            Assert.That(identityRow.WouldBe, Is.EqualTo("New Identity"));

            var provisionRow = table.Rows.Single(r => r.ChangeKind == CausalityTableChangeKind.Provision);
            Assert.That(provisionRow.ObjectKey, Does.StartWith("ds:"));
            Assert.That(provisionRow.Attribute, Is.EqualTo("connector: Glitterband EMEA"));

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
            Assert.That(downstreamObjects.Select(o => o.DisplayName),
                Is.EquivalentTo(new[] { "Glitterband EMEA", "Contoso AD" }));
            // Glitterband's deprovision carries the target's distinguishedName as a recalled attribute
            // row (the connector's resolution key); Contoso's fixture has none, so it stays at 1.
            var contoso = downstreamObjects.Single(o => o.DisplayName == "Contoso AD");
            var glitterband = downstreamObjects.Single(o => o.DisplayName == "Glitterband EMEA");
            Assert.That(contoso.RowCount, Is.EqualTo(1));
            Assert.That(glitterband.RowCount, Is.EqualTo(2));

            var deleteRow = table.Rows.Single(r => r.ChangeKind == CausalityTableChangeKind.Delete);
            Assert.That(deleteRow.ObjectKey, Is.EqualTo("identity"));
            Assert.That(deleteRow.WouldBe, Is.EqualTo("Deleted"));
            Assert.That(deleteRow.Via, Is.EqualTo("Deleted immediately: last authoritative source disconnected"));
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
    public void Matches_ScopeAndJoinFilter_MatchesOnlyScopeAndJoinRows()
    {
        var scopeRow = new CausalityTableRow("identity", CausalityTableChangeKind.Scope, "Import scope", "In scope", "Out of scope", null, "l", "t", CausalityTone.Warning);
        var joinRow = new CausalityTableRow("identity", CausalityTableChangeKind.JoinOrProjection, "Metaverse Object", null, "New Identity", null, "l", "t", CausalityTone.Primary);
        var deleteRow = new CausalityTableRow("identity", CausalityTableChangeKind.Delete, "Metaverse Object", "Active", "Deleted", null, "l", "t", CausalityTone.Error);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(CausalityTableFilters.Matches(scopeRow, CausalityTableFilter.ScopeAndJoin), Is.True);
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
            var row = new CausalityTableRow("identity", kind, "Metaverse Object", "a", "b", null, "l", "t", CausalityTone.Error);
            Assert.That(CausalityTableFilters.Matches(row, CausalityTableFilter.Destructive), Is.True, $"{kind} must be destructive");
        }

        var provisionRow = new CausalityTableRow("ds:2", CausalityTableChangeKind.Provision, "connector: X", null, "Account provisioned", null, "l", "t", CausalityTone.Primary);
        Assert.That(CausalityTableFilters.Matches(provisionRow, CausalityTableFilter.Destructive), Is.False);
    }

    [Test]
    public void Matches_AttributeAndObjectChangeFilters_ArePartitionsOfEachOther()
    {
        var attributeRow = new CausalityTableRow("identity", CausalityTableChangeKind.AttributeChange, "mail", "old", "new", null, "l", "t", CausalityTone.Info);
        var objectRow = new CausalityTableRow("identity", CausalityTableChangeKind.Delete, "Metaverse Object", "Active", "Deleted", null, "l", "t", CausalityTone.Error);

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
        var row = new CausalityTableRow("identity", CausalityTableChangeKind.ValuesPreserved, "department", "x", "x", null, "l", "t", CausalityTone.Warning);

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
            Assert.That(scopeRow.WouldBe, Is.EqualTo("Out of scope"));

            var deleteRow = table.Rows.Single(r => r.ChangeKind == CausalityTableChangeKind.Delete);
            Assert.That(deleteRow.ObjectKey, Is.EqualTo("identity"));
            Assert.That(deleteRow.OutcomeLabel, Is.EqualTo("Identity would be deleted"));

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
            Assert.That(identity.DisplayName, Is.EqualTo("Identity"));
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
            Assert.That(downstream[0].DisplayName, Is.EqualTo("Glitterband"));
            Assert.That(downstream[0].RowCount, Is.EqualTo(2), "The provision row and the queued-export row");
        }
    }
}
