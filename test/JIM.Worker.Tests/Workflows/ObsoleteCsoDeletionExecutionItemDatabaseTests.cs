// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Application.Servers;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using JIM.PostgresData;
using JIM.TestSupport;
using JIM.Worker.Processors;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using NpgsqlTypes;
using NUnit.Framework;

namespace JIM.Worker.Tests.Workflows;

/// <summary>
/// Real-PostgreSQL synchronisation runs that delete a Connected System Object which left its Connected System (#2807).
/// The object's execution item and its Deleted change record are written in the delete's own transaction; the page's
/// execution item flush, later, writes the rest of that item without writing it or its record a second time. In
/// between, the Metaverse Object deletion the departure triggered hangs its deprovisioning consequences on the very
/// same item, so the flush must still write those.
/// </summary>
/// <remarks>
/// The in-memory repository enforces no keys, so it cannot show an item written twice; this runs the processors against
/// the database. Opt-in via the <c>JIM_TEST_RESET_*</c> environment variables; ignored when <c>JIM_TEST_RESET_DB</c> is
/// absent.
/// </remarks>
[TestFixture]
[Category("RequiresPostgres")]
public class ObsoleteCsoDeletionExecutionItemDatabaseTests
{
    private static readonly Guid HrCsoId = Guid.Parse("00000000-0000-0000-0000-0000000000a1");
    private static readonly Guid DirectoryCsoId = Guid.Parse("00000000-0000-0000-0000-0000000000b1");

    private string _connectionString = null!;
    private int _systemId;
    private int _typeId;
    private int _externalIdAttributeId;
    private int _displayNameAttributeId;
    private int _directorySystemId;
    private int _directoryTypeId;
    private int _directoryExternalIdAttributeId;

    private JimDbContext NewContext(bool trackAll = false)
    {
        var builder = new DbContextOptionsBuilder<JimDbContext>()
            .UseNpgsql(_connectionString)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning));
        // The Worker tracks by default (src/JIM.Worker/Program.cs); a synchronisation run must too.
        if (trackAll)
            builder.UseQueryTrackingBehavior(QueryTrackingBehavior.TrackAll);
        return new JimDbContext(builder.Options);
    }

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        var connectionString = PostgresTestDatabase.ConnectionStringFromEnvironment();
        if (connectionString == null)
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL obsolete Connected System Object deletion tests.");
        _connectionString = connectionString;

        TestUtilities.SetEnvironmentVariables();
        using var ctx = NewContext();
        ctx.Database.Migrate();
    }

    [SetUp]
    public async Task SetUpAsync() => await PostgresTestDatabase.ResetAsync(_connectionString);

    /// <summary>
    /// The departed object's item holds its whole story, written once: Disconnected, the object's deletion, the
    /// Metaverse Object's deletion and the deprovisioning that caused in the other system, with the object's deletion
    /// record belonging to it.
    /// </summary>
    [TestCase(ConnectedSystemRunType.FullSynchronisation)]
    [TestCase(ConnectedSystemRunType.DeltaSynchronisation)]
    public async Task Synchronisation_JoinedObjectLeavesAndItsMvoIsDeleted_WritesItsItemAndRecordOnceWithEveryOutcomeAsync(ConnectedSystemRunType runType)
    {
        await SeedConfigurationAsync();
        await CreateHrObjectAsync();
        await RunSynchronisationAsync(ConnectedSystemRunType.FullSynchronisation);
        var mvoId = await ReadJoinedMetaverseObjectIdAsync();
        await JoinDirectoryObjectAsync(mvoId);

        await MarkHrObjectObsoleteAsync();
        var activity = await RunSynchronisationAsync(runType);

        await using var ctx = NewContext();
        var items = await ctx.ActivityRunProfileExecutionItems.AsNoTracking()
            .Where(i => i.ActivityId == activity.Id && i.ConnectedSystemObjectId == HrCsoId)
            .ToListAsync();
        // Disconnected is the item's change type: the departure broke the join, and the deletion is its consequence.
        Assert.That(items.Select(i => i.ObjectChangeType), Is.EqualTo(new[] { ObjectChangeType.Disconnected }),
            "one execution item reports the departed object's disconnection and deletion");
        var item = items[0];
        var outcomeTypes = await ctx.ActivityRunProfileExecutionItemSyncOutcomes.AsNoTracking()
            .Where(o => o.ActivityRunProfileExecutionItemId == item.Id)
            .Select(o => o.OutcomeType)
            .ToListAsync();
        var records = await ctx.ConnectedSystemObjectChanges.AsNoTracking()
            .Where(c => c.DeletedConnectedSystemObjectId == HrCsoId && c.ChangeType == ObjectChangeType.Deleted)
            .ToListAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(await ctx.ConnectedSystemObjects.AnyAsync(c => c.Id == HrCsoId), Is.False, "the departed object is deleted");
            Assert.That(await ctx.MetaverseObjects.AnyAsync(m => m.Id == mvoId), Is.False, "precondition: its departure deleted the Metaverse Object");
            Assert.That(item.Id.Version, Is.EqualTo(7), "the item's id is time-ordered (#2807)");
            Assert.That(outcomeTypes, Is.SupersetOf(new[]
            {
                ActivityRunProfileExecutionItemSyncOutcomeType.Disconnected,
                ActivityRunProfileExecutionItemSyncOutcomeType.CsoDeleted,
                ActivityRunProfileExecutionItemSyncOutcomeType.MvoDeleted,
                ActivityRunProfileExecutionItemSyncOutcomeType.DeprovisionQueued
            }), "the item keeps the outcomes hung on it after the delete wrote it, the deprovisioning included");
            Assert.That(item.OutcomeSummary, Does.Contain(nameof(ActivityRunProfileExecutionItemSyncOutcomeType.DeprovisionQueued)),
                "the item's summary counts the outcomes added after the delete wrote it");
            Assert.That(records, Has.Count.EqualTo(1), "one deletion record, written once");
            Assert.That(records.Select(r => r.ActivityRunProfileExecutionItemId), Is.EqualTo(new Guid?[] { item.Id }),
                "the deletion record belongs to the item");
        }
    }

    #region Harness

    /// <summary>
    /// HR projects each person and is the authoritative source whose departure deletes the Metaverse Object at once; the
    /// Directory system holds an export Synchronisation Rule that deletes a deleted person's account. Connected System
    /// Object change tracking is on, so each deletion is recorded.
    /// </summary>
    private async Task SeedConfigurationAsync()
    {
        await using var seed = NewContext();

        foreach (var (key, valueType, value) in new[]
                 {
                     (Constants.SettingKeys.SyncPageSize, ServiceSettingValueType.Integer, "100"),
                     ("ChangeTracking.CsoChanges.Enabled", ServiceSettingValueType.Boolean, "true"),
                     ("ChangeTracking.MvoChanges.Enabled", ServiceSettingValueType.Boolean, "true")
                 })
        {
            seed.ServiceSettingItems.Add(new ServiceSetting
            {
                Key = key, DisplayName = key, Category = ServiceSettingCategory.Synchronisation,
                ValueType = valueType, DefaultValue = value, Value = value
            });
        }

        var connectorDefinition = new ConnectorDefinition { Name = "JIM Test Connector", BuiltIn = true };
        var system = new ConnectedSystem
        {
            Name = "HR", ConnectorDefinition = connectorDefinition, ObjectMatchingRuleMode = ObjectMatchingRuleMode.SyncRule
        };
        var type = new ConnectedSystemObjectType { Name = "person", ConnectedSystem = system, Selected = true };
        var externalId = TextAttribute(type, "employeeId", isExternalId: true);
        var displayName = TextAttribute(type, "displayName");
        type.Attributes.Add(externalId);
        type.Attributes.Add(displayName);

        var directory = new ConnectedSystem
        {
            Name = "Directory", ConnectorDefinition = connectorDefinition, ObjectMatchingRuleMode = ObjectMatchingRuleMode.SyncRule
        };
        var directoryType = new ConnectedSystemObjectType { Name = "account", ConnectedSystem = directory, Selected = true };
        var directoryExternalId = TextAttribute(directoryType, "accountId", isExternalId: true);
        directoryType.Attributes.Add(directoryExternalId);

        var mvType = new MetaverseObjectType
        {
            Name = "Person", PluralName = "People", DeletionRule = MetaverseObjectDeletionRule.WhenAuthoritativeSourceDisconnected
        };
        var mvDisplayName = new MetaverseAttribute
        {
            Name = "Display Name", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.SingleValued,
            MetaverseObjectTypes = [mvType]
        };

        seed.AddRange(connectorDefinition, system, type, directory, directoryType, mvType, mvDisplayName);
        await seed.SaveChangesAsync();
        mvType.DeletionTriggerConnectedSystemIds = [system.Id];
        seed.ConnectedSystemRunProfiles.Add(new ConnectedSystemRunProfile
            { ConnectedSystemId = system.Id, Name = "Full Synchronisation", RunType = ConnectedSystemRunType.FullSynchronisation });
        seed.ConnectedSystemRunProfiles.Add(new ConnectedSystemRunProfile
            { ConnectedSystemId = system.Id, Name = "Delta Synchronisation", RunType = ConnectedSystemRunType.DeltaSynchronisation });

        var rule = new SyncRule
        {
            Name = "HR Import", ConnectedSystem = system, Direction = SyncRuleDirection.Import, Enabled = true,
            ConnectedSystemObjectType = type, MetaverseObjectType = mvType, ProjectToMetaverse = true,
            InboundOutOfScopeAction = InboundOutOfScopeAction.Disconnect
        };
        rule.AttributeFlowRules.Add(new SyncRuleMapping
        {
            SyncRule = rule, TargetMetaverseAttribute = mvDisplayName,
            Sources = { new SyncRuleMappingSource { Order = 0, ConnectedSystemAttribute = displayName } }
        });
        seed.SyncRules.Add(rule);
        seed.SyncRules.Add(new SyncRule
        {
            Name = "Directory Export", ConnectedSystem = directory, Direction = SyncRuleDirection.Export, Enabled = true,
            ConnectedSystemObjectType = directoryType, MetaverseObjectType = mvType, OutboundDeprovisionAction = OutboundDeprovisionAction.Delete
        });
        await seed.SaveChangesAsync();

        _systemId = system.Id;
        _typeId = type.Id;
        _externalIdAttributeId = externalId.Id;
        _displayNameAttributeId = displayName.Id;
        _directorySystemId = directory.Id;
        _directoryTypeId = directoryType.Id;
        _directoryExternalIdAttributeId = directoryExternalId.Id;
    }

    private static ConnectedSystemObjectTypeAttribute TextAttribute(ConnectedSystemObjectType type, string name, bool isExternalId = false) => new()
    {
        Name = name, ConnectedSystemObjectType = type, Type = AttributeDataType.Text,
        AttributePlurality = AttributePlurality.SingleValued, Selected = true, IsExternalId = isExternalId
    };

    /// <summary>The HR object, created through the import's own create writer.</summary>
    private async Task CreateHrObjectAsync()
    {
        await using var ctx = NewContext();
        var cso = new ConnectedSystemObject
        {
            Id = HrCsoId, ConnectedSystemId = _systemId, TypeId = _typeId, ExternalIdAttributeId = _externalIdAttributeId,
            Status = ConnectedSystemObjectStatus.Normal, Created = DateTime.UtcNow.AddHours(-2)
        };
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Id = Guid.NewGuid(), AttributeId = _externalIdAttributeId, StringValue = "E1" });
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Id = Guid.NewGuid(), AttributeId = _displayNameAttributeId, StringValue = "Jane Leaver" });
        await new PostgresDataRepository(ctx).Sync.CreateConnectedSystemObjectsAsync([cso]);
    }

    /// <summary>An account in the Directory system, joined to the Metaverse Object as a second connector.</summary>
    private async Task JoinDirectoryObjectAsync(Guid mvoId)
    {
        await using var ctx = NewContext();
        var cso = new ConnectedSystemObject
        {
            Id = DirectoryCsoId, ConnectedSystemId = _directorySystemId, TypeId = _directoryTypeId,
            ExternalIdAttributeId = _directoryExternalIdAttributeId, Status = ConnectedSystemObjectStatus.Normal,
            Created = DateTime.UtcNow.AddHours(-2), MetaverseObjectId = mvoId
        };
        cso.RecordJoin(ConnectedSystemObjectJoinMethod.Provisioning, null, DateTime.UtcNow);
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Id = Guid.NewGuid(), AttributeId = _directoryExternalIdAttributeId, StringValue = "A1" });
        await new PostgresDataRepository(ctx).Sync.CreateConnectedSystemObjectsAsync([cso]);
    }

    /// <summary>An import finding the HR object gone: obsolete, and pending synchronisation.</summary>
    private async Task MarkHrObjectObsoleteAsync()
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            UPDATE "ConnectedSystemObjects" SET "Status" = @obsolete, "SynchronisationPending" = true, "LastUpdated" = now()
            WHERE "Id" = @id
            """, connection);
        command.Parameters.Add(new NpgsqlParameter("obsolete", NpgsqlDbType.Integer) { Value = (int)ConnectedSystemObjectStatus.Obsolete });
        command.Parameters.Add(new NpgsqlParameter("id", NpgsqlDbType.Uuid) { Value = HrCsoId });
        await command.ExecuteNonQueryAsync();
    }

    private async Task<Guid> ReadJoinedMetaverseObjectIdAsync()
    {
        await using var ctx = NewContext();
        var mvoId = await ctx.ConnectedSystemObjects.AsNoTracking().Where(c => c.Id == HrCsoId).Select(c => c.MetaverseObjectId).SingleAsync();
        Assert.That(mvoId, Is.Not.Null, "precondition: the first Full Synchronisation projected the HR object");
        return mvoId!.Value;
    }

    /// <summary>Runs one synchronisation of the HR system on its own tracking context, as the Worker does.</summary>
    private async Task<Activity> RunSynchronisationAsync(ConnectedSystemRunType runType)
    {
        await using var ctx = NewContext(trackAll: true);
        var repository = new PostgresDataRepository(ctx);
        using var jim = new JimApplication(repository, syncRepository: repository.Sync);
        var system = await jim.ConnectedSystems.GetConnectedSystemAsync(_systemId, withChangeTracking: true)
            ?? throw new InvalidOperationException("Connected System not found.");
        var runProfile = system.RunProfiles!.Single(rp => rp.RunType == runType);
        var activity = new Activity
        {
            TargetName = runProfile.Name, Status = ActivityStatus.InProgress, ConnectedSystemId = system.Id,
            ConnectedSystemRunProfileId = runProfile.Id, ConnectedSystemRunType = runType
        };
        ctx.Activities.Add(activity);
        await ctx.SaveChangesAsync();

        if (runType == ConnectedSystemRunType.FullSynchronisation)
        {
            await new SyncFullSyncTaskProcessor(new SyncEngine(), new SyncServer(jim), jim.SyncRepository,
                system, runProfile, activity, new CancellationTokenSource()).PerformFullSyncAsync();
        }
        else
        {
            await new SyncDeltaSyncTaskProcessor(new SyncEngine(), new SyncServer(jim), jim.SyncRepository,
                system, runProfile, activity, new CancellationTokenSource()).PerformDeltaSyncAsync();
        }

        await using var read = NewContext();
        var errors = await read.ActivityRunProfileExecutionItems.AsNoTracking()
            .Where(i => i.ActivityId == activity.Id && i.ErrorType != null && i.ErrorType != ActivityRunProfileExecutionItemErrorType.NotSet)
            .ToListAsync();
        Assert.That(errors, Is.Empty, $"the run reported errors: {string.Join("; ", errors.Select(e => e.ErrorMessage))}");
        return activity;
    }

    #endregion
}
