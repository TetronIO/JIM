// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Threading.Tasks;
using JIM.Application;
using JIM.Application.Interfaces;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Core;
using JIM.Web.Middleware.Api;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;

namespace JIM.Web.Api.Tests;

/// <summary>
/// Tests for <see cref="RateLimitSettingsCache"/>: the short-TTL cache that keeps JIM's REST API rate limiter
/// (which runs on every request) off the database hot path, while still picking up a Service Settings change
/// within a bounded delay. Covers TTL behaviour and the pre-seeding/database-failure fallback to defaults.
/// </summary>
[TestFixture]
public class RateLimitSettingsCacheTests
{
    private Mock<IRepository> _mockRepository = null!;
    private Mock<IServiceSettingsRepository> _mockSettingsRepo = null!;
    private Mock<IJimApplicationFactory> _mockFactory = null!;
    private int _createCallCount;

    [SetUp]
    public void SetUp()
    {
        _mockRepository = new Mock<IRepository>();
        _mockSettingsRepo = new Mock<IServiceSettingsRepository>();
        _mockRepository.Setup(r => r.ServiceSettings).Returns(_mockSettingsRepo.Object);

        _createCallCount = 0;
        _mockFactory = new Mock<IJimApplicationFactory>();
        _mockFactory.Setup(f => f.Create()).Returns(() =>
        {
            _createCallCount++;
            return new JimApplication(_mockRepository.Object);
        });
    }

    [Test]
    public async Task GetSnapshotAsync_SettingsNotYetSeeded_FallsBackToCompiledInDefaultsAsync()
    {
        // GetSettingAsync returning null for every key mirrors a fresh database before SeedingServer has run.
        _mockSettingsRepo.Setup(r => r.GetSettingAsync(It.IsAny<string>())).ReturnsAsync((ServiceSetting?)null);
        var cache = new RateLimitSettingsCache(_mockFactory.Object, NullLogger());

        var snapshot = await cache.GetSnapshotAsync();

        Assert.That(snapshot.Enabled, Is.EqualTo(Constants.RateLimitDefaults.Enabled));
        Assert.That(snapshot.AuthenticatedRequestsPerMinute, Is.EqualTo(Constants.RateLimitDefaults.AuthenticatedRequestsPerMinute));
        Assert.That(snapshot.UnauthenticatedRequestsPerMinute, Is.EqualTo(Constants.RateLimitDefaults.UnauthenticatedRequestsPerMinute));
    }

    [Test]
    public async Task GetSnapshotAsync_SettingsPersisted_ReturnsPersistedValuesAsync()
    {
        SetupSetting(Constants.SettingKeys.RateLimitingEnabled, ServiceSettingValueType.Boolean, "false");
        SetupSetting(Constants.SettingKeys.RateLimitingAuthenticatedRequestsPerMinute, ServiceSettingValueType.Integer, "150");
        SetupSetting(Constants.SettingKeys.RateLimitingUnauthenticatedRequestsPerMinute, ServiceSettingValueType.Integer, "15");
        var cache = new RateLimitSettingsCache(_mockFactory.Object, NullLogger());

        var snapshot = await cache.GetSnapshotAsync();

        Assert.That(snapshot.Enabled, Is.False);
        Assert.That(snapshot.AuthenticatedRequestsPerMinute, Is.EqualTo(150));
        Assert.That(snapshot.UnauthenticatedRequestsPerMinute, Is.EqualTo(15));
    }

    [Test]
    public async Task GetSnapshotAsync_CalledTwiceWithinTtl_ReadsTheDatabaseOnlyOnceAsync()
    {
        _mockSettingsRepo.Setup(r => r.GetSettingAsync(It.IsAny<string>())).ReturnsAsync((ServiceSetting?)null);
        var cache = new RateLimitSettingsCache(_mockFactory.Object, NullLogger()) { CacheDuration = TimeSpan.FromMinutes(1) };

        await cache.GetSnapshotAsync();
        await cache.GetSnapshotAsync();

        Assert.That(_createCallCount, Is.EqualTo(1), "a second call within the TTL must be served from cache, not the database");
    }

    [Test]
    public async Task GetSnapshotAsync_CalledAfterTtlExpires_RefreshesFromDatabaseAsync()
    {
        _mockSettingsRepo.Setup(r => r.GetSettingAsync(It.IsAny<string>())).ReturnsAsync((ServiceSetting?)null);
        var cache = new RateLimitSettingsCache(_mockFactory.Object, NullLogger()) { CacheDuration = TimeSpan.FromMilliseconds(20) };

        await cache.GetSnapshotAsync();
        await Task.Delay(60);
        await cache.GetSnapshotAsync();

        Assert.That(_createCallCount, Is.EqualTo(2), "a call after the TTL has elapsed must refresh from the database");
    }

    [Test]
    public async Task GetSnapshotAsync_DatabaseThrows_FallsBackToLastKnownValuesInsteadOfThrowingAsync()
    {
        _mockFactory.Setup(f => f.Create()).Throws(new InvalidOperationException("database unavailable"));
        var cache = new RateLimitSettingsCache(_mockFactory.Object, NullLogger());

        var snapshot = await cache.GetSnapshotAsync();

        // First-ever read failed, so the "last known" value is the compiled-in Defaults.
        Assert.That(snapshot.Enabled, Is.EqualTo(Constants.RateLimitDefaults.Enabled));
        Assert.That(snapshot.AuthenticatedRequestsPerMinute, Is.EqualTo(Constants.RateLimitDefaults.AuthenticatedRequestsPerMinute));
        Assert.That(snapshot.UnauthenticatedRequestsPerMinute, Is.EqualTo(Constants.RateLimitDefaults.UnauthenticatedRequestsPerMinute));
    }

    [Test]
    public async Task GetSnapshotAsync_DatabaseThrowsAfterAGoodRead_KeepsServingThePreviousGoodValuesAsync()
    {
        SetupSetting(Constants.SettingKeys.RateLimitingEnabled, ServiceSettingValueType.Boolean, "true");
        SetupSetting(Constants.SettingKeys.RateLimitingAuthenticatedRequestsPerMinute, ServiceSettingValueType.Integer, "42");
        SetupSetting(Constants.SettingKeys.RateLimitingUnauthenticatedRequestsPerMinute, ServiceSettingValueType.Integer, "7");
        var cache = new RateLimitSettingsCache(_mockFactory.Object, NullLogger()) { CacheDuration = TimeSpan.FromMilliseconds(20) };
        await cache.GetSnapshotAsync();

        // The database now fails on the next refresh.
        _mockFactory.Setup(f => f.Create()).Throws(new InvalidOperationException("database unavailable"));
        await Task.Delay(60);
        var snapshot = await cache.GetSnapshotAsync();

        Assert.That(snapshot.AuthenticatedRequestsPerMinute, Is.EqualTo(42), "a failed refresh must keep serving the last successfully-read value, not the compiled-in default");
        Assert.That(snapshot.UnauthenticatedRequestsPerMinute, Is.EqualTo(7));
    }

    // -- helpers -------------------------------------------------------------------------------------------------------

    private void SetupSetting(string key, ServiceSettingValueType valueType, string value) =>
        _mockSettingsRepo.Setup(r => r.GetSettingAsync(key)).ReturnsAsync(new ServiceSetting
        {
            Key = key,
            DisplayName = key,
            ValueType = valueType,
            Value = value
        });

    private static ILogger<RateLimitSettingsCache> NullLogger() =>
        Mock.Of<ILogger<RateLimitSettingsCache>>();
}
