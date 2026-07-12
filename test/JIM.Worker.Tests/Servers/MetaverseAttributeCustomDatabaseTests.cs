// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Models.Core;
using JIM.Models.Core.DTOs;
using JIM.Models.ExampleData;
using JIM.Models.Logic;
using JIM.Models.Search;
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
            var impact = await jim.Metaverse.DeleteMetaverseAttributeWithCascadeAsync(attribute!, TestUtilities.GetInitiatedBy());
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
            var impact = await jim.Metaverse.DeleteMetaverseAttributeWithCascadeAsync(attribute!, TestUtilities.GetInitiatedBy());
            Assert.That(impact.BlockedByValues, Is.True);
            Assert.That(impact.Deleted, Is.False);
        }

        await using var verify = NewContext();
        Assert.That(await verify.MetaverseAttributes.AnyAsync(a => a.Id == attributeId), Is.True, "a values-blocked delete makes no change");
    }

    [Test]
    public async Task DeleteMetaverseAttributeWithCascadeAsync_ExportMappingLeftSourceless_RemovesParentButKeepsMappingWithOtherSourcesAsync()
    {
        int costCentreId, soleMappingId, mixedMappingId;
        await using (var seed = NewContext())
        {
            var connectorDefinition = new ConnectorDefinition { Name = "C", BuiltIn = true };
            var system = new ConnectedSystem { Name = "S", ConnectorDefinition = connectorDefinition };
            var csType = new ConnectedSystemObjectType { Name = "user", ConnectedSystem = system, Selected = true };
            var targetA = new ConnectedSystemObjectTypeAttribute { Name = "targetA", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.SingleValued, ConnectedSystemObjectType = csType, Selected = true };
            var targetB = new ConnectedSystemObjectTypeAttribute { Name = "targetB", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.SingleValued, ConnectedSystemObjectType = csType, Selected = true };
            var otherSource = new ConnectedSystemObjectTypeAttribute { Name = "otherSource", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.SingleValued, ConnectedSystemObjectType = csType, Selected = true };
            csType.Attributes.Add(targetA);
            csType.Attributes.Add(targetB);
            csType.Attributes.Add(otherSource);

            var personType = new MetaverseObjectType { Name = "Person", PluralName = "People", BuiltIn = true };
            var costCentre = new MetaverseAttribute { Name = "costCentre", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.SingleValued };
            personType.Attributes.Add(costCentre);

            seed.ConnectorDefinitions.Add(connectorDefinition);
            seed.ConnectedSystems.Add(system);
            seed.ConnectedSystemObjectTypes.Add(csType);
            seed.MetaverseObjectTypes.Add(personType);
            await seed.SaveChangesAsync();
            costCentreId = costCentre.Id;

            var exportRule = new SyncRule
            {
                Name = "Export",
                Direction = SyncRuleDirection.Export,
                Enabled = true,
                ConnectedSystem = system,
                ConnectedSystemObjectType = csType,
                MetaverseObjectType = personType
            };
            // Sole-source export mapping: its only source reads costCentre, so removing that source empties it.
            var soleMapping = new SyncRuleMapping { TargetConnectedSystemAttribute = targetA };
            soleMapping.Sources.Add(new SyncRuleMappingSource { MetaverseAttribute = costCentre, Order = 0 });
            // Mixed-source export mapping: keeps a Connected System source, so it survives.
            var mixedMapping = new SyncRuleMapping { TargetConnectedSystemAttribute = targetB };
            mixedMapping.Sources.Add(new SyncRuleMappingSource { MetaverseAttribute = costCentre, Order = 0 });
            mixedMapping.Sources.Add(new SyncRuleMappingSource { ConnectedSystemAttribute = otherSource, Order = 1 });
            exportRule.AttributeFlowRules.Add(soleMapping);
            exportRule.AttributeFlowRules.Add(mixedMapping);
            seed.SyncRules.Add(exportRule);
            await seed.SaveChangesAsync();
            soleMappingId = soleMapping.Id;
            mixedMappingId = mixedMapping.Id;
        }

        await using (var act = NewContext())
        {
            var jim = new JimApplication(new PostgresDataRepository(act));
            var attribute = await jim.Metaverse.GetMetaverseAttributeAsync(costCentreId);

            // Preview lists the source-less mapping as a knock-on removal, and the surviving mapping's source.
            var impact = await jim.Metaverse.EvaluateAttributeDeletionAsync(attribute!);
            Assert.That(impact.References.Any(r => r.Kind == AttributeReferenceKind.ExportAttributeFlowMapping && r.Id == soleMappingId), Is.True);
            Assert.That(impact.References.Any(r => r.Kind == AttributeReferenceKind.ExportAttributeFlowSource), Is.True);

            var result = await jim.Metaverse.DeleteMetaverseAttributeWithCascadeAsync(attribute!, TestUtilities.GetInitiatedBy());
            Assert.That(result.Deleted, Is.True);
        }

        await using var verify = NewContext();
        Assert.That(await verify.SyncRuleMappings.AnyAsync(m => m.Id == soleMappingId), Is.False, "the source-less export mapping was removed");
        Assert.That(await verify.SyncRuleMappings.AnyAsync(m => m.Id == mixedMappingId), Is.True, "the mapping with another source survives");
        Assert.That(await verify.SyncRuleMappingSources.CountAsync(), Is.EqualTo(1), "only the surviving mapping's non-costCentre source remains");
    }

    [Test]
    public async Task DeleteMetaverseAttributeWithCascadeAsync_ObjectMatchingRuleLeftSourceless_RemovesRuleButKeepsRuleWithOtherSourcesAsync()
    {
        int costCentreId, soleRuleId, mixedRuleId;
        await using (var seed = NewContext())
        {
            var connectorDefinition = new ConnectorDefinition { Name = "C", BuiltIn = true };
            var system = new ConnectedSystem { Name = "S", ConnectorDefinition = connectorDefinition };
            var csType = new ConnectedSystemObjectType { Name = "user", ConnectedSystem = system, Selected = true };
            var otherSource = new ConnectedSystemObjectTypeAttribute { Name = "otherSource", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.SingleValued, ConnectedSystemObjectType = csType, Selected = true };
            csType.Attributes.Add(otherSource);

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

            // Rules target buildingCode (not costCentre), so they are only removed via the source-less rule.
            var soleRule = new ObjectMatchingRule { ConnectedSystemObjectType = csType, MetaverseObjectType = personType, TargetMetaverseAttribute = buildingCode, Order = 0 };
            soleRule.Sources.Add(new ObjectMatchingRuleSource { MetaverseAttribute = costCentre, Order = 0 });
            var mixedRule = new ObjectMatchingRule { ConnectedSystemObjectType = csType, MetaverseObjectType = personType, TargetMetaverseAttribute = buildingCode, Order = 1 };
            mixedRule.Sources.Add(new ObjectMatchingRuleSource { MetaverseAttribute = costCentre, Order = 0 });
            mixedRule.Sources.Add(new ObjectMatchingRuleSource { ConnectedSystemAttribute = otherSource, Order = 1 });
            seed.ObjectMatchingRules.AddRange(soleRule, mixedRule);
            await seed.SaveChangesAsync();
            soleRuleId = soleRule.Id;
            mixedRuleId = mixedRule.Id;
        }

        await using (var act = NewContext())
        {
            var jim = new JimApplication(new PostgresDataRepository(act));
            var attribute = await jim.Metaverse.GetMetaverseAttributeAsync(costCentreId);

            var impact = await jim.Metaverse.EvaluateAttributeDeletionAsync(attribute!);
            Assert.That(impact.References.Any(r => r.Kind == AttributeReferenceKind.SourcelessObjectMatchingRule && r.Id == soleRuleId), Is.True);
            Assert.That(impact.References.Any(r => r.Kind == AttributeReferenceKind.ObjectMatchingRuleSource), Is.True);

            var result = await jim.Metaverse.DeleteMetaverseAttributeWithCascadeAsync(attribute!, TestUtilities.GetInitiatedBy());
            Assert.That(result.Deleted, Is.True);
        }

        await using var verify = NewContext();
        Assert.That(await verify.ObjectMatchingRules.AnyAsync(r => r.Id == soleRuleId), Is.False, "the source-less Object Matching Rule was removed");
        Assert.That(await verify.ObjectMatchingRules.AnyAsync(r => r.Id == mixedRuleId), Is.True, "the rule with another source survives");
        Assert.That(await verify.ObjectMatchingRuleSources.CountAsync(), Is.EqualTo(1), "only the surviving rule's non-costCentre source remains");
    }

    [Test]
    public async Task DeleteMetaverseAttributeWithCascadeAsync_ExtendedReferences_RemovedOrNulledAndPreviewedAsync()
    {
        int costCentreId;
        await using (var seed = NewContext())
        {
            var personType = new MetaverseObjectType { Name = "Person", PluralName = "People", BuiltIn = true };
            var costCentre = new MetaverseAttribute { Name = "costCentre", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.SingleValued };
            personType.Attributes.Add(costCentre);
            seed.MetaverseObjectTypes.Add(personType);
            await seed.SaveChangesAsync();
            costCentreId = costCentre.Id;

            // A Predefined Search display column, an Example Data template attribute, and the Service Settings SSO
            // unique-identifier mapping, all referencing the attribute.
            var search = new PredefinedSearch { MetaverseObjectType = personType, Name = "People", Uri = "people" };
            search.Attributes.Add(new PredefinedSearchAttribute { MetaverseAttribute = costCentre, Position = 0 });
            seed.PredefinedSearches.Add(search);
            seed.ExampleDataTemplateAttributes.Add(new ExampleDataTemplateAttribute { MetaverseAttribute = costCentre });
            seed.ServiceSettings.Add(new ServiceSettings { SSOUniqueIdentifierMetaverseAttribute = costCentre });
            await seed.SaveChangesAsync();
        }

        await using (var act = NewContext())
        {
            var jim = new JimApplication(new PostgresDataRepository(act));
            var attribute = await jim.Metaverse.GetMetaverseAttributeAsync(costCentreId);

            var impact = await jim.Metaverse.EvaluateAttributeDeletionAsync(attribute!);
            Assert.That(impact.References.Any(r => r.Kind == AttributeReferenceKind.PredefinedSearchAttribute), Is.True);
            Assert.That(impact.References.Any(r => r.Kind == AttributeReferenceKind.ExampleDataTemplateAttribute), Is.True);
            Assert.That(impact.References.Any(r => r.Kind == AttributeReferenceKind.ServiceSettingsSsoIdentifier), Is.True);

            var result = await jim.Metaverse.DeleteMetaverseAttributeWithCascadeAsync(attribute!, TestUtilities.GetInitiatedBy());
            Assert.That(result.Deleted, Is.True);
        }

        await using var verify = NewContext();
        Assert.That(await verify.PredefinedSearchAttributes.AnyAsync(), Is.False, "the Predefined Search column was removed");
        Assert.That(await verify.ExampleDataTemplateAttributes.AnyAsync(), Is.False, "the Example Data template attribute was removed");
        // The Service Settings singleton survives, but its SSO unique-identifier mapping was cleared (set null).
        var ssoFk = await verify.ServiceSettings
            .Select(s => EF.Property<int?>(s, "SSOUniqueIdentifierMetaverseAttributeId"))
            .SingleAsync();
        Assert.That(ssoFk, Is.Null, "the SSO unique-identifier mapping was cleared, not left dangling");
    }

    [Test]
    public async Task UnassignAttributeFromObjectTypeAsync_TypeScoped_CascadesTypeReferencesButLeavesOtherTypesAndGlobalSsoAsync()
    {
        int costCentreId, personTypeId, personImportMappingId, personExportMappingId, groupImportMappingId;
        await using (var seed = NewContext())
        {
            var connectorDefinition = new ConnectorDefinition { Name = "C", BuiltIn = true };
            var system = new ConnectedSystem { Name = "S", ConnectorDefinition = connectorDefinition };
            var csType = new ConnectedSystemObjectType { Name = "user", ConnectedSystem = system, Selected = true };
            var target = new ConnectedSystemObjectTypeAttribute { Name = "target", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.SingleValued, ConnectedSystemObjectType = csType, Selected = true };
            csType.Attributes.Add(target);

            var personType = new MetaverseObjectType { Name = "Person", PluralName = "People", BuiltIn = true };
            var groupType = new MetaverseObjectType { Name = "Group", PluralName = "Groups", BuiltIn = true };
            var costCentre = new MetaverseAttribute { Name = "costCentre", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.SingleValued };
            // Bound to both types.
            personType.Attributes.Add(costCentre);
            groupType.Attributes.Add(costCentre);

            seed.ConnectorDefinitions.Add(connectorDefinition);
            seed.ConnectedSystems.Add(system);
            seed.ConnectedSystemObjectTypes.Add(csType);
            seed.MetaverseObjectTypes.AddRange(personType, groupType);
            await seed.SaveChangesAsync();
            costCentreId = costCentre.Id;
            personTypeId = personType.Id;

            SyncRule Rule(string name, SyncRuleDirection direction, MetaverseObjectType type) => new()
            {
                Name = name, Direction = direction, Enabled = true,
                ConnectedSystem = system, ConnectedSystemObjectType = csType, MetaverseObjectType = type
            };

            // Import flow targeting costCentre, owned by a rule targeting Person (in scope for the unassign).
            var personImportRule = Rule("Person Import", SyncRuleDirection.Import, personType);
            var personImportMapping = new SyncRuleMapping { TargetMetaverseAttribute = costCentre };
            personImportRule.AttributeFlowRules.Add(personImportMapping);

            // Export flow whose sole source reads costCentre, owned by a rule targeting Person (source-less within scope).
            var personExportRule = Rule("Person Export", SyncRuleDirection.Export, personType);
            var personExportMapping = new SyncRuleMapping { TargetConnectedSystemAttribute = target };
            personExportMapping.Sources.Add(new SyncRuleMappingSource { MetaverseAttribute = costCentre, Order = 0 });
            personExportRule.AttributeFlowRules.Add(personExportMapping);

            // Import flow targeting costCentre, owned by a rule targeting Group (OUT of scope; must be untouched).
            var groupImportRule = Rule("Group Import", SyncRuleDirection.Import, groupType);
            var groupImportMapping = new SyncRuleMapping { TargetMetaverseAttribute = costCentre };
            groupImportRule.AttributeFlowRules.Add(groupImportMapping);

            seed.SyncRules.AddRange(personImportRule, personExportRule, groupImportRule);

            // Global SSO unique-identifier mapping (must NOT be cleared by a per-type unassign).
            seed.ServiceSettings.Add(new ServiceSettings { SSOUniqueIdentifierMetaverseAttribute = costCentre });
            await seed.SaveChangesAsync();
            personImportMappingId = personImportMapping.Id;
            personExportMappingId = personExportMapping.Id;
            groupImportMappingId = groupImportMapping.Id;
        }

        await using (var act = NewContext())
        {
            var jim = new JimApplication(new PostgresDataRepository(act));

            var impact = await jim.Metaverse.EvaluateAttributeUnassignAsync(costCentreId, personTypeId);
            // The preview lists the Person-scoped references but not the Group flow nor the global SSO mapping.
            Assert.That(impact.References.Any(r => r.Kind == AttributeReferenceKind.ImportAttributeFlow && r.Id == personImportMappingId), Is.True);
            Assert.That(impact.References.Any(r => r.Kind == AttributeReferenceKind.ExportAttributeFlowMapping && r.Id == personExportMappingId), Is.True);
            Assert.That(impact.References.Any(r => r.Id == groupImportMappingId), Is.False, "the Group-scoped flow is out of scope for a Person unassign");
            Assert.That(impact.References.Any(r => r.Kind == AttributeReferenceKind.ServiceSettingsSsoIdentifier), Is.False, "the global SSO mapping is never in a per-type unassign");

            var result = await jim.Metaverse.UnassignAttributeFromObjectTypeAsync(costCentreId, personTypeId, TestUtilities.GetInitiatedBy());
            Assert.That(result.Unassigned, Is.True);
        }

        await using var verify = NewContext();
        // The attribute itself survives (still bound to Group).
        Assert.That(await verify.MetaverseAttributes.AnyAsync(a => a.Id == costCentreId), Is.True, "the attribute is not deleted by an unassign");
        // Person-scoped references removed; Group-scoped reference untouched.
        Assert.That(await verify.SyncRuleMappings.AnyAsync(m => m.Id == personImportMappingId), Is.False, "the Person import flow was cascade-removed");
        Assert.That(await verify.SyncRuleMappings.AnyAsync(m => m.Id == personExportMappingId), Is.False, "the source-less Person export mapping was removed");
        Assert.That(await verify.SyncRuleMappings.AnyAsync(m => m.Id == groupImportMappingId), Is.True, "the Group import flow is untouched");
        // The binding to Person is gone; the binding to Group remains.
        var boundTypeNames = await verify.MetaverseObjectTypes
            .Where(t => t.Attributes.Any(a => a.Id == costCentreId))
            .Select(t => t.Name)
            .ToListAsync();
        Assert.That(boundTypeNames, Is.EquivalentTo(new[] { "Group" }), "only the Person binding was removed");
        // The global SSO unique-identifier mapping is still set.
        var ssoFk = await verify.ServiceSettings
            .Select(s => EF.Property<int?>(s, "SSOUniqueIdentifierMetaverseAttributeId"))
            .SingleAsync();
        Assert.That(ssoFk, Is.EqualTo(costCentreId), "a per-type unassign must not clear the global SSO mapping");
    }

    [Test]
    public async Task UnassignAttributeFromObjectTypeAsync_WithStoredValuesOfType_RefusesAndLeavesBindingAsync()
    {
        int costCentreId, personTypeId;
        await using (var seed = NewContext())
        {
            var personType = new MetaverseObjectType { Name = "Person", PluralName = "People", BuiltIn = true };
            var costCentre = new MetaverseAttribute { Name = "costCentre", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.SingleValued };
            personType.Attributes.Add(costCentre);
            seed.MetaverseObjectTypes.Add(personType);
            await seed.SaveChangesAsync();
            costCentreId = costCentre.Id;
            personTypeId = personType.Id;

            var p1 = new MetaverseObject { Type = personType };
            p1.AttributeValues.Add(new MetaverseObjectAttributeValue { Attribute = costCentre, MetaverseObject = p1, StringValue = "100" });
            seed.MetaverseObjects.Add(p1);
            await seed.SaveChangesAsync();
        }

        await using (var act = NewContext())
        {
            var jim = new JimApplication(new PostgresDataRepository(act));
            var impact = await jim.Metaverse.UnassignAttributeFromObjectTypeAsync(costCentreId, personTypeId, TestUtilities.GetInitiatedBy());
            Assert.That(impact.BlockedByValues, Is.True);
            Assert.That(impact.Unassigned, Is.False);
        }

        await using var verify = NewContext();
        var boundTypeNames = await verify.MetaverseObjectTypes
            .Where(t => t.Attributes.Any(a => a.Id == costCentreId))
            .Select(t => t.Name)
            .ToListAsync();
        Assert.That(boundTypeNames, Is.EquivalentTo(new[] { "Person" }), "a values-blocked unassign makes no change");
    }
}
