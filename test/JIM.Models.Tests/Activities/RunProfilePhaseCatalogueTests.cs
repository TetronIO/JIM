// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Linq;
using System.Reflection;
using JIM.Models.Activities;
using JIM.Models.Staging;
using NUnit.Framework;

namespace JIM.Models.Tests.Activities;

/// <summary>
/// Guards the Run Profile phase catalogue: the single declaration of the phases JIM itself moves
/// through during a Run Profile execution (#454). The worker enters phases by key, so a key that
/// is not declared here would render as an unnamed step; these tests keep the two in step.
/// </summary>
[TestFixture]
public class RunProfilePhaseCatalogueTests
{
    private static ConnectedSystemRunType[] ExecutableRunTypes => Enum.GetValues<ConnectedSystemRunType>()
        .Where(t => t != ConnectedSystemRunType.NotSet)
        .ToArray();

    [Test]
    public void GetPhases_EveryExecutableRunType_DeclaresPhases()
    {
        foreach (var runType in ExecutableRunTypes)
        {
            var phases = RunProfilePhaseCatalogue.GetPhases(runType);
            Assert.That(phases, Is.Not.Empty, $"{runType} declares no phases, so its Activity would show an empty stepper.");
        }
    }

    [Test]
    public void GetPhases_NotSetRunType_DeclaresNoPhases()
    {
        Assert.That(RunProfilePhaseCatalogue.GetPhases(ConnectedSystemRunType.NotSet), Is.Empty);
    }

    [Test]
    public void GetPhases_EveryExecutableRunType_HasUniqueKeys()
    {
        foreach (var runType in ExecutableRunTypes)
        {
            var keys = RunProfilePhaseCatalogue.GetPhases(runType).Select(p => p.Key).ToList();
            Assert.That(keys, Is.Unique, $"{runType} declares a duplicate phase key; phases are entered by key, so duplicates are ambiguous.");
        }
    }

    [Test]
    public void GetPhases_EveryExecutableRunType_HasAtMostOneConnectorHostPhase()
    {
        foreach (var runType in ExecutableRunTypes)
        {
            var hosts = RunProfilePhaseCatalogue.GetPhases(runType).Count(p => p.HostsConnectorPhases);
            Assert.That(hosts, Is.LessThanOrEqualTo(1),
                $"{runType} declares {hosts} connector host phases; a Connector's declared phases have one place to nest.");
        }
    }

    [Test]
    public void GetPhases_ImportAndExportRunTypes_HostConnectorPhases()
    {
        // The Connector does its work inside exactly one of JIM's phases for each of these run types.
        // Without a host phase a Connector's declared phases would have nowhere to nest and would be dropped.
        ConnectedSystemRunType[] connectorRunTypes =
        [
            ConnectedSystemRunType.FullImport,
            ConnectedSystemRunType.DeltaImport,
            ConnectedSystemRunType.Export
        ];

        foreach (var runType in connectorRunTypes)
            Assert.That(RunProfilePhaseCatalogue.GetPhases(runType).Any(p => p.HostsConnectorPhases), Is.True,
                $"{runType} runs a Connector but declares no host phase for its sub-phases.");
    }

    [Test]
    public void GetPhases_SynchronisationRunTypes_DoNotHostConnectorPhases()
    {
        // Synchronisation never calls a Connector; it works entirely against the connector space.
        ConnectedSystemRunType[] syncRunTypes =
        [
            ConnectedSystemRunType.FullSynchronisation,
            ConnectedSystemRunType.DeltaSynchronisation
        ];

        foreach (var runType in syncRunTypes)
            Assert.That(RunProfilePhaseCatalogue.GetPhases(runType).Any(p => p.HostsConnectorPhases), Is.False,
                $"{runType} does not call a Connector, so it must not declare a host phase.");
    }

    /// <summary>
    /// An export's rail showed three steps while the run narrated work belonging to none of them:
    /// giving provisioned accounts their initial passwords, and selecting containers the export had
    /// just created. Both do real work against the Connected System after the objects are written,
    /// and the first writes its own message, so the message changed while the rail stood still.
    /// </summary>
    [Test]
    public void GetPhases_Export_DeclaresTheWorkThatFollowsWritingTheObjects()
    {
        var keys = RunProfilePhaseCatalogue.GetPhases(ConnectedSystemRunType.Export).Select(p => p.Key).ToList();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(keys, Does.Contain(RunPhaseKeys.ExportDeferred),
                "An export's second pass re-resolves references that did not exist yet and writes what it can; at scale that pass is most of the run.");
            Assert.That(keys, Does.Contain(RunPhaseKeys.ExportSelectNewContainers),
                "An export that creates containers then goes and selects them; that is Connected System work with no step of its own.");
            Assert.That(keys, Does.Contain(RunPhaseKeys.ExportDeliverInitialPasswords),
                "Initial password delivery opens its own connection and narrates its own outcome, so it needs a step to narrate into.");
        }
    }

    [Test]
    public void GetPhases_Export_OrdersTheStepsAsTheRunPerformsThem()
    {
        var keys = RunProfilePhaseCatalogue.GetPhases(ConnectedSystemRunType.Export).Select(p => p.Key).ToList();

        Assert.That(keys, Is.EqualTo(new[]
        {
            RunPhaseKeys.ExportPrepare,
            RunPhaseKeys.ExportExecute,
            RunPhaseKeys.ExportDeferred,
            RunPhaseKeys.ExportResolveReferences,
            RunPhaseKeys.ExportSelectNewContainers,
            RunPhaseKeys.ExportDeliverInitialPasswords
        }));
    }

    /// <summary>
    /// The Outbound Temporal Scope Reconciler's apply step (#892) runs at the end of a
    /// synchronisation, re-evaluating export scope for Metaverse Objects whose scope drifted with
    /// the clock. It batches through the flagged set and writes Pending Exports, so at scale it is
    /// time an administrator can watch pass with no step accounting for it.
    /// </summary>
    [Test]
    public void GetPhases_Synchronisation_DeclaresTheScopeReviewThatFollowsProcessing()
    {
        ConnectedSystemRunType[] syncRunTypes =
        [
            ConnectedSystemRunType.FullSynchronisation,
            ConnectedSystemRunType.DeltaSynchronisation
        ];

        foreach (var runType in syncRunTypes)
        {
            var keys = RunProfilePhaseCatalogue.GetPhases(runType).Select(p => p.Key).ToList();
            Assert.That(keys, Does.Contain(RunPhaseKeys.SyncReviewExportScope), $"{runType} performs the scope review but declares no step for it.");
            Assert.That(keys.Last(), Is.EqualTo(RunPhaseKeys.SyncReviewExportScope), $"{runType} performs the scope review last, after cross-page references are resolved.");
        }
    }

    [Test]
    public void GetPhases_EveryDeclaredPhase_HasAKeyAndName()
    {
        foreach (var runType in ExecutableRunTypes)
        {
            foreach (var phase in RunProfilePhaseCatalogue.GetPhases(runType))
            {
                Assert.That(phase.Key, Is.Not.Null.And.Not.Empty, $"{runType} declares a phase with no key.");
                Assert.That(phase.Name, Is.Not.Null.And.Not.Empty, $"{runType} phase '{phase.Key}' has no administrator-facing name.");
            }
        }
    }

    [Test]
    public void GetPhases_EveryDeclaredName_FollowsHouseStyle()
    {
        foreach (var runType in ExecutableRunTypes)
        {
            foreach (var phase in RunProfilePhaseCatalogue.GetPhases(runType))
            {
                Assert.That(phase.Name, Does.Not.Contain("—"), $"{runType} phase '{phase.Key}' uses an em dash; house style forbids them.");
                Assert.That(phase.Name, Does.Not.Contain("..."), $"{runType} phase '{phase.Key}' reads as narration; a step label is a name, not a running commentary.");
                Assert.That(phase.Name.Trim(), Is.EqualTo(phase.Name), $"{runType} phase '{phase.Key}' has leading or trailing whitespace.");
            }
        }
    }

    [Test]
    public void PhaseKeyConstants_AreAllDeclaredInACatalogue()
    {
        // An orphaned constant means a worker call site enters a phase nobody declared, which would
        // render as an ad-hoc step appended to the end of the stepper rather than in its right place.
        var declaredKeys = ExecutableRunTypes
            .SelectMany(RunProfilePhaseCatalogue.GetPhases)
            .Select(p => p.Key)
            .ToHashSet(StringComparer.Ordinal);

        var constants = typeof(RunPhaseKeys)
            .GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
            .Where(f => f is { IsLiteral: true, IsInitOnly: false } && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToList();

        Assert.That(constants, Is.Not.Empty, "No phase key constants were found; the reflection lookup is wrong.");
        foreach (var constant in constants)
            Assert.That(declaredKeys, Does.Contain(constant), $"Phase key '{constant}' is not declared by any run type's catalogue.");
    }

    [Test]
    public void GetPhases_CalledTwice_ReturnsEquivalentPhases()
    {
        // The catalogue is read once per run to seed the Activity's phases; it must not vary per call.
        foreach (var runType in ExecutableRunTypes)
        {
            var first = RunProfilePhaseCatalogue.GetPhases(runType).Select(p => p.Key);
            var second = RunProfilePhaseCatalogue.GetPhases(runType).Select(p => p.Key);
            Assert.That(second, Is.EqualTo(first), $"{runType} returned a different phase list on a second call.");
        }
    }
}
