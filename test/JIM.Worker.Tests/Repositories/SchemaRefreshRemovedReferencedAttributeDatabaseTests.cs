// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Servers;
using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.PostgresData;
using JIM.Scim;
using JIM.Scim.Discovery;
using JIM.Scim.Schema;
using JIM.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NUnit.Framework;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// Real-PostgreSQL confirmation of what a schema refresh does with an attribute the Connector no longer discovers
/// while an Attribute Flow mapping still reads it (#2153, Upgrade and Migration item 5). After S2 the SCIM Connector
/// stops discovering <c>roles.primary</c>, so this is the path every SCIM Connected System mapping one of those
/// attributes takes on its first refresh.
/// </summary>
/// <remarks>
/// The refresh reports the attribute as removed, and the plain apply retains its row (#782): the mapping keeps a
/// valid foreign key, and nothing fails on save. Removing it is the administrator's decision through the schema
/// refresh decision's "Apply and Remove". The context is NoTracking, matching JIM.Web, where schema refreshes run.
/// </remarks>
[TestFixture]
[Category("RequiresPostgres")]
public class SchemaRefreshRemovedReferencedAttributeDatabaseTests
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
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL schema refresh tests.");

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
    public async Task UpdateConnectedSystemSchemaAsync_AttributeNoLongerDiscoveredButMapped_IsReportedRemovedAndRetainedAsync()
    {
        int systemId;
        int primaryAttributeId;
        int mappingId;
        await using (var seed = NewContext())
        {
            var connectorDefinition = new ConnectorDefinition { Name = "Test SCIM Connector", BuiltIn = true };
            var system = new ConnectedSystem { Name = "SCIM Service", ConnectorDefinition = connectorDefinition };
            var userType = new ConnectedSystemObjectType { ConnectedSystem = system, Name = "User", Selected = true };
            var rolesValue = new ConnectedSystemObjectTypeAttribute
            {
                ConnectedSystemObjectType = userType, Name = "roles.value", Type = AttributeDataType.Text,
                AttributePlurality = AttributePlurality.MultiValued, Selected = true
            };
            var rolesPrimary = new ConnectedSystemObjectTypeAttribute
            {
                ConnectedSystemObjectType = userType, Name = "roles.primary", Type = AttributeDataType.Boolean,
                AttributePlurality = AttributePlurality.MultiValued, Selected = true
            };
            userType.Attributes.Add(rolesValue);
            userType.Attributes.Add(rolesPrimary);
            system.ObjectTypes = [userType];

            var mvType = new MetaverseObjectType { Name = "S2 Person", PluralName = "S2 People" };
            var mvAttribute = new MetaverseAttribute { Name = "S2 Has Primary Role", Type = AttributeDataType.Boolean };
            seed.ConnectorDefinitions.Add(connectorDefinition);
            seed.ConnectedSystems.Add(system);
            seed.MetaverseObjectTypes.Add(mvType);
            seed.MetaverseAttributes.Add(mvAttribute);
            await seed.SaveChangesAsync();

            var rule = new SyncRule
            {
                Name = "SCIM Users Inbound",
                ConnectedSystemId = system.Id,
                ConnectedSystemObjectTypeId = userType.Id,
                MetaverseObjectTypeId = mvType.Id,
                Direction = SyncRuleDirection.Import
            };
            var mapping = new SyncRuleMapping { SyncRule = rule, TargetMetaverseAttributeId = mvAttribute.Id };
            mapping.Sources.Add(new SyncRuleMappingSource { Order = 0, ConnectedSystemAttributeId = rolesPrimary.Id });
            rule.AttributeFlowRules.Add(mapping);
            seed.SyncRules.Add(rule);
            await seed.SaveChangesAsync();

            systemId = system.Id;
            primaryAttributeId = rolesPrimary.Id;
            mappingId = mapping.Id;
        }

        // Load detached, as the portal and REST API do, then merge the schema the SCIM Connector now discovers:
        // roles.value, and no roles.primary.
        ConnectedSystem detachedSystem;
        await using (var loadContext = NewContext())
        {
            detachedSystem = (await new PostgresDataRepository(loadContext).ConnectedSystems.GetConnectedSystemAsync(systemId))!;
        }

        var roles = new ScimSchemaAttribute
        {
            Name = "roles",
            Type = ScimAttributeTypes.Complex,
            MultiValued = true,
            SubAttributes =
            [
                new ScimSchemaAttribute { Name = "value", Type = ScimAttributeTypes.String },
                new ScimSchemaAttribute { Name = "primary", Type = ScimAttributeTypes.Boolean }
            ]
        };
        var discovered = new ConnectorSchemaObjectType("User");
        discovered.Attributes.AddRange(ScimAttributeMapper.Flatten(roles, ScimUrns.User).Select(a => a.ToConnectorSchemaAttribute()));

        var result = ConnectedSystemServer.MergeSchemaIntoConnectedSystem(detachedSystem, new ConnectorSchema { ObjectTypes = [discovered] });

        await using (var saveContext = NewContext())
        {
            var saveRepository = new PostgresDataRepository(saveContext);
            Assert.That(async () => await saveRepository.ConnectedSystems.UpdateConnectedSystemSchemaAsync(detachedSystem), Throws.Nothing);
        }

        await using var verify = NewContext();
        var persistedPrimary = await verify.ConnectedSystemAttributes.SingleOrDefaultAsync(a => a.Id == primaryAttributeId);
        var sourcesReadingPrimary = await verify.SyncRuleMappingSources.CountAsync(s => s.ConnectedSystemAttributeId == primaryAttributeId);
        var mappingStillExists = await verify.SyncRuleMappings.AnyAsync(m => m.Id == mappingId);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.RemovedAttributes.GetValueOrDefault("User"), Is.EqualTo(new[] { "roles.primary" }));
            Assert.That(persistedPrimary, Is.Not.Null, "The plain apply retains a removed attribute's row (#782).");
            Assert.That(mappingStillExists, Is.True);
            Assert.That(sourcesReadingPrimary, Is.EqualTo(1), "The mapping still reads the retained row.");
        }
    }
}
