// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.PostgresData;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// Real-PostgreSQL verification of the custom Metaverse Attribute repository queries added in #377 (Phase 1). The EF
/// Core in-memory provider cannot be trusted for the case-insensitive uniqueness comparison, the per-Object-Type
/// value-count GROUP BY, reference discovery, or the multi-table cascade delete, so these exercise them against a real
/// database. Opt-in via <c>JIM_TEST_RESET_DB</c>; ignored when it is absent.
/// </summary>
[TestFixture]
[Category("RequiresPostgres")]
public class MetaverseAttributeCustomDatabaseTests
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
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL custom attribute tests.");

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
    public async Task IsMetaverseAttributeNameUniqueAsync_ComparesCaseInsensitivelyAsync()
    {
        int attributeId;
        await using (var seed = NewContext())
        {
            var personType = new MetaverseObjectType { Name = "Person", PluralName = "People", BuiltIn = true };
            var costCentre = new MetaverseAttribute { Name = "CostCentre", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.SingleValued };
            personType.Attributes.Add(costCentre);
            seed.MetaverseObjectTypes.Add(personType);
            await seed.SaveChangesAsync();
            attributeId = costCentre.Id;
        }

        await using var ctx = NewContext();
        var jim = new JimApplication(new PostgresDataRepository(ctx));

        // 'costCentre' clashes with the stored 'CostCentre' (case-insensitive).
        Assert.That(await jim.Metaverse.IsMetaverseAttributeNameUniqueAsync("costCentre"), Is.False);
        // A genuinely different name is available.
        Assert.That(await jim.Metaverse.IsMetaverseAttributeNameUniqueAsync("buildingCode"), Is.True);
        // Excluding the attribute itself frees its own (case-varied) name for a no-op rename.
        Assert.That(await jim.Metaverse.IsMetaverseAttributeNameUniqueAsync("costCentre", attributeId), Is.True);
    }

    [Test]
    public async Task EvaluateAttributeDeletionAsync_ReportsPerTypeValueCountsAndReferencesAsync()
    {
        int attributeId;
        await using (var seed = NewContext())
        {
            var personType = new MetaverseObjectType { Name = "Person", PluralName = "People", BuiltIn = true };
            var groupType = new MetaverseObjectType { Name = "Group", PluralName = "Groups", BuiltIn = true };
            var costCentre = new MetaverseAttribute { Name = "costCentre", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.SingleValued };
            personType.Attributes.Add(costCentre);
            groupType.Attributes.Add(costCentre);
            seed.MetaverseObjectTypes.AddRange(personType, groupType);
            await seed.SaveChangesAsync();
            attributeId = costCentre.Id;

            // Two People and one Group hold a value for the attribute (one Person holds two values: still one object).
            var p1 = new MetaverseObject { Type = personType };
            p1.AttributeValues.Add(new MetaverseObjectAttributeValue { Attribute = costCentre, MetaverseObject = p1, StringValue = "100" });
            p1.AttributeValues.Add(new MetaverseObjectAttributeValue { Attribute = costCentre, MetaverseObject = p1, StringValue = "101" });
            var p2 = new MetaverseObject { Type = personType };
            p2.AttributeValues.Add(new MetaverseObjectAttributeValue { Attribute = costCentre, MetaverseObject = p2, StringValue = "200" });
            var g1 = new MetaverseObject { Type = groupType };
            g1.AttributeValues.Add(new MetaverseObjectAttributeValue { Attribute = costCentre, MetaverseObject = g1, StringValue = "300" });
            seed.MetaverseObjects.AddRange(p1, p2, g1);
            await seed.SaveChangesAsync();
        }

        await using var ctx = NewContext();
        var jim = new JimApplication(new PostgresDataRepository(ctx));
        var attribute = await jim.Metaverse.GetMetaverseAttributeAsync(attributeId);

        var impact = await jim.Metaverse.EvaluateAttributeDeletionAsync(attribute!);

        Assert.That(impact.TotalObjectsWithValues, Is.EqualTo(3), "two People (one with two values) and one Group");
        var personCount = impact.ObjectTypeValueCounts.Single(c => c.MetaverseObjectTypeName == "Person");
        var groupCount = impact.ObjectTypeValueCounts.Single(c => c.MetaverseObjectTypeName == "Group");
        Assert.That(personCount.ObjectCount, Is.EqualTo(2));
        Assert.That(groupCount.ObjectCount, Is.EqualTo(1));
        // Two Object Type bindings are present as references.
        Assert.That(impact.References.Count(r => r.Kind == JIM.Models.Core.DTOs.AttributeReferenceKind.Binding), Is.EqualTo(2));
        Assert.That(impact.BlockedByValues, Is.True);
    }

    [Test]
    public async Task DeleteMetaverseAttributeWithCascadeAsync_WithReferencesNoValues_RemovesReferencesAndAttributeAsync()
    {
        int costCentreId;
        int buildingCodeId;
        await using (var seed = NewContext())
        {
            var connectorDefinition = new ConnectorDefinition { Name = "Test Connector", BuiltIn = true };
            var system = new ConnectedSystem { Name = "Test System", ConnectorDefinition = connectorDefinition };
            var csType = new ConnectedSystemObjectType { Name = "user", ConnectedSystem = system, Selected = true };

            var personType = new MetaverseObjectType { Name = "Person", PluralName = "People", BuiltIn = true };
            var costCentre = new MetaverseAttribute { Name = "costCentre", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.SingleValued };
            var buildingCode = new MetaverseAttribute { Name = "buildingCode", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.SingleValued };
            personType.Attributes.Add(costCentre);
            personType.Attributes.Add(buildingCode);

            seed.ConnectorDefinitions.Add(connectorDefinition);
            seed.ConnectedSystems.Add(system);
            seed.ConnectedSystemObjectTypes.Add(csType);
            seed.MetaverseObjectTypes.Add(personType);
            await seed.SaveChangesAsync();
            costCentreId = costCentre.Id;
            buildingCodeId = buildingCode.Id;

            // A Synchronisation Rule with an import mapping targeting costCentre (to be cascade-removed) and one
            // targeting buildingCode (a control that must survive), plus a scoping criterion on costCentre.
            var rule = new SyncRule
            {
                Name = "Person Import",
                Direction = SyncRuleDirection.Import,
                Enabled = true,
                ConnectedSystem = system,
                ConnectedSystemObjectType = csType,
                MetaverseObjectType = personType
            };
            rule.AttributeFlowRules.Add(new SyncRuleMapping { TargetMetaverseAttribute = costCentre });
            rule.AttributeFlowRules.Add(new SyncRuleMapping { TargetMetaverseAttribute = buildingCode });
            rule.ObjectScopingCriteriaGroups.Add(new SyncRuleScopingCriteriaGroup
            {
                Criteria = [new SyncRuleScopingCriteria { MetaverseAttribute = costCentre, StringValue = "100" }]
            });
            seed.SyncRules.Add(rule);
            await seed.SaveChangesAsync();
        }

        await using (var act = NewContext())
        {
            var jim = new JimApplication(new PostgresDataRepository(act));
            var attribute = await jim.Metaverse.GetMetaverseAttributeAsync(costCentreId);
            var impact = await jim.Metaverse.DeleteMetaverseAttributeWithCascadeAsync(attribute!, (MetaverseObject?)null);
            Assert.That(impact.Deleted, Is.True);
        }

        await using var verify = NewContext();
        // costCentre and all its references are gone; buildingCode and its mapping and the rule survive.
        Assert.That(await verify.MetaverseAttributes.AnyAsync(a => a.Id == costCentreId), Is.False, "the attribute was removed");
        Assert.That(await verify.SyncRuleMappings.AnyAsync(m => m.TargetMetaverseAttributeId == costCentreId), Is.False, "the import mapping was cascade-removed");
        Assert.That(await verify.SyncRuleScopingCriteria.AnyAsync(c => c.MetaverseAttributeId == costCentreId), Is.False, "the scoping criterion was cascade-removed");
        Assert.That(await verify.MetaverseAttributes.AnyAsync(a => a.Id == buildingCodeId), Is.True, "the control attribute survives");
        Assert.That(await verify.SyncRuleMappings.AnyAsync(m => m.TargetMetaverseAttributeId == buildingCodeId), Is.True, "the control mapping survives");
        Assert.That(await verify.SyncRules.CountAsync(), Is.EqualTo(1), "the Synchronisation Rule itself survives");
    }

    [Test]
    public async Task DeleteMetaverseAttributeWithCascadeAsync_WithStoredValues_RefusesAndLeavesAttributeAsync()
    {
        int attributeId;
        await using (var seed = NewContext())
        {
            var personType = new MetaverseObjectType { Name = "Person", PluralName = "People", BuiltIn = true };
            var costCentre = new MetaverseAttribute { Name = "costCentre", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.SingleValued };
            personType.Attributes.Add(costCentre);
            seed.MetaverseObjectTypes.Add(personType);
            await seed.SaveChangesAsync();
            attributeId = costCentre.Id;

            var p1 = new MetaverseObject { Type = personType };
            p1.AttributeValues.Add(new MetaverseObjectAttributeValue { Attribute = costCentre, MetaverseObject = p1, StringValue = "100" });
            seed.MetaverseObjects.Add(p1);
            await seed.SaveChangesAsync();
        }

        await using (var act = NewContext())
        {
            var jim = new JimApplication(new PostgresDataRepository(act));
            var attribute = await jim.Metaverse.GetMetaverseAttributeAsync(attributeId);
            var impact = await jim.Metaverse.DeleteMetaverseAttributeWithCascadeAsync(attribute!, (MetaverseObject?)null);
            Assert.That(impact.BlockedByValues, Is.True);
            Assert.That(impact.Deleted, Is.False);
        }

        await using var verify = NewContext();
        Assert.That(await verify.MetaverseAttributes.AnyAsync(a => a.Id == attributeId), Is.True, "a values-blocked delete makes no change");
    }
}
