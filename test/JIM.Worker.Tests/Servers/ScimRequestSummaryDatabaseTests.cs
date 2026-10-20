// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Connectors;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Scim;
using JIM.Models.Security;
using JIM.Models.Staging;
using JIM.PostgresData;
using JIM.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// The SCIM summary Activity (#124, D17): one Activity per Connected System, key and 15-minute window, counting the
/// creates, updates, deletes and rejections a SCIM client's requests made, so an initial load of thousands of Users is
/// one row, not thousands. Tallies arrive in batches from the web tier's buffer, so a window's row is created by the
/// first batch and added to by the rest, race-safe against concurrent flushes.
/// </summary>
/// <remarks>
/// Real PostgreSQL, as the upsert's unique index and atomic increments cannot be exercised in memory; opt-in via the
/// <c>JIM_TEST_RESET_*</c> environment variables like the other RequiresPostgres fixtures.
/// </remarks>
[TestFixture]
[Category("RequiresPostgres")]
public class ScimRequestSummaryDatabaseTests
{
    private static readonly DateTime Window = new(2026, 10, 20, 9, 15, 0, DateTimeKind.Utc);

    private string _connectionString = null!;
    private ApiKey _administrator = null!;
    private int _systemId;
    private ApiKey _key = null!;

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
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL SCIM summary Activity tests.");
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

        await WithJimAsync(jim => jim.FeatureFlags.SetFeatureFlagAsync(FeatureFlagCatalogue.ScimServiceProvider.Key, true, _administrator, allowInDevelopment: true));
        _systemId = await CreateServiceProviderSystemAsync("Entra provisioning");
        _key = (await WithJimAsync(jim => jim.Security.CreateScimServiceProviderKeyAsync(_systemId, "Entra key", null, null, _administrator))).Key;
    }

    [Test]
    public async Task RecordRequestTalliesAsync_NewWindow_OneActivityAttributedToTheKeyAsync()
    {
        await WithJimAsync(jim => jim.Scim.RecordRequestTalliesAsync([Tally(created: 3, updated: 2, deleted: 1)]));

        var activity = await SingleSummaryAsync();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(activity.ConnectedSystemId, Is.EqualTo(_systemId));
            Assert.That(activity.TargetName, Is.EqualTo("Entra provisioning"));
            Assert.That(activity.TargetOperationType, Is.EqualTo(ActivityTargetOperationType.ReceiveScimRequests));
            Assert.That(activity.InitiatedByType, Is.EqualTo(ActivityInitiatorType.ApiKey));
            Assert.That(activity.InitiatedById, Is.EqualTo(_key.Id));
            Assert.That(activity.InitiatedByName, Is.EqualTo("Entra key"));
            Assert.That(activity.AggregationWindowStart, Is.EqualTo(Window));
            Assert.That(activity.FirstSeen, Is.EqualTo(Window.AddMinutes(1)));
            Assert.That(activity.LastSeen, Is.EqualTo(Window.AddMinutes(2)));
            Assert.That((activity.TotalAdded, activity.TotalUpdated, activity.TotalDeleted, activity.TotalErrors), Is.EqualTo((3, 2, 1, 0)));
            Assert.That(activity.Status, Is.EqualTo(ActivityStatus.Complete));
            Assert.That(activity.WarningMessage, Is.Null);
        }
    }

    [Test]
    public async Task RecordRequestTalliesAsync_SameWindowAgain_AddsToTheSameActivityAsync()
    {
        await WithJimAsync(jim => jim.Scim.RecordRequestTalliesAsync([Tally(created: 3, firstMinute: 1, lastMinute: 2)]));
        await WithJimAsync(jim => jim.Scim.RecordRequestTalliesAsync([Tally(created: 1, updated: 4, firstMinute: 3, lastMinute: 7)]));

        var activity = await SingleSummaryAsync();
        using (Assert.EnterMultipleScope())
        {
            Assert.That((activity.TotalAdded, activity.TotalUpdated), Is.EqualTo((4, 4)));
            Assert.That(activity.FirstSeen, Is.EqualTo(Window.AddMinutes(1)), "the first request seen is kept");
            Assert.That(activity.LastSeen, Is.EqualTo(Window.AddMinutes(7)));
        }
    }

    [Test]
    public async Task RecordRequestTalliesAsync_Rejections_CompleteWithWarningNamingTheLatestAsync()
    {
        // The window opens clean, so the warning has to be raised by a later flush, not set when the Activity is created.
        await WithJimAsync(jim => jim.Scim.RecordRequestTalliesAsync([Tally(created: 1)]));
        await WithJimAsync(jim => jim.Scim.RecordRequestTalliesAsync([Tally(rejected: 2, latestRejection: "409 uniqueness on POST /Users")]));
        await WithJimAsync(jim => jim.Scim.RecordRequestTalliesAsync([Tally(rejected: 1, latestRejection: "412 on PATCH /Groups")]));
        await WithJimAsync(jim => jim.Scim.RecordRequestTalliesAsync([Tally(created: 1)]));

        var activity = await SingleSummaryAsync();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(activity.TotalErrors, Is.EqualTo(3));
            Assert.That(activity.Status, Is.EqualTo(ActivityStatus.CompleteWithWarning));
            Assert.That(activity.WarningMessage, Does.Contain("412 on PATCH /Groups"), "a later batch with no rejection keeps the latest one");
        }
    }

    [Test]
    public async Task RecordRequestTalliesAsync_AnotherKeyOrWindow_SeparateActivitiesAsync()
    {
        var second = (await WithJimAsync(jim => jim.Security.CreateScimServiceProviderKeyAsync(_systemId, "Entra key 2026", null, null, _administrator))).Key;

        await WithJimAsync(jim => jim.Scim.RecordRequestTalliesAsync(
        [
            Tally(created: 1),
            Tally(created: 1) with { ApiKeyId = second.Id, ApiKeyName = second.Name },
            Tally(created: 1) with { WindowStart = Window.AddMinutes(15) }
        ]));

        await using var ctx = NewContext();
        var count = await ctx.Activities.CountAsync(a => a.TargetType == ActivityTargetType.ScimRequests);
        Assert.That(count, Is.EqualTo(3));
    }

    [Test]
    public async Task RecordRequestTalliesAsync_ConcurrentFirstFlushes_OneActivityWithEveryCountAsync()
    {
        const int flushes = 16;

        await Task.WhenAll(Enumerable.Range(0, flushes).Select(_ => Task.Run(() =>
            WithJimAsync(jim => jim.Scim.RecordRequestTalliesAsync([Tally(created: 1, rejected: 1, latestRejection: "400 invalidValue on POST /Users")])))));

        var activity = await SingleSummaryAsync();
        Assert.That((activity.TotalAdded, activity.TotalErrors), Is.EqualTo((flushes, flushes)));
    }

    [Test]
    public async Task RecordRequestTalliesAsync_ConnectedSystemGone_DropsTheTallyWithoutThrowingAsync()
    {
        var tally = Tally(created: 1) with { ConnectedSystemId = int.MaxValue };

        Assert.That(() => WithJimAsync(jim => jim.Scim.RecordRequestTalliesAsync([tally])), Throws.Nothing);

        await using var ctx = NewContext();
        Assert.That(await ctx.Activities.CountAsync(a => a.TargetType == ActivityTargetType.ScimRequests), Is.Zero);
    }

    #region helpers

    private ScimRequestTally Tally(int created = 0, int updated = 0, int deleted = 0, int rejected = 0, string? latestRejection = null,
        int firstMinute = 1, int lastMinute = 2) => new()
    {
        ConnectedSystemId = _systemId,
        ApiKeyId = _key.Id,
        ApiKeyName = _key.Name,
        WindowStart = Window,
        FirstSeen = Window.AddMinutes(firstMinute),
        LastSeen = Window.AddMinutes(lastMinute),
        Created = created,
        Updated = updated,
        Deleted = deleted,
        Rejected = rejected,
        LatestRejection = latestRejection
    };

    private async Task<Activity> SingleSummaryAsync()
    {
        await using var ctx = NewContext();
        return await ctx.Activities.SingleAsync(a => a.TargetType == ActivityTargetType.ScimRequests);
    }

    private async Task<int> CreateServiceProviderSystemAsync(string name)
    {
        var definition = await WithJimAsync(jim => jim.ConnectedSystems.GetConnectorDefinitionAsync(ConnectorConstants.ScimServiceProviderConnectorName));
        var connectedSystem = new ConnectedSystem { Name = name, ConnectorDefinitionId = definition!.Id };
        await WithJimAsync(jim => jim.ConnectedSystems.CreateConnectedSystemAsync(connectedSystem, _administrator));
        return connectedSystem.Id;
    }

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
