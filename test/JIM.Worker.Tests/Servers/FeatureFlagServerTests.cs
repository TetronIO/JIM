// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Application.Exceptions;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Security;
using JIM.TestSupport;
using Moq;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// Tests <see cref="FeatureFlagServer"/> (#1781): reading a flag's state, the tier gate on enabling an In
/// Development flag, the not-found behaviour for an unknown key, and that a flag change persists through the
/// same Service Setting update path (and therefore the same audit Activity) as an ordinary setting.
/// </summary>
[TestFixture]
public class FeatureFlagServerTests
{
    // -- reads, against the shared in-memory fake --------------------------------------------------------------

    [Test]
    public async Task IsEnabledAsync_WhenFlagOn_ReturnsTrueAsync()
    {
        var repo = new Mock<IRepository>();
        repo.Setup(r => r.ServiceSettings).Returns(InMemoryServiceSettingsRepository.WithAllFeatureFlagsEnabled());
        using var jim = new JimApplication(repo.Object);

        var enabled = await jim.FeatureFlags.IsEnabledAsync(FeatureFlagCatalogue.UniqueValueGeneration.Key);

        Assert.That(enabled, Is.True);
    }

    [Test]
    public async Task IsEnabledAsync_WhenFlagRowMissing_DefaultsFalseAsync()
    {
        var repo = new Mock<IRepository>();
        repo.Setup(r => r.ServiceSettings).Returns(new InMemoryServiceSettingsRepository());
        using var jim = new JimApplication(repo.Object);

        var enabled = await jim.FeatureFlags.IsEnabledAsync(FeatureFlagCatalogue.UniqueValueGeneration.Key);

        Assert.That(enabled, Is.False, "an unseeded flag (e.g. before the seeding pass runs) must default off, never throw");
    }

    [Test]
    public void IsEnabledAsync_UnknownKey_ThrowsNotFound()
    {
        var repo = new Mock<IRepository>();
        repo.Setup(r => r.ServiceSettings).Returns(new InMemoryServiceSettingsRepository());
        using var jim = new JimApplication(repo.Object);

        Assert.That(async () => await jim.FeatureFlags.IsEnabledAsync("Features.DoesNotExist"),
            Throws.TypeOf<FeatureFlagNotFoundException>());
    }

    [Test]
    public async Task EnsureEnabledAsync_WhenEnabled_DoesNotThrowAsync()
    {
        var repo = new Mock<IRepository>();
        repo.Setup(r => r.ServiceSettings).Returns(InMemoryServiceSettingsRepository.WithAllFeatureFlagsEnabled());
        using var jim = new JimApplication(repo.Object);

        Assert.That(async () => await jim.FeatureFlags.EnsureEnabledAsync(FeatureFlagCatalogue.UniqueValueGeneration.Key), Throws.Nothing);
    }

    [Test]
    public void EnsureEnabledAsync_WhenDisabled_ThrowsFeatureDisabledNamingTheFeature()
    {
        var repo = new Mock<IRepository>();
        repo.Setup(r => r.ServiceSettings).Returns(new InMemoryServiceSettingsRepository());
        using var jim = new JimApplication(repo.Object);

        var ex = Assert.ThrowsAsync<FeatureDisabledException>(async () =>
            await jim.FeatureFlags.EnsureEnabledAsync(FeatureFlagCatalogue.UniqueValueGeneration.Key));

        Assert.That(ex!.Definition.Key, Is.EqualTo(FeatureFlagCatalogue.UniqueValueGeneration.Key));
        Assert.That(ex.Message, Does.Contain(FeatureFlagCatalogue.UniqueValueGeneration.DisplayName));
    }

    [Test]
    public async Task GetFeatureFlagsAsync_ExcludingInDevelopment_OmitsInDevelopmentFlagsAsync()
    {
        var repo = new Mock<IRepository>();
        repo.Setup(r => r.ServiceSettings).Returns(InMemoryServiceSettingsRepository.WithAllFeatureFlagsEnabled());
        using var jim = new JimApplication(repo.Object);

        var flags = await jim.FeatureFlags.GetFeatureFlagsAsync(includeInDevelopment: false);

        Assert.That(flags.Select(f => f.Definition.Key), Does.Not.Contain(FeatureFlagCatalogue.UniqueValueGeneration.Key),
            "an In Development flag must never be returned unless explicitly requested");
    }

    [Test]
    public async Task GetFeatureFlagsAsync_IncludingInDevelopment_ReturnsEveryCatalogueEntryAsync()
    {
        var repo = new Mock<IRepository>();
        repo.Setup(r => r.ServiceSettings).Returns(InMemoryServiceSettingsRepository.WithAllFeatureFlagsEnabled());
        using var jim = new JimApplication(repo.Object);

        var flags = await jim.FeatureFlags.GetFeatureFlagsAsync(includeInDevelopment: true);

        Assert.That(flags.Select(f => f.Definition.Key), Is.EquivalentTo(FeatureFlagCatalogue.All.Select(f => f.Key)));
        Assert.That(flags, Has.All.Matches<FeatureFlagState>(f => f.Enabled), "the shared in-memory fake seeds every flag enabled");
    }

    // -- writes, against a fully mocked repository so persistence/audit are observable --------------------------

    private Mock<IRepository> _repo = null!;
    private Mock<IActivityRepository> _activityRepo = null!;
    private Mock<IServiceSettingsRepository> _settingsRepo = null!;
    private JimApplication _jim = null!;
    private Activity? _completedActivity;
    private readonly Dictionary<string, ServiceSetting> _persisted = new();

    [SetUp]
    public void SetUp()
    {
        TestUtilities.SetEnvironmentVariables();

        _repo = new Mock<IRepository>();
        _activityRepo = new Mock<IActivityRepository>();
        _settingsRepo = new Mock<IServiceSettingsRepository>();
        _repo.Setup(r => r.Activity).Returns(_activityRepo.Object);
        _repo.Setup(r => r.ServiceSettings).Returns(_settingsRepo.Object);

        _activityRepo.Setup(r => r.CreateActivityAsync(It.IsAny<Activity>())).Returns(Task.CompletedTask);
        _activityRepo.Setup(r => r.UpdateActivityAsync(It.IsAny<Activity>()))
            .Callback<Activity>(a => _completedActivity = a)
            .Returns(Task.CompletedTask);
        _activityRepo.Setup(r => r.GetMaxConfigurationChangeVersionAsync(ActivityTargetType.ServiceSetting, It.IsAny<string>()))
            .ReturnsAsync(0);

        // Tracking disabled keeps the write path minimal (no hash key or prior-snapshot lookups) for these tests,
        // which are about the flag gate and persistence, not configuration change capture itself (covered by
        // ServiceSettingConfigurationChangeCaptureTests). Seeded into the same dictionary the catch-all
        // GetSettingAsync setup below reads from, so there is only one Setup per method and no ordering hazard
        // between a specific-key setup and a general It.IsAny<string> one (Moq's "last matching setup wins" rule
        // would otherwise let whichever is registered second silently shadow the other).
        _persisted[Constants.SettingKeys.ChangeTrackingConfigurationChangesEnabled] = new ServiceSetting
        {
            Key = Constants.SettingKeys.ChangeTrackingConfigurationChangesEnabled,
            DisplayName = "Track configuration changes",
            ValueType = ServiceSettingValueType.Boolean,
            Value = "false"
        };

        var inDevelopmentSetting = new ServiceSetting
        {
            Key = FeatureFlagCatalogue.UniqueValueGeneration.Key,
            DisplayName = FeatureFlagCatalogue.UniqueValueGeneration.DisplayName,
            Description = FeatureFlagCatalogue.UniqueValueGeneration.Description,
            Category = ServiceSettingCategory.FeatureFlags,
            ValueType = ServiceSettingValueType.Boolean,
            DefaultValue = "false",
            Value = "false"
        };
        _persisted[FeatureFlagCatalogue.UniqueValueGeneration.Key] = inDevelopmentSetting;

        _settingsRepo.Setup(r => r.GetSettingAsync(It.IsAny<string>()))
            .Returns((string key) => Task.FromResult(_persisted.GetValueOrDefault(key)));
        _settingsRepo.Setup(r => r.UpdateSettingAsync(It.IsAny<ServiceSetting>()))
            .Callback<ServiceSetting>(s => _persisted[s.Key] = s)
            .Returns(Task.CompletedTask);

        _jim = new JimApplication(_repo.Object);
    }

    [TearDown]
    public void TearDown() => _jim?.Dispose();

    [Test]
    public async Task SetFeatureFlagAsync_PersistsThroughTheOrdinarySettingUpdatePathAsync()
    {
        // The catalogue's only flag today is In Development, so allowInDevelopment is needed to enable it; the
        // point of this test is the persistence/audit path, which is identical whatever the tier.
        var key = FeatureFlagCatalogue.UniqueValueGeneration.Key;

        var state = await _jim.FeatureFlags.SetFeatureFlagAsync(key, true, NewUser(), allowInDevelopment: true);

        Assert.That(state.Enabled, Is.True);
        Assert.That(_persisted[key].Value, Is.EqualTo("true"));
        Assert.That(_completedActivity, Is.Not.Null, "a flag change must create the same audit Activity an ordinary Service Setting change does");
        Assert.That(_completedActivity!.TargetOperationType, Is.EqualTo(ActivityTargetOperationType.Update));
        // ServiceSettingKey/ConfigurationChangeSnapshot are set inside the capture path, which this fixture leaves
        // disabled (see the tracking setting's seed above) to keep these tests focused on the flag gate and
        // persistence; ServiceSettingConfigurationChangeCaptureTests covers capture itself, and the seeded and
        // toggled flag's key was confirmed to deep-link the change history correctly at runtime (#1781).
    }

    [Test]
    public void SetFeatureFlagAsync_EnablingInDevelopmentFlagWithoutAcknowledgement_Throws()
    {
        Assert.That(async () => await _jim.FeatureFlags.SetFeatureFlagAsync(
                FeatureFlagCatalogue.UniqueValueGeneration.Key, true, NewUser()),
            Throws.InvalidOperationException);
        Assert.That(_persisted[FeatureFlagCatalogue.UniqueValueGeneration.Key].Value, Is.EqualTo("false"),
            "a rejected enable must not write anything");
    }

    [Test]
    public async Task SetFeatureFlagAsync_EnablingInDevelopmentFlagWithAcknowledgement_SucceedsAsync()
    {
        var state = await _jim.FeatureFlags.SetFeatureFlagAsync(
            FeatureFlagCatalogue.UniqueValueGeneration.Key, true, NewUser(), allowInDevelopment: true);

        Assert.That(state.Enabled, Is.True);
        Assert.That(_persisted[FeatureFlagCatalogue.UniqueValueGeneration.Key].Value, Is.EqualTo("true"));
    }

    [Test]
    public async Task SetFeatureFlagAsync_DisablingInDevelopmentFlag_NeedsNoAcknowledgementAsync()
    {
        _persisted[FeatureFlagCatalogue.UniqueValueGeneration.Key].Value = "true";

        var state = await _jim.FeatureFlags.SetFeatureFlagAsync(FeatureFlagCatalogue.UniqueValueGeneration.Key, false, NewUser());

        Assert.That(state.Enabled, Is.False);
    }

    [Test]
    public void SetFeatureFlagAsync_UnknownKey_ThrowsNotFound()
    {
        Assert.That(async () => await _jim.FeatureFlags.SetFeatureFlagAsync("Features.DoesNotExist", true, NewUser()),
            Throws.TypeOf<FeatureFlagNotFoundException>());
    }

    [Test]
    public async Task SetFeatureFlagAsync_ApiKeyInitiated_PersistsAndCapturesAsync()
    {
        var apiKey = new ApiKey { Id = Guid.NewGuid(), Name = "automation" };

        var state = await _jim.FeatureFlags.SetFeatureFlagAsync(
            FeatureFlagCatalogue.UniqueValueGeneration.Key, true, apiKey, allowInDevelopment: true);

        Assert.That(state.Enabled, Is.True);
        Assert.That(_completedActivity, Is.Not.Null);
        Assert.That(_completedActivity!.InitiatedByType, Is.EqualTo(ActivityInitiatorType.ApiKey));
    }

    private static MetaverseObject NewUser() => new() { Id = Guid.NewGuid(), CachedDisplayName = "Admin User" };
}
