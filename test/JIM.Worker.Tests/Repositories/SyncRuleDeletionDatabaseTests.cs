// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.PostgresData;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// Real-PostgreSQL cover for deleting a Synchronisation Rule.
/// <para>
/// An Object Matching Rule belongs to exactly one of two owners, and which one decides what it is. In Simple
/// mode it hangs off a Connected System Object Type and matches every account of that type; in Advanced mode it
/// hangs off a Synchronisation Rule and matches only what that rule brings in. Either way it is contained by its
/// owner and means nothing without it, so deleting the owner has to take it too.
/// </para>
/// <para>
/// Both foreign keys were left to EF's convention, which for an optional reference is <c>ClientSetNull</c>: the
/// rule survived its owner's deletion with a null owner, belonging to nothing and reachable from nowhere. No
/// error was reported, because nulling the reference is what the convention asks for. The in-memory provider
/// cannot see the difference, so only a real provider tells these tests anything.
/// </para>
/// </summary>
[TestFixture]
[Category("RequiresPostgres")]
public class SyncRuleDeletionDatabaseTests
{
    private string _connectionString = null!;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        var dbName = Environment.GetEnvironmentVariable("JIM_TEST_RESET_DB");
        if (string.IsNullOrEmpty(dbName))
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL Synchronisation Rule deletion tests.");

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

    private sealed record Seeded(int SyncRuleId, int AdvancedRuleId, int SimpleRuleId, int ObjectTypeId);

    /// <summary>
    /// Seeds one Synchronisation Rule carrying an Advanced-mode Object Matching Rule, and beside it a Simple-mode
    /// Object Matching Rule on the same Connected System Object Type. The second one is the control: it must be
    /// left entirely alone, because it belongs to the Object Type rather than to the rule being deleted.
    /// </summary>
    private async Task<Seeded> SeedAsync(string suffix)
    {
        await using var ctx = NewContext();

        var definition = new ConnectorDefinition { Name = $"omr-def-{suffix}" };
        ctx.ConnectorDefinitions.Add(definition);
        await ctx.SaveChangesAsync();

        var system = new ConnectedSystem { Name = $"omr-cs-{suffix}", ConnectorDefinitionId = definition.Id };
        ctx.ConnectedSystems.Add(system);
        await ctx.SaveChangesAsync();

        var objectType = new ConnectedSystemObjectType { ConnectedSystemId = system.Id, Name = "user", Selected = true };
        ctx.ConnectedSystemObjectTypes.Add(objectType);
        await ctx.SaveChangesAsync();

        // The attribute reaches its Object Type by navigation rather than by a foreign-key property, so the
        // Object Type is attached to this context before it is used.
        var trackedObjectType = await ctx.ConnectedSystemObjectTypes.AsTracking().SingleAsync(t => t.Id == objectType.Id);
        var csAttribute = new ConnectedSystemObjectTypeAttribute
        {
            ConnectedSystemObjectType = trackedObjectType,
            Name = $"employeeId{suffix}"
        };
        ctx.ConnectedSystemAttributes.Add(csAttribute);

        var metaverseObjectType = new MetaverseObjectType { Name = $"OmrType{suffix}", PluralName = $"OmrTypes{suffix}" };
        ctx.MetaverseObjectTypes.Add(metaverseObjectType);
        var metaverseAttribute = new MetaverseAttribute { Name = $"omrAttr{suffix}" };
        ctx.MetaverseAttributes.Add(metaverseAttribute);
        await ctx.SaveChangesAsync();

        var syncRule = new SyncRule
        {
            Name = $"omr-rule-{suffix}",
            ConnectedSystemId = system.Id,
            ConnectedSystemObjectTypeId = objectType.Id,
            MetaverseObjectTypeId = metaverseObjectType.Id
        };
        ctx.SyncRules.Add(syncRule);
        await ctx.SaveChangesAsync();

        // Advanced mode: owned by the Synchronisation Rule, as the API writes it.
        var advanced = new ObjectMatchingRule
        {
            Order = 0,
            SyncRuleId = syncRule.Id,
            TargetMetaverseAttributeId = metaverseAttribute.Id
        };
        // Simple mode: owned by the Connected System Object Type, and nothing to do with the rule being deleted.
        var simple = new ObjectMatchingRule
        {
            Order = 0,
            ConnectedSystemObjectTypeId = objectType.Id,
            MetaverseObjectTypeId = metaverseObjectType.Id,
            TargetMetaverseAttributeId = metaverseAttribute.Id
        };
        ctx.ObjectMatchingRules.AddRange(advanced, simple);
        await ctx.SaveChangesAsync();

        ctx.ObjectMatchingRuleSources.Add(new ObjectMatchingRuleSource
        {
            ObjectMatchingRuleId = advanced.Id,
            Order = 0,
            ConnectedSystemAttributeId = csAttribute.Id
        });
        await ctx.SaveChangesAsync();

        return new Seeded(syncRule.Id, advanced.Id, simple.Id, objectType.Id);
    }

    [Test]
    public async Task DeleteSyncRuleAsync_WithAnAdvancedModeObjectMatchingRule_TakesItWithTheRuleAsync()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var seeded = await SeedAsync(suffix);

        await using (var deleteCtx = NewContext())
        {
            var repository = new PostgresDataRepository(deleteCtx);
            var syncRule = await repository.ConnectedSystems.GetSyncRuleAsync(seeded.SyncRuleId);
            Assert.That(syncRule, Is.Not.Null, "the seeded Synchronisation Rule is readable before it is deleted");

            await repository.ConnectedSystems.DeleteSyncRuleAsync(syncRule!);
        }

        await using var assertCtx = NewContext();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(await assertCtx.SyncRules.AnyAsync(sr => sr.Id == seeded.SyncRuleId), Is.False,
                "the Synchronisation Rule itself is gone");
            Assert.That(await assertCtx.ObjectMatchingRules.AnyAsync(r => r.Id == seeded.AdvancedRuleId), Is.False,
                "its Advanced-mode Object Matching Rule goes with it, rather than surviving with a null owner as " +
                "configuration that belongs to nothing and is reachable from nowhere");
            Assert.That(await assertCtx.ObjectMatchingRuleSources
                    .AnyAsync(s => s.ObjectMatchingRuleId == seeded.AdvancedRuleId),
                Is.False,
                "and so do the attributes it matched on");
            Assert.That(await assertCtx.ObjectMatchingRules.AnyAsync(r => r.Id == seeded.SimpleRuleId), Is.True,
                "a Simple-mode Object Matching Rule on the same Object Type is untouched: it belongs to the " +
                "Object Type, not to the deleted rule");
        }
    }
}
