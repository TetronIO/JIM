// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Servers.Preview;
using JIM.Models.Activities;
using JIM.Models.Preview;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// The two ways an adapter counts its delta stream (#1530): what an administrator is told a change would do, so a
/// count that drifted from the deltas beneath it would be the confident wrong number the preview exists to prevent.
/// </summary>
[TestFixture]
public class PreviewImpactCounterTests
{
    private const ActivityRunProfileExecutionItemSyncOutcomeType OutOfScope = ActivityRunProfileExecutionItemSyncOutcomeType.WouldFallOutOfScope;
    private const ActivityRunProfileExecutionItemSyncOutcomeType Flow = ActivityRunProfileExecutionItemSyncOutcomeType.AttributeFlow;

    [Test]
    public void PerDelta_CountsEachDeltaPerTransitionLargestFirst()
    {
        var counter = PreviewImpactCounter.PerDelta(connectedSystemId: 4);
        counter.Add(new PreviewDelta(Flow));
        counter.Add(new PreviewDelta(OutOfScope));
        counter.Add(new PreviewDelta(OutOfScope));

        Assert.That(counter.Build(), Is.EqualTo(new[]
        {
            new PreviewImpactCount(OutOfScope, 2, ConnectedSystemId: 4),
            new PreviewImpactCount(Flow, 1, ConnectedSystemId: 4)
        }));
    }

    [Test]
    public void PerDelta_ScopedToAMetaverseObjectType_CarriesItOnEveryCount()
    {
        var counter = PreviewImpactCounter.PerDelta(metaverseObjectTypeId: 7);
        counter.Add(new PreviewDelta(OutOfScope));

        Assert.That(counter.Build(), Is.EqualTo(new[] { new PreviewImpactCount(OutOfScope, 1, MetaverseObjectTypeId: 7) }));
    }

    [Test]
    public void PerDelta_EqualCounts_OrderedByTransition()
    {
        var counter = PreviewImpactCounter.PerDelta();
        counter.Add(new PreviewDelta(OutOfScope));
        counter.Add(new PreviewDelta(Flow));

        Assert.That(counter.Build().Select(c => c.TransitionType), Is.EqualTo(new[] { Flow, OutOfScope }.Order()),
            "a stable order, so the same change previews the same way twice");
    }

    [Test]
    public void PerSubject_SeveralDeltasForOneObject_CountsTheObjectOnce()
    {
        var ada = Guid.NewGuid();
        var counter = PreviewImpactCounter.PerSubject(delta => delta.MetaverseObjectId);
        counter.Add(new PreviewDelta(Flow, MetaverseObjectId: ada, AttributeName: "Email"));
        counter.Add(new PreviewDelta(Flow, MetaverseObjectId: ada, AttributeName: "Title"));
        counter.Add(new PreviewDelta(Flow, MetaverseObjectId: Guid.NewGuid(), AttributeName: "Email"));

        Assert.That(counter.Build(), Is.EqualTo(new[] { new PreviewImpactCount(Flow, 2) }));
    }

    [Test]
    public void PerSubject_DeltaNamingNoObject_IsNotCounted()
    {
        var counter = PreviewImpactCounter.PerSubject(delta => delta.MetaverseObjectId);
        counter.Add(new PreviewDelta(Flow));

        Assert.That(counter.Build(), Is.Empty);
    }

    [Test]
    public void Build_NothingAdded_ReturnsNoCounts()
    {
        Assert.That(PreviewImpactCounter.PerDelta().Build(), Is.Empty);
    }
}
