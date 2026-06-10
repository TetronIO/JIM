// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Connectors;
using JIM.Connectors.File;
using JIM.Data;
using JIM.Models.Staging;
using Moq;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

[TestFixture]
public class ConnectedSystemSettingsValidationTests
{
    private JimApplication _jim = null!;
    private string _tempCsvPath = null!;

    [SetUp]
    public void SetUp()
    {
        TestUtilities.SetEnvironmentVariables();
        _jim = new JimApplication(new Mock<IRepository>().Object);

        // FileConnector validation checks the file exists for import modes
        _tempCsvPath = Path.Combine(Path.GetTempPath(), $"jim-settings-validation-{Guid.NewGuid()}.csv");
        File.WriteAllText(_tempCsvPath, "id,displayName\n1,Test User\n");
    }

    [TearDown]
    public void TearDown()
    {
        _jim?.Dispose();
        if (File.Exists(_tempCsvPath))
            File.Delete(_tempCsvPath);
    }

    [Test]
    public void ValidateConnectedSystemSettings_FileConnectorWithoutObjectTypeSettings_ReturnsGroupError()
    {
        // Arrange
        var connectedSystem = CreateFileConnectorConnectedSystem();

        // Act
        var results = _jim.ConnectedSystems.ValidateConnectedSystemSettings(connectedSystem);

        // Assert: neither Object Type Column nor Object Type was supplied, so a group validation error is expected
        Assert.That(results.Any(r => !r.IsValid &&
                                     r.ErrorMessage != null &&
                                     r.ErrorMessage.Contains("Object Type Column") &&
                                     r.ErrorMessage.Contains("Object Type")), Is.True);
    }

    [Test]
    public void ValidateConnectedSystemSettings_FileConnectorWithObjectTypeSupplied_ReturnsNoErrors()
    {
        // Arrange
        var connectedSystem = CreateFileConnectorConnectedSystem();
        connectedSystem.SettingValues.Single(sv => sv.Setting.Name == "Object Type").StringValue = "user";

        // Act
        var results = _jim.ConnectedSystems.ValidateConnectedSystemSettings(connectedSystem);

        // Assert
        Assert.That(results.Where(r => !r.IsValid), Is.Empty);
    }

    [Test]
    public void CopyConnectorSettingsToConnectorDefinition_CopiesRequiredGroup()
    {
        // Arrange
        var connectorDefinition = new ConnectorDefinition { Name = ConnectorConstants.FileConnectorName };

        // Act
        _jim.ConnectedSystems.CopyConnectorSettingsToConnectorDefinition(new FileConnector(), connectorDefinition);

        // Assert
        var objectTypeColumn = connectorDefinition.Settings.Single(s => s.Name == "Object Type Column");
        Assert.That(objectTypeColumn.RequiredGroup, Is.Not.Null.And.Not.Empty);
    }

    /// <summary>
    /// Builds a Connected System using the File Connector's own setting definitions, mirroring how
    /// JIM creates setting values from a persisted connector definition.
    /// </summary>
    private ConnectedSystem CreateFileConnectorConnectedSystem()
    {
        var connectorDefinition = new ConnectorDefinition { Name = ConnectorConstants.FileConnectorName };
        _jim.ConnectedSystems.CopyConnectorSettingsToConnectorDefinition(new FileConnector(), connectorDefinition);

        var connectedSystem = new ConnectedSystem
        {
            Id = 1,
            Name = "Test File System",
            ConnectorDefinition = connectorDefinition,
            SettingValues = connectorDefinition.Settings.Select(s => new ConnectedSystemSettingValue
            {
                Setting = s,
                StringValue = s.DefaultStringValue
            }).ToList()
        };

        connectedSystem.SettingValues.Single(sv => sv.Setting.Name == "File Path").StringValue = _tempCsvPath;
        return connectedSystem;
    }
}
