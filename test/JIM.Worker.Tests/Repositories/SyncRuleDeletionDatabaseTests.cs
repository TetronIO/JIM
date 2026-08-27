// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Search;
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
/// <para>
/// The same convention, and so the same fault, applied to everything else the rule contains: its Attribute Flow
/// mappings and their sources, and its Scoping Criteria groups, the groups nested inside those, and their
/// criteria. Those are the larger population, because every rule has them.
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

    private sealed record SeededContained(
        int SyncRuleId, int MappingId, int MappingSourceId, int TopGroupId, int NestedGroupId, int NestedCriterionId);

    /// <summary>
    /// Seeds a Synchronisation Rule holding one Attribute Flow mapping with a source beneath it, and a Scoping
    /// Criteria group with a second group nested inside it carrying a criterion. The nested group is the point:
    /// it hangs off its parent rather than off the rule, so nothing that cascades from the rule alone reaches it.
    /// </summary>
    private async Task<SeededContained> SeedContainedConfigurationAsync(string suffix)
    {
        await using var ctx = NewContext();

        var definition = new ConnectorDefinition { Name = $"contained-def-{suffix}" };
        ctx.ConnectorDefinitions.Add(definition);
        await ctx.SaveChangesAsync();

        var system = new ConnectedSystem { Name = $"contained-cs-{suffix}", ConnectorDefinitionId = definition.Id };
        ctx.ConnectedSystems.Add(system);
        await ctx.SaveChangesAsync();

        var objectType = new ConnectedSystemObjectType { ConnectedSystemId = system.Id, Name = "user", Selected = true };
        ctx.ConnectedSystemObjectTypes.Add(objectType);
        await ctx.SaveChangesAsync();

        var trackedObjectType = await ctx.ConnectedSystemObjectTypes.AsTracking().SingleAsync(t => t.Id == objectType.Id);
        var csAttribute = new ConnectedSystemObjectTypeAttribute
        {
            ConnectedSystemObjectType = trackedObjectType,
            Name = $"department{suffix}"
        };
        ctx.ConnectedSystemAttributes.Add(csAttribute);

        var metaverseObjectType = new MetaverseObjectType { Name = $"ContainedType{suffix}", PluralName = $"ContainedTypes{suffix}" };
        ctx.MetaverseObjectTypes.Add(metaverseObjectType);
        var metaverseAttribute = new MetaverseAttribute { Name = $"containedAttr{suffix}" };
        ctx.MetaverseAttributes.Add(metaverseAttribute);
        await ctx.SaveChangesAsync();

        var syncRule = new SyncRule
        {
            Name = $"contained-rule-{suffix}",
            ConnectedSystemId = system.Id,
            ConnectedSystemObjectTypeId = objectType.Id,
            MetaverseObjectTypeId = metaverseObjectType.Id
        };

        var mapping = new SyncRuleMapping { TargetMetaverseAttributeId = metaverseAttribute.Id };
        mapping.Sources.Add(new SyncRuleMappingSource { Order = 0, ConnectedSystemAttributeId = csAttribute.Id });
        syncRule.AttributeFlowRules.Add(mapping);

        var nestedCriterion = new SyncRuleScopingCriteria
        {
            MetaverseAttributeId = metaverseAttribute.Id,
            ComparisonType = SearchComparisonType.Equals,
            StringValue = "Sales"
        };
        var nestedGroup = new SyncRuleScopingCriteriaGroup { Type = SearchGroupType.All };
        nestedGroup.Criteria.Add(nestedCriterion);
        var topGroup = new SyncRuleScopingCriteriaGroup { Type = SearchGroupType.All };
        topGroup.ChildGroups.Add(nestedGroup);
        syncRule.ObjectScopingCriteriaGroups.Add(topGroup);

        ctx.SyncRules.Add(syncRule);
        await ctx.SaveChangesAsync();

        return new SeededContained(
            syncRule.Id, mapping.Id, mapping.Sources[0].Id, topGroup.Id, nestedGroup.Id, nestedCriterion.Id);
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

    /// <summary>
    /// The rest of what a Synchronisation Rule contains: its Attribute Flow mappings and the sources beneath
    /// them, and its Scoping Criteria groups, the groups nested inside those, and their criteria. The nesting is
    /// the part worth seeding explicitly: a cascade from the rule reaches only the top-level groups, so a nested
    /// group whose own reference does not cascade holds the entire delete up, which is how the equivalent fault
    /// in the Connected System Container hierarchy behaved.
    /// </summary>
    [Test]
    public async Task DeleteSyncRuleAsync_WithMappingsAndNestedScopingGroups_TakesThemAllWithTheRuleAsync()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var seeded = await SeedContainedConfigurationAsync(suffix);

        await using (var deleteCtx = NewContext())
        {
            var repository = new PostgresDataRepository(deleteCtx);
            var syncRule = await repository.ConnectedSystems.GetSyncRuleAsync(seeded.SyncRuleId);
            Assert.That(syncRule, Is.Not.Null, "the seeded Synchronisation Rule is readable before it is deleted");

            Assert.That(async () => await repository.ConnectedSystems.DeleteSyncRuleAsync(syncRule!),
                Throws.Nothing,
                "a nested Scoping Criteria group must not hold the delete up the way a nested Container once did");
        }

        await using var assertCtx = NewContext();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(await assertCtx.SyncRules.AnyAsync(sr => sr.Id == seeded.SyncRuleId), Is.False,
                "the Synchronisation Rule itself is gone");
            Assert.That(await assertCtx.SyncRuleMappings.AnyAsync(m => m.Id == seeded.MappingId), Is.False,
                "its Attribute Flow mapping goes with it, rather than surviving with a null owner");
            Assert.That(await assertCtx.SyncRuleMappingSources.AnyAsync(s => s.Id == seeded.MappingSourceId), Is.False,
                "and the source the mapping read from");
            Assert.That(await assertCtx.SyncRuleScopingCriteriaGroups.AnyAsync(g => g.Id == seeded.TopGroupId), Is.False,
                "its top-level Scoping Criteria group goes with it");
            Assert.That(await assertCtx.SyncRuleScopingCriteriaGroups.AnyAsync(g => g.Id == seeded.NestedGroupId), Is.False,
                "so does the group nested inside it, which the cascade from the rule cannot reach directly");
            Assert.That(await assertCtx.SyncRuleScopingCriteria.AnyAsync(c => c.Id == seeded.NestedCriterionId), Is.False,
                "and the criterion inside that nested group");
        }
    }
}
