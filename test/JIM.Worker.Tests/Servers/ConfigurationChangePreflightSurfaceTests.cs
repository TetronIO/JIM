// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Application.Services;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Staging;
using Moq;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// Covers the surfaces the acknowledgement flow reaches beyond the Synchronisation Rule editor: Connected Systems
/// (settings, schema and partitions all save the same entity), Metaverse Object Types, Metaverse Attributes and
/// Service Settings.
///
/// Each surface is asked the same two questions, because getting either wrong makes the whole feature worse than not
/// having it: does a harmless edit stay silent, and does a consequential one say specifically what it will do? A
/// dialog that fires on a rename is one administrators learn to click through, and the destructive dropdown beside it
/// is what they then miss.
/// </summary>
[TestFixture]
public class ConfigurationChangePreflightSurfaceTests
{
    private Mock<IRepository> _repo = null!;
    private Mock<IActivityRepository> _activityRepo = null!;
    private Mock<IServiceSettingsRepository> _serviceSettingsRepo = null!;
    private JimApplication _jim = null!;
    private ConfigurationSnapshotService _snapshots = null!;

    private const int ConnectedSystemId = 3;
    private const int ObjectTypeId = 11;
    private const int AttributeId = 22;
    private static readonly byte[] HashKey = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();

    [SetUp]
    public void SetUp()
    {
        TestUtilities.SetEnvironmentVariables();
        _repo = new Mock<IRepository>();
        _activityRepo = new Mock<IActivityRepository>();
        _serviceSettingsRepo = new Mock<IServiceSettingsRepository>();
        _repo.Setup(r => r.Activity).Returns(_activityRepo.Object);
        _repo.Setup(r => r.ServiceSettings).Returns(_serviceSettingsRepo.Object);

        SetChangeTrackingEnabled(true);
        SetHashKey();

        _jim = new JimApplication(_repo.Object);
        _snapshots = _jim.ConfigurationSnapshots;
    }

    [TearDown]
    public void TearDown() => _jim?.Dispose();

    #region Connected System

    [Test]
    public async Task EvaluateConnectedSystemAsync_RenameOnly_NeedsNoAcknowledgementAsync()
    {
        SetStoredBaseline(System("HR Database"));

        var result = await _jim.ConfigurationChangePreflight.EvaluateConnectedSystemAsync(System("HR Database (EMEA)"));

        Assert.Multiple(() =>
        {
            Assert.That(result.RequiresAcknowledgement, Is.False, "renaming a Connected System cannot change a synchronisation outcome");
            Assert.That(result.HighestClass, Is.EqualTo(ConfigurationChangeClass.Cosmetic));
        });
    }

    [Test]
    public async Task EvaluateConnectedSystemAsync_DeselectingAnObjectType_StatesTheConsequenceAsync()
    {
        // The schema tab's most dangerous action, and until now it saved with no confirmation of any kind.
        SetStoredBaseline(System("HR Database"));
        var proposed = System("HR Database");
        proposed.ObjectTypes![0].Selected = false;

        var result = await _jim.ConfigurationChangePreflight.EvaluateConnectedSystemAsync(proposed);

        var item = result.DestructiveItems.SingleOrDefault();
        Assert.Multiple(() =>
        {
            Assert.That(result.IsDestructive, Is.True);
            Assert.That(item, Is.Not.Null);
            Assert.That(item!.Consequence, Does.Contain("deprovisioned"),
                "deselecting an Object Type obsoletes its objects and deprovisions what they are joined to; the dialog must say so");
            Assert.That(item!.Label, Does.Contain("Person"),
                "'Object Types > Object Type > Selected' is the same sentence for all twelve of them; the dialog must name the one being deselected");
        });
    }

    [Test]
    public async Task EvaluateConnectedSystemAsync_DeselectingAPartition_StatesTheConsequenceAsync()
    {
        SetStoredBaseline(System("HR Database"));
        var proposed = System("HR Database");
        proposed.Partitions![0].Selected = false;

        var result = await _jim.ConfigurationChangePreflight.EvaluateConnectedSystemAsync(proposed);

        Assert.Multiple(() =>
        {
            Assert.That(result.IsDestructive, Is.True);
            Assert.That(result.DestructiveItems.Single().Label, Does.Contain("EMEA"),
                "the administrator needs to know which partition they are removing from scope");
        });
    }

    [Test]
    public async Task EvaluateConnectedSystemAsync_SettingValueChange_AsksForAcknowledgementWithoutClaimingDestructionAsync()
    {
        SetStoredBaseline(System("HR Database"));
        var proposed = System("HR Database");
        proposed.SettingValues[0].StringValue = "/mnt/import/hr-2.csv";

        var result = await _jim.ConfigurationChangePreflight.EvaluateConnectedSystemAsync(proposed);

        Assert.Multiple(() =>
        {
            Assert.That(result.RequiresAcknowledgement, Is.True, "what a connector reads is what a synchronisation acts on");
            Assert.That(result.IsDestructive, Is.False);
            Assert.That(result.DestructiveItems, Is.Empty);
        });
    }

    [Test]
    public async Task EvaluateConnectedSystemAsync_NewSystem_NeedsNoAcknowledgementAsync()
    {
        var proposed = System("Brand New");
        proposed.Id = 0;

        var result = await _jim.ConfigurationChangePreflight.EvaluateConnectedSystemAsync(proposed);

        Assert.That(result.RequiresAcknowledgement, Is.False);
        _activityRepo.Verify(r => r.GetLatestConfigurationChangeSnapshotAsync(It.IsAny<ActivityTargetType>(), It.IsAny<int>()),
            Times.Never, "a create has no prior state to put at risk, so it needs no baseline lookup");
    }

    #endregion

    #region Metaverse Object Type

    [Test]
    public async Task EvaluateMetaverseObjectTypeAsync_IconChangeOnly_NeedsNoAcknowledgementAsync()
    {
        SetStoredBaseline(ObjectType());
        var proposed = ObjectType();
        proposed.Icon = "Groups";

        var result = await _jim.ConfigurationChangePreflight.EvaluateMetaverseObjectTypeAsync(proposed);

        Assert.That(result.RequiresAcknowledgement, Is.False);
    }

    [Test]
    public async Task EvaluateMetaverseObjectTypeAsync_DeletionRuleChange_StatesThatItAppliesImmediatelyAsync()
    {
        // The one place in JIM where saving alone can make existing objects eligible for deletion, with no
        // synchronisation run in between. That distinction is the entire point of the copy.
        SetStoredBaseline(ObjectType());
        var proposed = ObjectType();
        proposed.DeletionRule = MetaverseObjectDeletionRule.WhenAuthoritativeSourceDisconnected;
        proposed.DeletionTriggerConnectedSystemIds = [ConnectedSystemId];

        var result = await _jim.ConfigurationChangePreflight.EvaluateMetaverseObjectTypeAsync(proposed);

        Assert.Multiple(() =>
        {
            Assert.That(result.IsDestructive, Is.True);
            Assert.That(result.DestructiveItems.Select(i => i.Key), Does.Contain("deletionRule"));
            Assert.That(result.DestructiveItems.Single(i => i.Key == "deletionRule").Consequence,
                Does.Contain("immediately"));
        });
    }

    [Test]
    public async Task EvaluateMetaverseObjectTypeAsync_ShorteningTheGracePeriod_StatesTheConsequenceAsync()
    {
        SetStoredBaseline(ObjectType());
        var proposed = ObjectType();
        proposed.DeletionGracePeriod = TimeSpan.FromHours(1);

        var result = await _jim.ConfigurationChangePreflight.EvaluateMetaverseObjectTypeAsync(proposed);

        Assert.That(result.DestructiveItems.Single(i => i.Key == "deletionGracePeriod").Consequence,
            Does.Contain("brings forward"));
    }

    #endregion

    #region Metaverse Attribute

    [Test]
    public async Task EvaluateMetaverseAttributeAsync_RenameOnly_NeedsNoAcknowledgementAsync()
    {
        SetStoredBaseline(Attribute("employeeId"));

        var result = await _jim.ConfigurationChangePreflight.EvaluateMetaverseAttributeAsync(Attribute("employeeNumber"));

        Assert.That(result.RequiresAcknowledgement, Is.False);
    }

    [Test]
    public async Task EvaluateMetaverseAttributeAsync_PluralityChange_AsksForAcknowledgementAsync()
    {
        // Every Attribute Flow targeting this attribute changes shape; nothing is deleted, so it is not destructive.
        SetStoredBaseline(Attribute("employeeId"));
        var proposed = Attribute("employeeId");
        proposed.AttributePlurality = AttributePlurality.MultiValued;

        var result = await _jim.ConfigurationChangePreflight.EvaluateMetaverseAttributeAsync(proposed);

        Assert.Multiple(() =>
        {
            Assert.That(result.RequiresAcknowledgement, Is.True);
            Assert.That(result.IsDestructive, Is.False);
            Assert.That(result.Items.Select(i => i.Key), Does.Contain("attributePlurality"));
        });
    }

    [Test]
    public async Task EvaluateMetaverseAttributeAsync_StandardMappingChangeOnly_NeedsNoAcknowledgementAsync()
    {
        // Standard Mappings are advisory metadata; they steer nothing at synchronisation time.
        SetStoredBaseline(Attribute("employeeId"));
        var proposed = Attribute("employeeId");
        proposed.StandardMappings =
        [
            new MetaverseAttributeStandardMapping { Standard = AttributeStandard.Scim, CounterpartName = "externalId" }
        ];

        var result = await _jim.ConfigurationChangePreflight.EvaluateMetaverseAttributeAsync(proposed);

        Assert.Multiple(() =>
        {
            Assert.That(result.Items, Is.Not.Empty, "the change is real, it is just not consequential");
            Assert.That(result.RequiresAcknowledgement, Is.False);
        });
    }

    #endregion

    #region Service Setting

    [Test]
    public async Task EvaluateServiceSettingAsync_OperationalSetting_NeedsNoAcknowledgementAsync()
    {
        SetStoredBaseline(Setting(Constants.SettingKeys.SyncPageSize, "500"));
        var proposed = Setting(Constants.SettingKeys.SyncPageSize, "1000");

        var result = await _jim.ConfigurationChangePreflight.EvaluateServiceSettingAsync(proposed);

        Assert.Multiple(() =>
        {
            Assert.That(result.Items, Is.Not.Empty);
            Assert.That(result.RequiresAcknowledgement, Is.False, "page size changes throughput, not outcomes");
        });
    }

    [Test]
    public async Task EvaluateServiceSettingAsync_PartitionValidationMode_AsksForAcknowledgementAsync()
    {
        // Relaxing this lets a Run Profile whose partition has gone missing import zero objects, which a Full
        // Synchronisation then reads as everything having disappeared.
        SetStoredBaseline(Setting(Constants.SettingKeys.PartitionValidationMode, "Strict"));
        var proposed = Setting(Constants.SettingKeys.PartitionValidationMode, "Permissive");

        var result = await _jim.ConfigurationChangePreflight.EvaluateServiceSettingAsync(proposed);

        Assert.Multiple(() =>
        {
            Assert.That(result.RequiresAcknowledgement, Is.True);
            Assert.That(result.IsDestructive, Is.False);
        });
    }

    [Test]
    public async Task EvaluateServiceSettingAsync_LooksTheBaselineUpByKeyAsync()
    {
        // Service Settings are string-keyed, not id-keyed; using the wrong overload would silently find no baseline
        // and report every change as unknowable.
        SetStoredBaseline(Setting(Constants.SettingKeys.PartitionValidationMode, "Strict"));

        await _jim.ConfigurationChangePreflight.EvaluateServiceSettingAsync(
            Setting(Constants.SettingKeys.PartitionValidationMode, "Permissive"));

        _activityRepo.Verify(r => r.GetLatestConfigurationChangeSnapshotAsync(
            ActivityTargetType.ServiceSetting, Constants.SettingKeys.PartitionValidationMode), Times.Once);
    }

    [Test]
    public async Task EvaluateServiceSettingAsync_NoStoredBaseline_ReportsUnknownRatherThanSafeAsync()
    {
        _activityRepo.Setup(r => r.GetLatestConfigurationChangeSnapshotAsync(ActivityTargetType.ServiceSetting, It.IsAny<string>()))
            .ReturnsAsync((string?)null);

        var result = await _jim.ConfigurationChangePreflight.EvaluateServiceSettingAsync(
            Setting(Constants.SettingKeys.PartitionValidationMode, "Permissive"));

        Assert.Multiple(() =>
        {
            Assert.That(result.BaselineUnavailable, Is.True);
            Assert.That(result.RequiresAcknowledgement, Is.False);
        });
    }

    #endregion

    #region Helpers

    private void SetStoredBaseline(ConnectedSystem connectedSystem) =>
        _activityRepo.Setup(r => r.GetLatestConfigurationChangeSnapshotAsync(ActivityTargetType.ConnectedSystem, ConnectedSystemId))
            .ReturnsAsync(ConfigurationSnapshotService.Serialise(_snapshots.CreateSnapshot(connectedSystem, HashKey)));

    private void SetStoredBaseline(MetaverseObjectType objectType) =>
        _activityRepo.Setup(r => r.GetLatestConfigurationChangeSnapshotAsync(ActivityTargetType.MetaverseObjectType, ObjectTypeId))
            .ReturnsAsync(ConfigurationSnapshotService.Serialise(_snapshots.CreateSnapshot(objectType, HashKey)));

    private void SetStoredBaseline(MetaverseAttribute attribute) =>
        _activityRepo.Setup(r => r.GetLatestConfigurationChangeSnapshotAsync(ActivityTargetType.MetaverseAttribute, AttributeId))
            .ReturnsAsync(ConfigurationSnapshotService.Serialise(_snapshots.CreateSnapshot(attribute, HashKey)));

    private void SetStoredBaseline(ServiceSetting setting) =>
        _activityRepo.Setup(r => r.GetLatestConfigurationChangeSnapshotAsync(ActivityTargetType.ServiceSetting, setting.Key))
            .ReturnsAsync(ConfigurationSnapshotService.Serialise(_snapshots.CreateSnapshot(setting, HashKey)));

    private void SetChangeTrackingEnabled(bool enabled) =>
        _serviceSettingsRepo.Setup(r => r.GetSettingAsync(Constants.SettingKeys.ChangeTrackingConfigurationChangesEnabled))
            .ReturnsAsync(new ServiceSetting
            {
                Key = Constants.SettingKeys.ChangeTrackingConfigurationChangesEnabled,
                ValueType = ServiceSettingValueType.Boolean,
                Value = enabled ? "true" : "false"
            });

    private void SetHashKey() =>
        _serviceSettingsRepo.Setup(r => r.GetSettingAsync(Constants.SettingKeys.ConfigurationChangeHashKey))
            .ReturnsAsync(new ServiceSetting
            {
                Key = Constants.SettingKeys.ConfigurationChangeHashKey,
                ValueType = ServiceSettingValueType.StringEncrypted,
                Value = Convert.ToBase64String(HashKey)
            });

    private static ConnectedSystem System(string name) => new()
    {
        Id = ConnectedSystemId,
        Name = name,
        ConnectorDefinitionId = 1,
        SettingValues =
        [
            new ConnectedSystemSettingValue
            {
                Id = 90,
                Setting = new ConnectorDefinitionSetting { Id = 90, Name = "File Path", Type = ConnectedSystemSettingType.String },
                StringValue = "/mnt/import/hr.csv"
            }
        ],
        ObjectTypes =
        [
            new ConnectedSystemObjectType { Id = 40, Name = "Person", Selected = true }
        ],
        Partitions =
        [
            // Populated rather than left empty on purpose: three classification defects hid behind fixtures whose
            // collections were empty, because a collection that is absent from both snapshots can never diff.
            new ConnectedSystemPartition
            {
                Id = 50,
                Name = "EMEA",
                ExternalId = "DC=emea",
                Selected = true,
                Containers =
                [
                    new ConnectedSystemContainer { Id = 60, Name = "Users", ExternalId = "OU=Users,DC=emea", Selected = true }
                ]
            }
        ]
    };

    private static MetaverseObjectType ObjectType() => new()
    {
        Id = ObjectTypeId,
        Name = "User",
        PluralName = "Users",
        Icon = "Person",
        DeletionRule = MetaverseObjectDeletionRule.WhenLastConnectorDisconnected,
        DeletionGracePeriod = TimeSpan.FromDays(7),
        DeletionTriggerConnectedSystemIds = []
    };

    private static MetaverseAttribute Attribute(string name) => new()
    {
        Id = AttributeId,
        Name = name,
        Type = AttributeDataType.Text,
        AttributePlurality = AttributePlurality.SingleValued,
        MetaverseObjectTypes = [],
        StandardMappings = []
    };

    private static ServiceSetting Setting(string key, string value) => new()
    {
        Key = key,
        DisplayName = key,
        Category = ServiceSettingCategory.Synchronisation,
        ValueType = ServiceSettingValueType.String,
        Value = value,
        DefaultValue = "Strict"
    };

    #endregion
}
