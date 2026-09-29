// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.PostgresData;
using JIM.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NUnit.Framework;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// Real-PostgreSQL verification of <c>GetImportSyncRulesForMetaverseObjectTypeAsync</c> (#1750), the read the
/// save-time Metaverse-Derived Attribute Flow validation builds its graph from. The in-memory provider cannot be
/// trusted for missing <c>Include</c>s (it fixes navigations up regardless) or for tracking behaviour, and both matter
/// here: the graph reads each mapping's expression sources and generation settings, and the read must report what the
/// database holds even while the caller is holding a tracked, already-mutated mapping. Opt-in via
/// <c>JIM_TEST_RESET_DB</c>; ignored when it is absent.
/// </summary>
[TestFixture]
[Category("RequiresPostgres")]
public class ImportSyncRulesForMetaverseObjectTypeDatabaseTests
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
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL import Synchronisation Rule read tests.");

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

    /// <summary>
    /// Seeds Person with an enabled import rule (an Email expression reading mv) and a disabled one (a generated
    /// Account Name with a base expression), a Person export rule, and a Group import rule. Returns the Person type id
    /// and the Email mapping id.
    /// </summary>
    private async Task<(int PersonTypeId, int EmailMappingId)> SeedAsync()
    {
        await using var seed = NewContext();

        var connectorDefinition = new ConnectorDefinition { Name = "Test Connector", BuiltIn = true };
        var system = new ConnectedSystem { Name = "HR", ConnectorDefinition = connectorDefinition };
        var csType = new ConnectedSystemObjectType { Name = "user", ConnectedSystem = system, Selected = true };
        var csMail = new ConnectedSystemObjectTypeAttribute { Name = "mail", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.SingleValued, ConnectedSystemObjectType = csType, Selected = true };
        csType.Attributes.Add(csMail);

        var personType = new MetaverseObjectType { Name = "Person", PluralName = "People", BuiltIn = true };
        var accountName = new MetaverseAttribute { Name = "Account Name", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.SingleValued };
        var email = new MetaverseAttribute { Name = "Email", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.SingleValued };
        personType.Attributes.Add(accountName);
        personType.Attributes.Add(email);
        var groupType = new MetaverseObjectType { Name = "Group", PluralName = "Groups", BuiltIn = true };
        var groupName = new MetaverseAttribute { Name = "Name", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.SingleValued };
        groupType.Attributes.Add(groupName);

        seed.ConnectorDefinitions.Add(connectorDefinition);
        seed.ConnectedSystems.Add(system);
        seed.ConnectedSystemObjectTypes.Add(csType);
        seed.MetaverseObjectTypes.AddRange(personType, groupType);
        await seed.SaveChangesAsync();

        SyncRule Rule(string name, SyncRuleDirection direction, MetaverseObjectType type, bool enabled = true) => new()
        {
            Name = name,
            Direction = direction,
            Enabled = enabled,
            ConnectedSystem = system,
            ConnectedSystemObjectType = csType,
            MetaverseObjectType = type
        };

        var hr = Rule("HR Import", SyncRuleDirection.Import, personType);
        var emailMapping = new SyncRuleMapping { TargetMetaverseAttribute = email };
        emailMapping.Sources.Add(new SyncRuleMappingSource { Order = 0, Expression = "mv[\"Account Name\"] + \"@corp.local\"" });
        hr.AttributeFlowRules.Add(emailMapping);

        var paused = Rule("Paused Import", SyncRuleDirection.Import, personType, enabled: false);
        var generated = new SyncRuleMapping
        {
            TargetMetaverseAttribute = accountName,
            Enabled = false,
            Generation = new SyncRuleMappingGeneration { TokenKind = GeneratedValueTokenKind.OnlyIfTaken }
        };
        generated.Sources.Add(new SyncRuleMappingSource { Order = 0, Expression = "Lower(cs[\"givenName\"])" });
        paused.AttributeFlowRules.Add(generated);

        var export = Rule("Person Export", SyncRuleDirection.Export, personType);
        export.AttributeFlowRules.Add(new SyncRuleMapping { TargetConnectedSystemAttribute = csMail });

        var groups = Rule("Group Import", SyncRuleDirection.Import, groupType);
        groups.AttributeFlowRules.Add(new SyncRuleMapping { TargetMetaverseAttribute = groupName });

        seed.SyncRules.AddRange(hr, paused, export, groups);
        await seed.SaveChangesAsync();

        return (personType.Id, emailMapping.Id);
    }

    [Test]
    public async Task GetImportSyncRulesForMetaverseObjectTypeAsync_ReturnsTheTypesImportRulesWithEverythingTheGraphReadsAsync()
    {
        var ids = await SeedAsync();

        await using var ctx = NewContext();
        var rules = await new PostgresDataRepository(ctx).ConnectedSystems.GetImportSyncRulesForMetaverseObjectTypeAsync(ids.PersonTypeId);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(rules.Select(r => r.Name), Is.EquivalentTo(new[] { "HR Import", "Paused Import" }),
                "import rules of the type only, disabled included; no export rule and no other type");

            var emailMapping = rules.Single(r => r.Name == "HR Import").AttributeFlowRules.Single();
            Assert.That(emailMapping.Sources.Single().Expression, Is.EqualTo("mv[\"Account Name\"] + \"@corp.local\""));
            Assert.That(emailMapping.TargetMetaverseAttribute?.Name, Is.EqualTo("Email"));

            var generated = rules.Single(r => r.Name == "Paused Import").AttributeFlowRules.Single();
            Assert.That(generated.Generation, Is.Not.Null, "a generated mapping's settings travel with it");
            Assert.That(generated.Enabled, Is.False);
            Assert.That(generated.Sources.Single().Expression, Is.EqualTo("Lower(cs[\"givenName\"])"));
        }
    }

    [Test]
    public async Task GetImportSyncRulesForMetaverseObjectTypeAsync_CallerHoldsATrackedMutatedMapping_ReportsTheDatabaseStateAsync()
    {
        var ids = await SeedAsync();

        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);
        // The settings-update path loads the mapping tracked and mutates it before validating.
        var tracked = await repository.ConnectedSystems.GetSyncRuleMappingForUpdateAsync(ids.EmailMappingId);
        tracked!.Sources[0].Expression = "cs[\"mail\"]";

        var rules = await repository.ConnectedSystems.GetImportSyncRulesForMetaverseObjectTypeAsync(ids.PersonTypeId);

        var persisted = rules.SelectMany(r => r.AttributeFlowRules).Single(m => m.Id == ids.EmailMappingId);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(persisted, Is.Not.SameAs(tracked), "the read must not hand back the caller's tracked instance");
            Assert.That(persisted.Sources.Single().Expression, Is.EqualTo("mv[\"Account Name\"] + \"@corp.local\""));
        }
    }
}
