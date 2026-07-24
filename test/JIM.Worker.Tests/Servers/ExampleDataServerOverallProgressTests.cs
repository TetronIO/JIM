// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Servers;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// Tests the overall-progress mapping used by example data template execution. An execution runs in
/// equally-weighted phases (generation, an optional change-history build, then persistence), each
/// processing every object once. The mapping folds each phase's own 0->total count into a single
/// continuous 0->total overall count, so the Activity progress bar advances once from 0% to 100% across
/// the whole job rather than sweeping to 100% per phase (which was the reported "nothing, then done,
/// repeatedly" behaviour).
/// </summary>
[TestFixture]
public class ExampleDataServerOverallProgressTests
{
    private const int Total = 10000;

    [Test]
    public void CalculateOverallProgress_GenerationStart_IsZero()
    {
        Assert.That(ExampleDataServer.CalculateOverallProgress(phase: 0, phaseProcessed: 0, totalObjects: Total, phaseCount: 3), Is.EqualTo(0));
    }

    [Test]
    public void CalculateOverallProgress_ThreePhases_GenerationEnd_IsOneThird()
    {
        Assert.That(ExampleDataServer.CalculateOverallProgress(phase: 0, phaseProcessed: Total, totalObjects: Total, phaseCount: 3), Is.EqualTo(3333));
    }

    [Test]
    public void CalculateOverallProgress_ThreePhases_ChangeHistoryEnd_IsTwoThirds()
    {
        Assert.That(ExampleDataServer.CalculateOverallProgress(phase: 1, phaseProcessed: Total, totalObjects: Total, phaseCount: 3), Is.EqualTo(6667));
    }

    [Test]
    public void CalculateOverallProgress_ThreePhases_PersistenceEnd_IsTotal()
    {
        // The bar must reach exactly 100% (never overshoot) at the end of the final phase.
        Assert.That(ExampleDataServer.CalculateOverallProgress(phase: 2, phaseProcessed: Total, totalObjects: Total, phaseCount: 3), Is.EqualTo(Total));
    }

    [Test]
    public void CalculateOverallProgress_PhaseBoundaries_AreContinuous()
    {
        // End of one phase must equal the start of the next, so the bar never jumps backward or freezes.
        var generationEnd = ExampleDataServer.CalculateOverallProgress(phase: 0, phaseProcessed: Total, totalObjects: Total, phaseCount: 3);
        var changeHistoryStart = ExampleDataServer.CalculateOverallProgress(phase: 1, phaseProcessed: 0, totalObjects: Total, phaseCount: 3);
        var changeHistoryEnd = ExampleDataServer.CalculateOverallProgress(phase: 1, phaseProcessed: Total, totalObjects: Total, phaseCount: 3);
        var persistenceStart = ExampleDataServer.CalculateOverallProgress(phase: 2, phaseProcessed: 0, totalObjects: Total, phaseCount: 3);

        Assert.That(changeHistoryStart, Is.EqualTo(generationEnd));
        Assert.That(persistenceStart, Is.EqualTo(changeHistoryEnd));
    }

    [Test]
    public void CalculateOverallProgress_TwoPhases_GenerationEnd_IsHalf()
    {
        // With change tracking off there are only two phases (generation, persistence).
        Assert.That(ExampleDataServer.CalculateOverallProgress(phase: 0, phaseProcessed: Total, totalObjects: Total, phaseCount: 2), Is.EqualTo(5000));
    }

    [Test]
    public void CalculateOverallProgress_TwoPhases_PersistenceEnd_IsTotal()
    {
        Assert.That(ExampleDataServer.CalculateOverallProgress(phase: 1, phaseProcessed: Total, totalObjects: Total, phaseCount: 2), Is.EqualTo(Total));
    }

    [Test]
    public void CalculateOverallProgress_EmptyTemplate_IsZero()
    {
        Assert.That(ExampleDataServer.CalculateOverallProgress(phase: 2, phaseProcessed: 0, totalObjects: 0, phaseCount: 3), Is.EqualTo(0));
    }

    [Test]
    public void CalculateOverallProgress_PhaseProcessedAboveTotal_IsClampedToTotal()
    {
        // A phase-local count above total (should not happen, but be defensive) is clamped so the bar cannot overshoot.
        Assert.That(ExampleDataServer.CalculateOverallProgress(phase: 0, phaseProcessed: Total + 5000, totalObjects: Total, phaseCount: 3), Is.EqualTo(3333));
    }

    [Test]
    public void CalculateOverallProgress_NegativePhaseProcessed_IsClampedToPhaseOffset()
    {
        Assert.That(ExampleDataServer.CalculateOverallProgress(phase: 1, phaseProcessed: -50, totalObjects: Total, phaseCount: 3), Is.EqualTo(3333));
    }

    [Test]
    public void CalculateOverallProgress_ZeroPhaseCount_IsZero()
    {
        Assert.That(ExampleDataServer.CalculateOverallProgress(phase: 0, phaseProcessed: 0, totalObjects: Total, phaseCount: 0), Is.EqualTo(0));
    }
}
