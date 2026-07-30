// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Activities.DTOs;
using JIM.Models.Core;
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

        _jim = new JimApplication(_repo.Object);
    }

    [TearDown]
    public void TearDown() => _jim?.Dispose();

    [Test]
    public async Task GetConnectedSystemDriftAsync_NoChangesSinceLastFullSynchronisation_ReportsNoPendingChangesAsync()
    {
        var result = await _jim.ConfigurationDrift.GetConnectedSystemDriftAsync(SystemId);

        Assert.Multiple(() =>
        {
            Assert.That(result.HasPendingChanges, Is.False);
            Assert.That(result.IsDeterminable, Is.True);
            Assert.That(result.ChangeCount, Is.EqualTo(0));
            Assert.That(result.LastFullSynchronisation, Is.EqualTo(LastFullSync));
            Assert.That(result.HighestChangeClass, Is.EqualTo(ConfigurationChangeClass.NotClassified));
        });
    }

    [Test]
    public async Task GetConnectedSystemDriftAsync_SyncAffectingChangeToTheSystem_ReportsPendingChangesAsync()
    {
        SetImpacts(Impact(AfterSync, ConfigurationChangeClass.SyncAffecting, connectedSystemId: SystemId));

        var result = await _jim.ConfigurationDrift.GetConnectedSystemDriftAsync(SystemId);

        Assert.Multiple(() =>
        {
            Assert.That(result.HasPendingChanges, Is.True);
            Assert.That(result.ChangeCount, Is.EqualTo(1));
            Assert.That(result.MostRecentChange, Is.EqualTo(AfterSync));
            Assert.That(result.HighestChangeClass, Is.EqualTo(ConfigurationChangeClass.SyncAffecting));
        });
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

        Assert.Multiple(() =>
        {
            Assert.That(result.ChangeCount, Is.EqualTo(2));
            Assert.That(result.HighestChangeClass, Is.EqualTo(ConfigurationChangeClass.Destructive));
            Assert.That(result.MostRecentChange, Is.EqualTo(AfterSync.AddMinutes(10)));
        });
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

        Assert.Multiple(() =>
        {
            Assert.That(result.HasPendingChanges, Is.True);
            Assert.That(result.HighestChangeClass, Is.EqualTo(ConfigurationChangeClass.Destructive));
        });
    }

    [Test]
    public async Task GetConnectedSystemDriftAsync_NeverFullySynchronised_ReportsNeverRatherThanPendingAsync()
    {
        _activityRepo.Setup(r => r.GetLastFullSynchronisationStartsAsync(It.IsAny<IList<int>>()))
            .ReturnsAsync(new Dictionary<int, DateTime>());

        var result = await _jim.ConfigurationDrift.GetConnectedSystemDriftAsync(SystemId);

        Assert.Multiple(() =>
        {
            Assert.That(result.NeverFullySynchronised, Is.True);
            Assert.That(result.HasPendingChanges, Is.False);
            Assert.That(result.IsDeterminable, Is.False);
            Assert.That(result.LastFullSynchronisation, Is.Null);
        });
    }

    [Test]
    public async Task GetConnectedSystemDriftAsync_ChangeTrackingDisabled_ReportsUndeterminableRatherThanCleanAsync()
    {
        SetChangeTrackingEnabled(false);

        var result = await _jim.ConfigurationDrift.GetConnectedSystemDriftAsync(SystemId);

        Assert.Multiple(() =>
        {
            Assert.That(result.TrackingDisabled, Is.True);
            Assert.That(result.HasPendingChanges, Is.False);
            Assert.That(result.IsDeterminable, Is.False);
        });
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

        Assert.Multiple(() =>
        {
            Assert.That(results[SystemId].HasPendingChanges, Is.True);
            Assert.That(results[OtherSystemId].HasPendingChanges, Is.False);
        });
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

    #region helpers
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
        int? metaverseAttributeId = null, string? serviceSettingKey = null) => new()
    {
        When = when,
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
