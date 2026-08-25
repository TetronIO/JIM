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
/// The population stream a Configuration Change Preview walks has to yield objects that can NAME themselves
/// (#1450).
///
/// <see cref="ConnectedSystemObject.Name"/> ranks candidate naming attributes by each value's ATTRIBUTE entity, so
/// a query that brings back the values without their attributes leaves every candidate null. The object then falls
/// through to its External ID, which for a directory is a GUID, and a preview's drill-down renders a column of
/// them for the objects an administrator opened it to recognise.
/// </summary>
/// <remarks>
/// Database-backed deliberately: the in-memory provider resolves navigation properties from its identity map
/// whether or not the query asked for them, so this defect is invisible to the unit suite by construction. It is
/// the failure mode the repository's own guidance warns about under "Prefer FK Scalars Over Navigation Checks".
///
/// Opt-in via the same <c>JIM_TEST_RESET_*</c> environment variables as the other database-backed tests.
/// </remarks>
[TestFixture]
[Category("RequiresPostgres")]
public class ConnectedSystemObjectStreamNamingDatabaseTests
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
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL stream naming tests.");

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
    public async Task StreamConnectedSystemObjectsOfType_ObjectsWithNamingAttributes_YieldTheirNamesNotTheirExternalIdsAsync()
    {
        var (systemId, typeId) = await SeedAsync();

        await using var ctx = NewContext();
        var repo = new PostgresDataRepository(ctx);

        var streamed = new List<ConnectedSystemObject>();
        await foreach (var cso in repo.ConnectedSystems.StreamConnectedSystemObjectsOfType(systemId, typeId))
            streamed.Add(cso);

        Assert.That(streamed, Has.Count.EqualTo(3));
        using (Assert.EnterMultipleScope())
        {
            // The direct assertion of what the query must bring back. Checking the names alone is not enough: an
            // in-memory provider, or a fixture small enough for the context to have the attributes to hand anyway,
            // resolves them regardless and the test then cannot tell the two versions of the query apart.
            Assert.That(streamed.SelectMany(cso => cso.AttributeValues).All(av => av.Attribute != null), Is.True,
                "every value needs its attribute entity, which is what the name ranking reads");
            Assert.That(streamed.Select(cso => cso.Name),
                Is.EquivalentTo(new[] { "Ada Lovelace", "Grace Hopper", "Alan Turing" }));
            Assert.That(streamed.Select(cso => cso.NameOrId),
                Is.EquivalentTo(new[] { "Ada Lovelace", "Grace Hopper", "Alan Turing" }),
                "falling through to the External ID is what filled a preview drill-down with GUIDs");
        }
    }

    /// <summary>
    /// Three Connected System Objects over an Object Type shaped like a real directory's: fifteen attributes, a
    /// GUID External ID (entryUUID / objectGUID), and a displayName that is the first-ranked naming candidate.
    /// </summary>
    private async Task<(int SystemId, int TypeId)> SeedAsync()
    {
        await using var ctx = NewContext();

        var connectorDefinition = new ConnectorDefinition { Name = "Naming Test Connector", BuiltIn = false };
        var connectedSystem = new ConnectedSystem
        {
            Name = "Naming Test Source",
            ObjectMatchingRuleMode = ObjectMatchingRuleMode.SyncRule,
            ConnectorDefinition = connectorDefinition
        };
        ctx.ConnectorDefinitions.Add(connectorDefinition);
        ctx.ConnectedSystems.Add(connectedSystem);
        await ctx.SaveChangesAsync();

        string[] textAttributeNames =
        [
            "displayName", "cn", "uid", "mail", "givenName", "sn", "title", "o",
            "departmentNumber", "employeeNumber", "employeeType", "distinguishedName", "objectClass", "telephoneNumber"
        ];

        var csoType = new ConnectedSystemObjectType
        {
            ConnectedSystemId = connectedSystem.Id,
            Name = "jimPerson",
            Selected = true,
            Attributes =
            [
                new ConnectedSystemObjectTypeAttribute { Name = "entryUUID", Type = AttributeDataType.Guid, IsExternalId = true, Selected = true },
                .. textAttributeNames.Select(name =>
                    new ConnectedSystemObjectTypeAttribute { Name = name, Type = AttributeDataType.Text, Selected = true })
            ]
        };
        ctx.ConnectedSystemObjectTypes.Add(csoType);
        await ctx.SaveChangesAsync();

        var externalId = csoType.Attributes.Single(a => a.IsExternalId);
        var textAttributes = csoType.Attributes.Where(a => !a.IsExternalId).ToList();

        foreach (var name in (string[])["Ada Lovelace", "Grace Hopper", "Alan Turing"])
        {
            var cso = new ConnectedSystemObject
            {
                Id = Guid.NewGuid(),
                ConnectedSystemId = connectedSystem.Id,
                TypeId = csoType.Id,
                Status = ConnectedSystemObjectStatus.Normal
            };
            cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
            {
                Id = Guid.NewGuid(),
                AttributeId = externalId.Id,
                GuidValue = Guid.NewGuid()
            });

            foreach (var attribute in textAttributes)
            {
                cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
                {
                    Id = Guid.NewGuid(),
                    AttributeId = attribute.Id,
                    StringValue = attribute.Name == "displayName" ? name : $"{attribute.Name}-value"
                });
            }

            ctx.ConnectedSystemObjects.Add(cso);
        }

        await ctx.SaveChangesAsync();
        return (connectedSystem.Id, csoType.Id);
    }
}
