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
    private List<SyncRule> _otherImportRules = [];

    [SetUp]
    public void SetUp()
    {
        TestUtilities.SetEnvironmentVariables();
        _exportRules = [];
        _otherImportRules = [];

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
        // Saves read the configuration test cases covering what they save (the save gate, #2055 Phase 4b).
        _repo.Setup(r => r.ConfigurationTests).Returns(TestUtilities.CreateEmptyConfigurationTestRepository());
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
        _csRepo.Setup(r => r.GetImportSyncRulesForMetaverseObjectTypeAsync(It.IsAny<int>())).ReturnsAsync(() => [_hrImport, .. _otherImportRules]);
        _csRepo.Setup(r => r.GetExportSyncRulesWithAttributeFlowsAsync()).ReturnsAsync(() => _exportRules);
        _csRepo.Setup(r => r.GetConnectedSystemNamesAsync()).ReturnsAsync(() => new Dictionary<int, string> { [1] = "HR", [2] = "Corp AD", [3] = "Mail" });
        _csRepo.Setup(r => r.GetConnectedSystemsWithConnectorDefinitionsAsync()).ReturnsAsync(() =>
        [
            new ConnectedSystem { Id = 1, Name = "HR", ConnectorDefinition = new ConnectorDefinition { Name = "JIM File Connector" } },
            new ConnectedSystem { Id = 2, Name = "Corp AD", ConnectorDefinition = new ConnectorDefinition { Name = JIM.Connectors.ConnectorConstants.LdapConnectorName, SupportsUniquenessProbe = true } },
            new ConnectedSystem { Id = 3, Name = "Mail", ConnectorDefinition = new ConnectorDefinition { Name = "JIM SQL Connector" } }
        ]);
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
            Assert.That(described[upn.Id].CollisionRemediation, Is.True);
        }
    }

    [Test]
    public async Task GetBuiltFromGeneratedValuesAsync_GeneratedFlowsCollisionRemediationOff_SaysNothingIsCorrectedAsync()
    {
        // #2014: with the generated flow's own Collision Remediation off, nothing built from it is corrected whatever its
        // Keep unique switch says, so the surfaces must be able to show that switch as not in effect.
        _accountName.Generation!.CollisionRemediation = false;

        var described = await Application().ConnectedSystems.GetBuiltFromGeneratedValuesAsync(_hrImport, _hrImport.AttributeFlowRules);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(described[_email.Id].KeepUnique, Is.True);
            Assert.That(described[_email.Id].CollisionRemediation, Is.False);
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
    public async Task GetValuesBuiltFromGeneratedValuesAsync_ValuesOnTheGeneratedFlowsOwnRule_AreCheckedBeforeIssuingWhereTheRunProbesAsync()
    {
        // Generated Value Forms (#2023): Email and the User Principal Name built through it are on HR Import, so both are
        // checked with each candidate; Email goes to Corp AD's mail, which the LDAP Connector probes.
        AddUserPrincipalNameViaEmail();
        AddDirectoryExport();

        var values = (await Application().ConnectedSystems.GetValuesBuiltFromGeneratedValuesAsync(_hrImport, _hrImport.AttributeFlowRules))[_accountName.Id];

        using (Assert.EnterMultipleScope())
        {
            Assert.That(values.Select(v => v.CheckedBeforeIssuing), Is.EqualTo(new[] { true, true }));
            Assert.That(values[0].ProbedIn, Is.EqualTo(new[] { "Corp AD" }), "Email is probed where it is exported");
            Assert.That(values[1].ProbedIn, Is.Empty, "the User Principal Name is exported nowhere, so JIM's records alone");
            Assert.That(values.Select(v => v.Expression), Is.EqualTo(new[] { _email.Sources[0].Expression, "mv[\"Email\"]" }),
                "the expression that builds each, for the generated flow's preview to work them out from a candidate");
            Assert.That(values.Select(v => v.MoveToSyncRuleName), Is.All.Null);
        }
    }

    [Test]
    public async Task GetValuesBuiltFromGeneratedValuesAsync_KeepUniqueOff_IsNotCheckedButTheValueBuiltThroughItIsAsync()
    {
        AddUserPrincipalNameViaEmail();
        _email.KeepUnique = false;

        var values = (await Application().ConnectedSystems.GetValuesBuiltFromGeneratedValuesAsync(_hrImport, _hrImport.AttributeFlowRules))[_accountName.Id];

        Assert.That(values.Select(v => (v.MetaverseAttributeName, v.CheckedBeforeIssuing)),
            Is.EqualTo(new[] { ("Email", false), ("User Principal Name", true) }), "the switch is per value");
    }

    [Test]
    public async Task GetValuesBuiltFromGeneratedValuesAsync_ValueBuiltOnAnotherRuleFromMetaverseAttributes_IsNotCheckedAndSuggestsMovingItAsync()
    {
        var mailImport = MoveEmailToMailImport("mv[\"Account Name\"] + \"@panoply.local\"");

        var values = (await Application().ConnectedSystems.GetValuesBuiltFromGeneratedValuesAsync(_hrImport, _hrImport.AttributeFlowRules))[_accountName.Id];

        var email = values.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(email.SyncRuleName, Is.EqualTo(mailImport.Name));
            Assert.That(email.Kind, Is.EqualTo(GeneratedValueBuiltValueKind.BuiltOnAnotherRule));
            Assert.That(email.CheckedBeforeIssuing, Is.False);
            Assert.That(email.MoveToSyncRuleName, Is.EqualTo("HR Import"), "it reads only Metaverse attributes, so it can be moved to the generated value's own rule");
        }
    }

    [Test]
    public async Task GetValuesBuiltFromGeneratedValuesAsync_ValueBuiltOnAnotherRuleFromItsOwnSystemsAttributes_SuggestsNoMoveAsync()
    {
        MoveEmailToMailImport("mv[\"Account Name\"] + \"@\" + cs[\"domain\"]");

        var values = (await Application().ConnectedSystems.GetValuesBuiltFromGeneratedValuesAsync(_hrImport, _hrImport.AttributeFlowRules))[_accountName.Id];

        Assert.That(values.Single().MoveToSyncRuleName, Is.Null, "it reads the Mail system's own attributes, which HR Import cannot");
    }

    [Test]
    public async Task GetBuiltFromGeneratedValuesAsync_DerivedValueOnTheGeneratedFlowsOwnRule_IsCheckedBeforeIssuingAsync()
    {
        var described = await Application().ConnectedSystems.GetBuiltFromGeneratedValuesAsync(_hrImport, _hrImport.AttributeFlowRules);

        Assert.That(described[_email.Id].CheckedBeforeIssuing, Is.True);
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

    /// <summary>
    /// Takes Email off HR Import and builds it on a Mail Import rule instead, from <paramref name="expression"/>.
    /// </summary>
    private SyncRule MoveEmailToMailImport(string expression)
    {
        _hrImport.AttributeFlowRules.Remove(_email);
        var mailImport = new SyncRule
        {
            Id = 3, Name = "Mail Import", Direction = SyncRuleDirection.Import, Enabled = true, ConnectedSystemId = 3,
            MetaverseObjectTypeId = _hrImport.MetaverseObjectTypeId, MetaverseObjectType = _hrImport.MetaverseObjectType
        };
        var email = new SyncRuleMapping
        {
            Id = 300, SyncRule = mailImport, SyncRuleId = mailImport.Id,
            TargetMetaverseAttribute = _email.TargetMetaverseAttribute, TargetMetaverseAttributeId = _email.TargetMetaverseAttributeId
        };
        email.Sources.Add(new SyncRuleMappingSource { Order = 0, Expression = expression });
        mailImport.AttributeFlowRules.Add(email);
        _otherImportRules.Add(mailImport);
        return mailImport;
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
