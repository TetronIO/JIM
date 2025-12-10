using System.Collections.Generic;
using JIM.Models.Staging;
using NUnit.Framework;

namespace JIM.Models.Tests.Staging;

[TestFixture]
public class ConnectedSystemExtensionsTests
{
    #region GetMode Tests

    [Test]
    public void GetMode_WhenModeSettingExists_ReturnsValue()
    {
        // Arrange
        var connectedSystem = CreateConnectedSystemWithMode("Import Only");

        // Act
        var result = connectedSystem.GetMode();

        // Assert
        Assert.That(result, Is.EqualTo("Import Only"));
    }

    [Test]
    public void GetMode_WhenNoModeSetting_ReturnsNull()
    {
        // Arrange
        var connectedSystem = new ConnectedSystem { SettingValues = new List<ConnectedSystemSettingValue>() };

        // Act
        var result = connectedSystem.GetMode();

        // Assert
        Assert.That(result, Is.Null);
    }

    #endregion

    #region IsExportOnlyMode Tests

    [Test]
    public void IsExportOnlyMode_WhenModeIsExportOnly_ReturnsTrue()
    {
        // Arrange
        var connectedSystem = CreateConnectedSystemWithMode("Export Only");

        // Act
        var result = connectedSystem.IsExportOnlyMode();

        // Assert
        Assert.That(result, Is.True);
    }

    [Test]
    public void IsExportOnlyMode_WhenModeIsImportOnly_ReturnsFalse()
    {
        // Arrange
        var connectedSystem = CreateConnectedSystemWithMode("Import Only");

        // Act
        var result = connectedSystem.IsExportOnlyMode();

        // Assert
        Assert.That(result, Is.False);
    }

    [Test]
    public void IsExportOnlyMode_WhenModeIsBidirectional_ReturnsFalse()
    {
        // Arrange
        var connectedSystem = CreateConnectedSystemWithMode("Bidirectional");

        // Act
        var result = connectedSystem.IsExportOnlyMode();

        // Assert
        Assert.That(result, Is.False);
    }

    [Test]
    public void IsExportOnlyMode_WhenNoModeSetting_ReturnsFalse()
    {
        // Arrange
        var connectedSystem = new ConnectedSystem { SettingValues = new List<ConnectedSystemSettingValue>() };

        // Act
        var result = connectedSystem.IsExportOnlyMode();

        // Assert
        Assert.That(result, Is.False);
    }

    #endregion

    #region SupportsImportMode Tests

    [Test]
    public void SupportsImportMode_WhenModeIsImportOnly_ReturnsTrue()
    {
        // Arrange
        var connectedSystem = CreateConnectedSystemWithMode("Import Only");

        // Act
        var result = connectedSystem.SupportsImportMode();

        // Assert
        Assert.That(result, Is.True);
    }

    [Test]
    public void SupportsImportMode_WhenModeIsBidirectional_ReturnsTrue()
    {
        // Arrange
        var connectedSystem = CreateConnectedSystemWithMode("Bidirectional");

        // Act
        var result = connectedSystem.SupportsImportMode();

        // Assert
        Assert.That(result, Is.True);
    }

    [Test]
    public void SupportsImportMode_WhenModeIsExportOnly_ReturnsFalse()
    {
        // Arrange
        var connectedSystem = CreateConnectedSystemWithMode("Export Only");

        // Act
        var result = connectedSystem.SupportsImportMode();

        // Assert
        Assert.That(result, Is.False);
    }

    [Test]
    public void SupportsImportMode_WhenNoModeSetting_ReturnsTrue()
    {
        // Arrange - connector without Mode setting (e.g., LDAP)
        var connectedSystem = new ConnectedSystem { SettingValues = new List<ConnectedSystemSettingValue>() };

        // Act
        var result = connectedSystem.SupportsImportMode();

        // Assert
        Assert.That(result, Is.True);
    }

    #endregion

    #region SupportsExportMode Tests

    [Test]
    public void SupportsExportMode_WhenModeIsExportOnly_ReturnsTrue()
    {
        // Arrange
        var connectedSystem = CreateConnectedSystemWithMode("Export Only");

        // Act
        var result = connectedSystem.SupportsExportMode();

        // Assert
        Assert.That(result, Is.True);
    }

    [Test]
    public void SupportsExportMode_WhenModeIsBidirectional_ReturnsTrue()
    {
        // Arrange
        var connectedSystem = CreateConnectedSystemWithMode("Bidirectional");

        // Act
        var result = connectedSystem.SupportsExportMode();

        // Assert
        Assert.That(result, Is.True);
    }

    [Test]
    public void SupportsExportMode_WhenModeIsImportOnly_ReturnsFalse()
    {
        // Arrange
        var connectedSystem = CreateConnectedSystemWithMode("Import Only");

        // Act
        var result = connectedSystem.SupportsExportMode();

        // Assert
        Assert.That(result, Is.False);
    }

    [Test]
    public void SupportsExportMode_WhenNoModeSetting_ReturnsTrue()
    {
        // Arrange - connector without Mode setting (e.g., LDAP)
        var connectedSystem = new ConnectedSystem { SettingValues = new List<ConnectedSystemSettingValue>() };

        // Act
        var result = connectedSystem.SupportsExportMode();

        // Assert
        Assert.That(result, Is.True);
    }

    #endregion

    #region Helper Methods

    private static ConnectedSystem CreateConnectedSystemWithMode(string mode)
    {
        var modeSetting = new ConnectorDefinitionSetting { Name = "Mode" };
        var connectedSystem = new ConnectedSystem
        {
            SettingValues = new List<ConnectedSystemSettingValue>
            {
                new()
                {
                    Setting = modeSetting,
                    StringValue = mode
                }
            }
        };
        return connectedSystem;
    }

    #endregion
}
