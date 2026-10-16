// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Staging;
using JIM.PostgresData;
using JIM.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// Real-PostgreSQL verification that a Connected System Object Type attribute's operational flag (an LDAP directory's
/// operational attributes, #2153 D8) survives the schema save the portal and the Worker both use, are restated by a later one, and
/// read back through the query the REST API and PowerShell are served from.
/// </summary>
/// <remarks>
/// The column carries a database default, which the EF Core in-memory provider does not model: it cannot prove that
/// rows written before the column existed read back as not operational. The contexts are <c>NoTracking</c>, as the
/// portal's are, so a reconcile that forgot to copy the column onto the tracked attribute would show here as a flag
/// that never arrives.
///
/// Opt-in via the same <c>JIM_TEST_RESET_*</c> environment variables as the other database tests; ignored when
/// <c>JIM_TEST_RESET_DB</c> is absent.
/// </remarks>
[TestFixture]
[Category("RequiresPostgres")]
public class ConnectedSystemOperationalAttributePersistenceDatabaseTests
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
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL operational attribute persistence tests.");

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
        await PostgresTestDatabase.ResetAsync(_connectionString);
    }

    [Test]
    public async Task UpdateConnectedSystemSchemaAsync_NewlyDiscoveredOperationalAttribute_PersistsItsTagAsync()
    {
        var systemId = await SeedAsync();

        var detachedSystem = await LoadDetachedAsync(systemId);
        var objectType = detachedSystem.ObjectTypes!.Single();
        objectType.Attributes.Add(new ConnectedSystemObjectTypeAttribute
        {
            Name = "pwdFailureTime",
            Type = AttributeDataType.DateTime,
            AttributePlurality = AttributePlurality.MultiValued,
            Writability = AttributeWritability.ReadOnly,
            IsOperational = true,
            ConnectedSystemObjectType = objectType
        });
        await SaveSchemaAsync(detachedSystem);

        var reloaded = (await LoadDetachedAsync(systemId)).ObjectTypes!.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(reloaded.Attributes.Single(a => a.Name == "pwdFailureTime").IsOperational, Is.True);
            Assert.That(reloaded.Attributes.Single(a => a.Name == "pwdFailureTime").Selected, Is.False);
            Assert.That(reloaded.Attributes.Single(a => a.Name == "mail").IsOperational, Is.False);
        }
    }

    [Test]
    public async Task UpdateConnectedSystemSchemaAsync_ExistingAttributesFlagRestated_PersistsTheNewFlagAsync()
    {
        var systemId = await SeedAsync();

        // A refresh restates the flag on an attribute that already has a row: added on one pass, withdrawn on the next.
        var firstPass = await LoadDetachedAsync(systemId);
        firstPass.ObjectTypes!.Single().Attributes.Single(a => a.Name == "mail").IsOperational = true;
        await SaveSchemaAsync(firstPass);
        var afterFirst = (await LoadDetachedAsync(systemId)).ObjectTypes!.Single().Attributes.Single(a => a.Name == "mail");

        var secondPass = await LoadDetachedAsync(systemId);
        secondPass.ObjectTypes!.Single().Attributes.Single(a => a.Name == "mail").IsOperational = false;
        await SaveSchemaAsync(secondPass);
        var afterSecond = (await LoadDetachedAsync(systemId)).ObjectTypes!.Single().Attributes.Single(a => a.Name == "mail");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(afterFirst.IsOperational, Is.True);
            Assert.That(afterSecond.IsOperational, Is.False);
            Assert.That(afterSecond.Selected, Is.True, "Restating the flag must leave the administrator's selection alone.");
            Assert.That(afterSecond.Id, Is.EqualTo(afterFirst.Id), "The attribute keeps its row.");
        }
    }

    [Test]
    public async Task ColumnDefault_AttributeRowWrittenWithoutTheFlag_ReadsBackNotOperationalAsync()
    {
        // Every attribute discovered before the column existed gets the default when the migration adds it, and any
        // writer that does not name the column gets it too, so nothing reads as operational by accident.
        var systemId = await SeedAsync();
        await using (var rawContext = NewContext())
        {
            var objectTypeId = await rawContext.ConnectedSystemObjectTypes.Where(t => t.ConnectedSystemId == systemId).Select(t => t.Id).SingleAsync();
            await rawContext.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "ConnectedSystemAttributes" ("Name", "Type", "AttributePlurality", "Writability", "Selected", "IsExternalId",
                    "IsSecondaryExternalId", "Required", "SelectionLocked", "CaseExact", "CaseExactSetByAdministrator", "TypeSetByAdministrator",
                    "ConnectedSystemObjectTypeId", "Created")
                VALUES ('description', {(int)AttributeDataType.Text}, {(int)AttributePlurality.SingleValued}, {(int)AttributeWritability.Writable},
                    false, false, false, false, false, false, false, false, {objectTypeId}, now())
                """);
        }

        var reloaded = (await LoadDetachedAsync(systemId)).ObjectTypes!.Single().Attributes.Single(a => a.Name == "description");
        Assert.That(reloaded.IsOperational, Is.False);
    }

    [Test]
    public async Task GetObjectTypesAsync_OperationalAttribute_ReadsBackItsTagAsync()
    {
        // The object types list is what the REST API returns and PowerShell filters on: a different query from the
        // portal's, so it is checked on its own.
        var systemId = await SeedAsync();
        var detachedSystem = await LoadDetachedAsync(systemId);
        detachedSystem.ObjectTypes!.Single().Attributes.Single(a => a.Name == "mail").IsOperational = true;
        await SaveSchemaAsync(detachedSystem);

        await using var readContext = NewContext();
        var objectTypes = await new PostgresDataRepository(readContext).ConnectedSystems.GetObjectTypesAsync(systemId);

        Assert.That(objectTypes.Single().Attributes.Single(a => a.Name == "mail").IsOperational, Is.True);
    }

    private async Task<ConnectedSystem> LoadDetachedAsync(int systemId)
    {
        await using var loadContext = NewContext();
        var system = await new PostgresDataRepository(loadContext).ConnectedSystems.GetConnectedSystemAsync(systemId);
        Assert.That(system, Is.Not.Null);
        return system!;
    }

    private async Task SaveSchemaAsync(ConnectedSystem connectedSystem)
    {
        await using var saveContext = NewContext();
        await new PostgresDataRepository(saveContext).ConnectedSystems.UpdateConnectedSystemSchemaAsync(connectedSystem);
    }

    /// <summary>
    /// Seeds a Connected System with one Object Type carrying one selected, non-operational attribute.
    /// </summary>
    private async Task<int> SeedAsync()
    {
        await using var seed = NewContext();

        var connectorDefinition = new ConnectorDefinition { Name = "Test Connector", BuiltIn = true };
        var objectType = new ConnectedSystemObjectType { Name = "inetOrgPerson", Selected = true };
        objectType.Attributes =
        [
            new ConnectedSystemObjectTypeAttribute
            {
                Name = "mail", Type = AttributeDataType.Text, Selected = true, ConnectedSystemObjectType = objectType
            }
        ];
        var system = new ConnectedSystem
        {
            Name = "Test System",
            ConnectorDefinition = connectorDefinition,
            ObjectTypes = [objectType]
        };

        seed.ConnectorDefinitions.Add(connectorDefinition);
        seed.ConnectedSystems.Add(system);
        await seed.SaveChangesAsync();

        return system.Id;
    }
}
