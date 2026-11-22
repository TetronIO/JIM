// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.PostgresData;
using JIM.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// An export Synchronisation Rule may deprovision with Disable only where its Connected System's Connector can disable
/// an account (#2085). The check is on the application server, so the portal, the REST API and PowerShell all meet it.
/// Real PostgreSQL, because the capability is read from the stored Connector Definition, untracked, as JIM.Web reads it.
/// </summary>
[TestFixture]
[Category("RequiresPostgres")]
public class DisableDeprovisionActionCapabilityDatabaseTests
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
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL Disable deprovisioning capability tests.");

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
    public async Task CreateOrUpdateSyncRuleAsync_DisableOnAConnectorThatCannotDisable_RefusesNamingTheConnectorAsync()
    {
        var ruleId = await SeedExportRuleAsync(supportsDisable: false);

        await using (var act = NewContext())
        {
            var repository = new PostgresDataRepository(act);
            using var jim = new JimApplication(repository, syncRepository: repository.Sync);
            var rule = (await jim.ConnectedSystems.GetSyncRuleAsync(ruleId))!;
            rule.OutboundDeprovisionAction = OutboundDeprovisionAction.Disable;

            var refusal = Assert.ThrowsAsync<ArgumentException>(() => jim.ConnectedSystems.CreateOrUpdateSyncRuleAsync(rule, TestUtilities.GetInitiatedBy()));
            Assert.That(refusal!.Message, Does.Contain("Test Connector").And.Contain("cannot disable"));
        }

        await using var verify = NewContext();
        Assert.That((await verify.SyncRules.SingleAsync(r => r.Id == ruleId)).OutboundDeprovisionAction, Is.EqualTo(OutboundDeprovisionAction.Delete));
    }

    [Test]
    public async Task CreateOrUpdateSyncRuleAsync_DisableOnAConnectorThatCanDisable_SavesAsync()
    {
        var ruleId = await SeedExportRuleAsync(supportsDisable: true);

        await using (var act = NewContext())
        {
            var repository = new PostgresDataRepository(act);
            using var jim = new JimApplication(repository, syncRepository: repository.Sync);
            var rule = (await jim.ConnectedSystems.GetSyncRuleAsync(ruleId))!;
            rule.OutboundDeprovisionAction = OutboundDeprovisionAction.Disable;
            await jim.ConnectedSystems.CreateOrUpdateSyncRuleAsync(rule, TestUtilities.GetInitiatedBy());
        }

        await using var verify = NewContext();
        Assert.That((await verify.SyncRules.SingleAsync(r => r.Id == ruleId)).OutboundDeprovisionAction, Is.EqualTo(OutboundDeprovisionAction.Disable));
    }

    private async Task<int> SeedExportRuleAsync(bool supportsDisable)
    {
        await using var seed = NewContext();
        var connectorDefinition = new ConnectorDefinition { Name = "Test Connector", BuiltIn = true, SupportsExport = true, SupportsDisable = supportsDisable };
        var system = new ConnectedSystem { Name = "Directory", ConnectorDefinition = connectorDefinition };
        var userType = new ConnectedSystemObjectType { Name = "user", ConnectedSystem = system, Selected = true };
        userType.Attributes.Add(new ConnectedSystemObjectTypeAttribute
        {
            Name = "objectGUID", Type = AttributeDataType.Guid, AttributePlurality = AttributePlurality.SingleValued,
            ConnectedSystemObjectType = userType, Selected = true, IsExternalId = true
        });
        var personType = new MetaverseObjectType { Name = "Person", PluralName = "People", BuiltIn = true };
        var rule = new SyncRule
        {
            Name = "Directory Users Outbound",
            Direction = SyncRuleDirection.Export,
            Enabled = false,
            ConnectedSystem = system,
            ConnectedSystemObjectType = userType,
            MetaverseObjectType = personType,
            OutboundDeprovisionAction = OutboundDeprovisionAction.Delete
        };

        seed.ConnectorDefinitions.Add(connectorDefinition);
        seed.MetaverseObjectTypes.Add(personType);
        seed.SyncRules.Add(rule);
        await seed.SaveChangesAsync();
        return rule.Id;
    }
}
