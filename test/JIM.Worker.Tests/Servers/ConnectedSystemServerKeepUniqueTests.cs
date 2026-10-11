// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using JIM.TestSupport;
using Moq;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// The "Keep unique with Account Name" switch on a value built from a generated value (Generated Value Forms, #2023;
/// #2014): settable only where it can apply, an expression mapping, and changing it is the administrator's answer to any
/// decision the generated value is waiting on, so those are released for the next export to try again.
/// </summary>
[TestFixture]
public class ConnectedSystemServerKeepUniqueTests
{
    private const int GenerationId = 7;

    private Mock<IRepository> _repo = null!;
    private Mock<IConnectedSystemRepository> _csRepo = null!;
    private Mock<IActivityRepository> _activityRepo = null!;
    private JIM.InMemoryData.SyncRepository _syncRepo = null!;
    private MetaverseObject _initiator = null!;
    private SyncRule _hrImport = null!;
    private SyncRuleMapping _accountName = null!;
    private SyncRuleMapping _email = null!;
    private SyncRuleMapping _displayName = null!;
    private GeneratedValueAssignment _waiting = null!;
    private List<SyncRule> _exportRules = [];

    [SetUp]
    public void SetUp()
    {
        TestUtilities.SetEnvironmentVariables();

        var person = new MetaverseObjectType { Id = 1, Name = "Person", PluralName = "People" };
        var accountNameAttribute = new MetaverseAttribute { Id = 10, Name = "Account Name", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.SingleValued };
        var emailAttribute = new MetaverseAttribute { Id = 11, Name = "Email", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.SingleValued };
        var displayNameAttribute = new MetaverseAttribute { Id = 12, Name = "Display Name", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.SingleValued };
        person.Attributes = [accountNameAttribute, emailAttribute, displayNameAttribute];

        _hrImport = new SyncRule
        {
            Id = 1, Name = "HR Import", Direction = SyncRuleDirection.Import, ConnectedSystemId = 1,
            MetaverseObjectTypeId = person.Id, MetaverseObjectType = person
        };
        _accountName = Mapping(100, accountNameAttribute);
        _accountName.Generation = new SyncRuleMappingGeneration { Id = GenerationId, TokenKind = GeneratedValueTokenKind.OnlyIfTaken };
        _accountName.Sources.Add(new SyncRuleMappingSource { Order = 0, Expression = "Lower(cs[\"first\"])" });
        _email = Mapping(101, emailAttribute);
        _email.Sources.Add(new SyncRuleMappingSource { Order = 0, Expression = "mv[\"Account Name\"] + \"@panoply.local\"" });
        _displayName = Mapping(102, displayNameAttribute);
        _displayName.Sources.Add(new SyncRuleMappingSource
        {
            Order = 0,
            ConnectedSystemAttribute = new ConnectedSystemObjectTypeAttribute { Id = 900, Name = "displayName", Type = AttributeDataType.Text },
            ConnectedSystemAttributeId = 900
        });

        _repo = new Mock<IRepository>();
        _csRepo = new Mock<IConnectedSystemRepository>();
        _activityRepo = new Mock<IActivityRepository>();
        _repo.Setup(r => r.ConnectedSystems).Returns(_csRepo.Object);
        _repo.Setup(r => r.Activity).Returns(_activityRepo.Object);
        _repo.Setup(r => r.ServiceSettings).Returns(new InMemoryServiceSettingsRepository());
        // The derived-flow check on a save reads the Metaverse Object Type the rule targets (#1750).
        var metaverseRepo = new Mock<IMetaverseRepository>();
        metaverseRepo.Setup(r => r.GetMetaverseObjectTypeAsync(It.IsAny<int>(), It.IsAny<bool>())).ReturnsAsync(person);
        _repo.Setup(r => r.Metaverse).Returns(metaverseRepo.Object);
        _activityRepo.Setup(r => r.CreateActivityAsync(It.IsAny<Activity>())).Returns(Task.CompletedTask);
        _activityRepo.Setup(r => r.UpdateActivityAsync(It.IsAny<Activity>())).Returns(Task.CompletedTask);
        _activityRepo.Setup(r => r.GetMaxConfigurationChangeVersionAsync(It.IsAny<ActivityTargetType>(), It.IsAny<int>())).ReturnsAsync(0);

        _csRepo.Setup(r => r.GetSyncRuleMappingForUpdateAsync(It.IsAny<int>()))
            .ReturnsAsync((int id) => _hrImport.AttributeFlowRules.SingleOrDefault(m => m.Id == id));
        _csRepo.Setup(r => r.GetImportSyncRulesForMetaverseObjectTypeAsync(It.IsAny<int>())).ReturnsAsync(() => [_hrImport]);
        _csRepo.Setup(r => r.GetExportSyncRulesWithAttributeFlowsAsync()).ReturnsAsync(() => _exportRules);
        _csRepo.Setup(r => r.GetConnectedSystemNamesAsync()).ReturnsAsync(() => new Dictionary<int, string> { [1] = "HR", [2] = "Corp AD" });
        _csRepo.Setup(r => r.GetSyncRuleMappingsAsync(It.IsAny<int>())).ReturnsAsync(() => _hrImport.AttributeFlowRules);
        _csRepo.Setup(r => r.UpdateSyncRuleMappingAsync(It.IsAny<SyncRuleMapping>())).Returns(Task.CompletedTask);
        _csRepo.Setup(r => r.GetSyncRuleMappingGenerationsAsync(It.IsAny<IReadOnlyCollection<int>>()))
            .ReturnsAsync(() => new Dictionary<int, SyncRuleMappingGeneration>());

        _syncRepo = new JIM.InMemoryData.SyncRepository();
        _waiting = new GeneratedValueAssignment
        {
            Id = Guid.NewGuid(),
            MetaverseObjectId = Guid.NewGuid(),
            MetaverseAttributeId = accountNameAttribute.Id,
            Value = "grace.hopper",
            NormalisedValue = "grace.hopper",
            State = GeneratedValueAssignmentState.NeedsDecision,
            NeedsDecisionReason = GeneratedValueNeedsDecisionReason.AnchoredElsewhere,
            NeedsDecisionEnteredAt = DateTime.UtcNow,
            SyncRuleMappingGenerationId = GenerationId,
            Created = DateTime.UtcNow,
            LastUpdated = DateTime.UtcNow
        };
        _syncRepo.SeedGeneratedValueAssignment(_waiting);

        _initiator = TestUtilities.GetInitiatedBy();
    }

    [Test]
    public async Task UpdateSyncRuleMappingSettingsAsync_KeepUniqueOffOnADerivedValue_IsSavedAsync()
    {
        var updated = await Application().ConnectedSystems.UpdateSyncRuleMappingSettingsAsync(_email.Id, new SyncRuleMappingSettingsUpdate { KeepUnique = false }, _initiator);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(updated!.KeepUnique, Is.False);
            _csRepo.Verify(r => r.UpdateSyncRuleMappingAsync(It.Is<SyncRuleMapping>(m => m.Id == _email.Id && !m.KeepUnique)), Times.Once);
        }
    }

    [Test]
    public void UpdateSyncRuleMappingSettingsAsync_KeepUniqueOnADirectFlow_IsRefused()
    {
        var ex = Assert.ThrowsAsync<ArgumentException>(() =>
            Application().ConnectedSystems.UpdateSyncRuleMappingSettingsAsync(_displayName.Id, new SyncRuleMappingSettingsUpdate { KeepUnique = false }, _initiator));

        Assert.That(ex!.Message, Does.Contain("Keep unique"));
    }

    [Test]
    public void UpdateSyncRuleMappingSettingsAsync_KeepUniqueOnTheGeneratedValueItself_IsRefused()
    {
        var ex = Assert.ThrowsAsync<ArgumentException>(() =>
            Application().ConnectedSystems.UpdateSyncRuleMappingSettingsAsync(_accountName.Id, new SyncRuleMappingSettingsUpdate { KeepUnique = false }, _initiator));

        Assert.That(ex!.Message, Does.Contain("Keep unique"));
    }

    [Test]
    public async Task UpdateSyncRuleMappingSettingsAsync_KeepUniqueChanged_ReleasesTheGeneratedValuesHeldDecisionsAsync()
    {
        await Application().ConnectedSystems.UpdateSyncRuleMappingSettingsAsync(_email.Id, new SyncRuleMappingSettingsUpdate { KeepUnique = false }, _initiator);

        Assert.That(_syncRepo.GeneratedValueAssignments[_waiting.Id].State, Is.Not.EqualTo(GeneratedValueAssignmentState.NeedsDecision),
            "switching a value off is the administrator's answer, so the decision is released for the next export to try again");
    }

    [Test]
    public async Task UpdateSyncRuleMappingSettingsAsync_RulesLoadedWithoutTheirType_StillFindsTheGeneratedValueAsync()
    {
        // The rule queries do not load the Metaverse Object Type, whose attributes resolve mv["..."] names; the type is
        // read on its own.
        // User Principal Name is two steps away (via Email), so finding Account Name needs the derived flow graph.
        var upnAttribute = new MetaverseAttribute { Id = 13, Name = "User Principal Name", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.SingleValued };
        _hrImport.MetaverseObjectType.Attributes.Add(upnAttribute);
        var upn = Mapping(103, upnAttribute);
        upn.Sources.Add(new SyncRuleMappingSource { Order = 0, Expression = "mv[\"Email\"]" });
        _hrImport.MetaverseObjectType = null!;

        await Application().ConnectedSystems.UpdateSyncRuleMappingSettingsAsync(upn.Id, new SyncRuleMappingSettingsUpdate { KeepUnique = false }, _initiator);

        Assert.That(_syncRepo.GeneratedValueAssignments[_waiting.Id].State, Is.Not.EqualTo(GeneratedValueAssignmentState.NeedsDecision));
    }

    [Test]
    public async Task UpdateSyncRuleMappingSettingsAsync_KeepUniqueUnchanged_LeavesHeldDecisionsAloneAsync()
    {
        await Application().ConnectedSystems.UpdateSyncRuleMappingSettingsAsync(_email.Id, new SyncRuleMappingSettingsUpdate { KeepUnique = true }, _initiator);

        Assert.That(_syncRepo.GeneratedValueAssignments[_waiting.Id].State, Is.EqualTo(GeneratedValueAssignmentState.NeedsDecision));
    }

    [Test]
    public async Task GetBuiltFromGeneratedValuesAsync_DerivedValue_NamesTheGeneratedValueAndHowItIsBuiltAsync()
    {
        var upn = AddUserPrincipalNameViaEmail();

        var described = await Application().ConnectedSystems.GetBuiltFromGeneratedValuesAsync(_hrImport, _hrImport.AttributeFlowRules);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(described.Keys, Is.EquivalentTo(new[] { _email.Id, upn.Id }), "a direct flow and the generated value itself are built from nothing");
            Assert.That(described[upn.Id].GeneratedAttributeName, Is.EqualTo("Account Name"));
            Assert.That(described[upn.Id].Via, Is.EqualTo(new[] { "Email" }));
            Assert.That(described[upn.Id].Kind, Is.EqualTo(GeneratedValueBuiltValueKind.Form));
            Assert.That(described[upn.Id].KeepUnique, Is.True);
        }
    }

    [Test]
    public async Task GetValuesBuiltFromGeneratedValuesAsync_GeneratedValue_ListsEachValueAndWhereItIsExportedAsync()
    {
        AddUserPrincipalNameViaEmail();
        AddDirectoryExport();

        var described = await Application().ConnectedSystems.GetValuesBuiltFromGeneratedValuesAsync(_hrImport, _hrImport.AttributeFlowRules);

        var values = described[_accountName.Id];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(values.Select(v => v.MetaverseAttributeName), Is.EqualTo(new[] { "Email", "User Principal Name" }));
            Assert.That(values[0].ExportedTo.Single().ConnectedSystemName, Is.EqualTo("Corp AD"));
            Assert.That(values[0].ExportedTo.Single().AttributeName, Is.EqualTo("mail"));
        }
    }

    [Test]
    public async Task DescribeBuiltFromGeneratedValueAsync_FlowBeingEdited_IsDescribedAsync()
    {
        var proposal = new SyncRuleMapping { TargetMetaverseAttribute = _email.TargetMetaverseAttribute, TargetMetaverseAttributeId = _email.TargetMetaverseAttributeId };
        proposal.Sources.Add(new SyncRuleMappingSource { Order = 0, Expression = "Upper(mv[\"Account Name\"])" });

        var described = await Application().ConnectedSystems.DescribeBuiltFromGeneratedValueAsync(_hrImport, proposal);

        Assert.That(described?.GeneratedAttributeName, Is.EqualTo("Account Name"));
    }

    private SyncRuleMapping AddUserPrincipalNameViaEmail()
    {
        var upnAttribute = new MetaverseAttribute { Id = 13, Name = "User Principal Name", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.SingleValued };
        _hrImport.MetaverseObjectType.Attributes.Add(upnAttribute);
        var upn = Mapping(103, upnAttribute);
        upn.Sources.Add(new SyncRuleMappingSource { Order = 0, Expression = "mv[\"Email\"]" });
        return upn;
    }

    private void AddDirectoryExport()
    {
        var mail = new ConnectedSystemObjectTypeAttribute { Id = 950, Name = "mail", Type = AttributeDataType.Text };
        var export = new SyncRule
        {
            Id = 2, Name = "Corp AD Export", Direction = SyncRuleDirection.Export, Enabled = true, ConnectedSystemId = 2,
            MetaverseObjectTypeId = _hrImport.MetaverseObjectTypeId
        };
        var direct = new SyncRuleMapping
        {
            Id = 200, SyncRule = export, SyncRuleId = export.Id, Enabled = true,
            TargetConnectedSystemAttribute = mail, TargetConnectedSystemAttributeId = mail.Id
        };
        direct.Sources.Add(new SyncRuleMappingSource { Order = 0, MetaverseAttribute = _email.TargetMetaverseAttribute, MetaverseAttributeId = _email.TargetMetaverseAttributeId });
        export.AttributeFlowRules.Add(direct);
        _exportRules.Add(export);
    }

    private JimApplication Application() => new(_repo.Object, syncRepository: _syncRepo);

    private SyncRuleMapping Mapping(int id, MetaverseAttribute target)
    {
        var mapping = new SyncRuleMapping
        {
            Id = id, SyncRule = _hrImport, SyncRuleId = _hrImport.Id,
            TargetMetaverseAttribute = target, TargetMetaverseAttributeId = target.Id
        };
        _hrImport.AttributeFlowRules.Add(mapping);
        return mapping;
    }
}
