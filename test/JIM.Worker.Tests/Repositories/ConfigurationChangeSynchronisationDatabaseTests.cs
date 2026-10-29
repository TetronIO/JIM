// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.PostgresData;
using JIM.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NUnit.Framework;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// Real-PostgreSQL verification of the configuration changes awaiting synchronisation (#2615): the table, the claim and
/// apply statements a synchronisation runs, and the paths that delete what the rows hang off. The claim is the part of
/// the design that depends on the database (a READ COMMITTED UPDATE that returns what it claimed), and the in-memory
/// provider can neither run it nor enforce the foreign keys that decide what happens when a Connected System, an Object
/// Type, a Synchronisation Rule or an Activity goes. Opt-in via the same <c>JIM_TEST_RESET_*</c> environment variables
/// as the other RequiresPostgres fixtures.
/// </summary>
[TestFixture]
[Category("RequiresPostgres")]
public class ConfigurationChangeSynchronisationDatabaseTests
{
    private string _connectionString = null!;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        var dbName = Environment.GetEnvironmentVariable("JIM_TEST_RESET_DB");
        if (string.IsNullOrEmpty(dbName))
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL configuration change synchronisation tests.");

        var host = Environment.GetEnvironmentVariable("JIM_TEST_RESET_HOST") ?? "localhost";
        var user = Environment.GetEnvironmentVariable("JIM_TEST_RESET_USER") ?? "postgres";
        var password = Environment.GetEnvironmentVariable("JIM_TEST_RESET_PASSWORD") ?? "postgres";
        var port = Environment.GetEnvironmentVariable("JIM_TEST_RESET_PORT") ?? "5432";
        _connectionString = $"Host={host};Port={port};Database={dbName};Username={user};Password={password}";

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
        int SystemId, int OtherSystemId, int UserTypeId, int ContactTypeId, int OtherSystemTypeId, int SyncRuleId,
        Guid ChangeActivityId, Guid RunActivityId, Guid OtherRunActivityId,
        IReadOnlyDictionary<int, int> ExternalIdAttributeIdByTypeId);

    /// <summary>
    /// Two Connected Systems: the first with User and Contact types and a Synchronisation Rule, the second with a User
    /// type of its own; plus three Activities (the one that saved a change, and two synchronisation runs).
    /// </summary>
    private async Task<Seeded> SeedAsync()
    {
        await using var ctx = NewContext();

        var definition = new ConnectorDefinition { Name = "ccs-definition", BuiltIn = true };
        ctx.ConnectorDefinitions.Add(definition);
        await ctx.SaveChangesAsync();

        var system = new ConnectedSystem { Name = "ccs-system", ConnectorDefinitionId = definition.Id };
        var otherSystem = new ConnectedSystem { Name = "ccs-other-system", ConnectorDefinitionId = definition.Id };
        ctx.ConnectedSystems.AddRange(system, otherSystem);
        await ctx.SaveChangesAsync();

        var userType = new ConnectedSystemObjectType { ConnectedSystemId = system.Id, Name = "user", Selected = true };
        var contactType = new ConnectedSystemObjectType { ConnectedSystemId = system.Id, Name = "contact", Selected = true };
        var otherType = new ConnectedSystemObjectType { ConnectedSystemId = otherSystem.Id, Name = "user", Selected = true };
        ctx.ConnectedSystemObjectTypes.AddRange(userType, contactType, otherType);
        await ctx.SaveChangesAsync();

        var attributeIds = new Dictionary<int, int>();
        foreach (var type in new[] { userType, contactType, otherType })
        {
            var attribute = new ConnectedSystemObjectTypeAttribute
            {
                ConnectedSystemObjectType = type, Name = "id", Type = AttributeDataType.Text,
                AttributePlurality = AttributePlurality.SingleValued, Selected = true, IsExternalId = true
            };
            ctx.ConnectedSystemAttributes.Add(attribute);
            await ctx.SaveChangesAsync();
            attributeIds[type.Id] = attribute.Id;
        }

        var metaverseObjectType = new MetaverseObjectType { Name = "CcsPerson", PluralName = "CcsPeople" };
        ctx.MetaverseObjectTypes.Add(metaverseObjectType);
        await ctx.SaveChangesAsync();

        var rule = new SyncRule
        {
            Name = "ccs-rule", ConnectedSystemId = system.Id, ConnectedSystemObjectTypeId = userType.Id,
            MetaverseObjectTypeId = metaverseObjectType.Id
        };
        ctx.SyncRules.Add(rule);

        Activity NewActivity(string name) => new()
        {
            Id = Guid.NewGuid(),
            TargetName = name,
            TargetType = ActivityTargetType.ConnectedSystemRunProfile,
            TargetOperationType = ActivityTargetOperationType.Execute,
            Status = ActivityStatus.InProgress,
            InitiatedByType = ActivityInitiatorType.System,
            InitiatedByName = "System",
            Executed = DateTime.UtcNow
        };
        var changeActivity = NewActivity("ccs-change");
        var runActivity = NewActivity("ccs-run");
        var otherRunActivity = NewActivity("ccs-other-run");
        ctx.Activities.AddRange(changeActivity, runActivity, otherRunActivity);
        await ctx.SaveChangesAsync();

        return new Seeded(system.Id, otherSystem.Id, userType.Id, contactType.Id, otherType.Id, rule.Id,
            changeActivity.Id, runActivity.Id, otherRunActivity.Id, attributeIds);
    }

    /// <summary>
    /// Records a change as the portal does: through the application-facing repository, on a no-tracking context.
    /// </summary>
    private async Task<ConfigurationChangeSynchronisation> CreateChangeAsync(
        int systemId, int typeId, string reason = "Import rule scope changed", Guid? changeActivityId = null, int? syncRuleId = null)
    {
        await using var ctx = NewContext(QueryTrackingBehavior.NoTracking);
        var change = new ConfigurationChangeSynchronisation
        {
            ConnectedSystemId = systemId,
            ConnectedSystemObjectTypeId = typeId,
            Reason = reason,
            ConfigurationChangeActivityId = changeActivityId,
            SyncRuleId = syncRuleId
        };
        await new PostgresDataRepository(ctx).ConnectedSystems.CreateConfigurationChangeSynchronisationAsync(change);
        return change;
    }

    private async Task<List<ConfigurationChangeSynchronisation>> ReadAllAsync()
    {
        await using var ctx = NewContext(QueryTrackingBehavior.NoTracking);
        return await ctx.ConfigurationChangeSynchronisations.OrderBy(c => c.Id).ToListAsync();
    }

    /// <summary>
    /// Claims as the worker does: read the ids awaiting synchronisation first, then claim only those. A test supplies
    /// <paramref name="eligibleIds"/> to claim against ids read earlier.
    /// </summary>
    private async Task<IReadOnlyList<ClaimedConfigurationChangeSynchronisation>> ClaimAsync(
        int systemId, Guid runActivityId, IReadOnlyCollection<long>? eligibleIds = null)
    {
        await using var ctx = NewContext();
        var sync = new PostgresDataRepository(ctx).Sync;
        eligibleIds ??= await sync.GetConfigurationChangeSynchronisationIdsAwaitingAsync(systemId);
        return await sync.ClaimConfigurationChangeSynchronisationsAsync(systemId, eligibleIds, runActivityId);
    }

    private async Task<IReadOnlyList<long>> ReadAwaitingIdsAsync(int systemId)
    {
        await using var ctx = NewContext();
        return await new PostgresDataRepository(ctx).Sync.GetConfigurationChangeSynchronisationIdsAwaitingAsync(systemId);
    }

    private async Task<int> ApplyAsync(Guid runActivityId)
    {
        await using var ctx = NewContext();
        return await new PostgresDataRepository(ctx).Sync.ApplyClaimedConfigurationChangeSynchronisationsAsync(runActivityId);
    }

    [Test]
    public async Task CreateClaimAndApply_FullyPopulatedChange_RoundTripsEveryColumnAsync()
    {
        var s = await SeedAsync();
        var before = DateTime.UtcNow.AddSeconds(-1);

        var created = await CreateChangeAsync(s.SystemId, s.UserTypeId, "User Import: scope changed (Sync-affecting)", s.ChangeActivityId, s.SyncRuleId);
        Assert.That(created.Id, Is.Positive, "the key is generated");

        var awaiting = (await ReadAllAsync()).Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(awaiting.Id, Is.EqualTo(created.Id));
            Assert.That(awaiting.ConnectedSystemId, Is.EqualTo(s.SystemId));
            Assert.That(awaiting.ConnectedSystemObjectTypeId, Is.EqualTo(s.UserTypeId));
            Assert.That(awaiting.RequestedAt.ToUniversalTime(), Is.GreaterThan(before).And.LessThan(DateTime.UtcNow.AddSeconds(1)));
            Assert.That(awaiting.ConfigurationChangeActivityId, Is.EqualTo(s.ChangeActivityId));
            Assert.That(awaiting.SyncRuleId, Is.EqualTo(s.SyncRuleId));
            Assert.That(awaiting.Reason, Is.EqualTo("User Import: scope changed (Sync-affecting)"));
            Assert.That(awaiting.ClaimedByActivityId, Is.Null, "a new change is not claimed");
            Assert.That(awaiting.AppliedAt, Is.Null, "a new change is awaiting synchronisation");
        }

        var claimed = await ClaimAsync(s.SystemId, s.RunActivityId);
        Assert.That(claimed, Is.EqualTo(new[] { new ClaimedConfigurationChangeSynchronisation(created.Id, s.UserTypeId) }));
        Assert.That((await ReadAllAsync()).Single().ClaimedByActivityId, Is.EqualTo(s.RunActivityId));

        var appliedFrom = DateTime.UtcNow.AddSeconds(-1);
        Assert.That(await ApplyAsync(s.RunActivityId), Is.EqualTo(1));

        var applied = (await ReadAllAsync()).Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(applied.AppliedAt, Is.Not.Null);
            Assert.That(applied.AppliedAt!.Value.ToUniversalTime(), Is.GreaterThan(appliedFrom).And.LessThan(DateTime.UtcNow.AddSeconds(1)));
            Assert.That(applied.ClaimedByActivityId, Is.EqualTo(s.RunActivityId), "the applying run stays recorded");
            Assert.That(applied.Reason, Is.EqualTo(awaiting.Reason));
            Assert.That(applied.SyncRuleId, Is.EqualTo(s.SyncRuleId));
            Assert.That(applied.ConfigurationChangeActivityId, Is.EqualTo(s.ChangeActivityId));
            Assert.That(applied.RequestedAt, Is.EqualTo(awaiting.RequestedAt), "the apply writes nothing but the applied time");
        }
    }

    [Test]
    public async Task CreateConfigurationChangeSynchronisationAsync_RowRefusedByTheDatabase_LeavesNothingStagedForTheNextSaveAsync()
    {
        // The save that asked for the row then fails its Activity on this same context. A refused row left Added in the
        // change tracker would be sent again by that write (and every later one on the context), failing it the same way
        // and burying the Activity's failure under a second exception.
        var s = await SeedAsync();
        await using var ctx = NewContext(QueryTrackingBehavior.NoTracking);
        var repository = new PostgresDataRepository(ctx).ConnectedSystems;
        const int objectTypeThatDoesNotExist = int.MaxValue;

        Assert.ThrowsAsync<DbUpdateException>(() => repository.CreateConfigurationChangeSynchronisationAsync(new ConfigurationChangeSynchronisation
            { ConnectedSystemId = s.SystemId, ConnectedSystemObjectTypeId = objectTypeThatDoesNotExist, Reason = "refused" }));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(ctx.ChangeTracker.Entries<ConfigurationChangeSynchronisation>(), Is.Empty, "the refused row is no longer tracked");
            Assert.That(async () => await ctx.SaveChangesAsync(), Throws.Nothing, "a later write on the context is not poisoned by it");
        }

        await repository.CreateConfigurationChangeSynchronisationAsync(new ConfigurationChangeSynchronisation
            { ConnectedSystemId = s.SystemId, ConnectedSystemObjectTypeId = s.UserTypeId, Reason = "accepted" });
        Assert.That((await ReadAllAsync()).Select(c => c.Reason), Is.EqualTo(new[] { "accepted" }));
    }

    [Test]
    public async Task CreateConfigurationChangeSynchronisationAsync_ChangeAlreadyClaimedOrApplied_IsRefusedAsync()
    {
        var s = await SeedAsync();
        await using var ctx = NewContext(QueryTrackingBehavior.NoTracking);
        var repository = new PostgresDataRepository(ctx).ConnectedSystems;

        Assert.ThrowsAsync<ArgumentException>(() => repository.CreateConfigurationChangeSynchronisationAsync(new ConfigurationChangeSynchronisation
            { ConnectedSystemId = s.SystemId, ConnectedSystemObjectTypeId = s.UserTypeId, ClaimedByActivityId = s.RunActivityId }));
        Assert.ThrowsAsync<ArgumentException>(() => repository.CreateConfigurationChangeSynchronisationAsync(new ConfigurationChangeSynchronisation
            { ConnectedSystemId = s.SystemId, ConnectedSystemObjectTypeId = s.UserTypeId, AppliedAt = DateTime.UtcNow }));
        Assert.That(await ReadAllAsync(), Is.Empty);
    }

    [Test]
    public async Task ClaimConfigurationChangeSynchronisationsAsync_ClaimsOnlyRowsAwaitingSynchronisationForThatSystemAsync()
    {
        var s = await SeedAsync();
        var userChange = await CreateChangeAsync(s.SystemId, s.UserTypeId);
        var contactChange = await CreateChangeAsync(s.SystemId, s.ContactTypeId);
        var alreadyApplied = await CreateChangeAsync(s.SystemId, s.UserTypeId);
        var otherSystemChange = await CreateChangeAsync(s.OtherSystemId, s.OtherSystemTypeId);

        // One row already applied by an earlier run.
        await ClaimAsync(s.SystemId, s.OtherRunActivityId);
        await ApplyAsync(s.OtherRunActivityId);
        var laterChange = await CreateChangeAsync(s.SystemId, s.ContactTypeId);

        var claimed = await ClaimAsync(s.SystemId, s.RunActivityId);

        var rows = (await ReadAllAsync()).ToDictionary(r => r.Id);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(claimed.Select(c => c.Id), Is.EquivalentTo(new[] { laterChange.Id }),
                "only the row saved since the earlier run's claim: the other three were applied by it");
            Assert.That(rows[laterChange.Id].ClaimedByActivityId, Is.EqualTo(s.RunActivityId));
            Assert.That(rows[userChange.Id].ClaimedByActivityId, Is.EqualTo(s.OtherRunActivityId), "an applied row is not reclaimed");
            Assert.That(rows[otherSystemChange.Id].ClaimedByActivityId, Is.Null, "another system's row is not claimed");
            Assert.That(rows[otherSystemChange.Id].AppliedAt, Is.Null);
            Assert.That(new[] { userChange.Id, contactChange.Id, alreadyApplied.Id }, Has.All.Matches<long>(id => rows[id].AppliedAt != null));
        }
    }

    [Test]
    public async Task ClaimConfigurationChangeSynchronisationsAsync_ReturnsEachClaimedRowWithItsTypeAsync()
    {
        var s = await SeedAsync();
        var first = await CreateChangeAsync(s.SystemId, s.UserTypeId);
        var second = await CreateChangeAsync(s.SystemId, s.UserTypeId);
        var third = await CreateChangeAsync(s.SystemId, s.ContactTypeId);

        var claimed = await ClaimAsync(s.SystemId, s.RunActivityId);

        Assert.That(claimed.Select(c => (c.Id, c.ConnectedSystemObjectTypeId)),
            Is.EquivalentTo(new[] { (first.Id, s.UserTypeId), (second.Id, s.UserTypeId), (third.Id, s.ContactTypeId) }));
    }

    [Test]
    public async Task ClaimConfigurationChangeSynchronisationsAsync_NothingAwaiting_ReturnsNothingAsync()
    {
        var s = await SeedAsync();

        Assert.That(await ClaimAsync(s.SystemId, s.RunActivityId), Is.Empty);
    }

    [Test]
    public async Task ClaimConfigurationChangeSynchronisationsAsync_RowSavedAfterTheEligibleIdsWereRead_IsNotClaimedAndIsClaimedByTheNextRunAsync()
    {
        var s = await SeedAsync();
        var before = await CreateChangeAsync(s.SystemId, s.UserTypeId);
        var eligible = await ReadAwaitingIdsAsync(s.SystemId);
        // Committed after the worker read the ids and before the run claims: it may not be in what the run loaded.
        var after = await CreateChangeAsync(s.SystemId, s.UserTypeId);

        var claimed = await ClaimAsync(s.SystemId, s.RunActivityId, eligible);

        var rows = (await ReadAllAsync()).ToDictionary(r => r.Id);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(claimed.Select(c => c.Id), Is.EqualTo(new[] { before.Id }));
            Assert.That(rows[after.Id].ClaimedByActivityId, Is.Null, "the later row is not claimed");
            Assert.That(rows[after.Id].AppliedAt, Is.Null);
        }

        await ApplyAsync(s.RunActivityId);
        var next = await ClaimAsync(s.SystemId, s.OtherRunActivityId);
        Assert.That(next.Select(c => c.Id), Is.EqualTo(new[] { after.Id }), "the next run reads the ids afresh and claims it");
    }

    [Test]
    public async Task ClaimConfigurationChangeSynchronisationsAsync_NoEligibleIds_ClaimsNothingAsync()
    {
        var s = await SeedAsync();
        await CreateChangeAsync(s.SystemId, s.UserTypeId);

        var claimed = await ClaimAsync(s.SystemId, s.RunActivityId, []);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(claimed, Is.Empty);
            Assert.That((await ReadAllAsync()).Single().ClaimedByActivityId, Is.Null);
        }
    }

    [Test]
    public async Task ClaimConfigurationChangeSynchronisationsAsync_EligibleIdsOfAppliedRowsAndOtherSystems_AreNotClaimedAsync()
    {
        var s = await SeedAsync();
        var appliedRow = await CreateChangeAsync(s.SystemId, s.UserTypeId);
        await ClaimAsync(s.SystemId, s.OtherRunActivityId);
        await ApplyAsync(s.OtherRunActivityId);
        var otherSystemRow = await CreateChangeAsync(s.OtherSystemId, s.OtherSystemTypeId);
        var mine = await CreateChangeAsync(s.SystemId, s.ContactTypeId);

        // Every id is offered, but only this system's row still awaiting synchronisation can be claimed.
        var claimed = await ClaimAsync(s.SystemId, s.RunActivityId, [appliedRow.Id, otherSystemRow.Id, mine.Id]);

        var rows = (await ReadAllAsync()).ToDictionary(r => r.Id);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(claimed.Select(c => c.Id), Is.EqualTo(new[] { mine.Id }));
            Assert.That(rows[appliedRow.Id].ClaimedByActivityId, Is.EqualTo(s.OtherRunActivityId), "an applied row keeps its applying run");
            Assert.That(rows[otherSystemRow.Id].ClaimedByActivityId, Is.Null, "another system's row is not claimed even if its id is offered");
        }
    }

    [Test]
    public async Task GetConfigurationChangeSynchronisationIdsAwaitingAsync_ReturnsOnlyTheSystemsRowsAwaitingSynchronisationAsync()
    {
        var s = await SeedAsync();
        var first = await CreateChangeAsync(s.SystemId, s.UserTypeId);
        var second = await CreateChangeAsync(s.SystemId, s.ContactTypeId);
        await CreateChangeAsync(s.OtherSystemId, s.OtherSystemTypeId);
        await ClaimAsync(s.SystemId, s.OtherRunActivityId, [first.Id]);
        await ApplyAsync(s.OtherRunActivityId); // applies only the first
        var third = await CreateChangeAsync(s.SystemId, s.UserTypeId);

        var ids = await ReadAwaitingIdsAsync(s.SystemId);

        Assert.That(ids, Is.EquivalentTo(new[] { second.Id, third.Id }), "applied rows and other systems' rows are not awaiting this system");
        Assert.That((await ReadAllAsync()).Count(r => r.ClaimedByActivityId != null), Is.EqualTo(1), "reading claims nothing");
    }

    [Test]
    public async Task ClaimConfigurationChangeSynchronisationsAsync_RowClaimedByARunThatNeverApplied_IsReclaimedAsync()
    {
        var s = await SeedAsync();
        var change = await CreateChangeAsync(s.SystemId, s.UserTypeId);
        await ClaimAsync(s.SystemId, s.OtherRunActivityId); // that run failed or was cancelled: it never applied

        var claimed = await ClaimAsync(s.SystemId, s.RunActivityId);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(claimed.Select(c => c.Id), Is.EqualTo(new[] { change.Id }));
            Assert.That((await ReadAllAsync()).Single().ClaimedByActivityId, Is.EqualTo(s.RunActivityId), "the earlier claim is overwritten");
        }
    }

    [Test]
    public async Task ApplyClaimedConfigurationChangeSynchronisationsAsync_AppliesOnlyTheRunsOwnClaimedRowsAsync()
    {
        var s = await SeedAsync();
        var mine = await CreateChangeAsync(s.SystemId, s.UserTypeId);
        var mineToo = await CreateChangeAsync(s.SystemId, s.UserTypeId);
        await ClaimAsync(s.SystemId, s.RunActivityId);
        var savedAfterTheClaim = await CreateChangeAsync(s.SystemId, s.UserTypeId);
        var otherSystem = await CreateChangeAsync(s.OtherSystemId, s.OtherSystemTypeId);
        await ClaimAsync(s.OtherSystemId, s.OtherRunActivityId);

        var applied = await ApplyAsync(s.RunActivityId);

        var rows = (await ReadAllAsync()).ToDictionary(r => r.Id);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(applied, Is.EqualTo(2));
            Assert.That(rows[mine.Id].AppliedAt, Is.Not.Null);
            Assert.That(rows[mineToo.Id].AppliedAt, Is.Not.Null);
            Assert.That(rows[savedAfterTheClaim.Id].AppliedAt, Is.Null, "a change the run did not claim stays awaiting synchronisation");
            Assert.That(rows[otherSystem.Id].AppliedAt, Is.Null, "another run's claimed row is not this run's to apply");
        }

        Assert.That(await ApplyAsync(s.RunActivityId), Is.Zero, "applying again applies nothing");
    }

    [Test]
    public async Task ApplyClaimedConfigurationChangeSynchronisationsAsync_RowReclaimedByAnotherRun_IsLeftForThatRunAsync()
    {
        var s = await SeedAsync();
        await CreateChangeAsync(s.SystemId, s.UserTypeId);
        await ClaimAsync(s.SystemId, s.RunActivityId);
        await ClaimAsync(s.SystemId, s.OtherRunActivityId); // a second run overlapped and claimed the same row

        var firstRunApplied = await ApplyAsync(s.RunActivityId);
        var rowAfterFirst = (await ReadAllAsync()).Single();
        var secondRunApplied = await ApplyAsync(s.OtherRunActivityId);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(firstRunApplied, Is.Zero, "the run whose claim was overwritten applies nothing");
            Assert.That(rowAfterFirst.AppliedAt, Is.Null);
            Assert.That(secondRunApplied, Is.EqualTo(1), "the run that holds the claim applies it");
        }
    }

    [Test]
    public async Task ClaimConfigurationChangeSynchronisationsAsync_TwoRunsClaimingAtOnce_EveryRowIsAppliedByExactlyOneOfThemAsync()
    {
        var s = await SeedAsync();
        const int rowCount = 200;
        for (var i = 0; i < rowCount; i++)
            await CreateChangeAsync(s.SystemId, i % 2 == 0 ? s.UserTypeId : s.ContactTypeId);

        // Two overlapping synchronisations of the one system, each on its own connection: whichever statement runs
        // second waits for the first's row locks and then overwrites its claim, so every row ends with a single claimer.
        await Task.WhenAll(ClaimAsync(s.SystemId, s.RunActivityId), ClaimAsync(s.SystemId, s.OtherRunActivityId));
        var appliedByFirst = await ApplyAsync(s.RunActivityId);
        var appliedBySecond = await ApplyAsync(s.OtherRunActivityId);

        var rows = await ReadAllAsync();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(appliedByFirst + appliedBySecond, Is.EqualTo(rowCount), "no row is lost or applied twice");
            Assert.That(rows.Select(r => r.AppliedAt), Has.All.Not.Null);
            Assert.That(rows.Select(r => r.ClaimedByActivityId), Has.All.Matches<Guid?>(id => id == s.RunActivityId || id == s.OtherRunActivityId));
        }
    }

    [Test]
    public async Task GetConnectedSystemObjectTypeIdsAwaitingSynchronisationAsync_ReturnsTheTypesWithoutClaimingAnythingAsync()
    {
        var s = await SeedAsync();
        await CreateChangeAsync(s.SystemId, s.UserTypeId);
        await CreateChangeAsync(s.SystemId, s.UserTypeId);
        await CreateChangeAsync(s.OtherSystemId, s.OtherSystemTypeId);
        await CreateChangeAsync(s.SystemId, s.ContactTypeId);
        await ClaimAsync(s.SystemId, s.OtherRunActivityId);
        await ApplyAsync(s.OtherRunActivityId); // applies the system's three rows above
        await CreateChangeAsync(s.SystemId, s.UserTypeId);

        await using var ctx = NewContext();
        var awaiting = await new PostgresDataRepository(ctx).Sync.GetConnectedSystemObjectTypeIdsAwaitingSynchronisationAsync(s.SystemId);

        var rows = await ReadAllAsync();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(awaiting, Is.EquivalentTo(new[] { s.UserTypeId }), "only the User change saved since: the Contact change is applied, and the other system's is not this system's");
            Assert.That(rows.Count(r => r.ClaimedByActivityId == null), Is.EqualTo(2),
                "reading claimed nothing: the new User change and the other system's change are still unclaimed");
        }
    }

    [Test]
    public async Task ClaimAndApply_ContextTrackingTheRows_BringsTheTrackedInstancesIntoLineAndWritesNothingBackAsync()
    {
        // The worker's context lives for the whole run and tracks by default. If anything on it holds these rows, the
        // raw claim and apply must leave the instances reading as persisted, or a later SaveChanges writes stale values.
        var s = await SeedAsync();
        var created = await CreateChangeAsync(s.SystemId, s.UserTypeId);

        await using var ctx = NewContext();
        var tracked = await ctx.ConfigurationChangeSynchronisations.SingleAsync(c => c.Id == created.Id);
        var sync = new PostgresDataRepository(ctx).Sync;

        await sync.ClaimConfigurationChangeSynchronisationsAsync(s.SystemId, [created.Id], s.RunActivityId);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(tracked.ClaimedByActivityId, Is.EqualTo(s.RunActivityId), "the tracked instance reads as claimed");
            Assert.That(ctx.Entry(tracked).State, Is.EqualTo(EntityState.Unchanged), "and as persisted, not as a pending change");
        }

        await sync.ApplyClaimedConfigurationChangeSynchronisationsAsync(s.RunActivityId);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(tracked.AppliedAt, Is.Not.Null, "the tracked instance reads as applied");
            Assert.That(ctx.Entry(tracked).State, Is.EqualTo(EntityState.Unchanged));
        }

        Assert.That(await ctx.SaveChangesAsync(), Is.Zero, "nothing is left to write back");
        var row = (await ReadAllAsync()).Single();
        Assert.That(row.AppliedAt!.Value.ToUniversalTime(), Is.EqualTo(tracked.AppliedAt!.Value.ToUniversalTime()).Within(TimeSpan.FromMilliseconds(1)),
            "the tracked value is the stored one");
    }

    [Test]
    public async Task GetConnectedSystemObjectModifiedSinceOrOfTypesCountAsync_CountsTheObjectsTheDeltaLoadersPageAsync()
    {
        var s = await SeedAsync();
        var watermark = DateTime.UtcNow.AddHours(-1);
        var longAgo = DateTime.UtcNow.AddHours(-3);
        // Users: three untouched, one modified. Contacts: three untouched, one modified. The other system's are never counted.
        await SeedObjectsAsync(s, s.UserTypeId, 3, longAgo, lastUpdated: null);
        await SeedObjectsAsync(s, s.UserTypeId, 1, longAgo, lastUpdated: DateTime.UtcNow);
        await SeedObjectsAsync(s, s.ContactTypeId, 3, longAgo, lastUpdated: null);
        await SeedObjectsAsync(s, s.ContactTypeId, 1, longAgo, lastUpdated: DateTime.UtcNow);
        await SeedObjectsAsync(s, s.OtherSystemTypeId, 2, longAgo, lastUpdated: null, systemId: s.OtherSystemId);

        await using var ctx = NewContext();
        var sync = new PostgresDataRepository(ctx).Sync;

        var modifiedOnly = await sync.GetConnectedSystemObjectModifiedSinceCountAsync(s.SystemId, watermark);
        var noTypes = await sync.GetConnectedSystemObjectModifiedSinceOrOfTypesCountAsync(s.SystemId, watermark, []);
        var usersInFull = await sync.GetConnectedSystemObjectModifiedSinceOrOfTypesCountAsync(s.SystemId, watermark, [s.UserTypeId]);
        var bothInFull = await sync.GetConnectedSystemObjectModifiedSinceOrOfTypesCountAsync(s.SystemId, watermark, [s.UserTypeId, s.ContactTypeId]);

        // What the delta loaders hand out for the same selection: every User, and only the modified Contacts.
        var usersPaged = (await sync.GetConnectedSystemObjectsAsync(s.SystemId, 1, 100, objectTypeId: s.UserTypeId)).Results.Count;
        var contactsPaged = (await sync.GetConnectedSystemObjectsModifiedSinceAsync(s.SystemId, watermark, 1, 100, objectTypeId: s.ContactTypeId)).Results.Count;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(modifiedOnly, Is.EqualTo(2));
            Assert.That(noTypes, Is.EqualTo(modifiedOnly), "no types counts exactly as the plain modified-since count");
            Assert.That(usersInFull, Is.EqualTo(4 + 1), "every User, and the modified Contact; a modified User counts once");
            Assert.That(usersInFull, Is.EqualTo(usersPaged + contactsPaged), "the estimate matches what the loaders page");
            Assert.That(bothInFull, Is.EqualTo(8));
        }
    }

    [Test]
    public async Task DeleteConnectedSystemAsync_RemovesItsRowsAndLeavesOtherSystemsAsync()
    {
        var s = await SeedAsync();
        await CreateChangeAsync(s.SystemId, s.UserTypeId);
        await ClaimAsync(s.SystemId, s.RunActivityId);
        await ApplyAsync(s.RunActivityId);
        await CreateChangeAsync(s.SystemId, s.ContactTypeId, changeActivityId: s.ChangeActivityId, syncRuleId: s.SyncRuleId);
        var otherSystemChange = await CreateChangeAsync(s.OtherSystemId, s.OtherSystemTypeId);

        await using (var ctx = NewContext())
            await new PostgresDataRepository(ctx).ConnectedSystems.DeleteConnectedSystemAsync(s.SystemId);

        Assert.That((await ReadAllAsync()).Select(r => r.Id), Is.EqualTo(new[] { otherSystemChange.Id }),
            "applied and awaiting rows both go with the system");
    }

    [Test]
    public async Task ResetSystemAsync_ChangesAwaitingSynchronisationAndApplied_AreRemovedWithTheirConnectedSystemsAsync()
    {
        var s = await SeedAsync();
        await CreateChangeAsync(s.SystemId, s.UserTypeId, changeActivityId: s.ChangeActivityId, syncRuleId: s.SyncRuleId);
        await ClaimAsync(s.SystemId, s.RunActivityId);
        await ApplyAsync(s.RunActivityId);
        await CreateChangeAsync(s.OtherSystemId, s.OtherSystemTypeId);

        await using (var ctx = NewContext())
            await new PostgresDataRepository(ctx).System.ResetSystemAsync(includeAdministrators: false);

        Assert.That(await ReadAllAsync(), Is.Empty, "the factory reset truncates the Connected Systems, and the rows go with them");
    }

    [Test]
    public async Task DeleteConnectedSystemObjectType_RemovesOnlyThatTypesRowsAsync()
    {
        var s = await SeedAsync();
        await CreateChangeAsync(s.SystemId, s.UserTypeId);
        await CreateChangeAsync(s.SystemId, s.UserTypeId);
        var contactChange = await CreateChangeAsync(s.SystemId, s.ContactTypeId);

        // A schema refresh removes a type that has gone from the Connector, with its attributes.
        await using (var ctx = NewContext())
        {
            await ctx.Database.ExecuteSqlRawAsync(@"DELETE FROM ""ConnectedSystemAttributes"" WHERE ""ConnectedSystemObjectTypeId"" = {0}", s.UserTypeId);
            await ctx.Database.ExecuteSqlRawAsync(@"DELETE FROM ""SyncRules"" WHERE ""ConnectedSystemObjectTypeId"" = {0}", s.UserTypeId);
            await ctx.Database.ExecuteSqlRawAsync(@"DELETE FROM ""ConnectedSystemObjectTypes"" WHERE ""Id"" = {0}", s.UserTypeId);
        }

        Assert.That((await ReadAllAsync()).Select(r => r.Id), Is.EqualTo(new[] { contactChange.Id }));
    }

    [Test]
    public async Task DeleteSyncRuleAsync_RuleNamedByAChange_NullsTheReferenceAndKeepsTheRowAndItsReasonAsync()
    {
        var s = await SeedAsync();
        await CreateChangeAsync(s.SystemId, s.UserTypeId, "User Import: scope changed", s.ChangeActivityId, s.SyncRuleId);

        await using (var ctx = NewContext())
        {
            var rule = await ctx.SyncRules.SingleAsync(r => r.Id == s.SyncRuleId);
            await new PostgresDataRepository(ctx).ConnectedSystems.DeleteSyncRuleAsync(rule);
        }

        var row = (await ReadAllAsync()).Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(row.SyncRuleId, Is.Null, "history outlives the rule it names");
            Assert.That(row.Reason, Is.EqualTo("User Import: scope changed"), "and still reads");
            Assert.That(row.ConfigurationChangeActivityId, Is.EqualTo(s.ChangeActivityId));
        }
    }

    [Test]
    public async Task DeleteActivity_ActivityNamedByAChange_NullsTheReferencesAndKeepsTheRowAsync()
    {
        var s = await SeedAsync();
        await CreateChangeAsync(s.SystemId, s.UserTypeId, changeActivityId: s.ChangeActivityId);
        await ClaimAsync(s.SystemId, s.RunActivityId);
        await ApplyAsync(s.RunActivityId);

        await using (var ctx = NewContext())
            await ctx.Database.ExecuteSqlRawAsync(
                @"DELETE FROM ""Activities"" WHERE ""Id"" IN ({0}, {1})", s.ChangeActivityId, s.RunActivityId);

        var row = (await ReadAllAsync()).Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(row.ConfigurationChangeActivityId, Is.Null, "retention removing the saving Activity leaves the row");
            Assert.That(row.ClaimedByActivityId, Is.Null, "and so does removing the applying run's Activity");
            Assert.That(row.AppliedAt, Is.Not.Null, "the change is still recorded as applied");
        }
    }

    /// <summary>
    /// Adds Connected System Objects of one type, all created <paramref name="created"/> and last updated
    /// <paramref name="lastUpdated"/>.
    /// </summary>
    private async Task SeedObjectsAsync(Seeded s, int typeId, int count, DateTime created, DateTime? lastUpdated, int? systemId = null)
    {
        await using var ctx = NewContext();
        for (var i = 0; i < count; i++)
        {
            ctx.ConnectedSystemObjects.Add(new ConnectedSystemObject
            {
                Id = Guid.NewGuid(),
                ConnectedSystemId = systemId ?? s.SystemId,
                TypeId = typeId,
                ExternalIdAttributeId = s.ExternalIdAttributeIdByTypeId[typeId],
                Status = ConnectedSystemObjectStatus.Normal,
                JoinType = ConnectedSystemObjectJoinType.NotJoined,
                Created = created,
                LastUpdated = lastUpdated
            });
        }

        await ctx.SaveChangesAsync();
    }
}
