// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Text;
using JIM.Application;
using JIM.Application.Interfaces;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Scheduling;
using JIM.Models.Security;
using Moq;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// Tests configuration change-history capture for Schedules (issue #892): a redacted, versioned snapshot is
/// recorded on the audit Activity when tracking is enabled, keyed by the Schedule's Guid via
/// <see cref="Activity.ScheduleId"/>; nothing is captured when tracking is disabled; and a deletion records an
/// unversioned, unlinked tombstone snapshot, matching the Synchronisation Rule deletion behaviour. The snapshot
/// is built from the persisted schedule (reloaded with its steps) so history reflects saved truth, not the
/// caller's partial in-memory graph.
/// </summary>
[TestFixture]
public class ScheduleConfigurationChangeCaptureTests
{
    private Mock<IRepository> _repo = null!;
    private Mock<IActivityRepository> _activityRepo = null!;
    private Mock<IServiceSettingsRepository> _settingsRepo = null!;
    private Mock<ISchedulingRepository> _schedulingRepo = null!;
    private FakeProtection _protection = null!;
    private JimApplication _jim = null!;
    private Activity? _completedActivity;

    [SetUp]
    public void SetUp()
    {
        TestUtilities.SetEnvironmentVariables();

        _repo = new Mock<IRepository>();
        _activityRepo = new Mock<IActivityRepository>();
        _settingsRepo = new Mock<IServiceSettingsRepository>();
        _schedulingRepo = new Mock<ISchedulingRepository>();
        _repo.Setup(r => r.Activity).Returns(_activityRepo.Object);
        _repo.Setup(r => r.ServiceSettings).Returns(_settingsRepo.Object);
        _repo.Setup(r => r.Scheduling).Returns(_schedulingRepo.Object);

        _activityRepo.Setup(r => r.CreateActivityAsync(It.IsAny<Activity>())).Returns(Task.CompletedTask);
        _activityRepo.Setup(r => r.UpdateActivityAsync(It.IsAny<Activity>()))
            .Callback<Activity>(a => _completedActivity = a)
            .Returns(Task.CompletedTask);
        _schedulingRepo.Setup(r => r.CreateScheduleAsync(It.IsAny<Schedule>())).Returns(Task.CompletedTask);
        _schedulingRepo.Setup(r => r.UpdateScheduleAsync(It.IsAny<Schedule>())).Returns(Task.CompletedTask);
        _schedulingRepo.Setup(r => r.DeleteScheduleAsync(It.IsAny<Schedule>())).Returns(Task.CompletedTask);

        _protection = new FakeProtection();
        _jim = new JimApplication(_repo.Object) { CredentialProtection = _protection };
    }

    [TearDown]
    public void TearDown() => _jim?.Dispose();

    [Test]
    public async Task UpdateScheduleAsync_WhenTrackingEnabled_CapturesVersionedSnapshotOnActivityAsync()
    {
        SetupTrackingSetting(enabled: true);
        SetupHashKeySetting();
        var schedule = BuildSchedule();
        _schedulingRepo.Setup(r => r.GetScheduleWithStepsAsync(schedule.Id)).ReturnsAsync(schedule);
        _activityRepo.Setup(r => r.GetMaxConfigurationChangeVersionAsync(ActivityTargetType.Schedule, schedule.Id))
            .ReturnsAsync(6);

        await _jim.Scheduler.UpdateScheduleAsync(schedule, ActivityInitiatorType.User, Guid.NewGuid(), "Alice Admin");

        Assert.That(_completedActivity, Is.Not.Null);
        Assert.That(_completedActivity!.ScheduleId, Is.EqualTo(schedule.Id), "the activity must carry the Schedule id so history is queryable");
        Assert.That(_completedActivity.ConfigurationChangeVersion, Is.EqualTo(7), "version is the existing maximum (6) + 1");
        Assert.That(_completedActivity.ConfigurationChangeSnapshot, Is.Not.Null);
        Assert.That(_completedActivity.ConfigurationChangeSnapshot, Does.Contain("\"objectType\":\"Schedule\""));
    }

    [Test]
    public async Task UpdateScheduleAsync_WhenTrackingEnabled_SnapshotReflectsPersistedStepsAsync()
    {
        // The snapshot must be built from the persisted schedule (reloaded with steps), not the caller's
        // entity, which may have been loaded without its Steps navigation.
        SetupTrackingSetting(enabled: true);
        SetupHashKeySetting();
        var persisted = BuildSchedule();
        var callersPartialEntity = new Schedule { Id = persisted.Id, Name = persisted.Name, Steps = new List<ScheduleStep>() };
        _schedulingRepo.Setup(r => r.GetScheduleWithStepsAsync(persisted.Id)).ReturnsAsync(persisted);
        _activityRepo.Setup(r => r.GetMaxConfigurationChangeVersionAsync(ActivityTargetType.Schedule, persisted.Id))
            .ReturnsAsync(0);

        await _jim.Scheduler.UpdateScheduleAsync(callersPartialEntity, ActivityInitiatorType.User, Guid.NewGuid(), "Alice Admin");

        Assert.That(_completedActivity, Is.Not.Null);
        Assert.That(_completedActivity!.ConfigurationChangeSnapshot, Does.Contain("Reconcile Temporal Scope"),
            "the snapshot must include the persisted steps even when the caller's entity has none loaded");
    }

    [Test]
    public async Task UpdateScheduleAsync_WhenTrackingDisabled_RecordsNoSnapshotAsync()
    {
        SetupTrackingSetting(enabled: false);
        var schedule = BuildSchedule();

        await _jim.Scheduler.UpdateScheduleAsync(schedule, ActivityInitiatorType.User, Guid.NewGuid(), "Alice Admin");

        Assert.That(_completedActivity, Is.Not.Null);
        Assert.That(_completedActivity!.ConfigurationChangeSnapshot, Is.Null, "no snapshot is captured when tracking is disabled");
        Assert.That(_completedActivity.ConfigurationChangeVersion, Is.Null);
    }

    [Test]
    public async Task CreateScheduleAsync_WhenTrackingEnabled_CapturesVersionOneSnapshotAsync()
    {
        SetupTrackingSetting(enabled: true);
        SetupHashKeySetting();
        var schedule = BuildSchedule();
        _schedulingRepo.Setup(r => r.GetScheduleWithStepsAsync(schedule.Id)).ReturnsAsync(schedule);
        _activityRepo.Setup(r => r.GetMaxConfigurationChangeVersionAsync(ActivityTargetType.Schedule, schedule.Id))
            .ReturnsAsync(0);

        await _jim.Scheduler.CreateScheduleAsync(schedule, ActivityInitiatorType.User, Guid.NewGuid(), "Alice Admin");

        Assert.That(_completedActivity, Is.Not.Null);
        Assert.That(_completedActivity!.ScheduleId, Is.EqualTo(schedule.Id));
        Assert.That(_completedActivity.ConfigurationChangeVersion, Is.EqualTo(1), "the first capture for an object is version 1");
        Assert.That(_completedActivity.ConfigurationChangeSnapshot, Does.Contain("\"objectType\":\"Schedule\""));
    }

    [Test]
    public async Task DeleteScheduleAsync_WhenTrackingEnabled_CapturesTombstoneWithoutVersionOrLinkAsync()
    {
        SetupTrackingSetting(enabled: true);
        SetupHashKeySetting();
        var schedule = BuildSchedule();
        _schedulingRepo.Setup(r => r.GetScheduleWithStepsAsync(schedule.Id)).ReturnsAsync(schedule);

        await _jim.Scheduler.DeleteScheduleAsync(schedule, ActivityInitiatorType.User, Guid.NewGuid(), "Alice Admin");

        Assert.That(_completedActivity, Is.Not.Null);
        Assert.That(_completedActivity!.ConfigurationChangeSnapshot, Does.Contain("\"objectType\":\"Schedule\""), "the deletion records a tombstone snapshot");
        Assert.That(_completedActivity.ScheduleId, Is.Null, "a delete activity is left unlinked so it can complete after the schedule is removed");
        Assert.That(_completedActivity.ConfigurationChangeVersion, Is.Null, "deletions record a tombstone, not a versioned entry");
        _schedulingRepo.Verify(r => r.DeleteScheduleAsync(schedule), Times.Once);
    }

    // -- helpers -------------------------------------------------------------------------------------------------------

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
                Value = _protection.Protect(Convert.ToBase64String(new byte[32]))
            });

    private static Schedule BuildSchedule() => new()
    {
        Id = Guid.NewGuid(),
        Name = "Temporal Scope Reconciliation",
        BuiltIn = false,
        IsEnabled = true,
        TriggerType = ScheduleTriggerType.Cron,
        PatternType = SchedulePatternType.Interval,
        CronExpression = "0 * * * *",
        Steps = new List<ScheduleStep>
        {
            new()
            {
                Id = Guid.NewGuid(),
                StepIndex = 0,
                Name = "Reconcile Temporal Scope",
                StepType = ScheduleStepType.TemporalScopeReconciliation
            }
        }
    };

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
