// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Security;
using JIM.Models.Staging;
using JIM.PostgresData;
using JIM.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// An API key's scope (#124, D3) as the database holds it: a scoped key always names its Connected System and an
/// unscoped one never does, and deleting the Connected System deletes its scoped keys.
/// </summary>
[TestFixture]
[Category("RequiresPostgres")]
public class ApiKeyScopePersistenceDatabaseTests
{
    private string _connectionString = null!;
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
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL API key scope tests.");
        _connectionString = connectionString!;

        using var ctx = NewContext();
        ctx.Database.Migrate();
    }

    [SetUp]
    public async Task SetUpAsync()
    {
        await PostgresTestDatabase.ResetAsync(_connectionString);

        await using var ctx = NewContext();
        var definition = new ConnectorDefinition { Name = "api-key-scope-tests" };
        ctx.ConnectorDefinitions.Add(definition);
        await ctx.SaveChangesAsync();
        var system = new ConnectedSystem { Name = "Entra provisioning", ConnectorDefinitionId = definition.Id };
        ctx.ConnectedSystems.Add(system);
        await ctx.SaveChangesAsync();
        _systemId = system.Id;
    }

    [Test]
    public async Task ScopedKey_RoundTrips_WithItsConnectedSystemAsync()
    {
        var id = await AddKeyAsync(ApiKeyScope.ScimServiceProvider, _systemId);

        await using var ctx = NewContext();
        var key = await ctx.ApiKeys.SingleAsync(k => k.Id == id);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(key.Scope, Is.EqualTo(ApiKeyScope.ScimServiceProvider));
            Assert.That(key.ScopeConnectedSystemId, Is.EqualTo(_systemId));
        }
    }

    [Test]
    public async Task DeletingTheConnectedSystem_DeletesItsScopedKeysAsync()
    {
        await AddKeyAsync(ApiKeyScope.ScimServiceProvider, _systemId);
        var unscoped = await AddKeyAsync(ApiKeyScope.None, null);

        await using (var ctx = NewContext())
            await ctx.ConnectedSystems.Where(c => c.Id == _systemId).ExecuteDeleteAsync();

        await using var check = NewContext();
        Assert.That(await check.ApiKeys.Select(k => k.Id).ToListAsync(), Is.EqualTo(new[] { unscoped }));
    }

    [TestCase(ApiKeyScope.ScimServiceProvider, false)]
    [TestCase(ApiKeyScope.None, true)]
    public void ScopeAndConnectedSystem_DisagreeingAreRefusedByTheDatabase(ApiKeyScope scope, bool withSystem)
    {
        Assert.That(() => AddKeyAsync(scope, withSystem ? _systemId : null), Throws.InstanceOf<DbUpdateException>());
    }

    private async Task<Guid> AddKeyAsync(ApiKeyScope scope, int? systemId)
    {
        await using var ctx = NewContext();
        var key = new ApiKey
        {
            Id = Guid.NewGuid(),
            Name = $"key {Guid.NewGuid():N}",
            KeyHash = Guid.NewGuid().ToString("N"),
            KeyPrefix = "jim_ak_test",
            Scope = scope,
            ScopeConnectedSystemId = systemId,
            ExpiresAt = DateTime.UtcNow.AddDays(30)
        };
        ctx.ApiKeys.Add(key);
        await ctx.SaveChangesAsync();
        return key.Id;
    }
}
