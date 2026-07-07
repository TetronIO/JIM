// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Core;
using Moq;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

[TestFixture]
public class SeedingServerInstanceSettingsTests
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
    public async Task SyncServiceSettings_FirstRun_SeedsServiceNameWithNullValueAsync()
    {
        _mockServiceSettingsRepo.Setup(r => r.SettingExistsAsync(It.IsAny<string>()))
            .ReturnsAsync(false);

        ServiceSetting? captured = null;
        _mockServiceSettingsRepo.Setup(r => r.CreateSettingAsync(It.Is<ServiceSetting>(s => s.Key == Constants.SettingKeys.ServiceName)))
            .Callback<ServiceSetting>(s => captured = s)
            .Returns(Task.CompletedTask);
        _mockServiceSettingsRepo.Setup(r => r.CreateSettingAsync(It.Is<ServiceSetting>(s => s.Key != Constants.SettingKeys.ServiceName)))
            .Returns(Task.CompletedTask);

        await _application.Seeding.SyncServiceSettingsAsync();

        Assert.That(captured, Is.Not.Null);
        Assert.That(captured!.Category, Is.EqualTo(ServiceSettingCategory.Instance));
        Assert.That(captured.ValueType, Is.EqualTo(ServiceSettingValueType.String));
        Assert.That(captured.DefaultValue, Is.Null);
        Assert.That(captured.Value, Is.Null);
        Assert.That(captured.IsReadOnly, Is.False);
    }

    [Test]
    public async Task SyncServiceSettings_FirstRun_GeneratesServiceIdGuidAsync()
    {
        _mockServiceSettingsRepo.Setup(r => r.SettingExistsAsync(It.IsAny<string>()))
            .ReturnsAsync(false);

        ServiceSetting? captured = null;
        _mockServiceSettingsRepo.Setup(r => r.CreateSettingAsync(It.Is<ServiceSetting>(s => s.Key == Constants.SettingKeys.ServiceId)))
            .Callback<ServiceSetting>(s => captured = s)
            .Returns(Task.CompletedTask);
        _mockServiceSettingsRepo.Setup(r => r.CreateSettingAsync(It.Is<ServiceSetting>(s => s.Key != Constants.SettingKeys.ServiceId)))
            .Returns(Task.CompletedTask);

        await _application.Seeding.SyncServiceSettingsAsync();

        Assert.That(captured, Is.Not.Null);
        Assert.That(captured!.Category, Is.EqualTo(ServiceSettingCategory.Instance));
        Assert.That(captured.ValueType, Is.EqualTo(ServiceSettingValueType.Guid));
        Assert.That(captured.IsReadOnly, Is.True);
        Assert.That(Guid.TryParse(captured.Value, out var parsed), Is.True,
            "Service ID value must be a valid GUID");
        Assert.That(parsed, Is.Not.EqualTo(Guid.Empty));
    }

    [Test]
    public async Task SyncServiceSettings_SecondRun_PreservesExistingServiceIdAsync()
    {
        _mockServiceSettingsRepo.Setup(r => r.SettingExistsAsync(It.IsAny<string>()))
            .ReturnsAsync(false);
        _mockServiceSettingsRepo.Setup(r => r.SettingExistsAsync(Constants.SettingKeys.ServiceId))
            .ReturnsAsync(true);
        _mockServiceSettingsRepo.Setup(r => r.CreateSettingAsync(It.IsAny<ServiceSetting>()))
            .Returns(Task.CompletedTask);
        _mockServiceSettingsRepo.Setup(r => r.UpdateSettingAsync(It.IsAny<ServiceSetting>()))
            .Returns(Task.CompletedTask);

        await _application.Seeding.SyncServiceSettingsAsync();

        _mockServiceSettingsRepo.Verify(
            r => r.CreateSettingAsync(It.Is<ServiceSetting>(s => s.Key == Constants.SettingKeys.ServiceId)),
            Times.Never,
            "Service ID must not be recreated when it already exists");

        _mockServiceSettingsRepo.Verify(
            r => r.UpdateSettingAsync(It.Is<ServiceSetting>(s => s.Key == Constants.SettingKeys.ServiceId)),
            Times.Never,
            "Service ID must not be updated when it already exists");
    }
}
