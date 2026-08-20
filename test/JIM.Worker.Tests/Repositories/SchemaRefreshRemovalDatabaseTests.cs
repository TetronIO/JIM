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
/// What a schema refresh actually does to Object Types and attributes the Connected System no longer reports.
///
/// The answer is nothing: <c>ReconcileObjectTypesAsync</c> and <c>ReconcileAttributes</c> update and insert only,
/// deliberately, because deleting schema entries that Synchronisation Rules may reference needs reference-aware
/// handling (#782). So a refresh REPORTS a removal in <c>SchemaRefreshResult</c> and then retains the entry.
///
/// That gap is what the schema refresh preview (#421) has to describe honestly. An administrator reading
/// "3 attributes removed" reasonably believes JIM's schema now matches the Connected System; it does not, and the
/// retained entries stay selectable, stay mappable, and (per #1475) hold values that never refresh again.
/// </summary>
/// <remarks>
/// Opt-in via the same <c>JIM_TEST_RESET_*</c> environment variables as the other database-backed tests;
/// ignored when <c>JIM_TEST_RESET_DB</c> is absent.
/// </remarks>
[TestFixture]
[Category("RequiresPostgres")]
public class SchemaRefreshRemovalDatabaseTests
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
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL schema refresh removal tests.");

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
    public async Task UpdateConnectedSystemSchema_AnAttributeTheSchemaNoLongerCarries_IsRetainedNotDeletedAsync()
    {
        var connectedSystemId = await SeedAsync();

        // The shape a schema refresh produces for an attribute the Connected System has stopped reporting: the
        // merge rebuilds the Object Type's attribute collection from what the Connector returned, so the departed
        // attribute is simply absent from the graph that reaches the repository.
        ConnectedSystem connectedSystem;
        await using (var loadCtx = NewContext())
        {
            // Loaded in its own scope and saved in another, which is the shape the portal produces: the tab holds
            // a Connected System from one JimApplication instance and the schema import runs on a second. Mutating
            // a TRACKED collection instead would make EF cascade the removal itself, which is a different
            // mechanism and would prove nothing about the reconciliation under test.
            var loadRepo = new PostgresDataRepository(loadCtx);
            connectedSystem = (await loadRepo.ConnectedSystems.GetConnectedSystemAsync(connectedSystemId))!;
        }

        var objectType = connectedSystem.ObjectTypes!.Single();
        objectType.Attributes = objectType.Attributes.Where(a => a.Name != "department").ToList();

        await using (var saveCtx = NewContext())
        {
            var repo = new PostgresDataRepository(saveCtx);
            await repo.ConnectedSystems.UpdateConnectedSystemSchemaAsync(connectedSystem);
        }

        await using var verifyCtx = NewContext();
        var attributes = await verifyCtx.ConnectedSystemAttributes
            .Where(a => a.ConnectedSystemObjectType!.ConnectedSystemId == connectedSystemId)
            .Select(a => a.Name)
            .ToListAsync();

        Assert.That(attributes, Does.Contain("department"),
            "removals are deliberately not persisted (#782), so an attribute the Connected System no longer " +
            "reports is retained. A refresh that reports it as removed is therefore describing something that " +
            "did not happen, which is what the schema refresh preview has to say honestly (#421).");
    }

    [Test]
    public async Task UpdateConnectedSystemSchema_AnObjectTypeTheSchemaNoLongerCarries_IsRetainedNotDeletedAsync()
    {
        var connectedSystemId = await SeedAsync(includeSecondObjectType: true);

        ConnectedSystem connectedSystem;
        await using (var loadCtx = NewContext())
        {
            var loadRepo = new PostgresDataRepository(loadCtx);
            connectedSystem = (await loadRepo.ConnectedSystems.GetConnectedSystemAsync(connectedSystemId))!;
        }

        connectedSystem.ObjectTypes = connectedSystem.ObjectTypes!.Where(ot => ot.Name != "Group").ToList();

        await using (var saveCtx = NewContext())
        {
            var repo = new PostgresDataRepository(saveCtx);
            await repo.ConnectedSystems.UpdateConnectedSystemSchemaAsync(connectedSystem);
        }

        await using var verifyCtx = NewContext();
        var objectTypes = await verifyCtx.ConnectedSystemObjectTypes
            .Where(ot => ot.ConnectedSystemId == connectedSystemId)
            .Select(ot => ot.Name)
            .ToListAsync();

        Assert.That(objectTypes, Does.Contain("Group"),
            "the same holds one level up: an Object Type the Connected System no longer reports is retained");
    }

    private async Task<int> SeedAsync(bool includeSecondObjectType = false)
    {
        await using var ctx = NewContext();

        var connectorDefinition = new ConnectorDefinition { Name = "Schema Refresh Test Connector", BuiltIn = false };
        var connectedSystem = new ConnectedSystem { Name = "Schema Refresh Source", ConnectorDefinition = connectorDefinition };
        ctx.ConnectorDefinitions.Add(connectorDefinition);
        ctx.ConnectedSystems.Add(connectedSystem);
        await ctx.SaveChangesAsync();

        var userType = new ConnectedSystemObjectType
        {
            ConnectedSystemId = connectedSystem.Id,
            Name = "User",
            Selected = true,
            Attributes =
            [
                new ConnectedSystemObjectTypeAttribute { Name = "objectGuid", Type = AttributeDataType.Guid, IsExternalId = true, Selected = true },
                new ConnectedSystemObjectTypeAttribute { Name = "department", Type = AttributeDataType.Text, Selected = true }
            ]
        };
        ctx.ConnectedSystemObjectTypes.Add(userType);

        if (includeSecondObjectType)
        {
            ctx.ConnectedSystemObjectTypes.Add(new ConnectedSystemObjectType
            {
                ConnectedSystemId = connectedSystem.Id,
                Name = "Group",
                Selected = true,
                Attributes = [new ConnectedSystemObjectTypeAttribute { Name = "objectGuid", Type = AttributeDataType.Guid, IsExternalId = true, Selected = true }]
            });
        }

        await ctx.SaveChangesAsync();
        return connectedSystem.Id;
    }
}
