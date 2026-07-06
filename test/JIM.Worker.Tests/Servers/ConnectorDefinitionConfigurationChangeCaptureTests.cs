// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Application.Interfaces;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Staging;
using Moq;
using NUnit.Framework;
using System.Text;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// Tests configuration change-history capture for Connector Definitions: create/update/delete of a definition and its
/// files all record a versioned, metadata-only snapshot keyed by <see cref="Activity.ConnectorDefinitionId"/>, with the
/// same toggle/dedupe/tombstone/best-effort behaviours the shared capture service owns. File mutations roll up into the
/// owning definition's history (the granular sub-entity precedent), and the seed-baseline path records a
/// System-attributed Create child under the seeding parent. Mirrors <c>PredefinedSearchConfigurationChangeCaptureTests</c>.
/// </summary>
[TestFixture]
public class ConnectorDefinitionConfigurationChangeCaptureTests
{
    private const int DefinitionId = 88;

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

        _protection = new FakeProtection();
        _jim = new JimApplication(_repo.Object) { CredentialProtection = _protection };

        // NUnit reuses one fixture instance across every [Test], so reset the captured state explicitly.
        _completedActivity = null;

        SetupTrackingSetting(enabled: true);
        SetupHashKeySetting();
    }

    [TearDown]
    public void TearDown() => _jim?.Dispose();

    // -- CreateConnectorDefinitionAsync -------------------------------------------------------------------------------

    [Test]
    public async Task CreateConnectorDefinitionAsync_SystemInitiated_RecordsCreateActivityWithVersionOneSnapshotAsync()
    {
        var definition = BuildDefinition(DefinitionId, "LDAP");
        _csRepo.Setup(r => r.CreateConnectorDefinitionAsync(It.IsAny<ConnectorDefinition>())).Returns(Task.CompletedTask);
        _csRepo.Setup(r => r.GetConnectorDefinitionAsync(DefinitionId, It.IsAny<bool>())).ReturnsAsync(definition);
        SetupMaxVersion(0);

        await _jim.ConnectedSystems.CreateConnectorDefinitionAsync(definition, ActivityInitiatorType.System, null, "System", changeReason: "new connector");

        Assert.That(_completedActivity, Is.Not.Null);
        Assert.That(_completedActivity!.TargetType, Is.EqualTo(ActivityTargetType.ConnectorDefinition));
        Assert.That(_completedActivity!.TargetOperationType, Is.EqualTo(ActivityTargetOperationType.Create));
        Assert.That(_completedActivity!.InitiatedByType, Is.EqualTo(ActivityInitiatorType.System));
        Assert.That(_completedActivity!.ConnectorDefinitionId, Is.EqualTo(DefinitionId), "the activity must carry the definition id so history is queryable");
        Assert.That(_completedActivity!.ConfigurationChangeVersion, Is.EqualTo(1));
        Assert.That(_completedActivity!.ChangeReason, Is.EqualTo("new connector"));
        Assert.That(_completedActivity!.ConfigurationChangeSnapshot, Does.Contain("\"objectType\":\"ConnectorDefinition\""));
        Assert.That(_completedActivity!.ConfigurationChangeSnapshot, Does.Contain("LDAP"));
    }

    // -- UpdateConnectorDefinitionAsync -------------------------------------------------------------------------------

    [Test]
    public async Task UpdateConnectorDefinitionAsync_SystemInitiated_GroupsUnderParentAndRecordsVersionOneAsync()
    {
        var definition = BuildDefinition(DefinitionId, "LDAP");
        _csRepo.Setup(r => r.UpdateConnectorDefinitionAsync(It.IsAny<ConnectorDefinition>())).Returns(Task.CompletedTask);
        _csRepo.Setup(r => r.GetConnectorDefinitionAsync(DefinitionId, It.IsAny<bool>())).ReturnsAsync(definition);
        SetupMaxVersion(0);
        var parentActivityId = Guid.NewGuid();

        await _jim.ConnectedSystems.UpdateConnectorDefinitionAsync(definition, ActivityInitiatorType.System, null, "System",
            changeReason: "capability drift", parentActivityId: parentActivityId);

        Assert.That(_completedActivity, Is.Not.Null);
        Assert.That(_completedActivity!.TargetOperationType, Is.EqualTo(ActivityTargetOperationType.Update));
        Assert.That(_completedActivity!.ParentActivityId, Is.EqualTo(parentActivityId), "the drift-sync update must group under the seeding parent");
        Assert.That(_completedActivity!.ConnectorDefinitionId, Is.EqualTo(DefinitionId));
        Assert.That(_completedActivity!.ConfigurationChangeVersion, Is.EqualTo(1));
    }

    [Test]
    public async Task UpdateConnectorDefinitionAsync_WhenTrackingDisabled_RecordsActivityButNoSnapshotAsync()
    {
        SetupTrackingSetting(enabled: false);
        var definition = BuildDefinition(DefinitionId, "LDAP");
        _csRepo.Setup(r => r.UpdateConnectorDefinitionAsync(It.IsAny<ConnectorDefinition>())).Returns(Task.CompletedTask);

        await _jim.ConnectedSystems.UpdateConnectorDefinitionAsync(definition, ActivityInitiatorType.System, null, "System", changeReason: "no tracking");

        Assert.That(_completedActivity, Is.Not.Null);
        Assert.That(_completedActivity!.ConfigurationChangeSnapshot, Is.Null);
        Assert.That(_completedActivity!.ConfigurationChangeVersion, Is.Null);
        Assert.That(_completedActivity!.ChangeReason, Is.EqualTo("no tracking"));
    }

    [Test]
    public async Task UpdateConnectorDefinitionAsync_WhenResaveIsUnchanged_SkipsVersionAndSnapshotAsync()
    {
        var definition = BuildDefinition(DefinitionId, "LDAP");
        _csRepo.Setup(r => r.UpdateConnectorDefinitionAsync(It.IsAny<ConnectorDefinition>())).Returns(Task.CompletedTask);
        _csRepo.Setup(r => r.GetConnectorDefinitionAsync(DefinitionId, It.IsAny<bool>())).ReturnsAsync(definition);
        SetupMaxVersion(0);

        await _jim.ConnectedSystems.UpdateConnectorDefinitionAsync(definition, ActivityInitiatorType.System, null, "System", changeReason: "first save");
        var storedSnapshot = _completedActivity!.ConfigurationChangeSnapshot;
        Assert.That(storedSnapshot, Is.Not.Null);
        Assert.That(_completedActivity!.ConfigurationChangeVersion, Is.EqualTo(1));
        _activityRepo.Setup(r => r.GetLatestConfigurationChangeSnapshotAsync(ActivityTargetType.ConnectorDefinition, DefinitionId))
            .ReturnsAsync(storedSnapshot);
        _completedActivity = null;

        await _jim.ConnectedSystems.UpdateConnectorDefinitionAsync(definition, ActivityInitiatorType.System, null, "System", changeReason: "resend, nothing changed");

        Assert.That(_completedActivity, Is.Not.Null);
        Assert.That(_completedActivity!.ConfigurationChangeVersion, Is.Null, "an unchanged save must not consume a version");
        Assert.That(_completedActivity!.ConfigurationChangeSnapshot, Is.Null);
        Assert.That(_completedActivity!.ConnectorDefinitionId, Is.EqualTo(DefinitionId), "the activity still deep-links to the definition when capture is skipped");
    }

    // -- DeleteConnectorDefinitionAsync -------------------------------------------------------------------------------

    [Test]
    public async Task DeleteConnectorDefinitionAsync_RecordsTombstoneSnapshotWithoutVersionAsync()
    {
        var definition = BuildDefinition(DefinitionId, "LDAP");
        _csRepo.Setup(r => r.GetConnectorDefinitionAsync(DefinitionId, It.IsAny<bool>())).ReturnsAsync(definition);
        _csRepo.Setup(r => r.DeleteConnectorDefinitionAsync(It.IsAny<ConnectorDefinition>())).Returns(Task.CompletedTask);

        await _jim.ConnectedSystems.DeleteConnectorDefinitionAsync(definition, ActivityInitiatorType.System, null, "System", changeReason: "removing connector");

        Assert.That(_completedActivity, Is.Not.Null);
        Assert.That(_completedActivity!.TargetOperationType, Is.EqualTo(ActivityTargetOperationType.Delete));
        Assert.That(_completedActivity!.ConfigurationChangeSnapshot, Is.Not.Null, "a delete records a tombstone snapshot");
        Assert.That(_completedActivity!.ConfigurationChangeVersion, Is.Null, "a deletion tombstone consumes no version");
        Assert.That(_completedActivity!.ConnectorDefinitionId, Is.Null, "the tombstone is unlinked; the definition is deleted before completion");
        Assert.That(_completedActivity!.ChangeReason, Is.EqualTo("removing connector"));
    }

    // -- File mutations roll up to the owning definition --------------------------------------------------------------

    [Test]
    public async Task CreateConnectorDefinitionFileAsync_RollsUpToOwningDefinitionHistoryAsync()
    {
        var definition = BuildDefinition(DefinitionId, "File");
        var file = new ConnectorDefinitionFile { Id = 3, Filename = "File.dll", Version = "1.0.0", File = [1, 2, 3], ConnectorDefinition = definition };
        _csRepo.Setup(r => r.CreateConnectorDefinitionFileAsync(file)).Returns(Task.CompletedTask);
        _csRepo.Setup(r => r.GetConnectorDefinitionAsync(DefinitionId, It.IsAny<bool>())).ReturnsAsync(definition);
        SetupMaxVersion(0);

        await _jim.ConnectedSystems.CreateConnectorDefinitionFileAsync(file, ActivityInitiatorType.System, null, "System", changeReason: "uploaded assembly");

        Assert.That(_completedActivity, Is.Not.Null);
        Assert.That(_completedActivity!.TargetType, Is.EqualTo(ActivityTargetType.ConnectorDefinition));
        Assert.That(_completedActivity!.TargetOperationType, Is.EqualTo(ActivityTargetOperationType.Update), "a file change is an update to the owning definition");
        Assert.That(_completedActivity!.ConnectorDefinitionId, Is.EqualTo(DefinitionId), "the file change rolls up to the owning definition's history");
        Assert.That(_completedActivity!.ConfigurationChangeVersion, Is.EqualTo(1));
    }

    // -- RecordSeededConnectorDefinitionBaselineAsync -----------------------------------------------------------------

    [Test]
    public async Task RecordSeededConnectorDefinitionBaselineAsync_RecordsSystemCreateChildWithVersionOneBaselineAsync()
    {
        var definition = BuildDefinition(DefinitionId, "LDAP");
        _csRepo.Setup(r => r.GetConnectorDefinitionAsync(DefinitionId, It.IsAny<bool>())).ReturnsAsync(definition);
        SetupMaxVersion(0);
        var parentActivityId = Guid.NewGuid();

        await _jim.ConnectedSystems.RecordSeededConnectorDefinitionBaselineAsync(DefinitionId, "LDAP", parentActivityId);

        Assert.That(_completedActivity, Is.Not.Null);
        Assert.That(_completedActivity!.TargetType, Is.EqualTo(ActivityTargetType.ConnectorDefinition));
        Assert.That(_completedActivity!.TargetOperationType, Is.EqualTo(ActivityTargetOperationType.Create));
        Assert.That(_completedActivity!.InitiatedByType, Is.EqualTo(ActivityInitiatorType.System));
        Assert.That(_completedActivity!.ParentActivityId, Is.EqualTo(parentActivityId), "the baseline must group under the seeding parent Activity");
        Assert.That(_completedActivity!.ConnectorDefinitionId, Is.EqualTo(DefinitionId));
        Assert.That(_completedActivity!.ConfigurationChangeVersion, Is.EqualTo(1));
        Assert.That(_completedActivity!.ConfigurationChangeSnapshot, Does.Contain("\"objectType\":\"ConnectorDefinition\""));
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
        _activityRepo.Setup(r => r.GetMaxConfigurationChangeVersionAsync(ActivityTargetType.ConnectorDefinition, It.IsAny<int>()))
            .ReturnsAsync(max);

    private static ConnectorDefinition BuildDefinition(int id, string name) => new()
    {
        Id = id,
        Name = name,
        Description = $"{name} connector",
        BuiltIn = true,
        SupportsFullImport = true,
        SupportsExport = true
    };

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
