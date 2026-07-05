// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Application.Interfaces;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Security;
using Moq;
using NUnit.Framework;
using System.Security.Cryptography;
using System.Text;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// Tests configuration change-history capture for API Keys: every store mutation records a versioned, metadata-only
/// snapshot on its audit Activity, keyed by <see cref="Activity.ApiKeyId"/>; the key's secret material
/// (<see cref="ApiKey.KeyHash"/>) never appears in a snapshot in any form, not even a keyed hash, since there is no
/// legitimate "did it change" question for it; the operational <see cref="ApiKey.LastUsedAt"/> and
/// <see cref="ApiKey.LastUsedFromIp"/> fields are excluded too; a deletion records an unversioned, unlinked
/// tombstone; and the shared toggle and semantic-dedupe behaviours apply. Usage recording
/// (<see cref="SecurityServer.RecordApiKeyUsageAsync"/>) is deliberately excluded from all of this: it is
/// operational state, not a configuration change.
/// </summary>
[TestFixture]
public class ApiKeyConfigurationChangeCaptureTests
{
    private static readonly Guid ApiKeyId = Guid.Parse("7c1e2a4b-3d5f-4a6b-9c8d-1e2f3a4b5c6d");

    private static readonly string DefaultKeyHash =
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("default-placeholder-key"))).ToLowerInvariant();

    private Mock<IRepository> _repo = null!;
    private Mock<IActivityRepository> _activityRepo = null!;
    private Mock<IServiceSettingsRepository> _settingsRepo = null!;
    private Mock<IApiKeyRepository> _apiKeyRepo = null!;
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
        _apiKeyRepo = new Mock<IApiKeyRepository>();
        _repo.Setup(r => r.Activity).Returns(_activityRepo.Object);
        _repo.Setup(r => r.ServiceSettings).Returns(_settingsRepo.Object);
        _repo.Setup(r => r.ApiKeys).Returns(_apiKeyRepo.Object);

        _activityRepo.Setup(r => r.CreateActivityAsync(It.IsAny<Activity>())).Returns(Task.CompletedTask);
        _activityRepo.Setup(r => r.UpdateActivityAsync(It.IsAny<Activity>()))
            .Callback<Activity>(a => _completedActivity = a)
            .Returns(Task.CompletedTask);
        _apiKeyRepo.Setup(r => r.CreateAsync(It.IsAny<ApiKey>())).ReturnsAsync((ApiKey k) => k);
        _apiKeyRepo.Setup(r => r.UpdateAsync(It.IsAny<ApiKey>())).ReturnsAsync((ApiKey k) => k);
        _apiKeyRepo.Setup(r => r.DeleteAsync(It.IsAny<Guid>())).Returns(Task.CompletedTask);

        _protection = new FakeProtection();
        _jim = new JimApplication(_repo.Object) { CredentialProtection = _protection };

        // NUnit reuses one fixture instance across every [Test] by default, so a value left over from a previous
        // test would otherwise leak in here; explicit reset keeps the negative assertion in
        // RecordApiKeyUsageAsync_DoesNotRecordActivityOrCaptureAsync reliable regardless of execution order.
        _completedActivity = null;
    }

    [TearDown]
    public void TearDown() => _jim?.Dispose();

    [Test]
    public async Task CreateApiKeyAsync_UserInitiated_CapturesVersionOneSnapshotAsync()
    {
        SetupTrackingSetting(enabled: true);
        SetupHashKeySetting();
        SetupMaxVersion(0);
        ApiKey? persisted = null;
        _apiKeyRepo.Setup(r => r.CreateAsync(It.IsAny<ApiKey>()))
            .Callback<ApiKey>(k => persisted = k)
            .ReturnsAsync((ApiKey k) => k);
        _apiKeyRepo.Setup(r => r.GetByIdAsync(It.IsAny<Guid>())).ReturnsAsync(() => persisted);

        var apiKey = BuildApiKey();
        await _jim.Security.CreateApiKeyAsync(apiKey, NewUser(), changeReason: "new CI pipeline key");

        Assert.That(_completedActivity, Is.Not.Null);
        Assert.That(_completedActivity!.TargetType, Is.EqualTo(ActivityTargetType.ApiKey));
        Assert.That(_completedActivity.TargetOperationType, Is.EqualTo(ActivityTargetOperationType.Create));
        Assert.That(_completedActivity.InitiatedByType, Is.EqualTo(ActivityInitiatorType.User));
        Assert.That(_completedActivity.ApiKeyId, Is.EqualTo(apiKey.Id), "the activity must carry the key id so history is queryable");
        Assert.That(_completedActivity.ConfigurationChangeVersion, Is.EqualTo(1));
        Assert.That(_completedActivity.ChangeReason, Is.EqualTo("new CI pipeline key"));
        var snapshot = _completedActivity.ConfigurationChangeSnapshot;
        Assert.That(snapshot, Is.Not.Null);
        Assert.That(snapshot, Does.Contain("\"objectType\":\"ApiKey\""));
    }

    [Test]
    public async Task CreateApiKeyAsync_ApiKeyInitiated_AttributesActivityToTheInitiatingKeyAsync()
    {
        SetupTrackingSetting(enabled: true);
        SetupHashKeySetting();
        SetupMaxVersion(0);
        ApiKey? persisted = null;
        _apiKeyRepo.Setup(r => r.CreateAsync(It.IsAny<ApiKey>()))
            .Callback<ApiKey>(k => persisted = k)
            .ReturnsAsync((ApiKey k) => k);
        _apiKeyRepo.Setup(r => r.GetByIdAsync(It.IsAny<Guid>())).ReturnsAsync(() => persisted);

        var initiatingKey = new ApiKey { Id = Guid.NewGuid(), Name = "provisioning-key" };
        var apiKey = BuildApiKey();

        await _jim.Security.CreateApiKeyAsync(apiKey, initiatingKey, changeReason: "provisioned via API");

        Assert.That(_completedActivity, Is.Not.Null);
        Assert.That(_completedActivity!.InitiatedByType, Is.EqualTo(ActivityInitiatorType.ApiKey));
        Assert.That(_completedActivity.InitiatedById, Is.EqualTo(initiatingKey.Id));
        Assert.That(_completedActivity.ApiKeyId, Is.EqualTo(apiKey.Id));
        Assert.That(_completedActivity.ConfigurationChangeVersion, Is.EqualTo(1));
    }

    [Test]
    public async Task CreateApiKeyAsync_NoInitiator_RecordsSystemAttributedActivityAsync()
    {
        SetupTrackingSetting(enabled: true);
        SetupHashKeySetting();
        SetupMaxVersion(0);
        ApiKey? persisted = null;
        _apiKeyRepo.Setup(r => r.CreateAsync(It.IsAny<ApiKey>()))
            .Callback<ApiKey>(k => persisted = k)
            .ReturnsAsync((ApiKey k) => k);
        _apiKeyRepo.Setup(r => r.GetByIdAsync(It.IsAny<Guid>())).ReturnsAsync(() => persisted);

        // Mirrors the JIM.Web bootstrap call (Program.cs, infrastructure API key creation), which has no
        // MetaverseObject and no ApiKey to attribute the change to.
        var apiKey = BuildApiKey();
        await _jim.Security.CreateApiKeyAsync(apiKey);

        Assert.That(_completedActivity, Is.Not.Null);
        Assert.That(_completedActivity!.InitiatedByType, Is.EqualTo(ActivityInitiatorType.System));
        Assert.That(_completedActivity.InitiatedByName, Is.EqualTo("System"));
        Assert.That(_completedActivity.ConfigurationChangeVersion, Is.EqualTo(1));
        Assert.That(_completedActivity.ConfigurationChangeSnapshot, Is.Not.Null);
    }

    [Test]
    public async Task CreateApiKeyAsync_CapturesSnapshotWithoutSecretMaterialAsync()
    {
        SetupTrackingSetting(enabled: true);
        SetupHashKeySetting();
        SetupMaxVersion(0);
        ApiKey? persisted = null;
        _apiKeyRepo.Setup(r => r.CreateAsync(It.IsAny<ApiKey>()))
            .Callback<ApiKey>(k => persisted = k)
            .ReturnsAsync((ApiKey k) => k);
        _apiKeyRepo.Setup(r => r.GetByIdAsync(It.IsAny<Guid>())).ReturnsAsync(() => persisted);

        var keyHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("jim_ak_test-secret-value"))).ToLowerInvariant();
        var apiKey = BuildApiKey(keyHash: keyHash, roles: [new Role { Id = 1, Name = "Administrator" }]);
        apiKey.LastUsedAt = DateTime.UtcNow;
        apiKey.LastUsedFromIp = "203.0.113.7";

        await _jim.Security.CreateApiKeyAsync(apiKey, NewUser());

        var snapshot = _completedActivity!.ConfigurationChangeSnapshot;
        Assert.That(snapshot, Is.Not.Null);
        Assert.That(snapshot, Does.Not.Contain(keyHash), "the key hash value must never appear in a snapshot, in any form");
        Assert.That(snapshot!.ToLowerInvariant(), Does.Not.Contain("keyhash"), "no node may be named after the secret field, in any casing");
        Assert.That(snapshot.ToLowerInvariant(), Does.Not.Contain("lastused"), "operational usage state must not be captured, in any casing");
        Assert.That(snapshot, Does.Not.Contain("203.0.113.7"), "the last-used IP address must not be captured");
        Assert.That(snapshot, Does.Contain(apiKey.Name));
        Assert.That(snapshot, Does.Contain(apiKey.KeyPrefix));
        Assert.That(snapshot, Does.Contain("Administrator"));
    }

    [Test]
    public async Task UpdateApiKeyAsync_CapturesVersionedSnapshotWithRoleChangeAsync()
    {
        SetupTrackingSetting(enabled: true);
        SetupHashKeySetting();
        SetupMaxVersion(1);

        var updated = BuildApiKey(id: ApiKeyId, name: "CI Pipeline Key (renewed)", roles: [new Role { Id = 2, Name = "Read Only" }]);
        _apiKeyRepo.Setup(r => r.GetByIdAsync(ApiKeyId)).ReturnsAsync(updated);

        await _jim.Security.UpdateApiKeyAsync(updated, NewUser(), changeReason: "role rotation");

        Assert.That(_completedActivity, Is.Not.Null);
        Assert.That(_completedActivity!.TargetOperationType, Is.EqualTo(ActivityTargetOperationType.Update));
        Assert.That(_completedActivity.ApiKeyId, Is.EqualTo(ApiKeyId));
        Assert.That(_completedActivity.ConfigurationChangeVersion, Is.EqualTo(2), "version is the existing maximum (1) + 1");
        var snapshot = _completedActivity.ConfigurationChangeSnapshot;
        Assert.That(snapshot, Is.Not.Null);
        Assert.That(snapshot, Does.Contain("CI Pipeline Key (renewed)"));
        Assert.That(snapshot, Does.Contain("Read Only"));
        Assert.That(snapshot, Does.Not.Contain("Administrator"), "the replaced role must not still appear in the new version");
    }

    [Test]
    public async Task CreateApiKeyAsync_WhenTrackingDisabled_RecordsActivityButNoSnapshotAsync()
    {
        SetupTrackingSetting(enabled: false);

        var apiKey = BuildApiKey();
        await _jim.Security.CreateApiKeyAsync(apiKey, NewUser(), changeReason: "no tracking");

        Assert.That(_completedActivity, Is.Not.Null);
        Assert.That(_completedActivity!.ConfigurationChangeSnapshot, Is.Null);
        Assert.That(_completedActivity.ConfigurationChangeVersion, Is.Null);
        Assert.That(_completedActivity.ChangeReason, Is.EqualTo("no tracking"));
    }

    [Test]
    public async Task UpdateApiKeyAsync_WhenUnchanged_SkipsVersionAndSnapshotAsync()
    {
        SetupTrackingSetting(enabled: true);
        SetupHashKeySetting();
        var apiKey = BuildApiKey(id: ApiKeyId, roles: [new Role { Id = 1, Name = "Administrator" }]);
        _apiKeyRepo.Setup(r => r.GetByIdAsync(ApiKeyId)).ReturnsAsync(apiKey);
        SetupMaxVersion(3);

        await _jim.Security.UpdateApiKeyAsync(apiKey, NewUser(), changeReason: "first save");
        var storedSnapshot = _completedActivity!.ConfigurationChangeSnapshot;
        Assert.That(storedSnapshot, Is.Not.Null);
        Assert.That(_completedActivity.ConfigurationChangeVersion, Is.EqualTo(4));
        _activityRepo.Setup(r => r.GetLatestConfigurationChangeSnapshotAsync(ActivityTargetType.ApiKey, ApiKeyId))
            .ReturnsAsync(storedSnapshot);
        _completedActivity = null;

        await _jim.Security.UpdateApiKeyAsync(apiKey, NewUser(), changeReason: "resave, nothing changed");

        Assert.That(_completedActivity, Is.Not.Null);
        Assert.That(_completedActivity!.ConfigurationChangeVersion, Is.Null, "an unchanged API key must not consume a version");
        Assert.That(_completedActivity.ConfigurationChangeSnapshot, Is.Null);
        Assert.That(_completedActivity.ApiKeyId, Is.EqualTo(ApiKeyId), "the activity still deep-links to the key when the capture is skipped");
        Assert.That(_completedActivity.ChangeReason, Is.EqualTo("resave, nothing changed"));
    }

    [Test]
    public async Task DeleteApiKeyAsync_RecordsUnversionedUnlinkedTombstoneAsync()
    {
        SetupTrackingSetting(enabled: true);
        SetupHashKeySetting();
        var apiKey = BuildApiKey(id: ApiKeyId, roles: [new Role { Id = 1, Name = "Administrator" }]);
        _apiKeyRepo.Setup(r => r.GetByIdAsync(ApiKeyId)).ReturnsAsync(apiKey);

        await _jim.Security.DeleteApiKeyAsync(ApiKeyId, NewUser(), changeReason: "superseded");

        Assert.That(_completedActivity, Is.Not.Null);
        Assert.That(_completedActivity!.TargetOperationType, Is.EqualTo(ActivityTargetOperationType.Delete));
        Assert.That(_completedActivity.ConfigurationChangeSnapshot, Is.Not.Null, "the tombstone preserves the deleted key's metadata");
        Assert.That(_completedActivity.ConfigurationChangeSnapshot, Does.Contain($"\"objectName\":\"{apiKey.Name}\""));
        Assert.That(_completedActivity.ConfigurationChangeVersion, Is.Null, "deletion tombstones are unversioned");
        Assert.That(_completedActivity.ApiKeyId, Is.Null, "deletion tombstones are unlinked; the key no longer exists");
        Assert.That(_completedActivity.ChangeReason, Is.EqualTo("superseded"));
    }

    [Test]
    public async Task RecordApiKeyUsageAsync_DoesNotRecordActivityOrCaptureAsync()
    {
        _apiKeyRepo.Setup(r => r.RecordUsageAsync(ApiKeyId, "203.0.113.7")).Returns(Task.CompletedTask);

        await _jim.Security.RecordApiKeyUsageAsync(ApiKeyId, "203.0.113.7");

        _activityRepo.Verify(r => r.CreateActivityAsync(It.IsAny<Activity>()), Times.Never);
        _activityRepo.Verify(r => r.UpdateActivityAsync(It.IsAny<Activity>()), Times.Never);
        _apiKeyRepo.Verify(r => r.RecordUsageAsync(ApiKeyId, "203.0.113.7"), Times.Once);
        Assert.That(_completedActivity, Is.Null);
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

    private void SetupMaxVersion(int max) =>
        _activityRepo.Setup(r => r.GetMaxConfigurationChangeVersionAsync(ActivityTargetType.ApiKey, It.IsAny<Guid>()))
            .ReturnsAsync(max);

    private static ApiKey BuildApiKey(Guid? id = null, string? name = null, string? keyHash = null, List<Role>? roles = null) => new()
    {
        Id = id ?? Guid.NewGuid(),
        Name = name ?? "CI Pipeline Key",
        Description = "Used by the nightly build pipeline",
        KeyHash = keyHash ?? DefaultKeyHash,
        KeyPrefix = "jim_ak_7f3a9c2d",
        ExpiresAt = new DateTime(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        IsEnabled = true,
        Roles = roles ?? []
    };

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
