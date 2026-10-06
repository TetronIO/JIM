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
/// Export-mode live probing (Unique Value Generation, #242, release 3, plan Phase 7) through the real Full
/// Synchronisation pipeline: Ticketing's generated loginName is itself the probed attribute.
/// </summary>
public partial class ExportGeneratedValueWorkflowTests
{
    [Test]
    public async Task FullSync_ProbeFindsTheBaseValueInTheTarget_ProvisionsWithTheNextCandidateAsync()
    {
        var ctx = await SetUpExportGenerationAsync();
        await SeedHrCsoAsync(ctx, "E1", note: null);
        var connector = new FakeProbingConnector("e1");

        var activity = await RunFullSyncWithProbingAsync(ctx, connector);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(ProvisionedLoginNames(ctx), Is.EqualTo(new[] { "e11" }));
            Assert.That(connector.Requests[0].AttributeName, Is.EqualTo("loginName"));
            Assert.That(connector.Requests[0].ControlValue, Is.Null, "a first load holds no value to vouch for the search");
            Assert.That(activity.WarningMessage, Is.Null);
            Assert.That(connector.IsOpen, Is.False);
        }
    }

    [Test]
    public async Task FullSync_TargetCannotBeReached_ProvisionsOnJimsRecordsWithAWarningAsync()
    {
        var ctx = await SetUpExportGenerationAsync();
        await SeedHrCsoAsync(ctx, "E1", note: null);
        var connector = new FakeProbingConnector { OpenThrows = new InvalidOperationException("Connection refused.") };

        var activity = await RunFullSyncWithProbingAsync(ctx, connector);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(ProvisionedLoginNames(ctx), Is.EqualTo(new[] { "e1" }));
            Assert.That(activity.WarningMessage, Is.EqualTo(
                "JIM couldn't probe Ticketing for values already in use. Connecting to it failed: Connection refused. JIM chose 1 value using its own records only."));
        }
    }

    /// <summary>
    /// The joined account's current value is the object's own: the probe would find that very account, so the value
    /// is not probed and is generated as it would be without probing.
    /// </summary>
    [Test]
    public async Task FullSync_JoinedTargetAlreadyHoldsTheBaseValue_IsNotProbedAsync()
    {
        var ctx = await SetUpExportGenerationAsync();
        var mvo = await SeedPreExistingMvoAsync(ctx, employeeId: "E1", matchKey: "P1");
        var ticketingCso = await SeedTicketingCsoAsync(ctx, mvo.Id, loginName: "e1");
        ConfigureHrToJoinByMatchKey(ctx);
        await SeedHrCsoAsync(ctx, "E1", note: "first", matchKey: "P1");
        var connector = new FakeProbingConnector("e1");

        await RunFullSyncWithProbingAsync(ctx, connector);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(SyncRepo.GeneratedValueAssignments.Values.Single().Value, Is.EqualTo("e1"));
            Assert.That(SyncRepo.GeneratedValueAssignments.Values.Single().ConnectedSystemObjectId, Is.EqualTo(ticketingCso.Id));
            Assert.That(connector.Requests.SelectMany(r => r.Candidates), Does.Not.Contain("e1"));
        }
    }

    private IReadOnlyList<string?> ProvisionedLoginNames(ExportGenerationContext ctx) =>
        SyncRepo.PendingExports.Values
            .Where(pe => pe.ConnectedSystemId == ctx.Ticketing.Id)
            .SelectMany(pe => pe.AttributeValueChanges)
            .Where(c => c.AttributeId == ctx.TicketingLoginNameAttribute.Id)
            .Select(c => c.StringValue)
            .ToList();

    private async Task<Activity> RunFullSyncWithProbingAsync(ExportGenerationContext ctx, FakeProbingConnector connector)
    {
        var ticketing = SyncRepo.ConnectedSystems[ctx.Ticketing.Id];
        ticketing.ConnectorDefinition.SupportsUniquenessProbe = true;
        ticketing.ObjectTypes ??= [];
        if (ticketing.ObjectTypes.All(t => t.Id != ctx.TicketingCsoTypeId))
            ticketing.ObjectTypes.Add(SyncRepo.ObjectTypes[ctx.TicketingCsoTypeId]);

        var host = new FakeUniquenessProbeHost { ControlValueSource = SyncRepo.GetConnectedSystemAttributeSampleValuesAsync }
            .WithSystem(ticketing, _ => connector);

        var reloaded = await ReloadEntityAsync(ctx.Hr);
        var profile = await CreateRunProfileAsync(reloaded.Id, $"{reloaded.Name} Full Sync", ConnectedSystemRunType.FullSynchronisation);
        var activity = await CreateActivityAsync(reloaded.Id, profile, ConnectedSystemRunType.FullSynchronisation);
        await new SyncFullSyncTaskProcessor(new SyncEngine(), new SyncServer(Jim), SyncRepo, reloaded, profile, activity, new CancellationTokenSource(),
                uniquenessProbeSessionHost: host)
            .PerformFullSyncAsync();
        return activity;
    }
}
