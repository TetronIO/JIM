// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Servers;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Interfaces;
using JIM.Models.Staging;
using JIM.Models.Tasking;
using JIM.Worker.Processors;
using NUnit.Framework;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace JIM.Worker.Tests.Workflows;

/// <summary>
/// #2723: each Connected System Object's Partition is where the object is, not the Partition of the Run Profile that
/// first read it. Every Partition-scoped reader (deletion detection, the deletion limits' base, the Object Type change
/// retirement of #2049) filters on the object's Partition, so an object an unscoped Run Profile created, recorded with no
/// Partition, was invisible to all of them: a Partition-scoped Full Import never noticed it had been deleted. The import
/// now asks a Connector that can say (<see cref="IConnectorPartitionPlacement"/>) where each object it reads is, other
/// than a delete, and records that, keeping what it holds when the Connector has no answer.
/// </summary>
[TestFixture]
public class ImportPartitionPlacementTests : WorkflowTestBase
{
    private ConnectedSystem _system = null!;
    private ConnectedSystemObjectType _user = null!;
    private ConnectedSystemPartition _corp = null!;
    private ConnectedSystemPartition _partners = null!;

    [SetUp]
    public async Task SetUpAsync()
    {
        _system = await CreateConnectedSystemAsync("Directory");
        _user = await CreateDirectoryTypeAsync("user");
        _corp = await CreatePartitionAsync("dc=corp,dc=local", "corp.local");
        _partners = await CreatePartitionAsync("dc=partners,dc=local", "partners.local");
    }

    #region Where an object is recorded

    [Test]
    public async Task FullImport_Unscoped_RecordsEachObjectsPartitionAsync()
    {
        var pat = Guid.NewGuid();
        var sam = Guid.NewGuid();

        await RunAsync(Placing([User(pat, "uid=pat,ou=people,dc=corp,dc=local"), User(sam, "uid=sam,ou=people,dc=partners,dc=local")]),
            ConnectedSystemRunType.FullImport);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Held(pat).PartitionId, Is.EqualTo(_corp.Id));
            Assert.That(Held(sam).PartitionId, Is.EqualTo(_partners.Id));
        }
    }

    [Test]
    public async Task FullImport_ScopedRunCreatesAnObjectInAnotherPartition_RecordsWhereTheObjectIsAsync()
    {
        // An OpenLDAP superior database's search returns a subordinate suffix's entries too (glue): where the object is
        // decides, not which Run Profile happened to read it.
        var sam = Guid.NewGuid();

        await RunAsync(Placing([User(sam, "uid=sam,ou=people,dc=partners,dc=local")]), ConnectedSystemRunType.FullImport, _corp);

        Assert.That(Held(sam).PartitionId, Is.EqualTo(_partners.Id));
    }

    [TestCase(ConnectedSystemRunType.FullImport, ObjectChangeType.NotSet)]
    [TestCase(ConnectedSystemRunType.DeltaImport, ObjectChangeType.Updated)]
    public async Task Import_ObjectWhosePlacementMoved_IsRestampedWithNoExecutionItemAsync(ConnectedSystemRunType runType, ObjectChangeType changeType)
    {
        // Recorded against corp.local (by an earlier scoped run, before placement), though its DN is in partners.local:
        // nothing about the object has changed, so the Partition alone is corrected, and nothing is reported for it.
        const string dn = "uid=sam,ou=people,dc=partners,dc=local";
        var sam = SeedCso(Guid.NewGuid(), dn, "Sam", _corp);

        var activity = await RunAsync(Placing([User(sam.ExternalId, dn, "Sam", changeType)]), runType);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(sam.Cso.PartitionId, Is.EqualTo(_partners.Id));
            Assert.That(activity.RunProfileExecutionItems.Where(item => item.ConnectedSystemObjectId == sam.Cso.Id), Is.Empty,
                "a Partition-only change is not a change an administrator acts on");
            Assert.That(activity.Message, Does.Contain("1 unchanged"));
        }
    }

    [Test]
    public async Task FullImport_ObjectInNoPartition_KeepsItsStoredPartitionAsync()
    {
        // The Connected System's Partitions may be out of date (a naming context added since the last hierarchy refresh):
        // no answer is not evidence the object moved.
        const string dn = "uid=ann,ou=people,dc=elsewhere,dc=local";
        var ann = SeedCso(Guid.NewGuid(), dn, "Ann", _corp);

        await RunAsync(Placing([User(ann.ExternalId, dn, "Ann")]), ConnectedSystemRunType.FullImport);

        Assert.That(ann.Cso.PartitionId, Is.EqualTo(_corp.Id));
    }

    [Test]
    public async Task FullImport_NewObjectInNoPartition_TakesTheRunProfilesPartitionAsync()
    {
        var ann = Guid.NewGuid();

        await RunAsync(Placing([User(ann, "uid=ann,ou=people,dc=elsewhere,dc=local")]), ConnectedSystemRunType.FullImport, _corp);

        Assert.That(Held(ann).PartitionId, Is.EqualTo(_corp.Id), "as before placement: the Partition the Run Profile read it from");
    }

    [Test]
    public async Task FullImport_ConnectorWithoutPlacement_RecordsTheRunProfilesPartitionAsync()
    {
        // Guard, not a red test: a Connector that cannot say where its objects are keeps the rule from before placement.
        var scopedCreate = Guid.NewGuid();
        var unscopedCreate = Guid.NewGuid();

        await RunAsync(new PagedImportConnector([[User(scopedCreate, "uid=sam,ou=people,dc=partners,dc=local")]]), ConnectedSystemRunType.FullImport, _corp);
        await RunAsync(new PagedImportConnector([[User(scopedCreate, "uid=sam,ou=people,dc=partners,dc=local"), User(unscopedCreate, "uid=pat,ou=people,dc=corp,dc=local")]]),
            ConnectedSystemRunType.FullImport);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Held(scopedCreate).PartitionId, Is.EqualTo(_corp.Id));
            Assert.That(Held(unscopedCreate).PartitionId, Is.Null);
        }
    }

    [Test]
    public async Task DeltaImport_ReportedDelete_LeavesThePartitionAsync()
    {
        // A delete is never placed: the object is gone, and what it was last recorded against is what deletion counts use.
        const string dn = "uid=sam,ou=people,dc=partners,dc=local";
        var sam = SeedCso(Guid.NewGuid(), dn, "Sam", _corp);
        var connector = Placing([User(sam.ExternalId, dn, changeType: ObjectChangeType.Deleted)]);

        await RunAsync(connector, ConnectedSystemRunType.DeltaImport);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(sam.Cso.Status, Is.EqualTo(ConnectedSystemObjectStatus.Obsolete));
            Assert.That(sam.Cso.PartitionId, Is.EqualTo(_corp.Id));
            Assert.That(connector.IdentifiersPlaced, Is.Empty, "the Connector is not asked where a deleted object is");
        }
    }

    [Test]
    public async Task FullImport_ConfirmsAProvisionedObject_RecordsItsPartitionAsync()
    {
        // A Pending Provisioning object is created with no Partition; the import that finds it in the directory places it.
        const string dn = "uid=new,ou=people,dc=corp,dc=local";
        var provisioned = SeedPendingProvisioning(dn);
        var objectGuid = Guid.NewGuid();

        await RunAsync(Placing([User(objectGuid, dn, "New")]), ConnectedSystemRunType.FullImport);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(provisioned.Status, Is.EqualTo(ConnectedSystemObjectStatus.Normal), "precondition: the import confirmed the object");
            Assert.That(provisioned.PartitionId, Is.EqualTo(_corp.Id));
        }
    }

    [Test]
    public async Task Import_PlacesEachObjectOnceAndPreparesThePlacementOnceAsync()
    {
        var connector = Placing([User(Guid.NewGuid(), "uid=pat,dc=corp,dc=local"), User(Guid.NewGuid(), "uid=cal,dc=corp,dc=local")],
            [User(Guid.NewGuid(), "uid=sam,dc=partners,dc=local")]);

        await RunAsync(connector, ConnectedSystemRunType.FullImport);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(connector.IdentifiersPlaced, Is.EqualTo(new[] { "uid=pat,dc=corp,dc=local", "uid=cal,dc=corp,dc=local", "uid=sam,dc=partners,dc=local" }),
                "by the Secondary External ID, once per object across every page");
            Assert.That(connector.PreparedCount, Is.EqualTo(1), "every Partition is selected, so one placer serves the whole run");
        }
    }

    [Test]
    public async Task Import_SummarisesWhereItPlacedWhatItReadInOneLogEventAsync()
    {
        // One new object placed, one existing object moved, one in no Partition: one summary for the run, a warning
        // because an object could not be placed.
        const string movedDn = "uid=sam,ou=people,dc=partners,dc=local";
        var moved = SeedCso(Guid.NewGuid(), movedDn, "Sam", _corp);
        var connector = Placing([User(Guid.NewGuid(), "uid=pat,ou=people,dc=corp,dc=local"), User(moved.ExternalId, movedDn, "Sam"),
            User(Guid.NewGuid(), "uid=ann,ou=people,dc=elsewhere,dc=local")]);

        var summaries = await CaptureLogAsync(() => RunAsync(connector, ConnectedSystemRunType.FullImport),
            logEvent => logEvent.Properties.ContainsKey("Placed") && logEvent.Properties.ContainsKey("InNoPartition") && logEvent.Properties.ContainsKey("Restamped"));

        Assert.That(summaries, Has.Count.EqualTo(1), "one summary per run, whatever the number of objects");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(summaries[0].Level, Is.EqualTo(LogEventLevel.Warning));
            Assert.That(summaries[0].Properties["Placed"].ToString(), Is.EqualTo("2"));
            Assert.That(summaries[0].Properties["InNoPartition"].ToString(), Is.EqualTo("1"));
            Assert.That(summaries[0].Properties["Restamped"].ToString(), Is.EqualTo("1"));
        }
    }

    [Test]
    public async Task Import_EveryObjectPlaced_SummarisesAtInformationAsync()
    {
        var connector = Placing([User(Guid.NewGuid(), "uid=pat,ou=people,dc=corp,dc=local")]);

        var summaries = await CaptureLogAsync(() => RunAsync(connector, ConnectedSystemRunType.FullImport),
            logEvent => logEvent.Properties.ContainsKey("Placed") && logEvent.Properties.ContainsKey("InNoPartition") && logEvent.Properties.ContainsKey("Restamped"));

        Assert.That(summaries.Single().Level, Is.EqualTo(LogEventLevel.Information));
    }

    [Test]
    public async Task Import_ConnectorWithoutPlacement_LogsNoPlacementSummaryAsync()
    {
        var summaries = await CaptureLogAsync(
            () => RunAsync(new PagedImportConnector([[User(Guid.NewGuid(), "uid=pat,ou=people,dc=corp,dc=local")]]), ConnectedSystemRunType.FullImport),
            logEvent => logEvent.Properties.ContainsKey("Placed") && logEvent.Properties.ContainsKey("InNoPartition") && logEvent.Properties.ContainsKey("Restamped"));

        Assert.That(summaries, Is.Empty);
    }

    #endregion

    #region What Partition-scoped runs now see

    [Test]
    public async Task FullImport_ScopedAfterAnUnscopedImportCreatedTheObject_MarksTheObjectDeletedInTheDirectoryAsDeletedAsync()
    {
        // #2723 itself: created by an unscoped Full Import, deleted in the directory, and only Partition-scoped Run
        // Profiles run from then on. The scoped Full Import must notice.
        var pat = Guid.NewGuid();
        var cal = Guid.NewGuid();
        var sam = Guid.NewGuid();
        await RunAsync(Placing([User(pat, "uid=pat,ou=people,dc=corp,dc=local"), User(cal, "uid=cal,ou=people,dc=corp,dc=local"),
            User(sam, "uid=sam,ou=people,dc=partners,dc=local")]), ConnectedSystemRunType.FullImport);

        await RunAsync(Placing([User(cal, "uid=cal,ou=people,dc=corp,dc=local")]), ConnectedSystemRunType.FullImport, _corp);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Held(pat).Status, Is.EqualTo(ConnectedSystemObjectStatus.Obsolete), "deleted from corp.local, so a Full Import of corp.local marks it deleted");
            Assert.That(Held(cal).Status, Is.EqualTo(ConnectedSystemObjectStatus.Normal));
            Assert.That(Held(sam).Status, Is.EqualTo(ConnectedSystemObjectStatus.Normal), "partners.local was not read");
        }
    }

    [Test]
    public async Task FullImport_Scoped_DoesNotMarkAnotherPartitionsObjectDeletedAsync()
    {
        // Guard, not a red test: placing objects must not widen what a scoped run judges.
        var pat = Guid.NewGuid();
        var sam = Guid.NewGuid();
        await RunAsync(Placing([User(pat, "uid=pat,ou=people,dc=corp,dc=local"), User(sam, "uid=sam,ou=people,dc=partners,dc=local")]),
            ConnectedSystemRunType.FullImport);

        await RunAsync(Placing([User(pat, "uid=pat,ou=people,dc=corp,dc=local")]), ConnectedSystemRunType.FullImport, _corp);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Held(sam).Status, Is.EqualTo(ConnectedSystemObjectStatus.Normal));
            Assert.That(Held(sam).PartitionId, Is.EqualTo(_partners.Id));
        }
    }

    [Test]
    public async Task FullImport_ScopedDeletionLimit_MeasuresAgainstObjectsAnUnscopedImportCreatedAsync()
    {
        // The percentage limit's base is the run's Partition's objects. Counting only those a scoped run created made the
        // limit stricter than configured: here one deletion in four would have read as one in one.
        var cal = SeedCso(Guid.NewGuid(), "uid=cal,ou=people,dc=corp,dc=local", "Cal", _corp);
        var others = new[] { ("pat", Guid.NewGuid()), ("ann", Guid.NewGuid()), ("bob", Guid.NewGuid()) };
        var stillThere = others.Select(o => User(o.Item2, $"uid={o.Item1},ou=people,dc=corp,dc=local", o.Item1)).ToList();
        await RunAsync(Placing([User(cal.ExternalId, "uid=cal,ou=people,dc=corp,dc=local", "Cal"), .. stillThere]), ConnectedSystemRunType.FullImport);

        var activity = await RunAsync(Placing([.. others.Select(o => User(o.Item2, $"uid={o.Item1},ou=people,dc=corp,dc=local", o.Item1))]),
            ConnectedSystemRunType.FullImport, _corp, runProfile => runProfile.MaxDetectedDeletionsPercent = 50);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cal.Cso.Status, Is.EqualTo(ConnectedSystemObjectStatus.Obsolete), "one of four is within a 50% limit");
            Assert.That(activity.DetectedDeletionsWithheld, Is.Zero);
        }
    }

    [Test]
    public async Task FullImport_ScopedObjectTypeChange_RetiresAHeldObjectAnUnscopedImportCreatedAsync()
    {
        // #2049's retirement is deletion detection's, scoped to the run's Partition: a held object recorded with no
        // Partition was out of its reach, so the change was withdrawn and the object could never change type.
        _system.ConnectorDefinition.ExternalIdsUniqueAcrossObjectTypes = true;
        var computer = await CreateDirectoryTypeAsync("computer");
        const string dn = "cn=ws01,ou=computers,dc=corp,dc=local";
        var objectGuid = Guid.NewGuid();
        await RunAsync(Placing([User(objectGuid, dn, "WS01")]), ConnectedSystemRunType.FullImport);
        var held = Held(objectGuid);

        await RunAsync(Placing([DirectoryObject(computer, objectGuid, dn, "WS01")]), ConnectedSystemRunType.FullImport, _corp,
            runProfile => runProfile.AllowObjectTypeChanges = true);

        var replacement = SyncRepo.ConnectedSystemObjects.Values.SingleOrDefault(cso => cso.TypeId == computer.Id);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(held.Status, Is.EqualTo(ConnectedSystemObjectStatus.Obsolete), "the held object is retired");
            Assert.That(replacement, Is.Not.Null, "and its replacement created");
            Assert.That(replacement?.PartitionId, Is.EqualTo(_corp.Id));
        }
    }

    #endregion

    #region Helpers

    private sealed record SeededObject(ConnectedSystemObject Cso, Guid ExternalId);

    /// <summary>Runs <paramref name="action"/> with every log event captured, and returns those that match.</summary>
    private static async Task<List<LogEvent>> CaptureLogAsync(Func<Task> action, Func<LogEvent, bool> match)
    {
        var sink = new CapturingSink();
        var original = Log.Logger;
        Log.Logger = new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(sink).CreateLogger();
        try
        {
            await action();
        }
        finally
        {
            Log.Logger = original;
        }

        return sink.Events.Where(match).ToList();
    }

    private sealed class CapturingSink : ILogEventSink
    {
        private readonly List<LogEvent> _events = [];

        public List<LogEvent> Events
        {
            get
            {
                lock (_events)
                    return [.. _events];
            }
        }

        public void Emit(LogEvent logEvent)
        {
            lock (_events)
                _events.Add(logEvent);
        }
    }

    private PartitionPlacementTestConnector Placing(params List<ConnectedSystemImportObject>[] pages) => new([.. pages]);

    private async Task<ConnectedSystemObjectType> CreateDirectoryTypeAsync(string name) =>
        await CreateCsoTypeAsync(_system.Id, name,
        [
            new() { Name = "objectGUID", Type = AttributeDataType.Guid, IsExternalId = true, Selected = true },
            new() { Name = "distinguishedName", Type = AttributeDataType.Text, IsSecondaryExternalId = true, Selected = true },
            new() { Name = "DisplayName", Type = AttributeDataType.Text, Selected = true }
        ]);

    private async Task<ConnectedSystemPartition> CreatePartitionAsync(string distinguishedName, string name, bool selected = true)
    {
        var partition = new ConnectedSystemPartition { ConnectedSystem = _system, ExternalId = distinguishedName, Name = name, Selected = selected };
        DbContext.ConnectedSystemPartitions.Add(partition);
        await DbContext.SaveChangesAsync();
        return partition;
    }

    private async Task<Activity> RunAsync(IConnector connector, ConnectedSystemRunType runType, ConnectedSystemPartition? partition = null,
        Action<ConnectedSystemRunProfile>? configure = null)
    {
        var runProfile = await CreateRunProfileAsync(_system.Id, runType.ToString(), runType);
        runProfile.Partition = partition;
        configure?.Invoke(runProfile);
        var activity = await CreateActivityAsync(_system.Id, runProfile, runType);
        var workerTask = new SynchronisationWorkerTask(_system.Id, runProfile.Id)
        {
            Id = Guid.NewGuid(),
            Status = WorkerTaskStatus.Processing,
            Activity = activity
        };

        var processor = new SyncImportTaskProcessor(
            Jim, SyncRepo, new SyncServer(Jim), new SyncEngine(),
            connector, _system, runProfile, workerTask, new CancellationTokenSource());
        await processor.PerformImportAsync();
        return activity;
    }

    private ConnectedSystemImportObject User(Guid objectGuid, string distinguishedName, string? displayName = "User",
        ObjectChangeType changeType = ObjectChangeType.NotSet) =>
        DirectoryObject(_user, objectGuid, distinguishedName, displayName, changeType);

    private static ConnectedSystemImportObject DirectoryObject(ConnectedSystemObjectType type, Guid objectGuid, string distinguishedName, string? displayName,
        ObjectChangeType changeType = ObjectChangeType.NotSet)
    {
        var importObject = new ConnectedSystemImportObject
        {
            ObjectType = type.Name,
            ChangeType = changeType,
            Attributes =
            [
                new ConnectedSystemImportObjectAttribute { Name = "objectGUID", Type = AttributeDataType.Guid, GuidValues = [objectGuid] },
                new ConnectedSystemImportObjectAttribute { Name = "distinguishedName", Type = AttributeDataType.Text, StringValues = [distinguishedName] }
            ]
        };
        if (displayName != null)
            importObject.Attributes.Add(new ConnectedSystemImportObjectAttribute { Name = "DisplayName", Type = AttributeDataType.Text, StringValues = [displayName] });
        return importObject;
    }

    /// <summary>The Connected System Object JIM holds for an objectGUID.</summary>
    private ConnectedSystemObject Held(Guid objectGuid) =>
        SyncRepo.ConnectedSystemObjects.Values.Single(cso => cso.AttributeValues.Any(value => value.GuidValue == objectGuid));

    /// <summary>A Connected System Object as an earlier import would have left it, recorded against a Partition.</summary>
    private SeededObject SeedCso(Guid objectGuid, string distinguishedName, string displayName, ConnectedSystemPartition? partition)
    {
        var cso = NewCso(ConnectedSystemObjectStatus.Normal, partition);
        AddValue(cso, "objectGUID", guidValue: objectGuid);
        AddValue(cso, "distinguishedName", stringValue: distinguishedName);
        AddValue(cso, "DisplayName", stringValue: displayName);
        SyncRepo.SeedConnectedSystemObject(cso);
        return new SeededObject(cso, objectGuid);
    }

    /// <summary>An object JIM has provisioned and no import has confirmed: it has its DN and no objectGUID yet.</summary>
    private ConnectedSystemObject SeedPendingProvisioning(string distinguishedName)
    {
        var cso = NewCso(ConnectedSystemObjectStatus.PendingProvisioning, partition: null);
        AddValue(cso, "distinguishedName", stringValue: distinguishedName);
        SyncRepo.SeedConnectedSystemObject(cso);
        return cso;
    }

    private ConnectedSystemObject NewCso(ConnectedSystemObjectStatus status, ConnectedSystemPartition? partition) => new()
    {
        Id = Guid.NewGuid(),
        ConnectedSystemId = _system.Id,
        ConnectedSystem = _system,
        TypeId = _user.Id,
        Type = _user,
        ExternalIdAttributeId = _user.Attributes.Single(a => a.IsExternalId).Id,
        SecondaryExternalIdAttributeId = _user.Attributes.Single(a => a.IsSecondaryExternalId).Id,
        Status = status,
        PartitionId = partition?.Id,
        Created = DateTime.UtcNow
    };

    private void AddValue(ConnectedSystemObject cso, string attributeName, Guid? guidValue = null, string? stringValue = null)
    {
        var attribute = _user.Attributes.Single(a => a.Name == attributeName);
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

    #endregion
}
