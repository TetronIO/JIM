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
/// Real-PostgreSQL verification that a date attribute's resolution (#2153, decision D2) and whether an administrator
/// chose it survive the schema save the portal and the Worker both use, are restated by a later one, survive the single
/// attribute update the REST API uses, and read back through the query the REST API and PowerShell are served from.
/// </summary>
/// <remarks>
/// The resolution column is nullable with no default, so a row written before it existed (or by a writer that does not
/// name it) reads back as none, which JIM treats as the microsecond. The contexts are <c>NoTracking</c>, as the portal's
/// are, so a save that forgot the columns would show here as a resolution that never arrives.
///
/// Opt-in via the same <c>JIM_TEST_RESET_*</c> environment variables as the other database tests; ignored when
/// <c>JIM_TEST_RESET_DB</c> is absent.
/// </remarks>
[TestFixture]
[Category("RequiresPostgres")]
public class ConnectedSystemDateTimeResolutionPersistenceDatabaseTests
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
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL date resolution persistence tests.");

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
    public async Task UpdateConnectedSystemSchemaAsync_DiscoveredResolution_PersistsAndIsRestatedAsync()
    {
        var systemId = await SeedAsync();

        var firstPass = await LoadDetachedAsync(systemId);
        Attribute(firstPass).DateTimeResolution = DateTimeResolution.Second;
        await SaveSchemaAsync(firstPass);
        var afterFirst = Attribute(await LoadDetachedAsync(systemId));

        var secondPass = await LoadDetachedAsync(systemId);
        Attribute(secondPass).DateTimeResolution = DateTimeResolution.Millisecond;
        await SaveSchemaAsync(secondPass);
        var afterSecond = Attribute(await LoadDetachedAsync(systemId));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(afterFirst.DateTimeResolution, Is.EqualTo(DateTimeResolution.Second));
            Assert.That(afterSecond.DateTimeResolution, Is.EqualTo(DateTimeResolution.Millisecond));
            Assert.That(afterSecond.DateTimeResolutionSetByAdministrator, Is.False);
            Assert.That(afterSecond.Id, Is.EqualTo(afterFirst.Id), "The attribute keeps its row.");
        }
    }

    [Test]
    public async Task UpdateAttributeAsync_AdministratorsResolution_PersistsWithItsFlagAsync()
    {
        var systemId = await SeedAsync();
        var attribute = Attribute(await LoadDetachedAsync(systemId));
        attribute.DateTimeResolution = DateTimeResolution.HundredMilliseconds;
        attribute.DateTimeResolutionSetByAdministrator = true;

        await using (var saveContext = NewContext())
            await new PostgresDataRepository(saveContext).ConnectedSystems.UpdateAttributeAsync(attribute);

        var reloaded = Attribute(await LoadDetachedAsync(systemId));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(reloaded.DateTimeResolution, Is.EqualTo(DateTimeResolution.HundredMilliseconds));
            Assert.That(reloaded.DateTimeResolutionSetByAdministrator, Is.True);
        }
    }

    [Test]
    public async Task Column_AttributeRowWrittenWithoutAResolution_ReadsBackAsNoneAsync()
    {
        var systemId = await SeedAsync();

        var reloaded = Attribute(await LoadDetachedAsync(systemId));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(reloaded.DateTimeResolution, Is.Null);
            Assert.That(reloaded.DateTimeResolutionSetByAdministrator, Is.False);
        }
    }

    [Test]
    public async Task GetObjectTypesAsync_Resolution_ReadsBackAsync()
    {
        var systemId = await SeedAsync();
        var detachedSystem = await LoadDetachedAsync(systemId);
        Attribute(detachedSystem).DateTimeResolution = DateTimeResolution.Day;
        await SaveSchemaAsync(detachedSystem);

        await using var readContext = NewContext();
        var objectTypes = await new PostgresDataRepository(readContext).ConnectedSystems.GetObjectTypesAsync(systemId);

        Assert.That(objectTypes.Single().Attributes.Single(a => a.Name == "startDate").DateTimeResolution, Is.EqualTo(DateTimeResolution.Day));
    }

    private static ConnectedSystemObjectTypeAttribute Attribute(ConnectedSystem system) =>
        system.ObjectTypes!.Single().Attributes.Single(a => a.Name == "startDate");

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
    /// Seeds a Connected System with one Object Type carrying one selected date attribute with no resolution.
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
                Name = "startDate", Type = AttributeDataType.DateTime, Selected = true, ConnectedSystemObjectType = objectType
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
