// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Text;
using JIM.Application;
using JIM.Application.Interfaces;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Security;
using JIM.Models.Staging;
using Moq;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// Tests for configuration-change capture wiring: a redacted, versioned snapshot and optional reason are recorded on
/// the Activity when tracking is enabled; the reason is still recorded (but no snapshot) when tracking is disabled; the
/// version is assigned as max + 1; and the keyed-hash key is generated and persisted encrypted on first use.
/// </summary>
[TestFixture]
public class ConfigurationChangeCaptureTests
{
    private Mock<IRepository> _repo = null!;
    private Mock<IActivityRepository> _activityRepo = null!;
    private Mock<IServiceSettingsRepository> _settingsRepo = null!;
    private Mock<IConnectedSystemRepository> _csRepo = null!;
    private FakeProtection _protection = null!;
    private JimApplication _jim = null!;
    private Activity? _completedActivity;

    [SetUp]
    public void SetUp()
    {
        TestUtilities.SetEnvironmentVariables();

        _repo = new Mock<IRepository>();
        _activityRepo = new Mock<IActivityRepository>();
        _settingsRepo = new Mock<IServiceSettingsRepository>();
        _csRepo = new Mock<IConnectedSystemRepository>();
        _repo.Setup(r => r.Activity).Returns(_activityRepo.Object);
        _repo.Setup(r => r.ServiceSettings).Returns(_settingsRepo.Object);
        _repo.Setup(r => r.ConnectedSystems).Returns(_csRepo.Object);

        _activityRepo.Setup(r => r.CreateActivityAsync(It.IsAny<Activity>())).Returns(Task.CompletedTask);
        _activityRepo.Setup(r => r.UpdateActivityAsync(It.IsAny<Activity>()))
            .Callback<Activity>(a => _completedActivity = a)
            .Returns(Task.CompletedTask);
        _activityRepo.Setup(r => r.GetMaxConfigurationChangeVersionAsync(It.IsAny<ActivityTargetType>(), It.IsAny<int>()))
            .ReturnsAsync(6);
        _csRepo.Setup(r => r.UpdateSyncRuleAsync(It.IsAny<SyncRule>())).Returns(Task.CompletedTask);

        _protection = new FakeProtection();
        _jim = new JimApplication(_repo.Object) { CredentialProtection = _protection };
    }

    [TearDown]
    public void TearDown() => _jim?.Dispose();

    [Test]
    public async Task CreateOrUpdateSyncRuleAsync_WhenTrackingEnabled_CapturesSnapshotVersionAndReasonOnActivityAsync()
    {
        SetupTrackingSetting(enabled: true);
        SetupHashKeySetting();

        var result = await _jim.ConnectedSystems.CreateOrUpdateSyncRuleAsync(
            BuildExportRule(), NewApiKey(), changeReason: "Tighten scope (CHG0098)");

        Assert.That(result, Is.True);
        Assert.That(_completedActivity, Is.Not.Null);
        Assert.That(_completedActivity!.ConfigurationChangeVersion, Is.EqualTo(7), "version is the existing maximum (6) + 1");
        Assert.That(_completedActivity.ChangeReason, Is.EqualTo("Tighten scope (CHG0098)"));
        Assert.That(_completedActivity.SyncRuleId, Is.EqualTo(55), "the activity must carry the object id so history is queryable");
        Assert.That(_completedActivity.ConfigurationChangeSnapshot, Is.Not.Null);
        Assert.That(_completedActivity.ConfigurationChangeSnapshot, Does.Contain("\"objectType\":\"SynchronisationRule\""));
    }

    [Test]
    public async Task CreateOrUpdateSyncRuleAsync_WhenTrackingDisabled_RecordsReasonButNoSnapshotAsync()
    {
        SetupTrackingSetting(enabled: false);

        await _jim.ConnectedSystems.CreateOrUpdateSyncRuleAsync(
            BuildExportRule(), NewApiKey(), changeReason: "no tracking");

        Assert.That(_completedActivity, Is.Not.Null);
        Assert.That(_completedActivity!.ConfigurationChangeSnapshot, Is.Null, "no snapshot is captured when tracking is disabled");
        Assert.That(_completedActivity.ConfigurationChangeVersion, Is.Null);
        Assert.That(_completedActivity.ChangeReason, Is.EqualTo("no tracking"), "the reason is recorded independently of the snapshot toggle");
    }

    [Test]
    public async Task DeleteSyncRuleAsync_WhenTrackingEnabled_CapturesTombstoneSnapshotWithoutVersionOrLinkAsync()
    {
        SetupTrackingSetting(enabled: true);
        SetupHashKeySetting();
        _csRepo.Setup(r => r.DeleteSyncRuleAsync(It.IsAny<SyncRule>())).Returns(Task.CompletedTask);

        await _jim.ConnectedSystems.DeleteSyncRuleAsync(BuildExportRule(), NewApiKey(), changeReason: "decommissioned");

        Assert.That(_completedActivity, Is.Not.Null);
        Assert.That(_completedActivity!.ConfigurationChangeSnapshot, Does.Contain("\"objectType\":\"SynchronisationRule\""), "the deletion records a tombstone snapshot");
        Assert.That(_completedActivity.ChangeReason, Is.EqualTo("decommissioned"));
        Assert.That(_completedActivity.SyncRuleId, Is.Null, "a delete activity is left unlinked so it can complete after the rule is removed");
        Assert.That(_completedActivity.ConfigurationChangeVersion, Is.Null, "deletions record a tombstone, not a versioned entry");
    }

    [Test]
    public async Task GetNextConfigurationChangeVersionAsync_ReturnsExistingMaximumPlusOneAsync()
    {
        _activityRepo.Setup(r => r.GetMaxConfigurationChangeVersionAsync(ActivityTargetType.ConnectedSystem, 9)).ReturnsAsync(3);

        var next = await _jim.Activities.GetNextConfigurationChangeVersionAsync(ActivityTargetType.ConnectedSystem, 9);

        Assert.That(next, Is.EqualTo(4));
    }

    [Test]
    public async Task GetNextConfigurationChangeVersionAsync_GuidKeyed_ReturnsExistingMaximumPlusOneAsync()
    {
        var scheduleId = Guid.NewGuid();
        _activityRepo.Setup(r => r.GetMaxConfigurationChangeVersionAsync(ActivityTargetType.Schedule, scheduleId)).ReturnsAsync(3);

        var next = await _jim.Activities.GetNextConfigurationChangeVersionAsync(ActivityTargetType.Schedule, scheduleId);

        Assert.That(next, Is.EqualTo(4), "the Guid-keyed overload assigns max + 1 just like the integer-keyed one");
    }

    [Test]
    public async Task GetConfigurationChangeTrackingEnabledAsync_DefaultsToTrueWhenUnsetAsync()
    {
        _settingsRepo.Setup(r => r.GetSettingAsync(Constants.SettingKeys.ChangeTrackingConfigurationChangesEnabled))
            .ReturnsAsync((ServiceSetting?)null);

        Assert.That(await _jim.ServiceSettings.GetConfigurationChangeTrackingEnabledAsync(), Is.True);
    }

    [Test]
    public async Task GetOrCreateConfigurationChangeHashKeyAsync_GeneratesAndPersistsEncryptedKeyWhenAbsentAsync()
    {
        _settingsRepo.Setup(r => r.GetSettingAsync(Constants.SettingKeys.ConfigurationChangeHashKey)).ReturnsAsync((ServiceSetting?)null);
        ServiceSetting? created = null;
        _settingsRepo.Setup(r => r.GetOrCreateSettingAsync(It.IsAny<ServiceSetting>()))
            .Callback<ServiceSetting>(s => created = s)
            .ReturnsAsync((ServiceSetting s) => s);

        var key = await _jim.ServiceSettings.GetOrCreateConfigurationChangeHashKeyAsync();

        Assert.That(key, Has.Length.EqualTo(32), "the key is 256-bit");
        Assert.That(created, Is.Not.Null);
        Assert.That(created!.IsReadOnly, Is.True);
        Assert.That(created.ValueType, Is.EqualTo(ServiceSettingValueType.StringEncrypted));
        Assert.That(_protection.IsProtected(created.Value), Is.True, "the key is stored encrypted at rest, never as raw base64");
    }

    [Test]
    public void GetOrCreateConfigurationChangeHashKeyAsync_WhenStoredKeyEncryptedButNoProtectionService_ThrowsClearErrorAsync()
    {
        // Reproduces the JIM.Web bootstrap bug: a JimApplication constructed without a credential protection service
        // reads the hash key that another instance stored encrypted at rest. GetSettingValueAsync cannot decrypt it,
        // so it returns the ciphertext, which is not valid base64. Previously this surfaced as a cryptic FormatException
        // that the best-effort capture path swallowed, silently dropping the snapshot; it must now be a clear,
        // diagnosable InvalidOperationException naming the missing protection service.
        using var jimWithoutProtection = new JimApplication(_repo.Object);
        _settingsRepo.Setup(r => r.GetSettingAsync(Constants.SettingKeys.ConfigurationChangeHashKey))
            .ReturnsAsync(new ServiceSetting
            {
                Key = Constants.SettingKeys.ConfigurationChangeHashKey,
                DisplayName = "Configuration change hash key",
                ValueType = ServiceSettingValueType.StringEncrypted,
                Value = _protection.Protect(Convert.ToBase64String(new byte[32])) // an encrypted, non-base64 value
            });

        var ex = Assert.ThrowsAsync<InvalidOperationException>(
            async () => await jimWithoutProtection.ServiceSettings.GetOrCreateConfigurationChangeHashKeyAsync());
        Assert.That(ex!.Message, Does.Contain("CredentialProtection"), "the error must name the missing protection service");
        Assert.That(ex.InnerException, Is.TypeOf<FormatException>(), "the cryptic base64 decode failure is preserved as the inner cause");
    }

    // -- helpers -------------------------------------------------------------------------------------------------------

    private void SetupTrackingSetting(bool enabled) =>
        _settingsRepo.Setup(r => r.GetSettingAsync(Constants.SettingKeys.ChangeTrackingConfigurationChangesEnabled))
            .ReturnsAsync(new ServiceSetting
            {
                Key = Constants.SettingKeys.ChangeTrackingConfigurationChangesEnabled,
                DisplayName = "Track configuration changes",
                ValueType = ServiceSettingValueType.Boolean,
                Value = enabled ? "true" : "false"
            });

    private void SetupHashKeySetting() =>
        _settingsRepo.Setup(r => r.GetSettingAsync(Constants.SettingKeys.ConfigurationChangeHashKey))
            .ReturnsAsync(new ServiceSetting
            {
                Key = Constants.SettingKeys.ConfigurationChangeHashKey,
                DisplayName = "Configuration change hash key",
                ValueType = ServiceSettingValueType.StringEncrypted,
                Value = _protection.Protect(Convert.ToBase64String(new byte[32]))
            });

    private static SyncRule BuildExportRule() => new()
    {
        Id = 55,
        Name = "AD Export",
        Direction = SyncRuleDirection.Export,
        ConnectedSystem = new ConnectedSystem { Id = 3, Name = "AD" },
        ConnectedSystemId = 3,
        ConnectedSystemObjectType = new ConnectedSystemObjectType { Id = 7, Name = "user" },
        ConnectedSystemObjectTypeId = 7,
        MetaverseObjectType = new MetaverseObjectType { Id = 1, Name = "Person" },
        MetaverseObjectTypeId = 1
    };

    private static ApiKey NewApiKey() => new() { Id = Guid.NewGuid(), Name = "test-key" };

    /// <summary>A round-trip credential-protection test double using a recognisable encrypted-value prefix.</summary>
    private sealed class FakeProtection : ICredentialProtectionService
    {
        private const string Prefix = "$JIM$v1$";

        public string? Protect(string? plainText) =>
            string.IsNullOrEmpty(plainText) ? plainText : Prefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(plainText));

        public string? Unprotect(string? protectedData) =>
            string.IsNullOrEmpty(protectedData) || !IsProtected(protectedData)
                ? protectedData
                : Encoding.UTF8.GetString(Convert.FromBase64String(protectedData[Prefix.Length..]));

        public bool IsProtected(string? value) =>
            !string.IsNullOrEmpty(value) && value.StartsWith(Prefix, StringComparison.Ordinal);
    }
}
