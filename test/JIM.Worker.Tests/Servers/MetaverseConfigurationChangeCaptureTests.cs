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
using System.Text;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// Tests configuration change-history capture for Metaverse Object Types and Metaverse Attributes: every schema
/// mutation records an Activity carrying a redacted, versioned snapshot (fixing the previously silent
/// <c>UpdateMetaverseObjectTypeAsync</c>, which recorded nothing); snapshots capture the deletion-rule configuration
/// and the attribute/object-type associations; an attribute deletion records an unversioned, unlinked tombstone; and
/// the shared toggle and semantic-dedupe behaviours apply.
/// </summary>
[TestFixture]
public class MetaverseConfigurationChangeCaptureTests
{
    private const int ObjectTypeId = 42;
    private const int AttributeId = 7;

    private Mock<IRepository> _repo = null!;
    private Mock<IActivityRepository> _activityRepo = null!;
    private Mock<IServiceSettingsRepository> _settingsRepo = null!;
    private Mock<IMetaverseRepository> _metaverseRepo = null!;
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
        _metaverseRepo = new Mock<IMetaverseRepository>();
        _repo.Setup(r => r.Activity).Returns(_activityRepo.Object);
        _repo.Setup(r => r.ServiceSettings).Returns(_settingsRepo.Object);
        _repo.Setup(r => r.Metaverse).Returns(_metaverseRepo.Object);

        _activityRepo.Setup(r => r.CreateActivityAsync(It.IsAny<Activity>())).Returns(Task.CompletedTask);
        _activityRepo.Setup(r => r.UpdateActivityAsync(It.IsAny<Activity>()))
            .Callback<Activity>(a => _completedActivity = a)
            .Returns(Task.CompletedTask);
        _metaverseRepo.Setup(r => r.CreateMetaverseObjectTypeAsync(It.IsAny<MetaverseObjectType>())).Returns(Task.CompletedTask);
        _metaverseRepo.Setup(r => r.UpdateMetaverseObjectTypeAsync(It.IsAny<MetaverseObjectType>())).Returns(Task.CompletedTask);
        _metaverseRepo.Setup(r => r.CreateMetaverseAttributeAsync(It.IsAny<MetaverseAttribute>())).Returns(Task.CompletedTask);
        _metaverseRepo.Setup(r => r.UpdateMetaverseAttributeAsync(It.IsAny<MetaverseAttribute>())).Returns(Task.CompletedTask);
        _metaverseRepo.Setup(r => r.CascadeDeleteMetaverseAttributeAsync(It.IsAny<int>())).Returns(Task.CompletedTask);
        _metaverseRepo.Setup(r => r.GetAttributeReferencesAsync(It.IsAny<int>()))
            .ReturnsAsync(new List<JIM.Models.Core.DTOs.AttributeReference>());
        _metaverseRepo.Setup(r => r.GetAttributeValueObjectCountsByTypeAsync(It.IsAny<int>()))
            .ReturnsAsync(new List<JIM.Models.Core.DTOs.AttributeObjectTypeValueCount>());
        _metaverseRepo.Setup(r => r.GetAttributeValueObjectCountAsync(It.IsAny<int>())).ReturnsAsync(0);

        _protection = new FakeProtection();
        _jim = new JimApplication(_repo.Object) { CredentialProtection = _protection };
    }

    [TearDown]
    public void TearDown() => _jim?.Dispose();

    [Test]
    public async Task UpdateMetaverseObjectTypeAsync_UserInitiated_RecordsActivityWithVersionedSnapshotAsync()
    {
        SetupTrackingSetting(enabled: true);
        SetupHashKeySetting();
        var objectType = SetupObjectType(BuildObjectType());
        _activityRepo.Setup(r => r.GetMaxConfigurationChangeVersionAsync(ActivityTargetType.MetaverseObjectType, ObjectTypeId))
            .ReturnsAsync(2);

        await _jim.Metaverse.UpdateMetaverseObjectTypeAsync(objectType, NewUser(), changeReason: "tighten deletion rules");

        Assert.That(_completedActivity, Is.Not.Null, "the update must record an Activity; it previously recorded nothing");
        Assert.That(_completedActivity!.TargetType, Is.EqualTo(ActivityTargetType.MetaverseObjectType));
        Assert.That(_completedActivity!.TargetOperationType, Is.EqualTo(ActivityTargetOperationType.Update));
        Assert.That(_completedActivity!.MetaverseObjectTypeId, Is.EqualTo(ObjectTypeId), "the activity must carry the object type id so history is queryable");
        Assert.That(_completedActivity!.ConfigurationChangeVersion, Is.EqualTo(3), "version is the existing maximum (2) + 1");
        Assert.That(_completedActivity!.ChangeReason, Is.EqualTo("tighten deletion rules"));
        var snapshot = _completedActivity!.ConfigurationChangeSnapshot;
        Assert.That(snapshot, Is.Not.Null);
        Assert.That(snapshot, Does.Contain("\"objectType\":\"MetaverseObjectType\""));
        Assert.That(snapshot, Does.Contain("WhenAuthoritativeSourceDisconnected"), "the deletion rule is configuration");
        Assert.That(snapshot, Does.Contain("30.00:00:00"), "the deletion grace period is configuration");
        Assert.That(snapshot, Does.Contain("Display Name"), "attribute associations are part of the object type's configuration");
    }

    [Test]
    public async Task UpdateMetaverseObjectTypeAsync_ApiKeyInitiated_CapturesTooAsync()
    {
        SetupTrackingSetting(enabled: true);
        SetupHashKeySetting();
        var objectType = SetupObjectType(BuildObjectType());
        SetupObjectTypeMaxVersion(0);

        var apiKey = new ApiKey { Id = Guid.NewGuid(), Name = "prov-api" };
        await _jim.Metaverse.UpdateMetaverseObjectTypeAsync(objectType, apiKey, changeReason: "via API");

        Assert.That(_completedActivity, Is.Not.Null);
        Assert.That(_completedActivity!.MetaverseObjectTypeId, Is.EqualTo(ObjectTypeId));
        Assert.That(_completedActivity!.ConfigurationChangeVersion, Is.EqualTo(1));
        Assert.That(_completedActivity!.ChangeReason, Is.EqualTo("via API"));
    }

    [Test]
    public async Task UpdateMetaverseObjectTypeAsync_WhenTrackingDisabled_RecordsActivityButNoSnapshotAsync()
    {
        SetupTrackingSetting(enabled: false);
        var objectType = SetupObjectType(BuildObjectType());

        await _jim.Metaverse.UpdateMetaverseObjectTypeAsync(objectType, NewUser(), changeReason: "no tracking");

        Assert.That(_completedActivity, Is.Not.Null, "the audit Activity is recorded independently of the snapshot toggle");
        Assert.That(_completedActivity!.ConfigurationChangeSnapshot, Is.Null);
        Assert.That(_completedActivity!.ConfigurationChangeVersion, Is.Null);
        Assert.That(_completedActivity!.ChangeReason, Is.EqualTo("no tracking"));
    }

    [Test]
    public async Task UpdateMetaverseObjectTypeAsync_WhenUnchanged_SkipsVersionAndSnapshotAsync()
    {
        SetupTrackingSetting(enabled: true);
        SetupHashKeySetting();
        var objectType = SetupObjectType(BuildObjectType());
        SetupObjectTypeMaxVersion(3);

        await _jim.Metaverse.UpdateMetaverseObjectTypeAsync(objectType, NewUser());
        var storedSnapshot = _completedActivity!.ConfigurationChangeSnapshot;
        Assert.That(storedSnapshot, Is.Not.Null);
        Assert.That(_completedActivity!.ConfigurationChangeVersion, Is.EqualTo(4));
        _activityRepo.Setup(r => r.GetLatestConfigurationChangeSnapshotAsync(ActivityTargetType.MetaverseObjectType, ObjectTypeId))
            .ReturnsAsync(storedSnapshot);
        _completedActivity = null;

        await _jim.Metaverse.UpdateMetaverseObjectTypeAsync(objectType, NewUser());

        Assert.That(_completedActivity, Is.Not.Null);
        Assert.That(_completedActivity!.ConfigurationChangeVersion, Is.Null, "an unchanged Metaverse Object Type must not consume a version");
        Assert.That(_completedActivity!.ConfigurationChangeSnapshot, Is.Null);
        Assert.That(_completedActivity!.MetaverseObjectTypeId, Is.EqualTo(ObjectTypeId), "the activity still deep-links to the object type when the capture is skipped");
    }

    [Test]
    public async Task CreateMetaverseObjectTypeAsync_CapturesVersionOneSnapshotAsync()
    {
        SetupTrackingSetting(enabled: true);
        SetupHashKeySetting();
        var objectType = BuildObjectType();
        SetupObjectType(objectType);
        SetupObjectTypeMaxVersion(0);

        await _jim.Metaverse.CreateMetaverseObjectTypeAsync(objectType, NewUser(), changeReason: "new schema type");

        Assert.That(_completedActivity, Is.Not.Null);
        Assert.That(_completedActivity!.TargetOperationType, Is.EqualTo(ActivityTargetOperationType.Create));
        Assert.That(_completedActivity!.MetaverseObjectTypeId, Is.EqualTo(ObjectTypeId));
        Assert.That(_completedActivity!.ConfigurationChangeVersion, Is.EqualTo(1));
        Assert.That(_completedActivity!.ChangeReason, Is.EqualTo("new schema type"));
        Assert.That(_completedActivity!.ConfigurationChangeSnapshot, Does.Contain("\"objectName\":\"Robot\""));
    }

    [Test]
    public async Task UpdateMetaverseAttributeAsync_CapturesSnapshotWithObjectTypeAssociationsAsync()
    {
        SetupTrackingSetting(enabled: true);
        SetupHashKeySetting();
        var attribute = SetupAttribute(BuildAttribute());
        _activityRepo.Setup(r => r.GetMaxConfigurationChangeVersionAsync(ActivityTargetType.MetaverseAttribute, AttributeId))
            .ReturnsAsync(1);

        await _jim.Metaverse.UpdateMetaverseAttributeAsync(attribute, NewUser(), changeReason: "widen to groups");

        Assert.That(_completedActivity, Is.Not.Null);
        Assert.That(_completedActivity!.TargetType, Is.EqualTo(ActivityTargetType.MetaverseAttribute));
        Assert.That(_completedActivity!.MetaverseAttributeId, Is.EqualTo(AttributeId), "the activity must carry the attribute id so history is queryable");
        Assert.That(_completedActivity!.ConfigurationChangeVersion, Is.EqualTo(2));
        Assert.That(_completedActivity!.ChangeReason, Is.EqualTo("widen to groups"));
        var snapshot = _completedActivity!.ConfigurationChangeSnapshot;
        Assert.That(snapshot, Is.Not.Null);
        Assert.That(snapshot, Does.Contain("\"objectType\":\"MetaverseAttribute\""));
        Assert.That(snapshot, Does.Contain("Text"), "the data type is configuration");
        Assert.That(snapshot, Does.Contain("SingleValued"), "the plurality is configuration");
        Assert.That(snapshot, Does.Contain("Robot"), "object type associations are part of the attribute's configuration");
    }

    [Test]
    public async Task CreateMetaverseAttributeAsync_CapturesVersionOneSnapshotAsync()
    {
        SetupTrackingSetting(enabled: true);
        SetupHashKeySetting();
        var attribute = BuildAttribute();
        SetupAttribute(attribute);
        _activityRepo.Setup(r => r.GetMaxConfigurationChangeVersionAsync(ActivityTargetType.MetaverseAttribute, AttributeId))
            .ReturnsAsync(0);

        await _jim.Metaverse.CreateMetaverseAttributeAsync(attribute, NewUser());

        Assert.That(_completedActivity, Is.Not.Null);
        Assert.That(_completedActivity!.TargetOperationType, Is.EqualTo(ActivityTargetOperationType.Create));
        Assert.That(_completedActivity!.MetaverseAttributeId, Is.EqualTo(AttributeId));
        Assert.That(_completedActivity!.ConfigurationChangeVersion, Is.EqualTo(1));
        Assert.That(_completedActivity!.ConfigurationChangeSnapshot, Does.Contain("\"objectName\":\"Serial Number\""));
    }

    [Test]
    public async Task DeleteMetaverseAttributeWithCascadeAsync_RecordsUnversionedUnlinkedTombstoneAsync()
    {
        SetupTrackingSetting(enabled: true);
        SetupHashKeySetting();
        var attribute = SetupAttribute(BuildAttribute());

        await _jim.Metaverse.DeleteMetaverseAttributeWithCascadeAsync(attribute, NewUser(), changeReason: "no longer needed");

        Assert.That(_completedActivity, Is.Not.Null);
        Assert.That(_completedActivity!.TargetOperationType, Is.EqualTo(ActivityTargetOperationType.Delete));
        Assert.That(_completedActivity!.ConfigurationChangeSnapshot, Is.Not.Null, "the tombstone preserves the deleted attribute's configuration");
        Assert.That(_completedActivity!.ConfigurationChangeSnapshot, Does.Contain("\"objectName\":\"Serial Number\""));
        Assert.That(_completedActivity!.ConfigurationChangeVersion, Is.Null, "deletion tombstones are unversioned");
        Assert.That(_completedActivity!.MetaverseAttributeId, Is.Null, "deletion tombstones are unlinked; the attribute no longer exists");
        Assert.That(_completedActivity!.ChangeReason, Is.EqualTo("no longer needed"));
    }

    // -- RecordSeededMetaverseObjectTypeBaselineAsync / RecordSeededMetaverseAttributeBaselineAsync --------------------

    [Test]
    public async Task RecordSeededMetaverseObjectTypeBaselineAsync_RecordsSystemCreateChildWithVersionOneBaselineAsync()
    {
        SetupTrackingSetting(enabled: true);
        SetupHashKeySetting();
        SetupObjectType(BuildObjectType());
        SetupObjectTypeMaxVersion(0);
        var parentActivityId = Guid.NewGuid();

        await _jim.Metaverse.RecordSeededMetaverseObjectTypeBaselineAsync(ObjectTypeId, "User", parentActivityId);

        Assert.That(_completedActivity, Is.Not.Null);
        Assert.That(_completedActivity!.TargetType, Is.EqualTo(ActivityTargetType.MetaverseObjectType));
        Assert.That(_completedActivity!.TargetOperationType, Is.EqualTo(ActivityTargetOperationType.Create));
        Assert.That(_completedActivity!.InitiatedByType, Is.EqualTo(ActivityInitiatorType.System));
        Assert.That(_completedActivity!.ParentActivityId, Is.EqualTo(parentActivityId), "the baseline must group under the seeding parent Activity");
        Assert.That(_completedActivity!.MetaverseObjectTypeId, Is.EqualTo(ObjectTypeId));
        Assert.That(_completedActivity!.ConfigurationChangeVersion, Is.EqualTo(1));
        Assert.That(_completedActivity!.ConfigurationChangeSnapshot, Does.Contain("\"objectType\":\"MetaverseObjectType\""));
    }

    [Test]
    public async Task RecordSeededMetaverseAttributeBaselineAsync_RecordsSystemCreateChildWithVersionOneBaselineAsync()
    {
        SetupTrackingSetting(enabled: true);
        SetupHashKeySetting();
        SetupAttribute(BuildAttribute());
        _activityRepo.Setup(r => r.GetMaxConfigurationChangeVersionAsync(ActivityTargetType.MetaverseAttribute, It.IsAny<int>()))
            .ReturnsAsync(0);
        var parentActivityId = Guid.NewGuid();

        await _jim.Metaverse.RecordSeededMetaverseAttributeBaselineAsync(AttributeId, "Serial Number", parentActivityId);

        Assert.That(_completedActivity, Is.Not.Null);
        Assert.That(_completedActivity!.TargetType, Is.EqualTo(ActivityTargetType.MetaverseAttribute));
        Assert.That(_completedActivity!.TargetOperationType, Is.EqualTo(ActivityTargetOperationType.Create));
        Assert.That(_completedActivity!.InitiatedByType, Is.EqualTo(ActivityInitiatorType.System));
        Assert.That(_completedActivity!.ParentActivityId, Is.EqualTo(parentActivityId), "the baseline must group under the seeding parent Activity");
        Assert.That(_completedActivity!.MetaverseAttributeId, Is.EqualTo(AttributeId));
        Assert.That(_completedActivity!.ConfigurationChangeVersion, Is.EqualTo(1));
        Assert.That(_completedActivity!.ConfigurationChangeSnapshot, Does.Contain("\"objectType\":\"MetaverseAttribute\""));
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

    private MetaverseObjectType SetupObjectType(MetaverseObjectType objectType)
    {
        _metaverseRepo.Setup(r => r.GetMetaverseObjectTypeAsync(objectType.Id, true)).ReturnsAsync(objectType);
        return objectType;
    }

    private MetaverseAttribute SetupAttribute(MetaverseAttribute attribute)
    {
        _metaverseRepo.Setup(r => r.GetMetaverseAttributeWithObjectTypesAsync(attribute.Id, false)).ReturnsAsync(attribute);
        return attribute;
    }

    private void SetupObjectTypeMaxVersion(int max) =>
        _activityRepo.Setup(r => r.GetMaxConfigurationChangeVersionAsync(ActivityTargetType.MetaverseObjectType, It.IsAny<int>()))
            .ReturnsAsync(max);

    private static MetaverseObjectType BuildObjectType() => new()
    {
        Id = ObjectTypeId,
        Name = "Robot",
        PluralName = "Robots",
        BuiltIn = false,
        Icon = "SmartToy",
        DeletionRule = MetaverseObjectDeletionRule.WhenAuthoritativeSourceDisconnected,
        DeletionGracePeriod = TimeSpan.FromDays(30),
        DeletionTriggerConnectedSystemIds = new List<int> { 3 },
        Attributes = new List<MetaverseAttribute>
        {
            new() { Id = 1, Name = "Display Name", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.SingleValued }
        }
    };

    private static MetaverseAttribute BuildAttribute() => new()
    {
        Id = AttributeId,
        Name = "Serial Number",
        Type = AttributeDataType.Text,
        AttributePlurality = AttributePlurality.SingleValued,
        BuiltIn = false,
        MetaverseObjectTypes = new List<MetaverseObjectType>
        {
            new() { Id = ObjectTypeId, Name = "Robot", PluralName = "Robots" }
        }
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
