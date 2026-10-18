// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Application.Exceptions;
using JIM.Connectors;
using JIM.Models.Core;
using JIM.Models.Security;
using JIM.Models.Staging;
using JIM.PostgresData;
using JIM.PostgresData.Repositories;
using JIM.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// The SCIM 2.0 Service Provider Connector as an administrator meets it (#124, Phase 3): seeded as a built-in Connector
/// Definition with no settings, hidden and refused while <c>Features.ScimServiceProvider</c> is off (D16), and a
/// Connected System created with it starting with the core User and Group definitions in its store.
/// </summary>
/// <remarks>
/// Real PostgreSQL and a no-tracking context, as JIM.Web runs; opt-in via the <c>JIM_TEST_RESET_*</c> environment
/// variables like the other RequiresPostgres fixtures.
/// </remarks>
[TestFixture]
[Category("RequiresPostgres")]
public class ScimServiceProviderConnectedSystemDatabaseTests
{
    private string _connectionString = null!;
    private ApiKey _key = null!;

    private JimDbContext NewContext() => new(new DbContextOptionsBuilder<JimDbContext>()
        .UseNpgsql(_connectionString)
        .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking)
        .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
        .Options);

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        var dbName = Environment.GetEnvironmentVariable("JIM_TEST_RESET_DB");
        if (string.IsNullOrEmpty(dbName))
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL SCIM Service Provider tests.");

        var host = Environment.GetEnvironmentVariable("JIM_TEST_RESET_HOST") ?? "localhost";
        var user = Environment.GetEnvironmentVariable("JIM_TEST_RESET_USER") ?? "postgres";
        var pass = Environment.GetEnvironmentVariable("JIM_TEST_RESET_PASSWORD") ?? "postgres";
        var port = Environment.GetEnvironmentVariable("JIM_TEST_RESET_PORT") ?? "5432";
        _connectionString = $"Host={host};Port={port};Database={dbName};Username={user};Password={pass}";

        using var ctx = NewContext();
        ctx.Database.Migrate();
    }

    [SetUp]
    public async Task SetUpAsync()
    {
        await PostgresTestDatabase.ResetAsync(_connectionString);
        await WithJimAsync(jim => jim.Seeding.ApplyBuiltInConfigurationAsync());

        await using var ctx = NewContext();
        _key = new ApiKey { Id = Guid.NewGuid(), Name = "Administrator", KeyHash = "hash", KeyPrefix = "jim_ak_test" };
        ctx.ApiKeys.Add(_key);
        await ctx.SaveChangesAsync();
    }

    [Test]
    public async Task BuiltInConfiguration_SeedsTheServiceProviderDefinitionWithNoSettingsAsync()
    {
        var definition = await WithJimAsync(jim => jim.ConnectedSystems.GetConnectorDefinitionAsync(ConnectorConstants.ScimServiceProviderConnectorName));

        Assert.That(definition, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(definition!.Settings, Is.Empty);
            Assert.That(definition.SupportsFullImport, Is.True);
            Assert.That(definition.SupportsDeltaImport, Is.True);
            Assert.That(definition.SupportsExport, Is.False);
        }
    }

    [Test]
    public async Task GetConnectorDefinitionHeadersAsync_FlagOff_LeavesTheServiceProviderOutAsync()
    {
        var names = await WithJimAsync(async jim => (await jim.ConnectedSystems.GetConnectorDefinitionHeadersAsync()).Select(h => h.Name).ToList());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(names, Does.Not.Contain(ConnectorConstants.ScimServiceProviderConnectorName));
            Assert.That(names, Does.Contain(ConnectorConstants.ScimClientConnectorName));
        }
    }

    [Test]
    public async Task GetConnectorDefinitionHeadersAsync_FlagOn_IncludesTheServiceProviderAsync()
    {
        await EnableFlagAsync();

        var names = await WithJimAsync(async jim => (await jim.ConnectedSystems.GetConnectorDefinitionHeadersAsync()).Select(h => h.Name).ToList());

        Assert.That(names, Does.Contain(ConnectorConstants.ScimServiceProviderConnectorName));
    }

    [Test]
    public async Task CreateConnectedSystemAsync_FlagOff_RefusedAndNothingCreatedAsync()
    {
        var definitionId = await ServiceProviderDefinitionIdAsync();

        Assert.That(() => WithJimAsync(jim => jim.ConnectedSystems.CreateConnectedSystemAsync(
                new ConnectedSystem { Name = "Entra provisioning", ConnectorDefinitionId = definitionId }, _key)),
            Throws.InstanceOf<FeatureDisabledException>());

        await using var ctx = NewContext();
        Assert.That(await ctx.ConnectedSystems.CountAsync(), Is.Zero);
    }

    [Test]
    public async Task CreateConnectedSystemAsync_FlagOn_CreatesItWithTheCoreDefinitionsInItsStoreAsync()
    {
        await EnableFlagAsync();
        var definitionId = await ServiceProviderDefinitionIdAsync();
        var connectedSystem = new ConnectedSystem { Name = "Entra provisioning", ConnectorDefinitionId = definitionId };

        await WithJimAsync(jim => jim.ConnectedSystems.CreateConnectedSystemAsync(connectedSystem, _key));

        await using var ctx = NewContext();
        var store = new PostgresDataRepository(ctx).ScimStore;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(connectedSystem.Id, Is.Positive);
            Assert.That(connectedSystem.SettingValues, Is.Empty);
            // Nothing to fill in or validate, so the Schema and Run Profiles tabs are open straight away.
            Assert.That(connectedSystem.SettingValuesValid, Is.True);
            Assert.That((await store.GetResourceTypesAsync(connectedSystem.Id)).Select(t => t.Name), Is.EquivalentTo(new[] { "User", "Group" }));
        }
    }

    [Test]
    public async Task CreateConnectedSystemAsync_OtherConnectorDefinitionWithItsSettingsMissing_RefusedAsync()
    {
        // A definition with no settings is right only for a Connector that declares none; for any other it means the
        // settings were never loaded, and a Connected System built from it would have no setting values at all.
        var ldap = await WithJimAsync(jim => jim.ConnectedSystems.GetConnectorDefinitionAsync(ConnectorConstants.LdapConnectorName));
        ldap!.Settings.Clear();

        Assert.That(() => WithJimAsync(jim => jim.ConnectedSystems.CreateConnectedSystemAsync(
                new ConnectedSystem { Name = "Directory", ConnectorDefinition = ldap, ConnectorDefinitionId = ldap.Id }, _key)),
            Throws.ArgumentException);
    }

    private async Task EnableFlagAsync() =>
        await WithJimAsync(jim => jim.FeatureFlags.SetFeatureFlagAsync(FeatureFlagCatalogue.ScimServiceProvider.Key, true,
            _key, allowInDevelopment: true));

    private async Task<int> ServiceProviderDefinitionIdAsync() =>
        (await WithJimAsync(jim => jim.ConnectedSystems.GetConnectorDefinitionAsync(ConnectorConstants.ScimServiceProviderConnectorName)))!.Id;

    private async Task<T> WithJimAsync<T>(Func<JimApplication, Task<T>> call)
    {
        await using var ctx = NewContext();
        using var jim = new JimApplication(new PostgresDataRepository(ctx));
        return await call(jim);
    }

    private async Task WithJimAsync(Func<JimApplication, Task> call)
    {
        await using var ctx = NewContext();
        using var jim = new JimApplication(new PostgresDataRepository(ctx));
        await call(jim);
    }
}
