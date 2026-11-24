// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Application.Servers;
using JIM.Data.Repositories;
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
/// Real-PostgreSQL synchronisation runs that stop, or meet a failure, part-way through a page flush (#2848). A page's
/// state (its Metaverse Objects, joins, provisioning, Pending Exports and deletions) is written in one transaction, so a
/// run that stops leaves none of the page behind and the next run synchronises it whole; and the Metaverse Object
/// deletion step's per-object fallback still isolates a failing object inside that transaction, through savepoints.
/// </summary>
/// <remarks>
/// The in-memory repository has no transactions, so only PostgreSQL can show any of this. Opt-in via the
/// <c>JIM_TEST_RESET_*</c> environment variables; ignored when <c>JIM_TEST_RESET_DB</c> is absent.
/// </remarks>
[TestFixture]
[Category("RequiresPostgres")]
public class PageFlushAtomicityDatabaseTests
{
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
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL page flush atomicity tests.");
        _connectionString = connectionString;

        TestUtilities.SetEnvironmentVariables();
        using var ctx = NewContext();
        ctx.Database.Migrate();
    }

    [SetUp]
    public async Task SetUpAsync() => await PostgresTestDatabase.ResetAsync(_connectionString);

    /// <summary>
    /// A Delta Synchronisation that stops at the page's Pending Export write, after the page has projected its new object,
    /// joined it and provisioned its account, leaves none of that behind; and the next run provisions the account and
    /// stages its export. Before #2848 each of those steps committed on its own, and the join's write cleared the object's
    /// synchronisation-pending mark, so the next Delta Synchronisation skipped the object and the export was never staged.
    /// </summary>
    [Test]
    public async Task DeltaSync_StoppedAtThePendingExportWrite_LeavesNothingOfThePageAndTheNextRunStagesTheExportAsync()
    {
        await SeedConfigurationAsync(provisionToDirectory: true);
        await CreateHrObjectsAsync(1);
        await RunSynchronisationAsync(ConnectedSystemRunType.FullSynchronisation);
        Assert.That(await CountAsync(), Is.EqualTo(new Counts(Mvos: 1, DirectoryObjects: 1, PendingExports: 1)),
            "precondition: the first object is projected and provisioned");
        await CreateHrObjectsAsync(2);

        Assert.That(async () => await RunSynchronisationAsync(ConnectedSystemRunType.DeltaSynchronisation,
                wrap: (repository, context) => FailBefore(repository, context, nameof(ISyncRepository.CreatePendingExportsAsync))),
            Throws.Exception, "precondition: the run stopped inside its page flush");
        Assert.That(await CountAsync(), Is.EqualTo(new Counts(Mvos: 1, DirectoryObjects: 1, PendingExports: 1)),
            "nothing of the stopped page is left: no Metaverse Object, no provisioned account");

        await RunSynchronisationAsync(ConnectedSystemRunType.DeltaSynchronisation);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(await CountAsync(), Is.EqualTo(new Counts(Mvos: 2, DirectoryObjects: 2, PendingExports: 2)),
                "the next run projects the second object once, provisions its account and stages its export");
            Assert.That(await CountDirectoryObjectsWithoutAPendingExportAsync(), Is.Zero, "every provisioned account has its export");
        }
    }

    /// <summary>
    /// The bulk deletion of a page's Metaverse Objects fails after its evaluation has staged their Delete Pending Exports
    /// and disconnected their accounts. Inside the page's transaction the failure aborts every statement after it, so the
    /// flush rolls back to the savepoint it took before the bulk path, and the per-object fallback must stage and
    /// disconnect everything again, not trust the rolled-back work it remembers.
    /// </summary>
    [Test]
    public async Task DeltaSync_BulkMetaverseObjectDeletionFails_FallbackDeletesEachAndStagesItsDeprovisioningAsync()
    {
        await SeedConfigurationAsync(provisionToDirectory: false);
        var mvoIds = await ArrangeJoinedPeopleAsync(1, 2);
        await MarkHrObjectsObsoleteAsync(1, 2);

        var activity = await RunSynchronisationAsync(ConnectedSystemRunType.DeltaSynchronisation,
            wrap: (repository, context) => FailBefore(repository, context, nameof(ISyncRepository.DeleteMetaverseObjectsAsync), times: 1));

        await using var ctx = NewContext();
        var directoryObjects = await ctx.ConnectedSystemObjects.AsNoTracking()
            .Where(c => c.ConnectedSystemId == _directorySystemId).ToListAsync();
        var deleteExports = await ctx.PendingExports.AsNoTracking()
            .Where(pe => pe.ConnectedSystemId == _directorySystemId && pe.ChangeType == PendingExportChangeType.Delete)
            .Select(pe => pe.ConnectedSystemObjectId).ToListAsync();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(await ctx.MetaverseObjects.AnyAsync(m => mvoIds.Contains(m.Id)), Is.False, "the fallback deleted every Metaverse Object");
            Assert.That(directoryObjects.Select(c => c.MetaverseObjectId), Is.All.Null, "every account is disconnected");
            Assert.That(deleteExports, Is.EquivalentTo(directoryObjects.Select(c => (Guid?)c.Id)), "every account's deprovisioning is staged");
            Assert.That(await CountDeprovisionQueuedOutcomesAsync(activity), Is.EqualTo(directoryObjects.Count),
                "the Activity reports each account's deprovisioning once, and none of the rolled-back attempt's");
        }
    }

    /// <summary>
    /// The bulk deletion fails, and so does one object's own deletion in the fallback. That object is rolled back to its
    /// own savepoint and marked for housekeeping to retry, exactly as before the page had a transaction; its deprovisioning,
    /// evaluated before its deletion failed, stays staged; and the other object is deleted.
    /// </summary>
    [Test]
    public async Task DeltaSync_BulkAndOneObjectsDeletionFail_MarksThatObjectForHousekeepingAndDeletesTheOtherAsync()
    {
        await SeedConfigurationAsync(provisionToDirectory: false);
        var mvoIds = await ArrangeJoinedPeopleAsync(1, 2);
        await MarkHrObjectsObsoleteAsync(1, 2);
        var failing = mvoIds[0];

        await RunSynchronisationAsync(ConnectedSystemRunType.DeltaSynchronisation, assertNoErrors: false,
            wrap: (repository, context) => FailBefore(
                FailBefore(repository, context, nameof(ISyncRepository.DeleteMetaverseObjectsAsync), times: 1),
                context, nameof(ISyncRepository.DeleteMetaverseObjectAsync), when: args => ((MetaverseObject)args[0]!).Id == failing));

        await using var ctx = NewContext();
        var kept = await ctx.MetaverseObjects.AsNoTracking().Where(m => mvoIds.Contains(m.Id)).ToListAsync();
        var deleteExportCount = await ctx.PendingExports.AsNoTracking()
            .CountAsync(pe => pe.ConnectedSystemId == _directorySystemId && pe.ChangeType == PendingExportChangeType.Delete);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(kept.Select(m => m.Id), Is.EqualTo(new[] { failing }), "only the object whose deletion failed is kept");
            Assert.That(kept.Single().LastConnectorDisconnectedDate, Is.Not.Null, "and it is marked for housekeeping to retry");
            Assert.That(deleteExportCount, Is.EqualTo(2), "both objects' deprovisioning is staged");
        }
    }

    #region Harness

    private sealed record Counts(int Mvos, int DirectoryObjects, int PendingExports);

    private async Task<Counts> CountAsync()
    {
        await using var ctx = NewContext();
        return new Counts(
            await ctx.MetaverseObjects.AsNoTracking().CountAsync(),
            await ctx.ConnectedSystemObjects.AsNoTracking().CountAsync(c => c.ConnectedSystemId == _directorySystemId),
            await ctx.PendingExports.AsNoTracking().CountAsync(pe => pe.ConnectedSystemId == _directorySystemId));
    }

    private async Task<int> CountDeprovisionQueuedOutcomesAsync(Activity activity)
    {
        await using var ctx = NewContext();
        return await ctx.ActivityRunProfileExecutionItemSyncOutcomes.AsNoTracking()
            .CountAsync(o => o.OutcomeType == ActivityRunProfileExecutionItemSyncOutcomeType.DeprovisionQueued &&
                             ctx.ActivityRunProfileExecutionItems.Any(i => i.Id == o.ActivityRunProfileExecutionItemId && i.ActivityId == activity.Id));
    }

    private async Task<int> CountDirectoryObjectsWithoutAPendingExportAsync()
    {
        await using var ctx = NewContext();
        return await ctx.ConnectedSystemObjects.AsNoTracking()
            .CountAsync(c => c.ConnectedSystemId == _directorySystemId && !ctx.PendingExports.Any(pe => pe.ConnectedSystemObjectId == c.Id));
    }

    /// <summary>
    /// Wraps <paramref name="repository"/> so <paramref name="methodName"/> fails instead of running: on every call
    /// <paramref name="when"/> accepts, at most <paramref name="times"/> times when given. It fails as a database write
    /// does, with a statement PostgreSQL refuses on the run's own connection, so inside a transaction the failure aborts
    /// it as a real one would; an exception thrown in code alone would leave the transaction usable and prove nothing about
    /// recovering from one.
    /// </summary>
    private static ISyncRepository FailBefore(ISyncRepository repository, JimDbContext runContext, string methodName, int? times = null,
        Func<object?[], bool>? when = null)
    {
        var failed = 0;
        return InterceptingSyncRepositoryProxy.Create(repository, methodName, args =>
        {
            if ((when == null || when(args)) && (times == null || failed < times))
            {
                failed++;
                runContext.Database.ExecuteSqlRaw("SELECT 1 / 0");
            }
        });
    }

    private static Guid HrIdOf(int n) => Guid.Parse($"00000000-0000-0000-0000-0000000000{n:D2}");

    private static Guid DirectoryIdOf(int n) => Guid.Parse($"00000000-0000-0000-0001-0000000000{n:D2}");

    /// <summary>
    /// HR projects each person and is the authoritative source whose departure deletes the Metaverse Object at once. The
    /// Directory system's export Synchronisation Rule flows the display name, deletes a deleted person's account and, when
    /// <paramref name="provisionToDirectory"/>, provisions an account for every person.
    /// </summary>
    private async Task SeedConfigurationAsync(bool provisionToDirectory)
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
        var directoryDisplayName = TextAttribute(directoryType, "displayName");
        directoryType.Attributes.Add(directoryExternalId);
        directoryType.Attributes.Add(directoryDisplayName);

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

        var importRule = new SyncRule
        {
            Name = "HR Import", ConnectedSystem = system, Direction = SyncRuleDirection.Import, Enabled = true,
            ConnectedSystemObjectType = type, MetaverseObjectType = mvType, ProjectToMetaverse = true,
            InboundOutOfScopeAction = InboundOutOfScopeAction.Disconnect
        };
        importRule.AttributeFlowRules.Add(new SyncRuleMapping
        {
            SyncRule = importRule, TargetMetaverseAttribute = mvDisplayName,
            Sources = { new SyncRuleMappingSource { Order = 0, ConnectedSystemAttribute = displayName } }
        });
        var exportRule = new SyncRule
        {
            Name = "Directory Export", ConnectedSystem = directory, Direction = SyncRuleDirection.Export, Enabled = true,
            ConnectedSystemObjectType = directoryType, MetaverseObjectType = mvType, ProvisionToConnectedSystem = provisionToDirectory,
            OutboundDeprovisionAction = OutboundDeprovisionAction.Delete
        };
        exportRule.AttributeFlowRules.Add(new SyncRuleMapping
        {
            SyncRule = exportRule, TargetConnectedSystemAttribute = directoryDisplayName,
            Sources = { new SyncRuleMappingSource { Order = 0, MetaverseAttribute = mvDisplayName } }
        });
        seed.SyncRules.AddRange(importRule, exportRule);
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

    /// <summary>HR objects, created two hours ago through the import's own create writer.</summary>
    private async Task CreateHrObjectsAsync(params int[] ns)
    {
        await using var ctx = NewContext();
        var objects = ns.Select(n =>
        {
            var cso = new ConnectedSystemObject
            {
                Id = HrIdOf(n), ConnectedSystemId = _systemId, TypeId = _typeId, ExternalIdAttributeId = _externalIdAttributeId,
                Status = ConnectedSystemObjectStatus.Normal, Created = DateTime.UtcNow.AddHours(-2)
            };
            cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Id = Guid.NewGuid(), AttributeId = _externalIdAttributeId, StringValue = $"E{n}" });
            cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Id = Guid.NewGuid(), AttributeId = _displayNameAttributeId, StringValue = $"Person {n}" });
            return cso;
        }).ToList();
        await new PostgresDataRepository(ctx).Sync.CreateConnectedSystemObjectsAsync(objects);
    }

    /// <summary>
    /// The HR objects, projected by a Full Synchronisation, each with an exported Directory account joined to its
    /// Metaverse Object. Returns the Metaverse Object ids, in the order of <paramref name="ns"/>.
    /// </summary>
    private async Task<List<Guid>> ArrangeJoinedPeopleAsync(params int[] ns)
    {
        await CreateHrObjectsAsync(ns);
        await RunSynchronisationAsync(ConnectedSystemRunType.FullSynchronisation);

        var mvoIds = new List<Guid>();
        await using var ctx = NewContext();
        foreach (var n in ns)
        {
            var mvoId = await ctx.ConnectedSystemObjects.AsNoTracking().Where(c => c.Id == HrIdOf(n)).Select(c => c.MetaverseObjectId).SingleAsync();
            Assert.That(mvoId, Is.Not.Null, $"precondition: the Full Synchronisation projected HR object {n}");
            mvoIds.Add(mvoId!.Value);

            var account = new ConnectedSystemObject
            {
                Id = DirectoryIdOf(n), ConnectedSystemId = _directorySystemId, TypeId = _directoryTypeId,
                ExternalIdAttributeId = _directoryExternalIdAttributeId, Status = ConnectedSystemObjectStatus.Normal,
                Created = DateTime.UtcNow.AddHours(-2), MetaverseObjectId = mvoId
            };
            account.RecordJoin(ConnectedSystemObjectJoinMethod.Provisioning, null, DateTime.UtcNow);
            account.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Id = Guid.NewGuid(), AttributeId = _directoryExternalIdAttributeId, StringValue = $"A{n}" });
            await using var write = NewContext();
            await new PostgresDataRepository(write).Sync.CreateConnectedSystemObjectsAsync([account]);
        }

        return mvoIds;
    }

    /// <summary>An import finding the HR objects gone: obsolete, and pending synchronisation.</summary>
    private async Task MarkHrObjectsObsoleteAsync(params int[] ns)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            UPDATE "ConnectedSystemObjects" SET "Status" = @obsolete, "SynchronisationPending" = true, "LastUpdated" = now()
            WHERE "Id" = ANY(@ids)
            """, connection);
        command.Parameters.Add(new NpgsqlParameter("obsolete", NpgsqlDbType.Integer) { Value = (int)ConnectedSystemObjectStatus.Obsolete });
        command.Parameters.Add(new NpgsqlParameter("ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = ns.Select(HrIdOf).ToArray() });
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Runs one synchronisation of the HR system on its own tracking context, as the Worker does. <paramref name="wrap"/>
    /// wraps the run's repository for both the processor and the servers it calls, and is handed the run's context, so a
    /// test can make a write fail on the run's own connection wherever in the flush it is issued.
    /// </summary>
    private async Task<Activity> RunSynchronisationAsync(ConnectedSystemRunType runType, Func<ISyncRepository, JimDbContext, ISyncRepository>? wrap = null,
        bool assertNoErrors = true)
    {
        await using var ctx = NewContext(trackAll: true);
        var repository = new PostgresDataRepository(ctx);
        var syncRepository = wrap?.Invoke(repository.Sync, ctx) ?? repository.Sync;
        using var jim = new JimApplication(repository, syncRepository: syncRepository);
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
            await new SyncFullSyncTaskProcessor(new SyncEngine(), new SyncServer(jim), syncRepository,
                system, runProfile, activity, new CancellationTokenSource()).PerformFullSyncAsync();
        }
        else
        {
            await new SyncDeltaSyncTaskProcessor(new SyncEngine(), new SyncServer(jim), syncRepository,
                system, runProfile, activity, new CancellationTokenSource()).PerformDeltaSyncAsync();
        }

        if (assertNoErrors)
        {
            await using var read = NewContext();
            var errors = await read.ActivityRunProfileExecutionItems.AsNoTracking()
                .Where(i => i.ActivityId == activity.Id && i.ErrorType != null && i.ErrorType != ActivityRunProfileExecutionItemErrorType.NotSet)
                .ToListAsync();
            Assert.That(errors, Is.Empty, $"the run reported errors: {string.Join("; ", errors.Select(e => e.ErrorMessage))}");
        }

        return activity;
    }

    #endregion
}
