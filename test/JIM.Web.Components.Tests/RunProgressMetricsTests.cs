// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using Bunit;
using JIM.Models.Activities;
using JIM.Web.Shared;
using MudBlazor;
using NUnit.Framework;

namespace JIM.Web.Components.Tests;

/// <summary>
/// The live metrics beneath a Run Profile execution's progress bar. What is worth pinning is the
/// reason the component exists: every fact appears exactly once, carries a label, and the states
/// that used to fall back to the Activity's message (an unknown total, a stalled counter) still
/// say something rather than nothing.
/// </summary>
[TestFixture]
public class RunProgressMetricsTests : JimComponentTestContext
{
    private IRenderedComponent<RunProgressMetrics> RenderMetrics(
        int processed = 3500,
        int total = 10527,
        double? objectsPerSecond = 145d,
        double? secondsRemaining = 49d) =>
        Render<RunProgressMetrics>(p => p
            .Add(c => c.ObjectsProcessed, processed)
            .Add(c => c.ObjectsToProcess, total)
            .Add(c => c.ObjectsPerSecond, objectsPerSecond)
            .Add(c => c.EstimatedSecondsRemaining, secondsRemaining));

    /// <summary>
    /// Every number the component owns, read from JIM's own elements rather than the rendered
    /// markup as a whole; MudBlazor writes the percentage into the bar's inline style too, which
    /// a raw markup search would count as a second appearance of the same fact.
    /// </summary>
    private static List<string> Values(IRenderedComponent<RunProgressMetrics> cut) =>
        cut.FindAll(".jim-run-metric-value, .jim-run-percent")
            .Select(e => e.TextContent.Trim())
            .ToList();

    private static List<string> Labels(IRenderedComponent<RunProgressMetrics> cut) =>
        cut.FindAll(".jim-run-metric-label")
            .Select(e => e.TextContent.Trim())
            .ToList();

    /// <summary>
    /// An import midway through fetching, with the Connector reporting a step of its own.
    /// </summary>
    private static List<ActivityPhase> ImportFetching() =>
    [
        Phase(RunPhaseKeys.ImportConnect, "Connecting to Connected System", 0, ActivityPhaseStatus.Completed),
        Phase(RunPhaseKeys.ImportFetch, "Importing objects", 1, ActivityPhaseStatus.Active),
        Phase(ActivityPhase.QualifyConnectorKey("read"), "Reading the file", 2, ActivityPhaseStatus.Active, RunPhaseKeys.ImportFetch),
        Phase(RunPhaseKeys.ImportSave, "Saving changes", 3, ActivityPhaseStatus.Pending)
    ];

    private static ActivityPhase Phase(string key, string name, int order, ActivityPhaseStatus status, string? parentKey = null) => new()
    {
        Id = Guid.NewGuid(),
        Key = key,
        Name = name,
        Order = order,
        Status = status,
        ParentKey = parentKey
    };

    [Test]
    public void RunProgressMetrics_RunWithSteps_ScopesTheFiguresToTheStepTheyMeasure()
    {
        // Every figure here is reset by each step that counts its own work, so without this the
        // panel reads as an estimate for the whole run. The rail scrolls sideways, so pointing at
        // "the step running now" is not enough; the step has to be named.
        var cut = Render<RunProgressMetrics>(p => p
            .Add(c => c.ObjectsProcessed, 3500)
            .Add(c => c.ObjectsToProcess, 10527)
            .Add(c => c.Phases, ImportFetching()));

        Assert.That(cut.Find(".jim-run-scope").TextContent.Trim(),
            Is.EqualTo("Step 2 of 3: Importing objects"));
    }

    [Test]
    public void RunProgressMetrics_ConnectorStepRunning_NamesTheStepTheFiguresBelongTo()
    {
        // "Reading the file" is the Connector's own step and counts nothing; the counters belong
        // to the JIM step hosting it, so naming the inner one would mislabel the figures.
        var cut = Render<RunProgressMetrics>(p => p
            .Add(c => c.ObjectsProcessed, 3500)
            .Add(c => c.ObjectsToProcess, 10527)
            .Add(c => c.Phases, ImportFetching()));

        Assert.That(cut.Find(".jim-run-scope").TextContent, Does.Not.Contain("Reading the file"));
    }

    [Test]
    public void RunProgressMetrics_RunWithoutSteps_OmitsTheScopeRatherThanShowingAnEmptyOne()
    {
        // Activities that are not Run Profile executions, and runs predating step recording.
        var cut = RenderMetrics();

        Assert.That(cut.FindAll(".jim-run-scope"), Is.Empty);
    }

    [Test]
    public void RunProgressMetrics_RunInFlight_StatesEachFactExactlyOnce()
    {
        // The defect this component replaced: the count, rate and time remaining were each
        // printed two or three times, by the Activity's message and by the panel underneath it.
        var cut = RenderMetrics();

        Assert.That(Values(cut), Is.EquivalentTo(new[]
        {
            "33.2%",
            "3,500 / 10,527",
            "145 /sec",
            "~49 sec"
        }));
    }

    [Test]
    public void RunProgressMetrics_RunInFlight_LabelsEveryNumberItShows()
    {
        var cut = RenderMetrics();

        Assert.That(Labels(cut), Is.EquivalentTo(new[] { "Processed", "Rate", "Remaining" }));
    }

    [Test]
    public void RunProgressMetrics_KnownTotal_DrivesTheBarFromTheCountRatherThanRunningIndeterminate()
    {
        // The percentage beside the bar and the bar's own fill are the same computed value, so the
        // rendered percentage is what proves it; reading MudProgressLinear's parameter state back
        // would couple the test to a third party's internals for no extra confidence.
        var cut = RenderMetrics();

        Assert.Multiple(() =>
        {
            Assert.That(cut.FindComponent<MudProgressLinear>().Instance.Indeterminate, Is.False);
            Assert.That(cut.Find(".jim-run-percent").TextContent.Trim(), Is.EqualTo("33.2%"));
        });
    }

    [Test]
    public void RunProgressMetrics_UnknownTotal_StillSaysHowMuchHasBeenProcessed()
    {
        // Paged imports never learn a total. The panel used to show a bare indeterminate bar and
        // leave the count to the Activity's message, so removing it from the message would have
        // left nothing at all.
        var cut = RenderMetrics(total: 0, secondsRemaining: null);

        Assert.That(Values(cut), Does.Contain("3,500"));
    }

    [Test]
    public void RunProgressMetrics_UnknownTotal_OffersNoPercentageOrTimeRemaining()
    {
        var cut = RenderMetrics(total: 0, secondsRemaining: null);

        Assert.Multiple(() =>
        {
            Assert.That(cut.FindComponent<MudProgressLinear>().Instance.Indeterminate, Is.True);
            Assert.That(cut.FindAll(".jim-run-percent"), Is.Empty);
            Assert.That(Labels(cut), Does.Not.Contain("Remaining"));
        });
    }

    [Test]
    public void RunProgressMetrics_CounterStalled_SaysFinishingUpRatherThanGoingQuiet()
    {
        // The worker's own tracker said "finishing up" when the counter stopped advancing. The
        // portal's tracker simply reports a zero rate, so the panel has to say it instead.
        var cut = RenderMetrics(processed: 10527, objectsPerSecond: 0d, secondsRemaining: null);

        Assert.That(Values(cut), Does.Contain("Finishing up"));
    }

    [Test]
    public void RunProgressMetrics_CounterStalled_DoesNotReportZeroObjectsPerSecond()
    {
        var cut = RenderMetrics(processed: 10527, objectsPerSecond: 0d, secondsRemaining: null);

        Assert.That(Values(cut).Any(v => v.Contains("/sec")), Is.False);
    }

    [Test]
    public void RunProgressMetrics_RunThatHasNotStartedCounting_IsNotMistakenForAStalledOne()
    {
        // Two samples taken before the first object is processed give a rate of zero, which is a
        // run yet to get going rather than one tidying up.
        var cut = RenderMetrics(processed: 0, objectsPerSecond: 0d, secondsRemaining: null);

        Assert.That(Values(cut), Does.Not.Contain("Finishing up"));
    }

    [Test]
    public void RunProgressMetrics_ZeroRate_LeavesTheRateEmptyRatherThanReportingNoneAsANumber()
    {
        // A step that has not moved its counter yet reports a rate of zero. "0.0 /sec" reads as a
        // measurement; there is nothing to measure.
        var cut = RenderMetrics(processed: 0, total: 0, objectsPerSecond: 0d, secondsRemaining: null);

        Assert.That(Values(cut).Any(v => v.Contains("/sec")), Is.False);
    }

    [Test]
    public void RunProgressMetrics_NoRateYet_LeavesTheRateEmptyRatherThanInventingOne()
    {
        // The first read of a run has too few samples for a rate. The cells stay in place so the
        // layout does not jump once one arrives.
        var cut = RenderMetrics(objectsPerSecond: null, secondsRemaining: null);

        Assert.Multiple(() =>
        {
            Assert.That(Labels(cut), Does.Contain("Rate"));
            Assert.That(cut.HasComponent<EmptyValue>(), Is.True);
        });
    }

    [Test]
    public void RunProgressMetrics_SlowRun_KeepsADecimalSoTheRateIsNotRoundedToZero()
    {
        var cut = RenderMetrics(objectsPerSecond: 0.4d);

        Assert.That(Values(cut), Does.Contain("0.4 /sec"));
    }

    [Test]
    public void RunProgressMetrics_LongRun_ReportsTimeRemainingInLargerUnits()
    {
        var cut = RenderMetrics(secondsRemaining: 3720d);

        Assert.That(Values(cut).Any(v => v.StartsWith("~1 hr")), Is.True);
    }
}
