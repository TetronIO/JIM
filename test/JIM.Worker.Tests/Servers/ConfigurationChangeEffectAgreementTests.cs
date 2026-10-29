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
using JIM.Models.Enums;
using JIM.Models.Logic;
using JIM.Models.Preview;
using JIM.Models.Search;
using JIM.Models.Security;
using JIM.Models.Staging;
using JIM.Models.Utility;
using Moq;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// The acknowledgement before a Synchronisation Rule save and the save itself must agree about what the save does to
/// existing objects (#2615): whether it records the rule's Connected System Object Type as awaiting synchronisation, and
/// whether it flags Metaverse Objects for export review. The dialog's promise is worked out by
/// <see cref="ConfigurationChangePreflightService"/> before the save and the save's behaviour is whatever the save then
/// does, so these tests run both over each scenario and compare them. Every branch of
/// <see cref="ConfigurationChangeSynchronisationPolicy"/> that decides one of the two is a scenario here.
/// </summary>
[TestFixture]
public class ConfigurationChangeEffectAgreementTests
{
    private const int SyncRuleId = 55;
    private const int ConnectedSystemId = 3;
    private const int ObjectTypeId = 7;
    private const int MetaverseObjectTypeId = 1;
    private static readonly byte[] HashKey = new byte[32];

    private Mock<IRepository> _repo = null!;
    private Mock<IActivityRepository> _activityRepo = null!;
    private Mock<IServiceSettingsRepository> _settingsRepo = null!;
    private Mock<IConnectedSystemRepository> _csRepo = null!;
    private Mock<ISyncRepository> _syncRepo = null!;
    private FakeProtection _protection = null!;
    private JimApplication _jim = null!;
    private List<ConfigurationChangeSynchronisation> _recorded = null!;
    private List<int> _flaggedMetaverseObjectTypeIds = null!;

    [SetUp]
    public void SetUp()
    {
        TestUtilities.SetEnvironmentVariables();

        _recorded = [];
        _flaggedMetaverseObjectTypeIds = [];

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

        _activityRepo.Setup(r => r.CreateActivityAsync(It.IsAny<Activity>()))
            .Callback<Activity>(a =>
            {
                if (a.Id == Guid.Empty)
                    a.Id = Guid.NewGuid();
            })
            .Returns(Task.CompletedTask);
        _activityRepo.Setup(r => r.UpdateActivityAsync(It.IsAny<Activity>())).Returns(Task.CompletedTask);
        _activityRepo.Setup(r => r.GetMaxConfigurationChangeVersionAsync(It.IsAny<ActivityTargetType>(), It.IsAny<int>())).ReturnsAsync(6);

        _csRepo.Setup(r => r.CreateConfigurationChangeSynchronisationAsync(It.IsAny<ConfigurationChangeSynchronisation>()))
            .Callback<ConfigurationChangeSynchronisation>(c => _recorded.Add(c))
            .Returns(Task.CompletedTask);
        _csRepo.Setup(r => r.UpdateSyncRuleAsync(It.IsAny<SyncRule>())).Returns(Task.CompletedTask);
        _csRepo.Setup(r => r.CreateSyncRuleAsync(It.IsAny<SyncRule>())).Returns(Task.CompletedTask);
        _csRepo.Setup(r => r.GetSyncRuleMappingsAsync(It.IsAny<int>())).ReturnsAsync(new List<SyncRuleMapping>());
        _csRepo.Setup(r => r.GetImportSyncRulesForMetaverseObjectTypeAsync(It.IsAny<int>())).ReturnsAsync(() => []);
        _csRepo.Setup(r => r.GetImportMappingTargetMetaverseAttributesAsync(It.IsAny<int>())).ReturnsAsync(new Dictionary<int, int>());
        _csRepo.Setup(r => r.GetImportSyncRuleMappingsForMetaverseAttributeAsync(It.IsAny<int>(), It.IsAny<int>())).ReturnsAsync(new List<SyncRuleMapping>());

        _syncRepo = TestUtilities.StubQueuedChangeWithdrawal(new Mock<ISyncRepository>());
        _syncRepo.Setup(r => r.FlagMetaverseObjectsOfTypeForScopeReviewAsync(It.IsAny<int>()))
            .Callback<int>(typeId => _flaggedMetaverseObjectTypeIds.Add(typeId))
            .ReturnsAsync(0);

        _protection = new FakeProtection();
        _jim = new JimApplication(_repo.Object, syncRepository: _syncRepo.Object) { CredentialProtection = _protection };

        SetupTrackingSetting(enabled: true);
        SetupHashKeySetting();
    }

    [TearDown]
    public void TearDown() => _jim?.Dispose();

    // -- the scenarios: one per branch of the decisions the save makes -------------------------------------------------

    public static IEnumerable<TestCaseData> Scenarios()
    {
        // Import rules: ConfigurationChangeSynchronisationPolicy.RecordsAwaitingSynchronisation.
        yield return Scenario("import: a consequential edit to a rule that is on",
            stored: () => ImportRule(), edited: () => ImportRule(r => r.ProjectToMetaverse = false), records: true);
        yield return Scenario("import: a rename",
            stored: () => ImportRule(), edited: () => ImportRule(r => r.Name = "HR Import (renamed)"), records: false);
        yield return Scenario("import: saved unchanged",
            stored: () => ImportRule(), edited: () => ImportRule(), records: false);
        yield return Scenario("import: switched off",
            stored: () => ImportRule(), edited: () => ImportRule(r => r.Enabled = false), records: true);
        yield return Scenario("import: switched on",
            stored: () => ImportRule(r => r.Enabled = false), edited: () => ImportRule(), records: true);
        yield return Scenario("import: edited while it stays off",
            stored: () => ImportRule(r => { r.Enabled = false; r.ProjectToMetaverse = true; }),
            edited: () => ImportRule(r => { r.Enabled = false; r.ProjectToMetaverse = false; }), records: false);
        yield return Scenario("import: created on",
            stored: null, edited: () => ImportRule(r => r.Id = 0), records: true);
        yield return Scenario("import: created off",
            stored: null, edited: () => ImportRule(r => { r.Id = 0; r.Enabled = false; }), records: false);
        yield return Scenario("import: change tracking off",
            stored: () => ImportRule(), edited: () => ImportRule(r => r.ProjectToMetaverse = false), records: true, tracking: false);
        yield return Scenario("import: change tracking off, rule off",
            stored: () => ImportRule(r => r.Enabled = false), edited: () => ImportRule(r => r.Enabled = false), records: true, tracking: false);
        yield return Scenario("import: no earlier version, rule on",
            stored: () => ImportRule(), edited: () => ImportRule(r => r.ProjectToMetaverse = false), records: true, baseline: false);
        yield return Scenario("import: no earlier version, rule off",
            stored: () => ImportRule(r => r.Enabled = false), edited: () => ImportRule(r => r.Enabled = false), records: false, baseline: false);

        // Export rules: ConfigurationChangeSynchronisationPolicy.FlagsExportReview. An export rule never records.
        yield return Scenario("export: Scoping Criteria changed",
            stored: () => ExportRule(), edited: () => ExportRule(r => r.ObjectScopingCriteriaGroups.Add(EmployeesOnly())), records: false, flags: true);
        yield return Scenario("export: provisioning switched on",
            stored: () => ExportRule(r => r.ProvisionToConnectedSystem = false), edited: () => ExportRule(), records: false, flags: true);
        yield return Scenario("export: Deprovisioning Action changed",
            stored: () => ExportRule(), edited: () => ExportRule(r => r.OutboundDeprovisionAction = OutboundDeprovisionAction.Delete), records: false, flags: true);
        yield return Scenario("export: switched on",
            stored: () => ExportRule(r => r.Enabled = false), edited: () => ExportRule(), records: false, flags: true);
        yield return Scenario("export: switched off",
            stored: () => ExportRule(), edited: () => ExportRule(r => r.Enabled = false), records: false, flags: false);
        yield return Scenario("export: provisioning switched off",
            stored: () => ExportRule(), edited: () => ExportRule(r => r.ProvisionToConnectedSystem = false), records: false, flags: false);
        yield return Scenario("export: a rename",
            stored: () => ExportRule(), edited: () => ExportRule(r => r.Name = "AD Export (renamed)"), records: false, flags: false);
        yield return Scenario("export: Enforce state changed",
            stored: () => ExportRule(), edited: () => ExportRule(r => r.EnforceState = !r.EnforceState), records: false, flags: false);
        yield return Scenario("export: created on",
            stored: null, edited: () => ExportRule(r => r.Id = 0), records: false, flags: true);
        yield return Scenario("export: created off",
            stored: null, edited: () => ExportRule(r => { r.Id = 0; r.Enabled = false; }), records: false, flags: false);
    }

    [TestCaseSource(nameof(Scenarios))]
    public async Task Save_IsPromisedByTheAcknowledgementBeforeItAsync(
        string scenario, Func<SyncRule>? stored, Func<SyncRule> edited, bool records, bool flags, bool tracking, bool baseline)
    {
        SetupTrackingSetting(tracking);
        if (stored != null)
            SetupStoredRule(stored(), withBaseline: baseline);
        var rule = edited();

        // What the acknowledgement promises, asked the way the portal asks it, of the rule as it is about to be saved.
        var preflight = await _jim.ConfigurationChangePreflight.EvaluateSyncRuleAsync(rule);
        // What the save does.
        await _jim.ConnectedSystems.CreateOrUpdateSyncRuleAsync(rule, NewUser());

        var effect = preflight.Effect;
        Assert.That(effect, Is.Not.Null, $"{scenario}: a Synchronisation Rule evaluation always states its effect");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(effect!.RecordsAwaitingSynchronisation, Is.EqualTo(_recorded.Count > 0), $"{scenario}: records awaiting synchronisation");
            Assert.That(effect.FlagsExportReview, Is.EqualTo(_flaggedMetaverseObjectTypeIds.Count > 0), $"{scenario}: flags export review");

            // And the decision itself is the one the policy gives, so a branch cannot move in both places at once.
            Assert.That(effect.RecordsAwaitingSynchronisation, Is.EqualTo(records), $"{scenario}: expected to record");
            Assert.That(effect.FlagsExportReview, Is.EqualTo(flags), $"{scenario}: expected to flag");
        }

        if (effect.RecordsAwaitingSynchronisation)
        {
            var promised = effect.ObjectTypesProcessedInFull.Single();
            using (Assert.EnterMultipleScope())
            {
                Assert.That(promised.ConnectedSystemId, Is.EqualTo(_recorded.Single().ConnectedSystemId), $"{scenario}: the Connected System");
                Assert.That(promised.ConnectedSystemObjectTypeId, Is.EqualTo(_recorded.Single().ConnectedSystemObjectTypeId), $"{scenario}: the Object Type");
            }
        }

        if (effect.FlagsExportReview)
            Assert.That(_flaggedMetaverseObjectTypeIds.Single(), Is.EqualTo(MetaverseObjectTypeId), $"{scenario}: the Metaverse Object Type flagged");
    }

    // -- a failure reading the baseline degrades the acknowledgement as it degrades the capture ----------------------------

    /// <summary>A database failure of the kind the capture and the acknowledgement both treat as a failed read.</summary>
    private sealed class FakeDatabaseException(string message) : System.Data.Common.DbException(message);

    public static IEnumerable<TestCaseData> BaselineReadFailures()
    {
        yield return new TestCaseData(new InvalidOperationException("the baseline cannot be read")) { TestName = "InvalidOperationException" };
        yield return new TestCaseData(new FakeDatabaseException("the database refused the read")) { TestName = "DbException" };
        yield return new TestCaseData(new System.Text.Json.JsonException("the stored snapshot is not valid")) { TestName = "JsonException" };
    }

    [TestCaseSource(nameof(BaselineReadFailures))]
    public async Task Preflight_BaselineReadThatFails_StatesTheFailSafeEffectTheSaveThenRecordsAsync(Exception failure)
    {
        SetupStoredRule(ImportRule());
        _activityRepo.Setup(r => r.GetLatestConfigurationChangeSnapshotAsync(ActivityTargetType.SynchronisationRule, SyncRuleId)).ThrowsAsync(failure);
        var edited = ImportRule(r => r.ProjectToMetaverse = false);

        var preflight = await _jim.ConfigurationChangePreflight.EvaluateSyncRuleAsync(edited);
        await _jim.ConnectedSystems.CreateOrUpdateSyncRuleAsync(edited, NewUser());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(preflight.BaselineUnavailable, Is.True, "nothing can be said about what changed");
            Assert.That(preflight.Effect, Is.Not.Null, "the effect is still stated");
            Assert.That(preflight.Effect!.RecordsAwaitingSynchronisation, Is.True, "a capture that failed records, fail-safe");
            Assert.That(_recorded, Has.Count.EqualTo(1), "and so does the save");
            Assert.That(preflight.Effect.ObjectTypesProcessedInFull.Single().ConnectedSystemObjectTypeId,
                Is.EqualTo(_recorded.Single().ConnectedSystemObjectTypeId), "for the same Object Type");
        }
    }

    [Test]
    public async Task Preflight_BaselineReadThatFailsForAnExportRule_StillStatesTheReviewTheSaveFlagsAsync()
    {
        SetupStoredRule(ExportRule());
        _activityRepo.Setup(r => r.GetLatestConfigurationChangeSnapshotAsync(ActivityTargetType.SynchronisationRule, SyncRuleId))
            .ThrowsAsync(new InvalidOperationException("the baseline cannot be read"));
        var edited = ExportRule(r => r.ObjectScopingCriteriaGroups.Add(EmployeesOnly()));

        var preflight = await _jim.ConfigurationChangePreflight.EvaluateSyncRuleAsync(edited);
        await _jim.ConnectedSystems.CreateOrUpdateSyncRuleAsync(edited, NewUser());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(preflight.Effect!.FlagsExportReview, Is.True);
            Assert.That(_flaggedMetaverseObjectTypeIds, Has.Count.EqualTo(1), "the save flags the same review");
        }
    }

    [Test]
    public async Task Preflight_ReadOfTheTestCasesThatFails_DegradesAsTheCaptureDoesAsync()
    {
        // The proposed state records the rule's saved test cases, which the preflight reads before building it; the capture
        // reads them inside the same guarded block, so a failure there is the same failed capture.
        SetupStoredRule(ImportRule());
        var failingTestCases = new Mock<IConfigurationTestRepository>();
        failingTestCases.Setup(r => r.GetTestCasesForSyncRuleAsync(It.IsAny<int>())).ThrowsAsync(new InvalidOperationException("the test cases cannot be read"));
        _repo.Setup(r => r.ConfigurationTests).Returns(failingTestCases.Object);

        var preflight = await _jim.ConfigurationChangePreflight.EvaluateSyncRuleAsync(ImportRule(r => r.ProjectToMetaverse = false));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(preflight.BaselineUnavailable, Is.True);
            Assert.That(preflight.Effect!.RecordsAwaitingSynchronisation, Is.True);
        }
    }

    [Test]
    public async Task Preflight_EstimateThatCannotBeRead_StatesNothingRatherThanFailingTheDialogAsync()
    {
        SetupStoredRule(ImportRule());
        _csRepo.Setup(r => r.GetConnectedSystemObjectCountAsync(ConnectedSystemId, ObjectTypeId, null))
            .ThrowsAsync(new InvalidOperationException("the count cannot be read"));

        var preflight = await _jim.ConfigurationChangePreflight.EvaluateSyncRuleAsync(ImportRule(r => r.ProjectToMetaverse = false));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(preflight.RequiresAcknowledgement, Is.True, "the change itself is still described");
            Assert.That(preflight.Effect, Is.Null, "no effect is stated, so the dialog shows its general reminder");
        }
    }

    [Test]
    public void Preflight_FailureOfAnUnexpectedKind_IsNotSwallowed()
    {
        SetupStoredRule(ImportRule());
        _activityRepo.Setup(r => r.GetLatestConfigurationChangeSnapshotAsync(ActivityTargetType.SynchronisationRule, SyncRuleId))
            .ThrowsAsync(new ArgumentOutOfRangeException("snapshot", "a bug, not a failed read"));

        Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await _jim.ConfigurationChangePreflight.EvaluateSyncRuleAsync(ImportRule(r => r.ProjectToMetaverse = false)));
    }

    // -- what is left for a Full Synchronisation -----------------------------------------------------------------------

    [Test]
    public async Task Preflight_ImportChangeThatIsRecorded_LeavesNothingForAFullSynchronisationAsync()
    {
        SetupStoredRule(ImportRule());

        var preflight = await _jim.ConfigurationChangePreflight.EvaluateSyncRuleAsync(ImportRule(r => r.ProjectToMetaverse = false));

        Assert.That(preflight.Effect!.NeedsFullSynchronisation, Is.False, "processing the whole type applies everything in the rule");
    }

    [Test]
    public async Task Preflight_ImportRuleEditedWhileItStaysOff_LeavesItForAFullSynchronisationAsync()
    {
        // Not recorded, and a consequential edit all the same: the drift indicator goes on counting it, so the
        // acknowledgement goes on naming a Full Synchronisation for it.
        SetupStoredRule(ImportRule(r => { r.Enabled = false; r.ProjectToMetaverse = true; }));

        var preflight = await _jim.ConfigurationChangePreflight.EvaluateSyncRuleAsync(ImportRule(r => { r.Enabled = false; r.ProjectToMetaverse = false; }));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(preflight.Effect!.RecordsAwaitingSynchronisation, Is.False);
            Assert.That(preflight.Effect.FullSynchronisationSections, Is.Not.Empty);
            Assert.That(preflight.Effect.FullSynchronisationConnectedSystemName, Is.EqualTo("HR"));
        }
    }

    [Test]
    public async Task Preflight_ExportRuleAttributeFlowEdit_LeavesItForAFullSynchronisationAsync()
    {
        SetupStoredRule(ExportRule());

        var preflight = await _jim.ConfigurationChangePreflight.EvaluateSyncRuleAsync(ExportRule(r => r.EnforceState = !r.EnforceState));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(preflight.Effect!.FlagsExportReview, Is.False);
            Assert.That(preflight.Effect.FullSynchronisationSections, Is.EqualTo(new[] { "Enforce state" }));
        }
    }

    [Test]
    public async Task Preflight_ExportRuleScopeChange_IsCarriedByTheReviewAloneAsync()
    {
        SetupStoredRule(ExportRule());

        var preflight = await _jim.ConfigurationChangePreflight.EvaluateSyncRuleAsync(
            ExportRule(r => r.ObjectScopingCriteriaGroups.Add(EmployeesOnly())));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(preflight.Effect!.FlagsExportReview, Is.True);
            Assert.That(preflight.Effect.ExportReviewMetaverseObjectTypeName, Is.EqualTo("Person"));
            Assert.That(preflight.Effect.NeedsFullSynchronisation, Is.False, "the review reviews every object against the rule as it now stands");
        }
    }

    [Test]
    public async Task Preflight_ExportRuleScopeChangeAndAnAttributeFlowEdit_NeedsBothAsync()
    {
        SetupStoredRule(ExportRule());

        var preflight = await _jim.ConfigurationChangePreflight.EvaluateSyncRuleAsync(ExportRule(r =>
        {
            r.ObjectScopingCriteriaGroups.Add(EmployeesOnly());
            r.EnforceState = !r.EnforceState;
        }));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(preflight.Effect!.FlagsExportReview, Is.True);
            Assert.That(preflight.Effect.FullSynchronisationSections, Is.EqualTo(new[] { "Enforce state" }), "only what the review does not carry");
        }
    }

    [Test]
    public async Task Preflight_RecordedImportChange_EstimatesWhatTheRunWillCostAsync()
    {
        SetupStoredRule(ImportRule());
        _csRepo.Setup(r => r.GetConnectedSystemObjectCountAsync(ConnectedSystemId, ObjectTypeId, null)).ReturnsAsync(30_000);
        _csRepo.Setup(r => r.GetConnectedSystemObjectCountAsync(ConnectedSystemId, null)).ReturnsAsync(100_000);
        _activityRepo.Setup(r => r.GetLatestCompletedFullSynchronisationAsync(ConnectedSystemId))
            .ReturnsAsync(new Activity { ExecutionTime = TimeSpan.FromMinutes(50) });

        var preflight = await _jim.ConfigurationChangePreflight.EvaluateSyncRuleAsync(ImportRule(r => r.ProjectToMetaverse = false));

        var estimate = preflight.Effect!.ObjectTypesProcessedInFull.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(estimate.ConnectedSystemName, Is.EqualTo("HR"));
            Assert.That(estimate.ObjectTypeName, Is.EqualTo("user"));
            Assert.That(estimate.ObjectCount, Is.EqualTo(30_000));
            // 100,000 objects in 50 minutes is 2,000 a minute, so 30,000 take 15 minutes.
            Assert.That(estimate.EstimatedDuration, Is.EqualTo(TimeSpan.FromMinutes(15)));
        }
    }

    [Test]
    public async Task Preflight_RecordedImportChangeOnASystemThatHasNeverFullySynchronised_HasNoDurationAsync()
    {
        SetupStoredRule(ImportRule());
        _csRepo.Setup(r => r.GetConnectedSystemObjectCountAsync(ConnectedSystemId, ObjectTypeId, null)).ReturnsAsync(30_000);

        var preflight = await _jim.ConfigurationChangePreflight.EvaluateSyncRuleAsync(ImportRule(r => r.ProjectToMetaverse = false));

        var estimate = preflight.Effect!.ObjectTypesProcessedInFull.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(estimate.ObjectCount, Is.EqualTo(30_000));
            Assert.That(estimate.EstimatedDuration, Is.Null);
        }
    }

    [Test]
    public async Task TakeRecordedAwaitingSynchronisationAsync_AfterASave_ReportsWhatTheSaveRecordedAndOnlyOnceAsync()
    {
        SetupStoredRule(ImportRule());
        _csRepo.Setup(r => r.GetConnectedSystemObjectCountAsync(ConnectedSystemId, ObjectTypeId, null)).ReturnsAsync(1_250);

        await _jim.ConnectedSystems.CreateOrUpdateSyncRuleAsync(ImportRule(r => r.ProjectToMetaverse = false), NewUser());
        var first = await _jim.ConnectedSystems.TakeRecordedAwaitingSynchronisationAsync();
        var second = await _jim.ConnectedSystems.TakeRecordedAwaitingSynchronisationAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(first, Has.Count.EqualTo(1));
            Assert.That(first[0].ConnectedSystemId, Is.EqualTo(ConnectedSystemId));
            Assert.That(first[0].ConnectedSystemName, Is.EqualTo("HR"));
            Assert.That(first[0].ConnectedSystemObjectTypeId, Is.EqualTo(ObjectTypeId));
            Assert.That(first[0].ObjectTypeName, Is.EqualTo("user"));
            Assert.That(first[0].ObjectCount, Is.EqualTo(1_250));
            Assert.That(second, Is.Empty, "a response reports only its own save");
        }
    }

    [Test]
    public async Task TakeRecordedAwaitingSynchronisationAsync_SaveThatRecordsNothing_ReportsNothingAsync()
    {
        SetupStoredRule(ImportRule());

        await _jim.ConnectedSystems.CreateOrUpdateSyncRuleAsync(ImportRule(r => r.Name = "HR Import (renamed)"), NewUser());

        Assert.That(await _jim.ConnectedSystems.TakeRecordedAwaitingSynchronisationAsync(), Is.Empty);
    }

    // -- helpers -------------------------------------------------------------------------------------------------------

    private static TestCaseData Scenario(string name, Func<SyncRule>? stored, Func<SyncRule> edited, bool records, bool flags = false,
        bool tracking = true, bool baseline = true) =>
        new TestCaseData(name, stored, edited, records, flags, tracking, baseline) { TestName = $"Save_IsPromisedByTheAcknowledgementBeforeItAsync({name})" };

    private void SetupStoredRule(SyncRule stored, bool withBaseline = true)
    {
        if (withBaseline)
            _activityRepo.Setup(r => r.GetLatestConfigurationChangeSnapshotAsync(ActivityTargetType.SynchronisationRule, stored.Id))
                .ReturnsAsync(ConfigurationSnapshotService.Serialise(_jim.ConfigurationSnapshots.CreateSnapshot(stored, HashKey)));

        _csRepo.Setup(r => r.GetSyncRuleScopeStateAsync(stored.Id)).ReturnsAsync(new SyncRuleScopeState(
            stored.Enabled, stored.ProvisionToConnectedSystem == true, SyncRuleScopingProposal.FromCurrentScope(stored), stored.OutboundDeprovisionAction));

        var reloaded = stored.Direction == SyncRuleDirection.Import ? ImportRule() : ExportRule();
        reloaded.Id = stored.Id;
        reloaded.Enabled = stored.Enabled;
        _csRepo.Setup(r => r.GetSyncRuleAsync(stored.Id)).ReturnsAsync(reloaded);
    }

    private static SyncRule ImportRule(Action<SyncRule>? edit = null)
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
            MetaverseObjectType = new MetaverseObjectType { Id = MetaverseObjectTypeId, Name = "Person" },
            MetaverseObjectTypeId = MetaverseObjectTypeId
        };
        edit?.Invoke(rule);
        return rule;
    }

    private static SyncRule ExportRule(Action<SyncRule>? edit = null)
    {
        var rule = ImportRule();
        rule.Name = "AD Export";
        rule.Direction = SyncRuleDirection.Export;
        rule.ProjectToMetaverse = null;
        rule.ProvisionToConnectedSystem = true;
        edit?.Invoke(rule);
        return rule;
    }

    private static SyncRuleScopingCriteriaGroup EmployeesOnly() => new()
    {
        Type = SearchGroupType.All,
        Criteria =
        [
            new SyncRuleScopingCriteria
            {
                MetaverseAttribute = new MetaverseAttribute { Id = 12, Name = "Job Title", Type = AttributeDataType.Text },
                MetaverseAttributeId = 12,
                ComparisonType = SearchComparisonType.Equals,
                StringValue = "Employee",
                CaseSensitive = true
            }
        ]
    };

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
