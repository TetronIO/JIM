// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Collections.Generic;
using JIM.Web.Causality;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// Tests for <see cref="CausalityFlowConnectorCalculator"/>: the mock-up's drawFlowLinks connector
/// pairing (source to first Identity event, deepest Identity event to each downstream system group)
/// and the cubic bezier elbow geometry with invariant coordinate formatting.
/// </summary>
[TestFixture]
public class CausalityFlowConnectorCalculatorTests
{
    [Test]
    public void BuildConnectorPairs_WithIdentityEventsAndGroups_LinksSourceToFirstAndDeepestToEachGroup()
    {
        var pairs = CausalityFlowConnectorCalculator.BuildConnectorPairs(
            identityEventIds: ["evt-0", "evt-1"],
            systemGroupIds: ["sys-0", "sys-1"]);

        Assert.That(pairs, Is.EqualTo(new[]
        {
            new CausalityFlowConnectorPair("src", "evt-0"),
            new CausalityFlowConnectorPair("evt-1", "sys-0"),
            new CausalityFlowConnectorPair("evt-1", "sys-1")
        }));
    }

    [Test]
    public void BuildConnectorPairs_SingleIdentityEvent_UsesItAsBothFirstAndDeepest()
    {
        var pairs = CausalityFlowConnectorCalculator.BuildConnectorPairs(
            identityEventIds: ["evt-0"],
            systemGroupIds: ["sys-0"]);

        Assert.That(pairs, Is.EqualTo(new[]
        {
            new CausalityFlowConnectorPair("src", "evt-0"),
            new CausalityFlowConnectorPair("evt-0", "sys-0")
        }));
    }

    [Test]
    public void BuildConnectorPairs_NoIdentityEvents_LinksSourceToEachGroupDirectly()
    {
        var pairs = CausalityFlowConnectorCalculator.BuildConnectorPairs(
            identityEventIds: [],
            systemGroupIds: ["sys-0", "sys-1"]);

        Assert.That(pairs, Is.EqualTo(new[]
        {
            new CausalityFlowConnectorPair("src", "sys-0"),
            new CausalityFlowConnectorPair("src", "sys-1")
        }));
    }

    [Test]
    public void BuildConnectorPairs_NoGroups_LinksSourceToFirstIdentityEventOnly()
    {
        var pairs = CausalityFlowConnectorCalculator.BuildConnectorPairs(
            identityEventIds: ["evt-0", "evt-1"],
            systemGroupIds: []);

        Assert.That(pairs, Is.EqualTo(new[] { new CausalityFlowConnectorPair("src", "evt-0") }));
    }

    [Test]
    public void Compute_BuildsCubicBezierElbowsWithTerminalDots()
    {
        var measurements = new CausalityFlowMeasurements
        {
            Width = 900,
            Height = 400,
            Cards =
            [
                new CausalityFlowCardRect { Id = "src", Left = 0, Right = 200, Top = 0, Height = 100 },
                new CausalityFlowCardRect { Id = "evt-0", Left = 300, Right = 500, Top = 0, Height = 60 }
            ]
        };

        var connectors = CausalityFlowConnectorCalculator.Compute(
            measurements, [new CausalityFlowConnectorPair("src", "evt-0")]);

        // Anchors: y = top + min(height / 2, 34), so src anchors at 34 and evt-0 at 30; the elbow's
        // control points sit at the horizontal midpoint (250)
        Assert.That(connectors, Has.Count.EqualTo(1));
        Assert.That(connectors[0].PathData, Is.EqualTo("M 200 34 C 250 34, 250 30, 300 30"));
        Assert.That(connectors[0].DotX, Is.EqualTo("300"));
        Assert.That(connectors[0].DotY, Is.EqualTo("30"));
    }

    [Test]
    public void Compute_MeasuredHeaderRows_AnchorsOnTheirVerticalMiddle()
    {
        // A card's header row is the row naming the thing being connected, and its height varies by
        // kind: a Connected System group's chip row is shorter than an event card's icon row. The
        // fixed 34px cap therefore landed below the middle of both, by a different amount each time.
        var measurements = new CausalityFlowMeasurements
        {
            Width = 900,
            Height = 400,
            Cards =
            [
                // Event card: 12px padding above a 28px icon row, so the middle sits at 26
                new CausalityFlowCardRect
                {
                    Id = "src", Left = 0, Right = 200, Top = 0, Height = 160,
                    HeaderTop = 12, HeaderHeight = 28
                },
                // Connected System group: a 44px chip row flush to the top, so the middle sits at 22
                new CausalityFlowCardRect
                {
                    Id = "sys-0", Left = 300, Right = 500, Top = 0, Height = 220,
                    HeaderTop = 0, HeaderHeight = 44
                }
            ]
        };

        var connectors = CausalityFlowConnectorCalculator.Compute(
            measurements, [new CausalityFlowConnectorPair("src", "sys-0")]);

        Assert.That(connectors, Has.Count.EqualTo(1));
        Assert.That(connectors[0].PathData, Is.EqualTo("M 200 26 C 250 26, 250 22, 300 22"));
        Assert.That(connectors[0].DotY, Is.EqualTo("22"));
    }

    [Test]
    public void Compute_UnmeasuredHeaderRow_FallsBackToTheCappedCardCentre()
    {
        // Nothing measured means no header element was found, not that the header has no height, so
        // the connector must still land somewhere sensible rather than at the card's very top.
        var measurements = new CausalityFlowMeasurements
        {
            Width = 900,
            Height = 400,
            Cards =
            [
                new CausalityFlowCardRect { Id = "src", Left = 0, Right = 200, Top = 0, Height = 100 },
                new CausalityFlowCardRect { Id = "evt-0", Left = 300, Right = 500, Top = 0, Height = 60 }
            ]
        };

        var connectors = CausalityFlowConnectorCalculator.Compute(
            measurements, [new CausalityFlowConnectorPair("src", "evt-0")]);

        Assert.That(connectors[0].PathData, Is.EqualTo("M 200 34 C 250 34, 250 30, 300 30"));
    }

    [Test]
    public void Compute_SkipsPairsWhoseEndsWereNotMeasured()
    {
        var measurements = new CausalityFlowMeasurements
        {
            Width = 900,
            Height = 400,
            Cards = [new CausalityFlowCardRect { Id = "src", Left = 0, Right = 200, Top = 0, Height = 100 }]
        };

        var connectors = CausalityFlowConnectorCalculator.Compute(
            measurements, [new CausalityFlowConnectorPair("src", "missing")]);

        Assert.That(connectors, Is.Empty);
    }

    [Test]
    public void FormatCoordinate_UsesInvariantCultureAndOneDecimalPlace()
    {
        Assert.That(CausalityFlowConnectorCalculator.FormatCoordinate(300), Is.EqualTo("300"));
        Assert.That(CausalityFlowConnectorCalculator.FormatCoordinate(123.456), Is.EqualTo("123.5"));
    }
}
