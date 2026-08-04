// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Collections.Generic;
using System.Linq;
using JIM.Models.Activities;
using NUnit.Framework;

namespace JIM.Models.Tests.Activities;

/// <summary>
/// Reading a run's steps. Three surfaces answer "which step is this, and where does it sit in the
/// run?": the progress API, the portal's stepper, and the portal's progress readout. They had
/// begun to answer it three separate times, so the rules live here and are pinned once.
/// </summary>
[TestFixture]
public class RunPhaseReadingTests
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
    /// An import midway through fetching, with the Connector reporting a step of its own.
    /// </summary>
    private static List<ActivityPhase> ImportFetching() =>
    [
        Phase(RunPhaseKeys.ImportSave, "Saving changes", 3, ActivityPhaseStatus.Pending),
        Phase(ActivityPhase.QualifyConnectorKey("read"), "Reading the file", 2, ActivityPhaseStatus.Active, RunPhaseKeys.ImportFetch),
        Phase(RunPhaseKeys.ImportFetch, "Importing objects", 1, ActivityPhaseStatus.Active),
        Phase(RunPhaseKeys.ImportConnect, "Connecting to Connected System", 0, ActivityPhaseStatus.Completed)
    ];

    [Test]
    public void TopLevel_ReturnsTheRunsOwnStepsInRunOrder()
    {
        // A Connector's steps are detail inside the step that called it, so counting them would
        // make the same run read differently per Connector.
        var topLevel = RunPhaseReading.TopLevel(ImportFetching());

        Assert.That(topLevel.Select(p => p.Name), Is.EqualTo(new[]
        {
            "Connecting to Connected System",
            "Importing objects",
            "Saving changes"
        }));
    }

    [Test]
    public void ActiveTopLevel_ConnectorStepRunning_ReturnsTheStepHostingIt()
    {
        // The counters belong to the JIM step, not to the Connector's step inside it, so this is
        // what a progress readout must name.
        var active = RunPhaseReading.ActiveTopLevel(ImportFetching());

        Assert.That(active?.Name, Is.EqualTo("Importing objects"));
    }

    [Test]
    public void ActiveTopLevel_NothingRunning_ReturnsNull()
    {
        var phases = new List<ActivityPhase>
        {
            Phase(RunPhaseKeys.ImportFetch, "Importing objects", 0, ActivityPhaseStatus.Completed),
            Phase(RunPhaseKeys.ImportSave, "Saving changes", 1, ActivityPhaseStatus.Completed)
        };

        Assert.That(RunPhaseReading.ActiveTopLevel(phases), Is.Null);
    }

    [Test]
    public void PositionOf_ReturnsTheStepsPlaceAmongTheRunsOwnSteps()
    {
        var phases = ImportFetching();
        var active = RunPhaseReading.ActiveTopLevel(phases);

        Assert.That(RunPhaseReading.PositionOf(phases, active), Is.EqualTo(2), "1-based, so it reads as 'step 2 of 3'");
    }

    [Test]
    public void PositionOf_ConnectorStep_HasNoPositionOfItsOwn()
    {
        var phases = ImportFetching();
        var connectorStep = phases.Single(p => p.ParentKey != null);

        Assert.That(RunPhaseReading.PositionOf(phases, connectorStep), Is.Null);
    }

    [Test]
    public void PositionOf_NoStep_HasNoPosition()
    {
        Assert.That(RunPhaseReading.PositionOf(ImportFetching(), null), Is.Null);
    }

    [Test]
    public void TopLevel_NoPhases_IsEmptyRatherThanThrowing()
    {
        // Activities that are not Run Profile executions, and runs predating step recording.
        Assert.Multiple(() =>
        {
            Assert.That(RunPhaseReading.TopLevel([]), Is.Empty);
            Assert.That(RunPhaseReading.ActiveTopLevel([]), Is.Null);
        });
    }
}
