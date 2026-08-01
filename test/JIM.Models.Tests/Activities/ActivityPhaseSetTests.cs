// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Collections.Generic;
using System.Linq;
using JIM.Models.Activities;
using JIM.Models.Staging;
using NUnit.Framework;

namespace JIM.Models.Tests.Activities;

/// <summary>
/// The phase state machine behind the Activity phase stepper (#454). Kept as a pure in-memory type
/// so every transition rule (nesting, skipping, looping, failing) is provable without a database.
/// </summary>
[TestFixture]
public class ActivityPhaseSetTests
{
    private static readonly DateTime T0 = new(2026, 8, 1, 9, 0, 0, DateTimeKind.Utc);

    private static ActivityPhaseSet DeclareImport(IEnumerable<ConnectorPhase>? connectorPhases = null) =>
        ActivityPhaseSet.Declare(Guid.NewGuid(), ConnectedSystemRunType.FullImport, connectorPhases);

    #region Declare

    [Test]
    public void Declare_ImportRunType_RecordsTheCataloguePhasesInOrder()
    {
        var set = DeclareImport();

        var expected = RunProfilePhaseCatalogue.GetPhases(ConnectedSystemRunType.FullImport).Select(p => p.Key);
        Assert.That(set.Phases.Select(p => p.Key), Is.EqualTo(expected));
        Assert.That(set.Phases.Select(p => p.Order), Is.EqualTo(Enumerable.Range(0, set.Phases.Count)));
        Assert.That(set.Phases.All(p => p.Status == ActivityPhaseStatus.Pending), Is.True);
        Assert.That(set.Phases.All(p => p.Started == null && p.Ended == null), Is.True);
    }

    [Test]
    public void Declare_UnknownRunType_RecordsNoPhases()
    {
        var set = ActivityPhaseSet.Declare(Guid.NewGuid(), ConnectedSystemRunType.NotSet, null);

        Assert.That(set.Phases, Is.Empty);
    }

    [Test]
    public void Declare_WithConnectorPhases_NestsThemInsideTheHostPhase()
    {
        var set = DeclareImport([new ConnectorPhase("read", "Reading file"), new ConnectorPhase("parse", "Parsing rows")]);

        var hostIndex = set.Phases.ToList().FindIndex(p => p.Key == RunPhaseKeys.ImportFetch);
        var read = set.Phases[hostIndex + 1];
        var parse = set.Phases[hostIndex + 2];

        Assert.That(read.Key, Is.EqualTo(ActivityPhase.QualifyConnectorKey("read")));
        Assert.That(read.Name, Is.EqualTo("Reading file"));
        Assert.That(read.ParentKey, Is.EqualTo(RunPhaseKeys.ImportFetch));
        Assert.That(parse.Key, Is.EqualTo(ActivityPhase.QualifyConnectorKey("parse")));
        Assert.That(parse.ParentKey, Is.EqualTo(RunPhaseKeys.ImportFetch));
    }

    [Test]
    public void Declare_WithConnectorPhases_LeavesJimPhasesWithoutAParent()
    {
        var set = DeclareImport([new ConnectorPhase("read", "Reading file")]);

        var jimPhases = set.Phases.Where(p => !p.Key.StartsWith(ActivityPhase.ConnectorPhaseKeyPrefix, StringComparison.Ordinal));
        Assert.That(jimPhases.All(p => p.ParentKey == null), Is.True);
    }

    [Test]
    public void Declare_ConnectorPhasesForARunTypeWithNoHost_IgnoresThem()
    {
        // Synchronisation never calls a Connector, so there is nowhere to nest and nothing to show.
        var set = ActivityPhaseSet.Declare(Guid.NewGuid(), ConnectedSystemRunType.FullSynchronisation,
            [new ConnectorPhase("read", "Reading file")]);

        Assert.That(set.Phases.Any(p => p.ParentKey != null), Is.False);
        Assert.That(set.Phases.Count, Is.EqualTo(RunProfilePhaseCatalogue.GetPhases(ConnectedSystemRunType.FullSynchronisation).Count));
    }

    [Test]
    public void Declare_DuplicateConnectorPhaseKeys_KeepsOnlyTheFirst()
    {
        var set = DeclareImport([new ConnectorPhase("read", "Reading file"), new ConnectorPhase("read", "Reading file again")]);

        Assert.That(set.Phases.Count(p => p.Key == ActivityPhase.QualifyConnectorKey("read")), Is.EqualTo(1));
        Assert.That(set.Phases.Single(p => p.Key == ActivityPhase.QualifyConnectorKey("read")).Name, Is.EqualTo("Reading file"));
    }

    #endregion

    #region Enter

    [Test]
    public void Enter_FirstPhase_MarksItActiveAndStamped()
    {
        var set = DeclareImport();

        var changed = set.Enter(RunPhaseKeys.ImportConnect, T0);

        var phase = set.Phases.Single(p => p.Key == RunPhaseKeys.ImportConnect);
        Assert.That(phase.Status, Is.EqualTo(ActivityPhaseStatus.Active));
        Assert.That(phase.Started, Is.EqualTo(T0));
        Assert.That(phase.Ended, Is.Null);
        Assert.That(changed, Does.Contain(phase));
    }

    [Test]
    public void Enter_NextPhase_CompletesThePreviousOne()
    {
        var set = DeclareImport();
        set.Enter(RunPhaseKeys.ImportConnect, T0);

        set.Enter(RunPhaseKeys.ImportFetch, T0.AddSeconds(30));

        var connect = set.Phases.Single(p => p.Key == RunPhaseKeys.ImportConnect);
        Assert.That(connect.Status, Is.EqualTo(ActivityPhaseStatus.Completed));
        Assert.That(connect.Ended, Is.EqualTo(T0.AddSeconds(30)));
        Assert.That(connect.Duration, Is.EqualTo(TimeSpan.FromSeconds(30)));
    }

    [Test]
    public void Enter_SkippingOverPendingPhases_MarksThemSkipped()
    {
        // A Delta Import performs no deletion detection, so that phase is never entered.
        var set = DeclareImport();
        set.Enter(RunPhaseKeys.ImportFetch, T0);

        set.Enter(RunPhaseKeys.ImportSave, T0.AddMinutes(1));

        var deletions = set.Phases.Single(p => p.Key == RunPhaseKeys.ImportDeletions);
        var references = set.Phases.Single(p => p.Key == RunPhaseKeys.ImportResolveReferences);
        Assert.That(deletions.Status, Is.EqualTo(ActivityPhaseStatus.Skipped));
        Assert.That(references.Status, Is.EqualTo(ActivityPhaseStatus.Skipped));
        Assert.That(deletions.Started, Is.Null, "A skipped phase never ran, so it has no duration to show.");
    }

    [Test]
    public void Enter_SamePhaseTwice_DoesNotRestartIt()
    {
        var set = DeclareImport();
        set.Enter(RunPhaseKeys.ImportFetch, T0);

        var changed = set.Enter(RunPhaseKeys.ImportFetch, T0.AddSeconds(10));

        var fetch = set.Phases.Single(p => p.Key == RunPhaseKeys.ImportFetch);
        Assert.That(fetch.Started, Is.EqualTo(T0));
        Assert.That(fetch.Status, Is.EqualTo(ActivityPhaseStatus.Active));
        Assert.That(changed, Is.Empty, "Re-entering the active phase changes nothing, so there is nothing to persist.");
    }

    [Test]
    public void Enter_ConnectorPhase_KeepsItsHostPhaseActive()
    {
        var set = DeclareImport([new ConnectorPhase("read", "Reading file")]);
        set.Enter(RunPhaseKeys.ImportFetch, T0);

        set.Enter(ActivityPhase.QualifyConnectorKey("read"), T0.AddSeconds(5));

        Assert.That(set.Phases.Single(p => p.Key == RunPhaseKeys.ImportFetch).Status, Is.EqualTo(ActivityPhaseStatus.Active));
        Assert.That(set.Phases.Single(p => p.Key == ActivityPhase.QualifyConnectorKey("read")).Status, Is.EqualTo(ActivityPhaseStatus.Active));
    }

    [Test]
    public void Enter_PhaseAfterTheHost_CompletesBothTheHostAndItsConnectorPhase()
    {
        var set = DeclareImport([new ConnectorPhase("read", "Reading file")]);
        set.Enter(RunPhaseKeys.ImportFetch, T0);
        set.Enter(ActivityPhase.QualifyConnectorKey("read"), T0.AddSeconds(5));

        set.Enter(RunPhaseKeys.ImportSave, T0.AddMinutes(2));

        Assert.That(set.Phases.Single(p => p.Key == RunPhaseKeys.ImportFetch).Status, Is.EqualTo(ActivityPhaseStatus.Completed));
        Assert.That(set.Phases.Single(p => p.Key == ActivityPhase.QualifyConnectorKey("read")).Status, Is.EqualTo(ActivityPhaseStatus.Completed));
    }

    [Test]
    public void Enter_AnotherConnectorPhase_CompletesThePreviousConnectorPhaseOnly()
    {
        var set = DeclareImport([new ConnectorPhase("read", "Reading file"), new ConnectorPhase("parse", "Parsing rows")]);
        set.Enter(RunPhaseKeys.ImportFetch, T0);
        set.Enter(ActivityPhase.QualifyConnectorKey("read"), T0.AddSeconds(5));

        set.Enter(ActivityPhase.QualifyConnectorKey("parse"), T0.AddSeconds(20));

        Assert.That(set.Phases.Single(p => p.Key == ActivityPhase.QualifyConnectorKey("read")).Status, Is.EqualTo(ActivityPhaseStatus.Completed));
        Assert.That(set.Phases.Single(p => p.Key == ActivityPhase.QualifyConnectorKey("parse")).Status, Is.EqualTo(ActivityPhaseStatus.Active));
        Assert.That(set.Phases.Single(p => p.Key == RunPhaseKeys.ImportFetch).Status, Is.EqualTo(ActivityPhaseStatus.Active));
    }

    [Test]
    public void Enter_ACompletedPhaseAgain_ReopensItRatherThanDuplicatingIt()
    {
        // Paged imports loop: fetch a page, parse it, fetch the next. The second fetch is the same
        // step doing more of the same work, so its duration should cover the loop, not restart.
        var set = DeclareImport([new ConnectorPhase("fetch", "Fetching objects"), new ConnectorPhase("parse", "Parsing rows")]);
        set.Enter(RunPhaseKeys.ImportFetch, T0);
        set.Enter(ActivityPhase.QualifyConnectorKey("fetch"), T0.AddSeconds(1));
        set.Enter(ActivityPhase.QualifyConnectorKey("parse"), T0.AddSeconds(10));

        set.Enter(ActivityPhase.QualifyConnectorKey("fetch"), T0.AddSeconds(20));

        var fetch = set.Phases.Single(p => p.Key == ActivityPhase.QualifyConnectorKey("fetch"));
        Assert.That(set.Phases.Count(p => p.Key == ActivityPhase.QualifyConnectorKey("fetch")), Is.EqualTo(1));
        Assert.That(fetch.Status, Is.EqualTo(ActivityPhaseStatus.Active));
        Assert.That(fetch.Started, Is.EqualTo(T0.AddSeconds(1)), "The step started when it first ran; re-entering it does not reset that.");
        Assert.That(fetch.Ended, Is.Null);
    }

    [Test]
    public void Enter_UndeclaredKey_AppendsAStepRatherThanLosingTheNarration()
    {
        // A Connector that narrates a phase it did not declare must not blank the stepper.
        var set = DeclareImport();
        set.Enter(RunPhaseKeys.ImportFetch, T0);

        set.Enter(ActivityPhase.QualifyConnectorKey("surprise"), T0.AddSeconds(5), "Doing something unexpected");

        var added = set.Phases.Single(p => p.Key == ActivityPhase.QualifyConnectorKey("surprise"));
        Assert.That(added.Status, Is.EqualTo(ActivityPhaseStatus.Active));
        Assert.That(added.Name, Is.EqualTo("Doing something unexpected"));
        Assert.That(added.Order, Is.EqualTo(set.Phases.Max(p => p.Order)));
        Assert.That(added.ParentKey, Is.EqualTo(RunPhaseKeys.ImportFetch), "An undeclared Connector phase still belongs to the phase hosting the Connector.");
    }

    [Test]
    public void Enter_UndeclaredKeyWithNoName_FallsBackToAReadableLabel()
    {
        var set = DeclareImport();
        set.Enter(RunPhaseKeys.ImportFetch, T0);

        set.Enter(ActivityPhase.QualifyConnectorKey("load-existing-file"), T0.AddSeconds(1));

        var added = set.Phases.Single(p => p.Key == ActivityPhase.QualifyConnectorKey("load-existing-file"));
        Assert.That(added.Name, Is.Not.Null.And.Not.Empty);
        Assert.That(added.Name, Does.Not.Contain(ActivityPhase.ConnectorPhaseKeyPrefix));
    }

    [Test]
    public void Enter_UnknownKeyOnAnEmptySet_IsIgnored()
    {
        var set = ActivityPhaseSet.Declare(Guid.NewGuid(), ConnectedSystemRunType.NotSet, null);

        var changed = set.Enter("whatever", T0);

        Assert.That(changed, Is.Empty);
        Assert.That(set.Phases, Is.Empty);
    }

    #endregion

    #region Finish

    [Test]
    public void Finish_Successfully_CompletesTheActivePhaseAndSkipsTheRest()
    {
        var set = DeclareImport();
        set.Enter(RunPhaseKeys.ImportFetch, T0);

        set.Finish(T0.AddMinutes(5), failed: false);

        Assert.That(set.Phases.Single(p => p.Key == RunPhaseKeys.ImportFetch).Status, Is.EqualTo(ActivityPhaseStatus.Completed));
        Assert.That(set.Phases.Single(p => p.Key == RunPhaseKeys.ImportFetch).Ended, Is.EqualTo(T0.AddMinutes(5)));
        Assert.That(set.Phases.Where(p => p.Key != RunPhaseKeys.ImportFetch && p.Key != RunPhaseKeys.ImportConnect)
            .All(p => p.Status == ActivityPhaseStatus.Skipped), Is.True);
    }

    [Test]
    public void Finish_AfterAFailure_MarksTheActivePhaseFailed()
    {
        var set = DeclareImport();
        set.Enter(RunPhaseKeys.ImportSave, T0);

        set.Finish(T0.AddMinutes(1), failed: true);

        var save = set.Phases.Single(p => p.Key == RunPhaseKeys.ImportSave);
        Assert.That(save.Status, Is.EqualTo(ActivityPhaseStatus.Failed), "The step that was running when the run failed is where an administrator needs to look.");
        Assert.That(save.Ended, Is.EqualTo(T0.AddMinutes(1)));
    }

    [Test]
    public void Finish_AfterAFailure_SkipsPhasesThatNeverRan()
    {
        var set = DeclareImport();
        set.Enter(RunPhaseKeys.ImportFetch, T0);

        set.Finish(T0.AddMinutes(1), failed: true);

        Assert.That(set.Phases.Single(p => p.Key == RunPhaseKeys.ImportSave).Status, Is.EqualTo(ActivityPhaseStatus.Skipped));
    }

    [Test]
    public void Finish_Twice_IsHarmless()
    {
        var set = DeclareImport();
        set.Enter(RunPhaseKeys.ImportFetch, T0);
        set.Finish(T0.AddMinutes(1), failed: false);

        var changed = set.Finish(T0.AddMinutes(2), failed: false);

        Assert.That(changed, Is.Empty);
        Assert.That(set.Phases.Single(p => p.Key == RunPhaseKeys.ImportFetch).Ended, Is.EqualTo(T0.AddMinutes(1)));
    }

    #endregion

    #region Change tracking

    [Test]
    public void Enter_ReportsOnlyTheRowsThatChanged()
    {
        var set = DeclareImport();
        set.Enter(RunPhaseKeys.ImportConnect, T0);

        var changed = set.Enter(RunPhaseKeys.ImportSave, T0.AddSeconds(5));

        // Completed: connect. Skipped: fetch, deletions, references. Active: save.
        Assert.That(changed.Select(p => p.Key), Is.EquivalentTo(new[]
        {
            RunPhaseKeys.ImportConnect,
            RunPhaseKeys.ImportFetch,
            RunPhaseKeys.ImportDeletions,
            RunPhaseKeys.ImportResolveReferences,
            RunPhaseKeys.ImportSave
        }));
    }

    #endregion
}
