// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Staging;
using JIM.PostgresData;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NUnit.Framework;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// An Object Type whose Reference attribute declares itself as its target (#1285) must still be updatable
/// through the portal's path: a no-tracking load followed by <c>UpdateObjectTypeAsync</c>'s graph attach.
/// </summary>
/// <remarks>
/// <para>
/// This pins the failure that killed the first Scenario 16 run after #1285 landed: eager-loading the
/// <c>ReferencedObjectType</c> navigation on the object type retrievals materialised a self-referencing
/// Object Type twice under the web host's no-tracking queries (no identity resolution), and the update's
/// graph attach then failed with "another instance with the same key value is already being tracked" on
/// every <c>Set-JIMConnectedSystemObjectType</c> call. The retrievals therefore deliberately do not load
/// that navigation; the declared target's name is resolved from a sibling name projection instead.
/// </para>
/// <para>
/// Real PostgreSQL and a no-tracking context, matching JIM.Web: the in-memory provider tracks by default
/// and resolves identities, so the whole unit suite passes with the defect present.
/// </para>
/// <para>
/// Opt-in via the same <c>JIM_TEST_RESET_*</c> environment variables as the other RequiresPostgres fixtures.
/// </para>
/// </remarks>
[TestFixture]
[Category("RequiresPostgres")]
public class ObjectTypeUpdateWithDeclaredTargetDatabaseTests
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
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL declared-target update tests.");

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
    public async Task UpdateObjectTypeAsync_ASelfReferencingDeclaredTarget_UpdatesWithoutDoubleTrackingAsync()
    {
        var objectTypeId = await SeedSelfReferencingObjectTypeAsync();

        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);
        var loaded = await repository.ConnectedSystems.GetObjectTypeAsync(objectTypeId);
        Assert.That(loaded, Is.Not.Null);

        loaded!.Selected = true;
        Assert.That(async () => await repository.ConnectedSystems.UpdateObjectTypeAsync(loaded), Throws.Nothing,
            "A self-referencing declared target must not make the update's graph attach meet a second instance of the same Object Type.");

        await using var verify = NewContext();
        var persisted = await verify.ConnectedSystemObjectTypes.SingleAsync(ot => ot.Id == objectTypeId);
        Assert.That(persisted.Selected, Is.True, "The update must actually persist, not merely not throw.");
    }

    [Test]
    public async Task GetObjectTypeNamesAsync_ReturnsEveryTypeKeyedById()
    {
        var objectTypeId = await SeedSelfReferencingObjectTypeAsync();

        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);
        var loaded = await repository.ConnectedSystems.GetObjectTypeAsync(objectTypeId);
        var names = await repository.ConnectedSystems.GetObjectTypeNamesAsync(loaded!.ConnectedSystemId);

        Assert.That(names, Is.EqualTo(new Dictionary<int, string> { [objectTypeId] = "Person" }),
            "The name projection is what resolves a declared target's name for the API, so it must cover every Object Type of the system.");
    }

    /// <summary>
    /// Seeds one Connected System with a Person Object Type whose MANAGER Reference attribute declares
    /// Person itself as its target, which is the shape (a manager reference) that broke the portal.
    /// </summary>
    private async Task<int> SeedSelfReferencingObjectTypeAsync()
    {
        await using var seed = NewContext();

        var connectorDefinition = new ConnectorDefinition { Name = "Test Connector", BuiltIn = true };
        var connectedSystem = new ConnectedSystem { Name = "Glitterband", ConnectorDefinition = connectorDefinition };
        var objectType = new ConnectedSystemObjectType { Name = "Person", ConnectedSystem = connectedSystem };
        var anchorAttribute = new ConnectedSystemObjectTypeAttribute
        {
            Name = "EMPLOYEE_ID",
            ConnectedSystemObjectType = objectType,
            Type = AttributeDataType.Number,
            AttributePlurality = AttributePlurality.SingleValued,
            Selected = true,
            IsExternalId = true
        };
        var managerAttribute = new ConnectedSystemObjectTypeAttribute
        {
            Name = "MANAGER_EMPLOYEE_ID",
            ConnectedSystemObjectType = objectType,
            Type = AttributeDataType.Reference,
            AttributePlurality = AttributePlurality.SingleValued,
            Selected = true,
            ReferencedObjectType = objectType
        };
        objectType.Attributes.Add(anchorAttribute);
        objectType.Attributes.Add(managerAttribute);
        seed.AddRange(connectorDefinition, connectedSystem, objectType);
        await seed.SaveChangesAsync();

        return objectType.Id;
    }
}
