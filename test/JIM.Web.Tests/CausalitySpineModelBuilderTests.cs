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
/// Tests for <see cref="CausalitySpineModelBuilder"/>: the projection of a Run Profile Execution
/// Item's causality model and causal chain onto the object spine's columns (#1495). Covers column
/// derivation and order per item type, lit-column selection, chain-hop assignment (including the
/// derived source-import hop and the deprovision chain built from snapshots), cohort collapse, the
/// three endings, join labels and role heads.
/// </summary>
[TestFixture]
public class CausalitySpineModelBuilderTests
{
    private static readonly Guid SyncItemId = Guid.Parse("55555555-5555-5555-5555-555555555555");
    private static readonly Guid ImportItemId = Guid.Parse("66666666-6666-6666-6666-666666666666");
    private static readonly Guid ExportItemId = Guid.Parse("77777777-7777-7777-7777-777777777777");

    // ─── Column derivation and lit columns per item type ───

    [Test]
    public void Build_ImportItem_LightsTheSourceRecordColumnOnly()
    {
        var item = new ActivityRunProfileExecutionItem { Id = Guid.NewGuid() };
        CausalityTestData.AddOutcome(item, ActivityRunProfileExecutionItemSyncOutcomeType.CsoAdded,
            parent: null, ordinal: 0);
        var model = CausalityModelBuilder.Build(item, CausalityTestData.NewJoinerContext());

        var spine = CausalitySpineModelBuilder.Build(model, chain: null, ObjectChangeType.Added);

        Assert.That(spine.Columns, Has.Count.EqualTo(1));
        var column = spine.Columns[0];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(column.Kind, Is.EqualTo(CausalitySpineColumnKind.Record));
            Assert.That(column.Title, Is.EqualTo("Liam Allen"));
            Assert.That(column.SystemName, Is.EqualTo("Yellowstone APAC"));
            Assert.That(column.ObjectTypeName, Is.EqualTo("person"));
            Assert.That(column.IsLit, Is.True);
            Assert.That(column.Cards, Has.Count.EqualTo(1));
            Assert.That(column.Cards[0].IsThisRun, Is.True);
            Assert.That(spine.Joins, Is.Empty);
        }
    }

    [Test]
    public void Build_SyncNewJoinerItem_BuildsSourceIdentityAndTargetColumnsInOrder()
    {
        var item = CausalityTestData.NewJoinerItem();
        var model = CausalityModelBuilder.Build(item, CausalityTestData.NewJoinerContext());

        var spine = CausalitySpineModelBuilder.Build(model, chain: null, ObjectChangeType.Projected);

        Assert.That(spine.Columns, Has.Count.EqualTo(3));
        var source = spine.Columns[0];
        var identity = spine.Columns[1];
        var target = spine.Columns[2];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(source.Kind, Is.EqualTo(CausalitySpineColumnKind.Record));
            Assert.That(source.SystemName, Is.EqualTo("Yellowstone APAC"));
            Assert.That(source.IsLit, Is.False, "the sync run's events happen to the Identity and the target, not the source record");

            Assert.That(identity.Kind, Is.EqualTo(CausalitySpineColumnKind.Identity));
            Assert.That(identity.Title, Is.EqualTo("Liam Allen"));
            Assert.That(identity.ObjectTypeName, Is.EqualTo("Person"));
            Assert.That(identity.IsLit, Is.True);
            Assert.That(identity.Cards.Select(c => c.Event!.OutcomeType), Is.EqualTo(new[]
            {
                ActivityRunProfileExecutionItemSyncOutcomeType.Projected,
                ActivityRunProfileExecutionItemSyncOutcomeType.AttributeFlow
            }));

            Assert.That(target.Kind, Is.EqualTo(CausalitySpineColumnKind.Record));
            Assert.That(target.SystemName, Is.EqualTo("Glitterband EMEA"));
            Assert.That(target.Title, Is.EqualTo("Liam Allen"));
            Assert.That(target.IsLit, Is.True);
            Assert.That(target.Cards.Select(c => c.Event!.OutcomeType), Is.EqualTo(new[]
            {
                ActivityRunProfileExecutionItemSyncOutcomeType.Provisioned,
                ActivityRunProfileExecutionItemSyncOutcomeType.PendingExportCreated
            }));

            Assert.That(spine.Joins.Select(j => j.Label), Is.EqualTo(new[] { "projected", "provisioned" }));
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

        var spine = CausalitySpineModelBuilder.Build(model, chain, ObjectChangeType.Exported);

        Assert.That(spine.Columns, Has.Count.EqualTo(3));
        var source = spine.Columns[0];
        var identity = spine.Columns[1];
        var target = spine.Columns[2];
        using (Assert.EnterMultipleScope())
        {
            // The source record column comes from the derived source-import hop's snapshot.
            Assert.That(source.Kind, Is.EqualTo(CausalitySpineColumnKind.Record));
            Assert.That(source.SystemName, Is.EqualTo("Yellowstone APAC"));
            Assert.That(source.Title, Is.EqualTo("Liam Allen"));
            Assert.That(source.IsLit, Is.False);
            Assert.That(source.Cards, Has.Count.EqualTo(1));
            Assert.That(source.Cards[0].IsThisRun, Is.False);
            Assert.That(source.Cards[0].Hop!.RunKind, Is.EqualTo("Import run"));
            Assert.That(source.Cards[0].Hop!.ActivityItemHref, Is.EqualTo($"/activity/item/{ImportItemId}"));
            Assert.That(source.Endings.Select(e => e.Resolution),
                Is.EqualTo(new[] { CausalChainResolution.NoFurtherCauses }));

            // The Identity column exists because the graph never joins two records directly, even
            // though no loaded event happened to it on a create export.
            Assert.That(identity.Kind, Is.EqualTo(CausalitySpineColumnKind.Identity));
            Assert.That(identity.Title, Is.EqualTo("Liam Allen"));
            Assert.That(identity.Cards, Is.Empty);

            // The provisioning decision lands on the record it created; this run's export follows it.
            Assert.That(target.Kind, Is.EqualTo(CausalitySpineColumnKind.Record));
            Assert.That(target.SystemName, Is.EqualTo("Glitterband EMEA"));
            Assert.That(target.IsLit, Is.True);
            Assert.That(target.Cards, Has.Count.EqualTo(2));
            Assert.That(target.Cards[0].IsThisRun, Is.False, "the chain card is older, so it renders before this run's");
            Assert.That(target.Cards[0].Hop!.RunKind, Is.EqualTo("Synchronisation run"));
            Assert.That(target.Cards[0].Hop!.ActivityItemHref, Is.EqualTo($"/activity/item/{SyncItemId}"));
            Assert.That(target.Cards[1].IsThisRun, Is.True);

            Assert.That(spine.Joins.Select(j => j.Label), Is.EqualTo(new[] { "imported", "provisioned" }));
            Assert.That(spine.IsTruncatedByDepth, Is.False);
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

        var spine = CausalitySpineModelBuilder.Build(model, chain, ObjectChangeType.Exported);

        Assert.That(spine.Columns, Has.Count.EqualTo(2));
        var identity = spine.Columns[0];
        var target = spine.Columns[1];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(identity.Kind, Is.EqualTo(CausalitySpineColumnKind.Identity));
            Assert.That(identity.Cards, Has.Count.EqualTo(1), "the Identity's change is what caused an update export");
            Assert.That(identity.Endings.Select(e => e.Resolution),
                Is.EqualTo(new[] { CausalChainResolution.CauseNotRetained }));
            Assert.That(target.IsLit, Is.True);
            Assert.That(spine.Joins.Select(j => j.Label), Is.EqualTo(new[] { "exported" }));
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

        var spine = CausalitySpineModelBuilder.Build(model, chain, ObjectChangeType.Deprovisioned);

        Assert.That(spine.Columns, Has.Count.EqualTo(2));
        var identity = spine.Columns[0];
        var target = spine.Columns[1];
        using (Assert.EnterMultipleScope())
        {
            // The deleted Identity's column is built entirely from the edge's snapshots.
            Assert.That(identity.Kind, Is.EqualTo(CausalitySpineColumnKind.Identity));
            Assert.That(identity.Title, Is.EqualTo("Erin Byrne"));
            Assert.That(identity.Cards, Has.Count.EqualTo(1));
            Assert.That(identity.Cards[0].Hop!.SentenceParts.Select(p => p.Text).First(),
                Does.Contain("was deleted"));
            Assert.That(target.IsLit, Is.True);
            Assert.That(target.Cards.Single(c => c.IsThisRun).Event!.OutcomeType,
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

        var spine = CausalitySpineModelBuilder.Build(model, chain, ObjectChangeType.Exported);

        var identity = spine.Columns.Single(c => c.Kind == CausalitySpineColumnKind.Identity);
        using (Assert.EnterMultipleScope())
        {
            // One card per cohort, never one column or card per member.
            Assert.That(spine.Columns, Has.Count.EqualTo(2));
            Assert.That(identity.Cards, Has.Count.EqualTo(2));
            var cohortCard = identity.Cards.Single(c => c.Hop?.Cohort.MemberCount == 10);
            Assert.That(cohortCard.Hop!.Members, Has.Count.EqualTo(10));
            Assert.That(cohortCard.Hop!.Members.Select(m => m.DisplayName), Does.Contain("User 3"));
            Assert.That(cohortCard.Hop!.Members.All(m => m.ActivityItemHref != null), Is.True);
            Assert.That(cohortCard.Hop!.ActivityItemHref, Is.Null, "a plural cohort links per member, not as a whole");

            // Ten identical member endings dedupe to one column footer.
            Assert.That(identity.Endings.Select(e => e.Resolution),
                Is.EqualTo(new[] { CausalChainResolution.CauseNotRetained }));

            // The two Identity cards read oldest first: the deletions preceded the staging.
            Assert.That(identity.Cards[0].Hop!.Cohort.MemberCount, Is.EqualTo(10));
            Assert.That(identity.Cards[1].Hop!.Cohort.MemberCount, Is.EqualTo(1));

            // The single-object head wins over the role: the story's subject Identity is named.
            Assert.That(identity.Title, Is.EqualTo("Project Diamond"));
            Assert.That(identity.IsRoleHead, Is.False);
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

        var spine = CausalitySpineModelBuilder.Build(model, chain, ObjectChangeType.Exported);

        var identity = spine.Columns.Single(c => c.Kind == CausalitySpineColumnKind.Identity);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(identity.Title, Is.EqualTo("Users"));
            Assert.That(identity.IsRoleHead, Is.True);
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

        var spine = CausalitySpineModelBuilder.Build(model, chain, ObjectChangeType.Exported);

        var source = spine.Columns.First(c => c.Kind == CausalitySpineColumnKind.Record && c.SystemName == "Yellowstone APAC");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(source.Endings.Single().Resolution, Is.EqualTo(CausalChainResolution.DepthLimitReached));
            Assert.That(source.Endings.Single().Text, Is.EqualTo("More causes exist beyond this point"));
            Assert.That(spine.IsTruncatedByDepth, Is.True);
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

        var spine = CausalitySpineModelBuilder.Build(model, chain, ObjectChangeType.Exported);

        var trailing = spine.Columns[^1];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(trailing.Kind, Is.EqualTo(CausalitySpineColumnKind.Unassigned));
            Assert.That(trailing.Cards, Has.Count.EqualTo(1));
            var chainCardCount = spine.Columns.Sum(c => c.Cards.Count(card => !card.IsThisRun));
            Assert.That(chainCardCount, Is.EqualTo(1), "nothing in the chain is ever silently omitted");
            Assert.That(spine.Joins[^1].Label, Is.Null);
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

        var spine = CausalitySpineModelBuilder.Build(model, chain, ObjectChangeType.PendingExportConfirmed);

        Assert.That(spine.Columns, Has.Count.EqualTo(1));
        var record = spine.Columns[0];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(record.Kind, Is.EqualTo(CausalitySpineColumnKind.Record));
            Assert.That(record.Cards, Has.Count.EqualTo(2));
            Assert.That(record.Cards[0].Hop!.RunKind, Is.EqualTo("Export run"));
            Assert.That(record.Cards[1].IsThisRun, Is.True);
            Assert.That(record.IsLit, Is.True);
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

        var spine = CausalitySpineModelBuilder.Build(model, chain, ObjectChangeType.Exported);

        var hop = spine.Columns.SelectMany(c => c.Cards).Single(c => !c.IsThisRun).Hop!;
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

        var spine = CausalitySpineModelBuilder.Build(model, chain, ObjectChangeType.Exported);

        var identity = spine.Columns.Single(c => c.Kind == CausalitySpineColumnKind.Identity);
        var removalHop = identity.Cards.Single(c => c.Hop?.Cohort.AttributeName == "Static Members").Hop!;
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

        var spine = CausalitySpineModelBuilder.Build(model, chain: null, ObjectChangeType.NotSet);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(spine.Columns, Has.Count.EqualTo(1));
            Assert.That(spine.Columns[0].Kind, Is.EqualTo(CausalitySpineColumnKind.Record));
            Assert.That(spine.Columns[0].IsLit, Is.False);
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
