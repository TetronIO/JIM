// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.PostgresData;
using JIM.PostgresData.Repositories;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// Real-PostgreSQL cover for #1606: <see cref="MetaverseRepository.GetMetaverseObjectWithProvenanceAsync"/>
/// (which backs GET /api/v1/metaverse/objects/{id}) never included a Metaverse Object's joined Connected
/// System Objects, so MetaverseObjectDto.ConnectedSystemObjects always serialised empty. The in-memory
/// test provider auto-tracks navigation properties regardless of Include chains, so it cannot see a
/// missing Include; this needs a real provider.
/// <para>
/// Also proves the filtered include added for the fix actually translates: it must load exactly the
/// naming attribute values (per <see cref="ObjectNaming.ConnectedSystemNameAttributes"/>) plus the
/// external id value, and nothing else, so <see cref="ConnectedSystemObject.NameOrId"/> resolves while a
/// non-naming attribute value stays unloaded.
/// </para>
/// </summary>
[TestFixture]
[Category("RequiresPostgres")]
public class MetaverseObjectJoinedConnectedSystemObjectsDatabaseTests
{
    private string _connectionString = null!;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        TestUtilities.SetEnvironmentVariables();

        var dbName = Environment.GetEnvironmentVariable("JIM_TEST_RESET_DB");
        if (string.IsNullOrEmpty(dbName))
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL joined Connected System Objects tests.");

        var host = Environment.GetEnvironmentVariable("JIM_TEST_RESET_HOST") ?? "localhost";
        var user = Environment.GetEnvironmentVariable("JIM_TEST_RESET_USER") ?? "postgres";
        var pass = Environment.GetEnvironmentVariable("JIM_TEST_RESET_PASSWORD") ?? "postgres";
        var port = Environment.GetEnvironmentVariable("JIM_TEST_RESET_PORT") ?? "5432";
        _connectionString = $"Host={host};Port={port};Database={dbName};Username={user};Password={pass}";

        using var ctx = NewContext();
        ctx.Database.Migrate();
    }

    private JimDbContext NewContext() => new(new DbContextOptionsBuilder<JimDbContext>()
        .UseNpgsql(_connectionString)
        .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking)
        .Options);

    private sealed record Seeded(
        Guid MetaverseObjectId, Guid ConnectedSystemObjectId, int ConnectedSystemId, string ConnectedSystemName);

    private async Task<Seeded> SeedTopologyAsync(string suffix)
    {
        await using var seedCtx = NewContext();

        var definition = new ConnectorDefinition { Name = $"joined-cso-def-{suffix}" };
        seedCtx.ConnectorDefinitions.Add(definition);
        await seedCtx.SaveChangesAsync();

        var system = new ConnectedSystem
        {
            Name = $"joined-cso-system-{suffix}",
            ConnectorDefinitionId = definition.Id,
            ObjectMatchingRuleMode = ObjectMatchingRuleMode.ConnectedSystem
        };
        seedCtx.ConnectedSystems.Add(system);
        await seedCtx.SaveChangesAsync();

        var objectType = new ConnectedSystemObjectType { ConnectedSystemId = system.Id, Name = "user", Selected = true };
        seedCtx.ConnectedSystemObjectTypes.Add(objectType);
        await seedCtx.SaveChangesAsync();

        // Three attributes: the naming attribute NameOrId must resolve from, the external id attribute
        // NameOrId falls back to, and a non-naming attribute that must NOT survive the filtered include.
        var displayNameAttribute = new ConnectedSystemObjectTypeAttribute
        {
            ConnectedSystemObjectType = objectType,
            Name = "displayName",
            Type = AttributeDataType.Text,
            AttributePlurality = AttributePlurality.SingleValued,
            Selected = true
        };
        var employeeIdAttribute = new ConnectedSystemObjectTypeAttribute
        {
            ConnectedSystemObjectType = objectType,
            Name = "employeeId",
            Type = AttributeDataType.Text,
            AttributePlurality = AttributePlurality.SingleValued,
            Selected = true,
            IsExternalId = true
        };
        var jobTitleAttribute = new ConnectedSystemObjectTypeAttribute
        {
            ConnectedSystemObjectType = objectType,
            Name = "jobTitle",
            Type = AttributeDataType.Text,
            AttributePlurality = AttributePlurality.SingleValued,
            Selected = true
        };
        seedCtx.ConnectedSystemAttributes.AddRange(displayNameAttribute, employeeIdAttribute, jobTitleAttribute);
        await seedCtx.SaveChangesAsync();

        var mvoType = new MetaverseObjectType { Name = $"joined-cso-person-{suffix}", PluralName = $"joined-cso-people-{suffix}" };
        seedCtx.MetaverseObjectTypes.Add(mvoType);
        await seedCtx.SaveChangesAsync();

        var mvo = new MetaverseObject { Id = Guid.NewGuid(), Type = mvoType, Created = DateTime.UtcNow };
        seedCtx.MetaverseObjects.Add(mvo);
        await seedCtx.SaveChangesAsync();

        var cso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = system.Id,
            TypeId = objectType.Id,
            MetaverseObjectId = mvo.Id,
            ExternalIdAttributeId = employeeIdAttribute.Id
        };
        seedCtx.ConnectedSystemObjects.Add(cso);
        await seedCtx.SaveChangesAsync();

        seedCtx.ConnectedSystemObjectAttributeValues.AddRange(
            new ConnectedSystemObjectAttributeValue { ConnectedSystemObject = cso, AttributeId = displayNameAttribute.Id, StringValue = "Alice Example" },
            new ConnectedSystemObjectAttributeValue { ConnectedSystemObject = cso, AttributeId = employeeIdAttribute.Id, StringValue = "EMP123" },
            new ConnectedSystemObjectAttributeValue { ConnectedSystemObject = cso, AttributeId = jobTitleAttribute.Id, StringValue = "Engineer" });
        await seedCtx.SaveChangesAsync();

        return new Seeded(mvo.Id, cso.Id, system.Id, system.Name);
    }

    [Test]
    public async Task GetMetaverseObjectWithProvenanceAsync_JoinedConnectedSystemObject_PopulatesConnectedSystemObjectsAsync()
    {
        var seeded = await SeedTopologyAsync(Guid.NewGuid().ToString("N")[..8]);

        await using var assertCtx = NewContext();
        var repo = new MetaverseRepository(new PostgresDataRepository(assertCtx));

        var mvo = await repo.GetMetaverseObjectWithProvenanceAsync(seeded.MetaverseObjectId);

        Assert.That(mvo, Is.Not.Null);
        var loadedCso = mvo!.ConnectedSystemObjects.SingleOrDefault(c => c.Id == seeded.ConnectedSystemObjectId);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(loadedCso, Is.Not.Null, "the joined Connected System Object must be loaded onto the Metaverse Object");
            Assert.That(loadedCso!.ConnectedSystem, Is.Not.Null, "the ConnectedSystem navigation must be populated");
            Assert.That(loadedCso.ConnectedSystem.Id, Is.EqualTo(seeded.ConnectedSystemId));
            Assert.That(loadedCso.ConnectedSystem.Name, Is.EqualTo(seeded.ConnectedSystemName));
            Assert.That(loadedCso.NameOrId, Is.EqualTo("Alice Example"),
                "NameOrId must resolve from the filtered naming-attribute include plus its Attribute navigation");
            Assert.That(loadedCso.AttributeValues.Any(av => av.StringValue == "Engineer"), Is.False,
                "a non-naming, non-external-id attribute value must NOT be loaded by the filtered include");
        }
    }
}
