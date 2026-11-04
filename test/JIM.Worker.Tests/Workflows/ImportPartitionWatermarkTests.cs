// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Interfaces;
using JIM.Application.Servers;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Interfaces;
using JIM.Models.Staging;
using JIM.Models.Tasking;
using JIM.Worker.Processors;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;

namespace JIM.Worker.Tests.Workflows;

/// <summary>
/// #2715: the import processor's side of per-Partition Delta Import watermarks. A Connector that keeps a watermark per
/// Partition (<see cref="IConnectorPartitionWatermarks"/>) is handed the recorded value of every Partition the run
/// targets, once, before the connection opens, and returns what it reached in
/// <see cref="ConnectedSystemImportResult.PartitionWatermarks"/>. JIM records those values, with the Connected System's
/// persisted connector data, only once the run has staged everything it read, and only for the Partitions the run read,
/// so a Run Profile that reads one Partition never moves another's starting point. Every way out that keeps the
/// Connected System-level watermark (#1868, #2089, #2720, the close-connection override) keeps every Partition's too.
/// </summary>
[TestFixture]
public class ImportPartitionWatermarkTests : WorkflowTestBase
{
    /// <summary>When the seeded watermarks were recorded: long enough ago that any write by the run is later.</summary>
    private static readonly DateTime LongAgo = new(2026, 1, 1, 9, 0, 0, DateTimeKind.Utc);

    private ConnectedSystem _system = null!;
    private ConnectedSystemObjectType _type = null!;
    private ConnectedSystemPartition _corp = null!;
    private ConnectedSystemPartition _partners = null!;

    [SetUp]
    public async Task SetUpAsync()
    {
        _system = await CreateConnectedSystemAsync("Directory");
        _type = await CreateCsoTypeAsync(_system.Id, "user");
        _corp = await CreatePartitionAsync("dc=corp,dc=local", "corp.local");
        _partners = await CreatePartitionAsync("dc=partners,dc=local", "partners.local");
    }

    #region What the Connector is handed

    [Test]
    public async Task DeltaImport_TargetsOnePartition_HandsTheConnectorOnlyThatPartitionsWatermarkAsync()
    {
        await SeedWatermarkAsync(_corp, "corp-1");
        await SeedWatermarkAsync(_partners, "partners-1");
        var connector = new PartitionWatermarkTestConnector([[NewObject()]]);

        await (await PrepareAsync(connector, partition: _corp)).Processor.PerformImportAsync();

        Assert.That(connector.WatermarksSet.Single(), Is.EqualTo(new Dictionary<int, string?> { [_corp.Id] = "corp-1" }));
    }

    [Test]
    public async Task DeltaImport_PartitionWithNoRecordedWatermark_HandsTheConnectorNullForItAsync()
    {
        await SeedWatermarkAsync(_corp, "corp-1");
        var connector = new PartitionWatermarkTestConnector([[NewObject()]]);

        await (await PrepareAsync(connector)).Processor.PerformImportAsync();

        Assert.That(connector.WatermarksSet.Single(), Is.EqualTo(new Dictionary<int, string?> { [_corp.Id] = "corp-1", [_partners.Id] = null }));
    }

    [Test]
    public async Task Import_ConnectorKeepsPartitionWatermarks_HandsThemOverOnceBeforeOpeningTheConnectionAsync()
    {
        // Before the connection opens, as the certificate provider and the SCIM store are, so nothing the Connector does
        // on connecting or on its first page can miss them.
        var connector = new PartitionWatermarkTestConnector([[NewObject()], [NewObject()]]);

        await (await PrepareAsync(connector)).Processor.PerformImportAsync();

        Assert.That(connector.Calls, Is.EqualTo(new[] { "Set", "Open", "Import", "Import", "Close" }));
    }

    [Test]
    public async Task DeltaImport_LaterPagesAreHandedTheSameWatermarksAsTheFirstAsync()
    {
        // Whatever the first page returns is only held until the end of the run: an LDAP paging cookie built from the
        // values on page 1 must still match on page 2.
        await SeedWatermarkAsync(_corp, "corp-1");
        var connector = new PartitionWatermarkTestConnector([[NewObject()], [NewObject()]],
            partitionWatermarksByPage: [new() { [_corp.Id] = "corp-2", [_partners.Id] = "partners-1" }]);

        await (await PrepareAsync(connector)).Processor.PerformImportAsync();

        var asHanded = new Dictionary<int, string?> { [_corp.Id] = "corp-1", [_partners.Id] = null };
        using (Assert.EnterMultipleScope())
        {
            Assert.That(connector.WatermarksSet, Has.Count.EqualTo(1), "the values are handed over once per run");
            Assert.That(connector.WatermarksHeldByPage, Is.EqualTo(new[] { asHanded, asHanded }));
        }
    }

    [Test]
    public async Task Import_ConnectedSystemWithoutPartitions_HandsTheConnectorNoWatermarksAsync()
    {
        var system = await CreateConnectedSystemAsync("Database");
        var type = await CreateCsoTypeAsync(system.Id, "user");
        var connector = new PartitionWatermarkTestConnector([[NewObject(type)]]);

        await (await PrepareAsync(connector, system: system)).Processor.PerformImportAsync();

        Assert.That(connector.WatermarksSet.Single(), Is.Empty);
    }

    #endregion

    #region What is recorded

    [TestCase(ConnectedSystemRunType.DeltaImport)]
    [TestCase(ConnectedSystemRunType.FullImport)]
    public async Task Import_CompletesNormally_RecordsTheWatermarkOfEveryTargetedPartitionAsync(ConnectedSystemRunType runType)
    {
        await SeedWatermarkAsync(_corp, "corp-1");
        var connector = new PartitionWatermarkTestConnector([[NewObject()]],
            partitionWatermarksByPage: [new() { [_corp.Id] = "corp-2", [_partners.Id] = "partners-1" }]);

        await (await PrepareAsync(connector, runType)).Processor.PerformImportAsync();

        var recorded = await RecordedWatermarksAsync();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(recorded.ToDictionary(r => r.Key, r => r.Value.Value),
                Is.EqualTo(new Dictionary<int, string> { [_corp.Id] = "corp-2", [_partners.Id] = "partners-1" }));
            Assert.That(recorded.Values.Select(r => r.RecordedAt), Is.All.GreaterThan(LongAgo));
        }
    }

    [Test]
    public async Task DeltaImport_OfOnePartition_LeavesTheOtherPartitionsWatermarkUnchangedAsync()
    {
        // #2715 at the processor: the run read Corp only, so Partners' next Delta Import must still start where it was.
        await SeedWatermarkAsync(_corp, "corp-1");
        await SeedWatermarkAsync(_partners, "partners-1");
        var connector = new PartitionWatermarkTestConnector([[NewObject()]],
            partitionWatermarksByPage: [new() { [_corp.Id] = "corp-2" }]);

        await (await PrepareAsync(connector, partition: _corp)).Processor.PerformImportAsync();

        var recorded = await RecordedWatermarksAsync();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(recorded[_corp.Id].Value, Is.EqualTo("corp-2"));
            Assert.That(recorded[_partners.Id], Is.EqualTo(("partners-1", LongAgo)), "neither its value nor when it was recorded moves");
        }
    }

    [Test]
    public async Task FullImport_TargetsOnePartition_LeavesTheOtherPartitionsWatermarkUnchangedAsync()
    {
        // The scoped Full Import half of #2715.
        await SeedWatermarkAsync(_corp, "corp-1");
        await SeedWatermarkAsync(_partners, "partners-1");
        var connector = new PartitionWatermarkTestConnector([[NewObject()]],
            partitionWatermarksByPage: [new() { [_corp.Id] = "corp-2" }]);

        await (await PrepareAsync(connector, ConnectedSystemRunType.FullImport, partition: _corp)).Processor.PerformImportAsync();

        var recorded = await RecordedWatermarksAsync();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(recorded[_corp.Id].Value, Is.EqualTo("corp-2"));
            Assert.That(recorded[_partners.Id], Is.EqualTo(("partners-1", LongAgo)));
        }
    }

    [Test]
    public async Task DeltaImport_ConnectorReturnsNullForAPartition_RemovesThatPartitionsWatermarkAsync()
    {
        await SeedWatermarkAsync(_corp, "corp-1");
        await SeedWatermarkAsync(_partners, "partners-1");
        var connector = new PartitionWatermarkTestConnector([[NewObject()]],
            partitionWatermarksByPage: [new() { [_corp.Id] = "corp-2", [_partners.Id] = null }]);

        await (await PrepareAsync(connector)).Processor.PerformImportAsync();

        var recorded = await RecordedWatermarksAsync();
        Assert.That(recorded.ToDictionary(r => r.Key, r => r.Value.Value), Is.EqualTo(new Dictionary<int, string> { [_corp.Id] = "corp-2" }),
            "a null clears the Partition's watermark, so its next Delta Import reads every object in it");
    }

    [Test]
    public async Task DeltaImport_ConnectorLeavesAPartitionOut_KeepsThatPartitionsWatermarkAsync()
    {
        await SeedWatermarkAsync(_corp, "corp-1");
        await SeedWatermarkAsync(_partners, "partners-1");
        var connector = new PartitionWatermarkTestConnector([[NewObject()]],
            partitionWatermarksByPage: [new() { [_corp.Id] = "corp-2" }]);

        await (await PrepareAsync(connector)).Processor.PerformImportAsync();

        Assert.That((await RecordedWatermarksAsync())[_partners.Id], Is.EqualTo(("partners-1", LongAgo)));
    }

    [Test]
    public async Task Import_ConnectorReturnsTheWatermarkItWasHanded_RewritesWhenItWasRecordedAsync()
    {
        // Decision 6: when a watermark was recorded reads as when an import last read the Partition, so a run that found
        // nothing new still moves it. Unlike the Connected System-level value, an unchanged value is not skipped.
        await SeedWatermarkAsync(_corp, "corp-1");
        var connector = new PartitionWatermarkTestConnector([[NewObject()]],
            partitionWatermarksByPage: [new() { [_corp.Id] = "corp-1" }]);

        await (await PrepareAsync(connector, partition: _corp)).Processor.PerformImportAsync();

        var recorded = (await RecordedWatermarksAsync())[_corp.Id];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(recorded.Value, Is.EqualTo("corp-1"));
            Assert.That(recorded.RecordedAt, Is.GreaterThan(LongAgo));
        }
    }

    [Test]
    public async Task Import_CompletesNormally_SweepsTheWatermarksOfDeselectedPartitionsAsync()
    {
        // A Partition deselected by a path that did not remove its watermark: the run's end-of-run write removes it, so a
        // later reselection reads the Partition in full rather than resuming from before it was out of scope.
        var branch = await CreatePartitionAsync("dc=branch,dc=local", "branch.local", selected: false);
        await SeedWatermarkAsync(_corp, "corp-1");
        await SeedWatermarkAsync(branch, "branch-1");
        var connector = new PartitionWatermarkTestConnector([[NewObject()]]);

        await (await PrepareAsync(connector)).Processor.PerformImportAsync();

        var recorded = await RecordedWatermarksAsync();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(connector.WatermarksSet.Single().Keys, Is.EquivalentTo(new[] { _corp.Id, _partners.Id }),
                "precondition: a deselected Partition is not one the run reads");
            Assert.That(recorded.Keys, Is.EqualTo(new[] { _corp.Id }));
        }
    }

    [Test]
    public async Task Import_CompletesNormally_RecordsThePersistedConnectorDataWithThePartitionWatermarksAsync()
    {
        // A Connector that keeps a watermark per Partition still keeps Connected System-level data (the LDAP Connector's
        // directory capabilities and pinned server), recorded in the same write.
        await SetPersistedConnectorDataAsync("connector-data-1");
        var connector = new PartitionWatermarkTestConnector([[NewObject()]],
            partitionWatermarksByPage: [new() { [_corp.Id] = "corp-1" }], firstPageWatermark: "connector-data-2");

        await (await PrepareAsync(connector)).Processor.PerformImportAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(await StoredPersistedConnectorDataAsync(), Is.EqualTo("connector-data-2"));
            Assert.That(_system.PersistedConnectorData, Is.EqualTo("connector-data-2"));
            Assert.That((await RecordedWatermarksAsync())[_corp.Id].Value, Is.EqualTo("corp-1"));
        }
    }

    [Test]
    public async Task Import_RecordingTheWatermarksFails_LeavesThePersistedConnectorDataAsItWasAsync()
    {
        // The persisted connector data and the Partition watermarks are recorded by one write, so one cannot land
        // without the other.
        await SetPersistedConnectorDataAsync("connector-data-1");
        var connector = new PartitionWatermarkTestConnector([[NewObject()]],
            partitionWatermarksByPage: [new() { [_corp.Id] = "corp-1" }], firstPageWatermark: "connector-data-2");
        var failure = new InvalidOperationException("simulated watermark write failure");
        var syncServer = FaultingSyncServerProxy.Create(new SyncServer(Jim), nameof(ISyncServer.RecordImportWatermarksAsync), failure);

        var (processor, _) = await PrepareAsync(connector, syncServer: syncServer);
        var thrown = Assert.ThrowsAsync<InvalidOperationException>(processor.PerformImportAsync);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(thrown, Is.SameAs(failure), "precondition: the run failed while recording its watermarks");
            Assert.That(await StoredPersistedConnectorDataAsync(), Is.EqualTo("connector-data-1"));
            Assert.That(await RecordedWatermarksAsync(), Is.Empty);
        }
    }

    [Test]
    public async Task Import_ALaterPageReturnsOtherWatermarks_RecordsTheFirstPagesAsync()
    {
        // As with the persisted connector data, the first page that carries values is the one taken.
        var connector = new PartitionWatermarkTestConnector([[NewObject()], [NewObject()]],
            partitionWatermarksByPage: [new() { [_corp.Id] = "corp-1" }, new() { [_corp.Id] = "corp-2" }]);

        await (await PrepareAsync(connector)).Processor.PerformImportAsync();

        Assert.That((await RecordedWatermarksAsync())[_corp.Id].Value, Is.EqualTo("corp-1"));
    }

    #endregion

    #region Faults in the Connector

    [Test]
    public async Task DeltaImport_ConnectorReturnsAWatermarkForAPartitionTheRunDidNotTarget_FailsTheRunAndRecordsNothingAsync()
    {
        // Recording it would move Partners' starting point past changes no import of Partners has read: #2715 itself.
        await SetPersistedConnectorDataAsync("connector-data-1");
        await SeedWatermarkAsync(_corp, "corp-1");
        await SeedWatermarkAsync(_partners, "partners-1");
        var connector = new PartitionWatermarkTestConnector([[NewObject()]],
            partitionWatermarksByPage: [new() { [_corp.Id] = "corp-2", [_partners.Id] = "partners-2" }], firstPageWatermark: "connector-data-2");

        var (processor, activity) = await PrepareAsync(connector, partition: _corp);
        var failure = Assert.ThrowsAsync<InvalidDataException>(processor.PerformImportAsync);

        var recorded = await RecordedWatermarksAsync();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(failure!.Message, Does.Contain("partners.local").And.Contain("fault in the Connector"));
            Assert.That(await SyncRepo.GetConnectedSystemObjectCountAsync(_system.Id), Is.Zero, "nothing read was staged");
            Assert.That(recorded[_corp.Id], Is.EqualTo(("corp-1", LongAgo)));
            Assert.That(recorded[_partners.Id], Is.EqualTo(("partners-1", LongAgo)));
            Assert.That(await StoredPersistedConnectorDataAsync(), Is.EqualTo("connector-data-1"));
            Assert.That(activity.ImportPartitionWatermarksTo, Is.Null);
        }
    }

    [Test]
    public async Task Import_ALaterPageReturnsAWatermarkForAPartitionTheRunDidNotTarget_FailsTheRunAsync()
    {
        var connector = new PartitionWatermarkTestConnector([[NewObject()], [NewObject()]],
            partitionWatermarksByPage: [new() { [_corp.Id] = "corp-1" }, new() { [_partners.Id] = "partners-1" }]);

        var (processor, _) = await PrepareAsync(connector, partition: _corp);
        var failure = Assert.ThrowsAsync<InvalidDataException>(processor.PerformImportAsync);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(failure!.Message, Does.Contain("page 2").And.Contain("partners.local"));
            Assert.That(await SyncRepo.GetConnectedSystemObjectCountAsync(_system.Id), Is.Zero);
            Assert.That(await RecordedWatermarksAsync(), Is.Empty);
        }
    }

    [TestCase(ConnectedSystemRunType.DeltaImport)]
    [TestCase(ConnectedSystemRunType.FullImport)]
    public async Task Import_ConnectorWithoutTheCapabilityReturnsPartitionWatermarks_FailsTheRunAsync(ConnectedSystemRunType runType)
    {
        // A Connector cannot return what JIM never handed it.
        var connector = new PagedImportConnector([[NewObject()]], partitionWatermarksByPage: [new() { [_corp.Id] = "corp-1" }]);

        var (processor, _) = await PrepareAsync(connector, runType);
        var failure = Assert.ThrowsAsync<InvalidDataException>(processor.PerformImportAsync);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(failure!.Message, Does.Contain("fault in the Connector"));
            Assert.That(await SyncRepo.GetConnectedSystemObjectCountAsync(_system.Id), Is.Zero, "nothing read was staged");
            Assert.That(await RecordedWatermarksAsync(), Is.Empty);
        }
    }

    #endregion

    #region Runs that record nothing

    [TestCase(ConnectedSystemRunType.DeltaImport)]
    [TestCase(ConnectedSystemRunType.FullImport)]
    public async Task Import_StagingFailsAfterEveryPageIsRead_LeavesEveryPartitionWatermarkUnchangedAsync(ConnectedSystemRunType runType)
    {
        // #1868, per Partition: the values stand for every change the pages covered, and nothing was staged.
        await SeedWatermarkAsync(_corp, "corp-1");
        await SeedWatermarkAsync(_partners, "partners-1");
        var connector = new PartitionWatermarkTestConnector([[NewObject()], [NewObject()]],
            partitionWatermarksByPage: [new() { [_corp.Id] = "corp-2", [_partners.Id] = null }]);
        var stagingFailure = new InvalidOperationException("simulated staging failure");
        var syncServer = FaultingSyncServerProxy.Create(new SyncServer(Jim), nameof(ISyncServer.CreateConnectedSystemObjectsAsync), stagingFailure);

        var (processor, _) = await PrepareAsync(connector, runType, syncServer: syncServer);
        var thrown = Assert.ThrowsAsync<InvalidOperationException>(processor.PerformImportAsync);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(thrown, Is.SameAs(stagingFailure), "precondition: the run failed while staging");
            Assert.That(connector.WatermarksHeldByPage, Has.Count.EqualTo(2), "precondition: both pages were read before staging failed");
            Assert.That(await RecordedWatermarksAsync(), Is.EqualTo(Unchanged()));
        }
    }

    [Test]
    public async Task Import_Cancelled_LeavesEveryPartitionWatermarkUnchangedAsync()
    {
        await SeedWatermarkAsync(_corp, "corp-1");
        await SeedWatermarkAsync(_partners, "partners-1");
        using var cancellation = new CancellationTokenSource();
        var connector = new PartitionWatermarkTestConnector([[NewObject()], []],
            partitionWatermarksByPage: [new() { [_corp.Id] = "corp-2", [_partners.Id] = null }],
            beforePage: page =>
            {
                if (page == 1)
                    cancellation.Cancel();
            });

        var (processor, activity) = await PrepareAsync(connector, cancellation: cancellation);
        await processor.PerformImportAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(await SyncRepo.GetConnectedSystemObjectCountAsync(_system.Id), Is.Zero, "precondition: the cancelled run staged nothing");
            Assert.That(await RecordedWatermarksAsync(), Is.EqualTo(Unchanged()));
            Assert.That(activity.ImportPartitionWatermarksTo, Is.Null);
        }
    }

    [Test]
    public async Task DeltaImport_ReportedDeletesWithheld_LeavesEveryPartitionWatermarkUnchangedAsync()
    {
        // #2089: withheld deletes must be read again, so every Partition keeps the watermark the run started from.
        await SeedWatermarkAsync(_corp, "corp-1");
        await SeedWatermarkAsync(_partners, "partners-1");
        var held = SeedHeld(3);
        var connector = new PartitionWatermarkTestConnector([held.Select(Delete).ToList()],
            partitionWatermarksByPage: [new() { [_corp.Id] = "corp-2", [_partners.Id] = null }]);

        var (processor, activity) = await PrepareAsync(connector, maxDetectedDeletions: 2);
        await processor.PerformImportAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(activity.DetectedDeletionsWithheld, Is.EqualTo(3), "precondition: every delete was withheld");
            Assert.That(await RecordedWatermarksAsync(), Is.EqualTo(Unchanged()));
            Assert.That(activity.ImportPartitionWatermarksTo, Is.Null);
        }
    }

    [Test]
    public async Task DeltaImport_ReadEveryObjectButDeletionDetectionIncomplete_LeavesEveryPartitionWatermarkUnchangedAsync()
    {
        // #2720: a Delta Import that read every object finds deletions by absence; refused by the deletion limit, that
        // check did not run to completion, so the next Delta Import must read everything and check again.
        await SeedWatermarkAsync(_corp, "corp-1");
        await SeedWatermarkAsync(_partners, "partners-1");
        var held = SeedHeld(4);
        var connector = new PartitionWatermarkTestConnector([[Present(held[0]), Present(held[1])]],
            partitionWatermarksByPage: [new() { [_corp.Id] = "corp-2", [_partners.Id] = "partners-2" }], completeReadByPage: [true]);

        var (processor, activity) = await PrepareAsync(connector, maxDetectedDeletions: 1);
        await processor.PerformImportAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(activity.DetectedDeletionsWithheld, Is.EqualTo(2), "precondition: deletion detection was refused by the limit");
            Assert.That(await RecordedWatermarksAsync(), Is.EqualTo(Unchanged()));
            Assert.That(activity.ImportPartitionWatermarksTo, Is.Null);
        }
    }

    [Test]
    public async Task DeltaImport_CloseImportConnectionReturnsData_RecordsNoPartitionWatermarkAsync()
    {
        // What the Connector returns at close overrides what its pages reported (#230, #1868); the pages' Partition
        // watermarks were taken under the state it has just invalidated, so none is recorded.
        await SeedWatermarkAsync(_corp, "corp-1");
        await SeedWatermarkAsync(_partners, "partners-1");
        var connector = new PartitionWatermarkTestConnector([[NewObject()]],
            partitionWatermarksByPage: [new() { [_corp.Id] = "corp-2", [_partners.Id] = null }], closeReturn: "state-returned-at-close");

        var (processor, activity) = await PrepareAsync(connector);
        await processor.PerformImportAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(await SyncRepo.GetConnectedSystemObjectCountAsync(_system.Id), Is.EqualTo(1), "precondition: the run completed");
            Assert.That(await StoredPersistedConnectorDataAsync(), Is.EqualTo("state-returned-at-close"));
            Assert.That(await RecordedWatermarksAsync(), Is.EqualTo(Unchanged()));
            Assert.That(activity.ImportPartitionWatermarksTo, Is.Null);
        }
    }

    #endregion

    #region The Activity

    [Test]
    public async Task Import_CompletesNormally_RecordsEachPartitionsWatermarksOnTheActivityAsync()
    {
        // From: every Partition the run read, with what it was handed (null where it had none). To: what the run
        // recorded for every Partition the Connector named, a null being a watermark it cleared. Branch was left out by
        // the Connector, so it kept what it had and is not among what the run recorded.
        var branch = await CreatePartitionAsync("dc=branch,dc=local", "branch.local");
        await SeedWatermarkAsync(_corp, "corp-1");
        await SeedWatermarkAsync(branch, "branch-1");
        var connector = new PartitionWatermarkTestConnector([[NewObject()]],
            partitionWatermarksByPage: [new() { [_corp.Id] = null, [_partners.Id] = "partners-1" }]);

        var (processor, activity) = await PrepareAsync(connector);
        await processor.PerformImportAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Describe(activity.ImportPartitionWatermarksFrom), Is.EquivalentTo(new[]
            {
                (_corp.Id, "corp.local", (string?)"corp-1"),
                (_partners.Id, "partners.local", null),
                (branch.Id, "branch.local", "branch-1")
            }));
            Assert.That(Describe(activity.ImportPartitionWatermarksTo), Is.EquivalentTo(new[]
            {
                (_corp.Id, "corp.local", (string?)null),
                (_partners.Id, "partners.local", "partners-1")
            }));
        }
    }

    [Test]
    public async Task Import_StagingFails_RecordsWhereEachPartitionStartedButNoWatermarkReachedAsync()
    {
        await SeedWatermarkAsync(_corp, "corp-1");
        var connector = new PartitionWatermarkTestConnector([[NewObject()]],
            partitionWatermarksByPage: [new() { [_corp.Id] = "corp-2", [_partners.Id] = "partners-1" }]);
        var syncServer = FaultingSyncServerProxy.Create(
            new SyncServer(Jim), nameof(ISyncServer.CreateConnectedSystemObjectsAsync), new InvalidOperationException("simulated staging failure"));

        var (processor, activity) = await PrepareAsync(connector, syncServer: syncServer);
        Assert.ThrowsAsync<InvalidOperationException>(processor.PerformImportAsync);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Describe(activity.ImportPartitionWatermarksFrom), Is.EquivalentTo(new[]
            {
                (_corp.Id, "corp.local", (string?)"corp-1"),
                (_partners.Id, "partners.local", null)
            }));
            Assert.That(activity.ImportPartitionWatermarksTo, Is.Null, "a run that recorded no watermark must not claim one");
        }
    }

    [Test]
    public async Task Import_ConnectorWithoutTheCapability_RecordsNoPartitionWatermarksOnTheActivityAsync()
    {
        var (processor, activity) = await PrepareAsync(new PagedImportConnector([[NewObject()]], firstPageWatermark: "connector-data-2"));
        await processor.PerformImportAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(activity.ImportWatermarkTo, Is.EqualTo("connector-data-2"), "precondition: the run recorded its Connected System-level watermark");
            Assert.That(activity.ImportPartitionWatermarksFrom, Is.Null);
            Assert.That(activity.ImportPartitionWatermarksTo, Is.Null);
        }
    }

    #endregion

    #region Helpers

    private async Task<ConnectedSystemPartition> CreatePartitionAsync(string distinguishedName, string name, bool selected = true)
    {
        var partition = new ConnectedSystemPartition { ConnectedSystem = _system, ExternalId = distinguishedName, Name = name, Selected = selected };
        DbContext.ConnectedSystemPartitions.Add(partition);
        await DbContext.SaveChangesAsync();
        return partition;
    }

    /// <summary>
    /// Records a watermark for a Partition as an earlier import would have, then lets go of it, so the run under test
    /// reads it from the store as the Worker does.
    /// </summary>
    private async Task SeedWatermarkAsync(ConnectedSystemPartition partition, string value)
    {
        var row = new ConnectedSystemPartitionWatermark { PartitionId = partition.Id, ConnectedSystemId = _system.Id, Value = value, RecordedAt = LongAgo };
        DbContext.ConnectedSystemPartitionWatermarks.Add(row);
        await DbContext.SaveChangesAsync();
        DbContext.Entry(row).State = EntityState.Detached;
    }

    /// <summary>What JIM holds for each of the Connected System's Partitions, read from the store.</summary>
    private async Task<Dictionary<int, (string Value, DateTime RecordedAt)>> RecordedWatermarksAsync() =>
        (await Jim.ConnectedSystems.GetConnectedSystemPartitionWatermarksAsync(_system.Id))
            .ToDictionary(w => w.PartitionId, w => (w.Value, w.RecordedAt));

    /// <summary>The seeded watermarks, as they were before the run.</summary>
    private Dictionary<int, (string Value, DateTime RecordedAt)> Unchanged() =>
        new() { [_corp.Id] = ("corp-1", LongAgo), [_partners.Id] = ("partners-1", LongAgo) };

    /// <summary>The Connected System's persisted connector data as an earlier import would have recorded it.</summary>
    private async Task SetPersistedConnectorDataAsync(string value)
    {
        _system.PersistedConnectorData = value;
        await DbContext.SaveChangesAsync();
    }

    private async Task<string?> StoredPersistedConnectorDataAsync() =>
        await DbContext.ConnectedSystems.AsNoTracking().Where(cs => cs.Id == _system.Id).Select(cs => cs.PersistedConnectorData).SingleAsync();

    private async Task<(SyncImportTaskProcessor Processor, Activity Activity)> PrepareAsync(IConnector connector,
        ConnectedSystemRunType runType = ConnectedSystemRunType.DeltaImport, ConnectedSystemPartition? partition = null, ISyncServer? syncServer = null,
        CancellationTokenSource? cancellation = null, int? maxDetectedDeletions = null, ConnectedSystem? system = null)
    {
        system ??= _system;
        var runProfile = await CreateRunProfileAsync(system.Id, runType == ConnectedSystemRunType.FullImport ? "Full Import" : "Delta Import", runType);
        runProfile.Partition = partition;
        runProfile.MaxDetectedDeletions = maxDetectedDeletions;
        var activity = await CreateActivityAsync(system.Id, runProfile, runType);
        var workerTask = new SynchronisationWorkerTask(system.Id, runProfile.Id)
        {
            Id = Guid.NewGuid(),
            Status = WorkerTaskStatus.Processing,
            Activity = activity
        };

        var processor = new SyncImportTaskProcessor(
            Jim, SyncRepo, syncServer ?? new SyncServer(Jim), new SyncEngine(),
            connector, system, runProfile, workerTask, cancellation ?? new CancellationTokenSource());
        return (processor, activity);
    }

    private static List<(int PartitionId, string PartitionName, string? Value)>? Describe(List<ImportPartitionWatermark>? watermarks) =>
        watermarks?.Select(w => (w.PartitionId, w.PartitionName, w.Value)).ToList();

    private ConnectedSystemImportObject NewObject() => NewObject(_type);

    private static ConnectedSystemImportObject NewObject(ConnectedSystemObjectType type) => ImportObject(type, Guid.NewGuid(), "New user", ObjectChangeType.Created);

    private ConnectedSystemImportObject Present(ConnectedSystemObject cso) => ImportObject(_type, ExternalIdOf(cso), "Present", ObjectChangeType.NotSet);

    private ConnectedSystemImportObject Delete(ConnectedSystemObject cso) => ImportObject(_type, ExternalIdOf(cso), null, ObjectChangeType.Deleted);

    private static ConnectedSystemImportObject ImportObject(ConnectedSystemObjectType type, Guid externalId, string? displayName, ObjectChangeType changeType)
    {
        var importObject = new ConnectedSystemImportObject
        {
            ObjectType = type.Name,
            ChangeType = changeType,
            Attributes =
            [
                new ConnectedSystemImportObjectAttribute
                {
                    Name = type.Attributes.Single(a => a.IsExternalId).Name,
                    Type = AttributeDataType.Guid,
                    GuidValues = [externalId]
                }
            ]
        };
        if (displayName != null)
            importObject.Attributes.Add(new ConnectedSystemImportObjectAttribute { Name = "DisplayName", Type = AttributeDataType.Text, StringValues = [displayName] });
        return importObject;
    }

    private static Guid ExternalIdOf(ConnectedSystemObject cso) =>
        cso.AttributeValues.Single(v => v.GuidValue.HasValue).GuidValue!.Value;

    /// <summary>Connected System Objects JIM already holds, as an earlier import would have left them.</summary>
    private List<ConnectedSystemObject> SeedHeld(int count) =>
        Enumerable.Range(1, count).Select(i => SeedCso($"User {i}")).ToList();

    private ConnectedSystemObject SeedCso(string displayName)
    {
        var cso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = _system.Id,
            ConnectedSystem = _system,
            TypeId = _type.Id,
            Type = _type,
            ExternalIdAttributeId = _type.Attributes.Single(a => a.IsExternalId).Id,
            Status = ConnectedSystemObjectStatus.Normal,
            Created = DateTime.UtcNow
        };
        foreach (var (attribute, guidValue, stringValue) in new[]
                 {
                     (_type.Attributes.Single(a => a.IsExternalId), (Guid?)Guid.NewGuid(), (string?)null),
                     (_type.Attributes.Single(a => a.Name == "DisplayName"), null, displayName)
                 })
        {
            cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
            {
                Id = Guid.NewGuid(),
                AttributeId = attribute.Id,
                Attribute = attribute,
                GuidValue = guidValue,
                StringValue = stringValue,
                ConnectedSystemObject = cso
            });
        }

        SyncRepo.SeedConnectedSystemObject(cso);
        return cso;
    }

    #endregion
}
