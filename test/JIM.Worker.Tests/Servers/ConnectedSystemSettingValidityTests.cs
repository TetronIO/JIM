// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Connectors;
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
/// What <see cref="ConnectedSystem.SettingValuesValid"/> is allowed to mean.
/// </summary>
/// <remarks>
/// The flag is persisted, and the portal gates the Schema, Partitions &amp; Containers and Matching tabs on it, so it
/// has to answer a question about the configuration: are the setting values complete and well-formed? It used to be
/// recomputed on every save from the Connector's own validation, which opens a real connection to the target system.
/// Saving anything at all while that system was unreachable, a container selection included, therefore persisted
/// "settings invalid" and locked the administrator out of three tabs until somebody re-saved the Settings tab, and it
/// put a network round trip on the path of every unrelated save.
///
/// Whether the target answers is a live fact that belongs on the Settings tab, where an administrator is configuring
/// connectivity and can act on it. It is never a reason to declare the stored configuration invalid.
/// </remarks>
[TestFixture]
public class ConnectedSystemSettingValidityTests
{
    private Mock<IRepository> _repo = null!;
    private Mock<IActivityRepository> _activityRepo = null!;
    private Mock<IServiceSettingsRepository> _settingsRepo = null!;
    private Mock<IConnectedSystemRepository> _csRepo = null!;
    private JimApplication _jim = null!;

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
        _activityRepo.Setup(r => r.UpdateActivityAsync(It.IsAny<Activity>())).Returns(Task.CompletedTask);
        _csRepo.Setup(r => r.UpdateConnectedSystemAsync(It.IsAny<ConnectedSystem>())).Returns(Task.CompletedTask);

        // Configuration change capture is off: this fixture is about the validity flag, and a snapshot reload would
        // need a graph these mocks do not serve.
        _settingsRepo.Setup(r => r.GetSettingAsync(Constants.SettingKeys.ChangeTrackingConfigurationChangesEnabled))
            .ReturnsAsync(new ServiceSetting
            {
                Key = Constants.SettingKeys.ChangeTrackingConfigurationChangesEnabled,
                ValueType = ServiceSettingValueType.Boolean,
                Value = "false"
            });

        _jim = new JimApplication(_repo.Object);
    }

    [TearDown]
    public void TearDown() => _jim?.Dispose();

    [Test]
    public async Task UpdateConnectedSystemAsync_WithTheTargetSystemUnreachable_LeavesTheSettingsValidAsync()
    {
        // The File Connector stands in for any Connector whose own validation is a live probe: it reports a missing
        // file exactly as the LDAP Connector reports a directory it cannot bind to. Neither says anything about
        // whether the administrator's settings are complete.
        var connectedSystem = BuildFileConnectedSystem("/nowhere/never-created.csv");
        connectedSystem.SettingValuesValid = true;

        await _jim.ConnectedSystems.UpdateConnectedSystemAsync(connectedSystem, Administrator());

        Assert.That(connectedSystem.SettingValuesValid, Is.True,
            "an unreachable target must not persist the configuration as invalid, which is what gates the portal's tabs");
    }

    [Test]
    public async Task UpdateConnectedSystemAsync_WithARequiredSettingMissing_MarksTheSettingsInvalidAsync()
    {
        // The other half of the split: completeness is a fact about the configuration, and the flag still carries it.
        var connectedSystem = BuildFileConnectedSystem(filePath: null);
        connectedSystem.SettingValuesValid = true;

        await _jim.ConnectedSystems.UpdateConnectedSystemAsync(connectedSystem, Administrator());

        Assert.That(connectedSystem.SettingValuesValid, Is.False);
    }

    [Test]
    public void ValidateConnectedSystemSettings_StillReportsWhatTheConnectorItselfFinds()
    {
        // Unchanged, and the reason the split is safe: the Settings tab and the settings-writing REST endpoint both
        // call this, so an administrator configuring connectivity is still told the target cannot be reached.
        var connectedSystem = BuildFileConnectedSystem("/nowhere/never-created.csv");

        var results = _jim.ConnectedSystems.ValidateConnectedSystemSettings(connectedSystem);

        Assert.That(results.Any(r => !r.IsValid), Is.True);
    }

    private static MetaverseObject Administrator() => new() { Id = Guid.NewGuid(), CachedDisplayName = "Test Admin" };

    private static ConnectedSystem BuildFileConnectedSystem(string? filePath) => new()
    {
        Id = 1,
        Name = "HR Extract",
        ConnectorDefinition = new ConnectorDefinition { Name = ConnectorConstants.FileConnectorName },
        SettingValues =
        [
            new ConnectedSystemSettingValue
            {
                Id = 10,
                Setting = new ConnectorDefinitionSetting { Id = 100, Name = "File Path", Required = true, Type = ConnectedSystemSettingType.File },
                StringValue = filePath
            },
            new ConnectedSystemSettingValue
            {
                Id = 11,
                Setting = new ConnectorDefinitionSetting { Id = 101, Name = "Mode", Required = true, Type = ConnectedSystemSettingType.DropDown },
                StringValue = "Import Only"
            }
        ]
    };
}
