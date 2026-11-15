// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Text;
using System.Text.RegularExpressions;
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
/// A Full Import's deletion detection, end to end on real PostgreSQL (#2521, Phase 3): which held objects it finds missing,
/// which object each missing External ID resolves to, how it treats each by status, and what the Run Profile's deletion
/// limit refuses. The expected snapshots were taken from deletion detection as it ran before Phase 3, when each missing
/// External ID was resolved to a whole object, one query at a time, and every candidate was held to the end; resolving
/// candidates as summaries in batches must reach exactly the same verdicts.
/// <para>
/// The cases are the ones where the database, not the Worker, decides: an External ID held in another case ignoring case
/// (#2051), two objects holding one External ID (the first by id is resolved, as the lookup always has), two objects whose
/// External IDs differ only in case and are both missing, an object already Obsolete, one Pending Provisioning, and Number
/// and Guid anchors. Opt-in via the <c>JIM_TEST_RESET_*</c> environment variables; <c>SetUp</c> TRUNCATEs every table.
/// </para>
/// </summary>
[TestFixture]
[Category("RequiresPostgres")]
public class FullImportDeletionDetectionDatabaseTests
{
    private string _connectionString = null!;
    private int _systemId;
    private readonly Dictionary<string, int> _ids = [];
    private readonly Dictionary<Guid, string> _names = [];

    private static readonly Guid Kept = Guid.Parse("00000000-0000-0000-0000-00000000f001");
    private static readonly Guid Gone = Guid.Parse("00000000-0000-0000-0000-00000000f002");
    private static readonly Guid AlreadyObsolete = Guid.Parse("00000000-0000-0000-0000-00000000f003");
    private static readonly Guid Provisioning = Guid.Parse("00000000-0000-0000-0000-00000000f004");
    private static readonly Guid DuplicateFirst = Guid.Parse("00000000-0000-0000-0000-00000000f005");
    private static readonly Guid DuplicateSecond = Guid.Parse("00000000-0000-0000-0000-00000000f006");
    private static readonly Guid TwinFirst = Guid.Parse("00000000-0000-0000-0000-00000000f007");
    private static readonly Guid TwinSecond = Guid.Parse("00000000-0000-0000-0000-00000000f008");
    private static readonly Guid AccountKept = Guid.Parse("00000000-0000-0000-0000-00000000f101");
    private static readonly Guid AccountGone = Guid.Parse("00000000-0000-0000-0000-00000000f102");
    private static readonly Guid DeviceKept = Guid.Parse("00000000-0000-0000-0000-00000000f201");
    private static readonly Guid DeviceGone = Guid.Parse("00000000-0000-0000-0000-00000000f202");

    private static readonly Guid DeviceKeptAnchor = Guid.Parse("6d1d4a2c-0000-4000-8000-00000000a001");
    private static readonly Guid DeviceGoneAnchor = Guid.Parse("6d1d4a2c-0000-4000-8000-00000000a002");

    private const string ExpectedApplied = """
        Objects:
          accountGone: Obsolete
          accountKept: Normal
          alreadyObsolete: Obsolete
          deviceGone: Obsolete
          deviceKept: Normal
          duplicateFirst: Obsolete
          duplicateSecond: Normal
          gone: Obsolete
          kept: Normal
          provisioning: PendingProvisioning
          twinFirst: Obsolete
          twinSecond: Normal
        Pending Exports:
          provisioning
        Deletion items:
          accountGone
          deviceGone
          duplicateFirst
          gone
          twinFirst
        Activity:
          withheld=0 message=Import complete: 3 objects; 5 no longer found and marked as deleted
          warning=-

        """;

    private const string ExpectedRefused = """
        Objects:
          accountGone: Normal
          accountKept: Normal
          alreadyObsolete: Obsolete
          deviceGone: Normal
          deviceKept: Normal
          duplicateFirst: Normal
          duplicateSecond: Normal
          gone: Normal
          kept: Normal
          provisioning: PendingProvisioning
          twinFirst: Normal
          twinSecond: Normal
        Pending Exports:
          alreadyObsolete
          gone
          provisioning
        Deletion items:
        Activity:
          withheld=5 message=Import complete: 3 objects
          warning=Deletion detection found 5 objects (45% of 11) no longer in the Connected System, above this Run Profile's limit of 4; none were marked as deleted. Check the Connected System's scope and the connector's filters, or raise the limit, then run the Full Import again.

        """;

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
        var connectionString = PostgresTestDatabase.ConnectionStringFromEnvironment();
        if (connectionString == null)
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL Full Import deletion detection tests.");
        _connectionString = connectionString;

        TestUtilities.SetEnvironmentVariables();
        using var ctx = NewContext();
        ctx.Database.Migrate();
    }

    [SetUp]
    public async Task SetUpAsync()
    {
        await PostgresTestDatabase.ResetAsync(_connectionString);
        _ids.Clear();
        _names.Clear();
    }

    [Test]
    public async Task FullImport_ObjectsMissingFromTheImport_ResolvesAndMarksTheSameObjectsAsBeforeAsync()
    {
        await SeedAsync(maxDetectedDeletions: null);

        var activityId = await RunFullImportAsync();

        var snapshot = await SnapshotAsync(activityId);
        Assert.That(snapshot, Is.EqualTo(ExpectedApplied), "A deletion detection verdict changed. Actual:\n" + snapshot);
    }

    [Test]
    public async Task FullImport_MissingObjectsAboveTheDeletionLimit_RefusesAndChangesNothingAsBeforeAsync()
    {
        await SeedAsync(maxDetectedDeletions: 4);

        var activityId = await RunFullImportAsync();

        var snapshot = await SnapshotAsync(activityId);
        Assert.That(snapshot, Is.EqualTo(ExpectedRefused), "A deletion detection verdict changed. Actual:\n" + snapshot);
    }

    #region Harness

    private async Task SeedAsync(int? maxDetectedDeletions)
    {
        await using (var seed = NewContext())
        {
            var connectorDefinition = new ConnectorDefinition { Name = "JIM Test Connector", BuiltIn = true };
            var system = new ConnectedSystem { Name = "Directory", ConnectorDefinition = connectorDefinition };

            ConnectedSystemObjectType ObjectType(string name, string externalIdName, AttributeDataType externalIdType)
            {
                var objectType = new ConnectedSystemObjectType { Name = name, ConnectedSystem = system, Selected = true };
                objectType.Attributes.Add(new ConnectedSystemObjectTypeAttribute
                {
                    Name = externalIdName, ConnectedSystemObjectType = objectType, Type = externalIdType,
                    AttributePlurality = AttributePlurality.SingleValued, Selected = true, IsExternalId = true
                });
                objectType.Attributes.Add(new ConnectedSystemObjectTypeAttribute
                {
                    Name = "displayName", ConnectedSystemObjectType = objectType, Type = AttributeDataType.Text,
                    AttributePlurality = AttributePlurality.SingleValued, Selected = true
                });
                return objectType;
            }

            // The user's External ID ignores case, as a directory name does (#2051).
            var user = ObjectType("user", "id", AttributeDataType.Text);
            var account = ObjectType("account", "number", AttributeDataType.Number);
            var device = ObjectType("device", "deviceId", AttributeDataType.Guid);

            seed.AddRange(connectorDefinition, system, user, account, device);
            await seed.SaveChangesAsync();
            seed.ConnectedSystemRunProfiles.Add(new ConnectedSystemRunProfile
            {
                ConnectedSystemId = system.Id, Name = "Full Import", RunType = ConnectedSystemRunType.FullImport,
                MaxDetectedDeletions = maxDetectedDeletions
            });
            await seed.SaveChangesAsync();

            _systemId = system.Id;
            foreach (var objectType in new[] { user, account, device })
            {
                _ids[objectType.Name] = objectType.Id;
                foreach (var attribute in objectType.Attributes)
                    _ids[$"{objectType.Name}.{attribute.Name}"] = attribute.Id;
            }
        }

        var csos = new[]
        {
            User(Kept, "kept", "kept"),
            User(Gone, "gone", "gone"),
            User(AlreadyObsolete, "alreadyObsolete", "obsolete", ConnectedSystemObjectStatus.Obsolete),
            User(Provisioning, "provisioning", "provisioning", ConnectedSystemObjectStatus.PendingProvisioning),
            // Two objects holding one External ID: the lookup resolves the value to the first of them by id.
            User(DuplicateFirst, "duplicateFirst", "duplicate"),
            User(DuplicateSecond, "duplicateSecond", "duplicate"),
            // Two External IDs differing only in case, both missing: one value, which resolves to the first by id.
            User(TwinFirst, "twinFirst", "twin"),
            User(TwinSecond, "twinSecond", "TWIN"),
            Typed(AccountKept, "accountKept", "account", "number", value => value.IntValue = 1),
            Typed(AccountGone, "accountGone", "account", "number", value => value.IntValue = 2),
            Typed(DeviceKept, "deviceKept", "device", "deviceId", value => value.GuidValue = DeviceKeptAnchor),
            Typed(DeviceGone, "deviceGone", "device", "deviceId", value => value.GuidValue = DeviceGoneAnchor)
        };

        await using var write = NewContext();
        var sync = new PostgresDataRepository(write).Sync;
        await sync.CreateConnectedSystemObjectsAsync([.. csos]);

        // A stale Pending Export on objects deletion detection finds missing, and on the one it never looks at.
        await sync.CreatePendingExportsAsync([Export(Gone), Export(AlreadyObsolete), Export(Provisioning)]);
    }

    private ConnectedSystemObject User(Guid id, string name, string externalId, ConnectedSystemObjectStatus status = ConnectedSystemObjectStatus.Normal) =>
        Typed(id, name, "user", "id", value => value.StringValue = externalId, status);

    private ConnectedSystemObject Typed(Guid id, string name, string objectType, string externalIdName,
        Action<ConnectedSystemObjectAttributeValue> setExternalId, ConnectedSystemObjectStatus status = ConnectedSystemObjectStatus.Normal)
    {
        _names[id] = name;
        var cso = new ConnectedSystemObject
        {
            Id = id, ConnectedSystemId = _systemId, TypeId = _ids[objectType], ExternalIdAttributeId = _ids[$"{objectType}.{externalIdName}"],
            Status = status, Created = DateTime.UtcNow.AddHours(-2)
        };
        var externalIdValue = new ConnectedSystemObjectAttributeValue { Id = Guid.NewGuid(), AttributeId = _ids[$"{objectType}.{externalIdName}"] };
        setExternalId(externalIdValue);
        cso.AttributeValues.Add(externalIdValue);
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Id = Guid.NewGuid(), AttributeId = _ids[$"{objectType}.displayName"], StringValue = name });
        return cso;
    }

    private PendingExport Export(Guid csoId)
    {
        var pendingExport = new PendingExport
        {
            Id = Guid.NewGuid(), ConnectedSystemId = _systemId, ConnectedSystemObjectId = csoId, ChangeType = PendingExportChangeType.Update,
            Status = PendingExportStatus.Pending, CreatedAt = DateTime.UtcNow.AddMinutes(-5),
            AttributeValueChanges =
            [
                new PendingExportAttributeValueChange
                {
                    Id = Guid.NewGuid(), AttributeId = _ids["user.displayName"], ChangeType = PendingExportAttributeChangeType.Update,
                    Status = PendingExportAttributeChangeStatus.Pending, StringValue = "Renamed"
                }
            ]
        };
        _names[pendingExport.Id] = _names[csoId];
        return pendingExport;
    }

    /// <summary>
    /// What the directory returns: the kept user in another case, the kept account and the kept device, each as JIM holds
    /// it. Everything else JIM holds is missing.
    /// </summary>
    private static List<List<ConnectedSystemImportObject>> DirectoryPages()
    {
        static ConnectedSystemImportObjectAttribute Text(string name, string value) =>
            new() { Name = name, Type = AttributeDataType.Text, StringValues = [value] };

        return
        [
            [
                new() { ObjectType = "user", ChangeType = ObjectChangeType.NotSet, Attributes = [Text("id", "KEPT"), Text("displayName", "kept")] },
                new()
                {
                    ObjectType = "account", ChangeType = ObjectChangeType.NotSet,
                    Attributes = [new() { Name = "number", Type = AttributeDataType.Number, IntValues = [1] }, Text("displayName", "accountKept")]
                }
            ],
            [
                new()
                {
                    ObjectType = "device", ChangeType = ObjectChangeType.NotSet,
                    Attributes = [new() { Name = "deviceId", Type = AttributeDataType.Guid, GuidValues = [DeviceKeptAnchor] }, Text("displayName", "deviceKept")]
                }
            ]
        ];
    }

    private async Task<Guid> RunFullImportAsync()
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
            new PagedImportConnector(DirectoryPages()), system, runProfile, workerTask, new CancellationTokenSource());

        await processor.PerformImportAsync();
        await jim.SyncRepository.UpdateActivityAsync(activity);
        return activity.Id;
    }

    /// <summary>Every object's status, the Pending Exports left, the run's deletion items and its counters. Names stand in for ids.</summary>
    private async Task<string> SnapshotAsync(Guid activityId)
    {
        await using var ctx = NewContext();
        string Name(Guid? id) => id is { } value && _names.TryGetValue(value, out var name) ? name : "(unnamed)";

        var text = new StringBuilder();
        text.AppendLine("Objects:");
        var objects = await ctx.ConnectedSystemObjects.AsNoTracking().Where(o => o.ConnectedSystemId == _systemId)
            .Select(o => new { o.Id, o.Status }).ToListAsync();
        foreach (var cso in objects.OrderBy(o => Name(o.Id), StringComparer.Ordinal))
            text.AppendLine($"  {Name(cso.Id)}: {cso.Status}");

        text.AppendLine("Pending Exports:");
        var exports = await ctx.PendingExports.AsNoTracking().Where(p => p.ConnectedSystemId == _systemId).Select(p => p.Id).ToListAsync();
        foreach (var name in exports.Select(id => Name(id)).Order(StringComparer.Ordinal))
            text.AppendLine($"  {name}");

        text.AppendLine("Deletion items:");
        var items = await ctx.ActivityRunProfileExecutionItems.AsNoTracking()
            .Where(i => i.ActivityId == activityId && i.ObjectChangeType == ObjectChangeType.Deleted)
            .Select(i => i.ConnectedSystemObjectId).ToListAsync();
        foreach (var name in items.Select(Name).Order(StringComparer.Ordinal))
            text.AppendLine($"  {name}");

        var activity = await ctx.Activities.AsNoTracking().SingleAsync(a => a.Id == activityId);
        text.AppendLine("Activity:");
        text.AppendLine($"  withheld={activity.DetectedDeletionsWithheld} message={WithoutCountsAndThroughput(activity.Message)}");
        text.AppendLine($"  warning={(string.IsNullOrEmpty(activity.WarningMessage) ? "-" : activity.WarningMessage)}");
        return text.ToString().Replace("\r\n", "\n");
    }

    /// <summary>
    /// The completion message without its per-outcome counts (which count the objects read, not the deletions) and its
    /// throughput, which varies run to run.
    /// </summary>
    private static string WithoutCountsAndThroughput(string? message) =>
        message == null ? "-" : Regex.Replace(message, @" \(.*?\)[^;]*", string.Empty, RegexOptions.None, TimeSpan.FromSeconds(1));

    #endregion
}
