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

/// <summary>
/// Proves that Connector dispatch for unsupported and capability-mismatched Connector Definitions is centralised
/// through <see cref="JIM.Connectors.ConnectorFactory"/>, and always surfaces as a <see cref="NotSupportedException"/>
/// naming the offending connector, never a bare <see cref="NotImplementedException"/>.
/// </summary>
[TestFixture]
public class ConnectedSystemConnectorDispatchTests
{
    private const string UnknownConnectorName = "Nonexistent Connector";

    private JimApplication _jim = null!;
    private string _tempCsvPath = null!;

    [SetUp]
    public void SetUp()
    {
        TestUtilities.SetEnvironmentVariables();
        _jim = new JimApplication(new Mock<IRepository>().Object);

        // FileConnector settings need a file to exist for import modes; the connector reads it as CSV regardless
        // of extension, and the hierarchy test only needs the Connected System to be constructible.
        _tempCsvPath = Path.GetTempFileName();
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
    public void ValidateConnectedSystemSettings_UnknownConnectorDefinition_ThrowsNotSupportedException()
    {
        // Arrange
        var connectedSystem = CreateUnknownConnectorConnectedSystem();

        // Act & Assert
        var exception = Assert.Throws<NotSupportedException>(() => _jim.ConnectedSystems.ValidateConnectedSystemSettings(connectedSystem));
        Assert.That(exception.Message, Does.Contain(UnknownConnectorName));
    }

    [Test]
    public void ImportConnectedSystemSchemaAsync_UnknownConnectorDefinition_ThrowsNotSupportedException()
    {
        // Arrange
        var connectedSystem = CreateUnknownConnectorConnectedSystem();

        // Act & Assert: the connector is resolved before any Activity is created, so a mocked repository with no
        // Activity repository configured never gets touched; if it were, this would fail with a NullReferenceException
        // instead of the expected NotSupportedException.
        var exception = Assert.ThrowsAsync<NotSupportedException>(async () => await _jim.ConnectedSystems.ImportConnectedSystemSchemaAsync(connectedSystem, initiatedBy: null));
        Assert.That(exception.Message, Does.Contain(UnknownConnectorName));
    }

    [Test]
    public void ImportConnectedSystemHierarchyAsync_FileConnector_ThrowsNotSupportedException()
    {
        // Arrange: the File Connector does not implement IConnectorPartitions, so hierarchy import is unsupported.
        var connectedSystem = CreateFileConnectorConnectedSystem();

        // Act & Assert
        var exception = Assert.ThrowsAsync<NotSupportedException>(async () => await _jim.ConnectedSystems.ImportConnectedSystemHierarchyAsync(connectedSystem, initiatedBy: null));
        Assert.That(exception.Message, Does.Contain("hierarchy"));
    }

    [Test]
    public void ImportConnectedSystemHierarchyAsync_UnknownConnectorDefinition_ThrowsNotSupportedException()
    {
        // Arrange
        var connectedSystem = CreateUnknownConnectorConnectedSystem();

        // Act & Assert
        var exception = Assert.ThrowsAsync<NotSupportedException>(async () => await _jim.ConnectedSystems.ImportConnectedSystemHierarchyAsync(connectedSystem, initiatedBy: null));
        Assert.That(exception.Message, Does.Contain(UnknownConnectorName));
    }

    /// <summary>
    /// Builds a Connected System whose Connector Definition does not correspond to any built-in connector.
    /// </summary>
    private static ConnectedSystem CreateUnknownConnectorConnectedSystem()
    {
        var connectorDefinition = new ConnectorDefinition { Name = UnknownConnectorName };
        var setting = new ConnectorDefinitionSetting { Name = "Dummy Setting", Type = ConnectedSystemSettingType.Text };
        connectorDefinition.Settings.Add(setting);

        var connectedSystem = new ConnectedSystem
        {
            Id = 1,
            Name = "Test Unsupported System",
            ConnectorDefinition = connectorDefinition,
            SettingValues =
            [
                new ConnectedSystemSettingValue { Setting = setting, StringValue = "value" }
            ]
        };

        return connectedSystem;
    }

    /// <summary>
    /// Builds a Connected System using the File Connector's own setting definitions, mirroring how JIM creates
    /// setting values from a persisted connector definition.
    /// </summary>
    private ConnectedSystem CreateFileConnectorConnectedSystem()
    {
        var connectorDefinition = new ConnectorDefinition { Name = ConnectorConstants.FileConnectorName };
        _jim.ConnectedSystems.CopyConnectorSettingsToConnectorDefinition(new FileConnector(), connectorDefinition);

        var connectedSystem = new ConnectedSystem
        {
            Id = 2,
            Name = "Test File System",
            ConnectorDefinition = connectorDefinition,
            SettingValues = connectorDefinition.Settings.Select(s => new ConnectedSystemSettingValue
            {
                Setting = s,
                StringValue = s.DefaultStringValue
            }).ToList()
        };

        connectedSystem.SettingValues.Single(sv => sv.Setting.Name == "File Path").StringValue = _tempCsvPath;
        connectedSystem.SettingValues.Single(sv => sv.Setting.Name == "Object Type").StringValue = "user";
        return connectedSystem;
    }
}
