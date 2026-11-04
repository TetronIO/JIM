// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Application.Servers;
using JIM.Connectors.LDAP;
using JIM.Connectors.Mock;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Interfaces;
using JIM.Models.Staging;
using JIM.Models.Tasking;
using JIM.Models.Transactional;
using JIM.Worker.Processors;
using NUnit.Framework;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace JIM.Worker.Tests.Workflows;

/// <summary>
/// #2723 (D2): an object JIM provisions is created Pending Provisioning with no Partition, and every Partition-scoped
/// reader filters on the object's Partition, so until an import confirmed it no Partition-scoped run could see it: a
/// scoped Full Import never retried a Create that was exported and never confirmed. The export now records where the
/// object was written, from the distinguished name the Connector reports (else the one JIM provisioned), when a Create
/// or a rename succeeds, and keeps what it holds when the Connector has no answer.
/// </summary>
[TestFixture]
public class ProvisionedObjectPartitionTests : WorkflowTestBase
{
    private ConnectedSystem _system = null!;
    private ConnectedSystemObjectType _user = null!;
    private ConnectedSystemPartition _corp = null!;
    private ConnectedSystemPartition _partners = null!;
    private PartitionWriteRecordingSyncRepository _syncRepo = null!;

    [SetUp]
    public async Task SetUpAsync()
    {
        // Replaced before anything is seeded, so every helper seeds the repository the runs use.
        _syncRepo = new PartitionWriteRecordingSyncRepository();
        _syncRepo.SetSyncOutcomeTrackingLevel(ActivityRunProfileExecutionItemSyncOutcomeTrackingLevel.Detailed);
        SyncRepo = _syncRepo;
        Jim = new JimApplication(Repository, syncRepository: SyncRepo);

        _system = await CreateConnectedSystemAsync("Directory");
        _user = await CreateCsoTypeAsync(_system.Id, "user",
        [
            new() { Name = "objectGUID", Type = AttributeDataType.Guid, IsExternalId = true, Selected = true },
            new() { Name = "distinguishedName", Type = AttributeDataType.Text, IsSecondaryExternalId = true, Selected = true },
            new() { Name = "DisplayName", Type = AttributeDataType.Text, Selected = true }
        ]);
        _corp = await CreatePartitionAsync("dc=corp,dc=local", "corp.local");
        _partners = await CreatePartitionAsync("dc=partners,dc=local", "partners.local");
    }

    #region Where an exported object is recorded

    [Test]
    public async Task Export_SuccessfulCreate_RecordsThePartitionWrittenToAsync()
    {
        const string dn = "uid=new,ou=people,dc=corp,dc=local";
        var (provisioned, _) = SeedProvisioning(dn);

        await ExportAsync(PlacingExportConnector.Writing(dn));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(provisioned.PartitionId, Is.EqualTo(_corp.Id));
            Assert.That(_syncRepo.PartitionsWritten, Is.EqualTo(new[] { (provisioned.Id, _corp.Id) }),
                "persisted, not only set on the instance the export holds");
        }
    }

    [Test]
    public async Task Export_CreateResultWithNoDistinguishedName_RecordsThePartitionOfTheProvisionedOneAsync()
    {
        // A Connector need not report the name it wrote; the one JIM provisioned is then where the object is.
        var (provisioned, _) = SeedProvisioning("uid=new,ou=people,dc=partners,dc=local");

        await ExportAsync(PlacingExportConnector.Writing(distinguishedName: null));

        Assert.That(provisioned.PartitionId, Is.EqualTo(_partners.Id));
    }

    [Test]
    public async Task Export_RenameIntoAnotherPartition_RecordsTheNewPartitionAsync()
    {
        const string newDn = "uid=sam,ou=people,dc=partners,dc=local";
        var sam = SeedConfirmed("uid=sam,ou=people,dc=corp,dc=local", _corp);
        SeedRename(sam, newDn);

        await ExportAsync(PlacingExportConnector.Writing(newDn));

        Assert.That(sam.PartitionId, Is.EqualTo(_partners.Id));
    }

    [Test]
    public async Task Export_RenameOutsideEveryPartition_KeepsTheStoredPartitionAsync()
    {
        // Guard, not a red test: the Connected System's Partitions may be out of date, and no answer is not evidence the
        // object left the Partition JIM holds for it.
        const string newDn = "uid=sam,ou=people,dc=elsewhere,dc=local";
        var sam = SeedConfirmed("uid=sam,ou=people,dc=corp,dc=local", _corp);
        SeedRename(sam, newDn);

        await ExportAsync(PlacingExportConnector.Writing(newDn));

        Assert.That(sam.PartitionId, Is.EqualTo(_corp.Id));
    }

    [Test]
    public async Task Export_ConnectorWithoutPlacement_RecordsNoPartitionAsync()
    {
        // Guard, not a red test: a Connector that cannot say where its objects are leaves the object as it was before
        // placement, for its confirming import to record.
        const string dn = "uid=new,ou=people,dc=corp,dc=local";
        var (provisioned, _) = SeedProvisioning(dn);

        await ExportAsync(new MockCallConnector().WithConnectedSystemExportResultFactory(_ =>
            ConnectedSystemExportResult.Succeeded(Guid.NewGuid().ToString(), dn)));

        Assert.That(provisioned.PartitionId, Is.Null);
    }

    [Test]
    public async Task Export_PreparesThePlacementOnceForTheRunAsync()
    {
        SeedProvisioning("uid=pat,ou=people,dc=corp,dc=local");
        SeedProvisioning("uid=sam,ou=people,dc=partners,dc=local");
        var connector = PlacingExportConnector.Writing(distinguishedName: null);

        await ExportAsync(connector);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(connector.IdentifiersPlaced, Is.EquivalentTo(new[] { "uid=pat,ou=people,dc=corp,dc=local", "uid=sam,ou=people,dc=partners,dc=local" }));
            Assert.That(connector.PreparedCount, Is.EqualTo(1), "every Partition is selected, so one placer serves the whole run");
        }
    }

    [Test]
    public async Task Export_SummarisesWhereItPlacedWhatItWroteInOneLogEventAsync()
    {
        // One object placed in a Partition it was not recorded against, one in none: one summary for the run, a warning
        // because an object could not be placed.
        SeedProvisioning("uid=pat,ou=people,dc=corp,dc=local");
        SeedProvisioning("uid=ann,ou=people,dc=elsewhere,dc=local");

        var summaries = await CaptureLogAsync(() => ExportAsync(PlacingExportConnector.Writing(distinguishedName: null)),
            logEvent => logEvent.Properties.ContainsKey("Placed") && logEvent.Properties.ContainsKey("InNoPartition") && logEvent.Properties.ContainsKey("Restamped"));

        Assert.That(summaries, Has.Count.EqualTo(1), "one summary per run, whatever the number of objects");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(summaries[0].Level, Is.EqualTo(LogEventLevel.Warning));
            Assert.That(summaries[0].Properties["Placed"].ToString(), Is.EqualTo("1"));
            Assert.That(summaries[0].Properties["InNoPartition"].ToString(), Is.EqualTo("1"));
            Assert.That(summaries[0].Properties["Restamped"].ToString(), Is.EqualTo("1"));
        }
    }

    [Test]
    public async Task Export_PartitionCannotBeRecorded_StillRecordsTheSuccessfulCreateAsync()
    {
        // Where an object is, is worth less than knowing it was created: a Create whose result went unrecorded would be
        // sent again. The object keeps the Partition it had, for the import that confirms it to record, and the failure is
        // counted in the run's summary.
        const string dn = "uid=new,ou=people,dc=corp,dc=local";
        var objectGuid = Guid.NewGuid();
        var (provisioned, export) = SeedProvisioning(dn);
        _syncRepo.FailPartitionWritesWith = new InvalidOperationException("Simulated failure to record a Partition");

        Activity activity = null!;
        var summaries = await CaptureLogAsync(async () => activity = await ExportAsync(PlacingExportConnector.Writing(dn, objectGuid)),
            logEvent => logEvent.Properties.ContainsKey("Placed") && logEvent.Properties.ContainsKey("NotRecorded"));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(activity.RunProfileExecutionItems.Where(item => item.ConnectedSystemObjectId == provisioned.Id).Select(item => item.ErrorType).ToList(),
                Is.EqualTo(new ActivityRunProfileExecutionItemErrorType?[] { ActivityRunProfileExecutionItemErrorType.NotSet }), "the export is reported as the success it was");
            Assert.That(export.Status, Is.EqualTo(PendingExportStatus.Exported), "the Create is recorded as sent");
            Assert.That(provisioned.AttributeValues.Any(value => value.GuidValue == objectGuid), Is.True, "with the External ID it was given");
            Assert.That(provisioned.PartitionId, Is.Null);
            Assert.That(summaries.Select(summary => summary.Properties["NotRecorded"].ToString()).ToList(), Is.EqualTo(new[] { "1" }));
        }
    }

    #endregion

    #region What Partition-scoped runs now see

    [Test]
    public async Task FullImport_ScopedAfterAnExportedCreateNoImportConfirmed_RetriesTheCreateAsync()
    {
        // #2723 for provisioning: the Create was exported, and the scoped Full Import that should have found the object did
        // not. It must be retried, which it can only be if the run's Partition holds the object.
        const string dn = "uid=new,ou=people,dc=corp,dc=local";
        var (provisioned, export) = SeedProvisioning(dn);
        await ExportAsync(PlacingExportConnector.Writing(dn));
        Assert.That(export.Status, Is.EqualTo(PendingExportStatus.Exported), "precondition: the Create was exported");

        var activity = await ImportAsync(_corp, User(Guid.NewGuid(), "uid=cal,ou=people,dc=corp,dc=local"));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(export.Status, Is.EqualTo(PendingExportStatus.ExportNotConfirmed), "the Create is sent again at the next export");
            Assert.That(activity.RunProfileExecutionItems.Where(item => item.ConnectedSystemObjectId == provisioned.Id).Select(item => item.ErrorType).ToList(),
                Is.EqualTo(new[] { ActivityRunProfileExecutionItemErrorType.ExportNotConfirmed }));
        }
    }

    [Test]
    public async Task FullImport_Scoped_DoesNotRetryAnExportedCreateInAnotherPartitionAsync()
    {
        // Guard, not a red test: recording where the object was written must not widen what a scoped run judges.
        const string dn = "uid=new,ou=people,dc=partners,dc=local";
        var (_, export) = SeedProvisioning(dn);
        await ExportAsync(PlacingExportConnector.Writing(dn));

        await ImportAsync(_corp, User(Guid.NewGuid(), "uid=cal,ou=people,dc=corp,dc=local"));

        Assert.That(export.Status, Is.EqualTo(PendingExportStatus.Exported), "partners.local was not read");
    }

    [Test]
    public async Task FullImport_ScopedAfterAnUnscopedImportConfirmedAProvisionedObject_MarksItDeletedWhenGoneAsync()
    {
        // Provisioned, confirmed by an unscoped Full Import, deleted in the directory, and only Partition-scoped Run
        // Profiles run from then on: the scoped Full Import must notice.
        const string dn = "uid=new,ou=people,dc=corp,dc=local";
        var objectGuid = Guid.NewGuid();
        var (provisioned, _) = SeedProvisioning(dn);
        await ExportAsync(PlacingExportConnector.Writing(dn, objectGuid));
        await ImportAsync(partition: null, User(objectGuid, dn));
        Assert.That(provisioned.Status, Is.EqualTo(ConnectedSystemObjectStatus.Normal), "precondition: the unscoped import confirmed the object");

        await ImportAsync(_corp, User(Guid.NewGuid(), "uid=cal,ou=people,dc=corp,dc=local"));

        Assert.That(provisioned.Status, Is.EqualTo(ConnectedSystemObjectStatus.Obsolete));
    }

    #endregion

    #region Helpers

    private async Task<ConnectedSystemPartition> CreatePartitionAsync(string distinguishedName, string name)
    {
        var partition = new ConnectedSystemPartition { ConnectedSystem = _system, ExternalId = distinguishedName, Name = name, Selected = true };
        DbContext.ConnectedSystemPartitions.Add(partition);
        await DbContext.SaveChangesAsync();
        return partition;
    }

    private async Task<Activity> ExportAsync(IConnector connector)
    {
        var runProfile = await CreateRunProfileAsync(_system.Id, "Export", ConnectedSystemRunType.Export);
        var activity = await CreateActivityAsync(_system.Id, runProfile, ConnectedSystemRunType.Export);
        var processor = new SyncExportTaskProcessor(new SyncServer(Jim), SyncRepo, connector, _system, runProfile, WorkerTask(runProfile, activity),
            new CancellationTokenSource());
        await processor.PerformExportAsync();
        return activity;
    }

    private async Task<Activity> ImportAsync(ConnectedSystemPartition? partition, params ConnectedSystemImportObject[] importObjects)
    {
        var runProfile = await CreateRunProfileAsync(_system.Id, "Full Import", ConnectedSystemRunType.FullImport);
        runProfile.Partition = partition;
        var activity = await CreateActivityAsync(_system.Id, runProfile, ConnectedSystemRunType.FullImport);
        var processor = new SyncImportTaskProcessor(
            Jim, SyncRepo, new SyncServer(Jim), new SyncEngine(),
            new PartitionPlacementTestConnector([[.. importObjects]]), _system, runProfile, WorkerTask(runProfile, activity), new CancellationTokenSource());
        await processor.PerformImportAsync();
        return activity;
    }

    private SynchronisationWorkerTask WorkerTask(ConnectedSystemRunProfile runProfile, Activity activity) =>
        new(_system.Id, runProfile.Id)
        {
            Id = Guid.NewGuid(),
            Status = WorkerTaskStatus.Processing,
            Activity = activity
        };

    private ConnectedSystemImportObject User(Guid objectGuid, string distinguishedName) => new()
    {
        ObjectType = _user.Name,
        ChangeType = ObjectChangeType.NotSet,
        Attributes =
        [
            new ConnectedSystemImportObjectAttribute { Name = "objectGUID", Type = AttributeDataType.Guid, GuidValues = [objectGuid] },
            new ConnectedSystemImportObjectAttribute { Name = "distinguishedName", Type = AttributeDataType.Text, StringValues = [distinguishedName] },
            new ConnectedSystemImportObjectAttribute { Name = "DisplayName", Type = AttributeDataType.Text, StringValues = ["New"] }
        ]
    };

    /// <summary>
    /// An object JIM has decided to provision, as export evaluation leaves it: Pending Provisioning, with the name it is to
    /// be created with and no Partition, and its Create queued.
    /// </summary>
    private (ConnectedSystemObject Cso, PendingExport Export) SeedProvisioning(string distinguishedName)
    {
        var cso = NewCso(ConnectedSystemObjectStatus.PendingProvisioning, partition: null);
        cso.JoinType = ConnectedSystemObjectJoinType.Provisioned;
        AddValue(cso, "distinguishedName", distinguishedName);
        AddValue(cso, "DisplayName", "New");
        SyncRepo.SeedConnectedSystemObject(cso);

        var export = NewExport(cso, PendingExportChangeType.Create,
            Change("distinguishedName", distinguishedName, PendingExportAttributeChangeType.Add),
            Change("DisplayName", "New", PendingExportAttributeChangeType.Add));
        SyncRepo.SeedPendingExport(export);
        return (cso, export);
    }

    /// <summary>An object an import has confirmed, recorded against a Partition.</summary>
    private ConnectedSystemObject SeedConfirmed(string distinguishedName, ConnectedSystemPartition partition)
    {
        var cso = NewCso(ConnectedSystemObjectStatus.Normal, partition);
        cso.JoinType = ConnectedSystemObjectJoinType.Provisioned;
        AddValue(cso, "objectGUID", guidValue: Guid.NewGuid());
        AddValue(cso, "distinguishedName", distinguishedName);
        AddValue(cso, "DisplayName", "Sam");
        SyncRepo.SeedConnectedSystemObject(cso);
        return cso;
    }

    /// <summary>Queues a rename of <paramref name="cso"/> to <paramref name="newDistinguishedName"/>.</summary>
    private void SeedRename(ConnectedSystemObject cso, string newDistinguishedName) =>
        SyncRepo.SeedPendingExport(NewExport(cso, PendingExportChangeType.Update,
            Change("distinguishedName", newDistinguishedName, PendingExportAttributeChangeType.Update)));

    private PendingExport NewExport(ConnectedSystemObject cso, PendingExportChangeType changeType, params PendingExportAttributeValueChange[] changes) => new()
    {
        Id = Guid.NewGuid(),
        ConnectedSystemId = _system.Id,
        ConnectedSystem = _system,
        ConnectedSystemObject = cso,
        ConnectedSystemObjectId = cso.Id,
        Status = PendingExportStatus.Pending,
        ChangeType = changeType,
        CreatedAt = DateTime.UtcNow,
        AttributeValueChanges = [.. changes]
    };

    private PendingExportAttributeValueChange Change(string attributeName, string value, PendingExportAttributeChangeType changeType)
    {
        var attribute = _user.Attributes.Single(a => a.Name == attributeName);
        return new PendingExportAttributeValueChange
        {
            Id = Guid.NewGuid(),
            AttributeId = attribute.Id,
            Attribute = attribute,
            ChangeType = changeType,
            StringValue = value,
            Status = PendingExportAttributeChangeStatus.Pending
        };
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

    private void AddValue(ConnectedSystemObject cso, string attributeName, string? stringValue = null, Guid? guidValue = null)
    {
        var attribute = _user.Attributes.Single(a => a.Name == attributeName);
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            AttributeId = attribute.Id,
            Attribute = attribute,
            StringValue = stringValue,
            GuidValue = guidValue,
            ConnectedSystemObject = cso
        });
    }

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

    /// <summary>
    /// The in-memory repository, recording each Partition written through it. It holds the instances its callers hold, so
    /// the Partition on an object cannot tell a write that was persisted from one that was only made on the export's copy.
    /// </summary>
    private sealed class PartitionWriteRecordingSyncRepository : JIM.InMemoryData.SyncRepository
    {
        public List<(Guid ConnectedSystemObjectId, int PartitionId)> PartitionsWritten { get; } = [];

        /// <summary>When set, writing a Partition throws it.</summary>
        public Exception? FailPartitionWritesWith { get; set; }

        public override Task<int> UpdateConnectedSystemObjectPartitionsAsync(IReadOnlyCollection<(Guid ConnectedSystemObjectId, int PartitionId)> placements)
        {
            if (FailPartitionWritesWith != null)
                throw FailPartitionWritesWith;

            lock (PartitionsWritten)
                PartitionsWritten.AddRange(placements);
            return base.UpdateConnectedSystemObjectPartitionsAsync(placements);
        }
    }

    /// <summary>
    /// A Connector that writes every change it is given and says which Partition an object is in by the LDAP Connector's
    /// own rule, as <see cref="PartitionPlacementTestConnector"/> does for an import. A Create reports a new objectGUID and,
    /// where asked, the distinguished name written; an Update reports the name, as a rename does.
    /// </summary>
    private sealed class PlacingExportConnector : MockCallConnector, IConnectorPartitionPlacement
    {
        private readonly Lock _lock = new();

        private PlacingExportConnector() { }

        /// <summary>Every identifier JIM asked a placer to place.</summary>
        public List<string?> IdentifiersPlaced { get; } = [];

        /// <summary>How many times JIM prepared a placer.</summary>
        public int PreparedCount { get; private set; }

        public static PlacingExportConnector Writing(string? distinguishedName, Guid? objectGuid = null)
        {
            var connector = new PlacingExportConnector();
            connector.WithConnectedSystemExportResultFactory(pendingExport => pendingExport.ChangeType == PendingExportChangeType.Create
                ? ConnectedSystemExportResult.Succeeded((objectGuid ?? Guid.NewGuid()).ToString(), distinguishedName)
                : ConnectedSystemExportResult.Succeeded(null, distinguishedName));
            return connector;
        }

        public IConnectorPartitionPlacer PreparePartitionPlacement(IReadOnlyCollection<ConnectedSystemPartition> partitions)
        {
            lock (_lock)
                PreparedCount++;
            using var ldapConnector = new LdapConnector();
            return new RecordingPlacer(this, ((IConnectorPartitionPlacement)ldapConnector).PreparePartitionPlacement(partitions));
        }

        private sealed class RecordingPlacer(PlacingExportConnector owner, IConnectorPartitionPlacer ldapPlacer) : IConnectorPartitionPlacer
        {
            public ConnectedSystemPartition? GetPartition(string? objectIdentifier)
            {
                lock (owner._lock)
                    owner.IdentifiersPlaced.Add(objectIdentifier);
                return ldapPlacer.GetPartition(objectIdentifier);
            }
        }
    }

    #endregion
}
