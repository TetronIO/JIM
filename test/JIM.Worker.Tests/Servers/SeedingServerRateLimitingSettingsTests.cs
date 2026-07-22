// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Core;
using Moq;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// Tests that the three built-in API rate limiting Service Settings (issue #500, OWASP Top 10:2025 A02) are
/// seeded with the expected key, category, type, and default on first run. Mirrors
/// <see cref="SeedingServerInstanceSettingsTests"/>'s mock pattern.
/// </summary>
[TestFixture]
public class SeedingServerRateLimitingSettingsTests
{
    private Mock<IRepository> _mockRepository = null!;
    private Mock<IServiceSettingsRepository> _mockServiceSettingsRepo = null!;
    private JimApplication _application = null!;

    [SetUp]
    public void SetUp()
    {
        _mockRepository = new Mock<IRepository>();
        _mockServiceSettingsRepo = new Mock<IServiceSettingsRepository>();
        _mockRepository.Setup(r => r.ServiceSettings).Returns(_mockServiceSettingsRepo.Object);
        _mockServiceSettingsRepo.Setup(r => r.SettingExistsAsync(It.IsAny<string>())).ReturnsAsync(false);
        _mockServiceSettingsRepo.Setup(r => r.CreateSettingAsync(It.IsAny<ServiceSetting>())).Returns(Task.CompletedTask);
        // SyncServiceSettingsAsync diffs the setting keys before and after its loop to baseline newly-created settings;
        // these tests do not exercise that path, so return an empty set (no baselines recorded).
        _mockServiceSettingsRepo.Setup(r => r.GetAllSettingsAsync()).ReturnsAsync(new List<ServiceSetting>());
        _application = new JimApplication(_mockRepository.Object);
    }

    [TearDown]
    public void TearDown()
    {
        _application?.Dispose();
    }

    [Test]
    public async Task SyncServiceSettings_FirstRun_SeedsRateLimitingEnabledDefaultTrueAsync()
    {
        var captured = await CaptureSeededSettingAsync(Constants.SettingKeys.RateLimitingEnabled);

        Assert.That(captured, Is.Not.Null);
        Assert.That(captured!.Category, Is.EqualTo(ServiceSettingCategory.Security));
        Assert.That(captured!.ValueType, Is.EqualTo(ServiceSettingValueType.Boolean));
        Assert.That(captured!.DefaultValue, Is.EqualTo("true"));
        Assert.That(captured!.IsReadOnly, Is.False);
    }

    [Test]
    public async Task SyncServiceSettings_FirstRun_SeedsAuthenticatedRequestsPerMinuteDefault300Async()
    {
        var captured = await CaptureSeededSettingAsync(Constants.SettingKeys.RateLimitingAuthenticatedRequestsPerMinute);

        Assert.That(captured, Is.Not.Null);
        Assert.That(captured!.Category, Is.EqualTo(ServiceSettingCategory.Security));
        Assert.That(captured!.ValueType, Is.EqualTo(ServiceSettingValueType.Integer));
        Assert.That(captured!.DefaultValue, Is.EqualTo("300"));
        Assert.That(captured!.IsReadOnly, Is.False);
    }

    [Test]
    public async Task SyncServiceSettings_FirstRun_SeedsUnauthenticatedRequestsPerMinuteDefault30Async()
    {
        var captured = await CaptureSeededSettingAsync(Constants.SettingKeys.RateLimitingUnauthenticatedRequestsPerMinute);

        Assert.That(captured, Is.Not.Null);
        Assert.That(captured!.Category, Is.EqualTo(ServiceSettingCategory.Security));
        Assert.That(captured!.ValueType, Is.EqualTo(ServiceSettingValueType.Integer));
        Assert.That(captured!.DefaultValue, Is.EqualTo("30"));
        Assert.That(captured!.IsReadOnly, Is.False);
    }

    [Test]
    public async Task SyncServiceSettings_RateLimitingSettingsAlreadyExist_AreNotRecreatedAsync()
    {
        _mockServiceSettingsRepo.Setup(r => r.SettingExistsAsync(Constants.SettingKeys.RateLimitingEnabled)).ReturnsAsync(true);
        _mockServiceSettingsRepo.Setup(r => r.SettingExistsAsync(Constants.SettingKeys.RateLimitingAuthenticatedRequestsPerMinute)).ReturnsAsync(true);
        _mockServiceSettingsRepo.Setup(r => r.SettingExistsAsync(Constants.SettingKeys.RateLimitingUnauthenticatedRequestsPerMinute)).ReturnsAsync(true);
        // CreateOrUpdateSettingAsync only updates existing settings when they are IsReadOnly; these are not, so no
        // GetSettingAsync/UpdateSettingAsync call is expected for them either.
        _mockServiceSettingsRepo.Setup(r => r.GetSettingAsync(It.IsAny<string>())).ReturnsAsync((ServiceSetting?)null);

        await _application.Seeding.SyncServiceSettingsAsync();

        _mockServiceSettingsRepo.Verify(
            r => r.CreateSettingAsync(It.Is<ServiceSetting>(s => s.Key == Constants.SettingKeys.RateLimitingEnabled)),
            Times.Never);
        _mockServiceSettingsRepo.Verify(
            r => r.CreateSettingAsync(It.Is<ServiceSetting>(s => s.Key == Constants.SettingKeys.RateLimitingAuthenticatedRequestsPerMinute)),
            Times.Never);
        _mockServiceSettingsRepo.Verify(
            r => r.CreateSettingAsync(It.Is<ServiceSetting>(s => s.Key == Constants.SettingKeys.RateLimitingUnauthenticatedRequestsPerMinute)),
            Times.Never);
    }

    // -- helpers -------------------------------------------------------------------------------------------------------

    private async Task<ServiceSetting?> CaptureSeededSettingAsync(string key)
    {
        ServiceSetting? captured = null;
        _mockServiceSettingsRepo.Setup(r => r.CreateSettingAsync(It.Is<ServiceSetting>(s => s.Key == key)))
            .Callback<ServiceSetting>(s => captured = s)
            .Returns(Task.CompletedTask);

        await _application.Seeding.SyncServiceSettingsAsync();

        return captured;
    }
}
