// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Security;
using JIM.PostgresData;
using JIM.PostgresData.Repositories;
using Microsoft.EntityFrameworkCore;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// Behavioural tests for the API key usage stamp (<c>RecordUsageAsync</c>) via the EF in-memory
/// provider (the non-relational fallback path). The stamp is best-effort bookkeeping written on
/// every authenticated API request; it must throttle to at most one write per
/// <see cref="ApiKeyRepository.UsageStampInterval"/> per key, so API polling during a large
/// synchronisation run cannot form a hot-row convoy on the ApiKeys table (the Scale500k25kGroups
/// validation run of 2026-07-21 aborted when three queued usage writes exceeded the command
/// timeout during an import write burst; see #1078 validation notes).
/// </summary>
[TestFixture]
public class ApiKeyUsageStampTests
{
    private JimDbContext _dbContext = null!;
    private PostgresDataRepository _repository = null!;

    [SetUp]
    public void Setup()
    {
        TestUtilities.SetEnvironmentVariables();
        var options = new DbContextOptionsBuilder<JimDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _dbContext = new JimDbContext(options);
        _repository = new PostgresDataRepository(_dbContext);
    }

    [TearDown]
    public void TearDown()
    {
        (_repository as IDisposable)?.Dispose();
        _dbContext?.Dispose();
    }

    private async Task<ApiKey> SeedApiKeyAsync(DateTime? lastUsedAt = null, string? lastUsedFromIp = null)
    {
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
        _dbContext.ApiKeys.Add(apiKey);
        await _dbContext.SaveChangesAsync();
        return apiKey;
    }

    [Test]
    public async Task RecordUsageAsync_FirstUse_StampsLastUsedAtAndIp()
    {
        var apiKey = await SeedApiKeyAsync();
        var before = DateTime.UtcNow;

        await _repository.ApiKeys.RecordUsageAsync(apiKey.Id, "203.0.113.7");

        var stored = await _dbContext.ApiKeys.SingleAsync(ak => ak.Id == apiKey.Id);
        Assert.That(stored.LastUsedAt, Is.Not.Null);
        Assert.That(stored.LastUsedAt!.Value, Is.GreaterThanOrEqualTo(before));
        Assert.That(stored.LastUsedFromIp, Is.EqualTo("203.0.113.7"));
    }

    [Test]
    public async Task RecordUsageAsync_WithinThrottleInterval_DoesNotRestamp()
    {
        var recentStamp = DateTime.UtcNow.AddSeconds(-1);
        var apiKey = await SeedApiKeyAsync(lastUsedAt: recentStamp, lastUsedFromIp: "198.51.100.1");

        await _repository.ApiKeys.RecordUsageAsync(apiKey.Id, "203.0.113.7");

        var stored = await _dbContext.ApiKeys.SingleAsync(ak => ak.Id == apiKey.Id);
        Assert.That(stored.LastUsedAt, Is.EqualTo(recentStamp), "a stamp within the throttle interval must be a no-op");
        Assert.That(stored.LastUsedFromIp, Is.EqualTo("198.51.100.1"));
    }

    [Test]
    public async Task RecordUsageAsync_AfterThrottleInterval_Restamps()
    {
        var staleStamp = DateTime.UtcNow - ApiKeyRepository.UsageStampInterval - TimeSpan.FromSeconds(5);
        var apiKey = await SeedApiKeyAsync(lastUsedAt: staleStamp, lastUsedFromIp: "198.51.100.1");

        await _repository.ApiKeys.RecordUsageAsync(apiKey.Id, "203.0.113.7");

        var stored = await _dbContext.ApiKeys.SingleAsync(ak => ak.Id == apiKey.Id);
        Assert.That(stored.LastUsedAt, Is.GreaterThan(staleStamp));
        Assert.That(stored.LastUsedFromIp, Is.EqualTo("203.0.113.7"));
    }

    [Test]
    public void RecordUsageAsync_UnknownKey_DoesNotThrow()
    {
        Assert.DoesNotThrowAsync(async () => await _repository.ApiKeys.RecordUsageAsync(Guid.NewGuid(), "203.0.113.7"));
    }
}
