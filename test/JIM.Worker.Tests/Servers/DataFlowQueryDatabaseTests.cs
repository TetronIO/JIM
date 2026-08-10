// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Logic.DTOs;
using JIM.Models.Staging;
using JIM.PostgresData;
using JIM.PostgresData.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// Real-PostgreSQL verification of the Data Flow query (#1199): the filters and the direction-appropriate
/// projection behind the system-wide Data Flow view.
/// </summary>
/// <remarks>
/// A real database rather than the in-memory provider, for the reasons in <c>src/CLAUDE.md</c>: the in-memory
/// provider evaluates anything it cannot translate on the client, so a filter that PostgreSQL would refuse (the
/// <c>Sources.Any(...)</c> sub-queries and the lowered LIKE comparisons here are the candidates) passes in memory and
/// throws at runtime. The projection's navigation reads are the same class of risk: they only prove anything against
/// a provider that must actually join.
///
/// Opt-in via the same <c>JIM_TEST_RESET_*</c> environment variables as the other database fixtures; ignored when
/// <c>JIM_TEST_RESET_DB</c> is absent.
/// </remarks>
[TestFixture]
[Category("RequiresPostgres")]
public class DataFlowQueryDatabaseTests
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
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL Data Flow query tests.");

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
    public async Task GetDataFlowHeadersAsync_NoFilters_ReturnsEveryFlowInBothDirectionsAsync()
    {
        var ids = await SeedAsync();

        var flows = await QueryAsync(new DataFlowQuery());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(flows.Select(f => f.SyncRuleMappingId), Is.EquivalentTo(new[] { ids.ImportMappingId, ids.SecondImportMappingId, ids.ExportMappingId }));
            Assert.That(flows.Select(f => f.Direction).Distinct(),
                Is.EquivalentTo(new[] { SyncRuleDirection.Import, SyncRuleDirection.Export }));
        }
    }

    [Test]
    public async Task GetDataFlowHeadersAsync_ImportFlow_CarriesItsTargetAndSourceAndPriorityAsync()
    {
        var ids = await SeedAsync();

        var flows = await QueryAsync(new DataFlowQuery { Direction = SyncRuleDirection.Import });
        var flow = flows.Single(f => f.SyncRuleMappingId == ids.ImportMappingId);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(flow.TargetMetaverseAttributeName, Is.EqualTo("Display Name"));
            Assert.That(flow.TargetConnectedSystemAttributeId, Is.Null, "the Connected System side is the source on an Import flow");
            Assert.That(flow.Sources.Single().ConnectedSystemAttributeName, Is.EqualTo("cn"));
            Assert.That(flow.Priority, Is.EqualTo(1));
            Assert.That(flow.NullIsValue, Is.True);
            Assert.That(flow.EnforceState, Is.Null, "Enforce State is an Export concern");
            Assert.That(flow.MetaverseObjectTypeName, Is.EqualTo("Group"));
            Assert.That(flow.ConnectedSystemName, Is.EqualTo("Test System"));
        }
    }

    [Test]
    public async Task GetDataFlowHeadersAsync_ExportFlow_CarriesItsTargetAndSourceAndEnforceStateAsync()
    {
        var ids = await SeedAsync();

        var flows = await QueryAsync(new DataFlowQuery { Direction = SyncRuleDirection.Export });
        var flow = flows.Single(f => f.SyncRuleMappingId == ids.ExportMappingId);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(flow.TargetConnectedSystemAttributeName, Is.EqualTo("cn"));
            Assert.That(flow.TargetMetaverseAttributeId, Is.Null, "the Metaverse side is the source on an Export flow");
            Assert.That(flow.Sources.Single().MetaverseAttributeName, Is.EqualTo("Display Name"));
            Assert.That(flow.EnforceState, Is.True);
            Assert.That(flow.Priority, Is.Null, "priority is an Import concern");
            Assert.That(flow.NullIsValue, Is.Null);
        }
    }

    [Test]
    public async Task GetDataFlowHeadersAsync_MetaverseAttributeFilter_MatchesBothTargetAndSourceSidesAsync()
    {
        // "Display Name" is the Import flow's target and the Export flow's source, so filtering on it must return
        // both: the question the filter answers is "what touches this attribute?", not "what writes it".
        var ids = await SeedAsync();

        var flows = await QueryAsync(new DataFlowQuery { MetaverseAttributeId = ids.MvAttrId });

        Assert.That(flows.Select(f => f.SyncRuleMappingId), Is.EquivalentTo(new[] { ids.ImportMappingId, ids.ExportMappingId }));
    }

    [Test]
    public async Task GetDataFlowHeadersAsync_SearchMatchesAnExpressionAsync()
    {
        // An expression is the one source whose attribute references are not modelled, so free text is the only way
        // to find it. If this stops translating to SQL the whole search silently falls back to client evaluation.
        var ids = await SeedAsync();

        var flows = await QueryAsync(new DataFlowQuery { Search = "ToUpper" });

        Assert.That(flows.Select(f => f.SyncRuleMappingId), Is.EqualTo(new[] { ids.SecondImportMappingId }));
    }

    [Test]
    public async Task GetDataFlowHeadersAsync_ConnectedSystemFilter_ExcludesOtherSystemsAsync()
    {
        var ids = await SeedAsync();

        var matched = await QueryAsync(new DataFlowQuery { ConnectedSystemId = ids.SystemId });
        var unmatched = await QueryAsync(new DataFlowQuery { ConnectedSystemId = ids.SystemId + 1000 });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(matched, Has.Count.EqualTo(3));
            Assert.That(unmatched, Is.Empty);
        }
    }

    private async Task<IList<DataFlowHeader>> QueryAsync(DataFlowQuery query)
    {
        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);
        return await repository.ConnectedSystems.GetDataFlowHeadersAsync(query);
    }

    /// <summary>
    /// Seeds one Connected System with an Import rule carrying two mappings (a plain attribute source and an
    /// expression source) and an Export rule with Enforce State on, which is the smallest configuration that
    /// exercises both directions and both source shapes.
    /// </summary>
    private async Task<SeedIds> SeedAsync()
    {
        await using var seed = NewContext();

        var connectorDefinition = new ConnectorDefinition { Name = "Test Connector", BuiltIn = true };
        var system = new ConnectedSystem
        {
            Name = "Test System",
            ConnectorDefinition = connectorDefinition,
            ObjectMatchingRuleMode = ObjectMatchingRuleMode.SyncRule
        };
        var csType = new ConnectedSystemObjectType { Name = "jimGroup", ConnectedSystem = system, Selected = true };
        var csAttr = new ConnectedSystemObjectTypeAttribute { Name = "cn", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.SingleValued, ConnectedSystemObjectType = csType, Selected = true };
        csType.Attributes.Add(csAttr);

        var mvType = new MetaverseObjectType { Name = "Group", PluralName = "Groups", BuiltIn = true };
        var mvAttr = new MetaverseAttribute { Name = "Display Name", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.SingleValued, BuiltIn = true };
        var mvAttr2 = new MetaverseAttribute { Name = "Description", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.SingleValued, BuiltIn = true };
        mvType.Attributes.Add(mvAttr);
        mvType.Attributes.Add(mvAttr2);

        seed.ConnectorDefinitions.Add(connectorDefinition);
        seed.ConnectedSystems.Add(system);
        seed.ConnectedSystemObjectTypes.Add(csType);
        seed.MetaverseObjectTypes.Add(mvType);
        await seed.SaveChangesAsync();

        var importRule = new SyncRule
        {
            Name = "Test Import Rule",
            Direction = SyncRuleDirection.Import,
            Enabled = true,
            ConnectedSystem = system,
            ConnectedSystemObjectType = csType,
            MetaverseObjectType = mvType
        };
        var importMapping = new SyncRuleMapping
        {
            SyncRule = importRule,
            TargetMetaverseAttribute = mvAttr,
            Priority = 1,
            NullIsValue = true
        };
        importMapping.Sources.Add(new SyncRuleMappingSource { Order = 0, ConnectedSystemAttribute = csAttr });
        var expressionMapping = new SyncRuleMapping
        {
            SyncRule = importRule,
            TargetMetaverseAttribute = mvAttr2,
            Priority = int.MaxValue
        };
        expressionMapping.Sources.Add(new SyncRuleMappingSource { Order = 0, Expression = "ToUpper(cs[\"cn\"])" });
        importRule.AttributeFlowRules.Add(importMapping);
        importRule.AttributeFlowRules.Add(expressionMapping);

        var exportRule = new SyncRule
        {
            Name = "Test Export Rule",
            Direction = SyncRuleDirection.Export,
            Enabled = true,
            EnforceState = true,
            ConnectedSystem = system,
            ConnectedSystemObjectType = csType,
            MetaverseObjectType = mvType
        };
        var exportMapping = new SyncRuleMapping
        {
            SyncRule = exportRule,
            TargetConnectedSystemAttribute = csAttr
        };
        exportMapping.Sources.Add(new SyncRuleMappingSource { Order = 0, MetaverseAttribute = mvAttr });
        exportRule.AttributeFlowRules.Add(exportMapping);

        seed.SyncRules.Add(importRule);
        seed.SyncRules.Add(exportRule);
        await seed.SaveChangesAsync();

        return new SeedIds(system.Id, mvAttr.Id, importMapping.Id, expressionMapping.Id, exportMapping.Id);
    }

    private record SeedIds(int SystemId, int MvAttrId, int ImportMappingId, int SecondImportMappingId, int ExportMappingId);
}
