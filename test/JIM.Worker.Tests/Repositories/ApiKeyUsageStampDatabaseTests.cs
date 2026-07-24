// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Security;
using JIM.PostgresData;
using JIM.PostgresData.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NUnit.Framework;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// Real-PostgreSQL verification of the API key usage stamp's relational path: a single
/// set-based UPDATE (no tracked SaveChanges) with the throttle threshold in the predicate.
/// The write shape matters: a tracked SaveChanges here emitted EF SaveChangesFailed ERR logs
/// when a transient database stall timed the write out, aborting the Scale500k25kGroups
/// integration run on tolerated best-effort bookkeeping. Opt-in via the same
/// <c>JIM_TEST_RESET_*</c> environment variables as the other <c>RequiresPostgres</c> fixtures.
/// </summary>
[TestFixture]
[Category("RequiresPostgres")]
public class ApiKeyUsageStampDatabaseTests
{
    private string _connectionString = null!;

    private JimDbContext NewContext()
    {
        var options = new DbContextOptionsBuilder<JimDbContext>()
            .UseNpgsql(_connectionString)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;
        return new JimDbContext(options);
    }

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        var dbName = Environment.GetEnvironmentVariable("JIM_TEST_RESET_DB");
        if (string.IsNullOrEmpty(dbName))
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL API key usage stamp tests.");

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

    private async Task<Guid> SeedApiKeyAsync(DateTime? lastUsedAt, string? lastUsedFromIp)
    {
        await using var ctx = NewContext();
        var apiKey = new ApiKey
        {
            Id = Guid.NewGuid(),
            Name = "Test Key",
            KeyPrefix = "jim_ak_test",
            KeyHash = "hash",
            IsEnabled = true,
            Created = DateTime.UtcNow,
            LastUsedAt = lastUsedAt,
            LastUsedFromIp = lastUsedFromIp
        };
        ctx.ApiKeys.Add(apiKey);
        await ctx.SaveChangesAsync();
        return apiKey.Id;
    }

    private async Task<ApiKey> GetApiKeyAsync(Guid id)
    {
        await using var ctx = NewContext();
        return await ctx.ApiKeys.AsNoTracking().SingleAsync(ak => ak.Id == id);
    }

    [Test]
    public async Task RecordUsageAsync_FirstUse_StampsLastUsedAtAndIp()
    {
        var id = await SeedApiKeyAsync(lastUsedAt: null, lastUsedFromIp: null);
        var before = DateTime.UtcNow;

        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);
        await repository.ApiKeys.RecordUsageAsync(id, "203.0.113.7");

        var stored = await GetApiKeyAsync(id);
        Assert.That(stored.LastUsedAt, Is.Not.Null);
        Assert.That(stored.LastUsedAt!.Value, Is.GreaterThanOrEqualTo(before.AddSeconds(-1)));
        Assert.That(stored.LastUsedFromIp, Is.EqualTo("203.0.113.7"));
    }

    [Test]
    public async Task RecordUsageAsync_WithinThrottleInterval_DoesNotRestamp()
    {
        var recentStamp = DateTime.UtcNow.AddSeconds(-1);
        var id = await SeedApiKeyAsync(lastUsedAt: recentStamp, lastUsedFromIp: "198.51.100.1");

        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);
        await repository.ApiKeys.RecordUsageAsync(id, "203.0.113.7");

        var stored = await GetApiKeyAsync(id);
        Assert.That(stored.LastUsedAt!.Value, Is.EqualTo(recentStamp).Within(TimeSpan.FromMilliseconds(1)),
            "a stamp within the throttle interval must be a no-op");
        Assert.That(stored.LastUsedFromIp, Is.EqualTo("198.51.100.1"));
    }

    [Test]
    public async Task RecordUsageAsync_AfterThrottleInterval_Restamps()
    {
        var staleStamp = DateTime.UtcNow - ApiKeyRepository.UsageStampInterval - TimeSpan.FromSeconds(5);
        var id = await SeedApiKeyAsync(lastUsedAt: staleStamp, lastUsedFromIp: "198.51.100.1");

        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);
        await repository.ApiKeys.RecordUsageAsync(id, "203.0.113.7");

        var stored = await GetApiKeyAsync(id);
        Assert.That(stored.LastUsedAt!.Value, Is.GreaterThan(staleStamp.AddSeconds(1)));
        Assert.That(stored.LastUsedFromIp, Is.EqualTo("203.0.113.7"));
    }

    [Test]
    public async Task RecordUsageAsync_UnknownKey_DoesNotThrow()
    {
        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);
        Assert.DoesNotThrowAsync(async () => await repository.ApiKeys.RecordUsageAsync(Guid.NewGuid(), "203.0.113.7"));
    }
}
