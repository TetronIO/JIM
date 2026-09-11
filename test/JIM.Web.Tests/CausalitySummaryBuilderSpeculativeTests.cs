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
            "A Full Synchronisation on Yellowstone APAC would process the object for Liam Allen: " +
            "a new Identity would be created, and 3 attributes would flow to it."));
    }

    [Test]
    public void Build_SpeculativeEmptyTree_ReadsNoChangesAreNeeded()
    {
        var model = CausalityModelBuilder.BuildSpeculative(new SyncPreviewResult(), Context());
        var summary = CausalitySummaryBuilder.Build(model);

        Assert.That(RenderSentence(summary.Segments), Is.EqualTo(
            "A Full Synchronisation on Yellowstone APAC would process the object for Liam Allen: no changes are needed."));
    }

    [Test]
    public void Build_RecordedModel_StillUsesPastTense()
    {
        // The existing recorded-tree behaviour must be unaffected by the speculative addition.
        var item = CausalityTestData.NewJoinerItem();
        var model = CausalityModelBuilder.Build(item, CausalityTestData.NewJoinerContext());
        var summary = CausalitySummaryBuilder.Build(model);

        Assert.That(RenderSentence(summary.Segments), Does.Contain("processed the record for"));
        Assert.That(RenderSentence(summary.Segments), Does.Not.Contain("would"));
    }
}
