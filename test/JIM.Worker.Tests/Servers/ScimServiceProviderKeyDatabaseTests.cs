// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Application.Exceptions;
using JIM.Application.Servers;
using JIM.Application.Utilities;
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
/// Minting a SCIM client's key (#124, D3): scoped to one Connected System using the SCIM 2.0 Service Provider
/// Connector, carrying no roles, always expiring (a year by default, at most two), and at most two active at once so
/// keys can be rotated with an overlap. Once minted, an update can neither widen nor strip its scope.
/// </summary>
/// <remarks>
/// Real PostgreSQL and a no-tracking context, as JIM.Web runs; opt-in via the <c>JIM_TEST_RESET_*</c> environment
/// variables like the other RequiresPostgres fixtures.
/// </remarks>
[TestFixture]
[Category("RequiresPostgres")]
public class ScimServiceProviderKeyDatabaseTests
{
    private string _connectionString = null!;
    private ApiKey _administrator = null!;
    private int _systemId;

    private JimDbContext NewContext() => new(new DbContextOptionsBuilder<JimDbContext>()
        .UseNpgsql(_connectionString)
        .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking)
        .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
        .Options);

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        var connectionString = PostgresTestDatabase.ConnectionStringFromEnvironment();
        if (connectionString == null)
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL SCIM key tests.");
        _connectionString = connectionString!;

        using var ctx = NewContext();
        ctx.Database.Migrate();
    }

    [SetUp]
    public async Task SetUpAsync()
    {
        await PostgresTestDatabase.ResetAsync(_connectionString);
        await WithJimAsync(jim => jim.Seeding.ApplyBuiltInConfigurationAsync());

        await using (var ctx = NewContext())
        {
            _administrator = new ApiKey { Id = Guid.NewGuid(), Name = "Administrator", KeyHash = "hash", KeyPrefix = "jim_ak_test" };
            ctx.ApiKeys.Add(_administrator);
            await ctx.SaveChangesAsync();
        }

        await SetFlagAsync(true);
        _systemId = await CreateConnectedSystemAsync(ConnectorConstants.ScimServiceProviderConnectorName);
    }

    #region create
    [Test]
    public async Task CreateScimServiceProviderKeyAsync_Defaults_ScopedToTheSystemWithNoRolesForAYearAsync()
    {
        var before = DateTime.UtcNow;

        var created = await MintAsync();

        var stored = await StoredAsync(created.Key.Id);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(stored.Scope, Is.EqualTo(ApiKeyScope.ScimServiceProvider));
            Assert.That(stored.ScopeConnectedSystemId, Is.EqualTo(_systemId));
            Assert.That(stored.Roles, Is.Empty);
            Assert.That(stored.ExpiresAt, Is.EqualTo(before.Add(SecurityServer.ScopedApiKeyDefaultLifetime)).Within(TimeSpan.FromMinutes(1)));
            Assert.That(created.Secret, Does.StartWith(ApiKeyMaterial.Prefix));
            Assert.That(stored.KeyHash, Is.EqualTo(ApiKeyMaterial.Hash(created.Secret)));
            Assert.That(stored.KeyPrefix, Is.EqualTo(created.Secret[..12]));
        }
    }

    [Test]
    public async Task CreateScimServiceProviderKeyAsync_ExpiryWithinTheCap_IsKeptAsync()
    {
        var expiresAt = DateTime.UtcNow.AddDays(90);

        var created = await MintAsync(expiresAt: expiresAt);

        Assert.That((await StoredAsync(created.Key.Id)).ExpiresAt, Is.EqualTo(expiresAt).Within(TimeSpan.FromSeconds(1)));
    }

    [TestCase(731)]
    [TestCase(-1)]
    public void CreateScimServiceProviderKeyAsync_ExpiryBeyondTheCapOrPast_RefusedAsync(int days)
    {
        Assert.That(() => MintAsync(expiresAt: DateTime.UtcNow.AddDays(days)), Throws.ArgumentException);
    }

    [Test]
    public async Task CreateScimServiceProviderKeyAsync_TwoActiveAlready_RefusedAsync()
    {
        await MintAsync();
        await MintAsync();

        Assert.That(() => MintAsync(), Throws.InvalidOperationException.With.Message.Contains("two"));
    }

    [Test]
    public async Task CreateScimServiceProviderKeyAsync_DisabledOrExpiredKeysDoNotCountAsync()
    {
        var disabled = await MintAsync();
        var expired = await MintAsync();
        await using (var ctx = NewContext())
        {
            await ctx.ApiKeys.Where(k => k.Id == disabled.Key.Id).ExecuteUpdateAsync(s => s.SetProperty(k => k.IsEnabled, false));
            await ctx.ApiKeys.Where(k => k.Id == expired.Key.Id).ExecuteUpdateAsync(s => s.SetProperty(k => k.ExpiresAt, DateTime.UtcNow.AddDays(-1)));
        }

        Assert.That(() => MintAsync(), Throws.Nothing);
    }

    [Test]
    public async Task CreateScimServiceProviderKeyAsync_OtherConnector_RefusedAsync()
    {
        var other = await CreateConnectedSystemAsync(ConnectorConstants.FileConnectorName);

        Assert.That(() => MintAsync(connectedSystemId: other), Throws.ArgumentException);
    }

    [Test]
    public void CreateScimServiceProviderKeyAsync_NoSuchConnectedSystem_RefusedAsync()
    {
        Assert.That(() => MintAsync(connectedSystemId: int.MaxValue), Throws.ArgumentException);
    }

    [Test]
    public async Task CreateScimServiceProviderKeyAsync_FlagOff_RefusedAsync()
    {
        await SetFlagAsync(false);

        Assert.That(() => MintAsync(), Throws.InstanceOf<FeatureDisabledException>());
    }

    [Test]
    public void CreateApiKeyAsync_WithAScope_RefusedAsync()
    {
        // A scoped key is minted only by the method that enforces its rules.
        var key = new ApiKey
        {
            Id = Guid.NewGuid(), Name = "Sneaky", KeyHash = "h2", KeyPrefix = "jim_ak_test",
            Scope = ApiKeyScope.ScimServiceProvider, ScopeConnectedSystemId = _systemId, ExpiresAt = DateTime.UtcNow.AddDays(30)
        };

        Assert.That(() => WithJimAsync(jim => jim.Security.CreateApiKeyAsync(key, _administrator)), Throws.ArgumentException);
    }
    #endregion

    #region update
    [Test]
    public async Task UpdateApiKeyAsync_ScopedKeyGivenARole_RefusedAsync()
    {
        var created = await MintAsync();
        var key = await StoredAsync(created.Key.Id);
        key.Roles = [await AdministratorRoleAsync()];

        Assert.That(() => WithJimAsync(jim => jim.Security.UpdateApiKeyAsync(key, _administrator)), Throws.ArgumentException);
        Assert.That((await StoredAsync(created.Key.Id)).Roles, Is.Empty);
    }

    [Test]
    public async Task UpdateApiKeyAsync_ScopedKeyUnscoped_RefusedAsync()
    {
        var created = await MintAsync();
        var key = await StoredAsync(created.Key.Id);
        key.Scope = ApiKeyScope.None;
        key.ScopeConnectedSystemId = null;

        Assert.That(() => WithJimAsync(jim => jim.Security.UpdateApiKeyAsync(key, _administrator)), Throws.ArgumentException);
    }

    [Test]
    public async Task UpdateApiKeyAsync_UnscopedKeyGivenAScope_RefusedAsync()
    {
        var key = await StoredAsync(_administrator.Id);
        key.Scope = ApiKeyScope.ScimServiceProvider;
        key.ScopeConnectedSystemId = _systemId;

        Assert.That(() => WithJimAsync(jim => jim.Security.UpdateApiKeyAsync(key, _administrator)), Throws.ArgumentException);
    }

    [TestCase(731)]
    [TestCase(null)]
    public async Task UpdateApiKeyAsync_ScopedKeyExpiryBeyondTheCapOrRemoved_RefusedAsync(int? days)
    {
        var created = await MintAsync();
        var key = await StoredAsync(created.Key.Id);
        key.ExpiresAt = days == null ? null : DateTime.UtcNow.AddDays(days.Value);

        Assert.That(() => WithJimAsync(jim => jim.Security.UpdateApiKeyAsync(key, _administrator)), Throws.ArgumentException);
    }

    [Test]
    public async Task UpdateApiKeyAsync_ReEnablingAThirdActiveKey_RefusedAsync()
    {
        var first = await MintAsync();
        await using (var ctx = NewContext())
            await ctx.ApiKeys.Where(k => k.Id == first.Key.Id).ExecuteUpdateAsync(s => s.SetProperty(k => k.IsEnabled, false));
        await MintAsync();
        await MintAsync();
        var key = await StoredAsync(first.Key.Id);
        key.IsEnabled = true;

        Assert.That(() => WithJimAsync(jim => jim.Security.UpdateApiKeyAsync(key, _administrator)), Throws.InvalidOperationException);
    }

    [Test]
    public async Task UpdateApiKeyAsync_ScopedKeyRenamed_SavedAsync()
    {
        var created = await MintAsync();
        var key = await StoredAsync(created.Key.Id);
        key.Name = "Entra provisioning (rotated)";

        await WithJimAsync(jim => jim.Security.UpdateApiKeyAsync(key, _administrator));

        Assert.That((await StoredAsync(created.Key.Id)).Name, Is.EqualTo("Entra provisioning (rotated)"));
    }
    #endregion

    #region helpers
    private Task<CreatedApiKey> MintAsync(int? connectedSystemId = null, DateTime? expiresAt = null) =>
        WithJimAsync(jim => jim.Security.CreateScimServiceProviderKeyAsync(connectedSystemId ?? _systemId,
            $"Entra provisioning {Guid.NewGuid():N}", description: null, expiresAt, _administrator));

    private async Task<ApiKey> StoredAsync(Guid id) =>
        (await WithJimAsync(jim => jim.Security.GetApiKeyAsync(id)))!;

    private async Task<Role> AdministratorRoleAsync()
    {
        await using var ctx = NewContext();
        return await ctx.Roles.SingleAsync(r => r.Name == Constants.BuiltInRoles.Administrator);
    }

    private async Task<int> CreateConnectedSystemAsync(string connectorName)
    {
        var definition = await WithJimAsync(jim => jim.ConnectedSystems.GetConnectorDefinitionAsync(connectorName));
        var connectedSystem = new ConnectedSystem { Name = $"{connectorName} system", ConnectorDefinitionId = definition!.Id };
        await WithJimAsync(jim => jim.ConnectedSystems.CreateConnectedSystemAsync(connectedSystem, _administrator));
        return connectedSystem.Id;
    }

    private Task SetFlagAsync(bool enabled) =>
        WithJimAsync(jim => jim.FeatureFlags.SetFeatureFlagAsync(FeatureFlagCatalogue.ScimServiceProvider.Key, enabled, _administrator, allowInDevelopment: true));

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
    #endregion
}
