// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Application.Services;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.PostgresData;
using JIM.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NUnit.Framework;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// Real-PostgreSQL verification that the "configuration changed since the last Full Synchronisation" indicator counts net
/// difference, not edits (#2973): it compares the configuration in force when the last Full Synchronisation started with
/// the current one, read from the versioned configuration history. The "in force at" read is a version-ordered query over
/// the history, which only the real database answers faithfully. Opt-in via the same <c>JIM_TEST_RESET_*</c> environment
/// variables as the other RequiresPostgres fixtures.
/// </summary>
[TestFixture]
[Category("RequiresPostgres")]
public class ConfigurationDriftNetDifferenceDatabaseTests
{
    private static readonly byte[] HashKey = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
    private string _connectionString = null!;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        var dbName = Environment.GetEnvironmentVariable("JIM_TEST_RESET_DB");
        if (string.IsNullOrEmpty(dbName))
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL configuration drift tests.");

        var host = Environment.GetEnvironmentVariable("JIM_TEST_RESET_HOST") ?? "localhost";
        var user = Environment.GetEnvironmentVariable("JIM_TEST_RESET_USER") ?? "postgres";
        var password = Environment.GetEnvironmentVariable("JIM_TEST_RESET_PASSWORD") ?? "postgres";
        var port = Environment.GetEnvironmentVariable("JIM_TEST_RESET_PORT") ?? "5432";
        _connectionString = $"Host={host};Port={port};Database={dbName};Username={user};Password={password}";

        TestUtilities.SetEnvironmentVariables();
        using var ctx = NewContext(QueryTrackingBehavior.NoTracking);
        ctx.Database.Migrate();
    }

    [SetUp]
    public async Task SetUpAsync()
    {
        await PostgresTestDatabase.ResetAsync(_connectionString);
    }

    private JimDbContext NewContext(QueryTrackingBehavior tracking = QueryTrackingBehavior.TrackAll) => new(new DbContextOptionsBuilder<JimDbContext>()
        .UseNpgsql(_connectionString)
        .UseQueryTrackingBehavior(tracking)
        .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
        .Options);

    private sealed record Seeded(int SystemId, int MetaverseObjectTypeId, DateTime FullSynchronisationStart);

    /// <summary>
    /// A Connected System whose import Synchronisation Rule targets a Metaverse Object Type (so the type's changes are in the
    /// system's scope), the type's first recorded version two hours ago, and a Full Synchronisation an hour ago.
    /// </summary>
    private async Task<Seeded> SeedAsync()
    {
        await using var ctx = NewContext();

        var definition = new ConnectorDefinition { Name = "net-definition", BuiltIn = true };
        ctx.ConnectorDefinitions.Add(definition);
        await ctx.SaveChangesAsync();

        var system = new ConnectedSystem { Name = "net-system", ConnectorDefinitionId = definition.Id };
        ctx.ConnectedSystems.Add(system);
        await ctx.SaveChangesAsync();

        var userType = new ConnectedSystemObjectType { ConnectedSystemId = system.Id, Name = "user", Selected = true };
        ctx.ConnectedSystemObjectTypes.Add(userType);
        var metaverseObjectType = new MetaverseObjectType { Name = "NetPerson", PluralName = "NetPeople", DeletionGracePeriod = TimeSpan.FromDays(7) };
        ctx.MetaverseObjectTypes.Add(metaverseObjectType);
        await ctx.SaveChangesAsync();

        ctx.SyncRules.Add(new SyncRule
        {
            Name = "net-import", ConnectedSystemId = system.Id, ConnectedSystemObjectTypeId = userType.Id,
            MetaverseObjectTypeId = metaverseObjectType.Id, Direction = SyncRuleDirection.Import
        });

        var fullSynchronisationStart = DateTime.UtcNow.AddHours(-1);
        ctx.Activities.Add(new Activity
        {
            Id = Guid.NewGuid(),
            TargetName = "net-full-sync",
            TargetType = ActivityTargetType.ConnectedSystemRunProfile,
            TargetOperationType = ActivityTargetOperationType.Execute,
            ConnectedSystemRunType = ConnectedSystemRunType.FullSynchronisation,
            ConnectedSystemId = system.Id,
            Status = ActivityStatus.Complete,
            InitiatedByType = ActivityInitiatorType.System,
            InitiatedByName = "System",
            Created = fullSynchronisationStart,
            Executed = fullSynchronisationStart
        });
        await ctx.SaveChangesAsync();

        await RecordObjectTypeVersionAsync(metaverseObjectType.Id, version: 1, gracePeriod: TimeSpan.FromDays(7), DateTime.UtcNow.AddHours(-2),
            ConfigurationChangeClass.NotClassified);

        return new Seeded(system.Id, metaverseObjectType.Id, fullSynchronisationStart);
    }

    /// <summary>
    /// A versioned change to the Metaverse Object Type, as capture records it: the snapshot of the type after the save.
    /// </summary>
    private async Task RecordObjectTypeVersionAsync(int objectTypeId, int version, TimeSpan gracePeriod, DateTime when,
        ConfigurationChangeClass changeClass = ConfigurationChangeClass.Destructive, bool withAttribute = false)
    {
        var type = new MetaverseObjectType { Id = objectTypeId, Name = "NetPerson", PluralName = "NetPeople", DeletionGracePeriod = gracePeriod };
        if (withAttribute)
            type.Attributes.Add(new MetaverseAttribute { Id = 999, Name = "Employee Type" });

        string snapshot;
        await using (var snapshotContext = NewContext(QueryTrackingBehavior.NoTracking))
        {
            using var jim = new JimApplication(new PostgresDataRepository(snapshotContext));
            snapshot = ConfigurationSnapshotService.Serialise(jim.ConfigurationSnapshots.CreateSnapshot(type, HashKey));
        }

        await using var ctx = NewContext();
        ctx.Activities.Add(new Activity
        {
            Id = Guid.NewGuid(),
            TargetName = "NetPerson",
            TargetType = ActivityTargetType.MetaverseObjectType,
            TargetOperationType = version == 1 ? ActivityTargetOperationType.Create : ActivityTargetOperationType.Update,
            MetaverseObjectTypeId = objectTypeId,
            ConfigurationChangeClass = changeClass,
            ConfigurationChangeVersion = version,
            ConfigurationChangeSnapshot = snapshot,
            Status = ActivityStatus.Complete,
            InitiatedByType = ActivityInitiatorType.System,
            InitiatedByName = "System",
            Created = when
        });
        await ctx.SaveChangesAsync();
    }

    private async Task<(bool HasPendingChanges, int ChangeCount)> DriftAsync(int systemId)
    {
        await using var ctx = NewContext(QueryTrackingBehavior.NoTracking);
        using var jim = new JimApplication(new PostgresDataRepository(ctx));
        var status = await jim.ConfigurationDrift.GetConnectedSystemDriftAsync(systemId);
        return (status.HasPendingChanges, status.ChangeCount);
    }

    [Test]
    public async Task GetConfigurationChangeSnapshotInForceAsync_VersionsEitherSide_ReturnsTheHighestBeforeThePointAsync()
    {
        var s = await SeedAsync();
        await RecordObjectTypeVersionAsync(s.MetaverseObjectTypeId, version: 2, TimeSpan.FromDays(3), s.FullSynchronisationStart.AddMinutes(-30));
        await RecordObjectTypeVersionAsync(s.MetaverseObjectTypeId, version: 3, TimeSpan.Zero, s.FullSynchronisationStart.AddMinutes(10));

        await using var ctx = NewContext(QueryTrackingBehavior.NoTracking);
        var json = await new PostgresDataRepository(ctx).Activity
            .GetConfigurationChangeSnapshotInForceAsync(ActivityTargetType.MetaverseObjectType, s.MetaverseObjectTypeId, s.FullSynchronisationStart);

        var snapshot = ConfigurationSnapshotService.Deserialise(json);
        var gracePeriod = snapshot!.Root!.Children!.Single(c => c.Key == "deletionGracePeriod").Value;
        Assert.That(gracePeriod, Is.EqualTo(TimeSpan.FromDays(3).ToString("c")), "version 2, the last recorded before the Full Synchronisation");
    }

    [Test]
    public async Task GetConfigurationChangeSnapshotInForceAsync_NoVersionBeforeThePoint_ReturnsNullAsync()
    {
        var s = await SeedAsync();

        await using var ctx = NewContext(QueryTrackingBehavior.NoTracking);
        var json = await new PostgresDataRepository(ctx).Activity
            .GetConfigurationChangeSnapshotInForceAsync(ActivityTargetType.MetaverseObjectType, s.MetaverseObjectTypeId, DateTime.UtcNow.AddHours(-3));

        Assert.That(json, Is.Null);
    }

    [Test]
    public async Task GetConfigurationChangeSnapshotInForceAsync_ServiceSetting_ReadsByKeyAsync()
    {
        var at = DateTime.UtcNow;
        await using (var seed = NewContext())
        {
            seed.Activities.Add(new Activity
            {
                Id = Guid.NewGuid(),
                TargetName = "net-setting",
                TargetType = ActivityTargetType.ServiceSetting,
                TargetOperationType = ActivityTargetOperationType.Update,
                ServiceSettingKey = "Net.Setting",
                ConfigurationChangeVersion = 1,
                ConfigurationChangeSnapshot = "{\"objectType\":\"ServiceSetting\"}",
                Status = ActivityStatus.Complete,
                InitiatedByType = ActivityInitiatorType.System,
                InitiatedByName = "System",
                Created = at.AddMinutes(-5)
            });
            await seed.SaveChangesAsync();
        }

        await using var ctx = NewContext(QueryTrackingBehavior.NoTracking);
        var repository = new PostgresDataRepository(ctx).Activity;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(await repository.GetConfigurationChangeSnapshotInForceAsync(ActivityTargetType.ServiceSetting, "Net.Setting", at), Is.Not.Null);
            Assert.That(await repository.GetConfigurationChangeSnapshotInForceAsync(ActivityTargetType.ServiceSetting, "Other.Setting", at), Is.Null);
        }
    }

    [Test]
    public async Task GetConfigurationChangeImpactsSinceAsync_VersionedChange_CarriesItsVersionAsync()
    {
        var s = await SeedAsync();
        await RecordObjectTypeVersionAsync(s.MetaverseObjectTypeId, version: 2, TimeSpan.Zero, s.FullSynchronisationStart.AddMinutes(10));

        await using var ctx = NewContext(QueryTrackingBehavior.NoTracking);
        var impacts = await new PostgresDataRepository(ctx).Activity
            .GetConfigurationChangeImpactsSinceAsync(s.FullSynchronisationStart, ConfigurationChangeClass.SyncAffecting);

        Assert.That(impacts.Single().Version, Is.EqualTo(2));
    }

    [Test]
    public async Task GetConnectedSystemDriftAsync_ObjectTypeChangeUndoneSinceTheFullSynchronisation_IsNotPendingAsync()
    {
        // Scenario 001's Tests 4b and 4c: the grace period zeroed for a test and restored after it.
        var s = await SeedAsync();
        await RecordObjectTypeVersionAsync(s.MetaverseObjectTypeId, version: 2, TimeSpan.Zero, s.FullSynchronisationStart.AddMinutes(10));
        await RecordObjectTypeVersionAsync(s.MetaverseObjectTypeId, version: 3, TimeSpan.FromDays(7), s.FullSynchronisationStart.AddMinutes(20));

        var drift = await DriftAsync(s.SystemId);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(drift.HasPendingChanges, Is.False);
            Assert.That(drift.ChangeCount, Is.EqualTo(0));
        }
    }

    [Test]
    public async Task GetConnectedSystemDriftAsync_ObjectTypeChangeAFullSynchronisationApplies_IsPendingAsync()
    {
        var s = await SeedAsync();
        await RecordObjectTypeVersionAsync(s.MetaverseObjectTypeId, version: 2, TimeSpan.FromDays(7), s.FullSynchronisationStart.AddMinutes(10),
            ConfigurationChangeClass.SyncAffecting, withAttribute: true);

        var drift = await DriftAsync(s.SystemId);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(drift.HasPendingChanges, Is.True);
            Assert.That(drift.ChangeCount, Is.EqualTo(1));
        }
    }
}
