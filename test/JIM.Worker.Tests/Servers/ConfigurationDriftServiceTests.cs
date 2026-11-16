// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Application.Services;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Activities.DTOs;
using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Staging.DTOs;
using Moq;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// Tests for <see cref="JIM.Application.Services.ConfigurationDriftService"/>: whether a Connected System's
/// configuration has changed in a way that needs a Full Synchronisation to take effect.
///
/// The behaviour that matters to an administrator is that the indicator is trustworthy in both directions. It must
/// stay silent for changes that cannot alter synchronisation outcomes (or it becomes noise they learn to ignore), and
/// it must never claim a settled configuration when JIM cannot actually tell.
/// </summary>
[TestFixture]
public class ConfigurationDriftServiceTests
{
    private Mock<IRepository> _repo = null!;
    private Mock<IActivityRepository> _activityRepo = null!;
    private Mock<IConnectedSystemRepository> _connectedSystemRepo = null!;
    private Mock<IServiceSettingsRepository> _serviceSettingsRepo = null!;
    private JimApplication _jim = null!;

    private const int SystemId = 7;
    private const int OtherSystemId = 8;
    private static readonly DateTime LastFullSync = new(2026, 7, 20, 9, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime AfterSync = LastFullSync.AddHours(3);
    private static readonly DateTime BeforeSync = LastFullSync.AddHours(-3);

    [SetUp]
    public void SetUp()
    {
        TestUtilities.SetEnvironmentVariables();
        _repo = new Mock<IRepository>();
        _activityRepo = new Mock<IActivityRepository>();
        _connectedSystemRepo = new Mock<IConnectedSystemRepository>();
        _serviceSettingsRepo = new Mock<IServiceSettingsRepository>();
        _repo.Setup(r => r.Activity).Returns(_activityRepo.Object);
        _repo.Setup(r => r.ConnectedSystems).Returns(_connectedSystemRepo.Object);
        _repo.Setup(r => r.ServiceSettings).Returns(_serviceSettingsRepo.Object);

        // Change tracking on by default; the disabled case sets this explicitly.
        SetChangeTrackingEnabled(true);

        // Sensible defaults: the system was fully synchronised, has one rule, and nothing has changed since.
        _activityRepo.Setup(r => r.GetLastFullSynchronisationStartsAsync(It.IsAny<IList<int>>()))
            .ReturnsAsync(new Dictionary<int, DateTime> { [SystemId] = LastFullSync });
        _activityRepo.Setup(r => r.GetConfigurationChangeImpactsSinceAsync(It.IsAny<DateTime>(), It.IsAny<ConfigurationChangeClass>()))
            .ReturnsAsync([]);
        _connectedSystemRepo.Setup(r => r.GetConfigurationScopesAsync(It.IsAny<IList<int>>()))
            .ReturnsAsync([Scope(SystemId, syncRuleIds: [100], objectTypeIds: [20], attributeIds: [30])]);
        _connectedSystemRepo.Setup(r => r.GetConfigurationChangesAwaitingSynchronisationAsync(It.IsAny<IList<int>>()))
            .ReturnsAsync([]);
        _connectedSystemRepo.Setup(r => r.GetConfigurationChangeSynchronisationSummariesAwaitingAsync(It.IsAny<int>(), It.IsAny<int>()))
            .ReturnsAsync([]);

        _jim = new JimApplication(_repo.Object);
    }

    [TearDown]
    public void TearDown() => _jim?.Dispose();

    [Test]
    public async Task GetConnectedSystemDriftAsync_NoChangesSinceLastFullSynchronisation_ReportsNoPendingChangesAsync()
    {
        var result = await _jim.ConfigurationDrift.GetConnectedSystemDriftAsync(SystemId);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.HasPendingChanges, Is.False);
            Assert.That(result.IsDeterminable, Is.True);
            Assert.That(result.ChangeCount, Is.EqualTo(0));
            Assert.That(result.LastFullSynchronisation, Is.EqualTo(LastFullSync));
            Assert.That(result.HighestChangeClass, Is.EqualTo(ConfigurationChangeClass.NotClassified));
        }
    }

    [Test]
    public async Task GetConnectedSystemDriftAsync_SyncAffectingChangeToTheSystem_ReportsPendingChangesAsync()
    {
        SetImpacts(Impact(AfterSync, ConfigurationChangeClass.SyncAffecting, connectedSystemId: SystemId));

        var result = await _jim.ConfigurationDrift.GetConnectedSystemDriftAsync(SystemId);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.HasPendingChanges, Is.True);
            Assert.That(result.ChangeCount, Is.EqualTo(1));
            Assert.That(result.MostRecentChange, Is.EqualTo(AfterSync));
            Assert.That(result.HighestChangeClass, Is.EqualTo(ConfigurationChangeClass.SyncAffecting));
        }
    }

    [Test]
    public async Task GetConnectedSystemDriftAsync_ChangeRecordedBeforeTheLastFullSynchronisation_IsNotPendingAsync()
    {
        // The reference point is what makes the indicator meaningful: a change the last run already applied must not
        // keep prompting for another.
        SetImpacts(Impact(BeforeSync, ConfigurationChangeClass.Destructive, connectedSystemId: SystemId));

        var result = await _jim.ConfigurationDrift.GetConnectedSystemDriftAsync(SystemId);

        Assert.That(result.HasPendingChanges, Is.False);
    }

    [Test]
    public async Task GetConnectedSystemDriftAsync_DestructiveChange_ReportsHighestClassAsDestructiveAsync()
    {
        // The highest class drives how loudly the surface warns, so a destructive change alongside a sync-affecting
        // one must not be softened into the lesser of the two.
        SetImpacts(
            Impact(AfterSync, ConfigurationChangeClass.SyncAffecting, connectedSystemId: SystemId),
            Impact(AfterSync.AddMinutes(10), ConfigurationChangeClass.Destructive, connectedSystemId: SystemId));

        var result = await _jim.ConfigurationDrift.GetConnectedSystemDriftAsync(SystemId);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.ChangeCount, Is.EqualTo(2));
            Assert.That(result.HighestChangeClass, Is.EqualTo(ConfigurationChangeClass.Destructive));
            Assert.That(result.MostRecentChange, Is.EqualTo(AfterSync.AddMinutes(10)));
        }
    }

    [Test]
    public async Task GetConnectedSystemDriftAsync_ChangeToOneOfItsSynchronisationRules_ReportsPendingChangesAsync()
    {
        SetImpacts(Impact(AfterSync, ConfigurationChangeClass.SyncAffecting, syncRuleId: 100));

        var result = await _jim.ConfigurationDrift.GetConnectedSystemDriftAsync(SystemId);

        Assert.That(result.HasPendingChanges, Is.True);
    }

    [Test]
    public async Task GetConnectedSystemDriftAsync_ChangeToAnotherSystemsSynchronisationRule_IsNotPendingAsync()
    {
        SetImpacts(Impact(AfterSync, ConfigurationChangeClass.Destructive, syncRuleId: 999));

        var result = await _jim.ConfigurationDrift.GetConnectedSystemDriftAsync(SystemId);

        Assert.That(result.HasPendingChanges, Is.False);
    }

    [Test]
    public async Task GetConnectedSystemDriftAsync_MetaverseAttributeReferencedByItsRules_ReportsPendingChangesAsync()
    {
        SetImpacts(Impact(AfterSync, ConfigurationChangeClass.SyncAffecting, metaverseAttributeId: 30));

        var result = await _jim.ConfigurationDrift.GetConnectedSystemDriftAsync(SystemId);

        Assert.That(result.HasPendingChanges, Is.True);
    }

    [Test]
    public async Task GetConnectedSystemDriftAsync_MetaverseAttributeItsRulesDoNotReference_IsNotPendingAsync()
    {
        // The point of precise attribution: editing an attribute no rule of this system touches must leave it alone.
        SetImpacts(Impact(AfterSync, ConfigurationChangeClass.SyncAffecting, metaverseAttributeId: 31));

        var result = await _jim.ConfigurationDrift.GetConnectedSystemDriftAsync(SystemId);

        Assert.That(result.HasPendingChanges, Is.False);
    }

    [Test]
    public async Task GetConnectedSystemDriftAsync_MetaverseObjectTypeTargetedByItsRules_ReportsPendingChangesAsync()
    {
        SetImpacts(Impact(AfterSync, ConfigurationChangeClass.Destructive, metaverseObjectTypeId: 20));

        var result = await _jim.ConfigurationDrift.GetConnectedSystemDriftAsync(SystemId);

        Assert.That(result.HasPendingChanges, Is.True);
    }

    [Test]
    public async Task GetConnectedSystemDriftAsync_MetaverseObjectTypeItsRulesDoNotTarget_IsNotPendingAsync()
    {
        SetImpacts(Impact(AfterSync, ConfigurationChangeClass.Destructive, metaverseObjectTypeId: 21));

        var result = await _jim.ConfigurationDrift.GetConnectedSystemDriftAsync(SystemId);

        Assert.That(result.HasPendingChanges, Is.False);
    }

    [Test]
    public async Task GetConnectedSystemDriftAsync_SyncAffectingServiceSetting_AffectsEverySystemAsync()
    {
        // Service Settings are global, so there is no scope to match against; every system must pick them up.
        SetImpacts(Impact(AfterSync, ConfigurationChangeClass.SyncAffecting,
            serviceSettingKey: Constants.SettingKeys.ChangeTrackingConfigurationChangesEnabled));

        var result = await _jim.ConfigurationDrift.GetConnectedSystemDriftAsync(SystemId);

        Assert.That(result.HasPendingChanges, Is.True);
    }

    [Test]
    public async Task GetConnectedSystemDriftAsync_DeletedSynchronisationRule_ReportsPendingChangesOnItsSystemAsync()
    {
        // A rule deletion records no SyncRuleId (the rule is gone), so the Connected System id is the only link back.
        // Without it this change would be invisible: a false negative on one of the most consequential edits there is.
        SetImpacts(Impact(AfterSync, ConfigurationChangeClass.Destructive, connectedSystemId: SystemId));

        var result = await _jim.ConfigurationDrift.GetConnectedSystemDriftAsync(SystemId);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.HasPendingChanges, Is.True);
            Assert.That(result.HighestChangeClass, Is.EqualTo(ConfigurationChangeClass.Destructive));
        }
    }

    [Test]
    public async Task GetConnectedSystemDriftAsync_NeverFullySynchronised_ReportsNeverRatherThanPendingAsync()
    {
        _activityRepo.Setup(r => r.GetLastFullSynchronisationStartsAsync(It.IsAny<IList<int>>()))
            .ReturnsAsync(new Dictionary<int, DateTime>());

        var result = await _jim.ConfigurationDrift.GetConnectedSystemDriftAsync(SystemId);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.NeverFullySynchronised, Is.True);
            Assert.That(result.HasPendingChanges, Is.False);
            Assert.That(result.IsDeterminable, Is.False);
            Assert.That(result.LastFullSynchronisation, Is.Null);
        }
    }

    [Test]
    public async Task GetConnectedSystemDriftAsync_ChangeTrackingDisabled_ReportsUndeterminableRatherThanCleanAsync()
    {
        SetChangeTrackingEnabled(false);

        var result = await _jim.ConfigurationDrift.GetConnectedSystemDriftAsync(SystemId);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.TrackingDisabled, Is.True);
            Assert.That(result.HasPendingChanges, Is.False);
            Assert.That(result.IsDeterminable, Is.False);
        }
    }

    [Test]
    public async Task GetConnectedSystemDriftAsync_BatchOfSystems_EvaluatesEachAgainstItsOwnReferencePointAsync()
    {
        // The two systems were last fully synchronised at different times, and the change falls between them: pending
        // for the one that synchronised earlier, already applied for the one that synchronised later.
        var laterSync = AfterSync.AddHours(1);
        _activityRepo.Setup(r => r.GetLastFullSynchronisationStartsAsync(It.IsAny<IList<int>>()))
            .ReturnsAsync(new Dictionary<int, DateTime> { [SystemId] = LastFullSync, [OtherSystemId] = laterSync });
        _connectedSystemRepo.Setup(r => r.GetConfigurationScopesAsync(It.IsAny<IList<int>>()))
            .ReturnsAsync([Scope(SystemId), Scope(OtherSystemId)]);
        SetImpacts(
            Impact(AfterSync, ConfigurationChangeClass.SyncAffecting, connectedSystemId: SystemId),
            Impact(AfterSync, ConfigurationChangeClass.SyncAffecting, connectedSystemId: OtherSystemId));

        var results = await _jim.ConfigurationDrift.GetConnectedSystemDriftAsync([SystemId, OtherSystemId]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(results[SystemId].HasPendingChanges, Is.True);
            Assert.That(results[OtherSystemId].HasPendingChanges, Is.False);
        }
    }

    [Test]
    public async Task GetConnectedSystemDriftAsync_BatchOfSystems_QueriesChangesOnceRegardlessOfSystemCountAsync()
    {
        // Guards the list surface against an N+1: the query count must not scale with the number of systems.
        _activityRepo.Setup(r => r.GetLastFullSynchronisationStartsAsync(It.IsAny<IList<int>>()))
            .ReturnsAsync(new Dictionary<int, DateTime> { [SystemId] = LastFullSync, [OtherSystemId] = LastFullSync });
        _connectedSystemRepo.Setup(r => r.GetConfigurationScopesAsync(It.IsAny<IList<int>>()))
            .ReturnsAsync([Scope(SystemId), Scope(OtherSystemId)]);

        await _jim.ConfigurationDrift.GetConnectedSystemDriftAsync([SystemId, OtherSystemId]);

        _activityRepo.Verify(r => r.GetConfigurationChangeImpactsSinceAsync(It.IsAny<DateTime>(), It.IsAny<ConfigurationChangeClass>()), Times.Once);
        _connectedSystemRepo.Verify(r => r.GetConfigurationScopesAsync(It.IsAny<IList<int>>()), Times.Once);
    }

    [Test]
    public async Task GetConnectedSystemDriftAsync_OnlyRequestsSyncAffectingAndAboveAsync()
    {
        // Cosmetic changes are excluded at the query, so a rename can never raise the indicator.
        await _jim.ConfigurationDrift.GetConnectedSystemDriftAsync(SystemId);

        _activityRepo.Verify(r => r.GetConfigurationChangeImpactsSinceAsync(
            It.IsAny<DateTime>(), ConfigurationChangeClass.SyncAffecting), Times.Once);
    }

    [Test]
    public async Task GetConnectedSystemDriftAsync_SystemWithNoSynchronisationRules_StillReportsChangesToItselfAsync()
    {
        _connectedSystemRepo.Setup(r => r.GetConfigurationScopesAsync(It.IsAny<IList<int>>()))
            .ReturnsAsync([Scope(SystemId)]);
        SetImpacts(Impact(AfterSync, ConfigurationChangeClass.SyncAffecting, connectedSystemId: SystemId));

        var result = await _jim.ConfigurationDrift.GetConnectedSystemDriftAsync(SystemId);

        Assert.That(result.HasPendingChanges, Is.True);
    }

    [Test]
    public async Task GetConnectedSystemDriftAsync_NoSystemIds_ReturnsEmptyWithoutQueryingAsync()
    {
        var results = await _jim.ConfigurationDrift.GetConnectedSystemDriftAsync([]);

        Assert.That(results, Is.Empty);
        _activityRepo.Verify(r => r.GetLastFullSynchronisationStartsAsync(It.IsAny<IList<int>>()), Times.Never);
    }

    #region Changes awaiting synchronisation (#2615)

    [Test]
    public async Task GetConnectedSystemDriftAsync_ChangesAwaitingSynchronisation_AreReportedByObjectTypeAsync()
    {
        // An import rule change is carried to the objects by the system's next synchronisation, so it is not drift to
        // be cleared with a Full Synchronisation. What the administrator is told instead is which Object Types are
        // waiting, how many changes are behind each, and since when.
        SetAwaiting(
            Awaiting(SystemId, 20, "group", changeCount: 1, AfterSync.AddHours(2)),
            Awaiting(SystemId, 21, "user", changeCount: 3, AfterSync),
            Awaiting(OtherSystemId, 22, "user", changeCount: 5, AfterSync));

        var result = await _jim.ConfigurationDrift.GetConnectedSystemDriftAsync(SystemId);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.HasChangesAwaitingSynchronisation, Is.True);
            Assert.That(result.AwaitingSynchronisation.Select(a => a.ObjectTypeName), Is.EqualTo(new[] { "user", "group" }),
                "longest waiting first");
            Assert.That(result.AwaitingSynchronisation.Select(a => a.ChangeCount), Is.EqualTo(new[] { 3, 1 }));
            Assert.That(result.AwaitingSynchronisation[0].EarliestRequestedAt, Is.EqualTo(AfterSync));
            Assert.That(result.AwaitingSynchronisation.Select(a => a.ConnectedSystemId), Has.All.EqualTo(SystemId), "the other system's types are not this one's");
            Assert.That(result.HasPendingChanges, Is.False, "and a change awaiting synchronisation is not drift");
        }
    }

    [Test]
    public async Task GetConnectedSystemDriftAsync_NothingAwaitingSynchronisation_ReportsNoneAsync()
    {
        var result = await _jim.ConfigurationDrift.GetConnectedSystemDriftAsync(SystemId);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.HasChangesAwaitingSynchronisation, Is.False);
            Assert.That(result.AwaitingSynchronisation, Is.Empty);
        }
    }

    [Test]
    public async Task GetConnectedSystemDriftAsync_ChangeTrackingDisabled_StillReportsChangesAwaitingSynchronisationAsync()
    {
        // Changes recorded while tracking was off, or recorded because it was, are real whether or not drift can be told.
        SetChangeTrackingEnabled(false);
        SetAwaiting(Awaiting(SystemId, 21, "user", changeCount: 2, AfterSync));

        var result = await _jim.ConfigurationDrift.GetConnectedSystemDriftAsync(SystemId);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.TrackingDisabled, Is.True);
            Assert.That(result.AwaitingSynchronisation.Select(a => a.ObjectTypeName), Is.EqualTo(new[] { "user" }));
        }
    }

    [Test]
    public async Task GetConnectedSystemDriftAsync_NeverFullySynchronised_StillReportsChangesAwaitingSynchronisationAsync()
    {
        _activityRepo.Setup(r => r.GetLastFullSynchronisationStartsAsync(It.IsAny<IList<int>>()))
            .ReturnsAsync(new Dictionary<int, DateTime>());
        SetAwaiting(Awaiting(SystemId, 21, "user", changeCount: 2, AfterSync));

        var result = await _jim.ConfigurationDrift.GetConnectedSystemDriftAsync(SystemId);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.NeverFullySynchronised, Is.True);
            Assert.That(result.AwaitingSynchronisation.Select(a => a.ObjectTypeName), Is.EqualTo(new[] { "user" }));
        }
    }

    [Test]
    public async Task GetConnectedSystemDriftAsync_BatchOfSystems_ReadsChangesAwaitingSynchronisationOnceAndSplitsThemBySystemAsync()
    {
        // Guards the list surface against an N+1 as the changes query does: one extra read, not one per system.
        _activityRepo.Setup(r => r.GetLastFullSynchronisationStartsAsync(It.IsAny<IList<int>>()))
            .ReturnsAsync(new Dictionary<int, DateTime> { [SystemId] = LastFullSync, [OtherSystemId] = LastFullSync });
        _connectedSystemRepo.Setup(r => r.GetConfigurationScopesAsync(It.IsAny<IList<int>>()))
            .ReturnsAsync([Scope(SystemId), Scope(OtherSystemId)]);
        SetAwaiting(
            Awaiting(SystemId, 21, "user", changeCount: 1, AfterSync),
            Awaiting(OtherSystemId, 22, "person", changeCount: 4, AfterSync));

        var results = await _jim.ConfigurationDrift.GetConnectedSystemDriftAsync([SystemId, OtherSystemId]);

        using (Assert.EnterMultipleScope())
        {
            _connectedSystemRepo.Verify(r => r.GetConfigurationChangesAwaitingSynchronisationAsync(It.IsAny<IList<int>>()), Times.Once);
            Assert.That(results[SystemId].AwaitingSynchronisation.Select(a => a.ObjectTypeName), Is.EqualTo(new[] { "user" }));
            Assert.That(results[OtherSystemId].AwaitingSynchronisation.Select(a => a.ObjectTypeName), Is.EqualTo(new[] { "person" }));
        }
    }

    [Test]
    public async Task GetConnectedSystemDriftAsync_OneSystem_ListsTheChangesBehindEachWaitingTypeAndCountsItsObjectsAsync()
    {
        // The page that says what is waiting also says what the changes are and how many objects the run will process.
        SetAwaiting(
            Awaiting(SystemId, 21, "user", changeCount: 2, AfterSync),
            Awaiting(SystemId, 20, "group", changeCount: 1, AfterSync.AddHours(1)));
        _connectedSystemRepo.Setup(r => r.GetConfigurationChangeSynchronisationSummariesAwaitingAsync(SystemId, ObjectTypeAwaitingSynchronisation.MaxChangesListed))
            .ReturnsAsync(
            [
                Summary(1, 21, "user", "HR Import: Scope changed", AfterSync.AddHours(1)),
                Summary(2, 20, "group", "Groups: Enabled changed", AfterSync.AddHours(1)),
                Summary(3, 21, "user", "HR Import: Project to Metaverse changed", AfterSync)
            ]);
        _connectedSystemRepo.Setup(r => r.GetConnectedSystemObjectCountAsync(SystemId, 21, null)).ReturnsAsync(120_000);
        _connectedSystemRepo.Setup(r => r.GetConnectedSystemObjectCountAsync(SystemId, 20, null)).ReturnsAsync(35);

        var result = await _jim.ConfigurationDrift.GetConnectedSystemDriftAsync(SystemId);

        var user = result.AwaitingSynchronisation.Single(a => a.ObjectTypeName == "user");
        var group = result.AwaitingSynchronisation.Single(a => a.ObjectTypeName == "group");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(user.ObjectCount, Is.EqualTo(120_000));
            Assert.That(user.Changes.Select(c => c.Id), Is.EqualTo(new long[] { 1, 3 }), "this type's changes, newest first as read");
            Assert.That(group.ObjectCount, Is.EqualTo(35));
            Assert.That(group.Changes.Select(c => c.Id), Is.EqualTo(new long[] { 2 }));
            Assert.That(user.ChangeCount, Is.EqualTo(2), "the true count stays the aggregate's");
        }
    }

    [Test]
    public async Task GetConnectedSystemDriftAsync_ABatch_DoesNotListChangesOrCountObjectsAsync()
    {
        // A list of systems needs only the aggregate: the changes and a count per type would cost a read per system.
        _activityRepo.Setup(r => r.GetLastFullSynchronisationStartsAsync(It.IsAny<IList<int>>()))
            .ReturnsAsync(new Dictionary<int, DateTime> { [SystemId] = LastFullSync, [OtherSystemId] = LastFullSync });
        _connectedSystemRepo.Setup(r => r.GetConfigurationScopesAsync(It.IsAny<IList<int>>()))
            .ReturnsAsync([Scope(SystemId), Scope(OtherSystemId)]);
        SetAwaiting(Awaiting(SystemId, 21, "user", changeCount: 2, AfterSync));

        var results = await _jim.ConfigurationDrift.GetConnectedSystemDriftAsync([SystemId, OtherSystemId]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(results[SystemId].AwaitingSynchronisation.Single().Changes, Is.Empty);
            Assert.That(results[SystemId].AwaitingSynchronisation.Single().ObjectCount, Is.Null);
            _connectedSystemRepo.Verify(r => r.GetConfigurationChangeSynchronisationSummariesAwaitingAsync(It.IsAny<int>(), It.IsAny<int>()), Times.Never);
            _connectedSystemRepo.Verify(r => r.GetConnectedSystemObjectCountAsync(It.IsAny<int>(), It.IsAny<int?>(), It.IsAny<int?>()), Times.Never);
        }
    }

    [Test]
    public async Task GetConnectedSystemDriftAsync_OneSystemWithNothingWaiting_ReadsNoChangeDetailAsync()
    {
        await _jim.ConfigurationDrift.GetConnectedSystemDriftAsync(SystemId);

        _connectedSystemRepo.Verify(r => r.GetConfigurationChangeSynchronisationSummariesAwaitingAsync(It.IsAny<int>(), It.IsAny<int>()), Times.Never);
    }

    #endregion

    #region Net difference since the last Full Synchronisation (#2973)

    // The indicator answers "is the configuration in force different, in a way only a Full Synchronisation applies, from
    // the configuration the last one ran with?". Counting edits instead reports a change that was made and then undone,
    // which an administrator can do nothing about.

    [Test]
    public async Task GetConnectedSystemDriftAsync_ObjectTypeChangeThatWasUndone_IsNotPendingAsync()
    {
        // Scenario 001 zeroes the User type's grace period for a test and restores it afterwards: two Destructive edits,
        // and a configuration identical to the one the last Full Synchronisation ran with.
        SetImpacts(
            Impact(AfterSync, ConfigurationChangeClass.Destructive, metaverseObjectTypeId: 20, version: 2),
            Impact(AfterSync.AddMinutes(5), ConfigurationChangeClass.Destructive, metaverseObjectTypeId: 20, version: 3));
        SetObjectTypeHistory(inForceAtLastFullSync: ObjectType(), latest: ObjectType());

        var result = await _jim.ConfigurationDrift.GetConnectedSystemDriftAsync(SystemId);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.HasPendingChanges, Is.False);
            Assert.That(result.ChangeCount, Is.EqualTo(0));
        }
    }

    [Test]
    public async Task GetConnectedSystemDriftAsync_NetChangeOnlyToDeletionSettings_IsNotPendingAsync()
    {
        // Recorded before the save judged it (a null judgement counts), but a Full Synchronisation still has nothing to do.
        SetImpacts(Impact(AfterSync, ConfigurationChangeClass.Destructive, metaverseObjectTypeId: 20, version: 2));
        SetObjectTypeHistory(inForceAtLastFullSync: ObjectType(), latest: ObjectType(t => t.DeletionGracePeriod = TimeSpan.Zero));

        var result = await _jim.ConfigurationDrift.GetConnectedSystemDriftAsync(SystemId);

        Assert.That(result.HasPendingChanges, Is.False);
    }

    [Test]
    public async Task GetConnectedSystemDriftAsync_NetChangeAFullSynchronisationApplies_CountsEveryChangeBehindItAsync()
    {
        SetImpacts(
            Impact(AfterSync, ConfigurationChangeClass.Destructive, metaverseObjectTypeId: 20, version: 2),
            Impact(AfterSync.AddMinutes(5), ConfigurationChangeClass.SyncAffecting, metaverseObjectTypeId: 20, version: 3));
        SetObjectTypeHistory(inForceAtLastFullSync: ObjectType(),
            latest: ObjectType(t => t.Attributes.Add(new MetaverseAttribute { Id = 31, Name = "Employee Type" })));

        var result = await _jim.ConfigurationDrift.GetConnectedSystemDriftAsync(SystemId);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.HasPendingChanges, Is.True);
            Assert.That(result.ChangeCount, Is.EqualTo(2));
            Assert.That(result.HighestChangeClass, Is.EqualTo(ConfigurationChangeClass.Destructive));
        }
    }

    [Test]
    public async Task GetConnectedSystemDriftAsync_SynchronisationRuleFlowEditThatWasUndone_IsNotPendingAsync()
    {
        SetImpacts(
            Impact(AfterSync, ConfigurationChangeClass.SyncAffecting, syncRuleId: 100, version: 5),
            Impact(AfterSync.AddMinutes(5), ConfigurationChangeClass.SyncAffecting, syncRuleId: 100, version: 6));
        var rule = ExportRule();
        _activityRepo.Setup(r => r.GetConfigurationChangeSnapshotInForceAsync(ActivityTargetType.SynchronisationRule, 100, LastFullSync))
            .ReturnsAsync(Serialised(rule));
        _activityRepo.Setup(r => r.GetLatestConfigurationChangeSnapshotAsync(ActivityTargetType.SynchronisationRule, 100))
            .ReturnsAsync(Serialised(rule));

        var result = await _jim.ConfigurationDrift.GetConnectedSystemDriftAsync(SystemId);

        Assert.That(result.HasPendingChanges, Is.False);
    }

    [Test]
    public async Task GetConnectedSystemDriftAsync_ConfigurationCreatedSinceTheLastFullSynchronisation_IsPendingAsync()
    {
        // With no version in force at the reference point there is nothing to compare against, so the change counts.
        SetImpacts(Impact(AfterSync, ConfigurationChangeClass.SyncAffecting, syncRuleId: 100, version: 1));
        _activityRepo.Setup(r => r.GetConfigurationChangeSnapshotInForceAsync(ActivityTargetType.SynchronisationRule, 100, LastFullSync))
            .ReturnsAsync((string?)null);
        _activityRepo.Setup(r => r.GetLatestConfigurationChangeSnapshotAsync(ActivityTargetType.SynchronisationRule, 100))
            .ReturnsAsync(Serialised(ExportRule()));

        var result = await _jim.ConfigurationDrift.GetConnectedSystemDriftAsync(SystemId);

        Assert.That(result.HasPendingChanges, Is.True);
    }

    [Test]
    public async Task GetConnectedSystemDriftAsync_UnversionedChange_IsPendingWithoutAComparisonAsync()
    {
        // A deletion carries an unversioned tombstone: the configuration it belonged to has no history left to compare.
        SetImpacts(Impact(AfterSync, ConfigurationChangeClass.Destructive, connectedSystemId: SystemId));

        var result = await _jim.ConfigurationDrift.GetConnectedSystemDriftAsync(SystemId);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.HasPendingChanges, Is.True);
            _activityRepo.Verify(r => r.GetConfigurationChangeSnapshotInForceAsync(It.IsAny<ActivityTargetType>(), It.IsAny<int>(), It.IsAny<DateTime>()), Times.Never);
        }
    }

    [Test]
    public async Task GetConnectedSystemDriftAsync_TwoSystemsSynchronisedAtDifferentTimes_CompareAgainstTheirOwnReferencePointsAsync()
    {
        // The Object Type changed once before the other system's Full Synchronisation and was undone after both, so for
        // this system the configuration is back where it started while the other ran with the changed one.
        var otherLastFullSync = LastFullSync.AddHours(1);
        _activityRepo.Setup(r => r.GetLastFullSynchronisationStartsAsync(It.IsAny<IList<int>>()))
            .ReturnsAsync(new Dictionary<int, DateTime> { [SystemId] = LastFullSync, [OtherSystemId] = otherLastFullSync });
        _connectedSystemRepo.Setup(r => r.GetConfigurationScopesAsync(It.IsAny<IList<int>>()))
            .ReturnsAsync([Scope(SystemId, objectTypeIds: [20]), Scope(OtherSystemId, objectTypeIds: [20])]);
        SetImpacts(
            Impact(LastFullSync.AddMinutes(30), ConfigurationChangeClass.SyncAffecting, metaverseObjectTypeId: 20, version: 2),
            Impact(AfterSync, ConfigurationChangeClass.SyncAffecting, metaverseObjectTypeId: 20, version: 3));
        var changed = ObjectType(t => t.Attributes.Add(new MetaverseAttribute { Id = 31, Name = "Employee Type" }));
        _activityRepo.Setup(r => r.GetConfigurationChangeSnapshotInForceAsync(ActivityTargetType.MetaverseObjectType, 20, LastFullSync))
            .ReturnsAsync(Serialised(ObjectType()));
        _activityRepo.Setup(r => r.GetConfigurationChangeSnapshotInForceAsync(ActivityTargetType.MetaverseObjectType, 20, otherLastFullSync))
            .ReturnsAsync(Serialised(changed));
        _activityRepo.Setup(r => r.GetLatestConfigurationChangeSnapshotAsync(ActivityTargetType.MetaverseObjectType, 20))
            .ReturnsAsync(Serialised(ObjectType()));

        var results = await _jim.ConfigurationDrift.GetConnectedSystemDriftAsync([SystemId, OtherSystemId]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(results[SystemId].HasPendingChanges, Is.False, "back to what this system last ran with");
            Assert.That(results[OtherSystemId].HasPendingChanges, Is.True, "the other system ran with the changed type");
            Assert.That(results[OtherSystemId].ChangeCount, Is.EqualTo(1), "only the change after its own reference point");
        }
    }

    #endregion

    #region helpers

    private static readonly byte[] SnapshotHashKey = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();

    private string Serialised(MetaverseObjectType objectType) =>
        ConfigurationSnapshotService.Serialise(_jim.ConfigurationSnapshots.CreateSnapshot(objectType, SnapshotHashKey));

    private string Serialised(SyncRule rule) =>
        ConfigurationSnapshotService.Serialise(_jim.ConfigurationSnapshots.CreateSnapshot(rule, SnapshotHashKey));

    private void SetObjectTypeHistory(MetaverseObjectType inForceAtLastFullSync, MetaverseObjectType latest)
    {
        _activityRepo.Setup(r => r.GetConfigurationChangeSnapshotInForceAsync(ActivityTargetType.MetaverseObjectType, 20, LastFullSync))
            .ReturnsAsync(Serialised(inForceAtLastFullSync));
        _activityRepo.Setup(r => r.GetLatestConfigurationChangeSnapshotAsync(ActivityTargetType.MetaverseObjectType, 20))
            .ReturnsAsync(Serialised(latest));
    }

    private static MetaverseObjectType ObjectType(Action<MetaverseObjectType>? edit = null)
    {
        var type = new MetaverseObjectType
        {
            Id = 20,
            Name = "User",
            DeletionRule = MetaverseObjectDeletionRule.WhenLastConnectorDisconnected,
            DeletionGracePeriod = TimeSpan.FromDays(7),
            Attributes = [new MetaverseAttribute { Id = 30, Name = "Display Name" }]
        };
        edit?.Invoke(type);
        return type;
    }

    private static SyncRule ExportRule() => new()
    {
        Id = 100,
        Name = "AD Export",
        Direction = SyncRuleDirection.Export,
        Enabled = true,
        ProvisionToConnectedSystem = true,
        ConnectedSystemId = SystemId,
        ConnectedSystemObjectTypeId = 7,
        MetaverseObjectTypeId = 20
    };
    private static ConfigurationChangeSynchronisationSummary Summary(long id, int objectTypeId, string objectTypeName, string reason, DateTime when) => new()
    {
        Id = id,
        ConnectedSystemId = SystemId,
        ConnectedSystemObjectTypeId = objectTypeId,
        ObjectTypeName = objectTypeName,
        Reason = reason,
        RequestedAt = when
    };

    private void SetAwaiting(params ObjectTypeAwaitingSynchronisation[] awaiting)
    {
        _connectedSystemRepo.Setup(r => r.GetConfigurationChangesAwaitingSynchronisationAsync(It.IsAny<IList<int>>()))
            .ReturnsAsync(awaiting.ToList());
    }

    private static ObjectTypeAwaitingSynchronisation Awaiting(int connectedSystemId, int objectTypeId, string objectTypeName, int changeCount, DateTime since) => new()
    {
        ConnectedSystemId = connectedSystemId,
        ConnectedSystemObjectTypeId = objectTypeId,
        ObjectTypeName = objectTypeName,
        ChangeCount = changeCount,
        EarliestRequestedAt = since
    };

    private void SetChangeTrackingEnabled(bool enabled)
    {
        _serviceSettingsRepo.Setup(r => r.GetSettingAsync(Constants.SettingKeys.ChangeTrackingConfigurationChangesEnabled))
            .ReturnsAsync(new ServiceSetting
            {
                Key = Constants.SettingKeys.ChangeTrackingConfigurationChangesEnabled,
                ValueType = ServiceSettingValueType.Boolean,
                Value = enabled ? "true" : "false"
            });
    }

    private void SetImpacts(params ConfigurationChangeImpactData[] impacts)
    {
        _activityRepo.Setup(r => r.GetConfigurationChangeImpactsSinceAsync(It.IsAny<DateTime>(), It.IsAny<ConfigurationChangeClass>()))
            .ReturnsAsync(impacts.ToList());
    }

    private static ConfigurationChangeImpactData Impact(DateTime when, ConfigurationChangeClass changeClass,
        int? connectedSystemId = null, int? syncRuleId = null, int? metaverseObjectTypeId = null,
        int? metaverseAttributeId = null, string? serviceSettingKey = null, int? version = null) => new()
    {
        When = when,
        Version = version,
        Class = changeClass,
        ConnectedSystemId = connectedSystemId,
        SyncRuleId = syncRuleId,
        MetaverseObjectTypeId = metaverseObjectTypeId,
        MetaverseAttributeId = metaverseAttributeId,
        ServiceSettingKey = serviceSettingKey
    };

    private static ConnectedSystemConfigurationScope Scope(int connectedSystemId, int[]? syncRuleIds = null,
        int[]? objectTypeIds = null, int[]? attributeIds = null) => new()
    {
        ConnectedSystemId = connectedSystemId,
        SyncRuleIds = [.. syncRuleIds ?? []],
        MetaverseObjectTypeIds = [.. objectTypeIds ?? []],
        MetaverseAttributeIds = [.. attributeIds ?? []]
    };
    #endregion
}
