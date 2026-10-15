// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Logic.DTOs;
using JIM.Models.Search;
using JIM.Models.Staging;
using JIM.TestSupport;
using Moq;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// Saving a Synchronisation Rule refuses an expression scoping criterion that could never be evaluated (#2054), on the
/// one write path the portal, the REST API and PowerShell share.
/// </summary>
[TestFixture]
public class ConnectedSystemServerScopingExpressionSaveTests
{
    private Mock<IRepository> _repo = null!;
    private Mock<IConnectedSystemRepository> _csRepo = null!;
    private Mock<IMetaverseRepository> _mvRepo = null!;
    private Mock<IActivityRepository> _activityRepo = null!;
    private MetaverseObject _initiator = null!;
    private Dictionary<int, SyncRuleScopingCriterionState> _stored = null!;

    private readonly ConnectedSystemObjectType _person = new()
    {
        Id = 7, Name = "person",
        Attributes = [new ConnectedSystemObjectTypeAttribute { Id = 70, Name = "department", Type = AttributeDataType.Text }]
    };

    private readonly MetaverseObjectType _mvPerson = new()
    {
        Id = 1, Name = "Person",
        Attributes = [new MetaverseAttribute { Id = 10, Name = "Department", Type = AttributeDataType.Text }]
    };

    [SetUp]
    public void SetUp()
    {
        TestUtilities.SetEnvironmentVariables();

        _repo = new Mock<IRepository>();
        _csRepo = new Mock<IConnectedSystemRepository>();
        _mvRepo = new Mock<IMetaverseRepository>();
        _activityRepo = new Mock<IActivityRepository>();
        _repo.Setup(r => r.ConnectedSystems).Returns(_csRepo.Object);
        _repo.Setup(r => r.Metaverse).Returns(_mvRepo.Object);
        _repo.Setup(r => r.Activity).Returns(_activityRepo.Object);
        _repo.Setup(r => r.ServiceSettings).Returns(new InMemoryServiceSettingsRepository());

        _csRepo.Setup(r => r.GetImportSyncRulesForMetaverseObjectTypeAsync(It.IsAny<int>())).ReturnsAsync(() => []);
        _csRepo.Setup(r => r.GetSyncRuleMappingGenerationsAsync(It.IsAny<IReadOnlyCollection<int>>()))
            .ReturnsAsync(() => new Dictionary<int, SyncRuleMappingGeneration>());
        _csRepo.Setup(r => r.GetSyncRuleMappingsAsync(It.IsAny<int>())).ReturnsAsync(new List<SyncRuleMapping>());
        _csRepo.Setup(r => r.GetExportSyncRulesWithAttributeFlowsAsync()).ReturnsAsync(() => []);
        _csRepo.Setup(r => r.CreateSyncRuleAsync(It.IsAny<SyncRule>())).Returns(Task.CompletedTask);
        _csRepo.Setup(r => r.GetObjectTypeAsync(_person.Id)).ReturnsAsync(_person);
        // Only the overload with its attributes answers: the other returns the type without them.
        _mvRepo.Setup(r => r.GetMetaverseObjectTypeAsync(_mvPerson.Id, true)).ReturnsAsync(_mvPerson);
        _mvRepo.Setup(r => r.GetMetaverseObjectTypeAsync(_mvPerson.Id, false))
            .ReturnsAsync(new MetaverseObjectType { Id = _mvPerson.Id, Name = _mvPerson.Name });
        _activityRepo.Setup(r => r.CreateActivityAsync(It.IsAny<Activity>())).Returns(Task.CompletedTask);
        _activityRepo.Setup(r => r.UpdateActivityAsync(It.IsAny<Activity>())).Returns(Task.CompletedTask);
        _activityRepo.Setup(r => r.GetMaxConfigurationChangeVersionAsync(It.IsAny<ActivityTargetType>(), It.IsAny<int>())).ReturnsAsync(0);

        _stored = [];
        _csRepo.Setup(r => r.GetSyncRuleScopingCriterionStatesAsync(It.IsAny<IReadOnlyCollection<int>>())).ReturnsAsync(() => _stored);
        _csRepo.Setup(r => r.UpdateSyncRuleAsync(It.IsAny<SyncRule>())).Returns(Task.CompletedTask);
        _csRepo.Setup(r => r.GetImportMappingTargetMetaverseAttributesAsync(It.IsAny<int>())).ReturnsAsync(new Dictionary<int, int>());
        _csRepo.Setup(r => r.GetImportSyncRuleMappingsForMetaverseAttributeAsync(It.IsAny<int>(), It.IsAny<int>()))
            .ReturnsAsync(new List<SyncRuleMapping>());
        _csRepo.Setup(r => r.GetSyncRuleHeadersAsync(It.IsAny<int?>(), It.IsAny<SyncRuleDirection?>())).ReturnsAsync((IList<SyncRuleHeader>)new List<SyncRuleHeader>());
        _csRepo.Setup(r => r.GetSyncRuleMappingKeepUniqueAsync(It.IsAny<IReadOnlyCollection<int>>())).ReturnsAsync(new Dictionary<int, bool>());
        _repo.Setup(r => r.Sync).Returns(TestUtilities.StubQueuedChangeWithdrawal(new Mock<ISyncRepository>()).Object);

        _initiator = TestUtilities.GetInitiatedBy();
    }

    private JimApplication BuildApplication() => new(_repo.Object, syncRepository: new JIM.InMemoryData.SyncRepository());

    [Test]
    public async Task CreateOrUpdateSyncRuleAsync_WorkableImportExpression_IsSavedAsync()
    {
        var rule = ImportRule("Lower(cs[\"department\"])");

        await BuildApplication().ConnectedSystems.CreateOrUpdateSyncRuleAsync(rule, _initiator);

        _csRepo.Verify(r => r.CreateSyncRuleAsync(rule), Times.Once);
    }

    [Test]
    public void CreateOrUpdateSyncRuleAsync_ExpressionReadingAnUnknownAttribute_IsRefusedAndNotSaved()
    {
        var rule = ImportRule("Lower(cs[\"departmnet\"])");

        var ex = Assert.ThrowsAsync<ArgumentException>(() => BuildApplication().ConnectedSystems.CreateOrUpdateSyncRuleAsync(rule, _initiator));

        Assert.That(ex!.Message, Does.Contain("cs[\"departmnet\"]"));
        _csRepo.Verify(r => r.CreateSyncRuleAsync(It.IsAny<SyncRule>()), Times.Never);
    }

    [Test]
    public void CreateOrUpdateSyncRuleAsync_ExportExpressionReadingTheConnectedSystem_IsRefused()
    {
        var rule = ImportRule("Lower(cs[\"department\"])");
        rule.Direction = SyncRuleDirection.Export;

        var ex = Assert.ThrowsAsync<ArgumentException>(() => BuildApplication().ConnectedSystems.CreateOrUpdateSyncRuleAsync(rule, _initiator));

        Assert.That(ex!.Message, Does.Contain("export"));
    }

    [Test]
    public async Task CreateOrUpdateSyncRuleAsync_WorkableExportExpression_IsCheckedAgainstTheTypesAttributesAndSavedAsync()
    {
        var rule = ImportRule("Upper(mv[\"Department\"])");
        rule.Direction = SyncRuleDirection.Export;

        await BuildApplication().ConnectedSystems.CreateOrUpdateSyncRuleAsync(rule, _initiator);

        _csRepo.Verify(r => r.CreateSyncRuleAsync(rule), Times.Once);
    }

    [Test]
    public void CreateOrUpdateSyncRuleAsync_OperatorNotValidForTheCompareAsType_IsRefused()
    {
        // Starts With on a value compared as a Number: the shared criterion validation checks the operator against
        // the Compare As type.
        var rule = ImportRule("cs[\"department\"]", AttributeDataType.Number, SearchComparisonType.StartsWith);

        Assert.ThrowsAsync<ArgumentException>(() => BuildApplication().ConnectedSystems.CreateOrUpdateSyncRuleAsync(rule, _initiator));
    }

    // ── A criterion that stopped being valid after it was saved never locks the rule (#2054) ───────────────────────

    [Test]
    public async Task CreateOrUpdateSyncRuleAsync_DisablingARuleWithAnUnchangedRelativeDateOnText_SavesWithAWarningAsync()
    {
        // Stored when the attribute was a date; its type has since changed to Text under it.
        var rule = StoredRuleWith(new SyncRuleScopingCriteria
        {
            Id = 31, ConnectedSystemAttribute = _person.Attributes[0], ConnectedSystemAttributeId = 70, ComparisonType = SearchComparisonType.LessThan,
            ValueMode = DateCriteriaValueMode.Relative, RelativeCount = 30, RelativeUnit = RelativeDateUnit.Days, RelativeDirection = RelativeDateDirection.Ago
        });
        rule.Enabled = false;

        var saved = await BuildApplication().ConnectedSystems.CreateOrUpdateSyncRuleAsync(rule, _initiator);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(saved, Is.True);
            Assert.That(rule.ScopingSaveWarnings, Has.One.Contains("department"));
        }
    }

    [Test]
    public async Task CreateOrUpdateSyncRuleAsync_UnchangedExpressionReadingARenamedAttribute_SavesWithAWarningAsync()
    {
        var rule = StoredRuleWith(new SyncRuleScopingCriteria
        {
            Id = 31, Expression = "Lower(cs[\"dept\"])", CompareAs = AttributeDataType.Text, ComparisonType = SearchComparisonType.StartsWith, StringValue = "fin"
        });

        var saved = await BuildApplication().ConnectedSystems.CreateOrUpdateSyncRuleAsync(rule, _initiator);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(saved, Is.True);
            Assert.That(rule.ScopingSaveWarnings, Has.One.Contains("cs[\"dept\"]"));
        }
    }

    [Test]
    public void CreateOrUpdateSyncRuleAsync_ChangedCriterionStillInvalid_IsRefused()
    {
        // The save changed its value, so it is checked in full, as a new one is.
        var criterion = new SyncRuleScopingCriteria
        {
            Id = 31, ConnectedSystemAttribute = _person.Attributes[0], ConnectedSystemAttributeId = 70, ComparisonType = SearchComparisonType.LessThan,
            ValueMode = DateCriteriaValueMode.Relative, RelativeCount = 30, RelativeUnit = RelativeDateUnit.Days, RelativeDirection = RelativeDateDirection.Ago
        };
        var rule = StoredRuleWith(criterion);
        criterion.RelativeCount = 60;

        Assert.ThrowsAsync<ArgumentException>(() => BuildApplication().ConnectedSystems.CreateOrUpdateSyncRuleAsync(rule, _initiator));
    }

    /// <summary>A stored rule holding <paramref name="criterion"/> exactly as stored.</summary>
    private SyncRule StoredRuleWith(SyncRuleScopingCriteria criterion)
    {
        var rule = ImportRule("Lower(cs[\"department\"])");
        rule.Id = 5;
        rule.ObjectScopingCriteriaGroups[0].Criteria = [criterion];
        _stored[criterion.Id] = SyncRuleScopingCriterionState.Of(criterion);
        return rule;
    }

    private SyncRule ImportRule(string expression, AttributeDataType compareAs = AttributeDataType.Text,
        SearchComparisonType comparison = SearchComparisonType.Equals)
    {
        var rule = new SyncRule
        {
            Name = "HR Import",
            Direction = SyncRuleDirection.Import,
            Enabled = false,
            ConnectedSystem = new ConnectedSystem { Id = 1, Name = "HR" },
            ConnectedSystemId = 1,
            // The types without their attributes, as a caller may send them: the schema is loaded to check against.
            ConnectedSystemObjectType = new ConnectedSystemObjectType { Id = _person.Id, Name = _person.Name },
            ConnectedSystemObjectTypeId = _person.Id,
            MetaverseObjectType = new MetaverseObjectType { Id = _mvPerson.Id, Name = _mvPerson.Name },
            MetaverseObjectTypeId = _mvPerson.Id
        };
        rule.ObjectScopingCriteriaGroups.Add(new SyncRuleScopingCriteriaGroup
        {
            Type = SearchGroupType.All,
            Criteria = [new SyncRuleScopingCriteria
            {
                Expression = expression, CompareAs = compareAs, ComparisonType = comparison, StringValue = "finance", IntValue = 1
            }]
        });
        return rule;
    }
}
