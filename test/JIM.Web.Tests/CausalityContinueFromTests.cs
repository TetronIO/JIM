// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Linq;
using Bunit;
using JIM.Models.Activities;
using JIM.Models.Activities.DTOs;
using JIM.Models.Enums;
using JIM.Web.Causality;
using JIM.Web.Shared.Causality;
using NUnit.Framework;
using static JIM.Web.Tests.CausalityConsequenceTestData;

namespace JIM.Web.Tests;

/// <summary>
/// Where the walk stops at its bound (#2100), the Lineage offers to carry on from the farthest item it reached, which
/// shows the next levels from there. The walk is never widened to follow a long chain; it is continued from its far end.
/// A cohort of several items already links each of its members, so only a single item gets a link of its own.
/// </summary>
[TestFixture]
public class CausalityContinueFromTests : JimComponentTestContext
{
    private static readonly Guid ExportItemId = Guid.Parse("77777777-7777-7777-7777-777777777777");
    private static readonly Guid SyncItemId = Guid.Parse("55555555-5555-5555-5555-555555555555");
    private static readonly Guid ImportItemId = Guid.Parse("66666666-6666-6666-6666-666666666666");

    // ─── Upwards: the depth bound on causes ───

    [Test]
    public void Build_SoleCauseAtTheDepthBound_OffersToContinueFromIt()
    {
        var lineage = UpwardLineage(CausalityTestData.Member("Liam Allen", ImportItemId, CausalChainResolution.DepthLimitReached));

        var ending = lineage.Columns.SelectMany(c => c.Objects).SelectMany(o => o.Endings)
            .Single(e => e.Resolution == CausalChainResolution.DepthLimitReached);
        Assert.That(ending.Continuations.Select(c => (c.Text, c.Href)),
            Is.EqualTo(new[] { ("Continue from Liam Allen", $"test:item:{ImportItemId}") }));
    }

    [Test]
    public void Build_SeveralCausesAtTheDepthBound_LeaveEachToItsOwnLink()
    {
        var lineage = UpwardLineage(
            CausalityTestData.Member("Liam Allen", ImportItemId, CausalChainResolution.DepthLimitReached),
            CausalityTestData.Member("Owen Hart", Guid.NewGuid(), CausalChainResolution.DepthLimitReached));

        var ending = lineage.Columns.SelectMany(c => c.Objects).SelectMany(o => o.Endings)
            .Single(e => e.Resolution == CausalChainResolution.DepthLimitReached);
        Assert.That(ending.Continuations, Is.Empty);
    }

    [Test]
    public void Build_OtherEndings_OfferNothingToContinueFrom()
    {
        var lineage = UpwardLineage(CausalityTestData.Member("Liam Allen", ImportItemId, CausalChainResolution.NoFurtherCauses));

        Assert.That(lineage.Columns.SelectMany(c => c.Objects).SelectMany(o => o.Endings).SelectMany(e => e.Continuations), Is.Empty);
    }

    [Test]
    public void Render_SoleCauseAtTheDepthBound_LinksTheContinuation()
    {
        var lineage = UpwardLineage(CausalityTestData.Member("Liam Allen", ImportItemId, CausalChainResolution.DepthLimitReached));

        var cut = Render<CausalityLineageView>(ps => ps.Add(c => c.Model, lineage));

        var link = cut.Find(".ln-end a.ln-link");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(link.TextContent.Trim(), Is.EqualTo("Continue from Liam Allen"));
            Assert.That(link.GetAttribute("href"), Is.EqualTo($"test:item:{ImportItemId}"));
        }
    }

    // ─── Downwards: a branch that goes further than shown ───

    [Test]
    public void Build_SoleConsequencePastTheBound_OffersToContinueFromIt()
    {
        var synchronisation = SynchronisationCohort();
        synchronisation.IsFurtherThanShown = true;

        var lineage = BuildImportLineage(Consequences(synchronisation, truncatedByDepth: true), links: TestCausalityLinks.Instance);

        var further = FurtherThanShown(lineage);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(further.LinkText, Is.EqualTo("Continue from Tina Adams"));
            Assert.That(further.LinkHref, Is.EqualTo($"test:item:{synchronisation.Representative!.Id}"));
        }
    }

    [Test]
    public void Build_SeveralConsequencesPastTheBound_LeaveEachToItsOwnLink()
    {
        var removals = CompleteCascade().Cohorts[0].Consequences
            .Single(c => c.EdgeType == CausalEdgeType.MetaverseObjectDeletionCausedReferenceRemoval);
        removals.Consequences.Clear();
        removals.IsFurtherThanShown = true;

        var lineage = BuildImportLineage(Consequences(removals, truncatedByDepth: true), links: TestCausalityLinks.Instance);

        var further = FurtherThanShown(lineage);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(further.LinkHref, Is.Null);
            Assert.That(further.Text, Does.Contain("own link"));
        }
    }

    private static CausalityLineageConsequence FurtherThanShown(CausalityLineageModel lineage) =>
        lineage.Columns.SelectMany(c => c.Objects).SelectMany(o => o.Consequences)
            .Single(c => c.Kind == CausalityLineageConsequenceKind.FurtherThanShown);

    /// <summary>
    /// An export whose chain runs back through the synchronisation that staged it to the import that fed it, where the
    /// walk stops with <paramref name="importMembers"/>.
    /// </summary>
    private static CausalityLineageModel UpwardLineage(params CausalChainMember[] importMembers)
    {
        var item = new ActivityRunProfileExecutionItem { Id = ExportItemId };
        CausalityTestData.AddOutcome(item, ActivityRunProfileExecutionItemSyncOutcomeType.Exported, parent: null, ordinal: 0);
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
                        members: importMembers))));
        var context = CausalityTestData.ExportContext() with { Links = TestCausalityLinks.Instance };
        var model = CausalityModelBuilder.Build(item, context, chain: chain);
        return CausalityLineageModelBuilder.Build(model, chain, ObjectChangeType.Exported);
    }
}
