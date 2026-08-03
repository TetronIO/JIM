// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Staging;
using JIM.PostgresData;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NUnit.Framework;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// Real-PostgreSQL verification that re-running Schema Import against an existing Connected System does not fail
/// with a duplicate-key exception while reconciling newly-discovered attributes.
/// </summary>
/// <remarks>
/// Regression guard for issue #1171. <c>ConnectedSystemRepository.ReconcileAttributes</c> built a lookup of a
/// tracked object type's attributes keyed by <c>Id</c> (<c>trackedType.Attributes.ToDictionary(a => a.Id)</c>).
/// New attributes discovered by a schema refresh carry <c>Id == 0</c> until <c>SaveChangesAsync</c> assigns a real
/// key, and EF Core's navigation fixup adds each newly <c>Add()</c>-ed attribute into
/// <c>trackedType.Attributes</c> immediately (not deferred to <c>SaveChangesAsync</c>). So when
/// <c>ConnectedSystem.ObjectTypes</c> (the incoming graph <c>ConnectedSystemServer.MergeSchemaIntoConnectedSystem</c>
/// builds) contains more than one entry that resolves to the <em>same</em> existing object type, and at least two
/// new attributes are added across those entries before the last one reconciles, that last call's
/// <c>ToDictionary(a => a.Id)</c> finds two attributes still sitting at Id == 0 and throws "An item with the same
/// key has already been added. Key: 0" - reproduced exactly by
/// <see cref="UpdateConnectedSystemSchemaAsync_ObjectTypeReconciledTwiceInOnePassWithNewAttributes_DoesNotThrowAsync"/>
/// below. The EF Core in-memory provider auto-assigns keys on <c>Add</c>, so it cannot reproduce this; it needs
/// PostgreSQL's real identity-column behaviour (an unsaved entity's <c>Id</c> genuinely stays 0 until saved).
/// </remarks>
[TestFixture]
[Category("RequiresPostgres")]
public class ConnectedSystemSchemaReconciliationDatabaseTests
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
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL schema reconciliation tests.");

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

    /// <summary>
    /// Seeds a Connected System with one Object Type carrying a single, already-persisted attribute, mirroring
    /// the state left behind by a first-time Schema Import.
    /// </summary>
    private async Task<(int SystemId, int ObjectTypeId)> SeedAsync()
    {
        await using var seed = NewContext();

        var connectorDefinition = new ConnectorDefinition { Name = "Test Connector", BuiltIn = true };
        var system = new ConnectedSystem
        {
            Name = "Test System",
            ConnectorDefinition = connectorDefinition
        };
        var objectType = new ConnectedSystemObjectType
        {
            ConnectedSystem = system,
            Name = "user"
        };
        objectType.Attributes.Add(new ConnectedSystemObjectTypeAttribute
        {
            ConnectedSystemObjectType = objectType,
            Name = "cn",
            Type = AttributeDataType.Text
        });
        system.ObjectTypes ??= [];
        system.ObjectTypes.Add(objectType);

        seed.ConnectorDefinitions.Add(connectorDefinition);
        seed.ConnectedSystems.Add(system);
        await seed.SaveChangesAsync();

        return (system.Id, objectType.Id);
    }

    /// <summary>
    /// The ordinary re-import case: a single Object Type gains two new attributes in one Schema Import. This
    /// alone does not trigger #1171 (a single <c>ReconcileAttributes</c> call builds its lookup once, before
    /// either new attribute is added), but it is the shape the issue's reproduction steps describe, so it is
    /// covered here as a straightforward regression alongside the exact trigger below.
    /// </summary>
    [Test]
    public async Task UpdateConnectedSystemSchemaAsync_ObjectTypeGainsTwoNewAttributes_ReconcilesWithoutDuplicateKeyExceptionAsync()
    {
        var (systemId, objectTypeId) = await SeedAsync();

        // Load detached, exactly as the portal/API does before a schema refresh, then mutate it in memory the way
        // MergeSchemaIntoConnectedSystem does: the existing object type instance is reused, its Attributes
        // collection is rebuilt to hold the existing (Id-preserved) attribute plus two brand-new (Id == 0)
        // attributes discovered by the refreshed schema.
        ConnectedSystem detachedSystem;
        await using (var loadContext = NewContext())
        {
            var loadRepository = new PostgresDataRepository(loadContext);
            detachedSystem = (await loadRepository.ConnectedSystems.GetConnectedSystemAsync(systemId))!;
        }
        Assert.That(detachedSystem, Is.Not.Null);

        var objectType = detachedSystem.ObjectTypes!.Single(ot => ot.Id == objectTypeId);
        var existingAttribute = objectType.Attributes.Single();

        objectType.Attributes =
        [
            existingAttribute,
            new ConnectedSystemObjectTypeAttribute { Name = "mail", Type = AttributeDataType.Text },
            new ConnectedSystemObjectTypeAttribute { Name = "telephoneNumber", Type = AttributeDataType.Text }
        ];

        await using (var saveContext = NewContext())
        {
            var saveRepository = new PostgresDataRepository(saveContext);
            Assert.DoesNotThrowAsync(async () => await saveRepository.ConnectedSystems.UpdateConnectedSystemSchemaAsync(detachedSystem));
        }

        await using var verify = NewContext();
        var persistedAttributes = await verify.ConnectedSystemAttributes
            .Where(a => a.ConnectedSystemObjectType.Id == objectTypeId)
            .ToListAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(persistedAttributes, Has.Count.EqualTo(3), "The existing attribute and both new attributes must be persisted, each exactly once.");
            Assert.That(persistedAttributes.Select(a => a.Name), Is.EquivalentTo(new[] { "cn", "mail", "telephoneNumber" }));
            Assert.That(persistedAttributes.Select(a => a.Id).Distinct().Count(), Is.EqualTo(3), "Each attribute must get its own, distinct persisted Id.");
        }
    }

    /// <summary>
    /// The exact trigger for #1171: <c>ConnectedSystem.ObjectTypes</c> contains two entries that both resolve to
    /// the same existing (tracked) object type - which happens when
    /// <c>ConnectedSystemServer.MergeSchemaIntoConnectedSystem</c>'s per-schema-object-type loop matches more than
    /// one incoming object type to the same existing one by Name (e.g. a connector returning the same object type
    /// name more than once in a single schema response) - with at least two new attributes added across the two
    /// entries before the second one reconciles. Before the fix, the second <c>ReconcileAttributes</c> call found
    /// two Id == 0 attributes already sitting in <c>trackedType.Attributes</c> (added by the first call's EF
    /// navigation fixup) and its <c>ToDictionary(a => a.Id)</c> threw.
    /// </summary>
    [Test]
    public async Task UpdateConnectedSystemSchemaAsync_ObjectTypeReconciledTwiceInOnePassWithNewAttributes_DoesNotThrowAsync()
    {
        var (systemId, objectTypeId) = await SeedAsync();

        ConnectedSystem detachedSystem;
        await using (var loadContext = NewContext())
        {
            var loadRepository = new PostgresDataRepository(loadContext);
            detachedSystem = (await loadRepository.ConnectedSystems.GetConnectedSystemAsync(systemId))!;
        }
        Assert.That(detachedSystem, Is.Not.Null);

        var objectType = detachedSystem.ObjectTypes!.Single(ot => ot.Id == objectTypeId);
        var existingAttribute = objectType.Attributes.Single();

        // Two separate ConnectedSystemObjectType instances in the incoming graph, both carrying the existing
        // object type's Id, each contributing its own new (Id == 0) attribute - the shape MergeSchemaIntoConnectedSystem
        // would produce if the connector's schema response named this object type twice.
        // Both new attributes must land on the FIRST occurrence: EF's navigation fixup adds each into
        // trackedType.Attributes as it is Add()-ed, so the second occurrence's ToDictionary(a => a.Id) call is
        // the one that finds two Id == 0 entries and throws. A new attribute split one-per-occurrence does not
        // reproduce #1171 - the pass ends before a third call's dictionary build would see both.
        var objectTypeOccurrenceOne = new ConnectedSystemObjectType
        {
            Id = objectType.Id,
            ConnectedSystemId = objectType.ConnectedSystemId,
            Name = objectType.Name,
            Attributes =
            [
                existingAttribute,
                new ConnectedSystemObjectTypeAttribute { Name = "mail", Type = AttributeDataType.Text },
                new ConnectedSystemObjectTypeAttribute { Name = "telephoneNumber", Type = AttributeDataType.Text }
            ]
        };
        var objectTypeOccurrenceTwo = new ConnectedSystemObjectType
        {
            Id = objectType.Id,
            ConnectedSystemId = objectType.ConnectedSystemId,
            Name = objectType.Name,
            Attributes =
            [
                existingAttribute
            ]
        };
        detachedSystem.ObjectTypes = [objectTypeOccurrenceOne, objectTypeOccurrenceTwo];

        await using (var saveContext = NewContext())
        {
            var saveRepository = new PostgresDataRepository(saveContext);

            // This must not throw. Before the fix: System.ArgumentException, "An item with the same key has
            // already been added. Key: 0", thrown from ReconcileAttributes' ToDictionary(a => a.Id) call.
            Assert.DoesNotThrowAsync(async () => await saveRepository.ConnectedSystems.UpdateConnectedSystemSchemaAsync(detachedSystem));
        }

        await using var verify = NewContext();
        var persistedAttributes = await verify.ConnectedSystemAttributes
            .Where(a => a.ConnectedSystemObjectType.Id == objectTypeId)
            .ToListAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(persistedAttributes, Has.Count.EqualTo(3), "The existing attribute and both new attributes must be persisted, each exactly once, not duplicated across the two reconciliation passes.");
            Assert.That(persistedAttributes.Select(a => a.Name), Is.EquivalentTo(new[] { "cn", "mail", "telephoneNumber" }));
            Assert.That(persistedAttributes.Select(a => a.Id).Distinct().Count(), Is.EqualTo(3), "Each attribute must get its own, distinct persisted Id.");
        }
    }
}
