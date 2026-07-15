// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Models.Core;
using JIM.Models.Core.DTOs;
using JIM.Models.Logic;
using JIM.Models.Search;
using JIM.Models.Staging;
using JIM.PostgresData;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// Real-PostgreSQL verification of the Metaverse Object Type deletion repository behaviour added in #376. The EF Core
/// in-memory provider does not enforce the FK delete cascade, and reference discovery spans several tables, so these
/// exercise the object-count query, reference discovery, and the multi-table cascade delete against a real database.
/// Opt-in via <c>JIM_TEST_RESET_DB</c>; ignored when it is absent.
/// </summary>
[TestFixture]
[Category("RequiresPostgres")]
public class MetaverseObjectTypeDeletionDatabaseTests
{
    private string _connectionString = null!;

    private JimDbContext NewContext()
    {
        var options = new DbContextOptionsBuilder<JimDbContext>()
            .UseNpgsql(_connectionString)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;
        return new JimDbContext(options);
    }

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        var dbName = Environment.GetEnvironmentVariable("JIM_TEST_RESET_DB");
        if (string.IsNullOrEmpty(dbName))
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL object type deletion tests.");

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
    public async Task EvaluateObjectTypeDeletionAsync_ReportsObjectCountRulesAndReferencesAsync()
    {
        int deviceTypeId;
        await using (var seed = NewContext())
        {
            var connectorDefinition = new ConnectorDefinition { Name = "Test Connector", BuiltIn = true };
            var system = new ConnectedSystem { Name = "Test System", ConnectorDefinition = connectorDefinition };
            var csType = new ConnectedSystemObjectType { Name = "device", ConnectedSystem = system, Selected = true };

            var deviceType = new MetaverseObjectType { Name = "Device", PluralName = "Devices", BuiltIn = false };
            var serialNumber = new MetaverseAttribute { Name = "serialNumber", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.SingleValued };
            deviceType.Attributes.Add(serialNumber);

            seed.ConnectorDefinitions.Add(connectorDefinition);
            seed.ConnectedSystems.Add(system);
            seed.ConnectedSystemObjectTypes.Add(csType);
            seed.MetaverseObjectTypes.Add(deviceType);
            await seed.SaveChangesAsync();
            deviceTypeId = deviceType.Id;

            // A Synchronisation Rule targeting the type (a hard block), a Predefined Search for the type (a cascade
            // reference), and one Metaverse Object of the type (a hard block).
            seed.SyncRules.Add(new SyncRule
            {
                Name = "Device Import",
                Direction = SyncRuleDirection.Import,
                Enabled = true,
                ConnectedSystem = system,
                ConnectedSystemObjectType = csType,
                MetaverseObjectType = deviceType
            });
            seed.PredefinedSearches.Add(new PredefinedSearch { Name = "All Devices", Uri = "all-devices", MetaverseObjectType = deviceType });
            seed.MetaverseObjects.Add(new MetaverseObject { Type = deviceType });
            await seed.SaveChangesAsync();
        }

        await using var ctx = NewContext();
        var jim = new JimApplication(new PostgresDataRepository(ctx));
        var objectType = await jim.Metaverse.GetMetaverseObjectTypeAsync(deviceTypeId, includeChildObjects: false);

        var impact = await jim.Metaverse.EvaluateObjectTypeDeletionAsync(objectType!);

        Assert.That(impact.MetaverseObjectCount, Is.EqualTo(1));
        Assert.That(impact.BlockedByObjects, Is.True);
        Assert.That(impact.SynchronisationRules.Select(r => r.Description), Does.Contain("Device Import"));
        Assert.That(impact.BlockedBySynchronisationRules, Is.True);
        Assert.That(impact.CascadeReferences.Count(r => r.Kind == ObjectTypeReferenceKind.PredefinedSearch), Is.EqualTo(1));
        Assert.That(impact.CascadeReferences.Count(r => r.Kind == ObjectTypeReferenceKind.AttributeBinding), Is.EqualTo(1));
    }

    [Test]
    public async Task DeleteMetaverseObjectTypeAsync_WithReferencesNoObjects_CascadesReferencesAndKeepsAttributeAsync()
    {
        int deviceTypeId;
        int serialNumberId;
        await using (var seed = NewContext())
        {
            var deviceType = new MetaverseObjectType { Name = "Device", PluralName = "Devices", BuiltIn = false };
            var serialNumber = new MetaverseAttribute { Name = "serialNumber", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.SingleValued };
            deviceType.Attributes.Add(serialNumber);
            seed.MetaverseObjectTypes.Add(deviceType);
            await seed.SaveChangesAsync();
            deviceTypeId = deviceType.Id;
            serialNumberId = serialNumber.Id;

            seed.PredefinedSearches.Add(new PredefinedSearch { Name = "All Devices", Uri = "all-devices", MetaverseObjectType = deviceType });
            await seed.SaveChangesAsync();
        }

        await using (var act = NewContext())
        {
            var jim = new JimApplication(new PostgresDataRepository(act));
            var objectType = await jim.Metaverse.GetMetaverseObjectTypeAsync(deviceTypeId, includeChildObjects: false);
            var impact = await jim.Metaverse.DeleteMetaverseObjectTypeAsync(objectType!, TestUtilities.GetInitiatedBy());
            Assert.That(impact.Deleted, Is.True);
        }

        await using var verify = NewContext();
        Assert.That(await verify.MetaverseObjectTypes.AnyAsync(t => t.Id == deviceTypeId), Is.False, "the object type was removed");
        Assert.That(await verify.PredefinedSearches.AnyAsync(), Is.False, "the Predefined Search was cascade-removed");
        Assert.That(await verify.MetaverseAttributes.AnyAsync(a => a.Id == serialNumberId), Is.True, "the bound attribute itself survives");
    }

    [Test]
    public async Task DeleteMetaverseObjectTypeAsync_WithLiveObjects_RefusesAndLeavesTypeAsync()
    {
        int deviceTypeId;
        await using (var seed = NewContext())
        {
            var deviceType = new MetaverseObjectType { Name = "Device", PluralName = "Devices", BuiltIn = false };
            seed.MetaverseObjectTypes.Add(deviceType);
            await seed.SaveChangesAsync();
            deviceTypeId = deviceType.Id;

            seed.MetaverseObjects.Add(new MetaverseObject { Type = deviceType });
            await seed.SaveChangesAsync();
        }

        await using (var act = NewContext())
        {
            var jim = new JimApplication(new PostgresDataRepository(act));
            var objectType = await jim.Metaverse.GetMetaverseObjectTypeAsync(deviceTypeId, includeChildObjects: false);
            var impact = await jim.Metaverse.DeleteMetaverseObjectTypeAsync(objectType!, TestUtilities.GetInitiatedBy());
            Assert.That(impact.BlockedByObjects, Is.True);
            Assert.That(impact.Deleted, Is.False);
        }

        await using var verify = NewContext();
        Assert.That(await verify.MetaverseObjectTypes.AnyAsync(t => t.Id == deviceTypeId), Is.True, "the object type survives the refused deletion");
    }
}
