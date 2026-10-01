// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Logic.DTOs;
using JIM.Models.Security;
using JIM.Models.Staging;
using JIM.Models.Staging.DTOs;
using JIM.TestSupport;
using JIM.Worker.Tests.Services;
using Moq;
using NUnit.Framework;
using static JIM.Worker.Tests.Services.DerivedFlowTestModel;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// Metaverse-Derived Attribute Flows' authoring surfaces in <see cref="JIM.Application.Servers.ConnectedSystemServer"/>
/// (#1750, plan Phase 6): the FR 3 dependants every removal and disable path reports (mapping deletion, full and
/// settings update, whole-rule save, Synchronisation Rule deletion, schema refresh), the read-only analysis the portal
/// runs as an administrator types, and the step facts the read surfaces show. With the flag off, every one of them is
/// inert and reads nothing.
/// </summary>
[TestFixture]
public class ConnectedSystemServerDerivedFlowDependentsTests
{
    private const string CycleMessage =
        "Saving would create a dependency cycle: Mail Nickname (Synchronisation Rule 'AD Import') reads Display Name, " +
        "which (Synchronisation Rule 'HR Import') reads Mail Nickname.";

    private Mock<IRepository> _repo = null!;
    private Mock<IConnectedSystemRepository> _csRepo = null!;
    private Mock<IMetaverseRepository> _mvRepo = null!;
    private Mock<IActivityRepository> _activityRepo = null!;
    private Mock<ITaskingRepository> _taskingRepo = null!;
    private MetaverseObject _initiator = null!;
    private ApiKey _apiKey = null!;
    private DerivedFlowTestModel _model = null!;

    // What the database holds: HR Import (Connected System 10) derives Display Name from Mail Nickname; AD Import
    // (Connected System 20) flows Mail Nickname and Region from the directory. Rebuilt per read, as an AsNoTracking
    // query materialises fresh instances, so nothing a test changes on a proposal leaks into "before".
    private Func<List<SyncRule>> _persistedRules = null!;

    [SetUp]
    public void SetUp()
    {
        TestUtilities.SetEnvironmentVariables();
        _model = new DerivedFlowTestModel();

        _repo = new Mock<IRepository>();
        _csRepo = new Mock<IConnectedSystemRepository>();
        _mvRepo = new Mock<IMetaverseRepository>();
        _activityRepo = new Mock<IActivityRepository>();
        _taskingRepo = new Mock<ITaskingRepository>();
        _repo.Setup(r => r.ConnectedSystems).Returns(_csRepo.Object);
        _repo.Setup(r => r.Metaverse).Returns(_mvRepo.Object);
        _repo.Setup(r => r.Activity).Returns(_activityRepo.Object);
        _repo.Setup(r => r.Tasking).Returns(_taskingRepo.Object);

        _activityRepo.Setup(r => r.CreateActivityAsync(It.IsAny<Activity>())).Returns(Task.CompletedTask);
        _activityRepo.Setup(r => r.UpdateActivityAsync(It.IsAny<Activity>())).Returns(Task.CompletedTask);
        _activityRepo.Setup(r => r.GetMaxConfigurationChangeVersionAsync(It.IsAny<ActivityTargetType>(), It.IsAny<int>())).ReturnsAsync(0);

        _csRepo.Setup(r => r.GetSyncRuleMappingsAsync(It.IsAny<int>())).ReturnsAsync(new List<SyncRuleMapping>());
        _csRepo.Setup(r => r.GetImportSyncRuleMappingsForMetaverseAttributeAsync(It.IsAny<int>(), It.IsAny<int>())).ReturnsAsync(new List<SyncRuleMapping>());
        _csRepo.Setup(r => r.CreateSyncRuleMappingAsync(It.IsAny<SyncRuleMapping>())).Returns(Task.CompletedTask);
        _csRepo.Setup(r => r.UpdateSyncRuleMappingAsync(It.IsAny<SyncRuleMapping>())).Returns(Task.CompletedTask);
        _csRepo.Setup(r => r.DeleteSyncRuleMappingAsync(It.IsAny<SyncRuleMapping>())).Returns(Task.CompletedTask);
        _csRepo.Setup(r => r.UpdateSyncRuleAsync(It.IsAny<SyncRule>())).Returns(Task.CompletedTask);
        _csRepo.Setup(r => r.DeleteSyncRuleAsync(It.IsAny<SyncRule>())).Returns(Task.CompletedTask);
        _csRepo.Setup(r => r.GetImportMappingTargetMetaverseAttributesAsync(It.IsAny<int>())).ReturnsAsync(new Dictionary<int, int>());
        _csRepo.Setup(r => r.GetImportSyncRulesForMetaverseObjectTypeAsync(PersonTypeId)).ReturnsAsync(() => _persistedRules());
        _csRepo.Setup(r => r.GetConnectedSystemNamesAsync()).ReturnsAsync(new Dictionary<int, string> { [10] = "HR", [20] = "AD" });
        _mvRepo.Setup(r => r.GetMetaverseObjectTypeAsync(PersonTypeId, true)).ReturnsAsync(() => _model.Person);

        _persistedRules = ComposePersistedRules;
        _initiator = TestUtilities.GetInitiatedBy();
        _apiKey = new ApiKey { Id = Guid.NewGuid(), Name = "Automation", KeyHash = "hash", KeyPrefix = "jim_", IsEnabled = true };
    }

    private List<SyncRule> ComposePersistedRules()
    {
        var hr = ImportRule(1, "HR Import", connectedSystemId: 10);
        Expression(hr, 101, _model.DisplayName, "mv[\"Mail Nickname\"]");
        var ad = ImportRule(2, "AD Import", connectedSystemId: 20);
        ad.ConnectedSystemObjectTypeId = 7;
        Expression(ad, 102, _model.MailNickname, "cs[\"mailNickname\"]");
        Expression(ad, 103, _model.Region, "cs[\"region\"]");
        return [hr, ad];
    }

    private JimApplication BuildApplication(bool flagEnabled = true)
    {
        _repo.Setup(r => r.ServiceSettings).Returns(flagEnabled
            ? InMemoryServiceSettingsRepository.WithAllFeatureFlagsEnabled()
            : new InMemoryServiceSettingsRepository());
        return new JimApplication(_repo.Object, syncRepository: new JIM.InMemoryData.SyncRepository());
    }

    /// <summary>
    /// The AD Import mapping as a caller holds it: a separate instance from what the rules read returns.
    /// </summary>
    private SyncRuleMapping AdMappingAsCallerHoldsIt(int id)
    {
        var rule = ImportRule(2, "AD Import", connectedSystemId: 20);
        var persisted = ComposePersistedRules()[1].AttributeFlowRules.Single(m => m.Id == id);
        return Expression(rule, id, persisted.TargetMetaverseAttribute!, persisted.Sources[0].Expression!);
    }

    private static void AssertMailNicknameDependant(IReadOnlyList<DependentDerivedFlow> dependants)
    {
        Assert.That(dependants, Has.Count.EqualTo(1));
        var dependant = dependants[0];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(dependant.MappingId, Is.EqualTo(101));
            Assert.That(dependant.TargetMetaverseAttributeName, Is.EqualTo("Display Name"));
            Assert.That(dependant.SyncRuleId, Is.EqualTo(1));
            Assert.That(dependant.SyncRuleName, Is.EqualTo("HR Import"));
            Assert.That(dependant.ConnectedSystemId, Is.EqualTo(10));
            Assert.That(dependant.ConnectedSystemName, Is.EqualTo("HR"));
            Assert.That(dependant.MissingInputs, Has.Count.EqualTo(1));
            Assert.That(dependant.MissingInputs[0].MetaverseAttributeName, Is.EqualTo("Mail Nickname"));
            Assert.That(dependant.MissingInputs[0].Indirect, Is.False);
            Assert.That(dependant.MissingInputs[0].Via, Is.Empty);
        }
    }

    // ---- DeleteSyncRuleMappingAsync ----

    [Test]
    public async Task DeleteSyncRuleMappingAsync_LastContributorOfADerivedInput_ReportsTheDependantAsync()
    {
        var jim = BuildApplication();

        var result = await jim.ConnectedSystems.DeleteSyncRuleMappingAsync(AdMappingAsCallerHoldsIt(102), _initiator);

        AssertMailNicknameDependant(result.DependentDerivedFlows);
        _csRepo.Verify(r => r.DeleteSyncRuleMappingAsync(It.IsAny<SyncRuleMapping>()), Times.Once, "a dependant warns, it never blocks");
    }

    [Test]
    public async Task DeleteSyncRuleMappingAsync_ApiKey_LastContributorOfADerivedInput_ReportsTheDependantAsync()
    {
        var jim = BuildApplication();

        var result = await jim.ConnectedSystems.DeleteSyncRuleMappingAsync(AdMappingAsCallerHoldsIt(102), _apiKey);

        AssertMailNicknameDependant(result.DependentDerivedFlows);
    }

    [Test]
    public async Task DeleteSyncRuleMappingAsync_AnotherContributorRemains_ReportsNothingAsync()
    {
        var jim = BuildApplication();
        _persistedRules = () =>
        {
            var rules = ComposePersistedRules();
            var training = ImportRule(3, "Training Import", connectedSystemId: 30);
            Expression(training, 301, _model.MailNickname, "cs[\"nick\"]");
            rules.Add(training);
            return rules;
        };

        var result = await jim.ConnectedSystems.DeleteSyncRuleMappingAsync(AdMappingAsCallerHoldsIt(102), _initiator);

        Assert.That(result.DependentDerivedFlows, Is.Empty, "only an attribute losing its last enabled contributor yields dependants");
        _csRepo.Verify(r => r.GetConnectedSystemNamesAsync(), Times.Never, "names are looked up only when there is a dependant to name");
    }

    [Test]
    public async Task DeleteSyncRuleMappingAsync_AttributeNoDerivedFlowReads_ReportsNothingAsync()
    {
        var jim = BuildApplication();

        var result = await jim.ConnectedSystems.DeleteSyncRuleMappingAsync(AdMappingAsCallerHoldsIt(103), _initiator);

        Assert.That(result.DependentDerivedFlows, Is.Empty);
    }

    [Test]
    public async Task DeleteSyncRuleMappingAsync_FlagOff_ReportsNothingAndReadsNoRulesAsync()
    {
        var jim = BuildApplication(flagEnabled: false);

        var result = await jim.ConnectedSystems.DeleteSyncRuleMappingAsync(AdMappingAsCallerHoldsIt(102), _initiator);

        Assert.That(result.DependentDerivedFlows, Is.Empty);
        _csRepo.Verify(r => r.GetImportSyncRulesForMetaverseObjectTypeAsync(It.IsAny<int>()), Times.Never);
        _mvRepo.Verify(r => r.GetMetaverseObjectTypeAsync(It.IsAny<int>(), It.IsAny<bool>()), Times.Never);
    }

    [Test]
    public async Task DeleteSyncRuleMappingAsync_ExportMapping_ReadsNoRulesAsync()
    {
        var jim = BuildApplication();
        var export = ExportRule(5, "AD Export", connectedSystemId: 20);
        var mapping = new SyncRuleMapping
        {
            Id = 501,
            SyncRule = export,
            SyncRuleId = export.Id,
            TargetConnectedSystemAttribute = new ConnectedSystemObjectTypeAttribute { Id = 900, Name = "mail", Type = AttributeDataType.Text },
            TargetConnectedSystemAttributeId = 900
        };

        var result = await jim.ConnectedSystems.DeleteSyncRuleMappingAsync(mapping, _initiator);

        Assert.That(result.DependentDerivedFlows, Is.Empty);
        _csRepo.Verify(r => r.GetImportSyncRulesForMetaverseObjectTypeAsync(It.IsAny<int>()), Times.Never,
            "an export mapping contributes nothing to the Metaverse, so it cannot starve a derived flow");
    }

    // ---- UpdateSyncRuleMappingSettingsAsync ----

    private void TrackForUpdate(int id) =>
        _csRepo.Setup(r => r.GetSyncRuleMappingForUpdateAsync(id)).ReturnsAsync(AdMappingAsCallerHoldsIt(id));

    [Test]
    public async Task UpdateSyncRuleMappingSettingsAsync_DisablingTheLastContributor_ReportsTheDependantOnTheMappingAsync()
    {
        var jim = BuildApplication();
        TrackForUpdate(102);

        var updated = await jim.ConnectedSystems.UpdateSyncRuleMappingSettingsAsync(102, new SyncRuleMappingSettingsUpdate { Enabled = false }, _initiator);

        AssertMailNicknameDependant(updated!.SaveDependentDerivedFlows);
        _csRepo.Verify(r => r.UpdateSyncRuleMappingAsync(It.IsAny<SyncRuleMapping>()), Times.Once, "a dependant warns, it never blocks");
    }

    [Test]
    public async Task UpdateSyncRuleMappingSettingsAsync_ApiKey_DisablingTheLastContributor_ReportsTheDependantAsync()
    {
        var jim = BuildApplication();
        TrackForUpdate(102);

        var updated = await jim.ConnectedSystems.UpdateSyncRuleMappingSettingsAsync(102, new SyncRuleMappingSettingsUpdate { Enabled = false }, _apiKey);

        AssertMailNicknameDependant(updated!.SaveDependentDerivedFlows);
    }

    [Test]
    public async Task UpdateSyncRuleMappingSettingsAsync_SettingThatCannotLoseAContributor_ReadsNoRulesAsync()
    {
        var jim = BuildApplication();
        TrackForUpdate(102);

        var updated = await jim.ConnectedSystems.UpdateSyncRuleMappingSettingsAsync(102, new SyncRuleMappingSettingsUpdate { NullIsValue = true }, _initiator);

        Assert.That(updated!.SaveDependentDerivedFlows, Is.Empty);
        _csRepo.Verify(r => r.GetImportSyncRulesForMetaverseObjectTypeAsync(It.IsAny<int>()), Times.Never);
    }

    [Test]
    public async Task UpdateSyncRuleMappingSettingsAsync_FlagOff_DisablingTheLastContributor_ReportsNothingAsync()
    {
        var jim = BuildApplication(flagEnabled: false);
        TrackForUpdate(102);

        var updated = await jim.ConnectedSystems.UpdateSyncRuleMappingSettingsAsync(102, new SyncRuleMappingSettingsUpdate { Enabled = false }, _initiator);

        Assert.That(updated!.SaveDependentDerivedFlows, Is.Empty);
        _csRepo.Verify(r => r.GetImportSyncRulesForMetaverseObjectTypeAsync(It.IsAny<int>()), Times.Never);
    }

    // ---- UpdateSyncRuleMappingAsync ----

    [Test]
    public async Task UpdateSyncRuleMappingAsync_RetargetingTheLastContributor_ReportsTheDependantAsync()
    {
        var jim = BuildApplication();
        var mapping = AdMappingAsCallerHoldsIt(102);
        mapping.TargetMetaverseAttribute = _model.Email;
        mapping.TargetMetaverseAttributeId = _model.Email.Id;

        await jim.ConnectedSystems.UpdateSyncRuleMappingAsync(mapping, _initiator);

        AssertMailNicknameDependant(mapping.SaveDependentDerivedFlows);
    }

    // ---- CreateOrUpdateSyncRuleAsync ----

    private SyncRule AdRuleProposal(bool enabled)
    {
        var rule = ComposePersistedRules()[1];
        rule.Enabled = enabled;
        rule.ConnectedSystem = new ConnectedSystem { Id = 20, Name = "AD" };
        rule.ConnectedSystemObjectType = new ConnectedSystemObjectType { Id = 7, Name = "user" };
        rule.MetaverseObjectType = _model.Person;
        return rule;
    }

    [Test]
    public async Task CreateOrUpdateSyncRuleAsync_DisablingTheRuleHoldingTheLastContributor_ReportsTheDependantAsync()
    {
        var jim = BuildApplication();
        var rule = AdRuleProposal(enabled: false);

        var saved = await jim.ConnectedSystems.CreateOrUpdateSyncRuleAsync(rule, _initiator);

        Assert.That(saved, Is.True);
        AssertMailNicknameDependant(rule.SaveDependentDerivedFlows);
    }

    [Test]
    public async Task CreateOrUpdateSyncRuleAsync_ApiKey_RemovingTheLastContributor_ReportsTheDependantAsync()
    {
        var jim = BuildApplication();
        var rule = AdRuleProposal(enabled: false);
        rule.AttributeFlowRules.RemoveAll(m => m.Id == 102);

        await jim.ConnectedSystems.CreateOrUpdateSyncRuleAsync(rule, _apiKey);

        AssertMailNicknameDependant(rule.SaveDependentDerivedFlows);
    }

    [Test]
    public async Task CreateOrUpdateSyncRuleAsync_ExportRule_ReadsNoRulesAsync()
    {
        var jim = BuildApplication();
        var rule = ExportRule(5, "AD Export", connectedSystemId: 20);
        rule.Enabled = false;
        rule.ConnectedSystem = new ConnectedSystem { Id = 20, Name = "AD" };
        rule.ConnectedSystemObjectType = new ConnectedSystemObjectType { Id = 7, Name = "user" };
        rule.ConnectedSystemObjectTypeId = 7;
        rule.MetaverseObjectType = _model.Person;

        await jim.ConnectedSystems.CreateOrUpdateSyncRuleAsync(rule, _initiator);

        Assert.That(rule.SaveDependentDerivedFlows, Is.Empty);
        _csRepo.Verify(r => r.GetImportSyncRulesForMetaverseObjectTypeAsync(It.IsAny<int>()), Times.Never);
    }

    [Test]
    public async Task CreateOrUpdateSyncRuleAsync_FlagOff_DisablingTheRule_ReportsNothingAsync()
    {
        var jim = BuildApplication(flagEnabled: false);
        var rule = AdRuleProposal(enabled: false);

        await jim.ConnectedSystems.CreateOrUpdateSyncRuleAsync(rule, _initiator);

        Assert.That(rule.SaveDependentDerivedFlows, Is.Empty);
        _csRepo.Verify(r => r.GetImportSyncRulesForMetaverseObjectTypeAsync(It.IsAny<int>()), Times.Never);
    }

    // ---- DeleteSyncRuleAsync ----

    [Test]
    public async Task DeleteSyncRuleAsync_RuleHoldingTheLastContributor_ReportsTheDependantAsync()
    {
        var jim = BuildApplication();
        var rule = AdRuleProposal(enabled: true);

        var result = await jim.ConnectedSystems.DeleteSyncRuleAsync(rule, _initiator, recallContributedValues: false);

        AssertMailNicknameDependant(result.DependentDerivedFlows);
        _csRepo.Verify(r => r.DeleteSyncRuleAsync(rule), Times.Once);
    }

    [Test]
    public async Task DeleteSyncRuleAsync_FlagOff_ReportsNothingAsync()
    {
        var jim = BuildApplication(flagEnabled: false);
        var rule = AdRuleProposal(enabled: true);

        var result = await jim.ConnectedSystems.DeleteSyncRuleAsync(rule, _apiKey, recallContributedValues: false);

        Assert.That(result.DependentDerivedFlows, Is.Empty);
        _csRepo.Verify(r => r.GetImportSyncRulesForMetaverseObjectTypeAsync(It.IsAny<int>()), Times.Never);
    }

    // ---- DetectSchemaRefreshDependentsAsync ----

    private SchemaRefreshResult MailNicknameRemovedFromTheDirectory()
    {
        _csRepo.Setup(r => r.GetSyncRulesAsync(20, true)).ReturnsAsync(() => [ComposePersistedRules()[1]]);
        return new SchemaRefreshResult
        {
            Success = true,
            RemovedAttributes = new Dictionary<string, List<string>> { ["user"] = ["mailNickname"] },
            PreRefreshSchema =
            [
                new SchemaRefreshPreRefreshType
                {
                    Id = 7,
                    Name = "user",
                    Attributes = [new SchemaRefreshPreRefreshAttribute { Id = 70, Name = "mailNickname" }, new SchemaRefreshPreRefreshAttribute { Id = 71, Name = "region" }]
                }
            ]
        };
    }

    [Test]
    public async Task DetectSchemaRefreshDependentsAsync_InvalidatedMappingWasTheLastContributor_AddsTheDerivedDependantAsync()
    {
        var jim = BuildApplication();

        var dependents = await jim.ConnectedSystems.DetectSchemaRefreshDependentsAsync(20, MailNicknameRemovedFromTheDirectory());

        Assert.That(dependents.InvalidatedMappings.Select(m => m.MappingId), Is.EqualTo(new[] { 102 }), "the existing report is unchanged");
        AssertMailNicknameDependant(dependents.DependentDerivedFlows);
    }

    [Test]
    public async Task DetectSchemaRefreshDependentsAsync_FlagOff_AddsNothingAndReadsNoImportRulesAsync()
    {
        var jim = BuildApplication(flagEnabled: false);

        var dependents = await jim.ConnectedSystems.DetectSchemaRefreshDependentsAsync(20, MailNicknameRemovedFromTheDirectory());

        Assert.That(dependents.InvalidatedMappings, Has.Count.EqualTo(1));
        Assert.That(dependents.DependentDerivedFlows, Is.Empty);
        _csRepo.Verify(r => r.GetImportSyncRulesForMetaverseObjectTypeAsync(It.IsAny<int>()), Times.Never);
    }

    // ---- AnalyseDerivedFlowAsync ----

    private static SyncRule AdHostRule() => ImportRule(2, "AD Import", connectedSystemId: 20);

    private SyncRuleMapping Proposal(int id, MetaverseAttribute target, string expression)
    {
        var mapping = new SyncRuleMapping
        {
            Id = id,
            SyncRuleId = 2,
            TargetMetaverseAttribute = target,
            TargetMetaverseAttributeId = target.Id
        };
        mapping.Sources.Add(new SyncRuleMappingSource { Order = 0, Expression = expression });
        return mapping;
    }

    private void VerifyNothingWritten()
    {
        _csRepo.Verify(r => r.CreateSyncRuleMappingAsync(It.IsAny<SyncRuleMapping>()), Times.Never);
        _csRepo.Verify(r => r.UpdateSyncRuleMappingAsync(It.IsAny<SyncRuleMapping>()), Times.Never);
        _csRepo.Verify(r => r.UpdateSyncRuleAsync(It.IsAny<SyncRule>()), Times.Never);
        _activityRepo.Verify(r => r.CreateActivityAsync(It.IsAny<Activity>()), Times.Never);
    }

    [Test]
    public async Task AnalyseDerivedFlowAsync_UnsavedDerivedFlow_ReportsItsInputsStepAndChainAsync()
    {
        var jim = BuildApplication();

        var analysis = await jim.ConnectedSystems.AnalyseDerivedFlowAsync(AdHostRule(), Proposal(0, _model.Email, "mv[\"display name\"] + \"@corp.local\""));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(analysis.Status, Is.EqualTo(DerivedFlowAnalysisStatus.Derived));
            Assert.That(analysis.MetaverseInputs, Is.EqualTo(new[] { "display name" }), "as written");
            Assert.That(analysis.Step, Is.EqualTo(3), "Mail Nickname at step 1, Display Name at step 2, so Email at step 3");
            Assert.That(analysis.StepCount, Is.EqualTo(3));
            Assert.That(analysis.Steps.Select(s => (s.Step, string.Join(",", s.AttributeNames))),
                Is.EqualTo(new[] { (1, "Mail Nickname"), (2, "Display Name"), (3, "Email") }));
            Assert.That(analysis.Errors, Is.Empty);
            Assert.That(analysis.BlockingError, Is.Null);
            Assert.That(analysis.Warnings, Is.Empty);
        }
        VerifyNothingWritten();
    }

    [Test]
    public async Task AnalyseDerivedFlowAsync_ProposalClosingACycle_ReportsTheSavesMessageVerbatimAsync()
    {
        var jim = BuildApplication();

        var analysis = await jim.ConnectedSystems.AnalyseDerivedFlowAsync(AdHostRule(), Proposal(102, _model.MailNickname, "mv[\"Display Name\"]"));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(analysis.Status, Is.EqualTo(DerivedFlowAnalysisStatus.Derived));
            Assert.That(analysis.Errors, Is.EqualTo(new[] { CycleMessage }));
            Assert.That(analysis.BlockingError, Is.EqualTo(CycleMessage));
            Assert.That(analysis.Step, Is.Null, "a flow on a cycle cannot be ordered");
        }
        VerifyNothingWritten();
    }

    [Test]
    public async Task AnalyseDerivedFlowAsync_UnknownMetaverseName_ReportsTheSavesMessageAsync()
    {
        var jim = BuildApplication();

        var analysis = await jim.ConnectedSystems.AnalyseDerivedFlowAsync(AdHostRule(), Proposal(0, _model.Email, "mv[\"Acount Name\"]"));

        Assert.That(analysis.BlockingError, Is.EqualTo(
            "The Attribute Flow to Email (Synchronisation Rule 'AD Import') reads mv[\"Acount Name\"], but 'Acount Name' is not " +
            "an attribute of the Metaverse Object Type 'Person'."));
        Assert.That(analysis.MetaverseInputs, Is.EqualTo(new[] { "Acount Name" }));
    }

    [Test]
    public async Task AnalyseDerivedFlowAsync_NonRepeatableFunction_ReportsTheWarningAsync()
    {
        var jim = BuildApplication();

        var analysis = await jim.ConnectedSystems.AnalyseDerivedFlowAsync(AdHostRule(), Proposal(0, _model.Email, "mv[\"Region\"] + FormatDate(Now(), \"yyyy\")"));

        Assert.That(analysis.Errors, Is.Empty);
        Assert.That(analysis.Warnings, Has.Count.EqualTo(1));
        Assert.That(analysis.Warnings[0], Does.Contain("calls Now()"));
    }

    [Test]
    public async Task AnalyseDerivedFlowAsync_OrdinaryFlowADerivedFlowReads_IsStepOneAsync()
    {
        var jim = BuildApplication();

        var analysis = await jim.ConnectedSystems.AnalyseDerivedFlowAsync(AdHostRule(), Proposal(102, _model.MailNickname, "cs[\"nick\"]"));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(analysis.Status, Is.EqualTo(DerivedFlowAnalysisStatus.NotDerived));
            Assert.That(analysis.Step, Is.EqualTo(1));
            Assert.That(analysis.StepCount, Is.EqualTo(2));
            Assert.That(analysis.MetaverseInputs, Is.Empty);
            Assert.That(analysis.Steps, Is.Empty);
        }
    }

    [Test]
    public async Task AnalyseDerivedFlowAsync_OrdinaryFlowNothingReads_HasNoStepAsync()
    {
        var jim = BuildApplication();

        var analysis = await jim.ConnectedSystems.AnalyseDerivedFlowAsync(AdHostRule(), Proposal(103, _model.Region, "cs[\"region\"]"));

        Assert.That(analysis.Status, Is.EqualTo(DerivedFlowAnalysisStatus.NotDerived));
        Assert.That(analysis.Step, Is.Null);
        Assert.That(analysis.StepCount, Is.EqualTo(2));
    }

    [Test]
    public async Task AnalyseDerivedFlowAsync_FlagOff_IsNotApplicableAndReadsNothingAsync()
    {
        var jim = BuildApplication(flagEnabled: false);

        var analysis = await jim.ConnectedSystems.AnalyseDerivedFlowAsync(AdHostRule(), Proposal(0, _model.Email, "mv[\"Display Name\"]"));

        Assert.That(analysis.Status, Is.EqualTo(DerivedFlowAnalysisStatus.NotApplicable));
        Assert.That(analysis.StepCount, Is.Zero);
        _csRepo.Verify(r => r.GetImportSyncRulesForMetaverseObjectTypeAsync(It.IsAny<int>()), Times.Never);
    }

    [Test]
    public async Task AnalyseDerivedFlowAsync_ExportRule_IsNotApplicableAsync()
    {
        var jim = BuildApplication();
        var mapping = new SyncRuleMapping
        {
            TargetConnectedSystemAttribute = new ConnectedSystemObjectTypeAttribute { Id = 900, Name = "mail", Type = AttributeDataType.Text },
            TargetConnectedSystemAttributeId = 900
        };
        mapping.Sources.Add(new SyncRuleMappingSource { Expression = "mv[\"Email\"]" });

        var analysis = await jim.ConnectedSystems.AnalyseDerivedFlowAsync(ExportRule(5, "AD Export", connectedSystemId: 20), mapping);

        Assert.That(analysis.Status, Is.EqualTo(DerivedFlowAnalysisStatus.NotApplicable));
        _csRepo.Verify(r => r.GetImportSyncRulesForMetaverseObjectTypeAsync(It.IsAny<int>()), Times.Never);
    }

    [Test]
    public async Task AnalyseDerivedFlowAsync_NoTargetYet_IsNotApplicableAsync()
    {
        var jim = BuildApplication();
        var mapping = new SyncRuleMapping();
        mapping.Sources.Add(new SyncRuleMappingSource { Expression = "mv[\"Email\"]" });

        var analysis = await jim.ConnectedSystems.AnalyseDerivedFlowAsync(AdHostRule(), mapping);

        Assert.That(analysis.Status, Is.EqualTo(DerivedFlowAnalysisStatus.NotApplicable));
    }

    // ---- GetDerivedFlowStepsAsync ----

    [Test]
    public async Task GetDerivedFlowStepsAsync_RuleHostingADerivedFlow_ReportsItsStepAsync()
    {
        var jim = BuildApplication();

        var steps = await jim.ConnectedSystems.GetDerivedFlowStepsAsync(ComposePersistedRules()[0]);

        Assert.That(steps.Keys, Is.EqualTo(new[] { 101 }));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(steps[101].Step, Is.EqualTo(2));
            Assert.That(steps[101].StepCount, Is.EqualTo(2));
            Assert.That(steps[101].MetaverseInputs, Is.EqualTo(new[] { "Mail Nickname" }));
        }
    }

    [Test]
    public async Task GetDerivedFlowStepsAsync_RuleWithNoDerivedFlow_ReadsNothingAsync()
    {
        var jim = BuildApplication();

        var steps = await jim.ConnectedSystems.GetDerivedFlowStepsAsync(ComposePersistedRules()[1]);

        Assert.That(steps, Is.Empty);
        _csRepo.Verify(r => r.GetImportSyncRulesForMetaverseObjectTypeAsync(It.IsAny<int>()), Times.Never);
    }

    [Test]
    public async Task GetDerivedFlowStepsAsync_FlagOff_ReportsNothingAndReadsNothingAsync()
    {
        var jim = BuildApplication(flagEnabled: false);

        var steps = await jim.ConnectedSystems.GetDerivedFlowStepsAsync(ComposePersistedRules()[0]);

        Assert.That(steps, Is.Empty);
        _csRepo.Verify(r => r.GetImportSyncRulesForMetaverseObjectTypeAsync(It.IsAny<int>()), Times.Never);
    }
}
