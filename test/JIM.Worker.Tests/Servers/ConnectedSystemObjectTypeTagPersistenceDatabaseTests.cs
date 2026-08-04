// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Staging;
using JIM.PostgresData;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// Real-PostgreSQL verification that an Object Type's classification tags survive a save and are reconciled on a
/// later one: newly reported classifications are inserted, unchanged ones are left alone, and ones the Connector no
/// longer reports are deleted.
/// </summary>
/// <remarks>
/// The unit-level tests beside this one cover the merge's in-memory semantics; only a real database proves the
/// repository turns those semantics into the right INSERTs and DELETEs. Two things the EF Core in-memory provider
/// structurally cannot catch are checked here: the unique index on (object type, key, value), which turns any
/// delete-then-reinsert mistake into a constraint violation, and the portal's <c>NoTracking</c> context, under
/// which a mutating path that forgot <c>AsTracking</c> silently does nothing while reporting success.
///
/// Opt-in via the same <c>JIM_TEST_RESET_*</c> environment variables as the other database tests; ignored when
/// <c>JIM_TEST_RESET_DB</c> is absent.
/// </remarks>
[TestFixture]
[Category("RequiresPostgres")]
public class ConnectedSystemObjectTypeTagPersistenceDatabaseTests
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
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL object type tag persistence tests.");

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

    [Test]
    public async Task UpdateConnectedSystemSchemaAsync_WithANewlyClassifiedObjectType_PersistsTheClassificationAsync()
    {
        var systemId = await SeedAsync();

        var detachedSystem = await LoadDetachedAsync(systemId);
        detachedSystem.ObjectTypes!.Single().Tags =
        [
            new ConnectedSystemObjectTypeTag { Key = ObjectTypeTags.Keys.ClassKind, Value = ObjectTypeTags.Values.ClassKindStructural }
        ];
        await SaveSchemaAsync(detachedSystem);

        var reloaded = await LoadDetachedAsync(systemId);
        Assert.That(reloaded.ObjectTypes!.Single().Tags.Select(t => $"{t.Key}={t.Value}"),
            Is.EquivalentTo(new[] { $"{ObjectTypeTags.Keys.ClassKind}={ObjectTypeTags.Values.ClassKindStructural}" }));
    }

    [Test]
    public async Task UpdateConnectedSystemSchemaAsync_WhenAClassificationIsUnchanged_KeepsTheSameRowAsync()
    {
        var systemId = await SeedAsync();

        var firstPass = await LoadDetachedAsync(systemId);
        firstPass.ObjectTypes!.Single().Tags =
        [
            new ConnectedSystemObjectTypeTag { Key = ObjectTypeTags.Keys.ClassKind, Value = ObjectTypeTags.Values.ClassKindAuxiliary }
        ];
        await SaveSchemaAsync(firstPass);
        var originalTagId = (await LoadDetachedAsync(systemId)).ObjectTypes!.Single().Tags.Single().Id;

        // A second refresh reporting exactly the same classification must be a no-op, not a delete and re-insert:
        // churning the row on every refresh would be needless write traffic, and re-inserting into a uniquely
        // indexed table is exactly where a delete-then-insert ordering mistake would surface.
        var secondPass = await LoadDetachedAsync(systemId);
        await SaveSchemaAsync(secondPass);

        var reloaded = await LoadDetachedAsync(systemId);
        Assert.Multiple(() =>
        {
            Assert.That(reloaded.ObjectTypes!.Single().Tags, Has.Count.EqualTo(1));
            Assert.That(reloaded.ObjectTypes!.Single().Tags.Single().Id, Is.EqualTo(originalTagId),
                "An unchanged classification must keep its row rather than being deleted and re-inserted on every refresh.");
        });
    }

    [Test]
    public async Task UpdateConnectedSystemSchemaAsync_WhenAClassificationIsWithdrawn_DeletesItAsync()
    {
        var systemId = await SeedAsync();

        var firstPass = await LoadDetachedAsync(systemId);
        firstPass.ObjectTypes!.Single().Tags =
        [
            new ConnectedSystemObjectTypeTag { Key = ObjectTypeTags.Keys.ClassKind, Value = ObjectTypeTags.Values.ClassKindStructural }
        ];
        await SaveSchemaAsync(firstPass);

        // The Connected System now classifies the same type differently. The old classification must go, or the
        // type would claim to be two kinds of class at once.
        var secondPass = await LoadDetachedAsync(systemId);
        secondPass.ObjectTypes!.Single().Tags =
        [
            new ConnectedSystemObjectTypeTag { Key = ObjectTypeTags.Keys.ClassKind, Value = ObjectTypeTags.Values.ClassKindAuxiliary }
        ];
        await SaveSchemaAsync(secondPass);

        var reloaded = await LoadDetachedAsync(systemId);
        Assert.That(reloaded.ObjectTypes!.Single().Tags.Select(t => $"{t.Key}={t.Value}"),
            Is.EquivalentTo(new[] { $"{ObjectTypeTags.Keys.ClassKind}={ObjectTypeTags.Values.ClassKindAuxiliary}" }));
    }

    [Test]
    public async Task DeletingAnObjectType_AlsoDeletesItsClassificationAsync()
    {
        // Schema refresh data-loss semantics: a type that disappears at the Connected System takes its
        // classification with it, rather than orphaning rows.
        var systemId = await SeedAsync();

        var detachedSystem = await LoadDetachedAsync(systemId);
        detachedSystem.ObjectTypes!.Single().Tags =
        [
            new ConnectedSystemObjectTypeTag { Key = ObjectTypeTags.Keys.ClassKind, Value = ObjectTypeTags.Values.ClassKindStructural }
        ];
        await SaveSchemaAsync(detachedSystem);

        await using (var deleteContext = NewContext())
        {
            var objectType = await deleteContext.ConnectedSystemObjectTypes.AsTracking().SingleAsync();
            deleteContext.ConnectedSystemObjectTypes.Remove(objectType);
            await deleteContext.SaveChangesAsync();
        }

        await using var assertContext = NewContext();
        Assert.That(await assertContext.ConnectedSystemObjectTypeTags.CountAsync(), Is.Zero,
            "A deleted object type must take its classification tags with it.");
    }

    /// <summary>
    /// Loads the Connected System in its own scope and lets that scope go, so what the caller holds is detached,
    /// exactly as the portal does.
    /// </summary>
    private async Task<ConnectedSystem> LoadDetachedAsync(int systemId)
    {
        await using var loadContext = NewContext();
        var loadRepository = new PostgresDataRepository(loadContext);
        var system = await loadRepository.ConnectedSystems.GetConnectedSystemAsync(systemId);
        Assert.That(system, Is.Not.Null);
        return system!;
    }

    private async Task SaveSchemaAsync(ConnectedSystem connectedSystem)
    {
        await using var saveContext = NewContext();
        var saveRepository = new PostgresDataRepository(saveContext);
        await saveRepository.ConnectedSystems.UpdateConnectedSystemSchemaAsync(connectedSystem);
    }

    /// <summary>
    /// Seeds a Connected System carrying a single, as-yet unclassified Object Type.
    /// </summary>
    private async Task<int> SeedAsync()
    {
        await using var seed = NewContext();

        var connectorDefinition = new ConnectorDefinition { Name = "Test Connector", BuiltIn = true };
        var system = new ConnectedSystem
        {
            Name = "Test System",
            ConnectorDefinition = connectorDefinition,
            ObjectTypes =
            [
                new ConnectedSystemObjectType { Name = "inetOrgPerson" }
            ]
        };

        seed.ConnectorDefinitions.Add(connectorDefinition);
        seed.ConnectedSystems.Add(system);
        await seed.SaveChangesAsync();

        return system.Id;
    }
}
