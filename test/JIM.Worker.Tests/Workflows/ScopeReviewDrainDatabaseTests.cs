// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Application.Servers;
using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.PostgresData;
using JIM.TestSupport;
using JIM.Worker.Processors;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NUnit.Framework;

namespace JIM.Worker.Tests.Workflows;

/// <summary>
/// Real-PostgreSQL synchronisation runs of the export scope review drain (#2615). The drain clears a Metaverse Object's
/// flag only while the row still carries the version (<c>xmin</c>) it was loaded at, so a review that wrote the row
/// would keep every flag it cleared, for ever. These runs prove it does not (whether the review changes anything or
/// not), and that a write made between the drain's load and its clear is kept for the next run and delivered by it. Only
/// PostgreSQL can show either: the in-memory store has no row versions.
/// </summary>
[TestFixture]
[Category("RequiresPostgres")]
public class ScopeReviewDrainDatabaseTests
{
    private string _connectionString = null!;
    private int _hrId;
    private int _directoryId;
    private int _directoryTypeId;
    private int _mvTypeId;
    private int _mvDisplayNameAttributeId;
    private int _directoryDisplayNameAttributeId;

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
        var dbName = Environment.GetEnvironmentVariable("JIM_TEST_RESET_DB");
        if (string.IsNullOrEmpty(dbName))
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL scope review drain tests.");

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
        await SeedConfigurationAsync();
    }

    #region Harness

    /// <summary>
    /// An "HR" Connected System whose import rule projects every object into a Person and flows its display name, and a
    /// "Directory" Connected System with no export rule yet (each test adds the one it needs); change tracking off.
    /// </summary>
    private async Task SeedConfigurationAsync()
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
        var hr = new ConnectedSystem { Name = "HR", ConnectorDefinition = connectorDefinition, ObjectMatchingRuleMode = ObjectMatchingRuleMode.SyncRule };
        var hrType = new ConnectedSystemObjectType { Name = "person", ConnectedSystem = hr, Selected = true };
        var hrExternalId = new ConnectedSystemObjectTypeAttribute
        {
            Name = "employeeId", ConnectedSystemObjectType = hrType, Type = AttributeDataType.Text,
            AttributePlurality = AttributePlurality.SingleValued, Selected = true, IsExternalId = true
        };
        var hrDisplayName = new ConnectedSystemObjectTypeAttribute
        {
            Name = "displayName", ConnectedSystemObjectType = hrType, Type = AttributeDataType.Text,
            AttributePlurality = AttributePlurality.SingleValued, Selected = true
        };
        hrType.Attributes.Add(hrExternalId);
        hrType.Attributes.Add(hrDisplayName);

        var directory = new ConnectedSystem { Name = "Directory", ConnectorDefinition = connectorDefinition, ObjectMatchingRuleMode = ObjectMatchingRuleMode.SyncRule };
        var directoryType = new ConnectedSystemObjectType { Name = "account", ConnectedSystem = directory, Selected = true };
        var directoryExternalId = new ConnectedSystemObjectTypeAttribute
        {
            Name = "accountId", ConnectedSystemObjectType = directoryType, Type = AttributeDataType.Text,
            AttributePlurality = AttributePlurality.SingleValued, Selected = true, IsExternalId = true
        };
        var directoryDisplayName = new ConnectedSystemObjectTypeAttribute
        {
            Name = "cn", ConnectedSystemObjectType = directoryType, Type = AttributeDataType.Text,
            AttributePlurality = AttributePlurality.SingleValued, Selected = true
        };
        directoryType.Attributes.Add(directoryExternalId);
        directoryType.Attributes.Add(directoryDisplayName);

        var mvType = new MetaverseObjectType { Name = "Person", PluralName = "People" };
        var mvDisplayName = new MetaverseAttribute
        {
            Name = "Display Name", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.SingleValued,
            MetaverseObjectTypes = [mvType]
        };

        seed.AddRange(connectorDefinition, hr, directory, hrType, directoryType, mvType, mvDisplayName);
        await seed.SaveChangesAsync();
        seed.ConnectedSystemRunProfiles.Add(new ConnectedSystemRunProfile
            { ConnectedSystemId = hr.Id, Name = "Full Synchronisation", RunType = ConnectedSystemRunType.FullSynchronisation });
        seed.ConnectedSystemRunProfiles.Add(new ConnectedSystemRunProfile
            { ConnectedSystemId = hr.Id, Name = "Delta Synchronisation", RunType = ConnectedSystemRunType.DeltaSynchronisation });
        await seed.SaveChangesAsync();

        var import = new SyncRule
        {
            Name = "HR Import", ConnectedSystem = hr, Direction = SyncRuleDirection.Import, Enabled = true,
            ConnectedSystemObjectType = hrType, MetaverseObjectType = mvType, ProjectToMetaverse = true
        };
        import.AttributeFlowRules.Add(new SyncRuleMapping
        {
            SyncRule = import, TargetMetaverseAttribute = mvDisplayName,
            Sources = { new SyncRuleMappingSource { Order = 0, ConnectedSystemAttribute = hrDisplayName } }
        });
        seed.SyncRules.Add(import);
        await seed.SaveChangesAsync();

        _hrId = hr.Id;
        _directoryId = directory.Id;
        _directoryTypeId = directoryType.Id;
        _mvTypeId = mvType.Id;
        _mvDisplayNameAttributeId = mvDisplayName.Id;
        _directoryDisplayNameAttributeId = directoryDisplayName.Id;
        _hrExternalIdAttributeId = hrExternalId.Id;
        _hrDisplayNameAttributeId = hrDisplayName.Id;
    }

    private int _hrExternalIdAttributeId;
    private int _hrDisplayNameAttributeId;

    /// <summary>
    /// The Directory's export rule for Person, flowing Display Name to the Directory's cn. Added after the first
    /// synchronisation, so the people it covers exist but have not been reviewed against it.
    /// </summary>
    private async Task AddExportRuleAsync(bool provisioning)
    {
        await using var seed = NewContext();
        var rule = new SyncRule
        {
            Name = "Directory Export", Direction = SyncRuleDirection.Export, Enabled = true,
            ConnectedSystemId = _directoryId, ConnectedSystemObjectTypeId = _directoryTypeId, MetaverseObjectTypeId = _mvTypeId,
            ProvisionToConnectedSystem = provisioning, OutboundDeprovisionAction = OutboundDeprovisionAction.Disconnect,
            // Added after the objects it covers were last synchronised, as a later configuration change is.
            Created = DateTime.UtcNow.AddMinutes(-30)
        };
        rule.AttributeFlowRules.Add(new SyncRuleMapping
        {
            SyncRule = rule, TargetConnectedSystemAttributeId = _directoryDisplayNameAttributeId,
            Sources = { new SyncRuleMappingSource { Order = 0, MetaverseAttributeId = _mvDisplayNameAttributeId } }
        });
        seed.SyncRules.Add(rule);
        await seed.SaveChangesAsync();
    }

    /// <summary>Creates the people through the import's own create writer, created two hours ago.</summary>
    private async Task CreateHrPeopleAsync(params string[] names)
    {
        await using var ctx = NewContext();
        var typeId = ctx.ConnectedSystemObjectTypes.AsNoTracking().Single(t => t.ConnectedSystemId == _hrId).Id;
        var created = DateTime.UtcNow.AddHours(-2);
        var objects = names.Select((name, n) =>
        {
            var cso = new ConnectedSystemObject
            {
                Id = Guid.Parse($"00000000-0000-0000-0000-{n + 1:D12}"), ConnectedSystemId = _hrId, TypeId = typeId,
                ExternalIdAttributeId = _hrExternalIdAttributeId, Status = ConnectedSystemObjectStatus.Normal, Created = created
            };
            cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Id = Guid.NewGuid(), AttributeId = _hrExternalIdAttributeId, StringValue = $"E{n + 1}" });
            cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Id = Guid.NewGuid(), AttributeId = _hrDisplayNameAttributeId, StringValue = name });
            return cso;
        }).ToList();
        await new PostgresDataRepository(ctx).Sync.CreateConnectedSystemObjectsAsync(objects);
    }

    /// <summary>
    /// Runs one synchronisation of HR on its own tracking context, as the Worker does.
    /// <paramref name="wrapRepository"/> lets a test act between two repository calls.
    /// </summary>
    private async Task RunAsync(ConnectedSystemRunType runType, Func<ISyncRepository, ISyncRepository>? wrapRepository = null)
    {
        await using var ctx = NewContext(trackAll: true);
        var repository = new PostgresDataRepository(ctx);
        using var jim = new JimApplication(repository, syncRepository: repository.Sync);
        var system = await jim.ConnectedSystems.GetConnectedSystemAsync(_hrId, withChangeTracking: true)
            ?? throw new InvalidOperationException("Connected System not found.");
        var runProfile = system.RunProfiles!.Single(rp => rp.RunType == runType);
        var activity = new Activity
        {
            TargetName = runProfile.Name, Status = ActivityStatus.InProgress, ConnectedSystemId = system.Id,
            ConnectedSystemRunProfileId = runProfile.Id, ConnectedSystemRunType = runType
        };
        ctx.Activities.Add(activity);
        await ctx.SaveChangesAsync();

        var syncRepository = wrapRepository?.Invoke(jim.SyncRepository) ?? jim.SyncRepository;
        if (runType == ConnectedSystemRunType.FullSynchronisation)
            await new SyncFullSyncTaskProcessor(new SyncEngine(), new SyncServer(jim), syncRepository, system, runProfile, activity, new CancellationTokenSource())
                .PerformFullSyncAsync();
        else
            await new SyncDeltaSyncTaskProcessor(new SyncEngine(), new SyncServer(jim), syncRepository, system, runProfile, activity, new CancellationTokenSource())
                .PerformDeltaSyncAsync();

        var errors = activity.RunProfileExecutionItems.Where(i => i.ErrorType is { } e && e != ActivityRunProfileExecutionItemErrorType.NotSet).ToList();
        Assert.That(errors, Is.Empty, $"the run reported errors: {string.Join("; ", errors.Select(e => e.ErrorMessage))}");
    }

    /// <summary>Two people, projected by a Full Synchronisation, with the watermark set so a Delta Synchronisation finds nothing new.</summary>
    private async Task ArrangeProjectedPeopleAsync()
    {
        await CreateHrPeopleAsync("Alice", "Bob");
        await RunAsync(ConnectedSystemRunType.FullSynchronisation);
        await using var ctx = NewContext();
        Assert.That(await ctx.MetaverseObjects.AsNoTracking().CountAsync(), Is.EqualTo(2), "precondition: both people were projected");
    }

    /// <summary>Alice's Metaverse Object id (the one holding the display name Alice).</summary>
    private async Task<Guid> AliceAsync()
    {
        await using var ctx = NewContext();
        return await ctx.MetaverseObjectAttributeValues.AsNoTracking()
            .Where(av => av.AttributeId == _mvDisplayNameAttributeId && av.StringValue == "Alice")
            .Select(av => EF.Property<Guid>(av, "MetaverseObjectId"))
            .SingleAsync();
    }

    private async Task<MetaverseObject> ReadAsync(Guid id)
    {
        await using var ctx = NewContext();
        return await ctx.MetaverseObjects.AsNoTracking().SingleAsync(m => m.Id == id);
    }

    private async Task FlagAsync(Guid id)
    {
        await using var ctx = NewContext();
        await new PostgresDataRepository(ctx).Sync.FlagMetaverseObjectsForScopeReviewAsync([id]);
    }

    private async Task<int> DirectoryAccountCountAsync()
    {
        await using var ctx = NewContext();
        return await ctx.ConnectedSystemObjects.AsNoTracking().CountAsync(c => c.ConnectedSystemId == _directoryId);
    }

    /// <summary>
    /// The cn values staged for Directory accounts, one per staged change: what the Directory will be told.
    /// </summary>
    private async Task<List<string?>> StagedDirectoryNamesAsync()
    {
        await using var ctx = NewContext();
        return await ctx.PendingExportAttributeValueChanges.AsNoTracking()
            .Where(c => c.AttributeId == _directoryDisplayNameAttributeId)
            .OrderBy(c => c.StringValue)
            .Select(c => c.StringValue)
            .ToListAsync();
    }

    /// <summary>
    /// A person's display name edited as the portal or sign-in edits it: through the application layer, on its own
    /// context, in place, recording what changed.
    /// </summary>
    private async Task RenameOutsideSynchronisationAsync(Guid id, string newName)
    {
        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);
        using var jim = new JimApplication(repository, syncRepository: repository.Sync);
        var person = await ctx.MetaverseObjects.AsTracking()
            .Include(m => m.Type)
            .Include(m => m.AttributeValues).ThenInclude(av => av.Attribute)
            .SingleAsync(m => m.Id == id);
        var current = person.AttributeValues.Single(av => av.AttributeId == _mvDisplayNameAttributeId);
        var removal = current.CopyFor(person);
        current.StringValue = newName;
        await jim.Metaverse.UpdateMetaverseObjectAsync(person, additions: [current], removals: [removal], initiatedByType: ActivityInitiatorType.System);
    }

    private async Task<uint> RowVersionAsync(Guid id)
    {
        await using var ctx = NewContext();
        return await ctx.MetaverseObjects.AsNoTracking().Where(m => m.Id == id).Select(m => m.xmin).SingleAsync();
    }

    /// <summary>Wraps the repository so <paramref name="beforeClear"/> runs each time the drain is about to clear its batch.</summary>
    private static Func<ISyncRepository, ISyncRepository> BeforeClear(Action beforeClear) =>
        inner => InterceptingSyncRepositoryProxy.Create(inner, nameof(ISyncRepository.ClearMetaverseObjectScopeReviewPendingAsync), beforeClear);

    #endregion

    [Test]
    public async Task DeltaSync_ReviewThatChangesNothing_LeavesTheObjectsRowAloneAndClearsItsFlagAsync()
    {
        // The answer to "could a review keep its own flag for ever": it would, if reviewing wrote the Metaverse Object, as
        // the row version the clear is guarded by would always have moved. Reviewing an object that needs nothing writes
        // nothing to it.
        await ArrangeProjectedPeopleAsync();
        await AddExportRuleAsync(provisioning: false);
        var alice = await AliceAsync();
        await FlagAsync(alice);
        var versionBeforeTheRun = await RowVersionAsync(alice);

        uint versionAtTheClear = 0;
        await RunAsync(ConnectedSystemRunType.DeltaSynchronisation, BeforeClear(() => versionAtTheClear = RowVersionAsync(alice).GetAwaiter().GetResult()));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(versionAtTheClear, Is.EqualTo(versionBeforeTheRun), "reviewing the object wrote nothing to its row");
            Assert.That((await ReadAsync(alice)).ScopeReviewPending, Is.False, "so the guarded clear applied");
            Assert.That(await DirectoryAccountCountAsync(), Is.Zero, "and the rule does not provision");
        }
    }

    [Test]
    public async Task DeltaSync_ReviewThatProvisions_LeavesTheObjectsRowAloneAndClearsItsFlagAsync()
    {
        // A review that does change something (here, provisioning a Directory account) writes Connected System Objects
        // and Pending Exports, never the Metaverse Object, so its flag clears too.
        await ArrangeProjectedPeopleAsync();
        await AddExportRuleAsync(provisioning: true);
        var alice = await AliceAsync();
        await FlagAsync(alice);
        var versionBeforeTheRun = await RowVersionAsync(alice);

        uint versionAtTheClear = 0;
        await RunAsync(ConnectedSystemRunType.DeltaSynchronisation, BeforeClear(() => versionAtTheClear = RowVersionAsync(alice).GetAwaiter().GetResult()));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(await DirectoryAccountCountAsync(), Is.EqualTo(1), "the review provisioned Alice");
            Assert.That(await StagedDirectoryNamesAsync(), Is.EqualTo(new[] { "Alice" }));
            Assert.That(versionAtTheClear, Is.EqualTo(versionBeforeTheRun), "and wrote nothing to her row");
            Assert.That((await ReadAsync(alice)).ScopeReviewPending, Is.False);
        }
    }

    [Test]
    public async Task DeltaSync_ObjectEditedBetweenTheDrainsLoadAndItsClear_KeepsTheFlagAndTheRecordForTheNextRunAsync()
    {
        // The race the row version guards: a sign-in edits Alice after the drain loaded her and before it cleared her.
        // The review never saw the edit, so the clear must leave both the flag and the record of what changed.
        await ArrangeProjectedPeopleAsync();
        await AddExportRuleAsync(provisioning: true);
        var alice = await AliceAsync();
        await using (var ctx = NewContext())
            await new PostgresDataRepository(ctx).Sync.FlagMetaverseObjectsOfTypeForScopeReviewAsync(_mvTypeId);
        await RunAsync(ConnectedSystemRunType.DeltaSynchronisation);
        Assert.That(await StagedDirectoryNamesAsync(), Is.EquivalentTo(new[] { "Alice", "Bob" }), "precondition: the account for each is provisioned");
        await FlagAsync(alice);

        var edited = false;
        await RunAsync(ConnectedSystemRunType.DeltaSynchronisation, BeforeClear(() =>
        {
            if (edited)
                return;
            edited = true;
            RenameOutsideSynchronisationAsync(alice, "Alice Renamed").GetAwaiter().GetResult();
        }));

        var afterTheRace = await ReadAsync(alice);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(edited, Is.True, "precondition: the edit committed between the load and the clear");
            Assert.That(afterTheRace.ScopeReviewPending, Is.True, "the edit was never reviewed, so the flag stays");
            Assert.That(MetaverseObjectUnsynchronisedChanges.FromJson(afterTheRace.UnsynchronisedChangesJson)?.AttributeIds,
                Is.EqualTo(new[] { _mvDisplayNameAttributeId }), "and so does the record of what changed");
        }

        await RunAsync(ConnectedSystemRunType.DeltaSynchronisation);

        var afterTheNextRun = await ReadAsync(alice);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(afterTheNextRun.ScopeReviewPending, Is.False, "the next run reviewed the edit and cleared it");
            Assert.That(afterTheNextRun.UnsynchronisedChangesJson, Is.Null);
            Assert.That(await StagedDirectoryNamesAsync(), Is.EquivalentTo(new[] { "Alice Renamed", "Bob" }),
                "and delivered it: the rename replaced the name staged for her account");
        }
    }
}
