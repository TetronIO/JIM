// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Core;
using Moq;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// Tests the type-aware retention orchestration: the cleanup applies the general retention cutoff to sync/identity
/// Activities and a separate (typically much longer) cutoff to configuration-change Activities, reports both counts,
/// and the configuration retention period setting defaults to ten years with a zero-value guard.
/// </summary>
[TestFixture]
public class ChangeHistoryRetentionServerTests
{
    private Mock<IRepository> _repo = null!;
    private Mock<IActivityRepository> _activityRepo = null!;
    private Mock<IServiceSettingsRepository> _settingsRepo = null!;
    private Mock<IChangeHistoryRepository> _changeHistoryRepo = null!;
    private JimApplication _jim = null!;

    [SetUp]
    public void SetUp()
    {
        TestUtilities.SetEnvironmentVariables();

        _repo = new Mock<IRepository>();
        _activityRepo = new Mock<IActivityRepository>();
        _settingsRepo = new Mock<IServiceSettingsRepository>();
        _changeHistoryRepo = new Mock<IChangeHistoryRepository>();
        _repo.Setup(r => r.Activity).Returns(_activityRepo.Object);
        _repo.Setup(r => r.ServiceSettings).Returns(_settingsRepo.Object);
        _repo.Setup(r => r.ChangeHistory).Returns(_changeHistoryRepo.Object);

        _activityRepo.Setup(r => r.CreateActivityAsync(It.IsAny<Activity>())).Returns(Task.CompletedTask);
        _activityRepo.Setup(r => r.UpdateActivityAsync(It.IsAny<Activity>())).Returns(Task.CompletedTask);

        _jim = new JimApplication(_repo.Object);
    }

    [TearDown]
    public void TearDown() => _jim?.Dispose();

    [Test]
    public async Task DeleteExpiredChangeHistoryAsync_AppliesSeparateConfigurationCutoffAndReportsCountsAsync()
    {
        var generalCutoff = DateTime.UtcNow.AddDays(-90);
        var configurationCutoff = DateTime.UtcNow.AddDays(-3650);
        var securityCutoff = DateTime.UtcNow.AddDays(-365);
        _changeHistoryRepo.Setup(r => r.DeleteExpiredCsoChangesAsync(generalCutoff, 100)).ReturnsAsync(5);
        _changeHistoryRepo.Setup(r => r.DeleteExpiredMvoChangesAsync(generalCutoff, 100)).ReturnsAsync(4);
        _changeHistoryRepo.Setup(r => r.DeleteExpiredActivitiesAsync(generalCutoff, 100)).ReturnsAsync(3);
        _changeHistoryRepo.Setup(r => r.DeleteExpiredConfigurationChangeActivitiesAsync(configurationCutoff, 100)).ReturnsAsync(2);
        _changeHistoryRepo.Setup(r => r.DeleteExpiredSecurityEventActivitiesAsync(securityCutoff, 100)).ReturnsAsync(7);

        var result = await _jim.ChangeHistory.DeleteExpiredChangeHistoryAsync(generalCutoff, configurationCutoff, securityCutoff, 100);

        Assert.That(result.ActivitiesDeleted, Is.EqualTo(3));
        Assert.That(result.ConfigurationChangeActivitiesDeleted, Is.EqualTo(2));
        Assert.That(result.SecurityEventActivitiesDeleted, Is.EqualTo(7));
        _changeHistoryRepo.Verify(r => r.DeleteExpiredActivitiesAsync(generalCutoff, 100), Times.Once,
            "general Activities are flushed at the general retention cutoff");
        _changeHistoryRepo.Verify(r => r.DeleteExpiredConfigurationChangeActivitiesAsync(configurationCutoff, 100), Times.Once,
            "configuration-change Activities are flushed only at their own, longer cutoff");
        _changeHistoryRepo.Verify(r => r.DeleteExpiredSecurityEventActivitiesAsync(securityCutoff, 100), Times.Once,
            "security event Activities are flushed only at their own, dedicated cutoff");
    }

    [Test]
    public async Task GetSecurityEventRetentionPeriodAsync_NoSettingStored_DefaultsToOneYearAsync()
    {
        _settingsRepo.Setup(r => r.GetSettingAsync(Constants.SettingKeys.SecurityEventRetentionPeriod))
            .ReturnsAsync((ServiceSetting?)null);

        var period = await _jim.ServiceSettings.GetSecurityEventRetentionPeriodAsync();

        Assert.That(period, Is.EqualTo(TimeSpan.FromDays(365)));
    }

    [Test]
    public async Task GetSecurityEventRetentionPeriodAsync_ZeroConfigured_FallsBackToDefaultAsync()
    {
        _settingsRepo.Setup(r => r.GetSettingAsync(Constants.SettingKeys.SecurityEventRetentionPeriod))
            .ReturnsAsync(new ServiceSetting
            {
                Key = Constants.SettingKeys.SecurityEventRetentionPeriod,
                DisplayName = "Security event retention period",
                ValueType = ServiceSettingValueType.TimeSpan,
                Value = "00:00:00"
            });

        var period = await _jim.ServiceSettings.GetSecurityEventRetentionPeriodAsync();

        Assert.That(period, Is.EqualTo(TimeSpan.FromDays(365)),
            "a zero or negative retention period would delete all security event history and must be rejected");
    }

    [Test]
    public async Task GetConfigurationChangeRetentionPeriodAsync_NoSettingStored_DefaultsToTenYearsAsync()
    {
        _settingsRepo.Setup(r => r.GetSettingAsync(Constants.SettingKeys.ConfigurationChangeRetentionPeriod))
            .ReturnsAsync((ServiceSetting?)null);

        var period = await _jim.ServiceSettings.GetConfigurationChangeRetentionPeriodAsync();

        Assert.That(period, Is.EqualTo(TimeSpan.FromDays(3650)));
    }

    [Test]
    public async Task GetConfigurationChangeRetentionPeriodAsync_ZeroConfigured_FallsBackToDefaultAsync()
    {
        _settingsRepo.Setup(r => r.GetSettingAsync(Constants.SettingKeys.ConfigurationChangeRetentionPeriod))
            .ReturnsAsync(new ServiceSetting
            {
                Key = Constants.SettingKeys.ConfigurationChangeRetentionPeriod,
                DisplayName = "Configuration change retention period",
                ValueType = ServiceSettingValueType.TimeSpan,
                Value = "00:00:00"
            });

        var period = await _jim.ServiceSettings.GetConfigurationChangeRetentionPeriodAsync();

        Assert.That(period, Is.EqualTo(TimeSpan.FromDays(3650)),
            "a zero or negative retention period would delete all configuration history and must be rejected");
    }
}
