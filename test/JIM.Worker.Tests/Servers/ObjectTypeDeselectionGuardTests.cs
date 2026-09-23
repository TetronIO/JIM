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
using JIM.Models.Logic;
using JIM.Models.Logic.DTOs;
using JIM.Models.Security;
using JIM.Models.Staging;
using Moq;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// Deselecting a Connected System Object Type takes it out of management: the next Full Import obsoletes its objects
/// (#1474). An enabled Synchronisation Rule bound to a deselected type contradicts that, and is the one state in which
/// obsoleting would do harm (an outbound rule would act on the disconnected objects all over again), so JIM refuses
/// to reach it from either side: deselecting a type an enabled rule still manages, and saving an enabled rule against
/// a deselected type. Both are refused in the application layer, so the portal, the REST API and PowerShell all get
/// the same answer.
/// </summary>
[TestFixture]
public class ObjectTypeDeselectionGuardTests
{
    private const int ConnectedSystemId = 7;
    private const int OtherConnectedSystemId = 8;
    private const int UserTypeId = 1;
    private const int GroupTypeId = 2;

    private Mock<IConnectedSystemRepository> _connectedSystemRepository = null!;
    private JimApplication _jim = null!;
    private MetaverseObject _initiatedBy = null!;
    private ApiKey _apiKey = null!;
    private List<SyncRuleHeader> _syncRuleHeaders = null!;

    [SetUp]
    public void SetUp()
    {
        TestUtilities.SetEnvironmentVariables();

        var repository = new Mock<IRepository>();
        var activityRepository = new Mock<IActivityRepository>();
        var syncRepository = new Mock<ISyncRepository>();
        _connectedSystemRepository = new Mock<IConnectedSystemRepository>();
        repository.Setup(r => r.Activity).Returns(activityRepository.Object);
        repository.Setup(r => r.ConnectedSystems).Returns(_connectedSystemRepository.Object);
        repository.Setup(r => r.Metaverse).Returns(new Mock<IMetaverseRepository>().Object);
        repository.Setup(r => r.Sync).Returns(syncRepository.Object);
        activityRepository.Setup(r => r.CreateActivityAsync(It.IsAny<Activity>())).Returns(Task.CompletedTask);
        activityRepository.Setup(r => r.UpdateActivityAsync(It.IsAny<Activity>())).Returns(Task.CompletedTask);

        _connectedSystemRepository.Setup(r => r.UpdateConnectedSystemSchemaAsync(It.IsAny<ConnectedSystem>())).Returns(Task.CompletedTask);
        _connectedSystemRepository.Setup(r => r.UpdateObjectTypeAsync(It.IsAny<ConnectedSystemObjectType>())).Returns(Task.CompletedTask);
        _connectedSystemRepository.Setup(r => r.CreateSyncRuleAsync(It.IsAny<SyncRule>())).Returns(Task.CompletedTask);
        _connectedSystemRepository.Setup(r => r.UpdateSyncRuleAsync(It.IsAny<SyncRule>())).Returns(Task.CompletedTask);
        _connectedSystemRepository.Setup(r => r.GetImportMappingTargetMetaverseAttributesAsync(It.IsAny<int>()))
            .ReturnsAsync(new Dictionary<int, int>());

        _syncRuleHeaders = [];
        _connectedSystemRepository.Setup(r => r.GetSyncRuleHeadersAsync(It.IsAny<int?>(), It.IsAny<SyncRuleDirection?>()))
            .ReturnsAsync(() => _syncRuleHeaders);

        _initiatedBy = TestUtilities.GetInitiatedBy();
        _apiKey = new ApiKey { Id = Guid.NewGuid(), Name = "automation", KeyHash = "hash", KeyPrefix = "jim_" };
        _jim = new JimApplication(repository.Object, syncRepository: syncRepository.Object,
            connectorFactory: new StubConnectorFactory(new StubPlainConnector()));
    }

    [TearDown]
    public void TearDown() => _jim?.Dispose();

    #region Deselecting an Object Type: whole-graph schema save (portal)

    [Test]
    public void UpdateConnectedSystemSchemaAsync_DeselectedObjectTypeHasEnabledSyncRule_ThrowsAndDoesNotPersist()
    {
        BindRule("Provision groups", GroupTypeId, enabled: true);
        var connectedSystem = CreateConnectedSystem(userSelected: true, groupSelected: false);

        Assert.That(async () => await _jim.ConnectedSystems.UpdateConnectedSystemSchemaAsync(connectedSystem, _initiatedBy),
            Throws.TypeOf<InvalidSettingValuesException>()
                .With.Message.Contains("'group'")
                .And.Message.Contains("Provision groups"),
            "the refusal must name the Object Type and the Synchronisation Rule to disable, because that is the fix.");
        _connectedSystemRepository.Verify(r => r.UpdateConnectedSystemSchemaAsync(It.IsAny<ConnectedSystem>()), Times.Never);
    }

    [Test]
    public async Task UpdateConnectedSystemSchemaAsync_DeselectedObjectTypeHasOnlyDisabledSyncRule_PersistsAsync()
    {
        BindRule("Old group rule", GroupTypeId, enabled: false);
        var connectedSystem = CreateConnectedSystem(userSelected: true, groupSelected: false);

        await _jim.ConnectedSystems.UpdateConnectedSystemSchemaAsync(connectedSystem, _initiatedBy);

        _connectedSystemRepository.Verify(r => r.UpdateConnectedSystemSchemaAsync(connectedSystem), Times.Once,
            "a disabled Synchronisation Rule manages nothing, so it does not stand in the way of deselecting the type.");
    }

    [Test]
    public async Task UpdateConnectedSystemSchemaAsync_EnabledSyncRuleOnSelectedObjectTypeOnly_PersistsAsync()
    {
        BindRule("Import users", UserTypeId, enabled: true);
        var connectedSystem = CreateConnectedSystem(userSelected: true, groupSelected: false);

        await _jim.ConnectedSystems.UpdateConnectedSystemSchemaAsync(connectedSystem, _initiatedBy);

        _connectedSystemRepository.Verify(r => r.UpdateConnectedSystemSchemaAsync(connectedSystem), Times.Once);
    }

    [Test]
    public async Task UpdateConnectedSystemSchemaAsync_EnabledSyncRuleOnAnotherConnectedSystem_PersistsAsync()
    {
        // Object Type ids are unique, but the rule's Connected System is part of the match all the same: a rule is
        // only ever bound to a type on its own Connected System.
        BindRule("Groups elsewhere", GroupTypeId, enabled: true, connectedSystemId: OtherConnectedSystemId);
        var connectedSystem = CreateConnectedSystem(userSelected: true, groupSelected: false);

        await _jim.ConnectedSystems.UpdateConnectedSystemSchemaAsync(connectedSystem, _initiatedBy);

        _connectedSystemRepository.Verify(r => r.UpdateConnectedSystemSchemaAsync(connectedSystem), Times.Once);
    }

    #endregion

    #region Deselecting an Object Type: single Object Type update (REST API and PowerShell)

    [Test]
    public void UpdateObjectTypeAsync_DeselectingWithEnabledSyncRule_ThrowsAndDoesNotPersist()
    {
        BindRule("Provision groups", GroupTypeId, enabled: true);
        BindRule("Import groups", GroupTypeId, enabled: true);

        Assert.That(async () => await _jim.ConnectedSystems.UpdateObjectTypeAsync(GroupType(selected: false), _initiatedBy),
            Throws.TypeOf<InvalidSettingValuesException>()
                .With.Message.Contains("2 enabled Synchronisation Rules still manage it: Import groups, Provision groups"));
        _connectedSystemRepository.Verify(r => r.UpdateObjectTypeAsync(It.IsAny<ConnectedSystemObjectType>()), Times.Never);
    }

    [Test]
    public void UpdateObjectTypeAsync_ApiKeyDeselectingWithEnabledSyncRule_ThrowsAndDoesNotPersist()
    {
        BindRule("Provision groups", GroupTypeId, enabled: true);

        Assert.That(async () => await _jim.ConnectedSystems.UpdateObjectTypeAsync(GroupType(selected: false), _apiKey),
            Throws.TypeOf<InvalidSettingValuesException>().With.Message.Contains("Provision groups"));
        _connectedSystemRepository.Verify(r => r.UpdateObjectTypeAsync(It.IsAny<ConnectedSystemObjectType>()), Times.Never);
    }

    [Test]
    public async Task UpdateObjectTypeAsync_DeselectingWithNoEnabledSyncRule_PersistsAsync()
    {
        BindRule("Old group rule", GroupTypeId, enabled: false);
        var groupType = GroupType(selected: false);

        await _jim.ConnectedSystems.UpdateObjectTypeAsync(groupType, _initiatedBy);

        _connectedSystemRepository.Verify(r => r.UpdateObjectTypeAsync(groupType), Times.Once);
    }

    [Test]
    public async Task UpdateObjectTypeAsync_SelectingWithEnabledSyncRule_PersistsAsync()
    {
        BindRule("Provision groups", GroupTypeId, enabled: true);
        var groupType = GroupType(selected: true);

        await _jim.ConnectedSystems.UpdateObjectTypeAsync(groupType, _initiatedBy);

        _connectedSystemRepository.Verify(r => r.UpdateObjectTypeAsync(groupType), Times.Once,
            "selecting a type brings it into management, which is exactly what a rule bound to it needs.");
    }

    #endregion

    #region Saving a Synchronisation Rule against a deselected Object Type

    [Test]
    public void CreateOrUpdateSyncRuleAsync_EnabledRuleOnDeselectedObjectType_ThrowsAndDoesNotPersist()
    {
        ArrangePersistedObjectType(GroupType(selected: false));

        Assert.That(async () => await _jim.ConnectedSystems.CreateOrUpdateSyncRuleAsync(Rule(enabled: true), _initiatedBy),
            Throws.TypeOf<ArgumentException>()
                .With.Message.Contains("'group'")
                .And.Message.Contains("not selected"),
            "an enabled rule against a deselected type is the state the refusal on the other side exists to prevent.");

        using (Assert.EnterMultipleScope())
        {
            _connectedSystemRepository.Verify(r => r.CreateSyncRuleAsync(It.IsAny<SyncRule>()), Times.Never);
            _connectedSystemRepository.Verify(r => r.UpdateSyncRuleAsync(It.IsAny<SyncRule>()), Times.Never);
        }
    }

    [Test]
    public void CreateOrUpdateSyncRuleAsync_ApiKeyEnabledRuleOnDeselectedObjectType_ThrowsAndDoesNotPersist()
    {
        ArrangePersistedObjectType(GroupType(selected: false));

        Assert.That(async () => await _jim.ConnectedSystems.CreateOrUpdateSyncRuleAsync(Rule(enabled: true), _apiKey),
            Throws.TypeOf<ArgumentException>().With.Message.Contains("'group'"));
        _connectedSystemRepository.Verify(r => r.CreateSyncRuleAsync(It.IsAny<SyncRule>()), Times.Never);
    }

    [Test]
    public async Task CreateOrUpdateSyncRuleAsync_DisabledRuleOnDeselectedObjectType_PersistsAsync()
    {
        // Disabling the rules is the first step of taking a type out of management, and a disabled rule has to be
        // editable while the type stays deselected.
        ArrangePersistedObjectType(GroupType(selected: false));

        var saved = await _jim.ConnectedSystems.CreateOrUpdateSyncRuleAsync(Rule(enabled: false), _initiatedBy);

        Assert.That(saved, Is.True);
    }

    [Test]
    public async Task CreateOrUpdateSyncRuleAsync_EnabledRuleOnSelectedObjectType_PersistsAsync()
    {
        ArrangePersistedObjectType(GroupType(selected: true));

        var saved = await _jim.ConnectedSystems.CreateOrUpdateSyncRuleAsync(Rule(enabled: true), _initiatedBy);

        Assert.That(saved, Is.True);
    }

    #endregion

    #region Helpers

    private void BindRule(string name, int objectTypeId, bool enabled, int connectedSystemId = ConnectedSystemId) =>
        _syncRuleHeaders.Add(new SyncRuleHeader
        {
            Id = _syncRuleHeaders.Count + 1,
            Name = name,
            ConnectedSystemId = connectedSystemId,
            ConnectedSystemObjectTypeId = objectTypeId,
            Enabled = enabled
        });

    private void ArrangePersistedObjectType(ConnectedSystemObjectType objectType) =>
        _connectedSystemRepository.Setup(r => r.GetObjectTypeAsync(objectType.Id)).ReturnsAsync(objectType);

    private static ConnectedSystemObjectType GroupType(bool selected) =>
        new() { Id = GroupTypeId, Name = "group", ConnectedSystemId = ConnectedSystemId, Selected = selected };

    private static ConnectedSystem CreateConnectedSystem(bool userSelected, bool groupSelected)
    {
        var connectorDefinition = new ConnectorDefinition { Name = "Stub Plain Connector" };
        var setting = new ConnectorDefinitionSetting { Name = "Dummy Setting", Type = ConnectedSystemSettingType.Text };
        connectorDefinition.Settings.Add(setting);

        return new ConnectedSystem
        {
            Id = ConnectedSystemId,
            Name = "Glitterband Directory",
            ConnectorDefinition = connectorDefinition,
            SettingValues = [new ConnectedSystemSettingValue { Setting = setting, StringValue = "value" }],
            ObjectTypes =
            [
                new ConnectedSystemObjectType { Id = UserTypeId, Name = "user", ConnectedSystemId = ConnectedSystemId, Selected = userSelected },
                new ConnectedSystemObjectType { Id = GroupTypeId, Name = "group", ConnectedSystemId = ConnectedSystemId, Selected = groupSelected }
            ]
        };
    }

    /// <summary>
    /// A Synchronisation Rule against the group Object Type. The navigation is deliberately left unselected: the
    /// server must judge the Object Type as it is persisted, not as whatever copy of it the caller is holding.
    /// </summary>
    private static SyncRule Rule(bool enabled) => new()
    {
        Id = 12,
        Name = "Provision groups",
        Direction = SyncRuleDirection.Export,
        Enabled = enabled,
        ConnectedSystemId = ConnectedSystemId,
        ConnectedSystem = new ConnectedSystem { Id = ConnectedSystemId, Name = "Glitterband Directory" },
        MetaverseObjectType = new MetaverseObjectType { Id = 1, Name = "group" },
        ConnectedSystemObjectTypeId = GroupTypeId,
        ConnectedSystemObjectType = new ConnectedSystemObjectType { Id = GroupTypeId, Name = "group" }
    };

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
