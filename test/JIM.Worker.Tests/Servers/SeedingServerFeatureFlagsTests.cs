// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Core;
using JIM.TestSupport;
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
    private IDisposable _catalogue = null!;

    [SetUp]
    public void SetUp()
    {
        // The real catalogue declares no flag while none is needed; seed against a synthetic one so the creation and
        // keep paths are exercised, not merely vacuous.
        _catalogue = FeatureFlagCatalogueScope.Use(FeatureFlagCatalogueScope.InDevelopmentFlag);
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
    public void TearDown()
    {
        _application?.Dispose();
        _catalogue?.Dispose();
    }

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
    public async Task SyncServiceSettings_CatalogueFlag_SeededFromItsDefinitionAsync()
    {
        var flag = FeatureFlagCatalogueScope.InDevelopmentFlag;
        ServiceSetting? captured = null;
        _mockServiceSettingsRepo.Setup(r => r.CreateSettingAsync(It.Is<ServiceSetting>(s => s.Key == flag.Key)))
            .Callback<ServiceSetting>(s => captured = s)
            .Returns(Task.CompletedTask);

        await _application.Seeding.SyncServiceSettingsAsync();

        Assert.That(captured, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(captured!.DisplayName, Is.EqualTo(flag.DisplayName));
            Assert.That(captured!.Description, Is.EqualTo(flag.Description));
            Assert.That(captured!.Category, Is.EqualTo(ServiceSettingCategory.FeatureFlags));
            Assert.That(captured!.DefaultValue, Is.EqualTo("false"), "every flag defaults off");
        }
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
                new() { Key = FeatureFlagCatalogueScope.InDevelopmentFlag.Key, DisplayName = FeatureFlagCatalogueScope.InDevelopmentFlag.DisplayName, Category = ServiceSettingCategory.FeatureFlags, ValueType = ServiceSettingValueType.Boolean }
            });

        await _application.Seeding.SyncServiceSettingsAsync();

        _mockServiceSettingsRepo.Verify(r => r.DeleteSettingAsync(It.IsAny<string>()), Times.Never);
    }

    [Test]
    public async Task SyncServiceSettings_ShippedFlagsRetiredFromTheCatalogue_ArePrunedAsync()
    {
        // Unique Value Generation (#1803) and Metaverse-Derived Attribute Flows (#1878) shipped and their flags were
        // deleted from the real catalogue; a deployment that ran with them still holds their rows, which the next
        // seeding pass must remove. Measured against the real catalogue, so the synthetic one is set aside.
        _catalogue.Dispose();
        string[] retiredKeys = ["Features.UniqueValueGeneration", "Features.MetaverseDerivedAttributeFlows"];
        _mockServiceSettingsRepo.Setup(r => r.GetSettingsByCategoryAsync(ServiceSettingCategory.FeatureFlags))
            .ReturnsAsync(retiredKeys.Select(key => new ServiceSetting
            {
                Key = key,
                DisplayName = key,
                Category = ServiceSettingCategory.FeatureFlags,
                ValueType = ServiceSettingValueType.Boolean,
                DefaultValue = "false",
                Value = "true"
            }).ToList());

        await _application.Seeding.SyncServiceSettingsAsync();

        foreach (var key in retiredKeys)
            _mockServiceSettingsRepo.Verify(r => r.DeleteSettingAsync(key), Times.Once, $"{key} is no longer catalogued and must be pruned");
    }
}
