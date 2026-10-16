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
/// Saving a Synchronisation Rule checks its Attribute Flows' expressions in the application layer (#2055 Phase 3), so
/// the portal's whole-rule save refuses what the REST API and PowerShell refuse. A mapping that is new, or whose
/// expressions or target changed, is refused for any problem; one the save leaves as it was only ever warns, so an
/// administrator can always disable a rule, or remove a mapping, while another mapping is broken.
/// </summary>
[TestFixture]
public class SyncRuleAttributeFlowExpressionValidationTests
{
    private const int ConnectedSystemId = 3;
    private const int UserTypeId = 7;
    private const int PersonTypeId = 1;

    private Mock<IConnectedSystemRepository> _connectedSystemRepository = null!;
    private Mock<IMetaverseRepository> _metaverseRepository = null!;
    private Dictionary<int, SyncRuleMappingExpressionState> _persisted = null!;
    private JimApplication _jim = null!;
    private MetaverseObject _initiatedBy = null!;
    private MetaverseAttribute _displayName = null!;
    private MetaverseAttribute _department = null!;

    [SetUp]
    public void SetUp()
    {
        TestUtilities.SetEnvironmentVariables();

        var repository = new Mock<IRepository>();
        var activityRepository = new Mock<IActivityRepository>();
        var syncRepository = TestUtilities.StubQueuedChangeWithdrawal(new Mock<ISyncRepository>());
        _connectedSystemRepository = new Mock<IConnectedSystemRepository>();
        _metaverseRepository = new Mock<IMetaverseRepository>();
        repository.Setup(r => r.Activity).Returns(activityRepository.Object);
        repository.Setup(r => r.ConnectedSystems).Returns(_connectedSystemRepository.Object);
        // Saves read the configuration test cases covering what they save (the save gate, #2055 Phase 4b).
        repository.Setup(r => r.ConfigurationTests).Returns(TestUtilities.CreateEmptyConfigurationTestRepository());
        repository.Setup(r => r.Metaverse).Returns(_metaverseRepository.Object);
        repository.Setup(r => r.Sync).Returns(syncRepository.Object);
        activityRepository.Setup(r => r.CreateActivityAsync(It.IsAny<Activity>())).Returns(Task.CompletedTask);
        activityRepository.Setup(r => r.UpdateActivityAsync(It.IsAny<Activity>())).Returns(Task.CompletedTask);

        _displayName = new MetaverseAttribute { Id = 201, Name = "Display Name", Type = AttributeDataType.Text };
        _department = new MetaverseAttribute { Id = 202, Name = "Department", Type = AttributeDataType.Text };
        _metaverseRepository.Setup(r => r.GetMetaverseObjectTypeAsync(PersonTypeId, It.IsAny<bool>()))
            .ReturnsAsync(new MetaverseObjectType { Id = PersonTypeId, Name = "Person", Attributes = [_displayName, _department] });

        _persisted = [];
        _connectedSystemRepository.Setup(r => r.GetSyncRuleMappingExpressionStatesAsync(It.IsAny<IReadOnlyCollection<int>>()))
            .ReturnsAsync(() => _persisted);
        _connectedSystemRepository.Setup(r => r.CreateSyncRuleAsync(It.IsAny<SyncRule>())).Returns(Task.CompletedTask);
        _connectedSystemRepository.Setup(r => r.UpdateSyncRuleAsync(It.IsAny<SyncRule>())).Returns(Task.CompletedTask);
        _connectedSystemRepository.Setup(r => r.GetImportMappingTargetMetaverseAttributesAsync(It.IsAny<int>()))
            .ReturnsAsync(new Dictionary<int, int>());
        _connectedSystemRepository.Setup(r => r.GetSyncRuleHeadersAsync(It.IsAny<int?>(), It.IsAny<SyncRuleDirection?>()))
            .ReturnsAsync(new List<SyncRuleHeader>());
        _connectedSystemRepository.Setup(r => r.GetObjectTypeAsync(UserTypeId)).ReturnsAsync(UserType());
        _connectedSystemRepository.Setup(r => r.GetImportSyncRulesForMetaverseObjectTypeAsync(It.IsAny<int>()))
            .ReturnsAsync(new List<SyncRule>());
        _connectedSystemRepository.Setup(r => r.GetImportSyncRuleMappingsForMetaverseAttributeAsync(It.IsAny<int>(), It.IsAny<int>()))
            .ReturnsAsync(new List<SyncRuleMapping>());
        _connectedSystemRepository.Setup(r => r.GetSyncRuleMappingsAsync(It.IsAny<int>())).ReturnsAsync(new List<SyncRuleMapping>());
        _connectedSystemRepository.Setup(r => r.GetSyncRuleMappingKeepUniqueAsync(It.IsAny<IReadOnlyCollection<int>>()))
            .ReturnsAsync(new Dictionary<int, bool>());

        _initiatedBy = TestUtilities.GetInitiatedBy();
        _jim = new JimApplication(repository.Object, syncRepository: syncRepository.Object,
            connectorFactory: new StubConnectorFactory(new StubPlainConnector()));
    }

    [TearDown]
    public void TearDown() => _jim?.Dispose();

    [Test]
    public void CreateOrUpdateSyncRuleAsync_NewMappingThatDoesNotParse_ThrowsNamingItAndDoesNotPersist()
    {
        var syncRule = ImportRule(ImportMapping(0, _displayName, "Upper(cs[\"sn\"]"));

        Assert.That(async () => await _jim.ConnectedSystems.CreateOrUpdateSyncRuleAsync(syncRule, _initiatedBy),
            Throws.TypeOf<ArgumentException>().With.Message.Contains("Display Name").And.Message.Contains("does not parse"));

        _connectedSystemRepository.Verify(r => r.CreateSyncRuleAsync(It.IsAny<SyncRule>()), Times.Never);
    }

    [Test]
    public void CreateOrUpdateSyncRuleAsync_NewMappingReadingAnUnknownAttribute_Throws()
    {
        var syncRule = ImportRule(ImportMapping(0, _displayName, "Upper(cs[\"surname\"])"));

        Assert.That(async () => await _jim.ConnectedSystems.CreateOrUpdateSyncRuleAsync(syncRule, _initiatedBy),
            Throws.TypeOf<ArgumentException>().With.Message.Contains("cs[\"surname\"]"));
    }

    [Test]
    public void CreateOrUpdateSyncRuleAsync_NewExportMappingReadingTheConnectedSystem_Throws()
    {
        var syncRule = ExportRule(ExportMapping(0, "Upper(cs[\"sn\"])"));

        Assert.That(async () => await _jim.ConnectedSystems.CreateOrUpdateSyncRuleAsync(syncRule, _initiatedBy),
            Throws.TypeOf<ArgumentException>().With.Message.Contains("export Attribute Flow"));
    }

    [Test]
    public async Task CreateOrUpdateSyncRuleAsync_WorkableMappings_PersistsWithNoWarningAsync()
    {
        var mapping = ImportMapping(0, _displayName, "Upper(cs[\"sn\"])");

        var saved = await _jim.ConnectedSystems.CreateOrUpdateSyncRuleAsync(ImportRule(mapping), _initiatedBy);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(saved, Is.True);
            Assert.That(mapping.SaveWarnings, Is.Empty);
        }
    }

    [Test]
    public async Task CreateOrUpdateSyncRuleAsync_NewMappingCallingANonRepeatableFunction_SavesWithAWarningAsync()
    {
        var mapping = ImportMapping(0, _displayName, "\"synced \" + FormatDate(Now(), \"yyyy-MM-dd\")");

        var saved = await _jim.ConnectedSystems.CreateOrUpdateSyncRuleAsync(ImportRule(mapping), _initiatedBy);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(saved, Is.True);
            Assert.That(mapping.SaveWarnings, Has.One.Contains("Now()"));
        }
    }

    [Test]
    public void CreateOrUpdateSyncRuleAsync_ExistingMappingWhoseExpressionChangedToOneThatDoesNotParse_Throws()
    {
        _persisted[11] = State(_displayName.Id, "Upper(cs[\"sn\"])");
        var syncRule = ImportRule(ImportMapping(11, _displayName, "Upper(cs[\"sn\"]"));
        syncRule.Id = 5;

        Assert.That(async () => await _jim.ConnectedSystems.CreateOrUpdateSyncRuleAsync(syncRule, _initiatedBy),
            Throws.TypeOf<ArgumentException>().With.Message.Contains("does not parse"));
    }

    [Test]
    public void CreateOrUpdateSyncRuleAsync_ExistingMappingRetargetedWithABrokenExpression_Throws()
    {
        // The same expression aimed somewhere else is a changed mapping, so it is checked in full.
        _persisted[11] = State(_department.Id, "Upper(cs[\"surname\"])");
        var syncRule = ImportRule(ImportMapping(11, _displayName, "Upper(cs[\"surname\"])"));
        syncRule.Id = 5;

        Assert.That(async () => await _jim.ConnectedSystems.CreateOrUpdateSyncRuleAsync(syncRule, _initiatedBy),
            Throws.TypeOf<ArgumentException>());
    }

    [Test]
    public async Task CreateOrUpdateSyncRuleAsync_UnchangedBrokenMapping_SavesWithItsProblemsAsWarningsAsync()
    {
        // Already broken before this save (saved before Phase 3, or the schema lost the attribute since): a save that
        // leaves it as it was is not refused for it, and is told about it.
        _persisted[11] = State(_displayName.Id, "Upper(cs[\"surname\"])");
        var broken = ImportMapping(11, _displayName, "Upper(cs[\"surname\"])");
        var syncRule = ImportRule(broken, ImportMapping(0, _department, "Lower(cs[\"sn\"])"));
        syncRule.Id = 5;

        var saved = await _jim.ConnectedSystems.CreateOrUpdateSyncRuleAsync(syncRule, _initiatedBy);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(saved, Is.True);
            Assert.That(broken.SaveWarnings, Has.One.Contains("cs[\"surname\"]"));
        }
    }

    [Test]
    public async Task CreateOrUpdateSyncRuleAsync_DisablingARuleWithAnUnchangedMappingThatDoesNotParse_SavesAsync()
    {
        _persisted[11] = State(_displayName.Id, "Upper(cs[\"sn\"]");
        var syncRule = ImportRule(ImportMapping(11, _displayName, "Upper(cs[\"sn\"]"));
        syncRule.Id = 5;
        syncRule.Enabled = false;

        Assert.That(await _jim.ConnectedSystems.CreateOrUpdateSyncRuleAsync(syncRule, _initiatedBy), Is.True);
    }

    [Test]
    public async Task CreateOrUpdateSyncRuleAsync_RemovingAMappingWhileAnotherUnchangedOneDoesNotParse_SavesAsync()
    {
        // The rule held mappings 11 (broken) and 12; the save removes 12 and leaves 11 as it was.
        _persisted[11] = State(_displayName.Id, "Upper(cs[\"sn\"]");
        _persisted[12] = State(_department.Id, "Lower(cs[\"sn\"])");
        var syncRule = ImportRule(ImportMapping(11, _displayName, "Upper(cs[\"sn\"]"));
        syncRule.Id = 5;
        syncRule.Enabled = true;

        Assert.That(await _jim.ConnectedSystems.CreateOrUpdateSyncRuleAsync(syncRule, _initiatedBy), Is.True);
    }

    [Test]
    public void CreateSyncRuleMappingAsync_NewMappingThatDoesNotParse_Throws()
    {
        var syncRule = ImportRule();
        syncRule.Id = 5;
        var mapping = ImportMapping(0, _displayName, "Upper(cs[\"sn\"]");
        mapping.SyncRule = syncRule;
        mapping.SyncRuleId = syncRule.Id;

        Assert.That(async () => await _jim.ConnectedSystems.CreateSyncRuleMappingAsync(mapping, _initiatedBy),
            Throws.TypeOf<ArgumentException>().With.Message.Contains("does not parse"));

        _connectedSystemRepository.Verify(r => r.CreateSyncRuleMappingAsync(It.IsAny<SyncRuleMapping>()), Times.Never);
    }

    private static SyncRuleMappingExpressionState State(int targetMetaverseAttributeId, params string[] expressions) =>
        new(targetMetaverseAttributeId, null, expressions);

    private SyncRuleMapping ImportMapping(int id, MetaverseAttribute target, string expression)
    {
        var mapping = new SyncRuleMapping { Id = id, TargetMetaverseAttribute = target, TargetMetaverseAttributeId = target.Id };
        mapping.Sources.Add(new SyncRuleMappingSource { Order = 0, Expression = expression });
        return mapping;
    }

    private static SyncRuleMapping ExportMapping(int id, string expression)
    {
        var target = UserType().Attributes[0];
        var mapping = new SyncRuleMapping { Id = id, TargetConnectedSystemAttribute = target, TargetConnectedSystemAttributeId = target.Id };
        mapping.Sources.Add(new SyncRuleMappingSource { Order = 0, Expression = expression });
        return mapping;
    }

    private static SyncRule ImportRule(params SyncRuleMapping[] mappings) => Rule(SyncRuleDirection.Import, mappings);

    private static SyncRule ExportRule(params SyncRuleMapping[] mappings) => Rule(SyncRuleDirection.Export, mappings);

    private static SyncRule Rule(SyncRuleDirection direction, SyncRuleMapping[] mappings) => new()
    {
        Name = "Users",
        Direction = direction,
        Enabled = false,
        ConnectedSystemId = ConnectedSystemId,
        ConnectedSystem = new ConnectedSystem { Id = ConnectedSystemId, Name = "HR", ObjectMatchingRuleMode = ObjectMatchingRuleMode.ConnectedSystem },
        MetaverseObjectTypeId = PersonTypeId,
        MetaverseObjectType = new MetaverseObjectType { Id = PersonTypeId, Name = "Person" },
        ConnectedSystemObjectTypeId = UserTypeId,
        ConnectedSystemObjectType = new ConnectedSystemObjectType { Id = UserTypeId, Name = "user", ConnectedSystemId = ConnectedSystemId },
        AttributeFlowRules = [.. mappings]
    };

    private static ConnectedSystemObjectType UserType() => new()
    {
        Id = UserTypeId,
        Name = "user",
        ConnectedSystemId = ConnectedSystemId,
        Selected = true,
        Attributes =
        [
            new ConnectedSystemObjectTypeAttribute { Id = 101, Name = "cn", Type = AttributeDataType.Text },
            new ConnectedSystemObjectTypeAttribute { Id = 102, Name = "sn", Type = AttributeDataType.Text }
        ]
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
