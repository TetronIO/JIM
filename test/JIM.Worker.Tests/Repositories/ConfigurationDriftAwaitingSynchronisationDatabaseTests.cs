// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
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
/// Real-PostgreSQL verification of how the "configuration changed since the last Full Synchronisation" indicator treats
/// changes recorded as awaiting synchronisation (#2615). Once a Delta Synchronisation applies an import rule change, to
/// count it as drift would tell the administrator to run a Full Synchronisation for something the next ordinary run
/// does. The indicator therefore leaves those changes out, reports them as waiting instead, and keeps raising for what
/// JIM does not apply by itself: an export Attribute Flow edit, say. The exclusion is a join against the recorded
/// changes, which only the real database can answer. Opt-in via the same <c>JIM_TEST_RESET_*</c> environment variables
/// as the other RequiresPostgres fixtures.
/// </summary>
[TestFixture]
[Category("RequiresPostgres")]
public class ConfigurationDriftAwaitingSynchronisationDatabaseTests
{
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

    private sealed record Seeded(
        int SystemId, int OtherSystemId, int UserTypeId, int ContactTypeId, int OtherSystemTypeId, int ImportRuleId, int ExportRuleId,
        DateTime FullSynchronisationStart);

    /// <summary>
    /// A Connected System (User and Contact types, an import rule on User and an export rule on its Metaverse type) whose
    /// last Full Synchronisation completed an hour ago, and a second system with a User type of its own.
    /// </summary>
    private async Task<Seeded> SeedAsync()
    {
        await using var ctx = NewContext();

        var definition = new ConnectorDefinition { Name = "drift-definition", BuiltIn = true };
        ctx.ConnectorDefinitions.Add(definition);
        await ctx.SaveChangesAsync();

        var system = new ConnectedSystem { Name = "drift-system", ConnectorDefinitionId = definition.Id };
        var otherSystem = new ConnectedSystem { Name = "drift-other-system", ConnectorDefinitionId = definition.Id };
        ctx.ConnectedSystems.AddRange(system, otherSystem);
        await ctx.SaveChangesAsync();

        var userType = new ConnectedSystemObjectType { ConnectedSystemId = system.Id, Name = "user", Selected = true };
        var contactType = new ConnectedSystemObjectType { ConnectedSystemId = system.Id, Name = "contact", Selected = true };
        var otherType = new ConnectedSystemObjectType { ConnectedSystemId = otherSystem.Id, Name = "account", Selected = true };
        ctx.ConnectedSystemObjectTypes.AddRange(userType, contactType, otherType);

        var metaverseObjectType = new MetaverseObjectType { Name = "DriftPerson", PluralName = "DriftPeople" };
        ctx.MetaverseObjectTypes.Add(metaverseObjectType);
        await ctx.SaveChangesAsync();

        var importRule = new SyncRule
        {
            Name = "drift-import", ConnectedSystemId = system.Id, ConnectedSystemObjectTypeId = userType.Id,
            MetaverseObjectTypeId = metaverseObjectType.Id, Direction = SyncRuleDirection.Import
        };
        var exportRule = new SyncRule
        {
            Name = "drift-export", ConnectedSystemId = system.Id, ConnectedSystemObjectTypeId = userType.Id,
            MetaverseObjectTypeId = metaverseObjectType.Id, Direction = SyncRuleDirection.Export
        };
        ctx.SyncRules.AddRange(importRule, exportRule);

        var fullSynchronisationStart = DateTime.UtcNow.AddHours(-1);
        ctx.Activities.Add(new Activity
        {
            Id = Guid.NewGuid(),
            TargetName = "drift-full-sync",
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

        return new Seeded(system.Id, otherSystem.Id, userType.Id, contactType.Id, otherType.Id, importRule.Id, exportRule.Id, fullSynchronisationStart);
    }

    /// <summary>
    /// A Sync-affecting change to a Synchronisation Rule, saved after the last Full Synchronisation.
    /// </summary>
    private async Task<Activity> SaveChangeToRuleAsync(int syncRuleId, string name)
    {
        await using var ctx = NewContext();
        var activity = new Activity
        {
            Id = Guid.NewGuid(),
            TargetName = name,
            TargetType = ActivityTargetType.SynchronisationRule,
            TargetOperationType = ActivityTargetOperationType.Update,
            SyncRuleId = syncRuleId,
            ConfigurationChangeClass = ConfigurationChangeClass.SyncAffecting,
            Status = ActivityStatus.Complete,
            InitiatedByType = ActivityInitiatorType.System,
            InitiatedByName = "System",
            Created = DateTime.UtcNow.AddMinutes(-30)
        };
        ctx.Activities.Add(activity);
        await ctx.SaveChangesAsync();
        return activity;
    }

    private async Task<ConfigurationChangeSynchronisation> RecordAsync(int systemId, int typeId, Guid? activityId, int? syncRuleId, DateTime? requestedAt = null)
    {
        await using var ctx = NewContext(QueryTrackingBehavior.NoTracking);
        var change = new ConfigurationChangeSynchronisation
        {
            ConnectedSystemId = systemId,
            ConnectedSystemObjectTypeId = typeId,
            ConfigurationChangeActivityId = activityId,
            SyncRuleId = syncRuleId,
            Reason = "drift-change",
            RequestedAt = requestedAt ?? DateTime.UtcNow.AddMinutes(-30)
        };
        await new PostgresDataRepository(ctx).ConnectedSystems.CreateConfigurationChangeSynchronisationAsync(change);
        return change;
    }

    private async Task ApplyAsync(ConfigurationChangeSynchronisation change)
    {
        await using var ctx = NewContext();
        var stored = await ctx.ConfigurationChangeSynchronisations.SingleAsync(c => c.Id == change.Id);
        stored.AppliedAt = DateTime.UtcNow;
        await ctx.SaveChangesAsync();
    }

    private async Task<ConfigurationDriftStatusResult> DriftAsync(int systemId)
    {
        await using var ctx = NewContext(QueryTrackingBehavior.NoTracking);
        using var jim = new JimApplication(new PostgresDataRepository(ctx));
        var status = await jim.ConfigurationDrift.GetConnectedSystemDriftAsync(systemId);
        return new ConfigurationDriftStatusResult(status.HasPendingChanges, status.ChangeCount,
            status.AwaitingSynchronisation.Select(a => (a.ObjectTypeName, a.ChangeCount)).ToList());
    }

    private sealed record ConfigurationDriftStatusResult(bool HasPendingChanges, int ChangeCount, List<(string ObjectTypeName, int ChangeCount)> Awaiting);

    [Test]
    public async Task GetConfigurationChangeImpactsSinceAsync_ChangeAwaitingSynchronisation_IsLeftOutAsync()
    {
        var s = await SeedAsync();
        var importChange = await SaveChangeToRuleAsync(s.ImportRuleId, "drift-import-scope");
        await SaveChangeToRuleAsync(s.ExportRuleId, "drift-export-flow");
        await RecordAsync(s.SystemId, s.UserTypeId, importChange.Id, s.ImportRuleId);

        await using var ctx = NewContext(QueryTrackingBehavior.NoTracking);
        var impacts = await new PostgresDataRepository(ctx).Activity
            .GetConfigurationChangeImpactsSinceAsync(s.FullSynchronisationStart, ConfigurationChangeClass.SyncAffecting);

        Assert.That(impacts.Select(i => i.SyncRuleId), Is.EqualTo(new int?[] { s.ExportRuleId }),
            "the import change is carried by the next synchronisation; the export change is not, so it still counts");
    }

    [Test]
    public async Task GetConfigurationChangeImpactsSinceAsync_ChangeAlreadyApplied_IsLeftOutAsync()
    {
        var s = await SeedAsync();
        var importChange = await SaveChangeToRuleAsync(s.ImportRuleId, "drift-import-scope");
        var recorded = await RecordAsync(s.SystemId, s.UserTypeId, importChange.Id, s.ImportRuleId);
        await ApplyAsync(recorded);

        await using var ctx = NewContext(QueryTrackingBehavior.NoTracking);
        var impacts = await new PostgresDataRepository(ctx).Activity
            .GetConfigurationChangeImpactsSinceAsync(s.FullSynchronisationStart, ConfigurationChangeClass.SyncAffecting);

        Assert.That(impacts, Is.Empty, "an applied change is done: not drift, and not waiting either");
    }

    [Test]
    public async Task GetConfigurationChangesAwaitingSynchronisationAsync_GroupsByObjectTypeAndLeavesOutAppliedAndOtherSystemsAsync()
    {
        var s = await SeedAsync();
        var earliest = DateTime.UtcNow.AddHours(-3);
        await RecordAsync(s.SystemId, s.UserTypeId, null, s.ImportRuleId, earliest);
        await RecordAsync(s.SystemId, s.UserTypeId, null, s.ImportRuleId, earliest.AddHours(1));
        await RecordAsync(s.SystemId, s.UserTypeId, null, null, earliest.AddHours(2));
        await RecordAsync(s.SystemId, s.ContactTypeId, null, null, earliest.AddHours(1));
        await ApplyAsync(await RecordAsync(s.SystemId, s.ContactTypeId, null, null, earliest.AddHours(-1)));
        await RecordAsync(s.OtherSystemId, s.OtherSystemTypeId, null, null, earliest);

        await using var ctx = NewContext(QueryTrackingBehavior.NoTracking);
        var awaiting = await new PostgresDataRepository(ctx).ConnectedSystems.GetConfigurationChangesAwaitingSynchronisationAsync([s.SystemId]);

        Assert.That(awaiting, Has.Count.EqualTo(2), "one entry per Object Type of the requested system");
        var user = awaiting.Single(a => a.ConnectedSystemObjectTypeId == s.UserTypeId);
        var contact = awaiting.Single(a => a.ConnectedSystemObjectTypeId == s.ContactTypeId);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(user.ConnectedSystemId, Is.EqualTo(s.SystemId));
            Assert.That(user.ObjectTypeName, Is.EqualTo("user"));
            Assert.That(user.ChangeCount, Is.EqualTo(3));
            Assert.That(user.EarliestRequestedAt, Is.EqualTo(earliest).Within(TimeSpan.FromMilliseconds(5)));
            Assert.That(contact.ObjectTypeName, Is.EqualTo("contact"));
            Assert.That(contact.ChangeCount, Is.EqualTo(1), "the applied change is no longer waiting");
            Assert.That(contact.EarliestRequestedAt, Is.EqualTo(earliest.AddHours(1)).Within(TimeSpan.FromMilliseconds(5)),
                "and its time does not count as the earliest");
        }
    }

    [Test]
    public async Task GetConfigurationChangesAwaitingSynchronisationAsync_SeveralSystems_ReturnsEachOnesTypesInOneReadAsync()
    {
        var s = await SeedAsync();
        await RecordAsync(s.SystemId, s.UserTypeId, null, null);
        await RecordAsync(s.OtherSystemId, s.OtherSystemTypeId, null, null);

        await using var ctx = NewContext(QueryTrackingBehavior.NoTracking);
        var awaiting = await new PostgresDataRepository(ctx).ConnectedSystems.GetConfigurationChangesAwaitingSynchronisationAsync([s.SystemId, s.OtherSystemId]);

        Assert.That(awaiting.Select(a => (a.ConnectedSystemId, a.ObjectTypeName)),
            Is.EquivalentTo(new[] { (s.SystemId, "user"), (s.OtherSystemId, "account") }));
    }

    [Test]
    public async Task GetConnectedSystemDriftAsync_ImportScopeChangeSavedAfterTheLastFullSynchronisation_IsWaitingAndNotDriftAsync()
    {
        var s = await SeedAsync();
        var importChange = await SaveChangeToRuleAsync(s.ImportRuleId, "drift-import-scope");
        await RecordAsync(s.SystemId, s.UserTypeId, importChange.Id, s.ImportRuleId);

        var drift = await DriftAsync(s.SystemId);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(drift.HasPendingChanges, Is.False, "a Delta Synchronisation applies it, so no Full Synchronisation is called for");
            Assert.That(drift.Awaiting, Is.EqualTo(new[] { ("user", 1) }), "and it is shown as waiting for the system's next synchronisation");
        }
    }

    [Test]
    public async Task GetConnectedSystemDriftAsync_ImportScopeChangeOnceApplied_IsNeitherWaitingNorDriftAsync()
    {
        var s = await SeedAsync();
        var importChange = await SaveChangeToRuleAsync(s.ImportRuleId, "drift-import-scope");
        await ApplyAsync(await RecordAsync(s.SystemId, s.UserTypeId, importChange.Id, s.ImportRuleId));

        var drift = await DriftAsync(s.SystemId);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(drift.HasPendingChanges, Is.False);
            Assert.That(drift.Awaiting, Is.Empty);
        }
    }

    [Test]
    public async Task GetConnectedSystemDriftAsync_ExportAttributeFlowChangeSavedAfterTheLastFullSynchronisation_StillRaisesTheIndicatorAsync()
    {
        // Export follows the import types' synchronisation only for objects that synchronisation reprocesses; JIM does
        // not apply an export Attribute Flow edit by itself, so it is still drift.
        var s = await SeedAsync();
        await SaveChangeToRuleAsync(s.ExportRuleId, "drift-export-flow");

        var drift = await DriftAsync(s.SystemId);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(drift.HasPendingChanges, Is.True);
            Assert.That(drift.ChangeCount, Is.EqualTo(1));
            Assert.That(drift.Awaiting, Is.Empty);
        }
    }

    [Test]
    public async Task GetConnectedSystemDriftAsync_ImportAndExportChanges_CountsOnlyTheExportChangeAsync()
    {
        var s = await SeedAsync();
        var importChange = await SaveChangeToRuleAsync(s.ImportRuleId, "drift-import-scope");
        await SaveChangeToRuleAsync(s.ExportRuleId, "drift-export-flow");
        await RecordAsync(s.SystemId, s.UserTypeId, importChange.Id, s.ImportRuleId);

        var drift = await DriftAsync(s.SystemId);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(drift.HasPendingChanges, Is.True, "the export change still needs a Full Synchronisation");
            Assert.That(drift.ChangeCount, Is.EqualTo(1), "and the import change is not counted with it");
            Assert.That(drift.Awaiting, Is.EqualTo(new[] { ("user", 1) }));
        }
    }
}
