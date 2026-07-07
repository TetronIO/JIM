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
/// Real-PostgreSQL verification of the attribute contributor-count aggregate (#91, Surface 2 multi-contributor badge):
/// <see cref="JIM.Application.Servers.ConnectedSystemServer.GetAttributeContributorCountsAsync"/> and the repository
/// query it sits on (<c>GetImportSyncRuleMappingsForMetaverseObjectTypeAsync</c>). The EF Core in-memory provider
/// cannot be trusted for the GROUP BY translation or the direction/object-type/null-target filters, so this exercises
/// them against a real database. Opt-in via <c>JIM_TEST_RESET_DB</c>; ignored when it is absent.
/// </summary>
[TestFixture]
[Category("RequiresPostgres")]
public class AttributeContributorCountsDatabaseTests
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
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL attribute contributor-count tests.");

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

    /// <summary>
    /// Seeds a Person object type with two attributes (department, jobTitle) contributed by import rules (department by
    /// three, one of them disabled; jobTitle by one), an export rule that must not be counted, and a separate Group
    /// object type whose attribute must not appear in the Person counts. Returns the two Person attribute ids.
    /// </summary>
    private async Task<(int PersonTypeId, int DepartmentId, int JobTitleId, int GroupTypeId, int GroupNameId)> SeedAsync()
    {
        await using var seed = NewContext();

        var connectorDefinition = new ConnectorDefinition { Name = "Test Connector", BuiltIn = true };
        var system = new ConnectedSystem { Name = "Test System", ConnectorDefinition = connectorDefinition };
        var csType = new ConnectedSystemObjectType { Name = "user", ConnectedSystem = system, Selected = true };
        var csAttr = new ConnectedSystemObjectTypeAttribute { Name = "title", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.SingleValued, ConnectedSystemObjectType = csType, Selected = true };
        csType.Attributes.Add(csAttr);

        var personType = new MetaverseObjectType { Name = "Person", PluralName = "People", BuiltIn = true };
        var department = new MetaverseAttribute { Name = "department", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.SingleValued, BuiltIn = true };
        var jobTitle = new MetaverseAttribute { Name = "jobTitle", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.SingleValued, BuiltIn = true };
        personType.Attributes.Add(department);
        personType.Attributes.Add(jobTitle);

        var groupType = new MetaverseObjectType { Name = "Group", PluralName = "Groups", BuiltIn = true };
        var groupName = new MetaverseAttribute { Name = "displayName", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.SingleValued, BuiltIn = true };
        groupType.Attributes.Add(groupName);

        seed.ConnectorDefinitions.Add(connectorDefinition);
        seed.ConnectedSystems.Add(system);
        seed.ConnectedSystemObjectTypes.Add(csType);
        seed.MetaverseObjectTypes.Add(personType);
        seed.MetaverseObjectTypes.Add(groupType);
        await seed.SaveChangesAsync();

        SyncRule ImportRule(string name, MetaverseObjectType mvType, bool enabled = true) => new()
        {
            Name = name,
            Direction = SyncRuleDirection.Import,
            Enabled = enabled,
            ConnectedSystem = system,
            ConnectedSystemObjectType = csType,
            MetaverseObjectType = mvType
        };

        // department: three import contributors (one disabled, still counted); jobTitle: one contributor.
        var rule1 = ImportRule("Person Import A", personType);
        rule1.AttributeFlowRules.Add(new SyncRuleMapping { TargetMetaverseAttribute = department });

        var rule2 = ImportRule("Person Import B", personType);
        rule2.AttributeFlowRules.Add(new SyncRuleMapping { TargetMetaverseAttribute = department });
        rule2.AttributeFlowRules.Add(new SyncRuleMapping { TargetMetaverseAttribute = jobTitle });

        var rule3 = ImportRule("Person Import C (disabled)", personType, enabled: false);
        rule3.AttributeFlowRules.Add(new SyncRuleMapping { TargetMetaverseAttribute = department });

        // Export rule targeting a Connected System attribute: must never be counted (not an import to a MV attribute).
        var exportRule = new SyncRule
        {
            Name = "Person Export",
            Direction = SyncRuleDirection.Export,
            Enabled = true,
            ConnectedSystem = system,
            ConnectedSystemObjectType = csType,
            MetaverseObjectType = personType
        };
        exportRule.AttributeFlowRules.Add(new SyncRuleMapping { TargetConnectedSystemAttribute = csAttr });

        // A different object type's import: must not leak into the Person counts.
        var groupRule = ImportRule("Group Import", groupType);
        groupRule.AttributeFlowRules.Add(new SyncRuleMapping { TargetMetaverseAttribute = groupName });

        seed.SyncRules.AddRange(rule1, rule2, rule3, exportRule, groupRule);
        await seed.SaveChangesAsync();

        return (personType.Id, department.Id, jobTitle.Id, groupType.Id, groupName.Id);
    }

    [Test]
    public async Task GetAttributeContributorCountsAsync_CountsImportContributorsPerAttribute_ExcludingExportsAndOtherTypesAsync()
    {
        var ids = await SeedAsync();

        await using var ctx = NewContext();
        var jim = new JimApplication(new PostgresDataRepository(ctx));
        var counts = await jim.ConnectedSystems.GetAttributeContributorCountsAsync(ids.PersonTypeId);

        Assert.Multiple(() =>
        {
            // department has three import contributors (including the disabled rule, which holds position).
            Assert.That(counts.GetValueOrDefault(ids.DepartmentId), Is.EqualTo(3), "department contributor count");
            // jobTitle has a single import contributor.
            Assert.That(counts.GetValueOrDefault(ids.JobTitleId), Is.EqualTo(1), "jobTitle contributor count");
            // The export rule contributes to no Metaverse attribute, so it adds nothing.
            // A different object type's attribute must not appear in this type's counts.
            Assert.That(counts.ContainsKey(ids.GroupNameId), Is.False, "Group attribute must not leak into Person counts");
            // Exactly the two Person attributes that have contributors.
            Assert.That(counts, Has.Count.EqualTo(2), "only attributes with contributors are present");
        });
    }

    [Test]
    public async Task GetAttributeContributorCountsAsync_ScopesToTheRequestedObjectType_ReturnsThatTypesAttributesAsync()
    {
        var ids = await SeedAsync();

        await using var ctx = NewContext();
        var jim = new JimApplication(new PostgresDataRepository(ctx));
        var groupCounts = await jim.ConnectedSystems.GetAttributeContributorCountsAsync(ids.GroupTypeId);

        Assert.Multiple(() =>
        {
            Assert.That(groupCounts.GetValueOrDefault(ids.GroupNameId), Is.EqualTo(1), "Group displayName contributor count");
            Assert.That(groupCounts.ContainsKey(ids.DepartmentId), Is.False, "Person attribute must not leak into Group counts");
            Assert.That(groupCounts, Has.Count.EqualTo(1));
        });
    }
}
