// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Staging;
using JIM.PostgresData;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// Real-PostgreSQL verification of the auxiliary class data model: an administrator's selections survive a save,
/// the constraints that protect them are actually in the database, and the cascade behaviour on schema refresh is
/// what the refresh's documented data-loss semantics promise.
/// </summary>
/// <remarks>
/// Every assertion here is one the EF Core in-memory provider structurally cannot make. It enforces no unique
/// index, so a duplicate selection would pass; it enforces no foreign key, so a cross-system pairing would pass;
/// it applies no database cascade, so a removed auxiliary class would leave its selections behind; and it tracks
/// by default, so a mutating path that lost its <c>AsTracking</c> would silently do nothing while reporting
/// success. The context here is configured <c>NoTracking</c> to match JIM.Web and JIM.Scheduler.
///
/// Opt-in via the same <c>JIM_TEST_RESET_*</c> environment variables as the other database tests; ignored when
/// <c>JIM_TEST_RESET_DB</c> is absent.
/// </remarks>
[TestFixture]
[Category("RequiresPostgres")]
public class AuxiliaryClassExtensionPersistenceDatabaseTests
{
    private string _connectionString = null!;

    private JimDbContext NewContext()
    {
        var options = new DbContextOptionsBuilder<JimDbContext>()
            .UseNpgsql(_connectionString)
            .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;
        return new JimDbContext(options);
    }

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        var dbName = Environment.GetEnvironmentVariable("JIM_TEST_RESET_DB");
        if (string.IsNullOrEmpty(dbName))
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL auxiliary class persistence tests.");

        var host = Environment.GetEnvironmentVariable("JIM_TEST_RESET_HOST") ?? "localhost";
        var user = Environment.GetEnvironmentVariable("JIM_TEST_RESET_USER") ?? "postgres";
        var pass = Environment.GetEnvironmentVariable("JIM_TEST_RESET_PASSWORD") ?? "postgres";
        var port = Environment.GetEnvironmentVariable("JIM_TEST_RESET_PORT") ?? "5432";
        _connectionString = $"Host={host};Port={port};Database={dbName};Username={user};Password={pass}";

        using var ctx = NewContext();
        ctx.Database.Migrate();
    }

    [SetUp]
    public async Task SetUp()
    {
        await using var ctx = NewContext();
        await ctx.Database.ExecuteSqlRawAsync(@"
            DO $$
            DECLARE r RECORD;
            BEGIN
                FOR r IN (SELECT tablename FROM pg_tables WHERE schemaname = 'public' AND tablename <> '__EFMigrationsHistory') LOOP
                    EXECUTE 'TRUNCATE TABLE ""' || r.tablename || '"" RESTART IDENTITY CASCADE';
                END LOOP;
            END $$;");
    }

    #region Object Type extensions

    [Test]
    public async Task AddObjectTypeExtensionAsync_RecordsTheSelectionAsync()
    {
        var schema = await SeedAsync();

        await using (var ctx = NewContext())
            Assert.That(await new PostgresDataRepository(ctx).ConnectedSystems
                .AddObjectTypeExtensionAsync(schema.PersonId, schema.PosixAccountId), Is.True);

        await using var readContext = NewContext();
        var extensions = await new PostgresDataRepository(readContext).ConnectedSystems
            .GetObjectTypeExtensionsAsync(schema.ConnectedSystemId);

        Assert.That(extensions, Has.Count.EqualTo(1));
        Assert.Multiple(() =>
        {
            Assert.That(extensions[0].BaseObjectType.Name, Is.EqualTo("inetOrgPerson"));
            Assert.That(extensions[0].ExtensionObjectType.Name, Is.EqualTo("posixAccount"));
        });
    }

    [Test]
    public async Task AddObjectTypeExtensionAsync_WhenTheSelectionAlreadyExists_DoesNotDuplicateItAsync()
    {
        // The unique index would turn a second insert into a constraint violation, so re-asserting a selection has
        // to be a no-op rather than something a caller must check for first.
        var schema = await SeedAsync();

        await using (var ctx = NewContext())
            await new PostgresDataRepository(ctx).ConnectedSystems.AddObjectTypeExtensionAsync(schema.PersonId, schema.PosixAccountId);

        bool addedAgain;
        await using (var ctx = NewContext())
            addedAgain = await new PostgresDataRepository(ctx).ConnectedSystems
                .AddObjectTypeExtensionAsync(schema.PersonId, schema.PosixAccountId);

        await using var readContext = NewContext();
        var extensions = await new PostgresDataRepository(readContext).ConnectedSystems
            .GetObjectTypeExtensionsAsync(schema.ConnectedSystemId);

        Assert.Multiple(() =>
        {
            Assert.That(addedAgain, Is.False, "Re-asserting an existing selection must report that nothing was added.");
            Assert.That(extensions, Has.Count.EqualTo(1));
        });
    }

    [Test]
    public async Task AddObjectTypeExtensionAsync_ForATypeInAnotherConnectedSystem_IsRefusedAsync()
    {
        // Merging one directory's schema into another's is meaningless, and would corrupt the merged type.
        var schema = await SeedAsync();
        var otherSystem = await SeedAsync("Other System");

        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);

        Assert.That(async () => await repository.ConnectedSystems.AddObjectTypeExtensionAsync(schema.PersonId, otherSystem.PosixAccountId),
            Throws.ArgumentException);
    }

    [Test]
    public async Task AddObjectTypeExtensionAsync_ForATypeExtendingItself_IsRefusedAsync()
    {
        var schema = await SeedAsync();

        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);

        Assert.That(async () => await repository.ConnectedSystems.AddObjectTypeExtensionAsync(schema.PersonId, schema.PersonId),
            Throws.ArgumentException);
    }

    [Test]
    public async Task RemoveObjectTypeExtensionAsync_WithdrawsTheSelectionAsync()
    {
        var schema = await SeedAsync();

        await using (var ctx = NewContext())
            await new PostgresDataRepository(ctx).ConnectedSystems.AddObjectTypeExtensionAsync(schema.PersonId, schema.PosixAccountId);

        bool removed;
        await using (var ctx = NewContext())
            removed = await new PostgresDataRepository(ctx).ConnectedSystems
                .RemoveObjectTypeExtensionAsync(schema.PersonId, schema.PosixAccountId);

        await using var readContext = NewContext();
        var extensions = await new PostgresDataRepository(readContext).ConnectedSystems
            .GetObjectTypeExtensionsAsync(schema.ConnectedSystemId);

        Assert.Multiple(() =>
        {
            Assert.That(removed, Is.True);
            Assert.That(extensions, Is.Empty);
        });
    }

    [Test]
    public async Task RemoveObjectTypeExtensionAsync_WhenThereIsNothingToRemove_ReportsSoAsync()
    {
        var schema = await SeedAsync();

        await using var ctx = NewContext();
        var removed = await new PostgresDataRepository(ctx).ConnectedSystems
            .RemoveObjectTypeExtensionAsync(schema.PersonId, schema.PosixAccountId);

        Assert.That(removed, Is.False);
    }

    [Test]
    public async Task DeletingTheAuxiliaryObjectType_AlsoRemovesTheSelectionsPointingAtItAsync()
    {
        // A schema refresh that no longer reports an auxiliary class deletes the Object Type. The selections that
        // named it must go with it: this is the documented data-loss semantic of a refresh, and it is enforced by
        // a database cascade rather than by any code path remembering to tidy up.
        var schema = await SeedAsync();

        await using (var ctx = NewContext())
            await new PostgresDataRepository(ctx).ConnectedSystems.AddObjectTypeExtensionAsync(schema.PersonId, schema.PosixAccountId);

        await using (var ctx = NewContext())
        {
            var auxiliaryType = await ctx.ConnectedSystemObjectTypes.AsTracking().SingleAsync(t => t.Id == schema.PosixAccountId);
            ctx.ConnectedSystemObjectTypes.Remove(auxiliaryType);
            await ctx.SaveChangesAsync();
        }

        await using var readContext = NewContext();
        Assert.That(await readContext.ConnectedSystemObjectTypeExtensions.CountAsync(), Is.Zero,
            "An auxiliary class that disappears from the schema must take its selections with it.");
    }

    [Test]
    public async Task DeletingTheStructuralObjectType_AlsoRemovesItsSelectionsAsync()
    {
        var schema = await SeedAsync();

        await using (var ctx = NewContext())
            await new PostgresDataRepository(ctx).ConnectedSystems.AddObjectTypeExtensionAsync(schema.PersonId, schema.PosixAccountId);

        await using (var ctx = NewContext())
        {
            var structuralType = await ctx.ConnectedSystemObjectTypes.AsTracking().SingleAsync(t => t.Id == schema.PersonId);
            ctx.ConnectedSystemObjectTypes.Remove(structuralType);
            await ctx.SaveChangesAsync();
        }

        await using var readContext = NewContext();
        Assert.That(await readContext.ConnectedSystemObjectTypeExtensions.CountAsync(), Is.Zero);
    }

    #endregion

    #region Structural carrier

    [Test]
    public async Task SetStructuralCarrierObjectTypeAsync_PersistsTheCarrierAsync()
    {
        // The mutating path this exercises loads with an explicit AsTracking. Without it, this context's
        // NoTracking behaviour would make the save a silent no-op that still reported success.
        var schema = await SeedAsync();

        await using (var ctx = NewContext())
            await new PostgresDataRepository(ctx).ConnectedSystems
                .SetStructuralCarrierObjectTypeAsync(schema.PosixAccountId, schema.PersonId);

        await using var readContext = NewContext();
        var auxiliaryType = await readContext.ConnectedSystemObjectTypes.SingleAsync(t => t.Id == schema.PosixAccountId);

        Assert.That(auxiliaryType.StructuralCarrierObjectTypeId, Is.EqualTo(schema.PersonId));
    }

    [Test]
    public async Task SetStructuralCarrierObjectTypeAsync_WithNull_ClearsTheCarrierAsync()
    {
        var schema = await SeedAsync();

        await using (var ctx = NewContext())
            await new PostgresDataRepository(ctx).ConnectedSystems
                .SetStructuralCarrierObjectTypeAsync(schema.PosixAccountId, schema.PersonId);

        await using (var ctx = NewContext())
            await new PostgresDataRepository(ctx).ConnectedSystems
                .SetStructuralCarrierObjectTypeAsync(schema.PosixAccountId, null);

        await using var readContext = NewContext();
        var auxiliaryType = await readContext.ConnectedSystemObjectTypes.SingleAsync(t => t.Id == schema.PosixAccountId);

        Assert.That(auxiliaryType.StructuralCarrierObjectTypeId, Is.Null);
    }

    [Test]
    public async Task SetStructuralCarrierObjectTypeAsync_ForACarrierInAnotherConnectedSystem_IsRefusedAsync()
    {
        var schema = await SeedAsync();
        var otherSystem = await SeedAsync("Other System");

        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);

        Assert.That(async () => await repository.ConnectedSystems
                .SetStructuralCarrierObjectTypeAsync(schema.PosixAccountId, otherSystem.PersonId),
            Throws.ArgumentException);
    }

    [Test]
    public async Task DeletingAnObjectTypeThatCarriesAnother_IsRefusedRatherThanCascadingAsync()
    {
        // Restrict, not cascade: deleting the structural class something else is carried by must not silently
        // delete that something else too. An administrator should see the refusal.
        var schema = await SeedAsync();

        await using (var ctx = NewContext())
            await new PostgresDataRepository(ctx).ConnectedSystems
                .SetStructuralCarrierObjectTypeAsync(schema.PosixAccountId, schema.PersonId);

        await using var deleteContext = NewContext();
        var carrier = await deleteContext.ConnectedSystemObjectTypes.AsTracking().SingleAsync(t => t.Id == schema.PersonId);
        deleteContext.ConnectedSystemObjectTypes.Remove(carrier);

        Assert.That(async () => await deleteContext.SaveChangesAsync(), Throws.InstanceOf<DbUpdateException>());
    }

    #endregion

    #region Discovery runs

    [Test]
    public async Task CreateAuxiliaryClassDiscoveryRunAsync_PersistsTheRunAndItsResultsAsync()
    {
        var schema = await SeedAsync();

        await using (var ctx = NewContext())
        {
            await new PostgresDataRepository(ctx).ConnectedSystems.CreateAuxiliaryClassDiscoveryRunAsync(new AuxiliaryClassDiscoveryRun
            {
                ConnectedSystemId = schema.ConnectedSystemId,
                Scope = AuxiliaryClassDiscoveryScope.QuickSample,
                SampleSizePerObjectType = 100,
                Status = AuxiliaryClassDiscoveryStatus.Complete,
                EntriesRead = 100,
                Completed = DateTime.UtcNow,
                Results =
                [
                    new AuxiliaryClassDiscoveryResult
                    {
                        StructuralObjectTypeId = schema.PersonId,
                        AuxiliaryClassName = "posixAccount",
                        EntryCount = 87
                    }
                ]
            });
        }

        await using var readContext = NewContext();
        var run = await new PostgresDataRepository(readContext).ConnectedSystems
            .GetLatestAuxiliaryClassDiscoveryRunAsync(schema.ConnectedSystemId);

        Assert.That(run, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(run!.Scope, Is.EqualTo(AuxiliaryClassDiscoveryScope.QuickSample));
            Assert.That(run!.SampleSizePerObjectType, Is.EqualTo(100));
            Assert.That(run!.Status, Is.EqualTo(AuxiliaryClassDiscoveryStatus.Complete));
            Assert.That(run!.EntriesRead, Is.EqualTo(100));
            Assert.That(run!.Results, Has.Count.EqualTo(1));
            Assert.That(run!.Results[0].AuxiliaryClassName, Is.EqualTo("posixAccount"));
            Assert.That(run!.Results[0].EntryCount, Is.EqualTo(87));
            Assert.That(run!.Results[0].StructuralObjectType.Name, Is.EqualTo("inetOrgPerson"));
        });
    }

    [Test]
    public async Task CreateAuxiliaryClassDiscoveryRunAsync_WhenOneIsAlreadyInFlight_IsRefusedByTheDatabaseAsync()
    {
        // Two administrators pressing the button at once must not both get a run. A check-then-act in code loses
        // that race, so the constraint lives in the database as a filtered unique index.
        var schema = await SeedAsync();

        await using (var ctx = NewContext())
            await new PostgresDataRepository(ctx).ConnectedSystems.CreateAuxiliaryClassDiscoveryRunAsync(InProgressRun(schema.ConnectedSystemId));

        await using var secondContext = NewContext();
        var repository = new PostgresDataRepository(secondContext);

        Assert.That(async () => await repository.ConnectedSystems.CreateAuxiliaryClassDiscoveryRunAsync(InProgressRun(schema.ConnectedSystemId)),
            Throws.InstanceOf<DbUpdateException>());
    }

    [Test]
    public async Task CreateAuxiliaryClassDiscoveryRunAsync_WhenTheEarlierRunHasFinished_IsAllowedAsync()
    {
        // The constraint is on runs in flight, not on runs at all; a Connected System keeps its history.
        var schema = await SeedAsync();

        await using (var ctx = NewContext())
        {
            var repository = new PostgresDataRepository(ctx);
            var finished = InProgressRun(schema.ConnectedSystemId);
            finished.Status = AuxiliaryClassDiscoveryStatus.Complete;
            finished.Completed = DateTime.UtcNow;
            await repository.ConnectedSystems.CreateAuxiliaryClassDiscoveryRunAsync(finished);
        }

        await using (var ctx = NewContext())
            await new PostgresDataRepository(ctx).ConnectedSystems.CreateAuxiliaryClassDiscoveryRunAsync(InProgressRun(schema.ConnectedSystemId));

        await using var readContext = NewContext();
        Assert.That(await readContext.AuxiliaryClassDiscoveryRuns.CountAsync(), Is.EqualTo(2));
    }

    [Test]
    public async Task GetInProgressAuxiliaryClassDiscoveryRunAsync_WhenNoneIsRunning_ReturnsNullAsync()
    {
        var schema = await SeedAsync();

        await using (var ctx = NewContext())
        {
            var finished = InProgressRun(schema.ConnectedSystemId);
            finished.Status = AuxiliaryClassDiscoveryStatus.Cancelled;
            finished.Completed = DateTime.UtcNow;
            await new PostgresDataRepository(ctx).ConnectedSystems.CreateAuxiliaryClassDiscoveryRunAsync(finished);
        }

        await using var readContext = NewContext();
        Assert.That(await new PostgresDataRepository(readContext).ConnectedSystems
            .GetInProgressAuxiliaryClassDiscoveryRunAsync(schema.ConnectedSystemId), Is.Null);
    }

    [Test]
    public async Task DeletingTheConnectedSystem_AlsoRemovesItsDiscoveryRunsAndResultsAsync()
    {
        var schema = await SeedAsync();

        await using (var ctx = NewContext())
        {
            await new PostgresDataRepository(ctx).ConnectedSystems.CreateAuxiliaryClassDiscoveryRunAsync(new AuxiliaryClassDiscoveryRun
            {
                ConnectedSystemId = schema.ConnectedSystemId,
                Scope = AuxiliaryClassDiscoveryScope.FullScan,
                Status = AuxiliaryClassDiscoveryStatus.Complete,
                Completed = DateTime.UtcNow,
                Results =
                [
                    new AuxiliaryClassDiscoveryResult { StructuralObjectTypeId = schema.PersonId, AuxiliaryClassName = "posixAccount", EntryCount = 1 }
                ]
            });
        }

        await using (var ctx = NewContext())
        {
            var system = await ctx.ConnectedSystems.AsTracking().SingleAsync(cs => cs.Id == schema.ConnectedSystemId);
            ctx.ConnectedSystems.Remove(system);
            await ctx.SaveChangesAsync();
        }

        await using var readContext = NewContext();
        Assert.Multiple(async () =>
        {
            Assert.That(await readContext.AuxiliaryClassDiscoveryRuns.CountAsync(), Is.Zero);
            Assert.That(await readContext.AuxiliaryClassDiscoveryResults.CountAsync(), Is.Zero);
        });
    }

    #endregion

    private static AuxiliaryClassDiscoveryRun InProgressRun(int connectedSystemId)
    {
        return new AuxiliaryClassDiscoveryRun
        {
            ConnectedSystemId = connectedSystemId,
            Scope = AuxiliaryClassDiscoveryScope.QuickSample,
            SampleSizePerObjectType = 100,
            Status = AuxiliaryClassDiscoveryStatus.InProgress
        };
    }

    /// <summary>
    /// The schema refresh merges an administrator's auxiliary class selections onto the structural type that
    /// extends them, working from the Connected System graph the application layer loaded. A selection the query
    /// does not fetch is a selection the merge cannot see, and the failure is silent: the refresh succeeds and the
    /// attributes simply are not there. The EF Core in-memory provider cannot catch this, because it populates
    /// navigations from its change tracker whether the query asked for them or not.
    /// </summary>
    [Test]
    public async Task GetConnectedSystemAsync_ForATypeWithAnAuxiliaryClassSelection_LoadsThatSelectionAsync()
    {
        var seeded = await SeedAsync();

        await using (var create = NewContext())
        {
            create.ConnectedSystemObjectTypeExtensions.Add(new ConnectedSystemObjectTypeExtension
            {
                BaseObjectTypeId = seeded.PersonId,
                ExtensionObjectTypeId = seeded.PosixAccountId
            });
            await create.SaveChangesAsync();
        }

        await using var readContext = NewContext();
        var repository = new PostgresDataRepository(readContext);
        var connectedSystem = await repository.ConnectedSystems.GetConnectedSystemAsync(seeded.ConnectedSystemId);

        Assert.That(connectedSystem, Is.Not.Null);
        var person = connectedSystem!.ObjectTypes!.Single(objectType => objectType.Id == seeded.PersonId);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(person.Extensions, Has.Count.EqualTo(1));
            Assert.That(person.Extensions[0].ExtensionObjectTypeId, Is.EqualTo(seeded.PosixAccountId));
        }
    }

    /// <summary>
    /// Seeds a Connected System carrying a structural Object Type and an auxiliary one, shaped like the
    /// inetOrgPerson + posixAccount pairing that is near-universal in OpenLDAP estates.
    /// </summary>
    private async Task<SeededSchema> SeedAsync(string systemName = "Yellowstone")
    {
        await using var seed = NewContext();

        var connectorDefinition = new ConnectorDefinition { Name = $"Test Connector {systemName}", BuiltIn = true };
        var person = new ConnectedSystemObjectType { Name = "inetOrgPerson" };
        var posixAccount = new ConnectedSystemObjectType { Name = "posixAccount" };
        var system = new ConnectedSystem
        {
            Name = systemName,
            ConnectorDefinition = connectorDefinition,
            ObjectTypes = [person, posixAccount]
        };

        seed.ConnectorDefinitions.Add(connectorDefinition);
        seed.ConnectedSystems.Add(system);
        await seed.SaveChangesAsync();

        return new SeededSchema(system.Id, person.Id, posixAccount.Id);
    }

    private sealed record SeededSchema(int ConnectedSystemId, int PersonId, int PosixAccountId);
}
