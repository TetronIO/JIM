// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Application.Exceptions;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Security;
using JIM.Models.Staging;
using JIM.TestSupport;
using JIM.Worker.Tests.Services;
using Moq;
using NUnit.Framework;
using static JIM.Worker.Tests.Services.DerivedFlowTestModel;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// Metaverse-Derived Attribute Flow save-time validation, wired into every
/// <see cref="JIM.Application.Servers.ConnectedSystemServer"/> path that can change an import mapping's expression or
/// target (#1750, plan Phase 1 point 3): single mapping create (both initiator overloads), full update, settings
/// update (both overloads), and the whole-rule save (both overloads). Validation loads every import rule of the
/// Metaverse Object Type with the proposal substituted and disabled mappings included.
/// </summary>
[TestFixture]
public class ConnectedSystemServerDerivedFlowTests
{
    private const string CycleMessage =
        "Saving would create a dependency cycle: Mail Nickname (Synchronisation Rule 'AD Import') reads Display Name, " +
        "which (Synchronisation Rule 'HR Import') reads Mail Nickname.";

    private Mock<IRepository> _repo = null!;
    private Mock<IConnectedSystemRepository> _csRepo = null!;
    private Mock<IMetaverseRepository> _mvRepo = null!;
    private Mock<IActivityRepository> _activityRepo = null!;
    private MetaverseObject _initiator = null!;
    private ApiKey _apiKey = null!;
    private DerivedFlowTestModel _model = null!;

    // What the database holds: HR Import derives Display Name from Mail Nickname; AD Import flows Mail Nickname from
    // the directory. Rebuilt per test, and never the instances a proposal is made of, as a real AsNoTracking read is.
    private List<SyncRule> _persistedRules = null!;

    [SetUp]
    public void SetUp()
    {
        TestUtilities.SetEnvironmentVariables();
        _model = new DerivedFlowTestModel();

        _repo = new Mock<IRepository>();
        _csRepo = new Mock<IConnectedSystemRepository>();
        _mvRepo = new Mock<IMetaverseRepository>();
        _activityRepo = new Mock<IActivityRepository>();
        _repo.Setup(r => r.ConnectedSystems).Returns(_csRepo.Object);
        _repo.Setup(r => r.Metaverse).Returns(_mvRepo.Object);
        _repo.Setup(r => r.Activity).Returns(_activityRepo.Object);

        _activityRepo.Setup(r => r.CreateActivityAsync(It.IsAny<Activity>())).Returns(Task.CompletedTask);
        _activityRepo.Setup(r => r.UpdateActivityAsync(It.IsAny<Activity>())).Returns(Task.CompletedTask);
        _activityRepo.Setup(r => r.GetMaxConfigurationChangeVersionAsync(It.IsAny<ActivityTargetType>(), It.IsAny<int>())).ReturnsAsync(0);

        _csRepo.Setup(r => r.GetSyncRuleMappingsAsync(It.IsAny<int>())).ReturnsAsync(new List<SyncRuleMapping>());
        _csRepo.Setup(r => r.GetImportSyncRuleMappingsForMetaverseAttributeAsync(It.IsAny<int>(), It.IsAny<int>())).ReturnsAsync(new List<SyncRuleMapping>());
        _csRepo.Setup(r => r.CreateSyncRuleMappingAsync(It.IsAny<SyncRuleMapping>())).Returns(Task.CompletedTask);
        _csRepo.Setup(r => r.UpdateSyncRuleMappingAsync(It.IsAny<SyncRuleMapping>())).Returns(Task.CompletedTask);
        _csRepo.Setup(r => r.CreateSyncRuleAsync(It.IsAny<SyncRule>())).Returns(Task.CompletedTask);
        _csRepo.Setup(r => r.UpdateSyncRuleAsync(It.IsAny<SyncRule>())).Returns(Task.CompletedTask);
        _csRepo.Setup(r => r.GetImportMappingTargetMetaverseAttributesAsync(It.IsAny<int>())).ReturnsAsync(new Dictionary<int, int>());
        _csRepo.Setup(r => r.GetImportSyncRulesForMetaverseObjectTypeAsync(PersonTypeId)).ReturnsAsync(() => _persistedRules);
        _mvRepo.Setup(r => r.GetMetaverseObjectTypeAsync(PersonTypeId, true)).ReturnsAsync(() => _model.Person);

        _persistedRules = ComposePersistedRules();
        _initiator = TestUtilities.GetInitiatedBy();
        _apiKey = new ApiKey { Id = Guid.NewGuid(), Name = "Automation", KeyHash = "hash", KeyPrefix = "jim_", IsEnabled = true };
    }

    private List<SyncRule> ComposePersistedRules()
    {
        var hr = ImportRule(1, "HR Import", connectedSystemId: 10);
        Expression(hr, 101, _model.DisplayName, "mv[\"Mail Nickname\"]");
        var ad = ImportRule(2, "AD Import", connectedSystemId: 20);
        Expression(ad, 102, _model.MailNickname, "cs[\"mailNickname\"]");
        Expression(ad, 103, _model.Region, "cs[\"region\"]");
        return [hr, ad];
    }

    private JimApplication BuildApplication()
    {
        // An empty settings store: no feature flag is set. Validation used to run only with the
        // Features.MetaverseDerivedAttributeFlows flag on; the flag was removed when the feature shipped (#1878), so
        // every test here also proves the validation needs no flag.
        _repo.Setup(r => r.ServiceSettings).Returns(new InMemoryServiceSettingsRepository());
        return new JimApplication(_repo.Object, syncRepository: new JIM.InMemoryData.SyncRepository());
    }

    /// <summary>
    /// The AD Import rule as a caller holds it (a separate instance from what the database read returns).
    /// </summary>
    private static SyncRule AdRuleAsCallerHoldsIt() => ImportRule(2, "AD Import", connectedSystemId: 20);

    private SyncRuleMapping NewAdMapping(int id, MetaverseAttribute target, string expression) =>
        Expression(AdRuleAsCallerHoldsIt(), id, target, expression);

    // ---- CreateSyncRuleMappingAsync ----

    [Test]
    public void CreateSyncRuleMappingAsync_MappingClosingACycleWithAnotherRule_IsRejectedNamingBothRules()
    {
        var jim = BuildApplication();
        // AD Import does not map Mail Nickname yet in this test, so the new mapping is a plain create.
        _persistedRules[1].AttributeFlowRules.RemoveAll(m => m.Id == 102);
        var mapping = NewAdMapping(0, _model.MailNickname, "mv[\"Display Name\"]");

        var ex = Assert.ThrowsAsync<DerivedFlowValidationException>(async () =>
            await jim.ConnectedSystems.CreateSyncRuleMappingAsync(mapping, _initiator));

        Assert.That(ex!.Message, Is.EqualTo(CycleMessage));
        _csRepo.Verify(r => r.CreateSyncRuleMappingAsync(It.IsAny<SyncRuleMapping>()), Times.Never, "refused before anything is written");
        _activityRepo.Verify(r => r.CreateActivityAsync(It.IsAny<Activity>()), Times.Never, "a refused save leaves no Activity behind");
    }

    [Test]
    public void CreateSyncRuleMappingAsync_ApiKey_MappingClosingACycle_IsRejected()
    {
        var jim = BuildApplication();
        _persistedRules[1].AttributeFlowRules.RemoveAll(m => m.Id == 102);
        var mapping = NewAdMapping(0, _model.MailNickname, "mv[\"Display Name\"]");

        var ex = Assert.ThrowsAsync<DerivedFlowValidationException>(async () =>
            await jim.ConnectedSystems.CreateSyncRuleMappingAsync(mapping, _apiKey));

        Assert.That(ex!.Message, Is.EqualTo(CycleMessage));
        _csRepo.Verify(r => r.CreateSyncRuleMappingAsync(It.IsAny<SyncRuleMapping>()), Times.Never);
    }

    [Test]
    public async Task CreateSyncRuleMappingAsync_ValidDerivedMappingCallingNow_SavesAndReportsTheWarningAsync()
    {
        var jim = BuildApplication();
        var mapping = NewAdMapping(0, _model.Email, "mv[\"Display Name\"] + FormatDate(Now(), \"yyyy\")");

        await jim.ConnectedSystems.CreateSyncRuleMappingAsync(mapping, _initiator);

        _csRepo.Verify(r => r.CreateSyncRuleMappingAsync(mapping), Times.Once);
        Assert.That(mapping.SaveWarnings, Has.Count.EqualTo(1));
        Assert.That(mapping.SaveWarnings[0], Does.Contain("calls Now()"));
    }

    [Test]
    public void CreateSyncRuleMappingAsync_UnknownMetaverseName_IsRejected()
    {
        var jim = BuildApplication();
        var mapping = NewAdMapping(0, _model.Email, "mv[\"Acount Name\"] + \"@corp.local\"");

        var ex = Assert.ThrowsAsync<DerivedFlowValidationException>(async () =>
            await jim.ConnectedSystems.CreateSyncRuleMappingAsync(mapping, _initiator));

        Assert.That(ex!.Message, Does.Contain("'Acount Name' is not an attribute of the Metaverse Object Type 'Person'"));
    }

    [Test]
    public async Task CreateSyncRuleMappingAsync_MappingNotReadingMetaverse_LoadsNothingExtraAsync()
    {
        var jim = BuildApplication();
        var mapping = NewAdMapping(0, _model.Email, "cs[\"mail\"]");

        await jim.ConnectedSystems.CreateSyncRuleMappingAsync(mapping, _initiator);

        _csRepo.Verify(r => r.GetImportSyncRulesForMetaverseObjectTypeAsync(It.IsAny<int>()), Times.Never,
            "an ordinary mapping save must be unchanged: no derived-flow work at all");
        Assert.That(mapping.SaveWarnings, Is.Empty);
    }

    [Test]
    public async Task CreateSyncRuleMappingAsync_ExportMappingReadingMetaverse_IsNotDerivedFlowWorkAsync()
    {
        var jim = BuildApplication();
        var export = ExportRule(5, "AD Export", connectedSystemId: 20);
        var mapping = new SyncRuleMapping
        {
            SyncRule = export,
            SyncRuleId = export.Id,
            TargetConnectedSystemAttribute = new ConnectedSystemObjectTypeAttribute { Id = 900, Name = "mail", Type = AttributeDataType.Text },
            TargetConnectedSystemAttributeId = 900
        };
        mapping.Sources.Add(new SyncRuleMappingSource { Expression = "mv[\"Email\"]" });

        await jim.ConnectedSystems.CreateSyncRuleMappingAsync(mapping, _initiator);

        _csRepo.Verify(r => r.CreateSyncRuleMappingAsync(mapping), Times.Once, "export expressions have always read mv and are never derived-flow work");
        _csRepo.Verify(r => r.GetImportSyncRulesForMetaverseObjectTypeAsync(It.IsAny<int>()), Times.Never);
    }

    // ---- UpdateSyncRuleMappingAsync ----

    [Test]
    public void UpdateSyncRuleMappingAsync_ExistingMappingNowReadingItself_IsRejectedAsASelfReference()
    {
        var jim = BuildApplication();
        var mapping = NewAdMapping(103, _model.Region, "mv[\"Region\"] + cs[\"suffix\"]");

        var ex = Assert.ThrowsAsync<DerivedFlowValidationException>(async () =>
            await jim.ConnectedSystems.UpdateSyncRuleMappingAsync(mapping, _initiator));

        Assert.That(ex!.Message, Is.EqualTo("Saving would create a dependency cycle: Region (Synchronisation Rule 'AD Import') reads Region."));
        _csRepo.Verify(r => r.UpdateSyncRuleMappingAsync(It.IsAny<SyncRuleMapping>()), Times.Never);
    }

    [Test]
    public void UpdateSyncRuleMappingAsync_ExistingMappingNowClosingACycle_IsRejected()
    {
        var jim = BuildApplication();
        var mapping = NewAdMapping(102, _model.MailNickname, "mv[\"Display Name\"]");

        var ex = Assert.ThrowsAsync<DerivedFlowValidationException>(async () =>
            await jim.ConnectedSystems.UpdateSyncRuleMappingAsync(mapping, _initiator));

        Assert.That(ex!.Message, Is.EqualTo(CycleMessage), "the proposal replaces the persisted mapping with the same id");
    }

    // ---- UpdateSyncRuleMappingSettingsAsync ----

    private SyncRuleMapping TrackedAdMapping(int id)
    {
        var persisted = _persistedRules[1].AttributeFlowRules.Single(m => m.Id == id);
        // The settings path loads the mapping tracked; a separate instance from the AsNoTracking rules read.
        var tracked = NewAdMapping(id, persisted.TargetMetaverseAttribute!, persisted.Sources[0].Expression!);
        _csRepo.Setup(r => r.GetSyncRuleMappingForUpdateAsync(id)).ReturnsAsync(tracked);
        return tracked;
    }

    [Test]
    public void UpdateSyncRuleMappingSettingsAsync_ExpressionClosingACycle_IsRejected()
    {
        var jim = BuildApplication();
        TrackedAdMapping(102);

        var ex = Assert.ThrowsAsync<DerivedFlowValidationException>(async () =>
            await jim.ConnectedSystems.UpdateSyncRuleMappingSettingsAsync(102, new SyncRuleMappingSettingsUpdate { Expression = "mv[\"Display Name\"]" }, _initiator));

        Assert.That(ex!.Message, Is.EqualTo(CycleMessage));
        _csRepo.Verify(r => r.UpdateSyncRuleMappingAsync(It.IsAny<SyncRuleMapping>()), Times.Never);
    }

    [Test]
    public void UpdateSyncRuleMappingSettingsAsync_ApiKey_ExpressionClosingACycle_IsRejected()
    {
        var jim = BuildApplication();
        TrackedAdMapping(102);

        var ex = Assert.ThrowsAsync<DerivedFlowValidationException>(async () =>
            await jim.ConnectedSystems.UpdateSyncRuleMappingSettingsAsync(102, new SyncRuleMappingSettingsUpdate { Expression = "mv[\"Display Name\"]" }, _apiKey));

        Assert.That(ex!.Message, Is.EqualTo(CycleMessage));
    }

    [Test]
    public async Task UpdateSyncRuleMappingSettingsAsync_DerivedExpressionCallingRandom_ReturnsTheWarningOnTheMappingAsync()
    {
        var jim = BuildApplication();
        TrackedAdMapping(103);

        var updated = await jim.ConnectedSystems.UpdateSyncRuleMappingSettingsAsync(103,
            new SyncRuleMappingSettingsUpdate { Expression = "mv[\"Account Name\"] + RandomPassword(4, false)" }, _initiator);

        Assert.That(updated, Is.Not.Null);
        Assert.That(updated!.SaveWarnings, Has.Count.EqualTo(1));
        Assert.That(updated!.SaveWarnings[0], Does.Contain("calls RandomPassword()"));
    }

    [Test]
    public async Task UpdateSyncRuleMappingSettingsAsync_SettingUnrelatedToTheExpression_ReadsNoDerivedFlowConfigurationAsync()
    {
        var jim = BuildApplication();
        TrackedAdMapping(103);

        var updated = await jim.ConnectedSystems.UpdateSyncRuleMappingSettingsAsync(103, new SyncRuleMappingSettingsUpdate { NullIsValue = true }, _initiator);

        Assert.That(updated!.NullIsValue, Is.True);
        _csRepo.Verify(r => r.GetImportSyncRulesForMetaverseObjectTypeAsync(It.IsAny<int>()), Times.Never);
    }

    // ---- CreateOrUpdateSyncRuleAsync (the portal's save path) ----

    private SyncRule WholeRuleProposal(int id, string name)
    {
        var rule = ImportRule(id, name, connectedSystemId: id == 1 ? 10 : 20);
        rule.Enabled = false; // keeps the deselected-object-type guard out of the way
        rule.ConnectedSystem = new ConnectedSystem { Id = rule.ConnectedSystemId, Name = name };
        rule.ConnectedSystemObjectType = new ConnectedSystemObjectType { Id = 7, Name = "user" };
        rule.ConnectedSystemObjectTypeId = 7;
        rule.MetaverseObjectType = _model.Person;
        return rule;
    }

    [Test]
    public void CreateOrUpdateSyncRuleAsync_NewRuleClosingACycle_IsRejectedBeforeAnythingIsWritten()
    {
        var jim = BuildApplication();
        _persistedRules[1].AttributeFlowRules.RemoveAll(m => m.Id == 102);
        var rule = WholeRuleProposal(0, "AD Import");
        Expression(rule, 0, _model.MailNickname, "mv[\"Display Name\"]");

        var ex = Assert.ThrowsAsync<DerivedFlowValidationException>(async () =>
            await jim.ConnectedSystems.CreateOrUpdateSyncRuleAsync(rule, _initiator));

        Assert.That(ex!.Message, Is.EqualTo(CycleMessage));
        _csRepo.Verify(r => r.CreateSyncRuleAsync(It.IsAny<SyncRule>()), Times.Never);
    }

    [Test]
    public void CreateOrUpdateSyncRuleAsync_ApiKey_ExistingRuleNowClosingACycle_IsRejected()
    {
        var jim = BuildApplication();
        var rule = WholeRuleProposal(2, "AD Import");
        Expression(rule, 102, _model.MailNickname, "mv[\"Display Name\"]");

        var ex = Assert.ThrowsAsync<DerivedFlowValidationException>(async () =>
            await jim.ConnectedSystems.CreateOrUpdateSyncRuleAsync(rule, _apiKey));

        Assert.That(ex!.Message, Is.EqualTo(CycleMessage));
        _csRepo.Verify(r => r.UpdateSyncRuleAsync(It.IsAny<SyncRule>()), Times.Never);
    }

    [Test]
    public async Task CreateOrUpdateSyncRuleAsync_ProposalReplacesThePersistedRule_SoARemovedDependencyNoLongerCountsAsync()
    {
        var jim = BuildApplication();
        // HR Import is saved with its Display Name flow (101, reading Mail Nickname) removed, and a new flow deriving
        // Mail Nickname from Display Name added. Against the persisted HR Import that would be a cycle; against the
        // proposal it is not, because the proposal replaces the persisted rule wholesale.
        var rule = WholeRuleProposal(1, "HR Import");
        var mailNickname = Expression(rule, 0, _model.MailNickname, "mv[\"Display Name\"] + FormatDate(Today(), \"yy\")");

        var saved = await jim.ConnectedSystems.CreateOrUpdateSyncRuleAsync(rule, _initiator);

        Assert.That(saved, Is.True);
        _csRepo.Verify(r => r.UpdateSyncRuleAsync(rule), Times.Once);
        Assert.That(mailNickname.SaveWarnings, Has.Count.EqualTo(1), "the portal reads the warning off the mapping it saved");
        Assert.That(mailNickname.SaveWarnings[0], Does.Contain("calls Today()"));
    }

    [Test]
    public void CreateOrUpdateSyncRuleAsync_DisabledMappingOnAnotherRule_StillCountsTowardsACycle()
    {
        var jim = BuildApplication();
        _persistedRules[0].AttributeFlowRules[0].Enabled = false;
        _persistedRules[0].Enabled = false;
        var rule = WholeRuleProposal(2, "AD Import");
        Expression(rule, 102, _model.MailNickname, "mv[\"Display Name\"]");

        Assert.ThrowsAsync<DerivedFlowValidationException>(async () =>
            await jim.ConnectedSystems.CreateOrUpdateSyncRuleAsync(rule, _initiator),
            "enabling the paused flow later must never be able to complete a cycle (plan decision 2)");
    }
}
