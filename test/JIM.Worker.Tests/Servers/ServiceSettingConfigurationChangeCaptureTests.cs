// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Security.Cryptography;
using System.Text;
using JIM.Application;
using JIM.Application.Interfaces;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Security;
using Moq;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// Tests configuration change-history capture for Service Settings: a redacted, versioned snapshot is recorded on
/// the audit Activity when tracking is enabled, keyed by the setting's string key via
/// <see cref="Activity.ServiceSettingKey"/>; encrypted setting values never appear in the snapshot in plaintext or
/// ciphertext form (a keyed hash is stored instead); a revert-to-default snapshot represents the override being
/// removed; nothing is captured when tracking is disabled; and the semantic no-change dedupe guard applies.
/// </summary>
[TestFixture]
public class ServiceSettingConfigurationChangeCaptureTests
{
    private const string SettingKey = "History.RetentionPeriod";
    private const string EncryptedSettingKey = "SSO.Secret";

    private Mock<IRepository> _repo = null!;
    private Mock<IActivityRepository> _activityRepo = null!;
    private Mock<IServiceSettingsRepository> _settingsRepo = null!;
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
        _repo.Setup(r => r.Activity).Returns(_activityRepo.Object);
        _repo.Setup(r => r.ServiceSettings).Returns(_settingsRepo.Object);

        _activityRepo.Setup(r => r.CreateActivityAsync(It.IsAny<Activity>())).Returns(Task.CompletedTask);
        _activityRepo.Setup(r => r.UpdateActivityAsync(It.IsAny<Activity>()))
            .Callback<Activity>(a => _completedActivity = a)
            .Returns(Task.CompletedTask);
        _settingsRepo.Setup(r => r.UpdateSettingAsync(It.IsAny<ServiceSetting>())).Returns(Task.CompletedTask);

        _protection = new FakeProtection();
        _jim = new JimApplication(_repo.Object) { CredentialProtection = _protection };
    }

    [TearDown]
    public void TearDown() => _jim?.Dispose();

    [Test]
    public async Task UpdateSettingValueAsync_WhenTrackingEnabled_CapturesVersionedSnapshotKeyedBySettingKeyAsync()
    {
        SetupTrackingSetting(enabled: true);
        SetupHashKeySetting();
        var setting = SetupSetting(BuildSetting(value: "90.00:00:00"));
        _activityRepo.Setup(r => r.GetMaxConfigurationChangeVersionAsync(ActivityTargetType.ServiceSetting, SettingKey))
            .ReturnsAsync(6);

        await _jim.ServiceSettings.UpdateSettingValueAsync(SettingKey, "30.00:00:00", NewUser());

        Assert.That(_completedActivity, Is.Not.Null);
        Assert.That(_completedActivity!.ServiceSettingKey, Is.EqualTo(SettingKey), "the activity must carry the setting key so history is queryable");
        Assert.That(_completedActivity!.ConfigurationChangeVersion, Is.EqualTo(7), "version is the existing maximum (6) + 1");
        Assert.That(_completedActivity!.ConfigurationChangeSnapshot, Is.Not.Null);
        Assert.That(_completedActivity!.ConfigurationChangeSnapshot, Does.Contain("\"objectType\":\"ServiceSetting\""));
        Assert.That(_completedActivity!.ConfigurationChangeSnapshot, Does.Contain("30.00:00:00"), "the snapshot reflects the newly persisted value");
        Assert.That(setting.Value, Is.EqualTo("30.00:00:00"));
    }

    [Test]
    public async Task UpdateSettingValueAsync_SnapshotRepresentsOverrideSemanticsAsync()
    {
        SetupTrackingSetting(enabled: true);
        SetupHashKeySetting();
        SetupSetting(BuildSetting(value: null));
        SetupMaxVersion(0);

        await _jim.ServiceSettings.UpdateSettingValueAsync(SettingKey, "30.00:00:00", NewUser());

        Assert.That(_completedActivity, Is.Not.Null);
        Assert.That(_completedActivity!.ConfigurationChangeSnapshot, Does.Contain("\"overridden\""), "the snapshot must represent override-vs-default semantics");
        Assert.That(_completedActivity!.ConfigurationChangeSnapshot, Does.Contain("90.00:00:00"), "the default value provides the diff context for overrides and reverts");
    }

    [Test]
    public async Task UpdateSettingValueAsync_EncryptedSetting_NeverStoresPlaintextOrCiphertextAsync()
    {
        SetupTrackingSetting(enabled: true);
        SetupHashKeySetting();
        SetupSetting(BuildEncryptedSetting());
        _activityRepo.Setup(r => r.GetMaxConfigurationChangeVersionAsync(ActivityTargetType.ServiceSetting, EncryptedSettingKey))
            .ReturnsAsync(0);

        await _jim.ServiceSettings.UpdateSettingValueAsync(EncryptedSettingKey, "hunter2", NewUser());

        Assert.That(_completedActivity, Is.Not.Null);
        var snapshot = _completedActivity!.ConfigurationChangeSnapshot;
        Assert.That(snapshot, Is.Not.Null);
        Assert.That(snapshot, Does.Not.Contain("hunter2"), "the plaintext secret must never be stored");
        var ciphertext = _protection.Protect("hunter2")!;
        Assert.That(snapshot, Does.Not.Contain(ciphertext), "the ciphertext must never be stored either");
        Assert.That(snapshot, Does.Not.Contain(Convert.ToBase64String(Encoding.UTF8.GetBytes("hunter2"))), "nor any trivial encoding of the secret");
        Assert.That(snapshot, Does.Contain("\"isSecret\":true"), "the value is represented as a redacted secret");
        Assert.That(SecretValueNode(snapshot!).Value, Is.EqualTo(ExpectedKeyedHash("hunter2")),
            "a keyed hash lets a rotation be detected without disclosure");
    }

    [Test]
    public async Task UpdateSettingValueAsync_EncryptedSettingRotation_ProducesDifferentKeyedHashAsync()
    {
        SetupTrackingSetting(enabled: true);
        SetupHashKeySetting();
        SetupSetting(BuildEncryptedSetting());
        _activityRepo.Setup(r => r.GetMaxConfigurationChangeVersionAsync(ActivityTargetType.ServiceSetting, EncryptedSettingKey))
            .ReturnsAsync(0);

        await _jim.ServiceSettings.UpdateSettingValueAsync(EncryptedSettingKey, "hunter2", NewUser());
        var firstSnapshot = _completedActivity!.ConfigurationChangeSnapshot!;
        _activityRepo.Setup(r => r.GetLatestConfigurationChangeSnapshotAsync(ActivityTargetType.ServiceSetting, EncryptedSettingKey))
            .ReturnsAsync(firstSnapshot);
        _completedActivity = null;

        await _jim.ServiceSettings.UpdateSettingValueAsync(EncryptedSettingKey, "correct-horse", NewUser());

        Assert.That(_completedActivity, Is.Not.Null);
        Assert.That(_completedActivity!.ConfigurationChangeSnapshot, Is.Not.Null, "a rotated secret is a real change and must capture");
        Assert.That(SecretValueNode(_completedActivity!.ConfigurationChangeSnapshot!).Value, Is.EqualTo(ExpectedKeyedHash("correct-horse")));
        Assert.That(ExpectedKeyedHash("correct-horse"), Is.Not.EqualTo(ExpectedKeyedHash("hunter2")));
    }

    // Finds the redacted "value" node in a stored snapshot document, so hash assertions are robust to JSON string
    // escaping (Base64 '+' is stored as +, defeating raw-string Contains checks).
    private static ConfigurationSnapshotNode SecretValueNode(string snapshotJson)
    {
        var snapshot = JIM.Application.Services.ConfigurationSnapshotService.Deserialise(snapshotJson);
        Assert.That(snapshot, Is.Not.Null);
        var node = snapshot!.Root.Children!.Single(c => c.Key == "value");
        Assert.That(node.IsSecret, Is.True, "the value node must be marked secret");
        return node;
    }

    [Test]
    public async Task UpdateSettingValueAsync_WhenTrackingDisabled_RecordsNoSnapshotButStillTheReasonAsync()
    {
        SetupTrackingSetting(enabled: false);
        SetupSetting(BuildSetting(value: "90.00:00:00"));

        await _jim.ServiceSettings.UpdateSettingValueAsync(SettingKey, "30.00:00:00", NewUser(), changeReason: "no tracking");

        Assert.That(_completedActivity, Is.Not.Null);
        Assert.That(_completedActivity!.ConfigurationChangeSnapshot, Is.Null, "no snapshot is captured when tracking is disabled");
        Assert.That(_completedActivity!.ConfigurationChangeVersion, Is.Null);
        Assert.That(_completedActivity!.ChangeReason, Is.EqualTo("no tracking"), "the reason is recorded independently of the snapshot toggle");
    }

    [Test]
    public async Task UpdateSettingValueAsync_WhenValueUnchanged_SkipsVersionAndSnapshotAsync()
    {
        SetupTrackingSetting(enabled: true);
        SetupHashKeySetting();
        SetupSetting(BuildSetting(value: "90.00:00:00"));
        SetupMaxVersion(3);

        // First save captures version 4; the second, semantically identical save must not consume a version.
        await _jim.ServiceSettings.UpdateSettingValueAsync(SettingKey, "30.00:00:00", NewUser());
        var storedSnapshot = _completedActivity!.ConfigurationChangeSnapshot;
        Assert.That(storedSnapshot, Is.Not.Null);
        Assert.That(_completedActivity!.ConfigurationChangeVersion, Is.EqualTo(4));
        _activityRepo.Setup(r => r.GetLatestConfigurationChangeSnapshotAsync(ActivityTargetType.ServiceSetting, SettingKey))
            .ReturnsAsync(storedSnapshot);
        _completedActivity = null;

        await _jim.ServiceSettings.UpdateSettingValueAsync(SettingKey, "30.00:00:00", NewUser());

        Assert.That(_completedActivity, Is.Not.Null);
        Assert.That(_completedActivity!.ConfigurationChangeVersion, Is.Null, "an unchanged Service Setting must not consume a version");
        Assert.That(_completedActivity!.ConfigurationChangeSnapshot, Is.Null);
        Assert.That(_completedActivity!.ServiceSettingKey, Is.EqualTo(SettingKey), "the activity still deep-links to the setting when the capture is skipped");
    }

    [Test]
    public async Task RevertSettingToDefaultAsync_CapturesSnapshotShowingTheOverrideRemovedAsync()
    {
        SetupTrackingSetting(enabled: true);
        SetupHashKeySetting();
        var setting = SetupSetting(BuildSetting(value: "30.00:00:00"));
        SetupMaxVersion(4);

        await _jim.ServiceSettings.RevertSettingToDefaultAsync(SettingKey, NewUser(), changeReason: "back to default");

        Assert.That(_completedActivity, Is.Not.Null);
        Assert.That(setting.Value, Is.Null, "revert clears the override");
        Assert.That(_completedActivity!.ServiceSettingKey, Is.EqualTo(SettingKey));
        Assert.That(_completedActivity!.ConfigurationChangeVersion, Is.EqualTo(5));
        Assert.That(_completedActivity!.TargetOperationType, Is.EqualTo(ActivityTargetOperationType.Revert));
        Assert.That(_completedActivity!.ChangeReason, Is.EqualTo("back to default"));
        Assert.That(_completedActivity!.ConfigurationChangeSnapshot, Does.Contain("\"overridden\""));
        Assert.That(_completedActivity!.ConfigurationChangeSnapshot, Does.Not.Contain("30.00:00:00"),
            "the reverted snapshot no longer carries the removed override value");
    }

    [Test]
    public async Task UpdateSettingValueAsync_ApiKeyInitiated_CapturesTooAsync()
    {
        SetupTrackingSetting(enabled: true);
        SetupHashKeySetting();
        SetupSetting(BuildSetting(value: "90.00:00:00"));
        SetupMaxVersion(0);

        var apiKey = new ApiKey { Id = Guid.NewGuid(), Name = "prov-api" };
        await _jim.ServiceSettings.UpdateSettingValueAsync(SettingKey, "30.00:00:00", apiKey, changeReason: "via API");

        Assert.That(_completedActivity, Is.Not.Null);
        Assert.That(_completedActivity!.ServiceSettingKey, Is.EqualTo(SettingKey));
        Assert.That(_completedActivity!.ConfigurationChangeVersion, Is.EqualTo(1));
        Assert.That(_completedActivity!.ChangeReason, Is.EqualTo("via API"));
    }

    // -- helpers -------------------------------------------------------------------------------------------------------

    private static readonly byte[] HashKeyBytes = new byte[32];

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
                Value = _protection.Protect(Convert.ToBase64String(HashKeyBytes))
            });

    private ServiceSetting SetupSetting(ServiceSetting setting)
    {
        _settingsRepo.Setup(r => r.GetSettingAsync(setting.Key)).ReturnsAsync(setting);
        return setting;
    }

    private void SetupMaxVersion(int max) =>
        _activityRepo.Setup(r => r.GetMaxConfigurationChangeVersionAsync(ActivityTargetType.ServiceSetting, It.IsAny<string>()))
            .ReturnsAsync(max);

    private static ServiceSetting BuildSetting(string? value) => new()
    {
        Key = SettingKey,
        DisplayName = "History retention period",
        Category = ServiceSettingCategory.Maintenance,
        ValueType = ServiceSettingValueType.TimeSpan,
        DefaultValue = "90.00:00:00",
        Value = value
    };

    private static ServiceSetting BuildEncryptedSetting() => new()
    {
        Key = EncryptedSettingKey,
        DisplayName = "SSO secret",
        Category = ServiceSettingCategory.SSO,
        ValueType = ServiceSettingValueType.StringEncrypted,
        DefaultValue = null,
        Value = null
    };

    private static string ExpectedKeyedHash(string plaintext)
    {
        using var hmac = new HMACSHA256(HashKeyBytes);
        return Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(plaintext)));
    }

    private static MetaverseObject NewUser() => new() { Id = Guid.NewGuid(), CachedDisplayName = "Admin User" };

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
