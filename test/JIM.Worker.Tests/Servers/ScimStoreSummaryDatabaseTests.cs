// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Text.Json.Nodes;
using JIM.Application;
using JIM.Application.Exceptions;
using JIM.Connectors;
using JIM.Models.Core;
using JIM.Models.Scim;
using JIM.Models.Security;
using JIM.Models.Staging;
using JIM.PostgresData;
using JIM.PostgresData.Repositories;
using JIM.Scim;
using JIM.Scim.Service;
using JIM.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// What an administrator sees of a Connected System's SCIM store and keys (#124, Phase 5a): resources by type,
/// tombstones, the changes the next import will read, when a SCIM client last wrote, and the Connected System's own
/// keys. Refused while <c>Features.ScimServiceProvider</c> is off (D16).
/// </summary>
/// <remarks>
/// Real PostgreSQL and a no-tracking context, as JIM.Web runs; opt-in via the <c>JIM_TEST_RESET_*</c> environment
/// variables like the other RequiresPostgres fixtures.
/// </remarks>
[TestFixture]
[Category("RequiresPostgres")]
public class ScimStoreSummaryDatabaseTests
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
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL SCIM store summary tests.");
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
        _systemId = await CreateServiceProviderSystemAsync("Entra provisioning");
    }

    [Test]
    public async Task GetStoreSummaryAsync_EmptyStore_NothingAwaitingAndNoWriteAsync()
    {
        var summary = await WithJimAsync(jim => jim.Scim.GetStoreSummaryAsync(_systemId));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(summary.ResourceCounts.Select(c => (c.ResourceTypeName, c.Count)), Is.EquivalentTo(new[] { ("User", 0), ("Group", 0) }));
            Assert.That(summary.Tombstones, Is.Zero);
            Assert.That(summary.ChangesAwaitingImport, Is.Zero);
            Assert.That(summary.ImportedUpTo, Is.Null);
            Assert.That(summary.LastWrite, Is.Null);
            Assert.That(summary.RejectedLast24Hours, Is.Zero);
        }
    }

    [Test]
    public async Task GetStoreSummaryAsync_Rejections_CountsEveryKeysRejectionsFromTheLast24HoursAsync()
    {
        var first = (await WithJimAsync(jim => jim.Security.CreateScimServiceProviderKeyAsync(_systemId, "Entra key", null, null, _administrator))).Key;
        var second = (await WithJimAsync(jim => jim.Security.CreateScimServiceProviderKeyAsync(_systemId, "Entra key 2027", null, null, _administrator))).Key;
        var now = DateTime.UtcNow;
        var thisWindow = new DateTime(now.Ticks - now.Ticks % TimeSpan.FromMinutes(15).Ticks, DateTimeKind.Utc);

        await WithJimAsync(jim => jim.Scim.RecordRequestTalliesAsync(
        [
            Tally(first, thisWindow, rejected: 3),
            Tally(second, thisWindow.AddHours(-2), rejected: 2),
            Tally(first, thisWindow.AddDays(-2), rejected: 5)
        ]));

        var summary = await WithJimAsync(jim => jim.Scim.GetStoreSummaryAsync(_systemId));

        Assert.That(summary.RejectedLast24Hours, Is.EqualTo(5), "both keys' recent windows, not the one two days ago");
    }

    [Test]
    public async Task GetStoreSummaryAsync_NeverImported_CountsEveryResourceAsAwaitingAsync()
    {
        var alice = await CreateAsync("/Users", User("alice"));
        await CreateAsync("/Users", User("bob"));
        await CreateAsync("/Groups", Group("Engineers"));
        await WithJimAsync(jim => jim.Scim.DeleteAsync(Context(), "/Users", ScimReferenceEntries.Format(alice), ifMatch: null));

        var summary = await WithJimAsync(jim => jim.Scim.GetStoreSummaryAsync(_systemId));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(summary.ResourceCounts.Select(c => (c.ResourceTypeName, c.Count)), Is.EquivalentTo(new[] { ("User", 1), ("Group", 1) }));
            Assert.That(summary.Tombstones, Is.EqualTo(1));
            // Everything the store holds, tombstone included: a first import reads it all.
            Assert.That(summary.ChangesAwaitingImport, Is.EqualTo(3));
            Assert.That(summary.LastWrite, Is.EqualTo(DateTime.UtcNow).Within(TimeSpan.FromMinutes(1)));
        }
    }

    [Test]
    public async Task GetStoreSummaryAsync_AfterAnImport_CountsOnlyResourcesChangedSinceItsWatermarkAsync()
    {
        await CreateAsync("/Users", User("alice"));
        await CreateAsync("/Users", User("bob"));
        var imported = (await WithJimAsync(jim => jim.Scim.GetStoreStateAsync(_systemId)))!.LastChangeSequence;
        await SetWatermarkAsync(imported);
        await CreateAsync("/Users", User("carol"));

        var summary = await WithJimAsync(jim => jim.Scim.GetStoreSummaryAsync(_systemId));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(summary.ImportedUpTo, Is.EqualTo(imported));
            Assert.That(summary.ChangesAwaitingImport, Is.EqualTo(1));
            Assert.That(summary.LastChangeSequence, Is.GreaterThan(imported));
        }
    }

    [Test]
    public async Task GetStoreSummaryAsync_FlagOff_RefusedAsync()
    {
        await SetFlagAsync(false);

        Assert.That(() => WithJimAsync(jim => jim.Scim.GetStoreSummaryAsync(_systemId)), Throws.InstanceOf<FeatureDisabledException>());
    }

    [Test]
    public async Task GetScimServiceProviderKeysAsync_OnlyThisConnectedSystemsKeysAsync()
    {
        var other = await CreateServiceProviderSystemAsync("Okta provisioning");
        var mine = await WithJimAsync(jim => jim.Security.CreateScimServiceProviderKeyAsync(_systemId, "Entra key", null, null, _administrator));
        await WithJimAsync(jim => jim.Security.CreateScimServiceProviderKeyAsync(other, "Okta key", null, null, _administrator));

        var keys = await WithJimAsync(jim => jim.Security.GetScimServiceProviderKeysAsync(_systemId));

        Assert.That(keys.Select(k => k.Id), Is.EqualTo(new[] { mine.Key.Id }));
    }

    [Test]
    public async Task GetScimServiceProviderKeysAsync_FlagOff_RefusedAsync()
    {
        await SetFlagAsync(false);

        Assert.That(() => WithJimAsync(jim => jim.Security.GetScimServiceProviderKeysAsync(_systemId)), Throws.InstanceOf<FeatureDisabledException>());
    }

    #region helpers

    private ScimRequestTally Tally(ApiKey key, DateTime windowStart, int rejected) => new()
    {
        ConnectedSystemId = _systemId,
        ApiKeyId = key.Id,
        ApiKeyName = key.Name,
        WindowStart = windowStart,
        FirstSeen = windowStart,
        LastSeen = windowStart,
        Rejected = rejected,
        LatestRejection = "409 uniqueness on POST /Users"
    };

    private async Task<Guid> CreateAsync(string endpoint, JsonObject body)
    {
        var result = await WithJimAsync(jim => jim.Scim.CreateAsync(Context(), endpoint, body));
        Assert.That(result.StatusCode, Is.EqualTo(201), result.Error?.Detail);
        return Guid.Parse(result.Resource!["id"]!.GetValue<string>());
    }

    private ScimRequestContext Context() => new(_systemId, "https://jim.example/scim/v2", _administrator.Id, _administrator.Name, "203.0.113.9");

    private static JsonObject User(string userName) =>
        new() { ["schemas"] = new JsonArray(ScimUrns.User), ["userName"] = userName };

    private static JsonObject Group(string displayName) =>
        new() { ["schemas"] = new JsonArray(ScimUrns.Group), ["displayName"] = displayName };

    private async Task SetWatermarkAsync(long changeSequence)
    {
        await using var ctx = NewContext();
        await ctx.ConnectedSystems.Where(cs => cs.Id == _systemId)
            .ExecuteUpdateAsync(s => s.SetProperty(cs => cs.PersistedConnectorData, $"{{\"changeSequence\":{changeSequence}}}"));
    }

    private async Task<int> CreateServiceProviderSystemAsync(string name)
    {
        var definition = await WithJimAsync(jim => jim.ConnectedSystems.GetConnectorDefinitionAsync(ConnectorConstants.ScimServiceProviderConnectorName));
        var connectedSystem = new ConnectedSystem { Name = name, ConnectorDefinitionId = definition!.Id };
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
