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
/// Tests that <see cref="JIM.Application.Servers.SeedingServer.SyncServiceSettingsAsync"/> seeds one Service
/// Setting row per <see cref="FeatureFlagCatalogue"/> entry (#1781), and prunes any
/// <see cref="ServiceSettingCategory.FeatureFlags"/> row whose key is no longer in the catalogue. Mirrors
/// <see cref="SeedingServerRateLimitingSettingsTests"/>'s mock pattern.
/// </summary>
[TestFixture]
public class SeedingServerFeatureFlagsTests
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
        _mockServiceSettingsRepo.Setup(r => r.DeleteSettingAsync(It.IsAny<string>())).Returns(Task.CompletedTask);
        _mockServiceSettingsRepo.Setup(r => r.GetSettingsByCategoryAsync(ServiceSettingCategory.FeatureFlags))
            .ReturnsAsync(new List<ServiceSetting>());
        // SyncServiceSettingsAsync diffs the setting keys before and after its loop to baseline newly-created settings;
        // these tests do not exercise that path, so return an empty set (no baselines recorded).
        _mockServiceSettingsRepo.Setup(r => r.GetAllSettingsAsync()).ReturnsAsync(new List<ServiceSetting>());
        _application = new JimApplication(_mockRepository.Object);
    }

    [TearDown]
    public void TearDown() => _application?.Dispose();

    [Test]
    public async Task SyncServiceSettings_FirstRun_SeedsEveryCatalogueFlagAsBooleanDefaultFalseAsync()
    {
        var captured = new List<ServiceSetting>();
        _mockServiceSettingsRepo.Setup(r => r.CreateSettingAsync(It.Is<ServiceSetting>(s => s.Category == ServiceSettingCategory.FeatureFlags)))
            .Callback<ServiceSetting>(s => captured.Add(s))
            .Returns(Task.CompletedTask);

        await _application.Seeding.SyncServiceSettingsAsync();

        Assert.That(captured.Select(s => s.Key), Is.EquivalentTo(FeatureFlagCatalogue.All.Select(f => f.Key)));
        using (Assert.EnterMultipleScope())
        {
            foreach (var setting in captured)
            {
                Assert.That(setting.ValueType, Is.EqualTo(ServiceSettingValueType.Boolean), $"{setting.Key} must be a Boolean setting");
                Assert.That(setting.DefaultValue, Is.EqualTo("false"), $"{setting.Key} must default off");
                Assert.That(setting.IsReadOnly, Is.False, $"{setting.Key} must not be read-only; it changes through the feature-flag surfaces");
            }
        }
    }

    [Test]
    public async Task SyncServiceSettings_UniqueValueGeneration_SeededAsAgreedAsync()
    {
        ServiceSetting? captured = null;
        _mockServiceSettingsRepo.Setup(r => r.CreateSettingAsync(It.Is<ServiceSetting>(s => s.Key == FeatureFlagCatalogue.UniqueValueGeneration.Key)))
            .Callback<ServiceSetting>(s => captured = s)
            .Returns(Task.CompletedTask);

        await _application.Seeding.SyncServiceSettingsAsync();

        Assert.That(captured, Is.Not.Null);
        Assert.That(captured!.DisplayName, Is.EqualTo("Unique Value Generation"));
        Assert.That(captured!.Category, Is.EqualTo(ServiceSettingCategory.FeatureFlags));
    }

    [Test]
    public async Task SyncServiceSettings_ExistingFlagSettings_AreNotRecreatedAsync()
    {
        foreach (var flag in FeatureFlagCatalogue.All)
            _mockServiceSettingsRepo.Setup(r => r.SettingExistsAsync(flag.Key)).ReturnsAsync(true);
        // CreateOrUpdateSettingAsync only updates existing settings when they are IsReadOnly; flags are not, so no
        // GetSettingAsync/UpdateSettingAsync call is expected for them either.
        _mockServiceSettingsRepo.Setup(r => r.GetSettingAsync(It.IsAny<string>())).ReturnsAsync((ServiceSetting?)null);

        await _application.Seeding.SyncServiceSettingsAsync();

        foreach (var flag in FeatureFlagCatalogue.All)
        {
            _mockServiceSettingsRepo.Verify(
                r => r.CreateSettingAsync(It.Is<ServiceSetting>(s => s.Key == flag.Key)), Times.Never,
                $"{flag.Key} already exists and must not be recreated (which would clobber an administrator's toggle)");
        }
    }

    [Test]
    public async Task SyncServiceSettings_RemovesFeatureFlagRowsNoLongerInTheCatalogueAsync()
    {
        var staleKey = "Features.RemovedFlag";
        _mockServiceSettingsRepo.Setup(r => r.GetSettingsByCategoryAsync(ServiceSettingCategory.FeatureFlags))
            .ReturnsAsync(new List<ServiceSetting>
            {
                new() { Key = staleKey, DisplayName = "Removed Flag", Category = ServiceSettingCategory.FeatureFlags, ValueType = ServiceSettingValueType.Boolean }
            });

        await _application.Seeding.SyncServiceSettingsAsync();

        _mockServiceSettingsRepo.Verify(r => r.DeleteSettingAsync(staleKey), Times.Once);
    }

    [Test]
    public async Task SyncServiceSettings_DoesNotRemoveFlagsStillInTheCatalogueAsync()
    {
        _mockServiceSettingsRepo.Setup(r => r.GetSettingsByCategoryAsync(ServiceSettingCategory.FeatureFlags))
            .ReturnsAsync(new List<ServiceSetting>
            {
                new() { Key = FeatureFlagCatalogue.UniqueValueGeneration.Key, DisplayName = "Unique Value Generation", Category = ServiceSettingCategory.FeatureFlags, ValueType = ServiceSettingValueType.Boolean }
            });

        await _application.Seeding.SyncServiceSettingsAsync();

        _mockServiceSettingsRepo.Verify(r => r.DeleteSettingAsync(It.IsAny<string>()), Times.Never);
    }
}
