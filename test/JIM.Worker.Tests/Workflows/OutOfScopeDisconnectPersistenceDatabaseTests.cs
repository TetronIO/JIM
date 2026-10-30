// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Application.Servers;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Logic;
using JIM.Models.Search;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using JIM.PostgresData;
using JIM.TestSupport;
using JIM.Worker.Processors;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NUnit.Framework;

namespace JIM.Worker.Tests.Workflows;

/// <summary>
/// Real-PostgreSQL Full Synchronisation runs in which a joined Connected System Object falls out of scope of its import
/// Synchronisation Rule, whose Inbound Out-of-Scope Action is Disconnect. The Activity reports the disconnection; these
/// tests read the row back on a separate connection and assert that it was in fact released: no Metaverse Object and
/// none of the join record. Before the fix the release was made in memory only, so whenever the Metaverse Object
/// survived (a Manual deletion rule, a deletion scheduled by a grace period, another connector still joined) the row
/// kept its Metaverse Object and its join record. The in-memory repository cannot show this: it stores the instances
/// the run holds, so its read-back always sees the in-memory release (see
/// <c>OutOfScopeDisconnectPersistenceWorkflowTests</c> for the fast test of the same hand-off).
/// </summary>
[TestFixture]
[Category("RequiresPostgres")]
public class OutOfScopeDisconnectPersistenceDatabaseTests
{
    private string _connectionString = null!;
    private int _systemId;
    private int _typeId;
    private int _externalIdAttributeId;
    private int _displayNameAttributeId;
    private int _inScopeAttributeId;
    private int _directorySystemId;
    private int _directoryTypeId;
    private int _directoryExternalIdAttributeId;

    private static readonly Guid CsoId = Guid.Parse("00000000-0000-0000-0000-000000000001");

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
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL out-of-scope disconnection tests.");
        _connectionString = connectionString;

        TestUtilities.SetEnvironmentVariables();
        using var ctx = NewContext();
        ctx.Database.Migrate();
    }

    [SetUp]
    public async Task SetUpAsync() => await PostgresTestDatabase.ResetAsync(_connectionString);

    #region Harness

    /// <summary>
    /// The HR Connected System, whose import Synchronisation Rule projects each object and takes it out of scope when
    /// its <c>inScope</c> attribute is no longer "yes", and a Directory Connected System that holds no rules, whose
    /// object a test can join to the same Metaverse Object as a second connector. The Metaverse Object type carries
    /// the deletion rule under test.
    /// </summary>
    private async Task SeedConfigurationAsync(MetaverseObjectDeletionRule deletionRule, TimeSpan? gracePeriod,
        OutboundDeprovisionAction? hrExportDeprovisionAction = null)
    {
        await using var seed = NewContext();

        foreach (var (key, valueType, value) in new[]
                 {
                     (Constants.SettingKeys.SyncPageSize, ServiceSettingValueType.Integer, "100"),
                     ("ChangeTracking.CsoChanges.Enabled", ServiceSettingValueType.Boolean, "false"),
                     ("ChangeTracking.MvoChanges.Enabled", ServiceSettingValueType.Boolean, "false")
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
        var inScope = TextAttribute(type, "inScope");
        type.Attributes.Add(externalId);
        type.Attributes.Add(displayName);
        type.Attributes.Add(inScope);

        var directory = new ConnectedSystem
        {
            Name = "Directory", ConnectorDefinition = connectorDefinition, ObjectMatchingRuleMode = ObjectMatchingRuleMode.SyncRule
        };
        var directoryType = new ConnectedSystemObjectType { Name = "account", ConnectedSystem = directory, Selected = true };
        var directoryExternalId = TextAttribute(directoryType, "accountId", isExternalId: true);
        directoryType.Attributes.Add(directoryExternalId);

        var mvType = new MetaverseObjectType
        {
            Name = "Person", PluralName = "People", DeletionRule = deletionRule, DeletionGracePeriod = gracePeriod
        };
        var mvDisplayName = new MetaverseAttribute
        {
            Name = "Display Name", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.SingleValued,
            MetaverseObjectTypes = [mvType]
        };

        seed.AddRange(connectorDefinition, system, type, directory, directoryType, mvType, mvDisplayName);
        await seed.SaveChangesAsync();
        seed.ConnectedSystemRunProfiles.Add(new ConnectedSystemRunProfile
            { ConnectedSystemId = system.Id, Name = "Full Synchronisation", RunType = ConnectedSystemRunType.FullSynchronisation });
        seed.ConnectedSystemRunProfiles.Add(new ConnectedSystemRunProfile
            { ConnectedSystemId = system.Id, Name = "Delta Synchronisation", RunType = ConnectedSystemRunType.DeltaSynchronisation });
        await seed.SaveChangesAsync();

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
        rule.ObjectScopingCriteriaGroups.Add(new SyncRuleScopingCriteriaGroup
        {
            Type = SearchGroupType.All,
            Criteria =
            [
                new SyncRuleScopingCriteria
                {
                    ConnectedSystemAttribute = inScope, ComparisonType = SearchComparisonType.Equals, StringValue = "yes", CaseSensitive = true
                }
            ]
        });
        seed.SyncRules.Add(rule);

        // An export rule back to the HR system makes it a deprovisioning target as well as a source, so the deletion of a
        // Metaverse Object it was joined to has a rule to act on for the very object that left.
        if (hrExportDeprovisionAction is { } deprovisionAction)
        {
            seed.SyncRules.Add(new SyncRule
            {
                Name = "HR Export", ConnectedSystem = system, Direction = SyncRuleDirection.Export, Enabled = true,
                ConnectedSystemObjectType = type, MetaverseObjectType = mvType, OutboundDeprovisionAction = deprovisionAction
            });
        }
        await seed.SaveChangesAsync();

        _systemId = system.Id;
        _typeId = type.Id;
        _externalIdAttributeId = externalId.Id;
        _displayNameAttributeId = displayName.Id;
        _inScopeAttributeId = inScope.Id;
        _directorySystemId = directory.Id;
        _directoryTypeId = directoryType.Id;
        _directoryExternalIdAttributeId = directoryExternalId.Id;
    }

    private static ConnectedSystemObjectTypeAttribute TextAttribute(ConnectedSystemObjectType type, string name, bool isExternalId = false) => new()
    {
        Name = name, ConnectedSystemObjectType = type, Type = AttributeDataType.Text,
        AttributePlurality = AttributePlurality.SingleValued, Selected = true, IsExternalId = isExternalId
    };

    /// <summary>The HR object, created through the import's own create writer, in scope.</summary>
    private async Task CreateHrObjectAsync()
    {
        await using var ctx = NewContext();
        var cso = new ConnectedSystemObject
        {
            Id = CsoId, ConnectedSystemId = _systemId, TypeId = _typeId, ExternalIdAttributeId = _externalIdAttributeId,
            Status = ConnectedSystemObjectStatus.Normal, Created = DateTime.UtcNow.AddHours(-2)
        };
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Id = Guid.NewGuid(), AttributeId = _externalIdAttributeId, StringValue = "E1" });
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Id = Guid.NewGuid(), AttributeId = _displayNameAttributeId, StringValue = "Jane Smith" });
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Id = Guid.NewGuid(), AttributeId = _inScopeAttributeId, StringValue = "yes" });
        await new PostgresDataRepository(ctx).Sync.CreateConnectedSystemObjectsAsync([cso]);
    }

    /// <summary>An import committing a change that takes the HR object out of scope, through the import's own update writer.</summary>
    private async Task ImportScopeExitAsync()
    {
        await using var ctx = NewContext();
        var cso = await ctx.ConnectedSystemObjects.AsNoTracking().Include(c => c.AttributeValues).SingleAsync(c => c.Id == CsoId);
        var previous = cso.AttributeValues.Single(av => av.AttributeId == _inScopeAttributeId);
        var replacement = new ConnectedSystemObjectAttributeValue { Id = Guid.NewGuid(), AttributeId = _inScopeAttributeId, StringValue = "no" };
        cso.LastUpdated = DateTime.UtcNow;
        await new PostgresDataRepository(ctx).Sync.UpdateConnectedSystemObjectsAsync([cso], [(cso.Id, replacement)], [previous.Id]);
    }

    /// <summary>An object in the Directory system, joined to the Metaverse Object as a second connector.</summary>
    private async Task JoinDirectoryObjectAsync(Guid mvoId)
    {
        await using var ctx = NewContext();
        var cso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(), ConnectedSystemId = _directorySystemId, TypeId = _directoryTypeId,
            ExternalIdAttributeId = _directoryExternalIdAttributeId, Status = ConnectedSystemObjectStatus.Normal,
            Created = DateTime.UtcNow.AddHours(-2), MetaverseObjectId = mvoId
        };
        cso.RecordJoin(ConnectedSystemObjectJoinMethod.Provisioning, null, DateTime.UtcNow);
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Id = Guid.NewGuid(), AttributeId = _directoryExternalIdAttributeId, StringValue = "A1" });
        await new PostgresDataRepository(ctx).Sync.CreateConnectedSystemObjectsAsync([cso]);
    }

    /// <summary>Runs one synchronisation of the HR system on its own tracking context, as the Worker does.</summary>
    private async Task<Activity> RunSynchronisationAsync(ConnectedSystemRunType runType = ConnectedSystemRunType.FullSynchronisation)
    {
        await using var ctx = NewContext(trackAll: true);
        var repository = new PostgresDataRepository(ctx);
        // The sync repository is passed explicitly, as every production registration does: the MVO deletion flush
        // reads it through the application's export evaluation.
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

        // The page flush stores the execution items and releases them from the Activity, so they are read back from the database.
        await using var read = NewContext();
        var errors = await read.ActivityRunProfileExecutionItems.AsNoTracking()
            .Where(i => i.ActivityId == activity.Id && i.ErrorType != null && i.ErrorType != ActivityRunProfileExecutionItemErrorType.NotSet)
            .ToListAsync();
        Assert.That(errors, Is.Empty, $"the run reported errors: {string.Join("; ", errors.Select(e => e.ErrorMessage))}");
        return activity;
    }

    private async Task<bool> ActivityReportsDisconnectionAsync(Activity activity)
    {
        await using var ctx = NewContext();
        return await ctx.ActivityRunProfileExecutionItems.AsNoTracking()
            .AnyAsync(i => i.ActivityId == activity.Id && i.ObjectChangeType == ObjectChangeType.DisconnectedOutOfScope);
    }

    private sealed record JoinRow(
        Guid? MetaverseObjectId,
        ConnectedSystemObjectJoinType JoinType,
        DateTime? DateJoined,
        ConnectedSystemObjectJoinMethod? JoinMethod,
        int? JoinSyncRuleId,
        string? JoinSyncRuleName,
        int? JoinObjectMatchingRuleId,
        string? JoinMatchValue);

    /// <summary>The HR object's join columns as stored, read on a connection of their own.</summary>
    private async Task<JoinRow> ReadJoinRowAsync()
    {
        await using var ctx = NewContext();
        return await ctx.ConnectedSystemObjects.AsNoTracking().Where(c => c.Id == CsoId)
            .Select(c => new JoinRow(c.MetaverseObjectId, c.JoinType, c.DateJoined, c.JoinMethod, c.JoinSyncRuleId,
                c.JoinSyncRuleName, c.JoinObjectMatchingRuleId, c.JoinMatchValue))
            .SingleAsync();
    }

    private async Task<bool> MetaverseObjectExistsAsync(Guid mvoId)
    {
        await using var ctx = NewContext();
        return await ctx.MetaverseObjects.AsNoTracking().AnyAsync(m => m.Id == mvoId);
    }

    /// <summary>Projects the HR object, optionally gives its Metaverse Object a second connector, then takes it out of scope and synchronises again.</summary>
    private async Task<(Guid MvoId, Activity Activity)> ProjectThenTakeOutOfScopeAsync(bool withSecondConnector = false,
        ConnectedSystemRunType secondRunType = ConnectedSystemRunType.FullSynchronisation)
    {
        await CreateHrObjectAsync();
        await RunSynchronisationAsync();

        var projected = await ReadJoinRowAsync();
        Assert.That(projected.MetaverseObjectId, Is.Not.Null, "precondition: the first Full Synchronisation projected the object");
        Assert.That(projected.JoinType, Is.EqualTo(ConnectedSystemObjectJoinType.Projected), "precondition: the projection is recorded");
        var mvoId = projected.MetaverseObjectId!.Value;

        if (withSecondConnector)
            await JoinDirectoryObjectAsync(mvoId);

        await ImportScopeExitAsync();
        var activity = await RunSynchronisationAsync(secondRunType);
        Assert.That(await ActivityReportsDisconnectionAsync(activity), Is.True, "precondition: the Activity reports the disconnection");
        return (mvoId, activity);
    }

    /// <summary>The stored row of an object whose join was released: no Metaverse Object, none of the join record.</summary>
    private async Task AssertJoinReleasedAsync()
    {
        var row = await ReadJoinRowAsync();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(row.MetaverseObjectId, Is.Null, "the Activity reported the disconnection, so the row must no longer hold the Metaverse Object");
            Assert.That(row.JoinType, Is.EqualTo(ConnectedSystemObjectJoinType.NotJoined));
            Assert.That(row.DateJoined, Is.Null);
            Assert.That(row.JoinMethod, Is.Null);
            Assert.That(row.JoinSyncRuleId, Is.Null);
            Assert.That(row.JoinSyncRuleName, Is.Null);
            Assert.That(row.JoinObjectMatchingRuleId, Is.Null);
            Assert.That(row.JoinMatchValue, Is.Null);
        }
    }

    #endregion

    [Test]
    public async Task FullSync_CsoLeavesScopeAndMvoIsKeptByManualDeletionRule_ReleasesTheJoinInTheDatabaseAsync()
    {
        await SeedConfigurationAsync(MetaverseObjectDeletionRule.Manual, gracePeriod: null);

        var (mvoId, _) = await ProjectThenTakeOutOfScopeAsync();

        Assert.That(await MetaverseObjectExistsAsync(mvoId), Is.True, "precondition: the Metaverse Object is kept");
        await AssertJoinReleasedAsync();
    }

    [Test]
    public async Task FullSync_CsoLeavesScopeAndMvoIsScheduledForDeletion_ReleasesTheJoinInTheDatabaseAsync()
    {
        await SeedConfigurationAsync(MetaverseObjectDeletionRule.WhenLastConnectorDisconnected, TimeSpan.FromDays(7));

        var (mvoId, _) = await ProjectThenTakeOutOfScopeAsync();

        await using (var ctx = NewContext())
        {
            var scheduled = await ctx.MetaverseObjects.AsNoTracking().Where(m => m.Id == mvoId).Select(m => m.LastConnectorDisconnectedDate).SingleOrDefaultAsync();
            Assert.That(scheduled, Is.Not.Null, "precondition: the grace period keeps the Metaverse Object, its deletion scheduled");
        }
        await AssertJoinReleasedAsync();
    }

    [Test]
    public async Task FullSync_CsoLeavesScopeAndMvoKeepsAnotherConnector_ReleasesTheJoinInTheDatabaseAsync()
    {
        await SeedConfigurationAsync(MetaverseObjectDeletionRule.WhenLastConnectorDisconnected, gracePeriod: null);

        var (mvoId, _) = await ProjectThenTakeOutOfScopeAsync(withSecondConnector: true);

        Assert.That(await MetaverseObjectExistsAsync(mvoId), Is.True, "precondition: the other connector keeps the Metaverse Object");
        await AssertJoinReleasedAsync();
        await using var ctx = NewContext();
        Assert.That(await ctx.ConnectedSystemObjects.AsNoTracking().CountAsync(c => c.MetaverseObjectId == mvoId), Is.EqualTo(1),
            "the other connector stays joined");
    }

    [Test]
    public async Task FullSync_CsoLeavesScopeAndMvoIsDeletedImmediately_ReleasesTheJoinInTheDatabaseAsync()
    {
        await SeedConfigurationAsync(MetaverseObjectDeletionRule.WhenLastConnectorDisconnected, gracePeriod: null);

        var (mvoId, _) = await ProjectThenTakeOutOfScopeAsync();

        Assert.That(await MetaverseObjectExistsAsync(mvoId), Is.False, "precondition: the Metaverse Object is deleted in the same flush");
        await AssertJoinReleasedAsync();
    }

    /// <summary>
    /// Delta Synchronisation flushes its pages by a path of its own, and selects the object because the import changed it.
    /// </summary>
    [Test]
    public async Task DeltaSync_CsoLeavesScopeAndMvoIsKeptByManualDeletionRule_ReleasesTheJoinInTheDatabaseAsync()
    {
        await SeedConfigurationAsync(MetaverseObjectDeletionRule.Manual, gracePeriod: null);

        var (mvoId, _) = await ProjectThenTakeOutOfScopeAsync(secondRunType: ConnectedSystemRunType.DeltaSynchronisation);

        Assert.That(await MetaverseObjectExistsAsync(mvoId), Is.True, "precondition: the Metaverse Object is kept");
        await AssertJoinReleasedAsync();
    }

    /// <summary>
    /// The object that left scope is the one whose departure deletes the Metaverse Object, so it is not one of the
    /// Metaverse Object's remaining connectors to deprovision, even when an export rule would delete such an object.
    /// Left joined in the database until the deletion flush, it was found there as if it were, and a Delete Pending
    /// Export was staged against an object the Activity had just reported as disconnected.
    /// </summary>
    [Test]
    public async Task FullSync_CsoLeavesScopeAndMvoIsDeletedImmediately_StagesNoDeleteForTheObjectThatLeftAsync()
    {
        await SeedConfigurationAsync(MetaverseObjectDeletionRule.WhenLastConnectorDisconnected, gracePeriod: null,
            hrExportDeprovisionAction: OutboundDeprovisionAction.Delete);

        var (mvoId, _) = await ProjectThenTakeOutOfScopeAsync();

        Assert.That(await MetaverseObjectExistsAsync(mvoId), Is.False, "precondition: the Metaverse Object is deleted in the same flush");
        await using var ctx = NewContext();
        var deletes = await ctx.PendingExports.AsNoTracking()
            .Where(pe => pe.ConnectedSystemObjectId == CsoId && pe.ChangeType == PendingExportChangeType.Delete)
            .CountAsync();
        Assert.That(deletes, Is.Zero, "the object that left scope was disconnected, not deprovisioned");
    }
}
