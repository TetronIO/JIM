// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Linq;
using JIM.Models.Activities;
using JIM.Models.Activities.DTOs;
using JIM.Models.Enums;
using JIM.Web.Causality;
using NUnit.Framework;
using static JIM.Web.Tests.CausalityConsequenceTestData;

namespace JIM.Web.Tests;

/// <summary>
/// The Lineage's link policy (#2100): every address the Lineage writes for an execution item, a Connected System Object
/// or a Metaverse Object comes from one <see cref="ICausalityLinks"/>, so the Causality Explorer can render the same
/// Lineage with addresses that re-centre it instead of leaving the page.
///
/// Two things are pinned. The item page's own policy writes exactly the addresses the Lineage wrote before the policy
/// existed, so the item page cannot change. And no builder formats one of those addresses for itself: a policy whose
/// addresses no page writes reaches every one of them.
/// </summary>
[TestFixture]
public class CausalityLinkPolicyTests
{
    private static readonly Guid ItemId = Guid.Parse("55555555-5555-5555-5555-555555555555");
    private static readonly Guid ImportItemId = Guid.Parse("66666666-6666-6666-6666-666666666666");
    private static readonly Guid ExportItemId = Guid.Parse("77777777-7777-7777-7777-777777777777");

    // ─── The item page's own addresses ───

    [Test]
    public void PageLinks_Item_IsTheItemPage()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(CausalityPageLinks.Instance.ItemHref(ItemId), Is.EqualTo($"/activity/item/{ItemId}"));
            Assert.That(CausalityPageLinks.Instance.ItemLinkText, Is.EqualTo("View activity item"));
        }
    }

    [Test]
    public void PageLinks_ConnectedSystemObject_IsItsConnectorSpacePage()
    {
        Assert.That(CausalityPageLinks.Instance.ConnectedSystemObjectHref(2, CausalityTestData.CsoId),
            Is.EqualTo($"/admin/connected-systems/2/connector-space/{CausalityTestData.CsoId}"));
    }

    [Test]
    public void PageLinks_MetaverseObject_IsItsPageUnderItsTypesPluralName()
    {
        Assert.That(CausalityPageLinks.Instance.MetaverseObjectHref(CausalityTestData.MvoId, "People"),
            Is.EqualTo($"/t/people/v/{CausalityTestData.MvoId}"));
    }

    /// <summary>
    /// The Metaverse Object page is addressed by its type's plural name, so without one there is no address to write,
    /// and the Lineage names the object unlinked rather than inventing a route.
    /// </summary>
    [TestCase(null)]
    [TestCase("")]
    [TestCase("  ")]
    public void PageLinks_MetaverseObjectWithoutATypePluralName_HasNoAddress(string? pluralName)
    {
        Assert.That(CausalityPageLinks.Instance.MetaverseObjectHref(CausalityTestData.MvoId, pluralName), Is.Null);
    }

    [Test]
    public void PageContext_WithoutAPolicy_UsesThePagesOwnAddresses()
    {
        Assert.That(CausalityTestData.NewJoinerContext().Links, Is.SameAs(CausalityPageLinks.Instance));
    }

    // ─── The causality model's object links ───

    [Test]
    public void ModelBuild_RecordedItem_LinksItsObjectsThroughThePolicy()
    {
        var context = CausalityTestData.NewJoinerContext() with { Links = TestCausalityLinks.Instance };

        var model = CausalityModelBuilder.Build(CausalityTestData.NewJoinerItem(), context);

        var identityLink = model.Roots[0].Links.Single(l => l.Kind == CausalityEntityKind.Identity);
        var recordLink = model.Roots[0].Children[0].Children[0].Links.Single(l => l.Kind == CausalityEntityKind.Record);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(identityLink.Href, Is.EqualTo($"test:mvo:{CausalityTestData.MvoId}"));
            Assert.That(recordLink.Href, Is.EqualTo($"test:cso:2:{CausalityTestData.ProvisionedCsoId}"));
        }
    }

    [Test]
    public void ModelBuild_DisconnectedAccount_LinksItThroughThePolicy()
    {
        var context = CausalityTestData.ExportScopeReviewContext() with { Links = TestCausalityLinks.Instance };

        var model = CausalityModelBuilder.Build(CausalityTestData.ExportScopeReviewItem(), context);

        var recordLink = model.Roots[0].Links.Single(l => l.Kind == CausalityEntityKind.Record);
        Assert.That(recordLink.Href, Is.EqualTo($"test:cso:2:{CausalityTestData.ProvisionedCsoId}"));
    }

    // ─── The page's own record, wherever the panel names it ───

    /// <summary>
    /// The summary band sits above the Lineage on both hosts, so the record it names is followed under the same policy.
    /// </summary>
    [Test]
    public void SummaryBuild_PageRecord_LinksThroughThePolicy()
    {
        var context = CausalityTestData.NewJoinerContext() with { Links = TestCausalityLinks.Instance };
        var model = CausalityModelBuilder.Build(CausalityTestData.NewJoinerItem(), context);

        var summary = CausalitySummaryBuilder.Build(model);

        var record = summary.Segments.OfType<SummarySegment.Entity>().Single(e => e.Kind == CausalityEntityKind.Record);
        Assert.That(record.Href, Is.EqualTo($"test:cso:1:{CausalityTestData.CsoId}"));
    }

    [Test]
    public void TableBuild_SourceObject_LinksThroughThePolicy()
    {
        var context = CausalityTestData.NewJoinerContext() with { Links = TestCausalityLinks.Instance };
        var model = CausalityModelBuilder.Build(CausalityTestData.NewJoinerItem(), context);

        var table = CausalityTableModelBuilder.Build(model);

        var source = table.Objects.Single(o => o.Role == CausalityTableObjectRole.Source);
        Assert.That(source.Href, Is.EqualTo($"test:cso:1:{CausalityTestData.CsoId}"));
    }

    // ─── The Lineage's own addresses ───

    [Test]
    public void LineageBuild_PageRecordAndChainHops_LinkThroughThePolicy()
    {
        var (model, chain) = ExportCreateStory(TestCausalityLinks.Instance);

        var lineage = CausalityLineageModelBuilder.Build(model, chain, ObjectChangeType.Exported);

        var sourceRecord = lineage.Columns[0].Objects.Single();
        var targetRecord = lineage.Columns[^1].Objects.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(sourceRecord.Cards[0].Hop!.ActivityItemHref, Is.EqualTo($"test:item:{ImportItemId}"));
            Assert.That(targetRecord.Cards[0].Hop!.ActivityItemHref, Is.EqualTo($"test:item:{ItemId}"));
            Assert.That(targetRecord.Href, Is.EqualTo($"test:cso:2:{CausalityTestData.CsoId}"), "the page's own record");
        }
    }

    [Test]
    public void LineageBuild_PluralCohort_LinksEachMemberThroughThePolicy()
    {
        var item = new ActivityRunProfileExecutionItem { Id = ExportItemId };
        CausalityTestData.AddOutcome(item, ActivityRunProfileExecutionItemSyncOutcomeType.Exported, parent: null, ordinal: 0);
        var memberItemIds = Enumerable.Range(0, 3).Select(_ => Guid.NewGuid()).ToList();
        var chain = CausalityTestData.Chain(ExportItemId, truncatedByDepth: false,
            CausalityTestData.Cohort(
                CausalEdgeType.MetaverseObjectDeletionCausedReferenceRemoval,
                objectTypeName: "User", objectTypePluralName: "Users", attributeName: "Static Members",
                members: memberItemIds.Select((id, i) => CausalityTestData.Member($"User {i}", id,
                    CausalChainResolution.CauseNotRetained, occurred: CausalityTestData.ChainBaseTime)).ToArray()));
        var context = CausalityTestData.ExportContext() with { Links = TestCausalityLinks.Instance };
        var model = CausalityModelBuilder.Build(item, context, chain: chain);

        var lineage = CausalityLineageModelBuilder.Build(model, chain, ObjectChangeType.Exported);

        var hop = lineage.Columns.SelectMany(c => c.Objects).SelectMany(o => o.Cards).Single(c => c.Hop != null).Hop!;
        Assert.That(hop.Members.Select(m => m.ActivityItemHref), Is.EqualTo(memberItemIds.Select(id => $"test:item:{id}")));
    }

    /// <summary>
    /// The policy decides whether a Metaverse Object can be addressed, not the builder: an address that needs no type
    /// name must be written even where the page could not resolve one.
    /// </summary>
    [Test]
    public void LineageBuild_SubjectMetaverseObject_LinksThroughThePolicyEvenWithoutATypePluralName()
    {
        var context = CausalityTestData.ExportScopeReviewContext() with
        {
            MvoTypePluralName = null,
            Links = TestCausalityLinks.Instance
        };
        var model = CausalityModelBuilder.Build(CausalityTestData.ExportScopeReviewItem(), context);

        var lineage = CausalityLineageModelBuilder.Build(model, chain: null, ObjectChangeType.ExportScopeReview);

        Assert.That(lineage.Columns[0].Objects.Single().Href, Is.EqualTo($"test:mvo:{CausalityTestData.MvoId}"));
    }

    [Test]
    public void LineageBuild_SubjectMetaverseObjectOnTheItemPageWithoutATypePluralName_IsUnlinked()
    {
        var context = CausalityTestData.ExportScopeReviewContext() with { MvoTypePluralName = null };
        var model = CausalityModelBuilder.Build(CausalityTestData.ExportScopeReviewItem(), context);

        var lineage = CausalityLineageModelBuilder.Build(model, chain: null, ObjectChangeType.ExportScopeReview);

        Assert.That(lineage.Columns[0].Objects.Single().Href, Is.Null);
    }

    [Test]
    public void LineageBuild_Consequences_LinkTheirItemsAndTheObjectsTheyNameThroughThePolicy()
    {
        var lineage = BuildImportLineage(CompleteCascade(), links: TestCausalityLinks.Instance);

        var account = lineage.Columns[2].Objects[0];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(account.Consequences[1].ActivityItemHref, Does.StartWith("test:item:"));
            Assert.That(account.Href, Is.EqualTo($"test:cso:{Glitterband}:{AccountCsoId}"));
        }
    }

    /// <summary>
    /// What a later run did is built from that run's own item, under a context of its own; the policy has to reach it
    /// there too, or every link a later run's card carries would still leave the explorer for the item page.
    /// </summary>
    [Test]
    public void LineageBuild_LaterRunsOwnEvents_LinkTheirObjectsThroughThePolicy()
    {
        var lineage = BuildImportLineage(Consequences(SynchronisationCohort(disconnect: true)), links: TestCausalityLinks.Instance);

        var accountLink = lineage.Columns.SelectMany(c => c.Objects).SelectMany(o => o.Consequences)
            .Where(c => c.Event?.OutcomeType == ActivityRunProfileExecutionItemSyncOutcomeType.TargetDisconnected)
            .SelectMany(c => c.Event!.Links)
            .Single(l => l.Kind == CausalityEntityKind.Record);
        Assert.That(accountLink.Href, Is.EqualTo($"test:cso:{Glitterband}:{AccountCsoId}"));
    }

    /// <summary>
    /// The view writes some addresses itself (a cohort's members, paged on request), so the Lineage carries the policy
    /// its own addresses were written with, and the two can never disagree.
    /// </summary>
    [Test]
    public void LineageBuild_CarriesThePolicyItsAddressesWereWrittenWith()
    {
        var (explorerModel, explorerChain) = ExportCreateStory(TestCausalityLinks.Instance);
        var (pageModel, pageChain) = ExportCreateStory(links: null);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(CausalityLineageModelBuilder.Build(explorerModel, explorerChain, ObjectChangeType.Exported).Links,
                Is.SameAs(TestCausalityLinks.Instance));
            Assert.That(CausalityLineageModelBuilder.Build(pageModel, pageChain, ObjectChangeType.Exported).Links,
                Is.SameAs(CausalityPageLinks.Instance));
        }
    }

    /// <summary>
    /// The create-export story: the import that brought the record in, and the synchronisation that staged its
    /// provisioning, behind this run's export.
    /// </summary>
    internal static (CausalityModel Model, CausalChain Chain) ExportCreateStory(ICausalityLinks? links)
    {
        var item = new ActivityRunProfileExecutionItem { Id = ExportItemId };
        CausalityTestData.AddOutcome(item, ActivityRunProfileExecutionItemSyncOutcomeType.Exported,
            parent: null, ordinal: 0, detailCount: 11);
        var chain = CausalityTestData.Chain(ExportItemId, truncatedByDepth: false,
            CausalityTestData.Cohort(
                CausalEdgeType.PendingExportQueueingCausedExportExecution,
                reasonCode: CausalReasonCode.ExportCreateStaged,
                connectedSystemId: 2, connectedSystemName: "Glitterband EMEA",
                members: CausalityTestData.Member("Liam Allen", ItemId,
                    occurred: CausalityTestData.ChainBaseTime.AddMinutes(10),
                    causes: CausalityTestData.Cohort(
                        default,
                        sourceImportChangeType: ObjectChangeType.Added,
                        connectedSystemId: 1, connectedSystemName: "Yellowstone APAC",
                        members: CausalityTestData.Member("Liam Allen", ImportItemId,
                            CausalChainResolution.NoFurtherCauses,
                            occurred: CausalityTestData.ChainBaseTime)))));
        var context = links == null ? CausalityTestData.ExportContext() : CausalityTestData.ExportContext() with { Links = links };
        return (CausalityModelBuilder.Build(item, context, chain: chain), chain);
    }
}
