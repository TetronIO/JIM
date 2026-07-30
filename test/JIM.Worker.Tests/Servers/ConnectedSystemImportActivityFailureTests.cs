// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Connectors;
using JIM.Connectors.File;
using JIM.Connectors.LDAP;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Exceptions;
using JIM.Models.Security;
using JIM.Models.Staging;
using Moq;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// Schema and hierarchy import create an Activity before talking to the Connected System, so every failure after
/// that point has to be recorded on the Activity. These tests pin that down: a Connected System that cannot be
/// read must leave a failed Activity behind, never one that stays in flight for ever with nothing recorded
/// against it.
/// </summary>
/// <remarks>
/// The failures are provoked with settings the connector itself rejects (a File Connector pointed at a file that
/// does not exist; an LDAP Connector pointed at a port with nothing listening), because that is the shape of the
/// real thing: an administrator retrieving a schema or a hierarchy from a system they cannot reach.
/// </remarks>
[TestFixture]
public class ConnectedSystemImportActivityFailureTests
{
    private Mock<IRepository> _repository = null!;
    private Mock<IActivityRepository> _activityRepository = null!;
    private JimApplication _jim = null!;
    private Activity? _createdActivity;
    private Activity? _updatedActivity;

    [SetUp]
    public void SetUp()
    {
        TestUtilities.SetEnvironmentVariables();

        _createdActivity = null;
        _updatedActivity = null;

        _repository = new Mock<IRepository>();
        _activityRepository = new Mock<IActivityRepository>();
        _repository.Setup(r => r.Activity).Returns(_activityRepository.Object);

        _activityRepository.Setup(r => r.CreateActivityAsync(It.IsAny<Activity>()))
            .Callback<Activity>(a => _createdActivity = a)
            .Returns(Task.CompletedTask);
        _activityRepository.Setup(r => r.UpdateActivityAsync(It.IsAny<Activity>()))
            .Callback<Activity>(a => _updatedActivity = a)
            .Returns(Task.CompletedTask);

        _jim = new JimApplication(_repository.Object);
    }

    [TearDown]
    public void TearDown() => _jim?.Dispose();

    [Test]
    public void ImportConnectedSystemSchemaAsync_WhenTheSchemaCannotBeRetrieved_FailsTheActivity()
    {
        var connectedSystem = CreateUnreachableFileConnectorConnectedSystem();

        Assert.ThrowsAsync<InvalidSettingValuesException>(async () =>
            await _jim.ConnectedSystems.ImportConnectedSystemSchemaAsync(connectedSystem, NewInitiator()));

        AssertActivityFailed();
    }

    [Test]
    public void ImportConnectedSystemSchemaAsync_ApiKeyInitiated_WhenTheSchemaCannotBeRetrieved_FailsTheActivity()
    {
        var connectedSystem = CreateUnreachableFileConnectorConnectedSystem();

        Assert.ThrowsAsync<InvalidSettingValuesException>(async () =>
            await _jim.ConnectedSystems.ImportConnectedSystemSchemaAsync(connectedSystem, NewApiKey()));

        AssertActivityFailed();
    }

    [Test]
    public void ImportConnectedSystemHierarchyAsync_WhenTheHierarchyCannotBeRetrieved_FailsTheActivity()
    {
        var connectedSystem = CreateUnreachableLdapConnectorConnectedSystem();

        // Caught rather than typed: which exception a refused connection surfaces as is the connector's business.
        // What matters here is that the Activity is finished when one escapes.
        Assert.CatchAsync(async () =>
            await _jim.ConnectedSystems.ImportConnectedSystemHierarchyAsync(connectedSystem, NewInitiator()));

        AssertActivityFailed();
    }

    [Test]
    public void ImportConnectedSystemHierarchyAsync_ApiKeyInitiated_WhenTheHierarchyCannotBeRetrieved_FailsTheActivity()
    {
        var connectedSystem = CreateUnreachableLdapConnectorConnectedSystem();

        Assert.CatchAsync(async () =>
            await _jim.ConnectedSystems.ImportConnectedSystemHierarchyAsync(connectedSystem, NewApiKey()));

        AssertActivityFailed();
    }

    /// <summary>
    /// The Activity must be finished, and finished as a failure carrying the reason. An Activity left in
    /// <see cref="ActivityStatus.InProgress"/> is the defect being guarded against: the operation is over, but the
    /// portal shows it as still running and records nothing about why it stopped.
    /// </summary>
    private void AssertActivityFailed()
    {
        Assert.That(_createdActivity, Is.Not.Null, "The operation must create an Activity before contacting the Connected System.");
        Assert.That(_updatedActivity, Is.Not.Null, "A failed operation must finish its Activity rather than leaving it in flight.");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_updatedActivity!.Status, Is.EqualTo(ActivityStatus.FailedWithError));
            Assert.That(_updatedActivity!.ErrorMessage, Is.Not.Null.And.Not.Empty, "The failure reason must be recorded on the Activity.");
        }
    }

    private static MetaverseObject NewInitiator() => new()
    {
        Id = Guid.NewGuid(),
        CachedDisplayName = "Test Administrator"
    };

    private static ApiKey NewApiKey() => new()
    {
        Id = Guid.NewGuid(),
        Name = "Test API Key"
    };

    /// <summary>
    /// A File Connector pointed at a file that does not exist: schema discovery has nothing to read, so the
    /// connector rejects the settings.
    /// </summary>
    private ConnectedSystem CreateUnreachableFileConnectorConnectedSystem()
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

        connectedSystem.SettingValues.Single(sv => sv.Setting.Name == "File Path").StringValue =
            Path.Combine(Path.GetTempPath(), $"jim-no-such-file-{Guid.NewGuid():N}.csv");
        connectedSystem.SettingValues.Single(sv => sv.Setting.Name == "Object Type").StringValue = "user";
        return connectedSystem;
    }

    /// <summary>
    /// An LDAP Connector aimed at port 1 on the loopback interface: nothing is listening, so the connection is
    /// refused immediately rather than waiting out a timeout.
    /// </summary>
    private ConnectedSystem CreateUnreachableLdapConnectorConnectedSystem()
    {
        var connectorDefinition = new ConnectorDefinition { Name = ConnectorConstants.LdapConnectorName };
        _jim.ConnectedSystems.CopyConnectorSettingsToConnectorDefinition(new LdapConnector(), connectorDefinition);

        var connectedSystem = new ConnectedSystem
        {
            Id = 2,
            Name = "Test LDAP System",
            ConnectorDefinition = connectorDefinition,
            SettingValues = connectorDefinition.Settings.Select(s => new ConnectedSystemSettingValue
            {
                Setting = s,
                StringValue = s.DefaultStringValue,
                IntValue = s.DefaultIntValue,
                CheckboxValue = s.DefaultCheckboxValue ?? false
            }).ToList()
        };

        SetSetting(connectedSystem, "Host", stringValue: "127.0.0.1");
        SetSetting(connectedSystem, "Port", intValue: 1);
        SetSetting(connectedSystem, "Connection Timeout", intValue: 2);
        SetSetting(connectedSystem, "Maximum Retries", intValue: 0);
        SetSetting(connectedSystem, "Authentication Type", stringValue: "Simple");
        SetSetting(connectedSystem, "Username", stringValue: "cn=admin,dc=example,dc=org");
        SetSetting(connectedSystem, "Password", encryptedValue: "adminpassword");
        return connectedSystem;
    }

    private static void SetSetting(ConnectedSystem connectedSystem, string name, string? stringValue = null, string? encryptedValue = null, int? intValue = null)
    {
        var settingValue = connectedSystem.SettingValues.SingleOrDefault(sv => sv.Setting.Name == name);
        if (settingValue == null)
            return;

        if (stringValue != null)
            settingValue.StringValue = stringValue;
        if (encryptedValue != null)
            settingValue.StringEncryptedValue = encryptedValue;
        if (intValue != null)
            settingValue.IntValue = intValue;
    }
}
