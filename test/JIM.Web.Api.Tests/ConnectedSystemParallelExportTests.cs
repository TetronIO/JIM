using System;
using System.Collections.Generic;
using JIM.Connectors.File;
using JIM.Connectors.LDAP;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using JIM.Web.Models.Api;
using NUnit.Framework;

namespace JIM.Web.Api.Tests;

/// <summary>
/// Tests for the SupportsParallelExport connector capability and
/// MaxExportParallelism Connected System property across the API layer.
/// </summary>
[TestFixture]
public class ConnectedSystemParallelExportTests
{
    #region ConnectedSystemDetailDto.FromEntity mapping tests

    [Test]
    public void FromEntity_MaxExportParallelismSet_MapsCorrectly()
    {
        // Arrange
        var entity = CreateConnectedSystemEntity();
        entity.MaxExportParallelism = 4;

        // Act
        var dto = ConnectedSystemDetailDto.FromEntity(entity);

        // Assert
        Assert.That(dto.MaxExportParallelism, Is.EqualTo(4));
    }

    [Test]
    public void FromEntity_MaxExportParallelismNull_MapsAsNull()
    {
        // Arrange
        var entity = CreateConnectedSystemEntity();
        entity.MaxExportParallelism = null;

        // Act
        var dto = ConnectedSystemDetailDto.FromEntity(entity);

        // Assert
        Assert.That(dto.MaxExportParallelism, Is.Null);
    }

    [Test]
    public void FromEntity_MaxExportParallelismOne_MapsCorrectly()
    {
        // Arrange
        var entity = CreateConnectedSystemEntity();
        entity.MaxExportParallelism = 1;

        // Act
        var dto = ConnectedSystemDetailDto.FromEntity(entity);

        // Assert
        Assert.That(dto.MaxExportParallelism, Is.EqualTo(1));
    }

    #endregion

    #region Connector capability tests

    [Test]
    public void LdapConnector_SupportsParallelExport_ReturnsTrue()
    {
        using var connector = new LdapConnector();
        Assert.That(connector.SupportsParallelExport, Is.True);
    }

    [Test]
    public void FileConnector_SupportsParallelExport_ReturnsFalse()
    {
        var connector = new FileConnector();
        Assert.That(connector.SupportsParallelExport, Is.False);
    }

    #endregion

    #region ConnectorDefinition capability property tests

    [Test]
    public void ConnectorDefinition_SupportsParallelExport_DefaultsFalse()
    {
        var definition = new ConnectorDefinition();
        Assert.That(definition.SupportsParallelExport, Is.False);
    }

    [Test]
    public void ConnectorDefinition_SupportsParallelExport_CanBeSetTrue()
    {
        var definition = new ConnectorDefinition { SupportsParallelExport = true };
        Assert.That(definition.SupportsParallelExport, Is.True);
    }

    #endregion

    #region ConnectedSystem MaxExportParallelism property tests

    [Test]
    public void ConnectedSystem_MaxExportParallelism_DefaultsToNull()
    {
        var cs = new ConnectedSystem { Name = "Test" };
        Assert.That(cs.MaxExportParallelism, Is.Null);
    }

    [Test]
    public void ConnectedSystem_MaxExportParallelism_FallsBackToSequential()
    {
        // When MaxExportParallelism is null, the worker should default to 1
        var cs = new ConnectedSystem { Name = "Test" };
        var effectiveParallelism = cs.MaxExportParallelism ?? 1;
        Assert.That(effectiveParallelism, Is.EqualTo(1));
    }

    [Test]
    public void ConnectedSystem_MaxExportParallelism_ReturnsConfiguredValue()
    {
        var cs = new ConnectedSystem { Name = "Test", MaxExportParallelism = 4 };
        var effectiveParallelism = cs.MaxExportParallelism ?? 1;
        Assert.That(effectiveParallelism, Is.EqualTo(4));
    }

    #endregion

    #region ExportExecutionOptions wiring tests

    [Test]
    public void ExportExecutionOptions_MaxParallelism_ReadsFromConnectedSystem()
    {
        // Simulates what SyncExportTaskProcessor does
        var cs = new ConnectedSystem { Name = "Test", MaxExportParallelism = 8 };

        var options = new ExportExecutionOptions
        {
            BatchSize = 100,
            MaxParallelism = cs.MaxExportParallelism ?? 1
        };

        Assert.That(options.MaxParallelism, Is.EqualTo(8));
    }

    [Test]
    public void ExportExecutionOptions_MaxParallelism_DefaultsToOneWhenNull()
    {
        var cs = new ConnectedSystem { Name = "Test", MaxExportParallelism = null };

        var options = new ExportExecutionOptions
        {
            BatchSize = 100,
            MaxParallelism = cs.MaxExportParallelism ?? 1
        };

        Assert.That(options.MaxParallelism, Is.EqualTo(1));
    }

    #endregion

    #region UpdateConnectedSystemRequest tests

    [Test]
    public void UpdateConnectedSystemRequest_MaxExportParallelism_AcceptsValidValue()
    {
        var request = new UpdateConnectedSystemRequest
        {
            MaxExportParallelism = 4
        };

        Assert.That(request.MaxExportParallelism, Is.EqualTo(4));
    }

    [Test]
    public void UpdateConnectedSystemRequest_MaxExportParallelismNull_IsValid()
    {
        var request = new UpdateConnectedSystemRequest();

        Assert.That(request.MaxExportParallelism, Is.Null);
    }

    #endregion

    #region Helper methods

    private static ConnectedSystem CreateConnectedSystemEntity()
    {
        return new ConnectedSystem
        {
            Id = 1,
            Name = "Test System",
            Description = "Test Description",
            ConnectorDefinition = new ConnectorDefinition
            {
                Id = 1,
                Name = "Test Connector",
                SupportsExport = true,
                SupportsParallelExport = true
            },
            ObjectTypes = new List<ConnectedSystemObjectType>(),
            Objects = new List<ConnectedSystemObject>(),
            PendingExports = new List<PendingExport>(),
            SettingValues = new List<ConnectedSystemSettingValue>()
        };
    }

    #endregion
}
