// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Text;
using JIM.Application;
using JIM.Application.Interfaces;
using JIM.Application.Services;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Exceptions;
using JIM.Models.Logic;
using JIM.Models.Logic.DTOs;
using JIM.Models.Security;
using JIM.Models.Staging;
using Microsoft.EntityFrameworkCore;
using Moq;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// Every save that can change what an import produces records a ConfigurationChangeSynchronisation row for the
/// Connected System Object Type it can affect, so the system's next synchronisation processes every object of that
/// type (#2615). These tests drive each save path through its public entry point, with the repositories mocked, and
/// assert on the rows the save hands to the repository. Whether a particular change is worth a row is the policy's
/// decision (<see cref="ConfigurationChangeSynchronisationPolicyTests"/>); these tests are about the wiring: that each
/// path asks, asks about the right Object Type, and fails loudly when it cannot record.
/// </summary>
[TestFixture]
public class ConfigurationChangeSynchronisationRecordingTests
{
    private const int SyncRuleId = 55;
    private const int ConnectedSystemId = 3;
    private const int ObjectTypeId = 7;
    private static readonly byte[] HashKey = new byte[32];

    private Mock<IRepository> _repo = null!;
    private Mock<IActivityRepository> _activityRepo = null!;
    private Mock<IServiceSettingsRepository> _settingsRepo = null!;
    private Mock<IConnectedSystemRepository> _csRepo = null!;
    private FakeProtection _protection = null!;
    private JimApplication _jim = null!;
    private List<ConfigurationChangeSynchronisation> _recorded = null!;
    private List<Activity> _createdActivities = null!;
    private List<Activity> _updatedActivities = null!;

    [SetUp]
    public void SetUp()
    {
        TestUtilities.SetEnvironmentVariables();

        _recorded = [];
        _createdActivities = [];
        _updatedActivities = [];

        _repo = new Mock<IRepository>();
        _repo.Setup(r => r.ConfigurationTests).Returns(TestUtilities.CreateEmptyConfigurationTestRepository());
        _activityRepo = new Mock<IActivityRepository>();
        _settingsRepo = new Mock<IServiceSettingsRepository>();
        _csRepo = new Mock<IConnectedSystemRepository>();
        _repo.Setup(r => r.Activity).Returns(_activityRepo.Object);
        _repo.Setup(r => r.ServiceSettings).Returns(_settingsRepo.Object);
        _repo.Setup(r => r.ConnectedSystems).Returns(_csRepo.Object);

        var metaverseRepo = new Mock<IMetaverseRepository>();
        metaverseRepo.Setup(r => r.GetContributedValuesSummaryAsync(It.IsAny<int>(), It.IsAny<int?>())).ReturnsAsync(new ContributedValuesSummary());
        _repo.Setup(r => r.Metaverse).Returns(metaverseRepo.Object);

        // The repository gives an Activity its id on create; the saves link the row to it.
        _activityRepo.Setup(r => r.CreateActivityAsync(It.IsAny<Activity>()))
            .Callback<Activity>(a =>
            {
                if (a.Id == Guid.Empty)
                    a.Id = Guid.NewGuid();
                _createdActivities.Add(a);
            })
            .Returns(Task.CompletedTask);
        _activityRepo.Setup(r => r.UpdateActivityAsync(It.IsAny<Activity>()))
            .Callback<Activity>(a => _updatedActivities.Add(a))
            .Returns(Task.CompletedTask);
        _activityRepo.Setup(r => r.GetMaxConfigurationChangeVersionAsync(It.IsAny<ActivityTargetType>(), It.IsAny<int>())).ReturnsAsync(6);

        _csRepo.Setup(r => r.CreateConfigurationChangeSynchronisationAsync(It.IsAny<ConfigurationChangeSynchronisation>()))
            .Callback<ConfigurationChangeSynchronisation>(c => _recorded.Add(c))
            .Returns(Task.CompletedTask);
        _csRepo.Setup(r => r.UpdateSyncRuleAsync(It.IsAny<SyncRule>())).Returns(Task.CompletedTask);
        _csRepo.Setup(r => r.CreateSyncRuleAsync(It.IsAny<SyncRule>())).Returns(Task.CompletedTask);
        _csRepo.Setup(r => r.CreateSyncRuleMappingAsync(It.IsAny<SyncRuleMapping>())).Returns(Task.CompletedTask);
        _csRepo.Setup(r => r.UpdateSyncRuleMappingAsync(It.IsAny<SyncRuleMapping>())).Returns(Task.CompletedTask);
        _csRepo.Setup(r => r.DeleteSyncRuleMappingAsync(It.IsAny<SyncRuleMapping>())).Returns(Task.CompletedTask);
        _csRepo.Setup(r => r.UpdateSyncRuleMappingsAsync(It.IsAny<IReadOnlyCollection<SyncRuleMapping>>())).Returns(Task.CompletedTask);
        _csRepo.Setup(r => r.GetSyncRuleMappingsAsync(It.IsAny<int>())).ReturnsAsync(new List<SyncRuleMapping>());
        _csRepo.Setup(r => r.GetImportSyncRulesForMetaverseObjectTypeAsync(It.IsAny<int>())).ReturnsAsync(() => []);
        // The attribute priority bookkeeping that follows a mapping or rule save: no mapping contributes to any attribute
        // unless a test says so.
        _csRepo.Setup(r => r.GetImportMappingTargetMetaverseAttributesAsync(It.IsAny<int>())).ReturnsAsync(new Dictionary<int, int>());
        _csRepo.Setup(r => r.GetImportSyncRuleMappingsForMetaverseAttributeAsync(It.IsAny<int>(), It.IsAny<int>())).ReturnsAsync(new List<SyncRuleMapping>());

        _protection = new FakeProtection();
        _jim = new JimApplication(_repo.Object, syncRepository: TestUtilities.QueuedChangeWithdrawalSyncRepository()) { CredentialProtection = _protection };

        SetupTrackingSetting(enabled: true);
        SetupHashKeySetting();
    }

    [TearDown]
    public void TearDown() => _jim?.Dispose();

    // -- whole-rule saves ----------------------------------------------------------------------------------------------

    [Test]
    public async Task CreateOrUpdateSyncRuleAsync_ImportRuleChanged_UserInitiated_RecordsItsObjectTypeAsync()
    {
        SetupStoredRule(BuildImportRule(r => r.ProjectToMetaverse = true));

        var saved = await _jim.ConnectedSystems.CreateOrUpdateSyncRuleAsync(BuildImportRule(r => r.ProjectToMetaverse = false), NewUser());

        Assert.That(saved, Is.True);
        AssertRecordedFor(ObjectTypeId);
    }

    [Test]
    public async Task CreateOrUpdateSyncRuleAsync_ImportRuleChanged_ApiKeyInitiated_RecordsItsObjectTypeAsync()
    {
        SetupStoredRule(BuildImportRule(r => r.ProjectToMetaverse = true));

        var saved = await _jim.ConnectedSystems.CreateOrUpdateSyncRuleAsync(BuildImportRule(r => r.ProjectToMetaverse = false), NewApiKey());

        Assert.That(saved, Is.True);
        AssertRecordedFor(ObjectTypeId);
    }

    [Test]
    public async Task CreateOrUpdateSyncRuleAsync_ImportRuleChanged_RowLinksTheSavesActivityAndTheRuleAndSaysWhyAsync()
    {
        SetupStoredRule(BuildImportRule(r => r.ProjectToMetaverse = true));

        await _jim.ConnectedSystems.CreateOrUpdateSyncRuleAsync(BuildImportRule(r => r.ProjectToMetaverse = false), NewUser());

        var change = AssertRecordedFor(ObjectTypeId);
        var saveActivity = _createdActivities.Single(a => a.SyncRuleId == SyncRuleId);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(change.ConnectedSystemId, Is.EqualTo(ConnectedSystemId));
            Assert.That(change.SyncRuleId, Is.EqualTo(SyncRuleId));
            Assert.That(change.ConfigurationChangeActivityId, Is.EqualTo(saveActivity.Id), "the row names the save that asked for it");
            Assert.That(change.Reason, Does.Contain("HR Import").And.Contain("Project to Metaverse"), "the reason reads after the rule is gone");
            Assert.That(change.ClaimedByActivityId, Is.Null);
            Assert.That(change.AppliedAt, Is.Null);
        }
    }

    [Test]
    public async Task CreateOrUpdateSyncRuleAsync_ImportRuleChanged_RecordsOnlyOnceTheSaveHasBeenWrittenAsync()
    {
        // A row is claimed by the next synchronisation as soon as it is committed, so it must never be visible before
        // the configuration it describes: that ordering is what lets the worker's claim treat every row as already in
        // the rules it loaded.
        var calls = new List<string>();
        SetupStoredRule(BuildImportRule(r => r.ProjectToMetaverse = true));
        _csRepo.Setup(r => r.UpdateSyncRuleAsync(It.IsAny<SyncRule>())).Callback(() => calls.Add("rule saved")).Returns(Task.CompletedTask);
        _csRepo.Setup(r => r.CreateConfigurationChangeSynchronisationAsync(It.IsAny<ConfigurationChangeSynchronisation>()))
            .Callback(() => calls.Add("change recorded")).Returns(Task.CompletedTask);

        await _jim.ConnectedSystems.CreateOrUpdateSyncRuleAsync(BuildImportRule(r => r.ProjectToMetaverse = false), NewUser());

        Assert.That(calls, Is.EqualTo(new[] { "rule saved", "change recorded" }));
    }

    [Test]
    public async Task CreateOrUpdateSyncRuleAsync_ImportRuleDisabled_RecordsItsObjectTypeAsync()
    {
        SetupStoredRule(BuildImportRule());

        await _jim.ConnectedSystems.CreateOrUpdateSyncRuleAsync(BuildImportRule(r => r.Enabled = false), NewUser());

        AssertRecordedFor(ObjectTypeId);
    }

    [Test]
    public async Task CreateOrUpdateSyncRuleAsync_ImportRuleEditedWhileItStaysDisabled_RecordsNothingAsync()
    {
        // Switched off before and after: nothing it holds reaches the objects, so walking their type would change nothing.
        SetupStoredRule(BuildImportRule(r => { r.Enabled = false; r.ProjectToMetaverse = true; }));

        await _jim.ConnectedSystems.CreateOrUpdateSyncRuleAsync(BuildImportRule(r => { r.Enabled = false; r.ProjectToMetaverse = false; }), NewUser());

        Assert.That(_recorded, Is.Empty);
    }

    [Test]
    public async Task CreateOrUpdateSyncRuleAsync_DisabledImportRuleIsEnabled_RecordsItsObjectTypeAsync()
    {
        SetupStoredRule(BuildImportRule(r => r.Enabled = false));

        await _jim.ConnectedSystems.CreateOrUpdateSyncRuleAsync(BuildImportRule(), NewUser());

        AssertRecordedFor(ObjectTypeId);
    }

    [Test]
    public async Task CreateSyncRuleMappingAsync_ImportRuleThatIsDisabled_RecordsNothingAsync()
    {
        // The same rule applies to a granular edit: a mapping added to a rule that is off reaches no object.
        SetupStoredRule(BuildImportRule(r => r.Enabled = false), reloadedWithMapping: true);

        await _jim.ConnectedSystems.CreateSyncRuleMappingAsync(NewMapping(), NewApiKey());

        Assert.That(_recorded, Is.Empty);
    }

    [Test]
    public async Task CreateOrUpdateSyncRuleAsync_ImportRuleRenamed_RecordsNothingAsync()
    {
        SetupStoredRule(BuildImportRule());

        await _jim.ConnectedSystems.CreateOrUpdateSyncRuleAsync(BuildImportRule(r => r.Name = "HR Import (renamed)"), NewUser());

        Assert.That(_recorded, Is.Empty, "a rename changes no synchronisation outcome");
    }

    [Test]
    public async Task CreateOrUpdateSyncRuleAsync_ImportRuleSavedUnchanged_RecordsNothingAsync()
    {
        SetupStoredRule(BuildImportRule());

        await _jim.ConnectedSystems.CreateOrUpdateSyncRuleAsync(BuildImportRule(), NewUser());

        Assert.That(_recorded, Is.Empty);
    }

    [Test]
    public async Task CreateOrUpdateSyncRuleAsync_NewEnabledImportRule_RecordsItsObjectTypeAsync()
    {
        var saved = await _jim.ConnectedSystems.CreateOrUpdateSyncRuleAsync(BuildImportRule(r => r.Id = 0), NewUser());

        Assert.That(saved, Is.True);
        AssertRecordedFor(ObjectTypeId);
    }

    [Test]
    public async Task CreateOrUpdateSyncRuleAsync_NewDisabledImportRule_RecordsNothingAsync()
    {
        await _jim.ConnectedSystems.CreateOrUpdateSyncRuleAsync(BuildImportRule(r => { r.Id = 0; r.Enabled = false; }), NewUser());

        Assert.That(_recorded, Is.Empty, "a rule created disabled applies to nothing; enabling it later records then");
    }

    [Test]
    public async Task CreateOrUpdateSyncRuleAsync_ExportRuleChanged_RecordsNothingAsync()
    {
        // Export follows on its own: processing the import types reprocesses the Metaverse Objects, and export
        // evaluation runs on them as usual (plan decision 5).
        SetupStoredRule(BuildExportRule(r => r.Enabled = true));

        await _jim.ConnectedSystems.CreateOrUpdateSyncRuleAsync(BuildExportRule(r => r.Enabled = false), NewUser());

        Assert.That(_recorded, Is.Empty);
    }

    [Test]
    public async Task CreateOrUpdateSyncRuleAsync_TrackingDisabled_RecordsBecauseWhatChangedIsUnknownAsync()
    {
        SetupTrackingSetting(enabled: false);

        await _jim.ConnectedSystems.CreateOrUpdateSyncRuleAsync(BuildImportRule(), NewUser());

        var change = AssertRecordedFor(ObjectTypeId);
        Assert.That(change.Reason, Does.Contain("unknown"));
    }

    [Test]
    public async Task CreateOrUpdateSyncRuleAsync_TrackingDisabledAndRuleDisabled_StillRecordsAsync()
    {
        // With nothing compared, a disabled rule may just have been enabled: processing a type needlessly costs time,
        // missing one leaves objects wrong.
        SetupTrackingSetting(enabled: false);

        await _jim.ConnectedSystems.CreateOrUpdateSyncRuleAsync(BuildImportRule(r => r.Enabled = false), NewUser());

        AssertRecordedFor(ObjectTypeId);
    }

    [Test]
    public async Task CreateOrUpdateSyncRuleAsync_CaptureFailsAndIsSwallowed_RecordsBecauseWhatChangedIsUnknownAsync()
    {
        // The hash key cannot be read, so the capture fails (best-effort, swallowed) and the change history misses this
        // save. The change must still reach the objects.
        _settingsRepo.Setup(r => r.GetSettingAsync(Constants.SettingKeys.ConfigurationChangeHashKey))
            .ThrowsAsync(new InvalidOperationException("the key cannot be read"));

        var saved = await _jim.ConnectedSystems.CreateOrUpdateSyncRuleAsync(BuildImportRule(), NewUser());

        Assert.That(saved, Is.True);
        AssertRecordedFor(ObjectTypeId);
    }

    // -- Attribute Flow mappings ---------------------------------------------------------------------------------------

    [Test]
    public async Task CreateSyncRuleMappingAsync_ImportRule_RecordsItsObjectTypeAsync()
    {
        SetupStoredRule(BuildImportRule(), reloadedWithMapping: true);

        await _jim.ConnectedSystems.CreateSyncRuleMappingAsync(NewMapping(), NewApiKey());

        AssertRecordedFor(ObjectTypeId);
    }

    [Test]
    public async Task UpdateSyncRuleMappingAsync_ImportRule_RecordsItsObjectTypeAsync()
    {
        SetupStoredRule(BuildImportRule(), reloadedWithMapping: true);

        await _jim.ConnectedSystems.UpdateSyncRuleMappingAsync(NewMapping(), NewUser());

        AssertRecordedFor(ObjectTypeId);
    }

    [Test]
    public async Task DeleteSyncRuleMappingAsync_ImportRule_RecordsItsObjectTypeAsync()
    {
        SetupStoredRule(BuildImportRule(), reloadedWithMapping: true);

        await _jim.ConnectedSystems.DeleteSyncRuleMappingAsync(NewMapping(), NewApiKey());

        AssertRecordedFor(ObjectTypeId);
    }

    [Test]
    public async Task CreateSyncRuleMappingAsync_ExportRule_RecordsNothingAsync()
    {
        SetupStoredRule(BuildExportRule(), reloadedWithMapping: true);

        await _jim.ConnectedSystems.CreateSyncRuleMappingAsync(NewMapping(), NewApiKey());

        Assert.That(_recorded, Is.Empty);
    }

    [Test]
    public void CreateSyncRuleMappingAsync_RecordingFails_FailsTheMappingsActivityAndThrowsAsync()
    {
        SetupStoredRule(BuildImportRule(), reloadedWithMapping: true);
        _csRepo.Setup(r => r.CreateConfigurationChangeSynchronisationAsync(It.IsAny<ConfigurationChangeSynchronisation>()))
            .ThrowsAsync(new DbUpdateException("the row could not be saved"));

        var thrown = Assert.ThrowsAsync<ConfigurationChangeSynchronisationNotRecordedException>(
            async () => await _jim.ConnectedSystems.CreateSyncRuleMappingAsync(NewMapping(), NewApiKey()));

        Assert.That(thrown!.InnerException, Is.TypeOf<DbUpdateException>());
        var mappingActivity = _createdActivities.Single();
        Assert.That(mappingActivity.Status, Is.EqualTo(ActivityStatus.FailedWithError));
    }

    // -- attribute priority --------------------------------------------------------------------------------------------

    [Test]
    public async Task SetAttributePriorityOrderAsync_ImportRulesReordered_RecordsEachAffectedRulesObjectTypeAsync()
    {
        // Two import rules on different Object Types of different systems contribute to one Metaverse attribute; moving
        // one above the other changes which wins, for objects of both types.
        var first = BuildMapping(10, priority: 1, ruleId: 1000, objectTypeId: 70, connectedSystemId: 30);
        var second = BuildMapping(20, priority: 2, ruleId: 2000, objectTypeId: 80, connectedSystemId: 40);
        _csRepo.Setup(r => r.GetImportSyncRuleMappingsForMetaverseAttributeAsync(1, 42)).ReturnsAsync(new List<SyncRuleMapping> { first, second });
        SetupReloadedPriorityRule(1000, objectTypeId: 70, connectedSystemId: 30, priority: 2);
        SetupReloadedPriorityRule(2000, objectTypeId: 80, connectedSystemId: 40, priority: 1);

        await _jim.ConnectedSystems.SetAttributePriorityOrderAsync(1, 42, [(20, false), (10, false)], NewUser());

        Assert.That(_recorded.Select(c => c.ConnectedSystemObjectTypeId), Is.EquivalentTo(new[] { 70, 80 }));
        Assert.That(_recorded.Select(c => c.SyncRuleId), Is.EquivalentTo(new int?[] { 1000, 2000 }));
    }

    [Test]
    public void SetAttributePriorityOrderAsync_RecordingFails_FailsTheChildAndTheReorderActivityAndThrowsAsync()
    {
        var first = BuildMapping(10, priority: 1, ruleId: 1000, objectTypeId: 70, connectedSystemId: 30);
        var second = BuildMapping(20, priority: 2, ruleId: 2000, objectTypeId: 80, connectedSystemId: 40);
        _csRepo.Setup(r => r.GetImportSyncRuleMappingsForMetaverseAttributeAsync(1, 42)).ReturnsAsync(new List<SyncRuleMapping> { first, second });
        SetupReloadedPriorityRule(1000, objectTypeId: 70, connectedSystemId: 30, priority: 2);
        SetupReloadedPriorityRule(2000, objectTypeId: 80, connectedSystemId: 40, priority: 1);
        _csRepo.Setup(r => r.CreateConfigurationChangeSynchronisationAsync(It.IsAny<ConfigurationChangeSynchronisation>()))
            .ThrowsAsync(new TimeoutException("the database did not answer"));

        Assert.ThrowsAsync<ConfigurationChangeSynchronisationNotRecordedException>(
            async () => await _jim.ConnectedSystems.SetAttributePriorityOrderAsync(1, 42, [(20, false), (10, false)], NewUser()));

        var failed = _updatedActivities.Where(a => a.Status == ActivityStatus.FailedWithError).ToList();
        Assert.That(failed, Has.Count.EqualTo(2), "the child that could not record, and the reorder Activity it belongs to, which must not be left in progress");
    }

    // -- Object Matching Rules -----------------------------------------------------------------------------------------

    [Test]
    public async Task CreateObjectMatchingRuleAsync_AdvancedModeRuleOnAnImportRule_RecordsTheRulesObjectTypeAsync()
    {
        SetupStoredRule(BuildImportRule(), reloadedWithMatchingRule: true);
        _csRepo.Setup(r => r.CreateObjectMatchingRuleAsync(It.IsAny<ObjectMatchingRule>())).Returns(Task.CompletedTask);

        await _jim.ConnectedSystems.CreateObjectMatchingRuleAsync(BuildAdvancedModeMatchingRule(), NewApiKey());

        AssertRecordedFor(ObjectTypeId);
    }

    [Test]
    public async Task DeleteObjectMatchingRuleAsync_AdvancedModeRuleOnAnImportRule_RecordsTheRulesObjectTypeAsync()
    {
        SetupStoredRule(BuildImportRule(), reloadedWithMatchingRule: true);
        _csRepo.Setup(r => r.DeleteObjectMatchingRuleAsync(It.IsAny<ObjectMatchingRule>())).Returns(Task.CompletedTask);

        await _jim.ConnectedSystems.DeleteObjectMatchingRuleAsync(BuildAdvancedModeMatchingRule(), NewUser());

        AssertRecordedFor(ObjectTypeId);
    }

    [Test]
    public async Task CreateObjectMatchingRuleAsync_SimpleModeRuleOnAnObjectTypeWithAnEnabledImportRule_RecordsThatObjectTypeAsync()
    {
        // A Simple Mode rule belongs to the Connected System Object Type, not to a Synchronisation Rule, and the
        // Connected System capture it is recorded under names no type: the type must be carried through.
        SetupSimpleModeSystem(ImportRuleOn(55, ObjectTypeId, "user"), ImportRuleOn(56, 99, "group"));

        await _jim.ConnectedSystems.CreateObjectMatchingRuleAsync(BuildSimpleModeMatchingRule(), NewApiKey());

        var change = AssertRecordedFor(ObjectTypeId);
        Assert.That(change.SyncRuleId, Is.Null, "a Simple Mode rule is not a Synchronisation Rule's");
        Assert.That(change.Reason, Does.Contain("user").And.Contain("Object Matching Rule"));
    }

    [Test]
    public async Task UpdateObjectMatchingRuleAsync_SimpleModeRule_RecordsThatObjectTypeAsync()
    {
        SetupSimpleModeSystem(ImportRuleOn(55, ObjectTypeId, "user"));
        _csRepo.Setup(r => r.UpdateObjectMatchingRuleAsync(It.IsAny<ObjectMatchingRule>())).Returns(Task.CompletedTask);

        await _jim.ConnectedSystems.UpdateObjectMatchingRuleAsync(BuildSimpleModeMatchingRule(), NewUser());

        AssertRecordedFor(ObjectTypeId);
    }

    [Test]
    public async Task DeleteObjectMatchingRuleAsync_SimpleModeRule_RecordsThatObjectTypeAsync()
    {
        SetupSimpleModeSystem(ImportRuleOn(55, ObjectTypeId, "user"));
        _csRepo.Setup(r => r.DeleteObjectMatchingRuleAsync(It.IsAny<ObjectMatchingRule>())).Returns(Task.CompletedTask);

        await _jim.ConnectedSystems.DeleteObjectMatchingRuleAsync(BuildSimpleModeMatchingRule(), NewApiKey());

        AssertRecordedFor(ObjectTypeId);
    }

    [Test]
    public async Task UpdateObjectMatchingRuleAsync_SimpleModeRuleSavedUnchanged_RecordsNothingAsync()
    {
        // The Connected System's history already holds exactly this configuration, so the save changed nothing.
        SetupSimpleModeSystem(ImportRuleOn(55, ObjectTypeId, "user"));
        _csRepo.Setup(r => r.UpdateObjectMatchingRuleAsync(It.IsAny<ObjectMatchingRule>())).Returns(Task.CompletedTask);
        _activityRepo.Setup(r => r.GetLatestConfigurationChangeSnapshotAsync(ActivityTargetType.ConnectedSystem, ConnectedSystemId))
            .ReturnsAsync(ConfigurationSnapshotService.Serialise(
                _jim.ConfigurationSnapshots.CreateSnapshot(BuildSwitchableSystem(ObjectMatchingRuleMode.ConnectedSystem), HashKey)));

        await _jim.ConnectedSystems.UpdateObjectMatchingRuleAsync(BuildSimpleModeMatchingRule(), NewUser());

        Assert.That(_recorded, Is.Empty);
    }

    [Test]
    public async Task CreateObjectMatchingRuleAsync_SimpleModeRuleOnAnObjectTypeWithNoEnabledImportRule_RecordsNothingAsync()
    {
        // Nothing synchronises objects of a type no enabled import rule covers; enabling a rule later records then.
        SetupSimpleModeSystem(ImportRuleOn(55, ObjectTypeId, "user", enabled: false), ImportRuleOn(56, 99, "group"));

        await _jim.ConnectedSystems.CreateObjectMatchingRuleAsync(BuildSimpleModeMatchingRule(), NewApiKey());

        Assert.That(_recorded, Is.Empty);
    }

    [Test]
    public async Task CreateObjectMatchingRuleAsync_SimpleModeRule_OnlyAnotherSystemHasAnImportRuleForTheType_RecordsNothingAsync()
    {
        SetupSimpleModeSystem(ImportRuleOn(55, ObjectTypeId, "user", systemId: 99));

        await _jim.ConnectedSystems.CreateObjectMatchingRuleAsync(BuildSimpleModeMatchingRule(), NewApiKey());

        Assert.That(_recorded, Is.Empty, "the type ids are the system's own; another system's rule is not a reason");
    }

    // -- matching mode switch ------------------------------------------------------------------------------------------

    [Test]
    public async Task SwitchObjectMatchingModeAsync_ToAdvancedMode_RecordsEveryObjectTypeWithAnEnabledImportRuleAsync()
    {
        var connectedSystem = BuildSwitchableSystem(ObjectMatchingRuleMode.ConnectedSystem);
        SetupStoredConnectedSystem(connectedSystem);
        _csRepo.Setup(r => r.GetSyncRulesAsync(ConnectedSystemId, true, true)).ReturnsAsync(new List<SyncRule>());
        _csRepo.Setup(r => r.UpdateConnectedSystemAsync(It.IsAny<ConnectedSystem>())).Returns(Task.CompletedTask);
        SetupImportRuleHeaders(ImportRuleOn(55, ObjectTypeId, "user"), ImportRuleOn(56, 99, "group"), ImportRuleOn(57, 100, "role", enabled: false));

        var result = await _jim.ConnectedSystems.SwitchObjectMatchingModeAsync(connectedSystem, ObjectMatchingRuleMode.SyncRule, NewUser());

        Assert.That(result.Success, Is.True);
        Assert.That(_recorded.Select(c => c.ConnectedSystemObjectTypeId), Is.EquivalentTo(new[] { ObjectTypeId, 99 }),
            "every type with an enabled import rule: the mode decides how every one of them is matched");
    }

    [Test]
    public async Task SwitchObjectMatchingModeAsync_ToSimpleMode_RecordsEveryObjectTypeWithAnEnabledImportRuleAsync()
    {
        var connectedSystem = BuildSwitchableSystem(ObjectMatchingRuleMode.SyncRule);
        SetupStoredConnectedSystem(connectedSystem);
        _csRepo.Setup(r => r.GetSyncRulesAsync(ConnectedSystemId, true, true)).ReturnsAsync(new List<SyncRule>());
        _csRepo.Setup(r => r.UpdateConnectedSystemAsync(It.IsAny<ConnectedSystem>())).Returns(Task.CompletedTask);
        SetupImportRuleHeaders(ImportRuleOn(55, ObjectTypeId, "user"));

        await _jim.ConnectedSystems.SwitchObjectMatchingModeAsync(connectedSystem, ObjectMatchingRuleMode.ConnectedSystem, NewApiKey());

        AssertRecordedFor(ObjectTypeId);
    }

    [Test]
    public async Task SwitchObjectMatchingModeAsync_ToTheModeItIsAlreadyIn_RecordsNothingAsync()
    {
        var connectedSystem = BuildSwitchableSystem(ObjectMatchingRuleMode.SyncRule);
        SetupImportRuleHeaders(ImportRuleOn(55, ObjectTypeId, "user"));

        await _jim.ConnectedSystems.SwitchObjectMatchingModeAsync(connectedSystem, ObjectMatchingRuleMode.SyncRule, NewUser());

        Assert.That(_recorded, Is.Empty);
    }

    // -- the exemption -------------------------------------------------------------------------------------------------

    [Test]
    public async Task CaptureSyncRuleConfigurationChangeAsync_CallerOptsOutOfRecording_RecordsNothingAsync()
    {
        // A configuration test case is stored beside the rule it covers but changes nothing a synchronisation does;
        // its saves capture history and must not set a synchronisation in motion, even when tracking is off and the
        // capture could say nothing about what changed.
        SetupTrackingSetting(enabled: false);
        SetupStoredRule(BuildImportRule());
        var activity = new Activity { TargetType = ActivityTargetType.SynchronisationRule };

        await _jim.ConnectedSystems.CaptureSyncRuleConfigurationChangeAsync(activity, SyncRuleId, recordsAwaitingSynchronisation: false);

        Assert.That(_recorded, Is.Empty);
    }

    [Test]
    public async Task CreateTestCaseAsync_ScopeCaseOnAnImportRule_RecordsNothingEvenWhenTrackingIsOffAsync()
    {
        // A configuration test case changes nothing a synchronisation does. With tracking off the capture cannot grade
        // the save, which would make it record; the test case path opts out of recording altogether.
        SetupTrackingSetting(enabled: false);
        SetupStoredRule(BuildImportRule());
        var testRepository = new Mock<IConfigurationTestRepository>();
        testRepository.Setup(r => r.GetTestCasesForSyncRuleAsync(SyncRuleId)).ReturnsAsync((IReadOnlyList<ConfigurationTestCase>)[]);
        testRepository.Setup(r => r.CreateTestCaseAsync(It.IsAny<ConfigurationTestCase>())).Returns(Task.CompletedTask);
        _repo.Setup(r => r.ConfigurationTests).Returns(testRepository.Object);
        var testCase = new ConfigurationTestCase
        {
            Name = "Everyone is in scope", Kind = ConfigurationTestCaseKind.Scope, SyncRuleId = SyncRuleId,
            Expectation = new ConfigurationTestExpectation(ConfigurationTestExpectationKind.InScope)
        };

        await _jim.ConfigurationTests.CreateTestCaseAsync(testCase, NewUser());

        Assert.That(_recorded, Is.Empty);
    }

    // -- recording fails -----------------------------------------------------------------------------------------------

    [Test]
    public void CreateOrUpdateSyncRuleAsync_RecordingFails_FailsTheActivityNamingTheRemedyAndThrowsAsync()
    {
        SetupStoredRule(BuildImportRule(r => r.ProjectToMetaverse = true));
        _csRepo.Setup(r => r.CreateConfigurationChangeSynchronisationAsync(It.IsAny<ConfigurationChangeSynchronisation>()))
            .ThrowsAsync(new DbUpdateException("the row could not be saved"));

        var thrown = Assert.ThrowsAsync<ConfigurationChangeSynchronisationNotRecordedException>(
            async () => await _jim.ConnectedSystems.CreateOrUpdateSyncRuleAsync(BuildImportRule(r => r.ProjectToMetaverse = false), NewUser()));

        var saveActivity = _createdActivities.Single(a => a.SyncRuleId == SyncRuleId);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(saveActivity.Status, Is.EqualTo(ActivityStatus.FailedWithError), "the save is not reported as a success");
            Assert.That(saveActivity.ErrorMessage, Does.Contain("Full Synchronisation").And.Contain("HR").And.Contain("user"));
            Assert.That(thrown!.Message, Is.EqualTo(saveActivity.ErrorMessage));
            Assert.That(thrown.InnerException, Is.TypeOf<DbUpdateException>(), "the cause is kept for the log");
            Assert.That(_updatedActivities.Any(a => a.Status == ActivityStatus.Complete && a.Id == saveActivity.Id), Is.False,
                "the Activity is not completed over its failure");
        }
    }

    [Test]
    public void CreateOrUpdateSyncRuleAsync_RecordingFailsAndSoDoesFailingTheActivity_StillThrowsTheRecordingFailure()
    {
        // The database that refused the row is likely to refuse the Activity's failure too. What the caller needs is the
        // first failure, with its remedy, not the second.
        SetupStoredRule(BuildImportRule(r => r.ProjectToMetaverse = true));
        _csRepo.Setup(r => r.CreateConfigurationChangeSynchronisationAsync(It.IsAny<ConfigurationChangeSynchronisation>()))
            .ThrowsAsync(new DbUpdateException("the row could not be saved"));
        _activityRepo.Setup(r => r.UpdateActivityAsync(It.IsAny<Activity>()))
            .Returns<Activity>(a => a.Status == ActivityStatus.FailedWithError
                ? Task.FromException(new TimeoutException("the database did not answer"))
                : Task.CompletedTask);

        var thrown = Assert.ThrowsAsync<ConfigurationChangeSynchronisationNotRecordedException>(
            async () => await _jim.ConnectedSystems.CreateOrUpdateSyncRuleAsync(BuildImportRule(r => r.ProjectToMetaverse = false), NewUser()));

        Assert.That(thrown!.InnerException, Is.TypeOf<DbUpdateException>());
    }

    [TestCase(typeof(TimeoutException))]
    [TestCase(typeof(InvalidOperationException))]
    public void CreateOrUpdateSyncRuleAsync_RecordingFailsWithTransientError_FailsTheActivityAndThrowsAsync(Type exceptionType)
    {
        SetupStoredRule(BuildImportRule(r => r.ProjectToMetaverse = true));
        _csRepo.Setup(r => r.CreateConfigurationChangeSynchronisationAsync(It.IsAny<ConfigurationChangeSynchronisation>()))
            .ThrowsAsync((Exception)Activator.CreateInstance(exceptionType, "the database did not answer")!);

        Assert.ThrowsAsync<ConfigurationChangeSynchronisationNotRecordedException>(
            async () => await _jim.ConnectedSystems.CreateOrUpdateSyncRuleAsync(BuildImportRule(r => r.ProjectToMetaverse = false), NewApiKey()));

        var saveActivity = _createdActivities.Single(a => a.SyncRuleId == SyncRuleId);
        Assert.That(saveActivity.Status, Is.EqualTo(ActivityStatus.FailedWithError));
    }

    [Test]
    public void CreateOrUpdateSyncRuleAsync_RecordingThrowsSomethingUnexpected_IsNotSwallowedAsync()
    {
        // Only the failures a database write can produce are translated; anything else is a bug and surfaces as itself.
        SetupStoredRule(BuildImportRule(r => r.ProjectToMetaverse = true));
        _csRepo.Setup(r => r.CreateConfigurationChangeSynchronisationAsync(It.IsAny<ConfigurationChangeSynchronisation>()))
            .ThrowsAsync(new ArgumentException("a bug"));

        Assert.ThrowsAsync<ArgumentException>(
            async () => await _jim.ConnectedSystems.CreateOrUpdateSyncRuleAsync(BuildImportRule(r => r.ProjectToMetaverse = false), NewUser()));
    }

    // -- helpers -------------------------------------------------------------------------------------------------------

    private ConfigurationChangeSynchronisation AssertRecordedFor(int objectTypeId)
    {
        Assert.That(_recorded, Has.Count.EqualTo(1), "exactly one change awaiting synchronisation per save and Object Type");
        var change = _recorded[0];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(change.ConnectedSystemObjectTypeId, Is.EqualTo(objectTypeId));
            Assert.That(change.ConnectedSystemId, Is.EqualTo(ConnectedSystemId));
            Assert.That(change.Reason, Is.Not.Empty);
        }
        return change;
    }

    // The rule as the change history last saw it (so the capture has a baseline to diff against) and as a reload finds
    // it after the save.
    private void SetupStoredRule(SyncRule stored, bool reloadedWithMapping = false, bool reloadedWithMatchingRule = false)
    {
        _activityRepo.Setup(r => r.GetLatestConfigurationChangeSnapshotAsync(ActivityTargetType.SynchronisationRule, stored.Id))
            .ReturnsAsync(ConfigurationSnapshotService.Serialise(_jim.ConfigurationSnapshots.CreateSnapshot(stored, HashKey)));

        // The granular endpoints capture from the rule as reloaded, which now carries what the endpoint changed.
        var reloaded = stored.Direction == SyncRuleDirection.Import ? BuildImportRule() : BuildExportRule();
        reloaded.Id = stored.Id;
        reloaded.Enabled = stored.Enabled;
        if (reloadedWithMapping)
            reloaded.AttributeFlowRules = [new SyncRuleMapping { Id = 101, TargetMetaverseAttributeId = 42 }];
        if (reloadedWithMatchingRule)
            reloaded.ObjectMatchingRules = [new ObjectMatchingRule { Id = 9, SyncRuleId = stored.Id, TargetMetaverseAttributeId = 201, CaseSensitive = true }];
        _csRepo.Setup(r => r.GetSyncRuleAsync(stored.Id)).ReturnsAsync(reloaded);
    }

    private void SetupReloadedPriorityRule(int ruleId, int objectTypeId, int connectedSystemId, int priority)
    {
        var rule = new SyncRule
        {
            Id = ruleId,
            Name = $"Rule {ruleId}",
            Direction = SyncRuleDirection.Import,
            Enabled = true,
            ConnectedSystemId = connectedSystemId,
            ConnectedSystemObjectTypeId = objectTypeId,
            MetaverseObjectTypeId = 1,
            AttributeFlowRules = [new SyncRuleMapping { Id = ruleId / 100, Priority = priority, TargetMetaverseAttributeId = 42 }]
        };
        _csRepo.Setup(r => r.GetSyncRuleAsync(ruleId)).ReturnsAsync(rule);
    }

    private static SyncRuleMapping BuildMapping(int id, int priority, int ruleId, int objectTypeId, int connectedSystemId) => new()
    {
        Id = id,
        Priority = priority,
        TargetMetaverseAttributeId = 42,
        TargetMetaverseAttribute = new MetaverseAttribute { Id = 42, Name = "department" },
        SyncRuleId = ruleId,
        SyncRule = new SyncRule
        {
            Id = ruleId,
            Name = $"Rule {ruleId}",
            Enabled = true,
            Direction = SyncRuleDirection.Import,
            ConnectedSystemId = connectedSystemId,
            ConnectedSystemObjectTypeId = objectTypeId,
            ConnectedSystem = new ConnectedSystem { Id = connectedSystemId, Name = $"System {connectedSystemId}" }
        }
    };

    private static SyncRuleMapping NewMapping() => new()
    {
        Id = 0,
        SyncRule = new SyncRule { Id = SyncRuleId, Name = "HR Import" },
        SyncRuleId = SyncRuleId,
        TargetMetaverseAttributeId = 42,
        TargetMetaverseAttribute = new MetaverseAttribute { Id = 42, Name = "department", Type = AttributeDataType.Text },
        TargetConnectedSystemAttribute = new ConnectedSystemObjectTypeAttribute
        {
            Id = 9, Name = "displayName", Type = AttributeDataType.Text, Writability = AttributeWritability.Writable
        }
    };

    private static SyncRule BuildImportRule(Action<SyncRule>? edit = null)
    {
        var rule = new SyncRule
        {
            Id = SyncRuleId,
            Name = "HR Import",
            Direction = SyncRuleDirection.Import,
            Enabled = true,
            ProjectToMetaverse = true,
            ConnectedSystem = new ConnectedSystem { Id = ConnectedSystemId, Name = "HR" },
            ConnectedSystemId = ConnectedSystemId,
            ConnectedSystemObjectType = new ConnectedSystemObjectType { Id = ObjectTypeId, Name = "user", ConnectedSystemId = ConnectedSystemId },
            ConnectedSystemObjectTypeId = ObjectTypeId,
            MetaverseObjectType = new MetaverseObjectType { Id = 1, Name = "Person" },
            MetaverseObjectTypeId = 1
        };
        edit?.Invoke(rule);
        return rule;
    }

    private static SyncRule BuildExportRule(Action<SyncRule>? edit = null)
    {
        var rule = BuildImportRule();
        rule.Name = "AD Export";
        rule.Direction = SyncRuleDirection.Export;
        rule.ProjectToMetaverse = null;
        rule.ProvisionToConnectedSystem = true;
        edit?.Invoke(rule);
        return rule;
    }

    private static ObjectMatchingRule BuildAdvancedModeMatchingRule() => new()
    {
        Id = 9,
        SyncRuleId = SyncRuleId,
        SyncRule = new SyncRule
        {
            Id = SyncRuleId,
            Name = "HR Import",
            ConnectedSystemId = ConnectedSystemId,
            ConnectedSystemObjectTypeId = ObjectTypeId,
            ConnectedSystem = new ConnectedSystem { Id = ConnectedSystemId, Name = "HR", ObjectMatchingRuleMode = ObjectMatchingRuleMode.SyncRule },
            ConnectedSystemObjectType = new ConnectedSystemObjectType
            {
                Id = ObjectTypeId,
                Name = "user",
                ConnectedSystemId = ConnectedSystemId,
                Attributes = [new ConnectedSystemObjectTypeAttribute { Id = 101, Name = "employeeId", Type = AttributeDataType.Text }]
            }
        },
        TargetMetaverseAttributeId = 201,
        TargetMetaverseAttribute = new MetaverseAttribute { Id = 201, Name = "Employee ID", Type = AttributeDataType.Text },
        Sources = [new ObjectMatchingRuleSource { Order = 0, ConnectedSystemAttributeId = 101 }]
    };

    private static ObjectMatchingRule BuildSimpleModeMatchingRule() => new()
    {
        Id = 9,
        ConnectedSystemObjectTypeId = ObjectTypeId,
        ConnectedSystemObjectType = new ConnectedSystemObjectType
        {
            Id = ObjectTypeId,
            Name = "user",
            ConnectedSystemId = ConnectedSystemId,
            ConnectedSystem = new ConnectedSystem { Id = ConnectedSystemId, Name = "HR" },
            Attributes = [new ConnectedSystemObjectTypeAttribute { Id = 101, Name = "employeeId", Type = AttributeDataType.Text }]
        },
        MetaverseObjectTypeId = 1,
        TargetMetaverseAttributeId = 201,
        TargetMetaverseAttribute = new MetaverseAttribute { Id = 201, Name = "Employee ID", Type = AttributeDataType.Text },
        Sources = [new ObjectMatchingRuleSource { Order = 0, ConnectedSystemAttributeId = 101 }]
    };

    private static ConnectedSystem BuildSwitchableSystem(ObjectMatchingRuleMode mode) => new()
    {
        Id = ConnectedSystemId,
        Name = "HR",
        ObjectMatchingRuleMode = mode,
        SettingValues = [],
        RunProfiles = [],
        Partitions = [],
        ObjectTypes = [new ConnectedSystemObjectType { Id = ObjectTypeId, Name = "user", ConnectedSystemId = ConnectedSystemId, ObjectMatchingRules = [] }],
        ConnectorDefinition = new ConnectorDefinition { Id = 1, Name = "Test Connector" }
    };

    private void SetupStoredConnectedSystem(ConnectedSystem connectedSystem) =>
        _csRepo.Setup(r => r.GetConnectedSystemAsync(connectedSystem.Id, It.IsAny<bool>())).ReturnsAsync(connectedSystem);

    // A Simple Mode system: the Object Matching Rule lives on the type, the capture reloads the system, and the
    // enabled import rules say which types are synchronised.
    private void SetupSimpleModeSystem(params (int RuleId, int TypeId, string TypeName, bool Enabled, int SystemId)[] importRules)
    {
        SetupStoredConnectedSystem(BuildSwitchableSystem(ObjectMatchingRuleMode.ConnectedSystem));
        _csRepo.Setup(r => r.CreateObjectMatchingRuleAsync(It.IsAny<ObjectMatchingRule>())).Returns(Task.CompletedTask);
        SetupImportRuleHeaders(importRules);
    }

    private static (int RuleId, int TypeId, string TypeName, bool Enabled, int SystemId) ImportRuleOn(
        int ruleId, int typeId, string typeName, bool enabled = true, int systemId = ConnectedSystemId) =>
        (ruleId, typeId, typeName, enabled, systemId);

    private void SetupImportRuleHeaders(params (int RuleId, int TypeId, string TypeName, bool Enabled, int SystemId)[] rules)
    {
        IList<SyncRuleHeader> headers = rules.Select(r => new SyncRuleHeader
        {
            Id = r.RuleId,
            Name = $"Import {r.TypeName}",
            ConnectedSystemId = r.SystemId,
            ConnectedSystemObjectTypeId = r.TypeId,
            ConnectedSystemObjectTypeName = r.TypeName,
            Direction = SyncRuleDirection.Import,
            Enabled = r.Enabled
        }).ToList();
        _csRepo.Setup(r => r.GetSyncRuleHeadersAsync(It.IsAny<int?>(), SyncRuleDirection.Import)).ReturnsAsync(headers);
    }

    private void SetupTrackingSetting(bool enabled) =>
        _settingsRepo.Setup(r => r.GetSettingAsync(Constants.SettingKeys.ChangeTrackingConfigurationChangesEnabled))
            .ReturnsAsync(new ServiceSetting
            {
                Key = Constants.SettingKeys.ChangeTrackingConfigurationChangesEnabled,
                DisplayName = "Track configuration changes",
                ValueType = ServiceSettingValueType.Boolean,
                Value = enabled ? "true" : "false"
            });

    private void SetupHashKeySetting() =>
        _settingsRepo.Setup(r => r.GetSettingAsync(Constants.SettingKeys.ConfigurationChangeHashKey))
            .ReturnsAsync(new ServiceSetting
            {
                Key = Constants.SettingKeys.ConfigurationChangeHashKey,
                DisplayName = "Configuration change hash key",
                ValueType = ServiceSettingValueType.StringEncrypted,
                Value = _protection.Protect(Convert.ToBase64String(HashKey))
            });

    private static ApiKey NewApiKey() => new() { Id = Guid.NewGuid(), Name = "test-key" };

    private static MetaverseObject NewUser() => new() { Id = Guid.NewGuid(), CachedDisplayName = "Admin User" };

    /// <summary>A round-trip credential-protection test double using a recognisable encrypted-value prefix.</summary>
    private sealed class FakeProtection : ICredentialProtectionService
    {
        private const string Prefix = "$JIM$v1$";

        public string? Protect(string? plainText) =>
            string.IsNullOrEmpty(plainText) ? plainText : Prefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(plainText));

        public string? Unprotect(string? protectedData) =>
            string.IsNullOrEmpty(protectedData) || !IsProtected(protectedData)
                ? protectedData
                : Encoding.UTF8.GetString(Convert.FromBase64String(protectedData[Prefix.Length..]));

        public bool IsProtected(string? value) =>
            !string.IsNullOrEmpty(value) && value.StartsWith(Prefix, StringComparison.Ordinal);
    }
}
