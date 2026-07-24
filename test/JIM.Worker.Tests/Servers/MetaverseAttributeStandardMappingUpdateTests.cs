// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

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
/// Tests the audited Standard Mappings update for custom Metaverse Attributes (issue #1104 Phase 2 UI).
/// Administrators may edit the advisory Standard Mappings of custom attributes only; built-in attributes'
/// mappings are maintained by the built-in schema synchronisation pass and must be rejected. The update runs
/// through the audited path (an Update Activity with a versioned configuration snapshot that now includes the
/// mappings), mirroring the rename flow.
/// </summary>
[TestFixture]
public class MetaverseAttributeStandardMappingUpdateTests
{
    private const int AttributeId = 42;

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

        _completedActivity = null;
        _activityRepo.Setup(r => r.CreateActivityAsync(It.IsAny<Activity>())).Returns(Task.CompletedTask);
        _activityRepo.Setup(r => r.UpdateActivityAsync(It.IsAny<Activity>()))
            .Callback<Activity>(a => _completedActivity = a)
            .Returns(Task.CompletedTask);
        _activityRepo.Setup(r => r.GetMaxConfigurationChangeVersionAsync(ActivityTargetType.MetaverseAttribute, AttributeId))
            .ReturnsAsync(1);

        _protection = new FakeProtection();
        _jim = new JimApplication(_repo.Object) { CredentialProtection = _protection };

        SetupTrackingSetting(enabled: true);
        SetupHashKeySetting();
    }

    [TearDown]
    public void TearDown() => _jim?.Dispose();

    [Test]
    public async Task UpdateMetaverseAttributeStandardMappingsAsync_CustomAttribute_ReconcilesAndRecordsAuditedUpdateAsync()
    {
        var attribute = SetupAttribute(BuildCustomAttribute());
        attribute.StandardMappings.Add(new MetaverseAttributeStandardMapping { Id = 1, MetaverseAttributeId = AttributeId, Standard = AttributeStandard.Ldap, CounterpartName = "oldName", Notes = "stale" });
        attribute.StandardMappings.Add(new MetaverseAttributeStandardMapping { Id = 2, MetaverseAttributeId = AttributeId, Standard = AttributeStandard.Scim, CounterpartName = "costCenter", Notes = null });

        var requested = new List<MetaverseAttributeStandardMapping>
        {
            new() { Standard = AttributeStandard.Scim, CounterpartName = "costCenter", Notes = "SCIM Enterprise User extension." },
            new() { Standard = AttributeStandard.Jim, CounterpartName = "Cost Centre" }
        };

        await _jim.Metaverse.UpdateMetaverseAttributeStandardMappingsAsync(AttributeId, requested, NewUser(), "align with finance system");

        // the tracked collection must be reconciled in place: stale removed, kept updated, new added
        Assert.That(attribute.StandardMappings, Has.Count.EqualTo(2));
        Assert.That(attribute.StandardMappings.Any(m => m.CounterpartName == "oldName"), Is.False, "a mapping absent from the request must be removed");
        var kept = attribute.StandardMappings.Single(m => m.Standard == AttributeStandard.Scim && m.CounterpartName == "costCenter");
        Assert.That(kept.Id, Is.EqualTo(2), "a mapping retained by the request must keep its identity, not be deleted and recreated");
        Assert.That(kept.Notes, Is.EqualTo("SCIM Enterprise User extension."), "notes must be updated on retained mappings");
        Assert.That(attribute.StandardMappings.Any(m => m.Standard == AttributeStandard.Jim && m.CounterpartName == "Cost Centre"), Is.True);

        _metaverseRepo.Verify(r => r.UpdateMetaverseAttributeAsync(attribute), Times.Once, "the change must be persisted through the attribute update path");

        // audited: an Update Activity with an incremented, mappings-inclusive configuration snapshot
        Assert.That(_completedActivity, Is.Not.Null);
        Assert.That(_completedActivity!.TargetType, Is.EqualTo(ActivityTargetType.MetaverseAttribute));
        Assert.That(_completedActivity!.TargetOperationType, Is.EqualTo(ActivityTargetOperationType.Update));
        Assert.That(_completedActivity!.ConfigurationChangeVersion, Is.EqualTo(2));
        Assert.That(_completedActivity!.ChangeReason, Is.EqualTo("align with finance system"));
        Assert.That(_completedActivity!.ConfigurationChangeSnapshot, Does.Contain("standardMappings"),
            "the configuration snapshot must include the Standard Mappings so edits are diffable in change history");
        Assert.That(_completedActivity!.ConfigurationChangeSnapshot, Does.Contain("costCenter"));
    }

    [Test]
    public void UpdateMetaverseAttributeStandardMappingsAsync_BuiltInAttribute_ThrowsAsync()
    {
        var attribute = BuildCustomAttribute();
        attribute.BuiltIn = true;
        SetupAttribute(attribute);

        Assert.ThrowsAsync<InvalidOperationException>(() =>
            _jim.Metaverse.UpdateMetaverseAttributeStandardMappingsAsync(AttributeId, new List<MetaverseAttributeStandardMapping>(), NewUser()));
        _metaverseRepo.Verify(r => r.UpdateMetaverseAttributeAsync(It.IsAny<MetaverseAttribute>()), Times.Never);
        Assert.That(_completedActivity, Is.Null, "no Activity must be recorded for a rejected update");
    }

    [Test]
    public void UpdateMetaverseAttributeStandardMappingsAsync_AttributeNotFound_ThrowsAsync()
    {
        Assert.ThrowsAsync<ArgumentException>(() =>
            _jim.Metaverse.UpdateMetaverseAttributeStandardMappingsAsync(AttributeId, new List<MetaverseAttributeStandardMapping>(), NewUser()));
    }

    [TestCase(AttributeStandard.NotSet, "something", TestName = "UpdateMetaverseAttributeStandardMappingsAsync_StandardNotSet_ThrowsAsync")]
    [TestCase(AttributeStandard.Scim, "", TestName = "UpdateMetaverseAttributeStandardMappingsAsync_EmptyCounterpartName_ThrowsAsync")]
    [TestCase(AttributeStandard.Scim, "   ", TestName = "UpdateMetaverseAttributeStandardMappingsAsync_WhitespaceCounterpartName_ThrowsAsync")]
    public void UpdateMetaverseAttributeStandardMappingsAsync_InvalidMapping_ThrowsAsync(AttributeStandard standard, string counterpartName)
    {
        SetupAttribute(BuildCustomAttribute());
        var requested = new List<MetaverseAttributeStandardMapping> { new() { Standard = standard, CounterpartName = counterpartName } };

        Assert.ThrowsAsync<ArgumentException>(() =>
            _jim.Metaverse.UpdateMetaverseAttributeStandardMappingsAsync(AttributeId, requested, NewUser()));
        _metaverseRepo.Verify(r => r.UpdateMetaverseAttributeAsync(It.IsAny<MetaverseAttribute>()), Times.Never);
    }

    [Test]
    public void UpdateMetaverseAttributeStandardMappingsAsync_DuplicateMappings_ThrowsAsync()
    {
        SetupAttribute(BuildCustomAttribute());
        var requested = new List<MetaverseAttributeStandardMapping>
        {
            new() { Standard = AttributeStandard.Ldap, CounterpartName = "costCentre" },
            new() { Standard = AttributeStandard.Ldap, CounterpartName = "costCentre", Notes = "duplicate" }
        };

        Assert.ThrowsAsync<ArgumentException>(() =>
            _jim.Metaverse.UpdateMetaverseAttributeStandardMappingsAsync(AttributeId, requested, NewUser()));
        _metaverseRepo.Verify(r => r.UpdateMetaverseAttributeAsync(It.IsAny<MetaverseAttribute>()), Times.Never);
    }

    // -- helpers -------------------------------------------------------------------------------------------------------

    private MetaverseAttribute SetupAttribute(MetaverseAttribute attribute)
    {
        _metaverseRepo.Setup(r => r.GetMetaverseAttributeWithObjectTypesAsync(AttributeId, It.IsAny<bool>())).ReturnsAsync(attribute);
        return attribute;
    }

    private static MetaverseAttribute BuildCustomAttribute() => new()
    {
        Id = AttributeId,
        Name = "Cost Centre",
        Type = AttributeDataType.Text,
        AttributePlurality = AttributePlurality.SingleValued,
        BuiltIn = false,
        MetaverseObjectTypes = new List<MetaverseObjectType>
        {
            new() { Id = 1, Name = "User", PluralName = "Users" }
        }
    };

    private static MetaverseObject NewUser() => new() { Id = Guid.NewGuid(), CachedDisplayName = "Admin User" };

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