// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Text;
using JIM.Application;
using JIM.Application.Interfaces;
using JIM.Connectors;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Interfaces;
using JIM.Models.Logic;
using JIM.Models.Security;
using JIM.Models.Staging;
using Moq;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// Tests that close the configuration-change capture coverage gaps: every code path that mutates a Connected System's
/// configuration must record a versioned snapshot on its Activity (schema updates, object-matching mode switches,
/// container auto-selection, bulk attribute updates and the partition/container API endpoints), while runtime-state
/// writes (a status flip, the import watermark) must not pollute the configuration history. Also covers the
/// idempotent capture guard: a capture whose snapshot is identical to the object's latest stored one records no new
/// version, so worker paths can capture liberally without generating no-change noise.
/// </summary>
[TestFixture]
public class ConfigurationChangeCaptureCoverageTests
{
    private Mock<IRepository> _repo = null!;
    private Mock<IActivityRepository> _activityRepo = null!;
    private Mock<IServiceSettingsRepository> _settingsRepo = null!;
    private Mock<IConnectedSystemRepository> _csRepo = null!;
    private FakeProtection _protection = null!;
    private JimApplication _jim = null!;
    private Activity? _createdActivity;
    private Activity? _completedActivity;

    [SetUp]
    public void SetUp()
    {
        TestUtilities.SetEnvironmentVariables();

        _createdActivity = null;
        _completedActivity = null;

        _repo = new Mock<IRepository>();
        _activityRepo = new Mock<IActivityRepository>();
        _settingsRepo = new Mock<IServiceSettingsRepository>();
        _csRepo = new Mock<IConnectedSystemRepository>();
        _repo.Setup(r => r.Activity).Returns(_activityRepo.Object);
        _repo.Setup(r => r.ServiceSettings).Returns(_settingsRepo.Object);
        _repo.Setup(r => r.ConnectedSystems).Returns(_csRepo.Object);

        _activityRepo.Setup(r => r.CreateActivityAsync(It.IsAny<Activity>()))
            .Callback<Activity>(a => _createdActivity = a)
            .Returns(Task.CompletedTask);
        _activityRepo.Setup(r => r.UpdateActivityAsync(It.IsAny<Activity>()))
            .Callback<Activity>(a => _completedActivity = a)
            .Returns(Task.CompletedTask);
        _activityRepo.Setup(r => r.GetMaxConfigurationChangeVersionAsync(It.IsAny<ActivityTargetType>(), It.IsAny<int>()))
            .ReturnsAsync(6);
        _activityRepo.Setup(r => r.GetLatestConfigurationChangeSnapshotAsync(It.IsAny<ActivityTargetType>(), It.IsAny<int>()))
            .ReturnsAsync((string?)null);

        _csRepo.Setup(r => r.UpdateConnectedSystemAsync(It.IsAny<ConnectedSystem>())).Returns(Task.CompletedTask);
        _csRepo.Setup(r => r.UpdateConnectedSystemSchemaAsync(It.IsAny<ConnectedSystem>())).Returns(Task.CompletedTask);
        _csRepo.Setup(r => r.GetConnectedSystemAsync(1, It.IsAny<bool>())).ReturnsAsync(BuildConnectedSystem);

        _protection = new FakeProtection();
        _jim = new JimApplication(_repo.Object) { CredentialProtection = _protection };

        SetupTrackingSetting(enabled: true);
        SetupHashKeySetting();
    }

    [TearDown]
    public void TearDown() => _jim?.Dispose();

    // -- gap paths: an Activity existed but no snapshot was captured ------------------------------------------------------

    [Test]
    public async Task UpdateConnectedSystemSchemaAsync_WhenTrackingEnabled_CapturesVersionedSnapshotAsync()
    {
        var connectedSystem = BuildConnectedSystem();

        await _jim.ConnectedSystems.UpdateConnectedSystemSchemaAsync(connectedSystem, NewUser());

        AssertCapturedConnectedSystemVersion();
    }

    [Test]
    public async Task SwitchObjectMatchingModeAsync_WhenTrackingEnabled_CapturesVersionedSnapshotAsync()
    {
        var connectedSystem = BuildConnectedSystem();
        connectedSystem.ObjectMatchingRuleMode = ObjectMatchingRuleMode.ConnectedSystem;
        _csRepo.Setup(r => r.GetSyncRulesAsync(1, true, It.IsAny<bool>())).ReturnsAsync([]);

        var result = await _jim.ConnectedSystems.SwitchObjectMatchingModeAsync(
            connectedSystem, ObjectMatchingRuleMode.SyncRule, NewUser());

        Assert.That(result.Success, Is.True);
        AssertCapturedConnectedSystemVersion();
    }

    [Test]
    public async Task RefreshAndAutoSelectContainersWithTriadAsync_WhenContainersAdded_CapturesVersionedSnapshotAsync()
    {
        var connectedSystem = BuildConnectedSystem();
        connectedSystem.Partitions =
        [
            new ConnectedSystemPartition
            {
                Id = 20, ExternalId = "DC=corp,DC=local", Name = "corp.local", Selected = true,
                Containers = []
            }
        ];

        await _jim.ConnectedSystems.RefreshAndAutoSelectContainersWithTriadAsync(
            connectedSystem, new FakeContainerCreatingConnector(), ["OU=NewTeam,DC=corp,DC=local"],
            ActivityInitiatorType.System, null, "Infrastructure Key");

        AssertCapturedConnectedSystemVersion();
        Assert.That(_completedActivity!.Message, Does.StartWith("Auto-selected 1 container(s)"));
    }

    [Test]
    public async Task BulkUpdateAttributesAsync_WhenTrackingEnabled_CapturesVersionedSnapshotAsync()
    {
        var connectedSystem = BuildConnectedSystem();
        var objectType = new ConnectedSystemObjectType
        {
            Id = 7,
            Name = "user",
            ConnectedSystemId = 1,
            Attributes = [new ConnectedSystemObjectTypeAttribute { Id = 9, Name = "displayName", Selected = false }]
        };
        _csRepo.Setup(r => r.UpdateAttributesAsync(It.IsAny<List<ConnectedSystemObjectTypeAttribute>>()))
            .Returns(Task.CompletedTask);

        await _jim.ConnectedSystems.BulkUpdateAttributesAsync(
            connectedSystem, objectType,
            new Dictionary<int, (bool? Selected, bool? IsExternalId, bool? IsSecondaryExternalId)> { [9] = (true, null, null) },
            NewUser());

        AssertCapturedConnectedSystemVersion();
    }

    // -- gap paths: no Activity at all (the REST API partition/container endpoints) ---------------------------------------

    [Test]
    public async Task UpdateConnectedSystemPartitionAsync_WithInitiator_CreatesActivityAndCapturesSnapshotAsync()
    {
        var partition = new ConnectedSystemPartition { Id = 20, ExternalId = "DC=corp,DC=local", Name = "corp.local", Selected = true };
        _csRepo.Setup(r => r.UpdateConnectedSystemPartitionAsync(partition)).Returns(Task.CompletedTask);
        _csRepo.Setup(r => r.GetConnectedSystemCoreAsync(1, It.IsAny<bool>())).ReturnsAsync(BuildConnectedSystem);

        await _jim.ConnectedSystems.UpdateConnectedSystemPartitionAsync(partition, 1, NewUser());

        Assert.That(_createdActivity, Is.Not.Null, "a partition-selection change must be recorded with an Activity");
        Assert.That(_createdActivity!.TargetType, Is.EqualTo(ActivityTargetType.ConnectedSystem));
        Assert.That(_createdActivity.TargetOperationType, Is.EqualTo(ActivityTargetOperationType.Update));
        AssertCapturedConnectedSystemVersion();
    }

    [Test]
    public async Task UpdateConnectedSystemContainerAsync_WithApiKeyInitiator_CreatesActivityAndCapturesSnapshotAsync()
    {
        var container = new ConnectedSystemContainer { Id = 30, ExternalId = "OU=People,DC=corp,DC=local", Name = "People", Selected = false };
        _csRepo.Setup(r => r.UpdateConnectedSystemContainerAsync(container)).Returns(Task.CompletedTask);
        _csRepo.Setup(r => r.GetConnectedSystemCoreAsync(1, It.IsAny<bool>())).ReturnsAsync(BuildConnectedSystem);

        await _jim.ConnectedSystems.UpdateConnectedSystemContainerAsync(container, 1, NewApiKey());

        Assert.That(_createdActivity, Is.Not.Null, "a container-selection change must be recorded with an Activity");
        Assert.That(_createdActivity!.TargetType, Is.EqualTo(ActivityTargetType.ConnectedSystem));
        AssertCapturedConnectedSystemVersion();
    }

    // -- runtime state must not pollute the configuration history ---------------------------------------------------------

    [Test]
    public async Task UpdateConnectedSystemStatusAsync_DoesNotCreateActivityOrCaptureSnapshotAsync()
    {
        var connectedSystem = BuildConnectedSystem();
        connectedSystem.Status = ConnectedSystemStatus.Deleting;

        await _jim.ConnectedSystems.UpdateConnectedSystemStatusAsync(connectedSystem, ConnectedSystemStatus.Active);

        Assert.That(connectedSystem.Status, Is.EqualTo(ConnectedSystemStatus.Active));
        _csRepo.Verify(r => r.UpdateConnectedSystemAsync(connectedSystem), Times.Once);
        Assert.That(_createdActivity, Is.Null, "a runtime status flip is not a configuration change and must not create an Activity");
        Assert.That(_completedActivity, Is.Null);
    }

    [Test]
    public async Task UpdateConnectedSystemPersistedConnectorDataAsync_DoesNotCreateActivityOrCaptureSnapshotAsync()
    {
        var connectedSystem = BuildConnectedSystem();
        connectedSystem.PersistedConnectorData = "watermark-cookie-v1";

        await _jim.ConnectedSystems.UpdateConnectedSystemPersistedConnectorDataAsync(connectedSystem, "watermark-cookie-v2");

        Assert.That(connectedSystem.PersistedConnectorData, Is.EqualTo("watermark-cookie-v2"));
        _csRepo.Verify(r => r.UpdateConnectedSystemAsync(connectedSystem), Times.Once);
        Assert.That(_createdActivity, Is.Null, "the import watermark is machine-generated runtime state, not a configuration change, and must not create an Activity");
        Assert.That(_completedActivity, Is.Null);
    }

    // -- idempotent capture guard ------------------------------------------------------------------------------------------

    [Test]
    public async Task UpdateConnectedSystemAsync_WhenSnapshotUnchanged_SkipsVersionAndSnapshotAsync()
    {
        var connectedSystem = BuildConnectedSystem();

        // First save: no prior snapshot exists, so a versioned snapshot is captured.
        await _jim.ConnectedSystems.UpdateConnectedSystemAsync(connectedSystem, NewUser());
        Assert.That(_completedActivity, Is.Not.Null);
        var storedSnapshot = _completedActivity!.ConfigurationChangeSnapshot;
        Assert.That(storedSnapshot, Is.Not.Null);
        Assert.That(_completedActivity.ConfigurationChangeVersion, Is.EqualTo(7));

        // Second save of the identical configuration: the latest stored snapshot matches, so no version is recorded.
        _activityRepo.Setup(r => r.GetLatestConfigurationChangeSnapshotAsync(ActivityTargetType.ConnectedSystem, 1))
            .ReturnsAsync(storedSnapshot);
        _completedActivity = null;

        await _jim.ConnectedSystems.UpdateConnectedSystemAsync(connectedSystem, NewUser(), changeReason: "no-op save");

        Assert.That(_completedActivity, Is.Not.Null);
        Assert.That(_completedActivity!.ConfigurationChangeVersion, Is.Null, "an unchanged configuration must not consume a version");
        Assert.That(_completedActivity!.ConfigurationChangeSnapshot, Is.Null, "an unchanged configuration must not store a duplicate snapshot");
        Assert.That(_completedActivity!.ChangeReason, Is.EqualTo("no-op save"), "the reason is still recorded independently of the dedupe guard");
    }

    [Test]
    public async Task UpdateConnectedSystemAsync_WhenStoredSnapshotIsJsonbNormalised_StillSkipsUnchangedAsync()
    {
        var connectedSystem = BuildConnectedSystem();

        // First save captures; harvest the stored snapshot.
        await _jim.ConnectedSystems.UpdateConnectedSystemAsync(connectedSystem, NewUser());
        var storedSnapshot = _completedActivity!.ConfigurationChangeSnapshot;
        Assert.That(storedSnapshot, Is.Not.Null);

        // PostgreSQL stores the snapshot in a jsonb column, which normalises the text (key ordering, spacing), so the
        // string read back never equals the fresh serialisation. Emulate that with a semantically-identical reformat:
        // the guard must compare meaning, not bytes, or it never skips anything in production.
        var normalised = System.Text.Json.Nodes.JsonNode.Parse(storedSnapshot!)!
            .ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        Assert.That(normalised, Is.Not.EqualTo(storedSnapshot), "the reformat must differ textually for this test to prove anything");
        _activityRepo.Setup(r => r.GetLatestConfigurationChangeSnapshotAsync(ActivityTargetType.ConnectedSystem, 1))
            .ReturnsAsync(normalised);
        _completedActivity = null;

        await _jim.ConnectedSystems.UpdateConnectedSystemAsync(connectedSystem, NewUser());

        Assert.That(_completedActivity, Is.Not.Null);
        Assert.That(_completedActivity!.ConfigurationChangeVersion, Is.Null, "a semantically-unchanged configuration must not consume a version, regardless of jsonb text normalisation");
        Assert.That(_completedActivity!.ConfigurationChangeSnapshot, Is.Null);
    }

    [Test]
    public async Task CreateOrUpdateSyncRuleAsync_WhenSnapshotUnchanged_SkipsVersionAndSnapshotAsync()
    {
        _csRepo.Setup(r => r.UpdateSyncRuleAsync(It.IsAny<SyncRule>())).Returns(Task.CompletedTask);
        var rule = BuildExportRule();

        // First save captures; harvest the stored snapshot.
        await _jim.ConnectedSystems.CreateOrUpdateSyncRuleAsync(rule, NewApiKey());
        var storedSnapshot = _completedActivity!.ConfigurationChangeSnapshot;
        Assert.That(storedSnapshot, Is.Not.Null);

        // Second identical save records no new version.
        _activityRepo.Setup(r => r.GetLatestConfigurationChangeSnapshotAsync(ActivityTargetType.SyncRule, 55))
            .ReturnsAsync(storedSnapshot);
        _completedActivity = null;

        await _jim.ConnectedSystems.CreateOrUpdateSyncRuleAsync(rule, NewApiKey());

        Assert.That(_completedActivity, Is.Not.Null);
        Assert.That(_completedActivity!.ConfigurationChangeVersion, Is.Null, "an unchanged Synchronisation Rule must not consume a version");
        Assert.That(_completedActivity!.ConfigurationChangeSnapshot, Is.Null);
    }

    // -- helpers -------------------------------------------------------------------------------------------------------

    private void AssertCapturedConnectedSystemVersion()
    {
        Assert.That(_completedActivity, Is.Not.Null);
        Assert.That(_completedActivity!.ConfigurationChangeVersion, Is.EqualTo(7), "version is the existing maximum (6) + 1");
        Assert.That(_completedActivity.ConnectedSystemId, Is.EqualTo(1), "the activity must carry the Connected System id so the change is queryable in its history");
        Assert.That(_completedActivity.ConfigurationChangeSnapshot, Is.Not.Null);
        Assert.That(_completedActivity.ConfigurationChangeSnapshot, Does.Contain("\"objectType\":\"ConnectedSystem\""));
    }

    // A minimal valid Connected System: the File connector needs no configured connector instance to validate settings.
    private static ConnectedSystem BuildConnectedSystem() => new()
    {
        Id = 1,
        Name = "HR System",
        ConnectorDefinition = new ConnectorDefinition { Name = ConnectorConstants.FileConnectorName },
        SettingValues =
        [
            new ConnectedSystemSettingValue
            {
                Id = 10,
                // Distinct Setting ids matter: the snapshot keys setting nodes on Setting.Id for diff matching.
                Setting = new ConnectorDefinitionSetting { Id = 100, Name = "File Path", Required = true, Type = ConnectedSystemSettingType.File },
                StringValue = "/data/users.csv"
            },
            new ConnectedSystemSettingValue
            {
                Id = 11,
                Setting = new ConnectorDefinitionSetting { Id = 101, Name = "Mode", Required = true, Type = ConnectedSystemSettingType.DropDown },
                StringValue = "Import Only"
            }
        ],
        RunProfiles = [],
        ObjectTypes = [],
        Partitions = []
    };

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

    /// <summary>A connector test double that reports container-creation support for the auto-selection path.</summary>
    private sealed class FakeContainerCreatingConnector : IConnector, IConnectorContainerCreation
    {
        public string Name => "Fake Directory";
        public string? Description => null;
        public string? Url => null;
        public IReadOnlyList<string> CreatedContainerExternalIds { get; } = [];
        public Task<bool> VerifyContainerExistsAsync(string containerExternalId) => Task.FromResult(true);
        public string? GetParentContainerExternalId(string containerExternalId) =>
            containerExternalId.Contains(',') ? containerExternalId[(containerExternalId.IndexOf(',') + 1)..] : null;
        public string GetContainerDisplayName(string containerExternalId) =>
            containerExternalId.Split(',')[0].Split('=')[^1];
    }

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
