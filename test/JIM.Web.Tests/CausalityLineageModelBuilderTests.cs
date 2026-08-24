// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Linq;
using JIM.Models.Activities;
using JIM.Models.Activities.DTOs;
using JIM.Models.Enums;
using JIM.Web.Causality;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// Tests for <see cref="CausalityLineageModelBuilder"/>: the projection of a Run Profile Execution
/// Item's causality model and causal chain onto the object lineage's columns (#1495). Covers column
/// derivation and order per item type, lit-column selection, chain-hop assignment (including the
/// derived source-import hop and the deprovision chain built from snapshots), cohort collapse, the
/// three endings, join labels and role heads.
/// </summary>
[TestFixture]
public class CausalityLineageModelBuilderTests
{
    private static readonly Guid SyncItemId = Guid.Parse("55555555-5555-5555-5555-555555555555");
    private static readonly Guid ImportItemId = Guid.Parse("66666666-6666-6666-6666-666666666666");
    private static readonly Guid ExportItemId = Guid.Parse("77777777-7777-7777-7777-777777777777");

    /// <summary>
    /// The one object a single-object column holds (#1495 moved Title/Cards/Endings and friends from
    /// the column onto its objects, since a side can now hold several). Most of this file's stories
    /// put one record on a side, so this is the common accessor; index <c>column.Objects[i]</c>
    /// directly where a test deliberately puts several records on one side.
    /// </summary>
    private static CausalityLineageObject Sole(CausalityLineageColumn column) => column.Objects.Single();

    // ─── Objects the panel knows are gone (#1495) ───

    /// <summary>
    /// An object this run deleted is marked gone and carries its deletion record. "Cannot be linked" is the
    /// wrong conclusion for it: JIM retains a deletion record, so there is somewhere to go, and the panel
    /// already deep-links it from the event that recorded the deletion.
    /// </summary>
    [Test]
    public void Build_IdentityDeletedByThisRun_IsMarkedGoneAndCarriesItsDeletionRecord()
    {
        var model = CausalityModelBuilder.Build(CausalityTestData.LeaverItem(), CausalityTestData.NewJoinerContext());

        var lineage = CausalityLineageModelBuilder.Build(model, chain: null, ObjectChangeType.Disconnected);

        var identity = Sole(lineage.Columns.Single(c => c.Kind == CausalityLineageColumnKind.Identity));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(identity.IsDeleted, Is.True);
            Assert.That(identity.DeletionRecordHref, Does.StartWith("/admin/deleted-objects"));
            Assert.That(identity.DeletionRecordShownOnACard, Is.True,
                "this run's own MVO Deleted card already offers the link, so the head must not repeat it");
        }
    }

    /// <summary>
    /// An object nothing proves is gone is not claimed to be. A record that simply has no link (its
    /// snapshots carried no id) is a different fact from a deleted one, and conflating them would tell
    /// the reader an object had been deleted when it may be perfectly alive.
    /// </summary>
    [Test]
    public void Build_ObjectWithNoDeletionEvidence_IsNotMarkedGone()
    {
        var model = CausalityModelBuilder.Build(CausalityTestData.NewJoinerItem(), CausalityTestData.NewJoinerContext());

        var lineage = CausalityLineageModelBuilder.Build(model, chain: null, ObjectChangeType.Projected);

        Assert.That(lineage.Columns.SelectMany(c => c.Objects).Select(o => o.IsDeleted), Has.All.False);
    }

    /// <summary>
    /// An Identity a *later* run deleted leaves no trace on this item, so the panel only knows it is gone
    /// because the page looked it up and found nothing. That is the common case, and the one the reader most
    /// needs: this item's own story is intact, and the object it created no longer exists.
    /// </summary>
    [Test]
    public void Build_IdentityTheLookupFoundMissing_IsMarkedGoneEvenThoughThisRunDidNotDeleteIt()
    {
        var deletedId = Guid.Parse("2faa1700-a4bf-438d-a987-2a00a5f32794");
        var context = CausalityTestData.NewJoinerContext() with { DeletedMetaverseObjectId = deletedId };
        var model = CausalityModelBuilder.Build(CausalityTestData.NewJoinerItem(), context);

        var lineage = CausalityLineageModelBuilder.Build(model, chain: null, ObjectChangeType.Projected);

        var identity = Sole(lineage.Columns.Single(c => c.Kind == CausalityLineageColumnKind.Identity));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(identity.IsDeleted, Is.True);
            Assert.That(identity.DeletionRecordHref, Does.Contain(deletedId.ToString()));
            Assert.That(identity.DeletionRecordShownOnACard, Is.False,
                "nothing on this item recorded the deletion, so the head is the only place to offer the record");
        }
    }

    /// <summary>
    /// The evidence is about the Identity, so it never marks a record gone.
    /// </summary>
    [Test]
    public void Build_IdentityTheLookupFoundMissing_LeavesRecordObjectsAlone()
    {
        var context = CausalityTestData.NewJoinerContext() with { DeletedMetaverseObjectId = Guid.NewGuid() };
        var model = CausalityModelBuilder.Build(CausalityTestData.NewJoinerItem(), context);

        var lineage = CausalityLineageModelBuilder.Build(model, chain: null, ObjectChangeType.Projected);

        Assert.That(lineage.Columns.Where(c => c.Kind == CausalityLineageColumnKind.Record)
            .SelectMany(c => c.Objects).Select(o => o.IsDeleted), Has.All.False);
    }

    // ─── Sides holding several records (#1495) ───

    /// <summary>
    /// Records on the same side of the Identity share a column. A column each widened the canvas by a
    /// track and a gutter per Connected System, and placed the records on an axis that means "one hop
    /// further along the causal chain" when they are siblings: the builder returns no relationship
    /// between two of them, so those gutters were always drawn empty.
    /// </summary>
    [Test]
    public void Build_SeveralTargetSystems_PutsEveryTargetRecordInOneColumn()
    {
        var model = CausalityModelBuilder.Build(CausalityTestData.LeaverItem(), CausalityTestData.NewJoinerContext());

        var lineage = CausalityLineageModelBuilder.Build(model, chain: null, ObjectChangeType.Disconnected);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(lineage.Columns, Has.Count.EqualTo(3),
                "two deprovisioned systems must not add a column each: source, Identity, targets");
            Assert.That(lineage.Columns[2].Kind, Is.EqualTo(CausalityLineageColumnKind.Record));
            Assert.That(lineage.Columns[2].Objects.Select(o => o.SystemName),
                Is.EqualTo(new[] { "Glitterband EMEA", "Contoso AD" }));
            // Each object keeps its own events, which is what lets one column read as two stories.
            Assert.That(lineage.Columns[2].Objects.Select(o => o.Cards.Count), Is.EqualTo(new[] { 1, 1 }));
            Assert.That(lineage.Joins, Has.Count.EqualTo(2), "one join per adjacent pair of columns");
        }
    }

    /// <summary>
    /// A story's width is now bounded by its sides, not by the deployment's Connected System count:
    /// four columns at most (source side, Identity, target side, the trailing unplaceable column).
    /// </summary>
    [Test]
    public void Build_ManyTargetSystems_StillProducesAtMostFourColumns()
    {
        var item = new ActivityRunProfileExecutionItem { Id = Guid.NewGuid() };
        var projected = CausalityTestData.AddOutcome(item,
            ActivityRunProfileExecutionItemSyncOutcomeType.Projected, parent: null, ordinal: 0,
            targetEntityId: Guid.NewGuid(), targetEntityDescription: "Liam Allen");
        for (var systemId = 2; systemId <= 9; systemId++)
        {
            CausalityTestData.AddOutcome(item, ActivityRunProfileExecutionItemSyncOutcomeType.Provisioned,
                parent: projected, ordinal: systemId, targetEntityId: Guid.NewGuid(),
                targetEntityDescription: $"System {systemId}", detailMessage: $"{systemId}|person");
        }

        var model = CausalityModelBuilder.Build(item, CausalityTestData.NewJoinerContext());
        var lineage = CausalityLineageModelBuilder.Build(model, chain: null, ObjectChangeType.Projected);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(lineage.Columns, Has.Count.LessThanOrEqualTo(4));
            Assert.That(lineage.Columns[^1].Objects, Has.Count.EqualTo(8),
                "every target system is still present, stacked rather than spread sideways");
        }
    }

    /// <summary>
    /// One label now speaks for a whole side, so it may only claim what is true of every record on it.
    /// Every provisioned record was also exported to, but not every exported record was created, so a
    /// story where one system gained an account while another merely took an update must not read as
    /// though both were provisioned.
    /// </summary>
    [Test]
    public void Build_TargetSideWhereOnlySomeRecordsWereProvisioned_ReadsExported()
    {
        var item = new ActivityRunProfileExecutionItem { Id = Guid.NewGuid() };
        var projected = CausalityTestData.AddOutcome(item,
            ActivityRunProfileExecutionItemSyncOutcomeType.Projected, parent: null, ordinal: 0,
            targetEntityId: Guid.NewGuid(), targetEntityDescription: "Liam Allen");
        CausalityTestData.AddOutcome(item, ActivityRunProfileExecutionItemSyncOutcomeType.Provisioned,
            parent: projected, ordinal: 0, targetEntityId: Guid.NewGuid(),
            targetEntityDescription: "Glitterband EMEA", detailMessage: "2|person");
        // The second system took a staged update; nothing was created there.
        CausalityTestData.AddOutcome(item, ActivityRunProfileExecutionItemSyncOutcomeType.PendingExportCreated,
            parent: projected, ordinal: 1, targetEntityId: Guid.NewGuid(),
            targetEntityDescription: "Contoso AD", detailCount: 3, detailMessage: "3");

        var model = CausalityModelBuilder.Build(item, CausalityTestData.NewJoinerContext());
        var lineage = CausalityLineageModelBuilder.Build(model, chain: null, ObjectChangeType.Projected);

        Assert.That(lineage.Columns[^1].Objects, Has.Count.EqualTo(2));
        Assert.That(lineage.Joins[^1].Label, Is.EqualTo("exported"));
    }

    /// <summary>
    /// The other half of the same rule: a side every record of which was created does read "provisioned".
    /// </summary>
    [Test]
    public void Build_TargetSideWhereEveryRecordWasProvisioned_ReadsProvisioned()
    {
        var item = new ActivityRunProfileExecutionItem { Id = Guid.NewGuid() };
        var projected = CausalityTestData.AddOutcome(item,
            ActivityRunProfileExecutionItemSyncOutcomeType.Projected, parent: null, ordinal: 0,
            targetEntityId: Guid.NewGuid(), targetEntityDescription: "Liam Allen");
        CausalityTestData.AddOutcome(item, ActivityRunProfileExecutionItemSyncOutcomeType.Provisioned,
            parent: projected, ordinal: 0, targetEntityId: Guid.NewGuid(),
            targetEntityDescription: "Glitterband EMEA", detailMessage: "2|person");
        CausalityTestData.AddOutcome(item, ActivityRunProfileExecutionItemSyncOutcomeType.Provisioned,
            parent: projected, ordinal: 1, targetEntityId: Guid.NewGuid(),
            targetEntityDescription: "Contoso AD", detailMessage: "3|user");

        var model = CausalityModelBuilder.Build(item, CausalityTestData.NewJoinerContext());
        var lineage = CausalityLineageModelBuilder.Build(model, chain: null, ObjectChangeType.Projected);

        Assert.That(lineage.Columns[^1].Objects, Has.Count.EqualTo(2));
        Assert.That(lineage.Joins[^1].Label, Is.EqualTo("provisioned"));
    }

    // ─── Column derivation and lit columns per item type ───

    [Test]
    public void Build_ImportItem_LightsTheSourceRecordColumnOnly()
    {
        var item = new ActivityRunProfileExecutionItem { Id = Guid.NewGuid() };
        CausalityTestData.AddOutcome(item, ActivityRunProfileExecutionItemSyncOutcomeType.CsoAdded,
            parent: null, ordinal: 0);
        var model = CausalityModelBuilder.Build(item, CausalityTestData.NewJoinerContext());

        var lineage = CausalityLineageModelBuilder.Build(model, chain: null, ObjectChangeType.Added);

        Assert.That(lineage.Columns, Has.Count.EqualTo(1));
        var column = lineage.Columns[0];
        var record = Sole(column);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(column.Kind, Is.EqualTo(CausalityLineageColumnKind.Record));
            Assert.That(record.Title, Is.EqualTo("Liam Allen"));
            Assert.That(record.SystemName, Is.EqualTo("Yellowstone APAC"));
            Assert.That(record.ObjectTypeName, Is.EqualTo("person"));
            Assert.That(column.IsLit, Is.True);
            Assert.That(record.Cards, Has.Count.EqualTo(1));
            Assert.That(record.Cards[0].IsThisRun, Is.True);
            Assert.That(lineage.Joins, Is.Empty);
        }
    }

    [Test]
    public void Build_SyncNewJoinerItem_BuildsSourceIdentityAndTargetColumnsInOrder()
    {
        var item = CausalityTestData.NewJoinerItem();
        var model = CausalityModelBuilder.Build(item, CausalityTestData.NewJoinerContext());

        var lineage = CausalityLineageModelBuilder.Build(model, chain: null, ObjectChangeType.Projected);

        Assert.That(lineage.Columns, Has.Count.EqualTo(3));
        var source = lineage.Columns[0];
        var identity = lineage.Columns[1];
        var target = lineage.Columns[2];
        var sourceRecord = Sole(source);
        var identityObject = Sole(identity);
        var targetRecord = Sole(target);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(source.Kind, Is.EqualTo(CausalityLineageColumnKind.Record));
            Assert.That(sourceRecord.SystemName, Is.EqualTo("Yellowstone APAC"));
            Assert.That(source.IsLit, Is.False, "the sync run's events happen to the Identity and the target, not the source record");

            Assert.That(identity.Kind, Is.EqualTo(CausalityLineageColumnKind.Identity));
            Assert.That(identityObject.Title, Is.EqualTo("Liam Allen"));
            Assert.That(identityObject.ObjectTypeName, Is.EqualTo("Person"));
            Assert.That(identity.IsLit, Is.True);
            Assert.That(identityObject.Cards.Select(c => c.Event!.OutcomeType), Is.EqualTo(new[]
            {
                ActivityRunProfileExecutionItemSyncOutcomeType.Projected,
                ActivityRunProfileExecutionItemSyncOutcomeType.AttributeFlow
            }));

            Assert.That(target.Kind, Is.EqualTo(CausalityLineageColumnKind.Record));
            Assert.That(targetRecord.SystemName, Is.EqualTo("Glitterband EMEA"));
            Assert.That(targetRecord.Title, Is.EqualTo("Liam Allen"));
            Assert.That(target.IsLit, Is.True);
            Assert.That(targetRecord.Cards.Select(c => c.Event!.OutcomeType), Is.EqualTo(new[]
            {
                ActivityRunProfileExecutionItemSyncOutcomeType.Provisioned,
                ActivityRunProfileExecutionItemSyncOutcomeType.PendingExportCreated
            }));

            Assert.That(lineage.Joins.Select(j => j.Label), Is.EqualTo(new[] { "projected", "provisioned" }));
        }
    }

    // ─── Export items: chain hops land on the objects they happened to ───

    [Test]
    public void Build_ExportCreateItem_PlacesChainHopsOnTheirObjectColumns()
    {
        var item = new ActivityRunProfileExecutionItem { Id = ExportItemId };
        CausalityTestData.AddOutcome(item, ActivityRunProfileExecutionItemSyncOutcomeType.Exported,
            parent: null, ordinal: 0, detailCount: 11);
        var chain = CausalityTestData.Chain(ExportItemId, truncatedByDepth: false,
            CausalityTestData.Cohort(
                CausalEdgeType.PendingExportQueueingCausedExportExecution,
                reasonCode: CausalReasonCode.ExportCreateStaged,
                connectedSystemId: 2, connectedSystemName: "Glitterband EMEA",
                syncRuleId: 9, syncRuleName: "Glitterband People - Outbound",
                members: CausalityTestData.Member("Liam Allen", SyncItemId,
                    occurred: CausalityTestData.ChainBaseTime.AddMinutes(10),
                    causes: CausalityTestData.Cohort(
                        default,
                        sourceImportChangeType: ObjectChangeType.Added,
                        connectedSystemId: 1, connectedSystemName: "Yellowstone APAC",
                        members: CausalityTestData.Member("Liam Allen", ImportItemId,
                            CausalChainResolution.NoFurtherCauses,
                            occurred: CausalityTestData.ChainBaseTime)))));
        var model = CausalityModelBuilder.Build(item, CausalityTestData.ExportContext(), chain: chain);

        var lineage = CausalityLineageModelBuilder.Build(model, chain, ObjectChangeType.Exported);

        Assert.That(lineage.Columns, Has.Count.EqualTo(3));
        var source = lineage.Columns[0];
        var identity = lineage.Columns[1];
        var target = lineage.Columns[2];
        var sourceRecord = Sole(source);
        var identityObject = Sole(identity);
        var targetRecord = Sole(target);
        using (Assert.EnterMultipleScope())
        {
            // The source record column comes from the derived source-import hop's snapshot.
            Assert.That(source.Kind, Is.EqualTo(CausalityLineageColumnKind.Record));
            Assert.That(sourceRecord.SystemName, Is.EqualTo("Yellowstone APAC"));
            Assert.That(sourceRecord.Title, Is.EqualTo("Liam Allen"));
            Assert.That(source.IsLit, Is.False);
            Assert.That(sourceRecord.Cards, Has.Count.EqualTo(1));
            Assert.That(sourceRecord.Cards[0].IsThisRun, Is.False);
            Assert.That(sourceRecord.Cards[0].Hop!.RunKind, Is.EqualTo("Import run"));
            Assert.That(sourceRecord.Cards[0].Hop!.ActivityItemHref, Is.EqualTo($"/activity/item/{ImportItemId}"));
            Assert.That(sourceRecord.Endings.Select(e => e.Resolution),
                Is.EqualTo(new[] { CausalChainResolution.NoFurtherCauses }));

            // The Identity column exists because the graph never joins two records directly, even
            // though no loaded event happened to it on a create export.
            Assert.That(identity.Kind, Is.EqualTo(CausalityLineageColumnKind.Identity));
            Assert.That(identityObject.Title, Is.EqualTo("Liam Allen"));
            Assert.That(identityObject.Cards, Is.Empty);

            // The provisioning decision lands on the record it created; this run's export follows it.
            Assert.That(target.Kind, Is.EqualTo(CausalityLineageColumnKind.Record));
            Assert.That(targetRecord.SystemName, Is.EqualTo("Glitterband EMEA"));
            Assert.That(target.IsLit, Is.True);
            Assert.That(targetRecord.Cards, Has.Count.EqualTo(2));
            Assert.That(targetRecord.Cards[0].IsThisRun, Is.False, "the chain card is older, so it renders before this run's");
            Assert.That(targetRecord.Cards[0].Hop!.RunKind, Is.EqualTo("Synchronisation run"));
            Assert.That(targetRecord.Cards[0].Hop!.ActivityItemHref, Is.EqualTo($"/activity/item/{SyncItemId}"));
            Assert.That(targetRecord.Cards[1].IsThisRun, Is.True);

            Assert.That(lineage.Joins.Select(j => j.Label), Is.EqualTo(new[] { "imported", "provisioned" }));
            Assert.That(lineage.IsTruncatedByDepth, Is.False);
        }
    }

    [Test]
    public void Build_ExportUpdateItem_QueueingHopLandsOnTheIdentityColumn()
    {
        var item = new ActivityRunProfileExecutionItem { Id = ExportItemId };
        CausalityTestData.AddOutcome(item, ActivityRunProfileExecutionItemSyncOutcomeType.Exported,
            parent: null, ordinal: 0, detailCount: 2);
        var chain = CausalityTestData.Chain(ExportItemId, truncatedByDepth: false,
            CausalityTestData.Cohort(
                CausalEdgeType.PendingExportQueueingCausedExportExecution,
                reasonCode: CausalReasonCode.ExportUpdateStaged,
                connectedSystemId: 2, connectedSystemName: "Glitterband EMEA",
                members: CausalityTestData.Member("Liam Allen", SyncItemId,
                    CausalChainResolution.CauseNotRetained)));
        var model = CausalityModelBuilder.Build(item, CausalityTestData.ExportContext(), chain: chain);

        var lineage = CausalityLineageModelBuilder.Build(model, chain, ObjectChangeType.Exported);

        Assert.That(lineage.Columns, Has.Count.EqualTo(2));
        var identity = lineage.Columns[0];
        var target = lineage.Columns[1];
        var identityObject = Sole(identity);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(identity.Kind, Is.EqualTo(CausalityLineageColumnKind.Identity));
            Assert.That(identityObject.Cards, Has.Count.EqualTo(1), "the Identity's change is what caused an update export");
            Assert.That(identityObject.Endings.Select(e => e.Resolution),
                Is.EqualTo(new[] { CausalChainResolution.CauseNotRetained }));
            Assert.That(target.IsLit, Is.True);
            Assert.That(lineage.Joins.Select(j => j.Label), Is.EqualTo(new[] { "exported" }));
        }
    }

    [Test]
    public void Build_DeprovisionItem_DeletionDecisionRendersOnIdentityColumnFromSnapshots()
    {
        var item = new ActivityRunProfileExecutionItem { Id = ExportItemId };
        CausalityTestData.AddOutcome(item, ActivityRunProfileExecutionItemSyncOutcomeType.Deprovisioned,
            parent: null, ordinal: 0);
        var chain = CausalityTestData.Chain(ExportItemId, truncatedByDepth: false,
            CausalityTestData.Cohort(
                CausalEdgeType.PendingExportQueueingCausedExportExecution,
                reasonCode: CausalReasonCode.ExportDeleteStaged,
                connectedSystemId: 2, connectedSystemName: "Glitterband EMEA",
                members: CausalityTestData.Member("Erin Byrne", SyncItemId,
                    CausalChainResolution.CauseNotRetained)));
        var model = CausalityModelBuilder.Build(item, CausalityTestData.ExportContext(), chain: chain);

        var lineage = CausalityLineageModelBuilder.Build(model, chain, ObjectChangeType.Deprovisioned);

        Assert.That(lineage.Columns, Has.Count.EqualTo(2));
        var identity = lineage.Columns[0];
        var target = lineage.Columns[1];
        var identityObject = Sole(identity);
        var targetRecord = Sole(target);
        using (Assert.EnterMultipleScope())
        {
            // The deleted Identity's column is built entirely from the edge's snapshots.
            Assert.That(identity.Kind, Is.EqualTo(CausalityLineageColumnKind.Identity));
            Assert.That(identityObject.Title, Is.EqualTo("Erin Byrne"));
            Assert.That(identityObject.Cards, Has.Count.EqualTo(1));
            Assert.That(identityObject.Cards[0].Hop!.SentenceParts.Select(p => p.Text).First(),
                Does.Contain("was deleted"));
            Assert.That(target.IsLit, Is.True);
            Assert.That(targetRecord.Cards.Single(c => c.IsThisRun).Event!.OutcomeType,
                Is.EqualTo(ActivityRunProfileExecutionItemSyncOutcomeType.Deprovisioned));
        }
    }

    // ─── Cohorts, endings, role heads ───

    [Test]
    public void Build_CohortOfCauses_CollapsesToOneCardCarryingTheCount()
    {
        var item = new ActivityRunProfileExecutionItem { Id = ExportItemId };
        CausalityTestData.AddOutcome(item, ActivityRunProfileExecutionItemSyncOutcomeType.Exported,
            parent: null, ordinal: 0);
        var deletedUsers = Enumerable.Range(1, 10)
            .Select(i => CausalityTestData.Member($"User {i}", Guid.NewGuid(),
                CausalChainResolution.CauseNotRetained,
                occurred: CausalityTestData.ChainBaseTime.AddMinutes(i)))
            .ToArray();
        var chain = CausalityTestData.Chain(ExportItemId, truncatedByDepth: false,
            CausalityTestData.Cohort(
                CausalEdgeType.PendingExportQueueingCausedExportExecution,
                reasonCode: CausalReasonCode.ExportUpdateStaged,
                connectedSystemId: 2, connectedSystemName: "Glitterband EMEA",
                members: CausalityTestData.Member("Project Diamond", SyncItemId,
                    occurred: CausalityTestData.ChainBaseTime.AddMinutes(30),
                    causes: CausalityTestData.Cohort(
                        CausalEdgeType.MetaverseObjectDeletionCausedReferenceRemoval,
                        objectTypeName: "User", objectTypePluralName: "Users",
                        attributeName: "Static Members",
                        members: deletedUsers))));
        var model = CausalityModelBuilder.Build(item, CausalityTestData.ExportContext(), chain: chain);

        var lineage = CausalityLineageModelBuilder.Build(model, chain, ObjectChangeType.Exported);

        var identity = lineage.Columns.Single(c => c.Kind == CausalityLineageColumnKind.Identity);
        var identityObject = Sole(identity);
        using (Assert.EnterMultipleScope())
        {
            // One card per cohort, never one column or card per member.
            Assert.That(lineage.Columns, Has.Count.EqualTo(2));
            Assert.That(identityObject.Cards, Has.Count.EqualTo(2));
            var cohortCard = identityObject.Cards.Single(c => c.Hop?.Cohort.MemberCount == 10);
            Assert.That(cohortCard.Hop!.Members, Has.Count.EqualTo(10));
            Assert.That(cohortCard.Hop!.Members.Select(m => m.DisplayName), Does.Contain("User 3"));
            Assert.That(cohortCard.Hop!.Members.All(m => m.ActivityItemHref != null), Is.True);
            Assert.That(cohortCard.Hop!.ActivityItemHref, Is.Null, "a plural cohort links per member, not as a whole");

            // Ten identical member endings dedupe to one column footer.
            Assert.That(identityObject.Endings.Select(e => e.Resolution),
                Is.EqualTo(new[] { CausalChainResolution.CauseNotRetained }));

            // The two Identity cards read oldest first: the deletions preceded the staging.
            Assert.That(identityObject.Cards[0].Hop!.Cohort.MemberCount, Is.EqualTo(10));
            Assert.That(identityObject.Cards[1].Hop!.Cohort.MemberCount, Is.EqualTo(1));

            // The single-object head wins over the role: the story's subject Identity is named.
            Assert.That(identityObject.Title, Is.EqualTo("Project Diamond"));
            Assert.That(identityObject.IsRoleHead, Is.False);
        }
    }

    [Test]
    public void Build_IdentityColumnHoldingOnlyAPluralCohort_GetsARoleHead()
    {
        var item = new ActivityRunProfileExecutionItem { Id = ExportItemId };
        CausalityTestData.AddOutcome(item, ActivityRunProfileExecutionItemSyncOutcomeType.Exported,
            parent: null, ordinal: 0);
        var chain = CausalityTestData.Chain(ExportItemId, truncatedByDepth: false,
            CausalityTestData.Cohort(
                CausalEdgeType.MetaverseObjectDeletionCausedReferenceRemoval,
                objectTypeName: "User", objectTypePluralName: "Users",
                attributeName: "Static Members",
                members:
                [
                    CausalityTestData.Member("User 1", Guid.NewGuid(), CausalChainResolution.CauseNotRetained),
                    CausalityTestData.Member("User 2", Guid.NewGuid(), CausalChainResolution.CauseNotRetained)
                ]));
        var model = CausalityModelBuilder.Build(item, CausalityTestData.ExportContext(), chain: chain);

        var lineage = CausalityLineageModelBuilder.Build(model, chain, ObjectChangeType.Exported);

        var identity = lineage.Columns.Single(c => c.Kind == CausalityLineageColumnKind.Identity);
        var identityObject = Sole(identity);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(identityObject.Title, Is.EqualTo("Users"));
            Assert.That(identityObject.IsRoleHead, Is.True);
        }
    }

    [Test]
    public void Build_ChainEndings_RenderDistinctlyUnderTheirClosingColumns()
    {
        var item = new ActivityRunProfileExecutionItem { Id = ExportItemId };
        CausalityTestData.AddOutcome(item, ActivityRunProfileExecutionItemSyncOutcomeType.Exported,
            parent: null, ordinal: 0);
        var chain = CausalityTestData.Chain(ExportItemId, truncatedByDepth: true,
            CausalityTestData.Cohort(
                CausalEdgeType.PendingExportQueueingCausedExportExecution,
                reasonCode: CausalReasonCode.ExportUpdateStaged,
                connectedSystemId: 2, connectedSystemName: "Glitterband EMEA",
                members: CausalityTestData.Member("Liam Allen", SyncItemId,
                    occurred: CausalityTestData.ChainBaseTime.AddMinutes(10),
                    causes: CausalityTestData.Cohort(
                        default,
                        sourceImportChangeType: ObjectChangeType.Updated,
                        connectedSystemId: 1, connectedSystemName: "Yellowstone APAC",
                        members: CausalityTestData.Member("Liam Allen", ImportItemId,
                            CausalChainResolution.DepthLimitReached)))));
        var model = CausalityModelBuilder.Build(item, CausalityTestData.ExportContext(), chain: chain);

        var lineage = CausalityLineageModelBuilder.Build(model, chain, ObjectChangeType.Exported);

        var source = lineage.Columns.First(c =>
            c.Kind == CausalityLineageColumnKind.Record && c.Objects.Any(o => o.SystemName == "Yellowstone APAC"));
        var sourceRecord = Sole(source);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(sourceRecord.Endings.Single().Resolution, Is.EqualTo(CausalChainResolution.DepthLimitReached));
            Assert.That(sourceRecord.Endings.Single().Text, Is.EqualTo("More causes exist beyond this point"));
            Assert.That(lineage.IsTruncatedByDepth, Is.True);
        }
    }

    [Test]
    public void Build_UnknownEdgeType_PlacesTheHopOnATrailingColumnRatherThanDroppingIt()
    {
        var item = new ActivityRunProfileExecutionItem { Id = ExportItemId };
        CausalityTestData.AddOutcome(item, ActivityRunProfileExecutionItemSyncOutcomeType.Exported,
            parent: null, ordinal: 0);
        var chain = CausalityTestData.Chain(ExportItemId, truncatedByDepth: false,
            CausalityTestData.Cohort(
                (CausalEdgeType)99,
                members: CausalityTestData.Member("Mystery cause", Guid.NewGuid(),
                    CausalChainResolution.NoFurtherCauses)));
        var model = CausalityModelBuilder.Build(item, CausalityTestData.ExportContext(), chain: chain);

        var lineage = CausalityLineageModelBuilder.Build(model, chain, ObjectChangeType.Exported);

        var trailing = lineage.Columns[^1];
        var trailingObject = Sole(trailing);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(trailing.Kind, Is.EqualTo(CausalityLineageColumnKind.Unassigned));
            Assert.That(trailingObject.Cards, Has.Count.EqualTo(1));
            var chainCardCount = lineage.Columns.SelectMany(c => c.Objects)
                .Sum(o => o.Cards.Count(card => !card.IsThisRun));
            Assert.That(chainCardCount, Is.EqualTo(1), "nothing in the chain is ever silently omitted");
            Assert.That(lineage.Joins[^1].Label, Is.Null);
        }
    }

    // ─── Confirming imports, self-link suppression, sentence chaining ───

    [Test]
    public void Build_ConfirmingImport_ExportCauseLandsOnThePageRecordColumn()
    {
        var item = new ActivityRunProfileExecutionItem { Id = ImportItemId };
        CausalityTestData.AddOutcome(item, ActivityRunProfileExecutionItemSyncOutcomeType.ExportConfirmed,
            parent: null, ordinal: 0);
        var chain = CausalityTestData.Chain(ImportItemId, truncatedByDepth: false,
            CausalityTestData.Cohort(
                CausalEdgeType.ExportCausedImportConfirmation,
                connectedSystemId: 1, connectedSystemName: "Yellowstone APAC",
                members: CausalityTestData.Member("Liam Allen", ExportItemId,
                    CausalChainResolution.NoFurtherCauses)));
        var model = CausalityModelBuilder.Build(item, CausalityTestData.NewJoinerContext(), chain: chain);

        var lineage = CausalityLineageModelBuilder.Build(model, chain, ObjectChangeType.PendingExportConfirmed);

        Assert.That(lineage.Columns, Has.Count.EqualTo(1));
        var column = lineage.Columns[0];
        var record = Sole(column);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(column.Kind, Is.EqualTo(CausalityLineageColumnKind.Record));
            Assert.That(record.Cards, Has.Count.EqualTo(2));
            Assert.That(record.Cards[0].Hop!.RunKind, Is.EqualTo("Export run"));
            Assert.That(record.Cards[1].IsThisRun, Is.True);
            Assert.That(column.IsLit, Is.True);
        }
    }

    [Test]
    public void Build_CauseRecordedOnTheItemBeingViewed_GetsNoSelfLink()
    {
        var item = new ActivityRunProfileExecutionItem { Id = ExportItemId };
        CausalityTestData.AddOutcome(item, ActivityRunProfileExecutionItemSyncOutcomeType.Exported,
            parent: null, ordinal: 0);
        var chain = CausalityTestData.Chain(ExportItemId, truncatedByDepth: false,
            CausalityTestData.Cohort(
                CausalEdgeType.PendingExportQueueingCausedExportExecution,
                reasonCode: CausalReasonCode.ExportUpdateStaged,
                members: CausalityTestData.Member("Liam Allen", ExportItemId,
                    CausalChainResolution.NoFurtherCauses)));
        var model = CausalityModelBuilder.Build(item, CausalityTestData.ExportContext(), chain: chain);

        var lineage = CausalityLineageModelBuilder.Build(model, chain, ObjectChangeType.Exported);

        var hop = lineage.Columns.SelectMany(c => c.Objects).SelectMany(o => o.Cards).Single(c => !c.IsThisRun).Hop!;
        Assert.That(hop.ActivityItemHref, Is.Null, "a link back to the page being read is noise");
    }

    [Test]
    public void Build_NestedCauses_ChainSentencesThroughTheirEffectNames()
    {
        var item = new ActivityRunProfileExecutionItem { Id = ExportItemId };
        CausalityTestData.AddOutcome(item, ActivityRunProfileExecutionItemSyncOutcomeType.Exported,
            parent: null, ordinal: 0);
        var chain = CausalityTestData.Chain(ExportItemId, truncatedByDepth: false,
            CausalityTestData.Cohort(
                CausalEdgeType.PendingExportQueueingCausedExportExecution,
                reasonCode: CausalReasonCode.ExportUpdateStaged,
                members: CausalityTestData.Member("Project Diamond", SyncItemId,
                    occurred: CausalityTestData.ChainBaseTime.AddMinutes(20),
                    causes: CausalityTestData.Cohort(
                        CausalEdgeType.MetaverseObjectDeletionCausedReferenceRemoval,
                        objectTypeName: "User", objectTypePluralName: "Users",
                        attributeName: "Static Members",
                        members: CausalityTestData.Member("Aisha Khan", ImportItemId,
                            CausalChainResolution.CauseNotRetained)))));
        var model = CausalityModelBuilder.Build(item, CausalityTestData.ExportContext(), chain: chain);

        var lineage = CausalityLineageModelBuilder.Build(model, chain, ObjectChangeType.Exported);

        var identity = lineage.Columns.Single(c => c.Kind == CausalityLineageColumnKind.Identity);
        var removalHop = Sole(identity).Cards.Single(c => c.Hop?.Cohort.AttributeName == "Static Members").Hop!;
        var sentence = string.Concat(removalHop.SentenceParts.Select(p => p.Text));
        Assert.That(sentence, Does.Contain("Project Diamond"),
            "a nested hop's sentence names the object it acted on, exactly as the Caused by list did");
    }

    // ─── The empty degenerate case ───

    [Test]
    public void Build_NoOutcomesAndNoChain_ProducesThePageRecordColumnAlone()
    {
        var item = new ActivityRunProfileExecutionItem { Id = Guid.NewGuid() };
        var model = CausalityModelBuilder.Build(item, CausalityTestData.NewJoinerContext());

        var lineage = CausalityLineageModelBuilder.Build(model, chain: null, ObjectChangeType.NotSet);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(lineage.Columns, Has.Count.EqualTo(1));
            Assert.That(lineage.Columns[0].Kind, Is.EqualTo(CausalityLineageColumnKind.Record));
            Assert.That(lineage.Columns[0].IsLit, Is.False);
        }
    }

    // ─── Export decision captions (PRD requirement 6) ───

    [Test]
    public void Build_ExportedOutcomeWithCreateStagedReason_ReadsRecordCreated()
    {
        var item = new ActivityRunProfileExecutionItem { Id = ExportItemId };
        var outcome = CausalityTestData.AddOutcome(item,
            ActivityRunProfileExecutionItemSyncOutcomeType.Exported, parent: null, ordinal: 0);
        var chain = CausalityTestData.Chain(ExportItemId, truncatedByDepth: false,
            CausalityTestData.Cohort(
                CausalEdgeType.PendingExportQueueingCausedExportExecution,
                reasonCode: CausalReasonCode.ExportCreateStaged,
                effectSyncOutcomeId: outcome.Id,
                members: CausalityTestData.Member("Liam Allen", SyncItemId,
                    CausalChainResolution.NoFurtherCauses)));

        var model = CausalityModelBuilder.Build(item, CausalityTestData.ExportContext(), chain: chain);

        var exportedEvent = model.AllEvents().Single(e =>
            e.OutcomeType == ActivityRunProfileExecutionItemSyncOutcomeType.Exported);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(exportedEvent.PlainLabel, Is.EqualTo("Record created"));
            Assert.That(exportedEvent.Tone, Is.EqualTo(CausalityTone.Success));
        }
    }

    [Test]
    public void Build_ExportedOutcomeWithoutAChain_KeepsTheBareExportedLabel()
    {
        var item = new ActivityRunProfileExecutionItem { Id = ExportItemId };
        CausalityTestData.AddOutcome(item, ActivityRunProfileExecutionItemSyncOutcomeType.Exported,
            parent: null, ordinal: 0);

        var model = CausalityModelBuilder.Build(item, CausalityTestData.ExportContext());

        var exportedEvent = model.AllEvents().Single(e =>
            e.OutcomeType == ActivityRunProfileExecutionItemSyncOutcomeType.Exported);
        Assert.That(exportedEvent.PlainLabel, Is.EqualTo("Exported"));
    }

    [Test]
    public void Build_QueueingCohortWithNoEffectOutcomeId_StillSuppliesTheDecision()
    {
        var item = new ActivityRunProfileExecutionItem { Id = ExportItemId };
        CausalityTestData.AddOutcome(item, ActivityRunProfileExecutionItemSyncOutcomeType.Exported,
            parent: null, ordinal: 0);
        var chain = CausalityTestData.Chain(ExportItemId, truncatedByDepth: false,
            CausalityTestData.Cohort(
                CausalEdgeType.PendingExportQueueingCausedExportExecution,
                reasonCode: CausalReasonCode.ExportUpdateStaged,
                members: CausalityTestData.Member("Liam Allen", SyncItemId,
                    CausalChainResolution.NoFurtherCauses)));

        var model = CausalityModelBuilder.Build(item, CausalityTestData.ExportContext(), chain: chain);

        var exportedEvent = model.AllEvents().Single(e =>
            e.OutcomeType == ActivityRunProfileExecutionItemSyncOutcomeType.Exported);
        Assert.That(exportedEvent.PlainLabel, Is.EqualTo("Changes applied"));
    }
}
