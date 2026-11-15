// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Text;
using JIM.Application;
using JIM.Application.Servers;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Staging;
using JIM.Models.Tasking;
using JIM.Models.Transactional;
using JIM.PostgresData;
using JIM.TestSupport;
using JIM.Worker.Processors;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NUnit.Framework;

namespace JIM.Worker.Tests.Workflows;

/// <summary>
/// A confirming Full Import's Pending Export verdicts over more than one save batch, end to end on real PostgreSQL (#2521):
/// a change held until its Initial Password is delivered, one released because none is owed, one held because Exchange
/// still owes it, one not confirmed and kept to be sent again, one confirmed, and a Pending Export on an object the import
/// found deleted. Reconciliation loads each save batch's Pending Exports as it reaches the batch; every verdict must be the
/// one the whole-system load reached.
/// <para>
/// Run with and without a context factory: given one, the import loads the next batch's Pending Exports on a context of its
/// own while it reconciles the current batch, as the Worker does. The deleted object's Pending Export is the case that
/// told the two loads apart: deletion detection removes it before the save phase, and a load taken at the start of the
/// run still held it, so reconciliation judged an export that no longer existed and reported it unconfirmed on the
/// object's deletion. Opt-in via the <c>JIM_TEST_RESET_*</c> environment variables; <c>SetUp</c> TRUNCATEs every table.
/// </para>
/// </summary>
[TestFixture]
[Category("RequiresPostgres")]
public class ConfirmingImportPendingExportHoldsDatabaseTests
{
    private const int Disabled = 514;
    private const int Enabled = 512;

    private string _connectionString = null!;
    private int _systemId;
    private readonly Dictionary<string, int> _attributeIds = [];
    private readonly Dictionary<Guid, string> _names = [];

    private static readonly Guid Held = Guid.Parse("00000000-0000-0000-0000-00000000d001");
    private static readonly Guid Released = Guid.Parse("00000000-0000-0000-0000-00000000d002");
    private static readonly Guid Exchange = Guid.Parse("00000000-0000-0000-0000-00000000d003");
    private static readonly Guid Retried = Guid.Parse("00000000-0000-0000-0000-00000000d004");
    private static readonly Guid Confirmed = Guid.Parse("00000000-0000-0000-0000-00000000d005");
    private static readonly Guid Gone = Guid.Parse("00000000-0000-0000-0000-00000000d006");
    private static readonly Guid HeldPerson = Guid.Parse("00000000-0000-0000-0000-00000000e001");
    private static readonly Guid ReleasedPerson = Guid.Parse("00000000-0000-0000-0000-00000000e002");

    private JimDbContext NewContext(bool trackAll = false)
    {
        var builder = new DbContextOptionsBuilder<JimDbContext>()
            .UseNpgsql(_connectionString)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning));
        // The Worker tracks by default (src/JIM.Worker/Program.cs); an import run must too.
        if (trackAll)
            builder.UseQueryTrackingBehavior(QueryTrackingBehavior.TrackAll);
        return new JimDbContext(builder.Options);
    }

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        var dbName = Environment.GetEnvironmentVariable("JIM_TEST_RESET_DB");
        if (string.IsNullOrEmpty(dbName))
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL confirming import hold tests.");

        var host = Environment.GetEnvironmentVariable("JIM_TEST_RESET_HOST") ?? "localhost";
        var user = Environment.GetEnvironmentVariable("JIM_TEST_RESET_USER") ?? "postgres";
        var pass = Environment.GetEnvironmentVariable("JIM_TEST_RESET_PASSWORD") ?? "postgres";
        var port = Environment.GetEnvironmentVariable("JIM_TEST_RESET_PORT") ?? "5432";
        _connectionString = $"Host={host};Port={port};Database={dbName};Username={user};Password={pass}";

        TestUtilities.SetEnvironmentVariables();
        using var ctx = NewContext();
        ctx.Database.Migrate();
    }

    [SetUp]
    public async Task SetUpAsync()
    {
        await PostgresTestDatabase.ResetAsync(_connectionString);
        _attributeIds.Clear();
        _names.Clear();
        await SeedAsync();
    }

    /// <param name="importBatchSize">The save phase's batch size: one batch for the run, or two objects a batch, so the five
    /// objects the import updates are reconciled over three batches.</param>
    /// <param name="withContextFactory">Whether the import is given a context factory, as the Worker gives it, for loading
    /// Pending Exports alongside its own work.</param>
    [TestCase(2000, false)]
    [TestCase(2, false)]
    [TestCase(2, true)]
    public async Task FullImport_ConfirmingHoldsAndReleasesAcrossSaveBatches_ReachesTheSameVerdictsAsync(int importBatchSize, bool withContextFactory)
    {
        var activityId = await RunConfirmingFullImportAsync(importBatchSize, withContextFactory);

        var snapshot = await SnapshotAsync(activityId);

        Assert.That(snapshot, Is.EqualTo(ExpectedSnapshot), "A Pending Export verdict changed. Actual:\n" + snapshot);
    }

    /// <summary>
    /// Taken from reconciliation as it ran before #2521 without a context factory, which loaded the Pending Exports once
    /// the save phase began. Held: owed a password, so its change stays awaiting confirmation with no item. Released: owed
    /// nothing, so released to be sent again, with no item: the interim value was written on purpose. Exchange: still
    /// queued for Exchange, so held with no item. Retried: not what was exported, reported on its item and kept. Confirmed:
    /// its Pending Export deleted, with the outcome on its item. Gone: deletion detection removed its Pending Export and
    /// recorded only the deletion. (The Activity's error total is taken before reconciliation merges Retried's error onto
    /// its item, as it always has been.)
    /// </summary>
    private const string ExpectedSnapshot = """
        Pending Exports:
          exchange: Update Exported
            recipientType ExportedPendingConfirmation awaitsExchange=True awaitsPassword=False attempts=1
          held: Update Exported
            userAccountControl ExportedPendingConfirmation awaitsExchange=False awaitsPassword=True attempts=1
          released: Update Pending
            userAccountControl Pending awaitsExchange=False awaitsPassword=False attempts=1
          retried: Update ExportNotConfirmed
            displayName ExportedNotConfirmed awaitsExchange=False awaitsPassword=False attempts=1
        Execution items:
          confirmed: Updated error=NotSet summary=CsoUpdated:1,ExportConfirmed:1
          gone: Deleted error=NotSet summary=DeletionDetected:1
          retried: Updated error=ExportNotConfirmed summary=CsoUpdated:1,ExportFailed:1
        Activity:
          confirmed=1 errors=0

        """;

    #region Harness

    private async Task SeedAsync()
    {
        await using (var seed = NewContext())
        {
            var connectorDefinition = new ConnectorDefinition { Name = "JIM Test Connector", BuiltIn = true };
            var system = new ConnectedSystem { Name = "Target Directory", ConnectorDefinition = connectorDefinition };
            var user = new ConnectedSystemObjectType { Name = "user", ConnectedSystem = system, Selected = true };

            void Attribute(string name, AttributeDataType dataType, bool externalId = false, bool secondaryExternalId = false, bool connectorDefined = false) =>
                user.Attributes.Add(new ConnectedSystemObjectTypeAttribute
                {
                    Name = name, ConnectedSystemObjectType = user, Type = dataType, AttributePlurality = AttributePlurality.SingleValued,
                    Selected = true, IsExternalId = externalId, IsSecondaryExternalId = secondaryExternalId, IsConnectorDefined = connectorDefined
                });

            Attribute("id", AttributeDataType.Text, externalId: true);
            Attribute("uid", AttributeDataType.Text, secondaryExternalId: true);
            Attribute("displayName", AttributeDataType.Text);
            Attribute("userAccountControl", AttributeDataType.Number);
            Attribute("recipientType", AttributeDataType.Text, connectorDefined: true);

            var person = new MetaverseObjectType { Name = "Person", PluralName = "People" };
            seed.AddRange(connectorDefinition, system, user, person);
            seed.MetaverseObjects.AddRange(
                new MetaverseObject { Id = MetaverseObjectOf(Held), Type = person, Created = DateTime.UtcNow },
                new MetaverseObject { Id = MetaverseObjectOf(Released), Type = person, Created = DateTime.UtcNow });
            await seed.SaveChangesAsync();
            seed.ConnectedSystemRunProfiles.Add(new ConnectedSystemRunProfile
                { ConnectedSystemId = system.Id, Name = "Full Import", RunType = ConnectedSystemRunType.FullImport });
            await seed.SaveChangesAsync();

            _systemId = system.Id;
            _attributeIds["user"] = user.Id;
            foreach (var attribute in user.Attributes)
                _attributeIds[attribute.Name] = attribute.Id;
        }

        var csos = new[]
        {
            Cso(Held, "held", metaverseObjectId: MetaverseObjectOf(Held), userAccountControl: Disabled),
            Cso(Released, "released", metaverseObjectId: MetaverseObjectOf(Released), userAccountControl: Disabled),
            Cso(Exchange, "exchange", recipientType: "None"),
            Cso(Retried, "retried"),
            Cso(Confirmed, "confirmed"),
            Cso(Gone, "gone")
        };

        await using (var write = NewContext())
        {
            var sync = new PostgresDataRepository(write).Sync;
            await sync.CreateConnectedSystemObjectsAsync([.. csos]);

            var exportedAt = DateTime.UtcNow.AddMinutes(-30);
            await sync.CreatePendingExportsAsync([
                Export(Held, "userAccountControl", change => { change.IntValue = Enabled; change.AwaitsInitialPassword = true; }, exportedAt),
                Export(Released, "userAccountControl", change => { change.IntValue = Enabled; change.AwaitsInitialPassword = true; }, exportedAt),
                Export(Exchange, "recipientType", change => { change.StringValue = "Mailbox"; change.AwaitsExchange = true; }, exportedAt),
                Export(Retried, "displayName", change => change.StringValue = "Retried Renamed", exportedAt),
                Export(Confirmed, "displayName", change => change.StringValue = "Confirmed Renamed", exportedAt),
                Export(Gone, "displayName", change => change.StringValue = "Gone Renamed", exportedAt)
            ]);

            // Held's account is still owed its Initial Password; Released's is owed nothing. Exchange still has its change
            // on the Exchange queue.
            await sync.QueuePasswordChangesAsync([new PendingPasswordChange
            {
                Id = Guid.NewGuid(), MetaverseObjectId = MetaverseObjectOf(Held), ConnectedSystemId = _systemId, ConnectedSystemObjectId = Held,
                Status = PendingPasswordChangeStatus.Pending, Origin = PendingPasswordChangeOrigin.Provisioned, CreatedAt = DateTime.UtcNow,
                ExpiresAt = DateTime.UtcNow.AddDays(1), ActivityId = Guid.NewGuid()
            }]);
            await sync.StageExchangeChangesAsync([new PendingExchangeChange
            {
                Id = Guid.NewGuid(), ConnectedSystemId = _systemId, ConnectedSystemObjectId = Exchange, RecipientType = "Mailbox",
                Status = PendingExchangeChangeStatus.Pending, CreatedAt = DateTime.UtcNow, ActivityId = Guid.NewGuid()
            }]);
        }
    }

    private static Guid MetaverseObjectOf(Guid connectedSystemObjectId) =>
        connectedSystemObjectId == Held ? HeldPerson : connectedSystemObjectId == Released ? ReleasedPerson : throw new ArgumentOutOfRangeException(nameof(connectedSystemObjectId));

    private ConnectedSystemObject Cso(Guid id, string name, Guid? metaverseObjectId = null, int? userAccountControl = null, string? recipientType = null)
    {
        _names[id] = name;
        var cso = new ConnectedSystemObject
        {
            Id = id, ConnectedSystemId = _systemId, TypeId = _attributeIds["user"], ExternalIdAttributeId = _attributeIds["id"],
            SecondaryExternalIdAttributeId = _attributeIds["uid"], MetaverseObjectId = metaverseObjectId, Status = ConnectedSystemObjectStatus.Normal,
            Created = DateTime.UtcNow.AddHours(-2)
        };
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Id = Guid.NewGuid(), AttributeId = _attributeIds["id"], StringValue = name });
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Id = Guid.NewGuid(), AttributeId = _attributeIds["uid"], StringValue = name });
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Id = Guid.NewGuid(), AttributeId = _attributeIds["displayName"], StringValue = name });
        if (userAccountControl is { } control)
            cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Id = Guid.NewGuid(), AttributeId = _attributeIds["userAccountControl"], IntValue = control });
        if (recipientType != null)
            cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Id = Guid.NewGuid(), AttributeId = _attributeIds["recipientType"], StringValue = recipientType });
        return cso;
    }

    private PendingExport Export(Guid csoId, string attribute, Action<PendingExportAttributeValueChange> setValue, DateTime exportedAt)
    {
        var change = new PendingExportAttributeValueChange
        {
            Id = Guid.NewGuid(), AttributeId = _attributeIds[attribute], ChangeType = PendingExportAttributeChangeType.Update,
            Status = PendingExportAttributeChangeStatus.ExportedPendingConfirmation, ExportAttemptCount = 1, LastExportedAt = exportedAt
        };
        setValue(change);
        _names[change.Id] = attribute;
        var pendingExport = new PendingExport
        {
            Id = Guid.NewGuid(), ConnectedSystemId = _systemId, ConnectedSystemObjectId = csoId, ChangeType = PendingExportChangeType.Update,
            Status = PendingExportStatus.Exported, CreatedAt = exportedAt.AddMinutes(-5), LastAttemptedAt = exportedAt, AttributeValueChanges = [change]
        };
        _names[pendingExport.Id] = _names[csoId];
        return pendingExport;
    }

    /// <summary>
    /// What the directory returns: every object but Gone. Each one's name has changed, so every object is compared and
    /// reconciled; Confirmed's is the name exported, Retried's is not, and the two accounts are still disabled.
    /// </summary>
    private static List<List<ConnectedSystemImportObject>> DirectoryPages()
    {
        static ConnectedSystemImportObjectAttribute Text(string name, string value) =>
            new() { Name = name, Type = AttributeDataType.Text, StringValues = [value] };

        static ConnectedSystemImportObject User(string id, string displayName, params ConnectedSystemImportObjectAttribute[] more) =>
            new() { ObjectType = "user", ChangeType = ObjectChangeType.NotSet, Attributes = [Text("id", id), Text("uid", id), Text("displayName", displayName), .. more] };

        static ConnectedSystemImportObjectAttribute Control(int value) =>
            new() { Name = "userAccountControl", Type = AttributeDataType.Number, IntValues = [value] };

        return
        [
            [
                User("held", "Held Renamed", Control(Disabled)),
                User("released", "Released Renamed", Control(Disabled)),
                User("exchange", "Exchange Renamed", Text("recipientType", "None"))
            ],
            [
                User("retried", "Someone Else"),
                User("confirmed", "Confirmed Renamed")
            ]
        ];
    }

    private async Task<Guid> RunConfirmingFullImportAsync(int importBatchSize, bool withContextFactory)
    {
        await using var ctx = NewContext(trackAll: true);
        var repository = new PostgresDataRepository(ctx);
        using var jim = new JimApplication(repository, syncRepository: repository.Sync);
        var system = await jim.ConnectedSystems.GetConnectedSystemAsync(_systemId, withChangeTracking: true)
            ?? throw new InvalidOperationException("Connected System not found.");
        var runProfile = system.RunProfiles!.Single(rp => rp.RunType == ConnectedSystemRunType.FullImport);
        var activity = new Activity
        {
            TargetName = runProfile.Name, Status = ActivityStatus.InProgress, ConnectedSystemId = system.Id,
            ConnectedSystemRunProfileId = runProfile.Id, ConnectedSystemRunType = ConnectedSystemRunType.FullImport
        };
        ctx.Activities.Add(activity);
        await ctx.SaveChangesAsync();

        var workerTask = new SynchronisationWorkerTask(system.Id, runProfile.Id)
        {
            Id = Guid.NewGuid(), Status = WorkerTaskStatus.Processing, Activity = activity
        };
        var processor = new SyncImportTaskProcessor(jim, jim.SyncRepository, new SyncServer(jim), new SyncEngine(),
            new PagedImportConnector(DirectoryPages()), system, runProfile, workerTask, new CancellationTokenSource(),
            withContextFactory ? new ContextFactory(() => NewContext()) : null)
        {
            ImportBatchSize = importBatchSize
        };

        await processor.PerformImportAsync();
        await jim.SyncRepository.UpdateActivityAsync(activity);
        return activity.Id;
    }

    /// <summary>The Pending Exports left with their changes, the run's execution items, and its counters. Names stand in for ids.</summary>
    private async Task<string> SnapshotAsync(Guid activityId)
    {
        await using var ctx = NewContext();
        string Name(Guid? id) => id is { } value && _names.TryGetValue(value, out var name) ? name : "(unnamed)";

        var text = new StringBuilder();
        text.AppendLine("Pending Exports:");
        var exports = await ctx.PendingExports.AsNoTracking().Include(p => p.AttributeValueChanges)
            .Where(p => p.ConnectedSystemId == _systemId).ToListAsync();
        foreach (var export in exports.OrderBy(p => Name(p.Id), StringComparer.Ordinal))
        {
            text.AppendLine($"  {Name(export.Id)}: {export.ChangeType} {export.Status}");
            foreach (var change in export.AttributeValueChanges.OrderBy(c => c.Id))
                text.AppendLine($"    {Name(change.Id)} {change.Status} awaitsExchange={change.AwaitsExchange} awaitsPassword={change.AwaitsInitialPassword} " +
                    $"attempts={change.ExportAttemptCount}");
        }

        text.AppendLine("Execution items:");
        var items = await ctx.ActivityRunProfileExecutionItems.AsNoTracking().Where(i => i.ActivityId == activityId).ToListAsync();
        foreach (var item in items.Where(i => i.ErrorType != ActivityRunProfileExecutionItemErrorType.NotSet || i.OutcomeSummary?.Contains("Export") == true
                         || i.ObjectChangeType == ObjectChangeType.Deleted)
                     .OrderBy(i => Name(i.ConnectedSystemObjectId), StringComparer.Ordinal))
            text.AppendLine($"  {Name(item.ConnectedSystemObjectId)}: {item.ObjectChangeType} error={item.ErrorType?.ToString() ?? "-"} summary={item.OutcomeSummary ?? "-"}");

        var activity = await ctx.Activities.AsNoTracking().SingleAsync(a => a.Id == activityId);
        text.AppendLine("Activity:");
        text.AppendLine($"  confirmed={activity.PendingExportsConfirmed} errors={activity.TotalErrors}");
        return text.ToString().Replace("\r\n", "\n");
    }

    /// <summary>Hands out a new context per call, as the Worker's pooled factory does.</summary>
    private sealed class ContextFactory(Func<JimDbContext> create) : IDbContextFactory<JimDbContext>
    {
        public JimDbContext CreateDbContext() => create();
    }

    #endregion
}
