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
/// Tests that granular Synchronisation Rule sub-entity mutations (adding, updating and removing an Attribute Flow
/// mapping) capture a complete, versioned configuration snapshot of the parent rule. Without this, a rule built up
/// from individual mapping calls (the API / PowerShell path) drifts from its captured history, so a later whole-rule
/// save reports the pre-existing mappings as freshly "added" (the reported defect). The snapshot is taken from the
/// rule reloaded from the repository, never the caller's partial in-memory graph.
/// </summary>
[TestFixture]
public class ConfigurationChangeCaptureGranularTests
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

        // The capture reloads the full parent rule from the repository so the snapshot reflects persisted truth, not
        // the caller's partial graph. Return a rule that already carries a mapping so the snapshot contains it.
        _csRepo.Setup(r => r.GetSyncRuleAsync(55)).ReturnsAsync(BuildRuleWithMapping);
        _csRepo.Setup(r => r.CreateSyncRuleMappingAsync(It.IsAny<SyncRuleMapping>())).Returns(Task.CompletedTask);
        _csRepo.Setup(r => r.UpdateSyncRuleMappingAsync(It.IsAny<SyncRuleMapping>())).Returns(Task.CompletedTask);
        _csRepo.Setup(r => r.DeleteSyncRuleMappingAsync(It.IsAny<SyncRuleMapping>())).Returns(Task.CompletedTask);

        _protection = new FakeProtection();
        _jim = new JimApplication(_repo.Object) { CredentialProtection = _protection };

        SetupTrackingSetting(enabled: true);
        SetupHashKeySetting();
    }

    [TearDown]
    public void TearDown() => _jim?.Dispose();

    [Test]
    public async Task CreateSyncRuleMappingAsync_WhenTrackingEnabled_CapturesVersionedSnapshotOfWholeRuleAsync()
    {
        await _jim.ConnectedSystems.CreateSyncRuleMappingAsync(NewMappingFor(55), NewApiKey());

        AssertCapturedRuleVersion();
    }

    [Test]
    public async Task UpdateSyncRuleMappingAsync_WhenTrackingEnabled_CapturesVersionedSnapshotOfWholeRuleAsync()
    {
        await _jim.ConnectedSystems.UpdateSyncRuleMappingAsync(NewMappingFor(55), NewUser());

        AssertCapturedRuleVersion();
    }

    [Test]
    public async Task DeleteSyncRuleMappingAsync_WhenTrackingEnabled_CapturesVersionedSnapshotOfWholeRuleAsync()
    {
        await _jim.ConnectedSystems.DeleteSyncRuleMappingAsync(NewMappingFor(55), NewApiKey());

        AssertCapturedRuleVersion();
    }

    [Test]
    public async Task UpdateObjectTypeAsync_WhenTrackingEnabled_CapturesVersionedSnapshotOfConnectedSystemAsync()
    {
        _csRepo.Setup(r => r.GetConnectedSystemAsync(3, It.IsAny<bool>())).ReturnsAsync(BuildConnectedSystem);
        _csRepo.Setup(r => r.UpdateObjectTypeAsync(It.IsAny<ConnectedSystemObjectType>())).Returns(Task.CompletedTask);

        var objectType = new ConnectedSystemObjectType
        {
            Id = 7, Name = "user", ConnectedSystemId = 3, ConnectedSystem = new ConnectedSystem { Id = 3, Name = "AD" }
        };

        await _jim.ConnectedSystems.UpdateObjectTypeAsync(objectType, NewUser());

        Assert.That(_completedActivity, Is.Not.Null);
        Assert.That(_completedActivity!.ConfigurationChangeVersion, Is.EqualTo(7));
        Assert.That(_completedActivity.ConnectedSystemId, Is.EqualTo(3), "the activity must carry the Connected System id so the change is queryable in its history");
        Assert.That(_completedActivity.ConfigurationChangeSnapshot, Does.Contain("\"objectType\":\"ConnectedSystem\""));
    }

    [Test]
    public async Task CreateObjectMatchingRuleAsync_ObjectTypeAttached_CapturesConnectedSystemSnapshotAsync()
    {
        // A Simple Mode Object Matching Rule attaches to a Connected System Object Type, not a Synchronisation Rule; it
        // is Connected System configuration, so the change must be captured in the system's history (the reported gap:
        // these previously captured nothing at all).
        _csRepo.Setup(r => r.GetConnectedSystemAsync(3, It.IsAny<bool>())).ReturnsAsync(BuildConnectedSystem);
        _csRepo.Setup(r => r.CreateObjectMatchingRuleAsync(It.IsAny<ObjectMatchingRule>())).Returns(Task.CompletedTask);
        var rule = new ObjectMatchingRule
        {
            Id = 9,
            ConnectedSystemObjectTypeId = 7,
            ConnectedSystemObjectType = new ConnectedSystemObjectType
            {
                Id = 7, Name = "user", ConnectedSystemId = 3, ConnectedSystem = new ConnectedSystem { Id = 3, Name = "AD" }
            }
        };

        await _jim.ConnectedSystems.CreateObjectMatchingRuleAsync(rule, NewApiKey());

        Assert.That(_completedActivity, Is.Not.Null);
        Assert.That(_completedActivity!.ConfigurationChangeVersion, Is.EqualTo(7));
        Assert.That(_completedActivity.ConnectedSystemId, Is.EqualTo(3), "the activity must carry the Connected System id so the change is queryable in its history and the target can link to the Matching tab");
        Assert.That(_completedActivity.ConfigurationChangeSnapshot, Does.Contain("\"objectType\":\"ConnectedSystem\""));
    }

    [Test]
    public async Task DeleteObjectMatchingRuleAsync_SyncRuleNavigationOnly_CapturesSyncRuleSnapshotAsync()
    {
        // The rule arrives with only the SyncRule navigation populated (no scalar FK); capture must still resolve the
        // owning Synchronisation Rule rather than silently skipping.
        _csRepo.Setup(r => r.DeleteObjectMatchingRuleAsync(It.IsAny<ObjectMatchingRule>())).Returns(Task.CompletedTask);
        var rule = new ObjectMatchingRule { Id = 9, SyncRule = new SyncRule { Id = 55, Name = "AD Export" } };

        await _jim.ConnectedSystems.DeleteObjectMatchingRuleAsync(rule, NewUser());

        AssertCapturedRuleVersion();
    }

    [Test]
    public async Task CreateSyncRuleMappingAsync_WhenTrackingDisabled_RecordsNoSnapshotAsync()
    {
        SetupTrackingSetting(enabled: false);

        await _jim.ConnectedSystems.CreateSyncRuleMappingAsync(NewMappingFor(55), NewApiKey());

        Assert.That(_completedActivity, Is.Not.Null);
        Assert.That(_completedActivity!.ConfigurationChangeSnapshot, Is.Null);
        Assert.That(_completedActivity.ConfigurationChangeVersion, Is.Null);
    }

    // -- helpers -------------------------------------------------------------------------------------------------------

    private void AssertCapturedRuleVersion()
    {
        Assert.That(_completedActivity, Is.Not.Null);
        Assert.That(_completedActivity!.ConfigurationChangeVersion, Is.EqualTo(7), "version is the existing maximum (6) + 1");
        Assert.That(_completedActivity.SyncRuleId, Is.EqualTo(55), "the activity must carry the rule id so the change is queryable in the rule's history");
        Assert.That(_completedActivity.ConfigurationChangeSnapshot, Is.Not.Null);
        Assert.That(_completedActivity.ConfigurationChangeSnapshot, Does.Contain("\"objectType\":\"SynchronisationRule\""));
        Assert.That(_completedActivity.ConfigurationChangeSnapshot, Does.Contain("attributeFlowRule"),
            "the snapshot must reflect the rule's mappings, captured from the reloaded rule rather than the caller's partial graph");
    }

    private static SyncRuleMapping NewMappingFor(int syncRuleId) => new()
    {
        Id = 0,
        SyncRule = new SyncRule { Id = syncRuleId, Name = "AD Export" },
        SyncRuleId = syncRuleId,
        TargetConnectedSystemAttribute = new ConnectedSystemObjectTypeAttribute
        {
            Id = 9, Name = "displayName", Type = AttributeDataType.Text, Writability = AttributeWritability.Writable
        }
    };

    // A fully-loaded export rule that already carries one Attribute Flow mapping, so the captured snapshot is complete.
    private static SyncRule BuildRuleWithMapping() => new()
    {
        Id = 55,
        Name = "AD Export",
        Direction = SyncRuleDirection.Export,
        ConnectedSystemId = 3,
        ConnectedSystemObjectTypeId = 7,
        MetaverseObjectTypeId = 1,
        AttributeFlowRules =
        [
            new SyncRuleMapping { Id = 101, TargetConnectedSystemAttributeId = 9 }
        ]
    };

    // A Connected System with initialised (empty) configuration collections, so the snapshot builder has a complete
    // graph to walk when capture reloads it.
    private static ConnectedSystem BuildConnectedSystem() => new()
    {
        Id = 3,
        Name = "AD",
        SettingValues = [],
        RunProfiles = [],
        ObjectTypes = [],
        Partitions = []
    };

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

    private static ApiKey NewApiKey() => new() { Id = Guid.NewGuid(), Name = "test-key" };

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
