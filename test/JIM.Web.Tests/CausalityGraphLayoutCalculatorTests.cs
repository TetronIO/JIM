// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Linq;
using JIM.Models.Activities;
using JIM.Web.Causality;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// Unit tests for <see cref="CausalityGraphLayoutCalculator"/>: the layered node-link layout ported
/// from the approved mock-up's renderGraph algorithm. Pins node/edge counts and exact coordinates
/// for the three PRD scenarios, parent-centring over children, synthetic source root placement,
/// deep-chain layout, overall width/height, bezier edge paths (invariant culture) and the title/sub
/// truncation boundaries.
/// </summary>
[TestFixture]
public class CausalityGraphLayoutCalculatorTests
{
    private static CausalityGraphLayout ComputeNewJoiner(bool technicalNames = false)
    {
        var model = CausalityModelBuilder.Build(
            CausalityTestData.NewJoinerItem(), CausalityTestData.NewJoinerContext());
        return CausalityGraphLayoutCalculator.Compute(model, technicalNames);
    }

    private static CausalityGraphLayout ComputeLeaver()
    {
        var model = CausalityModelBuilder.Build(
            CausalityTestData.LeaverItem(), CausalityTestData.NewJoinerContext());
        return CausalityGraphLayoutCalculator.Compute(model, technicalNames: false);
    }

    private static CausalityGraphLayout ComputeExportFailure()
    {
        var model = CausalityModelBuilder.Build(
            CausalityTestData.ExportFailureItem(), CausalityTestData.ExportContext());
        return CausalityGraphLayoutCalculator.Compute(model, technicalNames: false);
    }

    private static CausalityGraphNode NodeById(CausalityGraphLayout layout, string id)
    {
        return layout.Nodes.Single(n => n.Id == id);
    }

    [Test]
    public void Compute_NewJoinerChain_ProducesOneNodePerEventPlusTheSourceRoot()
    {
        var layout = ComputeNewJoiner();

        Assert.That(layout.Nodes, Has.Count.EqualTo(5));
        Assert.That(layout.Edges, Has.Count.EqualTo(4));
        Assert.That(layout.Nodes.Select(n => n.Id),
            Is.EquivalentTo(new[] { "src", "evt-0", "evt-1", "evt-2", "evt-3" }));
    }

    [Test]
    public void Compute_NewJoinerChain_AssignsDepthAsTheXColumn()
    {
        var layout = ComputeNewJoiner();

        // x = depth * (280 + 70) + 2; the chain occupies one column per depth
        Assert.That(NodeById(layout, "src").Depth, Is.EqualTo(0));
        Assert.That(NodeById(layout, "src").X, Is.EqualTo(2));
        Assert.That(NodeById(layout, "evt-0").X, Is.EqualTo(352));
        Assert.That(NodeById(layout, "evt-1").X, Is.EqualTo(702));
        Assert.That(NodeById(layout, "evt-2").X, Is.EqualTo(1052));
        Assert.That(NodeById(layout, "evt-3").Depth, Is.EqualTo(4));
        Assert.That(NodeById(layout, "evt-3").X, Is.EqualTo(1402));
    }

    [Test]
    public void Compute_NewJoinerChain_PlacesTheSingleLeafChainOnOneRow()
    {
        var layout = ComputeNewJoiner();

        // One leaf: every parent centres over it, so all nodes sit at y = 0 + 2
        Assert.That(layout.Nodes.Select(n => n.Y), Is.All.EqualTo(2));
    }

    [Test]
    public void Compute_NewJoinerChain_ComputesOverallWidthAndHeight()
    {
        var layout = ComputeNewJoiner();

        // W = (maxDepth + 1) * 350 - 70 + 4; H = max(nextY - 26, 58) + 4
        Assert.That(layout.Width, Is.EqualTo(1684));
        Assert.That(layout.Height, Is.EqualTo(62));
    }

    [Test]
    public void Compute_NewJoinerChain_BuildsCubicBezierEdgePathsInInvariantCulture()
    {
        var layout = ComputeNewJoiner();

        // src (right centre: 2 + 280, 2 + 29) to evt-0 (left centre: 352, 31); mid x = 317
        var edge = layout.Edges[0];
        Assert.That(edge.FromId, Is.EqualTo("src"));
        Assert.That(edge.ToId, Is.EqualTo("evt-0"));
        Assert.That(edge.PathData, Is.EqualTo("M 282 31 C 317 31, 317 31, 352 31"));
    }

    [Test]
    public void Compute_NewJoinerChain_ConnectsParentToChildInTreeOrder()
    {
        var layout = ComputeNewJoiner();

        Assert.That(layout.Edges.Select(e => (e.FromId, e.ToId)), Is.EqualTo(new[]
        {
            ("src", "evt-0"), ("evt-0", "evt-1"), ("evt-1", "evt-2"), ("evt-2", "evt-3")
        }));
    }

    [Test]
    public void Compute_NewJoiner_DerivesTitlesSubsTonesAndAttributeFlags()
    {
        var layout = ComputeNewJoiner();

        var source = NodeById(layout, "src");
        Assert.That(source.Title, Is.EqualTo("Source record"));
        Assert.That(source.Sub, Is.EqualTo("Liam Allen"));
        Assert.That(source.Tone, Is.EqualTo(CausalityTone.Secondary));
        Assert.That(source.HasAttributeRows, Is.False);
        Assert.That(source.Event, Is.Null);

        // Identity created: no attribute rows, so the sub is the first entity chip label
        var projected = NodeById(layout, "evt-0");
        Assert.That(projected.Title, Is.EqualTo("Identity created"));
        Assert.That(projected.Sub, Is.EqualTo("Liam Allen"));
        Assert.That(projected.Tone, Is.EqualTo(CausalityTone.Primary));
        Assert.That(projected.Event, Is.Not.Null);

        // Provisioned: first entity chip is the target Connected System
        Assert.That(NodeById(layout, "evt-2").Sub, Is.EqualTo("Glitterband EMEA"));

        // Export queued: attribute rows take precedence over entity labels
        var pendingExport = NodeById(layout, "evt-3");
        Assert.That(pendingExport.Sub, Is.EqualTo("3 attributes"));
        Assert.That(pendingExport.HasAttributeRows, Is.True);
    }

    [Test]
    public void Compute_TechnicalNames_SwapsNodeTitlesToTheTechnicalLabels()
    {
        var layout = ComputeNewJoiner(technicalNames: true);

        Assert.That(NodeById(layout, "evt-0").Title, Is.EqualTo("MVO Projected"));
        Assert.That(NodeById(layout, "evt-3").Title, Is.EqualTo("CSO Pending Export"));
        // The synthetic source root keeps its plain title in both modes
        Assert.That(NodeById(layout, "src").Title, Is.EqualTo("Source record"));
    }

    [Test]
    public void Compute_LeaverFanOut_CentresTheParentOverItsChildren()
    {
        var layout = ComputeLeaver();

        Assert.That(layout.Nodes, Has.Count.EqualTo(6));
        Assert.That(layout.Edges, Has.Count.EqualTo(5));

        // The chain is three deep now that the deprovisioning Pending Exports hang off MvoDeleted:
        //   evt-0 Left scope > evt-1 Attributes flowed (leaf)
        //                    > evt-2 Identity deleted > evt-3 Export queued (leaf)
        //                                             > evt-4 Export queued (leaf)
        // Three leaves stack at rows 0, 84, 168 (y offset + 2)
        Assert.That(NodeById(layout, "evt-1").Y, Is.EqualTo(2));
        Assert.That(NodeById(layout, "evt-3").Y, Is.EqualTo(86));
        Assert.That(NodeById(layout, "evt-4").Y, Is.EqualTo(170));

        // Each parent sits at the midpoint of its first and last children: (84 + 168) / 2 + 2
        Assert.That(NodeById(layout, "evt-2").Y, Is.EqualTo(128));
        // ...and evt-0 over its own two children, one of which is that midpoint: (0 + 126) / 2 + 2
        Assert.That(NodeById(layout, "evt-0").Y, Is.EqualTo(65));
        // The source root centres over the roots; one root, so it shares its y
        Assert.That(NodeById(layout, "src").Y, Is.EqualTo(65));
    }

    [Test]
    public void Compute_LeaverFanOut_ComputesOverallWidthAndHeight()
    {
        var layout = ComputeLeaver();

        // maxDepth 3: W = 4 * 350 - 70 + 4; three leaf rows: H = (3 * 84) - 26 + 4
        Assert.That(layout.Width, Is.EqualTo(1334));
        Assert.That(layout.Height, Is.EqualTo(230));
    }

    [Test]
    public void Compute_ExportFailure_LaysOutTheShortChain()
    {
        var layout = ComputeExportFailure();

        Assert.That(layout.Nodes, Has.Count.EqualTo(3));
        Assert.That(layout.Edges, Has.Count.EqualTo(2));
        Assert.That(layout.Width, Is.EqualTo(984));
        Assert.That(layout.Height, Is.EqualTo(62));

        // Neither export event carries entity chips here, so the sub falls back to the system name
        Assert.That(NodeById(layout, "evt-0").Sub, Is.EqualTo("Glitterband EMEA"));
        Assert.That(NodeById(layout, "evt-1").Sub, Is.EqualTo("Glitterband EMEA"));
        Assert.That(NodeById(layout, "evt-1").Tone, Is.EqualTo(CausalityTone.Error));
    }

    [Test]
    public void Compute_MultipleRoots_CentresTheSourceRootOverThem()
    {
        var item = new ActivityRunProfileExecutionItem { Id = Guid.NewGuid() };
        CausalityTestData.AddOutcome(item, ActivityRunProfileExecutionItemSyncOutcomeType.Projected,
            parent: null, ordinal: 0, targetEntityId: CausalityTestData.MvoId,
            targetEntityDescription: "Liam Allen");
        CausalityTestData.AddOutcome(item, ActivityRunProfileExecutionItemSyncOutcomeType.AttributeFlow,
            parent: null, ordinal: 1, detailCount: 4);
        var model = CausalityModelBuilder.Build(item, CausalityTestData.NewJoinerContext());

        var layout = CausalityGraphLayoutCalculator.Compute(model, technicalNames: false);

        Assert.That(NodeById(layout, "evt-0").Y, Is.EqualTo(2));
        Assert.That(NodeById(layout, "evt-1").Y, Is.EqualTo(86));
        // The source root sits at the midpoint of the first and last roots: (0 + 84) / 2 + 2
        Assert.That(NodeById(layout, "src").Y, Is.EqualTo(44));
        Assert.That(layout.Width, Is.EqualTo(634));
        Assert.That(layout.Height, Is.EqualTo(146));
    }

    [Test]
    public void Compute_NoEvents_StillRendersTheSourceRoot()
    {
        var item = new ActivityRunProfileExecutionItem { Id = Guid.NewGuid() };
        var model = CausalityModelBuilder.Build(item, CausalityTestData.NewJoinerContext());

        var layout = CausalityGraphLayoutCalculator.Compute(model, technicalNames: false);

        Assert.That(layout.Nodes, Has.Count.EqualTo(1));
        Assert.That(layout.Edges, Is.Empty);
        Assert.That(NodeById(layout, "src").Y, Is.EqualTo(2));
        // A single empty column: W = 280 - 70 + 4; H = max(0 - 26, 58) + 4
        Assert.That(layout.Width, Is.EqualTo(284));
        Assert.That(layout.Height, Is.EqualTo(62));
    }

    [Test]
    public void Truncate_AtTheTitleBoundary_LeavesTwentySixCharactersUntouched()
    {
        var exactFit = new string('a', 26);

        Assert.That(CausalityGraphLayoutCalculator.Truncate(exactFit, CausalityGraphLayoutCalculator.TitleMaxLength),
            Is.EqualTo(exactFit));
    }

    [Test]
    public void Truncate_OverTheTitleBoundary_KeepsTwentyFiveCharactersPlusEllipsis()
    {
        var tooLong = new string('a', 27);

        var truncated = CausalityGraphLayoutCalculator.Truncate(tooLong, CausalityGraphLayoutCalculator.TitleMaxLength);

        Assert.That(truncated, Is.EqualTo(new string('a', 25) + "…"));
        Assert.That(truncated, Has.Length.EqualTo(26));
    }

    [Test]
    public void Truncate_AtTheSubBoundary_LeavesThirtyCharactersUntouched()
    {
        var exactFit = new string('b', 30);

        Assert.That(CausalityGraphLayoutCalculator.Truncate(exactFit, CausalityGraphLayoutCalculator.SubMaxLength),
            Is.EqualTo(exactFit));
    }

    [Test]
    public void Truncate_OverTheSubBoundary_KeepsTwentyNineCharactersPlusEllipsis()
    {
        var tooLong = new string('b', 31);

        var truncated = CausalityGraphLayoutCalculator.Truncate(tooLong, CausalityGraphLayoutCalculator.SubMaxLength);

        Assert.That(truncated, Is.EqualTo(new string('b', 29) + "…"));
        Assert.That(truncated, Has.Length.EqualTo(30));
    }

    [Test]
    public void Truncate_NullValue_ReturnsAnEmptyString()
    {
        Assert.That(CausalityGraphLayoutCalculator.Truncate(null, CausalityGraphLayoutCalculator.SubMaxLength),
            Is.Empty);
    }

    [Test]
    public void Compute_LongLabels_TruncatesNodeTitlesAndSubs()
    {
        var item = new ActivityRunProfileExecutionItem { Id = Guid.NewGuid() };
        CausalityTestData.AddOutcome(item, ActivityRunProfileExecutionItemSyncOutcomeType.Projected,
            parent: null, ordinal: 0, targetEntityId: CausalityTestData.MvoId,
            targetEntityDescription: "An extremely long Identity display name that cannot fit");
        var model = CausalityModelBuilder.Build(item, CausalityTestData.NewJoinerContext());

        var layout = CausalityGraphLayoutCalculator.Compute(model, technicalNames: false);

        var node = NodeById(layout, "evt-0");
        Assert.That(node.Sub, Has.Length.EqualTo(30));
        Assert.That(node.Sub, Does.EndWith("…"));
        Assert.That(node.Sub, Does.StartWith("An extremely long Identity di"));
    }

    [Test]
    public void Compute_SingleAttributeRow_UsesTheSingularSubForm()
    {
        var item = new ActivityRunProfileExecutionItem { Id = Guid.NewGuid() };
        var pendingExport = CausalityTestData.AddOutcome(item,
            ActivityRunProfileExecutionItemSyncOutcomeType.PendingExportCreated,
            parent: null, ordinal: 0, targetEntityId: CausalityTestData.PendingExportId,
            targetEntityDescription: "Glitterband EMEA", detailCount: 1, detailMessage: "2");
        var snapshot = CausalityTestData.BuildCsoChangeSnapshot();
        snapshot.AttributeChanges.RemoveRange(1, snapshot.AttributeChanges.Count - 1);
        pendingExport.ConnectedSystemObjectChange = snapshot;
        var model = CausalityModelBuilder.Build(item, CausalityTestData.NewJoinerContext());

        var layout = CausalityGraphLayoutCalculator.Compute(model, technicalNames: false);

        Assert.That(NodeById(layout, "evt-0").Sub, Is.EqualTo("1 attribute"));
    }
}
