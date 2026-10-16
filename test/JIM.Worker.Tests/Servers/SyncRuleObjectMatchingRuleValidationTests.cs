// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Connectors;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Interfaces;
using JIM.Models.Logic;
using JIM.Models.Logic.DTOs;
using JIM.Models.Staging;
using Moq;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// In advanced matching mode the portal adds Object Matching Rules to a Synchronisation Rule and stores them with
/// it, never through the Object Matching Rule endpoints. The Synchronisation Rule save therefore refuses an unworkable
/// matching rule exactly as those endpoints do (#2052), or the portal would be the one way round the check.
/// </summary>
[TestFixture]
public class SyncRuleObjectMatchingRuleValidationTests
{
    private const int ConnectedSystemId = 3;
    private const int UserTypeId = 7;

    private Mock<IConnectedSystemRepository> _connectedSystemRepository = null!;
    private JimApplication _jim = null!;
    private MetaverseObject _initiatedBy = null!;

    [SetUp]
    public void SetUp()
    {
        TestUtilities.SetEnvironmentVariables();

        var repository = new Mock<IRepository>();
        var activityRepository = new Mock<IActivityRepository>();
        var syncRepository = TestUtilities.StubQueuedChangeWithdrawal(new Mock<ISyncRepository>());
        _connectedSystemRepository = new Mock<IConnectedSystemRepository>();
        repository.Setup(r => r.Activity).Returns(activityRepository.Object);
        repository.Setup(r => r.ConnectedSystems).Returns(_connectedSystemRepository.Object);
        repository.Setup(r => r.Metaverse).Returns(new Mock<IMetaverseRepository>().Object);
        repository.Setup(r => r.Sync).Returns(syncRepository.Object);
        activityRepository.Setup(r => r.CreateActivityAsync(It.IsAny<Activity>())).Returns(Task.CompletedTask);
        activityRepository.Setup(r => r.UpdateActivityAsync(It.IsAny<Activity>())).Returns(Task.CompletedTask);

        _connectedSystemRepository.Setup(r => r.CreateSyncRuleAsync(It.IsAny<SyncRule>())).Returns(Task.CompletedTask);
        _connectedSystemRepository.Setup(r => r.UpdateSyncRuleAsync(It.IsAny<SyncRule>())).Returns(Task.CompletedTask);
        _connectedSystemRepository.Setup(r => r.GetImportMappingTargetMetaverseAttributesAsync(It.IsAny<int>()))
            .ReturnsAsync(new Dictionary<int, int>());
        _connectedSystemRepository.Setup(r => r.GetSyncRuleHeadersAsync(It.IsAny<int?>(), It.IsAny<SyncRuleDirection?>()))
            .ReturnsAsync(new List<SyncRuleHeader>());
        _connectedSystemRepository.Setup(r => r.GetObjectTypeAsync(UserTypeId)).ReturnsAsync(UserType());

        _initiatedBy = TestUtilities.GetInitiatedBy();
        _jim = new JimApplication(repository.Object, syncRepository: syncRepository.Object,
            connectorFactory: new StubConnectorFactory(new StubPlainConnector()));
    }

    [TearDown]
    public void TearDown() => _jim?.Dispose();

    [Test]
    public void CreateOrUpdateSyncRuleAsync_AdvancedModeMatchingRuleReadingTheMetaverse_ThrowsAndDoesNotPersist()
    {
        var syncRule = ImportRule(ObjectMatchingRuleMode.SyncRule, "Trim(cs[\"employeeId\"]) + Trim(mv[\"Department\"])");

        Assert.That(async () => await _jim.ConnectedSystems.CreateOrUpdateSyncRuleAsync(syncRule, _initiatedBy),
            Throws.TypeOf<ArgumentException>()
                .With.Message.Contains("Object Matching Rule 1")
                .And.Message.Contains("mv[\"Department\"]"),
            "the refusal names which of the Synchronisation Rule's matching rules is at fault, then what is wrong with it");

        _connectedSystemRepository.Verify(r => r.CreateSyncRuleAsync(It.IsAny<SyncRule>()), Times.Never);
    }

    [Test]
    public async Task CreateOrUpdateSyncRuleAsync_AdvancedModeWorkableExpressionRule_PersistsAsync()
    {
        var syncRule = ImportRule(ObjectMatchingRuleMode.SyncRule, "\"E\" + cs[\"employeeId\"]");

        var saved = await _jim.ConnectedSystems.CreateOrUpdateSyncRuleAsync(syncRule, _initiatedBy);

        Assert.That(saved, Is.True);
        _connectedSystemRepository.Verify(r => r.CreateSyncRuleAsync(syncRule), Times.Once);
    }

    [Test]
    public async Task CreateOrUpdateSyncRuleAsync_SimpleModeSystem_ClearsTheRulesRatherThanJudgingThemAsync()
    {
        // In simple mode a Synchronisation Rule's own matching rules are inert and are cleared on save, so an
        // unworkable one is no reason to refuse the save.
        var syncRule = ImportRule(ObjectMatchingRuleMode.ConnectedSystem, "mv[\"Department\"]");

        var saved = await _jim.ConnectedSystems.CreateOrUpdateSyncRuleAsync(syncRule, _initiatedBy);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(saved, Is.True);
            Assert.That(syncRule.ObjectMatchingRules, Is.Empty);
        }
    }

    private static SyncRule ImportRule(ObjectMatchingRuleMode mode, string expression) => new()
    {
        Name = "Import users",
        Direction = SyncRuleDirection.Import,
        Enabled = false,
        ConnectedSystemId = ConnectedSystemId,
        ConnectedSystem = new ConnectedSystem { Id = ConnectedSystemId, Name = "HR", ObjectMatchingRuleMode = mode },
        MetaverseObjectType = new MetaverseObjectType { Id = 1, Name = "Person" },
        ConnectedSystemObjectTypeId = UserTypeId,
        // The portal's copy of the type, as loaded for the Synchronisation Rule page: the attributes are not
        // guaranteed, so the server reads the persisted type.
        ConnectedSystemObjectType = new ConnectedSystemObjectType { Id = UserTypeId, Name = "user", ConnectedSystemId = ConnectedSystemId },
        ObjectMatchingRules =
        [
            new ObjectMatchingRule
            {
                Order = 0,
                TargetMetaverseAttributeId = 201,
                TargetMetaverseAttribute = new MetaverseAttribute { Id = 201, Name = "Employee ID", Type = AttributeDataType.Text },
                Sources = [new ObjectMatchingRuleSource { Order = 0, Expression = expression }]
            }
        ]
    };

    private static ConnectedSystemObjectType UserType() => new()
    {
        Id = UserTypeId,
        Name = "user",
        ConnectedSystemId = ConnectedSystemId,
        Selected = true,
        Attributes = [new ConnectedSystemObjectTypeAttribute { Id = 101, Name = "employeeId", Type = AttributeDataType.Text }]
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
}
