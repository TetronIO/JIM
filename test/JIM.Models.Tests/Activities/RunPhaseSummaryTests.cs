// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Collections.Generic;
using System.Linq;
using JIM.Models.Activities;
using NUnit.Framework;

namespace JIM.Models.Tests.Activities;

/// <summary>
/// A run's steps reduced to what a list view needs (#1162): the shape of the whole run, and the
/// sentence naming the step running now. The Operations queue, the REST worker-task reads and
/// PowerShell all show a run they are not the detail page for, and each had no way to say more
/// than "something is happening" without deriving the same thing again.
/// </summary>
[TestFixture]
public class RunPhaseSummaryTests
{
    private static ActivityPhase Phase(string key, string name, int order, ActivityPhaseStatus status, string? parentKey = null) => new()
    {
        Id = Guid.NewGuid(),
        Key = key,
        Name = name,
        Order = order,
        Status = status,
        ParentKey = parentKey
    };

    /// <summary>
    /// An import partway through saving, with a skipped step behind it and a Connector step
    /// recorded inside the step that fetched.
    /// </summary>
    private static List<ActivityPhase> ImportSaving() =>
    [
        Phase(RunPhaseKeys.ImportConnect, "Connecting to Connected System", 0, ActivityPhaseStatus.Skipped),
        Phase(RunPhaseKeys.ImportFetch, "Importing objects", 1, ActivityPhaseStatus.Completed),
        Phase(ActivityPhase.QualifyConnectorKey("read"), "Reading the file", 2, ActivityPhaseStatus.Completed, RunPhaseKeys.ImportFetch),
        Phase(RunPhaseKeys.ImportSave, "Saving changes", 3, ActivityPhaseStatus.Active),
        Phase(RunPhaseKeys.ImportRecordResults, "Recording results", 4, ActivityPhaseStatus.Pending)
    ];

    [Test]
    public void From_NoPhasesRecorded_IsNull()
    {
        // Clearing Connected System Objects, example data generation and factory reset are not Run
        // Profile executions and have no steps. One null check at the call site is the whole of
        // their handling; a summary of nothing would need unpicking everywhere instead.
        Assert.That(RunPhaseSummary.From([]), Is.Null);
    }

    [Test]
    public void From_OnlyConnectorPhasesRecorded_IsNull()
    {
        // A Connector's steps are detail inside a JIM step. On their own they are not a run.
        var phases = new List<ActivityPhase>
        {
            Phase(ActivityPhase.QualifyConnectorKey("read"), "Reading the file", 0, ActivityPhaseStatus.Active, RunPhaseKeys.ImportFetch)
        };

        Assert.That(RunPhaseSummary.From(phases), Is.Null);
    }

    [Test]
    public void From_Run_CountsOnlyTheRunsOwnSteps()
    {
        // Four top-level steps and one Connector step. Counting the Connector's would make the same
        // Run Profile read as "step 4 of 5" against one Connected System and "step 3 of 4" against
        // another, which is exactly what RunPhaseReading.TopLevel exists to prevent.
        var summary = RunPhaseSummary.From(ImportSaving());

        Assert.That(summary, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(summary!.TotalSteps, Is.EqualTo(4));
            Assert.That(summary.Steps, Has.Count.EqualTo(4));
            Assert.That(summary.Steps.Select(s => s.Name), Does.Not.Contain("Reading the file"));
        }
    }

    [Test]
    public void From_Run_KeepsTheStepsInRunOrder()
    {
        var summary = RunPhaseSummary.From(ImportSaving());

        Assert.That(summary!.Steps.Select(s => s.Name), Is.EqualTo(new[]
        {
            "Connecting to Connected System",
            "Importing objects",
            "Saving changes",
            "Recording results"
        }));
    }

    [Test]
    public void From_Run_CarriesEachStepsOwnOutcome()
    {
        // The rail's whole job is that a skipped step reads differently from one not reached yet.
        var summary = RunPhaseSummary.From(ImportSaving());

        Assert.That(summary!.Steps.Select(s => s.Status), Is.EqualTo(new[]
        {
            ActivityPhaseStatus.Skipped,
            ActivityPhaseStatus.Completed,
            ActivityPhaseStatus.Active,
            ActivityPhaseStatus.Pending
        }));
    }

    [Test]
    public void From_RunInFlight_NamesTheStepRunningNowAndWhereItSits()
    {
        var summary = RunPhaseSummary.From(ImportSaving());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(summary!.CurrentStepName, Is.EqualTo("Saving changes"));
            Assert.That(summary.CurrentStepNumber, Is.EqualTo(3), "1-based, so it reads as 'step 3 of 4'");
        }
    }

    [Test]
    public void From_ConnectorStepRunning_NamesTheStepHostingItRatherThanTheConnectors()
    {
        // The Activity's object counters belong to the JIM step, so that is the step the figures
        // beside this sentence are measuring. Naming the Connector's step would mislabel them.
        var phases = new List<ActivityPhase>
        {
            Phase(RunPhaseKeys.ImportFetch, "Importing objects", 0, ActivityPhaseStatus.Active),
            Phase(ActivityPhase.QualifyConnectorKey("read"), "Reading the file", 1, ActivityPhaseStatus.Active, RunPhaseKeys.ImportFetch)
        };

        var summary = RunPhaseSummary.From(phases);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(summary!.CurrentStepName, Is.EqualTo("Importing objects"));
            Assert.That(summary.CurrentStepNumber, Is.EqualTo(1));
        }
    }

    [Test]
    public void From_RunBetweenSteps_NamesNoStepRatherThanGuessing()
    {
        // A run momentarily has nothing running as it moves between steps. Holding on to the last
        // step would report work that has finished as still going.
        var phases = new List<ActivityPhase>
        {
            Phase(RunPhaseKeys.ImportFetch, "Importing objects", 0, ActivityPhaseStatus.Completed),
            Phase(RunPhaseKeys.ImportSave, "Saving changes", 1, ActivityPhaseStatus.Pending)
        };

        var summary = RunPhaseSummary.From(phases);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(summary!.CurrentStepName, Is.Null);
            Assert.That(summary.CurrentStepNumber, Is.Null);
            Assert.That(summary.TotalSteps, Is.EqualTo(2), "The run's shape is still worth showing when nothing is running");
        }
    }

    [Test]
    public void From_FinishedRun_StillCarriesTheShapeOfTheRun()
    {
        var phases = new List<ActivityPhase>
        {
            Phase(RunPhaseKeys.ImportFetch, "Importing objects", 0, ActivityPhaseStatus.Completed),
            Phase(RunPhaseKeys.ImportSave, "Saving changes", 1, ActivityPhaseStatus.Failed)
        };

        var summary = RunPhaseSummary.From(phases);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(summary!.CurrentStepName, Is.Null);
            Assert.That(summary.Steps.Last().Status, Is.EqualTo(ActivityPhaseStatus.Failed),
                "Where a run failed is the one thing a list view most needs to keep showing");
        }
    }

    [Test]
    public void From_PhasesInAnyOrder_IsSortedByTheRunsOwnOrdering()
    {
        // Rows arrive from the database in whatever order the query left them; Order is the run's
        // own sequence and is what the rail must be drawn in.
        var summary = RunPhaseSummary.From(ImportSaving().OrderByDescending(p => p.Order).ToList());

        Assert.That(summary!.Steps.Select(s => s.Order), Is.Ordered.Ascending);
    }
}
