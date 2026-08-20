// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Connectors;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Exceptions;
using JIM.Models.Interfaces;
using JIM.Models.Staging;
using Moq;
using NUnit.Framework;
using Serilog;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// A Connector whose settings can only be judged against the schema selection (the SQL Connector's delta-mode
/// requirements are owed by the selected Object Types only, #1424) is asked at the moments the selection can
/// change: the whole-graph schema save the portal makes, and the per-Object-Type update the REST API and
/// PowerShell make. A selection the Connector refuses is not persisted, and the settings validation the
/// Settings tab runs carries the same verdict.
/// </summary>
[TestFixture]
public class ConnectedSystemObjectTypeSelectionValidationTests
{
    private Mock<IConnectedSystemRepository> _connectedSystemRepository = null!;
    private JimApplication _jim = null!;
    private StubSelectionValidatingConnector _connector = null!;

    [SetUp]
    public void SetUp()
    {
        TestUtilities.SetEnvironmentVariables();

        var repository = new Mock<IRepository>();
        var activityRepository = new Mock<IActivityRepository>();
        _connectedSystemRepository = new Mock<IConnectedSystemRepository>();
        repository.Setup(r => r.Activity).Returns(activityRepository.Object);
        repository.Setup(r => r.ConnectedSystems).Returns(_connectedSystemRepository.Object);
        activityRepository.Setup(r => r.CreateActivityAsync(It.IsAny<Activity>())).Returns(Task.CompletedTask);
        activityRepository.Setup(r => r.UpdateActivityAsync(It.IsAny<Activity>())).Returns(Task.CompletedTask);
        _connectedSystemRepository.Setup(r => r.UpdateConnectedSystemSchemaAsync(It.IsAny<ConnectedSystem>())).Returns(Task.CompletedTask);
        _connectedSystemRepository.Setup(r => r.UpdateObjectTypeAsync(It.IsAny<ConnectedSystemObjectType>())).Returns(Task.CompletedTask);

        _connector = new StubSelectionValidatingConnector();
        _jim = new JimApplication(repository.Object, connectorFactory: new StubConnectorFactory(_connector));
    }

    [TearDown]
    public void TearDown()
    {
        _jim?.Dispose();
    }

    #region Whole-graph schema save (portal)

    [Test]
    public async Task UpdateConnectedSystemSchemaAsync_SelectionTheConnectorAccepts_PersistsAsync()
    {
        var connectedSystem = CreateConnectedSystem(("Person", true), ("AppUser", false));

        await _jim.ConnectedSystems.UpdateConnectedSystemSchemaAsync(connectedSystem, TestUtilities.GetInitiatedBy());

        using (Assert.EnterMultipleScope())
        {
            _connectedSystemRepository.Verify(r => r.UpdateConnectedSystemSchemaAsync(connectedSystem), Times.Once);
            Assert.That(_connector.SelectedNamesSeen, Is.EquivalentTo(new[] { "Person" }),
                "The Connector is shown the selection as it will be persisted, so it can judge exactly what a Delta Import would read.");
        }
    }

    [Test]
    public void UpdateConnectedSystemSchemaAsync_SelectionTheConnectorRefuses_ThrowsAndDoesNotPersist()
    {
        _connector.RefuseWhenSelected("AppUser", "Delta Import Mode is 'Watermark Column', but Object Type 'AppUser' has no 'watermarkColumn'.");
        var connectedSystem = CreateConnectedSystem(("Person", true), ("AppUser", true));

        Assert.That(async () => await _jim.ConnectedSystems.UpdateConnectedSystemSchemaAsync(connectedSystem, TestUtilities.GetInitiatedBy()),
            Throws.TypeOf<InvalidSettingValuesException>().With.Message.Contains("AppUser").And.Message.Contains("watermarkColumn"),
            "Selecting an Object Type the settings cannot serve is refused where it is done, with the Connector's own message, rather than by the next Delta Import.");
        _connectedSystemRepository.Verify(r => r.UpdateConnectedSystemSchemaAsync(It.IsAny<ConnectedSystem>()), Times.Never);
    }

    [Test]
    public async Task UpdateConnectedSystemSchemaAsync_ConnectorWithoutSelectionValidation_PersistsWithoutAskingAsync()
    {
        _jim.ConnectedSystems.ConnectorFactory = new StubConnectorFactory(new StubPlainConnector());
        var connectedSystem = CreateConnectedSystem(("Person", true));

        await _jim.ConnectedSystems.UpdateConnectedSystemSchemaAsync(connectedSystem, TestUtilities.GetInitiatedBy());

        _connectedSystemRepository.Verify(r => r.UpdateConnectedSystemSchemaAsync(connectedSystem), Times.Once);
    }

    #endregion

    #region Per-Object Type update (REST API and PowerShell)

    [Test]
    public async Task UpdateObjectTypeAsync_SelectingAnObjectTypeTheConnectorAccepts_PersistsAsync()
    {
        var connectedSystem = CreateConnectedSystem(("Person", true), ("AppUser", false));
        ArrangePersisted(connectedSystem);
        var appUser = connectedSystem.ObjectTypes!.Single(objectType => objectType.Name == "AppUser");
        var pending = new ConnectedSystemObjectType { Id = appUser.Id, Name = appUser.Name, ConnectedSystemId = connectedSystem.Id, Selected = true };

        await _jim.ConnectedSystems.UpdateObjectTypeAsync(pending, TestUtilities.GetInitiatedBy());

        using (Assert.EnterMultipleScope())
        {
            _connectedSystemRepository.Verify(r => r.UpdateObjectTypeAsync(pending), Times.Once);
            Assert.That(_connector.SelectedNamesSeen, Is.EquivalentTo(new[] { "Person", "AppUser" }),
                "The Connector judges the selection as it will stand after the update: the persisted Object Types with this one's new state in place of its old one.");
        }
    }

    [Test]
    public void UpdateObjectTypeAsync_SelectingAnObjectTypeTheConnectorRefuses_ThrowsAndDoesNotPersist()
    {
        _connector.RefuseWhenSelected("AppUser", "Delta Import Mode is 'Change-Log Table', but Object Type 'AppUser' has no 'changeLog'.");
        var connectedSystem = CreateConnectedSystem(("Person", true), ("AppUser", false));
        ArrangePersisted(connectedSystem);
        var appUser = connectedSystem.ObjectTypes!.Single(objectType => objectType.Name == "AppUser");
        var pending = new ConnectedSystemObjectType { Id = appUser.Id, Name = appUser.Name, ConnectedSystemId = connectedSystem.Id, Selected = true };

        Assert.That(async () => await _jim.ConnectedSystems.UpdateObjectTypeAsync(pending, TestUtilities.GetInitiatedBy()),
            Throws.TypeOf<InvalidSettingValuesException>().With.Message.Contains("AppUser").And.Message.Contains("changeLog"));
        _connectedSystemRepository.Verify(r => r.UpdateObjectTypeAsync(It.IsAny<ConnectedSystemObjectType>()), Times.Never);
    }

    [Test]
    public async Task UpdateObjectTypeAsync_DeselectingAnObjectType_PersistsWithoutAskingTheConnectorAsync()
    {
        _connector.RefuseWhenSelected("AppUser", "would refuse if asked");
        var connectedSystem = CreateConnectedSystem(("Person", true), ("AppUser", true));
        ArrangePersisted(connectedSystem);
        var appUser = connectedSystem.ObjectTypes!.Single(objectType => objectType.Name == "AppUser");
        var pending = new ConnectedSystemObjectType { Id = appUser.Id, Name = appUser.Name, ConnectedSystemId = connectedSystem.Id, Selected = false };

        await _jim.ConnectedSystems.UpdateObjectTypeAsync(pending, TestUtilities.GetInitiatedBy());

        using (Assert.EnterMultipleScope())
        {
            _connectedSystemRepository.Verify(r => r.UpdateObjectTypeAsync(pending), Times.Once);
            Assert.That(_connector.SelectedNamesSeen, Is.Null,
                "Deselecting can only shrink what a Delta Import reads, so it can never newly violate the settings; deselecting an Object Type that lacks what the mode needs is exactly how an administrator fixes the refusal.");
        }
    }

    [Test]
    public async Task UpdateObjectTypeAsync_ApiKeyInitiated_IsJudgedTheSameWayAsync()
    {
        _connector.RefuseWhenSelected("AppUser", "Delta Import Mode is 'Watermark Column', but Object Type 'AppUser' has no 'watermarkColumn'.");
        var connectedSystem = CreateConnectedSystem(("Person", true), ("AppUser", false));
        ArrangePersisted(connectedSystem);
        var appUser = connectedSystem.ObjectTypes!.Single(objectType => objectType.Name == "AppUser");
        var pending = new ConnectedSystemObjectType { Id = appUser.Id, Name = appUser.Name, ConnectedSystemId = connectedSystem.Id, Selected = true };
        var apiKey = new JIM.Models.Security.ApiKey { Id = Guid.NewGuid(), Name = "automation", KeyHash = "hash", KeyPrefix = "jim_" };

        Assert.That(async () => await _jim.ConnectedSystems.UpdateObjectTypeAsync(pending, apiKey),
            Throws.TypeOf<InvalidSettingValuesException>().With.Message.Contains("AppUser"));
        _connectedSystemRepository.Verify(r => r.UpdateObjectTypeAsync(It.IsAny<ConnectedSystemObjectType>()), Times.Never);
        await Task.CompletedTask;
    }

    #endregion

    #region Settings validation

    [Test]
    public void ValidateConnectedSystemSettings_ConnectorRefusesTheSelection_CarriesTheRefusal()
    {
        _connector.RefuseWhenSelected("AppUser", "Delta Import Mode is 'Watermark Column', but Object Type 'AppUser' has no 'watermarkColumn'.");
        var connectedSystem = CreateConnectedSystem(("Person", true), ("AppUser", true));

        var results = _jim.ConnectedSystems.ValidateConnectedSystemSettings(connectedSystem);

        Assert.That(results.Where(result => !result.IsValid).Select(result => result.ErrorMessage), Has.Exactly(1).Items.And.Some.Contains("AppUser"),
            "The Settings tab and the settings-writing endpoint show the administrator that the mode they are saving cannot serve what is selected.");
    }

    [Test]
    public void ValidateConnectedSystemSettings_NoSchemaImportedYet_AsksTheConnectorWithNothingSelected()
    {
        var connectedSystem = CreateConnectedSystem();
        connectedSystem.ObjectTypes = null;

        var results = _jim.ConnectedSystems.ValidateConnectedSystemSettings(connectedSystem);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(results.All(result => result.IsValid), Is.True);
            Assert.That(_connector.SelectedNamesSeen, Is.Empty, "No schema is no selection, not a reason to skip the Connector.");
        }
    }

    #endregion

    #region Helpers

    private void ArrangePersisted(ConnectedSystem connectedSystem)
    {
        _connectedSystemRepository.Setup(r => r.GetConnectedSystemCoreAsync(connectedSystem.Id, It.IsAny<bool>())).ReturnsAsync(connectedSystem);
        // What the repository hands back is a separate graph from the Object Type being updated; the server has to
        // fold the pending change in itself.
        _connectedSystemRepository.Setup(r => r.GetObjectTypesAsync(connectedSystem.Id))
            .ReturnsAsync(() => connectedSystem.ObjectTypes!.Select(objectType => new ConnectedSystemObjectType
            {
                Id = objectType.Id,
                Name = objectType.Name,
                Selected = objectType.Selected,
                ConnectedSystemId = connectedSystem.Id
            }).ToList());
    }

    private static ConnectedSystem CreateConnectedSystem(params (string Name, bool Selected)[] objectTypes)
    {
        var connectorDefinition = new ConnectorDefinition { Name = "Stub Selection Connector" };
        var setting = new ConnectorDefinitionSetting { Name = "Dummy Setting", Type = ConnectedSystemSettingType.Text };
        connectorDefinition.Settings.Add(setting);

        var id = 1;
        return new ConnectedSystem
        {
            Id = 7,
            Name = "HR Database",
            ConnectorDefinition = connectorDefinition,
            SettingValues = [new ConnectedSystemSettingValue { Setting = setting, StringValue = "value" }],
            ObjectTypes = objectTypes.Select(objectType => new ConnectedSystemObjectType
            {
                Id = id++,
                Name = objectType.Name,
                Selected = objectType.Selected,
                ConnectedSystemId = 7
            }).ToList()
        };
    }

    /// <summary>
    /// A Connector that refuses whenever a named Object Type is among the selected ones, and records what it was shown.
    /// </summary>
    private sealed class StubSelectionValidatingConnector : IConnector, IConnectorObjectTypeSelectionValidation
    {
        private string? _refusedObjectType;
        private string? _refusalMessage;

        public string Name => "Stub Selection Connector";
        public string? Description => null;
        public string? Url => null;

        /// <summary>The selected Object Type names the Connector was last shown, or null if it was never asked.</summary>
        public List<string>? SelectedNamesSeen { get; private set; }

        public void RefuseWhenSelected(string objectTypeName, string message)
        {
            _refusedObjectType = objectTypeName;
            _refusalMessage = message;
        }

        public List<ConnectorSettingValueValidationResult> ValidateObjectTypeSelection(List<ConnectedSystemSettingValue> settingValues, IReadOnlyCollection<ConnectedSystemObjectType> objectTypes, ILogger logger)
        {
            SelectedNamesSeen = objectTypes.Where(objectType => objectType.Selected).Select(objectType => objectType.Name).ToList();

            if (_refusedObjectType != null && SelectedNamesSeen.Contains(_refusedObjectType))
                return [new ConnectorSettingValueValidationResult { IsValid = false, ErrorMessage = _refusalMessage, SettingValue = settingValues[0] }];

            return [];
        }
    }

    private sealed class StubPlainConnector : IConnector
    {
        public string Name => "Stub Plain Connector";
        public string? Description => null;
        public string? Url => null;
    }

    private sealed class StubConnectorFactory(IConnector connector) : IConnectorFactory
    {
        public IConnector Create(string connectorName, ICredentialProtection? credentialProtection = null, ICertificateProvider? certificateProvider = null) => connector;
    }

    #endregion
}
