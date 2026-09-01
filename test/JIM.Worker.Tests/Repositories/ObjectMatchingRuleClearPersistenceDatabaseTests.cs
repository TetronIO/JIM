// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Security;
using JIM.Models.Staging;
using JIM.PostgresData;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// Real-PostgreSQL cover for the Synchronisation Rule save path's matching-rule clears (#1589).
/// <para>
/// The save path clears a rule's own Object Matching Rules where the system's matching mode makes them inert.
/// Both owner foreign keys on an Object Matching Rule are optional, and EF Core answers a severed optional
/// relationship by nulling the foreign key, not by deleting the row, so a plain collection Clear() persisted
/// parentless rules: invisible to every scope the engine or the portal reads, and fatal to the owning Connected
/// System's deletion, whose sequence removes matching rules by scope and then cannot delete the attributes the
/// orphans' sources still reference. Only a real provider can see this; the in-memory provider has no foreign
/// keys and the unit fixtures mock the repository entirely.
/// </para>
/// </summary>
[TestFixture]
[Category("RequiresPostgres")]
public class ObjectMatchingRuleClearPersistenceDatabaseTests
{
    private string _connectionString = null!;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        TestUtilities.SetEnvironmentVariables();

        var dbName = Environment.GetEnvironmentVariable("JIM_TEST_RESET_DB");
        if (string.IsNullOrEmpty(dbName))
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL matching-rule clear tests.");

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

    [Test]
    public async Task CreateOrUpdateSyncRuleAsync_ClearingInertMatchingRules_DeletesTheRowsRatherThanOrphaningThemAsync()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];

        int syncRuleId;
        int matchingRuleId;
        int attributeId;
        Guid apiKeyId;
        await using (var seedCtx = NewContext())
        {
            var definition = new ConnectorDefinition { Name = $"omr-clear-def-{suffix}" };
            seedCtx.ConnectorDefinitions.Add(definition);
            await seedCtx.SaveChangesAsync();

            // Simple matching mode: the export rule's own matching rules are the inert shape the save clears.
            var system = new ConnectedSystem
            {
                Name = $"omr-clear-system-{suffix}",
                ConnectorDefinitionId = definition.Id,
                ObjectMatchingRuleMode = ObjectMatchingRuleMode.ConnectedSystem
            };
            seedCtx.ConnectedSystems.Add(system);
            await seedCtx.SaveChangesAsync();

            var objectType = new ConnectedSystemObjectType { ConnectedSystemId = system.Id, Name = "user", Selected = true };
            seedCtx.ConnectedSystemObjectTypes.Add(objectType);
            await seedCtx.SaveChangesAsync();

            var attribute = new ConnectedSystemObjectTypeAttribute { ConnectedSystemObjectType = objectType, Name = "employeeId" };
            seedCtx.ConnectedSystemAttributes.Add(attribute);
            await seedCtx.SaveChangesAsync();
            attributeId = attribute.Id;

            var mvoType = new MetaverseObjectType { Name = $"omr-clear-person-{suffix}", PluralName = $"omr-clear-people-{suffix}" };
            seedCtx.MetaverseObjectTypes.Add(mvoType);
            await seedCtx.SaveChangesAsync();

            var syncRule = new SyncRule
            {
                Name = $"omr-clear-export-{suffix}",
                Direction = SyncRuleDirection.Export,
                ConnectedSystemId = system.Id,
                ConnectedSystemObjectTypeId = objectType.Id,
                MetaverseObjectTypeId = mvoType.Id,
                ObjectMatchingRules =
                [
                    new ObjectMatchingRule
                    {
                        Order = 0,
                        Sources = [new ObjectMatchingRuleSource { Order = 0, ConnectedSystemAttributeId = attribute.Id }]
                    }
                ]
            };
            seedCtx.SyncRules.Add(syncRule);
            await seedCtx.SaveChangesAsync();
            syncRuleId = syncRule.Id;
            matchingRuleId = syncRule.ObjectMatchingRules[0].Id;

            var apiKey = new ApiKey
            {
                Id = Guid.NewGuid(),
                Name = $"omr-clear-key-{suffix}",
                KeyHash = "hash",
                KeyPrefix = "test",
                IsEnabled = true,
                Created = DateTime.UtcNow
            };
            seedCtx.ApiKeys.Add(apiKey);
            await seedCtx.SaveChangesAsync();
            apiKeyId = apiKey.Id;
        }

        // Mirror JIM.Web: a NoTracking-default context, the rule loaded via GetSyncRuleAsync (which tracks it),
        // then saved through the application layer, whose simple-mode validation clears the inert rules.
        await using (var saveCtx = NewContext())
        {
            using var application = new JimApplication(new PostgresDataRepository(saveCtx));
            var syncRule = await application.ConnectedSystems.GetSyncRuleAsync(syncRuleId);
            Assert.That(syncRule, Is.Not.Null);
            var apiKey = await application.Repository.ApiKeys.GetByIdAsync(apiKeyId);
            Assert.That(apiKey, Is.Not.Null);

            var result = await application.ConnectedSystems.CreateOrUpdateSyncRuleAsync(syncRule!, apiKey!);
            Assert.That(result, Is.True);
        }

        await using var assertCtx = NewContext();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(await assertCtx.ObjectMatchingRules.AnyAsync(r => r.Id == matchingRuleId), Is.False,
                "the cleared matching rule's row must be deleted, not left orphaned with both owner foreign keys null");
            Assert.That(await assertCtx.ObjectMatchingRuleSources
                    .AnyAsync(src => src.ConnectedSystemAttributeId == attributeId),
                Is.False,
                "no source row may survive either; a lingering attribute reference is what blocks the " +
                "Connected System's deletion");
        }
    }
}
