// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.PostgresData;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// Real-PostgreSQL verification of <see cref="JIM.PostgresData.Repositories.ConnectedSystemRepository.DeleteConnectedSystemAsync"/>.
/// The deletion is raw SQL, so the EF Core in-memory provider cannot catch column-name regressions; this fixture
/// runs it against a real database. Opt-in via the same <c>JIM_TEST_RESET_*</c> environment variables as
/// <see cref="SystemResetDatabaseTests"/>; ignored when <c>JIM_TEST_RESET_DB</c> is absent.
/// </summary>
/// <remarks>
/// Regression guard: the SyncRuleMapping schema consolidated onto a single <c>SyncRuleId</c> foreign key, but the
/// deletion SQL still referenced the removed <c>AttributeFlowSynchronisationRuleId</c> /
/// <c>ObjectMatchingSynchronisationRuleId</c> columns, throwing PostgreSQL 42703 (undefined column) for every
/// connected-system deletion (the offending statement is parsed regardless of whether any sync rules exist).
/// </remarks>
[TestFixture]
[Category("RequiresPostgres")]
public class ConnectedSystemDeletionDatabaseTests
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
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL connected-system deletion tests.");

        var host = Environment.GetEnvironmentVariable("JIM_TEST_RESET_HOST") ?? "localhost";
        var user = Environment.GetEnvironmentVariable("JIM_TEST_RESET_USER") ?? "postgres";
        var pass = Environment.GetEnvironmentVariable("JIM_TEST_RESET_PASSWORD") ?? "postgres";
        _connectionString = $"Host={host};Database={dbName};Username={user};Password={pass}";

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
    public async Task DeleteConnectedSystemAsync_WithNoSyncRules_RemovesTheSystemAsync()
    {
        int systemId;
        await using (var seed = NewContext())
        {
            var connectorDefinition = new ConnectorDefinition { Name = "Test Connector", BuiltIn = true };
            var system = new ConnectedSystem { Name = "Empty System", ConnectorDefinition = connectorDefinition };
            seed.ConnectorDefinitions.Add(connectorDefinition);
            seed.ConnectedSystems.Add(system);
            await seed.SaveChangesAsync();
            systemId = system.Id;
        }

        await using (var ctx = NewContext())
        {
            var repository = new PostgresDataRepository(ctx);
            // Before the fix this throws PostgresException 42703 at the SyncRuleMappingSourceParamValues step.
            await repository.ConnectedSystems.DeleteConnectedSystemAsync(systemId, deleteChangeHistory: true);
        }

        await using var verify = NewContext();
        Assert.That(await verify.ConnectedSystems.AnyAsync(), Is.False);
    }

    [Test]
    public async Task DeleteConnectedSystemAsync_WithSyncRuleAndMapping_RemovesTheWholeGraphAsync()
    {
        int systemId;
        await using (var seed = NewContext())
        {
            var connectorDefinition = new ConnectorDefinition { Name = "Test Connector", BuiltIn = true };
            var system = new ConnectedSystem { Name = "Mapped System", ConnectorDefinition = connectorDefinition };
            var csType = new ConnectedSystemObjectType { Name = "USER", ConnectedSystem = system };
            var mvType = new JIM.Models.Core.MetaverseObjectType { Name = "User", PluralName = "Users", BuiltIn = true };

            var rule = new SyncRule
            {
                Name = "Import Rule",
                ConnectedSystem = system,
                ConnectedSystemObjectType = csType,
                MetaverseObjectType = mvType,
                Direction = SyncRuleDirection.Import,
                Enabled = true
            };
            var mapping = new SyncRuleMapping { SyncRule = rule };
            mapping.Sources.Add(new SyncRuleMappingSource { Order = 0, Expression = "\"literal\"" });

            seed.ConnectorDefinitions.Add(connectorDefinition);
            seed.ConnectedSystems.Add(system);
            seed.ConnectedSystemObjectTypes.Add(csType);
            seed.MetaverseObjectTypes.Add(mvType);
            seed.SyncRules.Add(rule);
            seed.Add(mapping);
            await seed.SaveChangesAsync();
            systemId = system.Id;
        }

        await using (var ctx = NewContext())
        {
            var repository = new PostgresDataRepository(ctx);
            await repository.ConnectedSystems.DeleteConnectedSystemAsync(systemId, deleteChangeHistory: true);
        }

        await using var verify = NewContext();
        Assert.That(await verify.ConnectedSystems.AnyAsync(), Is.False, "Connected system should be removed.");
        Assert.That(await verify.SyncRules.AnyAsync(), Is.False, "Sync rules should be removed.");
        Assert.That(await verify.SyncRuleMappings.AnyAsync(), Is.False, "Sync rule mappings should be removed.");
        Assert.That(await verify.SyncRuleMappingSources.AnyAsync(), Is.False, "Sync rule mapping sources should be removed.");
    }

    [Test]
    public async Task DeleteConnectedSystemAsync_WithObjectMatchingRule_RemovesTheWholeGraphAsync()
    {
        int systemId;
        await using (var seed = NewContext())
        {
            var connectorDefinition = new ConnectorDefinition { Name = "Test Connector", BuiltIn = true };
            var system = new ConnectedSystem { Name = "Matched System", ConnectorDefinition = connectorDefinition };
            var csType = new ConnectedSystemObjectType { Name = "USER", ConnectedSystem = system };
            var mvType = new JIM.Models.Core.MetaverseObjectType { Name = "User", PluralName = "Users", BuiltIn = true };

            var rule = new SyncRule
            {
                Name = "Import Rule",
                ConnectedSystem = system,
                ConnectedSystemObjectType = csType,
                MetaverseObjectType = mvType,
                Direction = SyncRuleDirection.Import,
                Enabled = true
            };

            // An object matching rule referencing both the system's object type and its sync rule, with a
            // source and a source parameter value (the OMR source graph cascades from the rule).
            var omr = new ObjectMatchingRule
            {
                Order = 0,
                ConnectedSystemObjectType = csType,
                SyncRule = rule,
                MetaverseObjectType = mvType
            };
            var omrSource = new ObjectMatchingRuleSource { Order = 0, Expression = "\"literal\"" };
            omrSource.ParameterValues.Add(new ObjectMatchingRuleSourceParamValue { Name = "p" });
            omr.Sources.Add(omrSource);

            seed.ConnectorDefinitions.Add(connectorDefinition);
            seed.ConnectedSystems.Add(system);
            seed.ConnectedSystemObjectTypes.Add(csType);
            seed.MetaverseObjectTypes.Add(mvType);
            seed.SyncRules.Add(rule);
            seed.ObjectMatchingRules.Add(omr);
            await seed.SaveChangesAsync();
            systemId = system.Id;
        }

        await using (var ctx = NewContext())
        {
            var repository = new PostgresDataRepository(ctx);
            await repository.ConnectedSystems.DeleteConnectedSystemAsync(systemId, deleteChangeHistory: true);
        }

        await using var verify = NewContext();
        Assert.That(await verify.ConnectedSystems.AnyAsync(), Is.False, "Connected system should be removed.");
        Assert.That(await verify.SyncRules.AnyAsync(), Is.False, "Sync rules should be removed.");
        Assert.That(await verify.ObjectMatchingRules.AnyAsync(), Is.False, "Object matching rules should be removed.");
        Assert.That(await verify.ObjectMatchingRuleSources.AnyAsync(), Is.False, "Object matching rule sources should be removed.");
        Assert.That(await verify.ObjectMatchingRuleSourceParamValues.AnyAsync(), Is.False, "Object matching rule source parameter values should be removed.");
    }
}
