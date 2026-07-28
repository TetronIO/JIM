// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Models.Core;
using JIM.Models.Staging;
using JIM.PostgresData;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// Real-PostgreSQL verification of the two reads that feed the Attribute Flow editor's Standard Mapping hints
/// (issue #1122): the mappings held by a Metaverse Object Type's attributes, and the standard a Connected
/// System's Connector declares. Both traverse relationships (a many-to-many binding and a Connector Definition
/// reference) that the in-memory provider resolves from its own tracked graph rather than from the query, so
/// only a real database proves the queries themselves are right. Opt-in via <c>JIM_TEST_RESET_DB</c>.
/// </summary>
[TestFixture]
[Category("RequiresPostgres")]
public class StandardMappingHintDataDatabaseTests
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
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL Standard Mapping hint data tests.");

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
    public async Task GetStandardMappingsForObjectTypeAsync_ReturnsOnlyTheBoundAttributesMappingsAsync()
    {
        int userTypeId;
        await using (var seed = NewContext())
        {
            var userType = new MetaverseObjectType { Name = "User", PluralName = "Users" };
            var groupType = new MetaverseObjectType { Name = "Group", PluralName = "Groups" };
            seed.MetaverseObjectTypes.AddRange(userType, groupType);

            var firstName = new MetaverseAttribute { Name = "First Name", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.SingleValued, MetaverseObjectTypes = [userType] };
            firstName.StandardMappings.Add(new MetaverseAttributeStandardMapping { Standard = AttributeStandard.Scim, CounterpartName = "name.givenName" });
            firstName.StandardMappings.Add(new MetaverseAttributeStandardMapping { Standard = AttributeStandard.Ldap, CounterpartName = "givenName" });

            // Bound to the User type but carrying no mappings: it must simply contribute nothing.
            var employeeStartDate = new MetaverseAttribute { Name = "Employee Start Date", Type = AttributeDataType.DateTime, AttributePlurality = AttributePlurality.SingleValued, MetaverseObjectTypes = [userType] };

            // Bound to a different Object Type: its mappings must not leak into the User type's hints.
            var groupScope = new MetaverseAttribute { Name = "Group Scope", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.SingleValued, MetaverseObjectTypes = [groupType] };
            groupScope.StandardMappings.Add(new MetaverseAttributeStandardMapping { Standard = AttributeStandard.Ldap, CounterpartName = "groupType" });

            seed.MetaverseAttributes.AddRange(firstName, employeeStartDate, groupScope);
            await seed.SaveChangesAsync();
            userTypeId = userType.Id;
        }

        await using var read = NewContext();
        var jim = new JimApplication(new PostgresDataRepository(read));

        var mappings = await jim.Metaverse.GetStandardMappingsForObjectTypeAsync(userTypeId);

        Assert.That(mappings, Has.Count.EqualTo(2));
        Assert.That(mappings.Select(m => m.CounterpartName), Is.EquivalentTo(new[] { "name.givenName", "givenName" }));
        Assert.That(mappings.All(m => m.MetaverseAttributeId > 0), Is.True, "the hints are keyed on the attribute id");
        Assert.That(mappings.Any(m => m.CounterpartName == "groupType"), Is.False, "another Object Type's mappings must not be returned");
    }

    [Test]
    public async Task GetStandardMappingsForObjectTypeAsync_ObjectTypeWithNoMappings_ReturnsEmptyAsync()
    {
        int typeId;
        await using (var seed = NewContext())
        {
            var objectType = new MetaverseObjectType { Name = "Device", PluralName = "Devices" };
            seed.MetaverseObjectTypes.Add(objectType);
            seed.MetaverseAttributes.Add(new MetaverseAttribute { Name = "Asset Tag", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.SingleValued, MetaverseObjectTypes = [objectType] });
            await seed.SaveChangesAsync();
            typeId = objectType.Id;
        }

        await using var read = NewContext();
        var jim = new JimApplication(new PostgresDataRepository(read));

        Assert.That(await jim.Metaverse.GetStandardMappingsForObjectTypeAsync(typeId), Is.Empty);
    }

    [Test]
    public async Task GetConnectedSystemSchemaStandardAsync_ReturnsTheConnectorsDeclaredStandardAsync()
    {
        int connectedSystemId;
        await using (var seed = NewContext())
        {
            var definition = new ConnectorDefinition { Name = "LDAP Connector", BuiltIn = true, SchemaStandard = AttributeStandard.Ldap };
            var connectedSystem = new ConnectedSystem { Name = "Corporate AD", ConnectorDefinition = definition };
            seed.ConnectedSystems.Add(connectedSystem);
            await seed.SaveChangesAsync();
            connectedSystemId = connectedSystem.Id;
        }

        await using var read = NewContext();
        var jim = new JimApplication(new PostgresDataRepository(read));

        Assert.That(await jim.ConnectedSystems.GetConnectedSystemSchemaStandardAsync(connectedSystemId), Is.EqualTo(AttributeStandard.Ldap));
    }

    [Test]
    public async Task GetConnectedSystemSchemaStandardAsync_ConnectorDeclaringNoStandard_ReturnsNotSetAsync()
    {
        int connectedSystemId;
        await using (var seed = NewContext())
        {
            var definition = new ConnectorDefinition { Name = "File Connector", BuiltIn = true };
            var connectedSystem = new ConnectedSystem { Name = "HR Extract", ConnectorDefinition = definition };
            seed.ConnectedSystems.Add(connectedSystem);
            await seed.SaveChangesAsync();
            connectedSystemId = connectedSystem.Id;
        }

        await using var read = NewContext();
        var jim = new JimApplication(new PostgresDataRepository(read));

        Assert.That(await jim.ConnectedSystems.GetConnectedSystemSchemaStandardAsync(connectedSystemId), Is.EqualTo(AttributeStandard.NotSet));
    }

    [Test]
    public async Task GetConnectedSystemSchemaStandardAsync_UnknownConnectedSystem_ReturnsNotSetAsync()
    {
        await using var read = NewContext();
        var jim = new JimApplication(new PostgresDataRepository(read));

        Assert.That(await jim.ConnectedSystems.GetConnectedSystemSchemaStandardAsync(9999), Is.EqualTo(AttributeStandard.NotSet));
    }
}
