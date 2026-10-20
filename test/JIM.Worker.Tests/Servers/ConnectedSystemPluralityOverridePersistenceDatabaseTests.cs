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
/// Real-PostgreSQL verification that an administrator's plurality, and the flag recording it as theirs (#2186), survive
/// the single attribute update the REST API and the portal use, and the schema save a refresh uses.
/// </summary>
/// <remarks>
/// The contexts are <c>NoTracking</c>, as the portal's are, so a save that forgot the column would show here as a flag
/// that never arrives. Opt-in via the same <c>JIM_TEST_RESET_*</c> environment variables as the other database tests;
/// ignored when <c>JIM_TEST_RESET_DB</c> is absent.
/// </remarks>
[TestFixture]
[Category("RequiresPostgres")]
public class ConnectedSystemPluralityOverridePersistenceDatabaseTests
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
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL plurality override persistence tests.");

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
    public async Task UpdateAttributeAsync_AdministratorsPlurality_PersistsWithItsFlagAsync()
    {
        var systemId = await SeedAsync();
        var attribute = Attribute(await LoadDetachedAsync(systemId));
        attribute.AttributePlurality = AttributePlurality.MultiValued;
        attribute.PluralitySetByAdministrator = true;

        await using (var saveContext = NewContext())
            await new PostgresDataRepository(saveContext).ConnectedSystems.UpdateAttributeAsync(attribute);

        var reloaded = Attribute(await LoadDetachedAsync(systemId));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(reloaded.AttributePlurality, Is.EqualTo(AttributePlurality.MultiValued));
            Assert.That(reloaded.PluralitySetByAdministrator, Is.True);
        }
    }

    [Test]
    public async Task UpdateConnectedSystemSchemaAsync_AdministratorsPlurality_SurvivesASchemaSaveAsync()
    {
        var systemId = await SeedAsync();
        var system = await LoadDetachedAsync(systemId);
        Attribute(system).AttributePlurality = AttributePlurality.MultiValued;
        Attribute(system).PluralitySetByAdministrator = true;
        await SaveSchemaAsync(system);

        var reloaded = Attribute(await LoadDetachedAsync(systemId));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(reloaded.AttributePlurality, Is.EqualTo(AttributePlurality.MultiValued));
            Assert.That(reloaded.PluralitySetByAdministrator, Is.True);
        }
    }

    [Test]
    public async Task Column_AttributeRowWrittenWithoutTheFlag_ReadsBackAsDiscoveredAsync()
    {
        var systemId = await SeedAsync();

        Assert.That(Attribute(await LoadDetachedAsync(systemId)).PluralitySetByAdministrator, Is.False);
    }

    private static ConnectedSystemObjectTypeAttribute Attribute(ConnectedSystem system) =>
        system.ObjectTypes!.Single().Attributes.Single(a => a.Name == "member");

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
    /// Seeds a Connected System with one Object Type carrying one selected, discovered single-valued attribute.
    /// </summary>
    private async Task<int> SeedAsync()
    {
        await using var seed = NewContext();

        var connectorDefinition = new ConnectorDefinition { Name = "Test Connector", BuiltIn = true };
        var objectType = new ConnectedSystemObjectType { Name = "User", Selected = true };
        objectType.Attributes =
        [
            new ConnectedSystemObjectTypeAttribute
            {
                Name = "member", Type = AttributeDataType.Text, Selected = true, ConnectedSystemObjectType = objectType
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
