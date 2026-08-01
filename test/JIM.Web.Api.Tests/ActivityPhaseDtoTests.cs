// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Collections.Generic;
using System.Linq;
using JIM.Models.Activities;
using JIM.Web.Models;
using JIM.Web.Models.Api;
using NUnit.Framework;

namespace JIM.Web.Api.Tests;

/// <summary>
/// The API's view of a run's steps (#454). Automation reads these to show or act on where a run has
/// got to, so the shape has to answer "which step, out of how many, and how long did the finished
/// ones take" without the caller parsing an English sentence.
/// </summary>
[TestFixture]
public class ActivityPhaseDtoTests
{
    private static readonly DateTime Started = new(2026, 8, 1, 10, 0, 0, DateTimeKind.Utc);

    private static ActivityPhase Phase(
        string key,
        string name,
        int order,
        ActivityPhaseStatus status,
        string? parentKey = null,
        DateTime? started = null,
        DateTime? ended = null) => new()
    {
        Id = Guid.NewGuid(),
        ActivityId = Guid.NewGuid(),
        Key = key,
        Name = name,
        Order = order,
        Status = status,
        ParentKey = parentKey,
        Started = started,
        Ended = ended
    };

    private static ActivityProgress ProgressWith(params ActivityPhase[] phases) => new()
    {
        ActivityId = Guid.NewGuid(),
        Status = ActivityStatus.InProgress,
        ObjectsProcessed = 10,
        ObjectsToProcess = 100,
        Created = Started,
        Executed = Started,
        Phases = phases.ToList()
    };

    [Test]
    public void FromEntity_CompletedPhase_ReportsItsDuration()
    {
        var phase = Phase(RunPhaseKeys.ImportSave, "Saving changes", 4, ActivityPhaseStatus.Completed,
            started: Started, ended: Started.AddMinutes(22));

        var dto = ActivityPhaseDto.FromEntity(phase);

        Assert.That(dto.DurationSeconds, Is.EqualTo(TimeSpan.FromMinutes(22).TotalSeconds));
        Assert.That(dto.Name, Is.EqualTo("Saving changes"));
        Assert.That(dto.Status, Is.EqualTo(ActivityPhaseStatus.Completed));
    }

    [Test]
    public void FromEntity_RunningPhase_HasNoDurationYet()
    {
        var phase = Phase(RunPhaseKeys.ImportFetch, "Importing objects", 1, ActivityPhaseStatus.Active, started: Started);

        var dto = ActivityPhaseDto.FromEntity(phase);

        Assert.That(dto.DurationSeconds, Is.Null);
        Assert.That(dto.Ended, Is.Null);
    }

    [Test]
    public void FromEntity_ConnectorPhase_CarriesTheStepItRunsInside()
    {
        var phase = Phase(ActivityPhase.QualifyConnectorKey("read"), "Reading the file", 2,
            ActivityPhaseStatus.Active, parentKey: RunPhaseKeys.ImportFetch, started: Started);

        var dto = ActivityPhaseDto.FromEntity(phase);

        Assert.That(dto.ParentKey, Is.EqualTo(RunPhaseKeys.ImportFetch));
    }

    [Test]
    public void ProgressDto_WithPhases_ReportsTheStepPositionAmongTopLevelSteps()
    {
        var progress = ProgressWith(
            Phase(RunPhaseKeys.ImportConnect, "Connecting to Connected System", 0, ActivityPhaseStatus.Completed, started: Started, ended: Started.AddSeconds(2)),
            Phase(RunPhaseKeys.ImportFetch, "Importing objects", 1, ActivityPhaseStatus.Completed, started: Started.AddSeconds(2), ended: Started.AddMinutes(4)),
            Phase(RunPhaseKeys.ImportSave, "Saving changes", 2, ActivityPhaseStatus.Active, started: Started.AddMinutes(4)),
            Phase(RunPhaseKeys.ImportReconcile, "Reconciling Pending Exports", 3, ActivityPhaseStatus.Pending));

        var dto = ActivityProgressDto.FromEntity(progress, new ActivityEtaEstimate(null, null), Started.AddMinutes(5));

        Assert.That(dto.TotalPhases, Is.EqualTo(4));
        Assert.That(dto.CurrentPhaseNumber, Is.EqualTo(3));
        Assert.That(dto.CurrentPhase!.Key, Is.EqualTo(RunPhaseKeys.ImportSave));
    }

    [Test]
    public void ProgressDto_InsideAConnectorPhase_CountsTheStepThatCalledTheConnectorAsync()
    {
        // "Step 2 of 4" must stay true whichever Connector is in use, so a Connector's own steps are
        // detail within the step that called it rather than steps of the run in their own right.
        var progress = ProgressWith(
            Phase(RunPhaseKeys.ImportConnect, "Connecting to Connected System", 0, ActivityPhaseStatus.Completed, started: Started, ended: Started.AddSeconds(2)),
            Phase(RunPhaseKeys.ImportFetch, "Importing objects", 1, ActivityPhaseStatus.Active, started: Started.AddSeconds(2)),
            Phase(ActivityPhase.QualifyConnectorKey("fetch"), "Fetching objects", 2, ActivityPhaseStatus.Active,
                parentKey: RunPhaseKeys.ImportFetch, started: Started.AddSeconds(3)),
            Phase(RunPhaseKeys.ImportSave, "Saving changes", 3, ActivityPhaseStatus.Pending));

        var dto = ActivityProgressDto.FromEntity(progress, new ActivityEtaEstimate(null, null), Started.AddMinutes(1));

        Assert.That(dto.TotalPhases, Is.EqualTo(3), "Connector steps are not steps of the run in their own right");
        Assert.That(dto.CurrentPhaseNumber, Is.EqualTo(2));
        Assert.That(dto.CurrentPhase!.Key, Is.EqualTo(ActivityPhase.QualifyConnectorKey("fetch")),
            "The step shown is the most specific one running, so an administrator sees what the Connector is doing");
    }

    [Test]
    public void ProgressDto_NothingRunning_ReportsNoCurrentStep()
    {
        var progress = ProgressWith(
            Phase(RunPhaseKeys.ImportFetch, "Importing objects", 0, ActivityPhaseStatus.Completed, started: Started, ended: Started.AddMinutes(1)),
            Phase(RunPhaseKeys.ImportSave, "Saving changes", 1, ActivityPhaseStatus.Completed, started: Started.AddMinutes(1), ended: Started.AddMinutes(2)));

        var dto = ActivityProgressDto.FromEntity(progress, new ActivityEtaEstimate(null, null), Started.AddMinutes(3));

        Assert.That(dto.CurrentPhase, Is.Null);
        Assert.That(dto.CurrentPhaseNumber, Is.Null);
        Assert.That(dto.TotalPhases, Is.EqualTo(2));
    }

    [Test]
    public void ProgressDto_RunWithNoRecordedPhases_ReportsNoneWithoutFailing()
    {
        // Activities that are not Run Profile executions, and runs that predate phase recording.
        var progress = ProgressWith();

        var dto = ActivityProgressDto.FromEntity(progress, new ActivityEtaEstimate(null, null), Started);

        Assert.That(dto.Phases, Is.Empty);
        Assert.That(dto.CurrentPhase, Is.Null);
        Assert.That(dto.CurrentPhaseNumber, Is.Null);
        Assert.That(dto.TotalPhases, Is.Zero);
    }

    [Test]
    public void ProgressDto_WithPhases_PreservesRunOrder()
    {
        var progress = ProgressWith(
            Phase(RunPhaseKeys.ImportSave, "Saving changes", 2, ActivityPhaseStatus.Pending),
            Phase(RunPhaseKeys.ImportConnect, "Connecting to Connected System", 0, ActivityPhaseStatus.Completed, started: Started, ended: Started.AddSeconds(1)),
            Phase(RunPhaseKeys.ImportFetch, "Importing objects", 1, ActivityPhaseStatus.Active, started: Started.AddSeconds(1)));

        var dto = ActivityProgressDto.FromEntity(progress, new ActivityEtaEstimate(null, null), Started.AddMinutes(1));

        Assert.That(dto.Phases.Select(p => p.Order), Is.Ordered);
    }
}
