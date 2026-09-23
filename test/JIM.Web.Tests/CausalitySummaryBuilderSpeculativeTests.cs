// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Collections.Generic;
using System.Linq;
using JIM.Models.Activities;
using JIM.Models.Sync;
using JIM.Models.Transactional;
using JIM.Web.Causality;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// Tests for the conditional-mood summary sentence <see cref="CausalitySummaryBuilder.Build"/> produces
/// for a speculative model (#1519, D-S9).
/// </summary>
[TestFixture]
public class CausalitySummaryBuilderSpeculativeTests
{
    private static string RenderSentence(IEnumerable<SummarySegment> segments) =>
        string.Concat(segments.Select(s => s switch
        {
            SummarySegment.Text text => text.Value,
            SummarySegment.Entity entity => entity.Label,
            SummarySegment.LiteralValue literalValue => literalValue.Value,
            _ => string.Empty
        }));

    private static CausalityPageContext Context() => new(
        ConnectedSystemId: 1,
        ConnectedSystemName: "Yellowstone APAC",
        RunProfileName: "Full Synchronisation",
        CsoId: null,
        CsoConnectedSystemId: null,
        CsoConnectedSystemName: null,
        CsoDisplayName: "Liam Allen",
        CsoExternalId: "S8-287551",
        CsoObjectTypeName: "person",
        MvoTypeName: "Person",
        MvoTypePluralName: "People");

    [Test]
    public void Build_SpeculativeJoinerScenario_UsesConditionalMood()
    {
        var preview = new SyncPreviewResult
        {
            OutcomeTree =
            [
                new SyncOutcomeNode
                {
                    OutcomeType = ActivityRunProfileExecutionItemSyncOutcomeType.Projected,
                    Children =
                    [
                        new SyncOutcomeNode
                        {
                            OutcomeType = ActivityRunProfileExecutionItemSyncOutcomeType.AttributeFlow,
                            DetailCount = 3
                        }
                    ]
                }
            ]
        };

        var model = CausalityModelBuilder.BuildSpeculative(preview, Context());
        var summary = CausalitySummaryBuilder.Build(model);

        Assert.That(RenderSentence(summary.Segments), Is.EqualTo(
            "A Full Synchronisation on Yellowstone APAC would process person Liam Allen: " +
            "a new Metaverse Object would be projected, and 3 attributes would flow to it."));
    }

    /// <summary>
    /// Unique Value Generation (#242): the speculative joiner clause uses the conditional mood ("would be
    /// generated as") in the same position (after the attribute flow clause, before the queued export
    /// clause) as the recorded joiner shape.
    /// </summary>
    [Test]
    public void Build_SpeculativeJoinerScenarioWithGeneratedValue_UsesConditionalMood()
    {
        var preview = new SyncPreviewResult
        {
            OutcomeTree =
            [
                new SyncOutcomeNode
                {
                    OutcomeType = ActivityRunProfileExecutionItemSyncOutcomeType.Projected,
                    Children =
                    [
                        new SyncOutcomeNode
                        {
                            OutcomeType = ActivityRunProfileExecutionItemSyncOutcomeType.AttributeFlow,
                            DetailCount = 3,
                            Ordinal = 0
                        },
                        new SyncOutcomeNode
                        {
                            OutcomeType = ActivityRunProfileExecutionItemSyncOutcomeType.GeneratedValueAssigned,
                            DetailMessage = "Account Name: jallen42",
                            Ordinal = 1
                        }
                    ]
                }
            ]
        };

        var model = CausalityModelBuilder.BuildSpeculative(preview, Context());
        var summary = CausalitySummaryBuilder.Build(model);

        Assert.That(RenderSentence(summary.Segments), Is.EqualTo(
            "A Full Synchronisation on Yellowstone APAC would process person Liam Allen: " +
            "a new Metaverse Object would be projected, 3 attributes would flow to it, " +
            "and Account Name would be generated as jallen42."));
    }

    [Test]
    public void Build_SpeculativeJoinerScenarioWithAdoptedValue_UsesConditionalMood()
    {
        var preview = new SyncPreviewResult
        {
            OutcomeTree =
            [
                new SyncOutcomeNode
                {
                    OutcomeType = ActivityRunProfileExecutionItemSyncOutcomeType.Projected,
                    Children =
                    [
                        new SyncOutcomeNode
                        {
                            OutcomeType = ActivityRunProfileExecutionItemSyncOutcomeType.GeneratedValueAdopted,
                            DetailMessage = "Employee Number: 40021"
                        }
                    ]
                }
            ]
        };

        var model = CausalityModelBuilder.BuildSpeculative(preview, Context());
        var summary = CausalitySummaryBuilder.Build(model);

        Assert.That(RenderSentence(summary.Segments), Is.EqualTo(
            "A Full Synchronisation on Yellowstone APAC would process person Liam Allen: " +
            "a new Metaverse Object would be projected, and the existing Employee Number 40021 would be adopted."));
    }

    [Test]
    public void Build_SpeculativeEmptyTree_ReadsNoChangesAreNeeded()
    {
        var model = CausalityModelBuilder.BuildSpeculative(new SyncPreviewResult(), Context());
        var summary = CausalitySummaryBuilder.Build(model);

        Assert.That(RenderSentence(summary.Segments), Is.EqualTo(
            "A Full Synchronisation on Yellowstone APAC would process person Liam Allen: no changes are needed."));
    }

    [Test]
    public void Build_RecordedModel_StillUsesPastTense()
    {
        // The existing recorded-tree behaviour must be unaffected by the speculative addition.
        var item = CausalityTestData.NewJoinerItem();
        var model = CausalityModelBuilder.Build(item, CausalityTestData.NewJoinerContext());
        var summary = CausalitySummaryBuilder.Build(model);

        Assert.That(RenderSentence(summary.Segments), Does.Contain("processed person"));
        Assert.That(RenderSentence(summary.Segments), Does.Not.Contain("would"));
    }
}
