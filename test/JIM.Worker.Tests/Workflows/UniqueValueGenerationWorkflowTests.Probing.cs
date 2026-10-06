// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Servers;
using JIM.Models.Activities;
using JIM.Models.Staging;
using JIM.Worker.Processors;
using JIM.Worker.Tests.UniqueValues;
using NUnit.Framework;

namespace JIM.Worker.Tests.Workflows;

/// <summary>
/// Import-mode live probing (Unique Value Generation, #242, release 3, plan Phase 7) through the real Full
/// Synchronisation pipeline: HR's generated Account Name flows directly to Directory's sAMAccountName, so Directory
/// is probed for every candidate the local gates accept, over a scripted Connector.
/// </summary>
public partial class UniqueValueGenerationWorkflowTests
{
    [Test]
    public async Task FullSync_ProbeFindsTheBaseValueOutsideJimsRecords_GeneratesTheNextCandidateAsync()
    {
        var ctx = await SetUpDirectoryParticipantScenarioAsync();
        await SeedDirectoryCsoAsync(ctx, "E1", "jsmith");
        await RunFullSyncReturningActivityAsync(ctx.Directory!);

        // "john.smith" belongs to an account JIM has never imported: only the probe can see it.
        var connector = new FakeProbingConnector("john.smith", "jsmith");
        await SeedHrCsoAsync(ctx, "John", "Smith", "E1");
        var activity = await RunFullSyncWithProbingAsync(ctx, connector);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(ResolvedAccountNames(ctx).Single(), Is.EqualTo("john.smith1"));
            Assert.That(activity.WarningMessage, Is.Null, "every probe answered, so there is nothing to report");
            Assert.That(connector.Requests, Has.Count.EqualTo(1), "one window of candidates answers every round");
            Assert.That(connector.Requests[0].AttributeName, Is.EqualTo("sAMAccountName"));
            Assert.That(connector.Requests[0].ControlValue, Is.EqualTo("jsmith"), "the control is a value JIM knows Directory holds");
            Assert.That(connector.IsOpen, Is.False, "the run closes its probe connection when it ends");
            Assert.That(connector.CloseCount, Is.EqualTo(1));
        }
    }

    [Test]
    public async Task FullSync_DirectoryCannotBeReached_AcceptsOnJimsRecordsAndCompletesWithOneWarningAsync()
    {
        var ctx = await SetUpDirectoryParticipantScenarioAsync();
        await SeedDirectoryCsoAsync(ctx, "E1", "jsmith");
        await SeedDirectoryCsoAsync(ctx, "E2", "adal");
        await RunFullSyncReturningActivityAsync(ctx.Directory!);

        var connector = new FakeProbingConnector { OpenThrows = new InvalidOperationException("Connection refused.") };
        await SeedHrCsoAsync(ctx, "John", "Smith", "E1");
        await SeedHrCsoAsync(ctx, "Ada", "Lovelace", "E2");
        var activity = await RunFullSyncWithProbingAsync(ctx, connector);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(ResolvedAccountNames(ctx), Is.EquivalentTo(new[] { "john.smith", "ada.lovelace" }));
            Assert.That(connector.OpenCount, Is.EqualTo(1), "an unreachable system is not retried per object");
            Assert.That(activity.WarningMessage, Is.EqualTo(
                "JIM couldn't probe Directory for values already in use. Connecting to it failed: Connection refused. JIM chose 2 values using its own records only."));
            Assert.That(activity.RunProfileExecutionItems.Where(r => r.ErrorType is not null and not ActivityRunProfileExecutionItemErrorType.NotSet).Select(r => r.ErrorMessage), Is.Empty, "an unreachable target never fails the run");
        }
    }

    /// <summary>
    /// The object's own joined Directory account already holding the base value is the same person: the probe would
    /// find that very account, so the value is not probed at all.
    /// </summary>
    [Test]
    public async Task FullSync_OwnJoinedAccountHoldsTheBaseValue_IsNotProbedAndIsKeptAsync()
    {
        var ctx = await SetUpDirectoryParticipantScenarioAsync();
        await SeedDirectoryCsoAsync(ctx, "E1", "john.smith");
        await RunFullSyncReturningActivityAsync(ctx.Directory!);

        var connector = new FakeProbingConnector("john.smith");
        await SeedHrCsoAsync(ctx, "John", "Smith", "E1");
        await RunFullSyncWithProbingAsync(ctx, connector);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(ResolvedAccountNames(ctx).Single(), Is.EqualTo("john.smith"));
            Assert.That(connector.Requests.SelectMany(r => r.Candidates), Does.Not.Contain("john.smith"));
        }
    }

    [Test]
    public async Task FullSync_ReRunWithAnAssignment_ProbesNothingAsync()
    {
        var ctx = await SetUpDirectoryParticipantScenarioAsync();
        await SeedDirectoryCsoAsync(ctx, "E1", "jsmith");
        await RunFullSyncReturningActivityAsync(ctx.Directory!);
        await SeedHrCsoAsync(ctx, "John", "Smith", "E1");
        await RunFullSyncWithProbingAsync(ctx, new FakeProbingConnector("jsmith"));

        var connector = new FakeProbingConnector("jsmith");
        await RunFullSyncWithProbingAsync(ctx, connector);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(ResolvedAccountNames(ctx).Single(), Is.EqualTo("john.smith"));
            Assert.That(connector.OpenCount, Is.Zero, "a sticky value is never probed, so no connection is opened");
        }
    }

    /// <summary>
    /// Runs HR's Full Synchronisation with a probe host whose Directory Connector is <paramref name="connector"/>.
    /// </summary>
    private async Task<Activity> RunFullSyncWithProbingAsync(GenerationContext ctx, FakeProbingConnector connector)
    {
        var directory = SyncRepo.ConnectedSystems[ctx.Directory!.Id];
        directory.ConnectorDefinition.SupportsUniquenessProbe = true;
        directory.ObjectTypes ??= [];
        if (directory.ObjectTypes.All(t => t.Id != ctx.DirectoryCsoTypeId))
            directory.ObjectTypes.Add(SyncRepo.ObjectTypes[ctx.DirectoryCsoTypeId!.Value]);

        var host = new FakeUniquenessProbeHost { ControlValueSource = SyncRepo.GetConnectedSystemAttributeSampleValuesAsync }
            .WithSystem(directory, _ => connector);

        var reloaded = await ReloadEntityAsync(ctx.Hr);
        var profile = await CreateRunProfileAsync(reloaded.Id, $"{reloaded.Name} Full Sync", ConnectedSystemRunType.FullSynchronisation);
        var activity = await CreateActivityAsync(reloaded.Id, profile, ConnectedSystemRunType.FullSynchronisation);
        await new SyncFullSyncTaskProcessor(new SyncEngine(), new SyncServer(Jim), SyncRepo, reloaded, profile, activity, new CancellationTokenSource(),
                uniquenessProbeSessionHost: host)
            .PerformFullSyncAsync();
        return activity;
    }
}
