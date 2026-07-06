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
/// Tests that the built-in Temporal Scope Reconciliation schedule (issue #892) is seeded through the audited
/// create path, not written straight to the repository. A repository-direct seed leaves no Create Activity and
/// no version-1 configuration change snapshot, so the schedule's change history starts with whichever principal
/// touches it next (for example an API key disabling it), which misattributes the schedule's origin in the
/// portal. The seeded creation must be attributed to System, with a version-1 snapshot, and enabled by default.
/// </summary>
[TestFixture]
public class BuiltInScheduleSeedingTests
{
    private Mock<IRepository> _repo = null!;
    private Mock<IActivityRepository> _activityRepo = null!;
    private Mock<IServiceSettingsRepository> _settingsRepo = null!;
    private Mock<ISchedulingRepository> _schedulingRepo = null!;
    private FakeProtection _protection = null!;
    private JimApplication _jim = null!;
    private Schedule? _createdSchedule;
    private Activity? _createdActivity;
    private Activity? _completedActivity;
    private List<Activity> _createdActivities = null!;

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

        _createdSchedule = null;
        _createdActivity = null;
        _completedActivity = null;
        _createdActivities = new List<Activity>();

        _activityRepo.Setup(r => r.CreateActivityAsync(It.IsAny<Activity>()))
            .Callback<Activity>(a =>
            {
                _createdActivities.Add(a);
                _createdActivity = a;
            })
            .Returns(Task.CompletedTask);
        _activityRepo.Setup(r => r.UpdateActivityAsync(It.IsAny<Activity>()))
            .Callback<Activity>(a => _completedActivity = a)
            .Returns(Task.CompletedTask);
        _activityRepo.Setup(r => r.GetMaxConfigurationChangeVersionAsync(ActivityTargetType.Schedule, It.IsAny<Guid>()))
            .ReturnsAsync(0);
        _schedulingRepo.Setup(r => r.CreateScheduleAsync(It.IsAny<Schedule>()))
            .Callback<Schedule>(s => _createdSchedule = s)
            .Returns(Task.CompletedTask);
        _schedulingRepo.Setup(r => r.GetScheduleWithStepsAsync(It.IsAny<Guid>()))
            .ReturnsAsync(() => _createdSchedule);

        _protection = new FakeProtection();
        _jim = new JimApplication(_repo.Object) { CredentialProtection = _protection };

        SetupTrackingSetting(enabled: true);
        SetupHashKeySetting();
    }

    [TearDown]
    public void TearDown() => _jim?.Dispose();

    [Test]
    public async Task SeedBuiltInSchedulesAsync_NoScheduleExists_CreatesEnabledScheduleThroughAuditedPathAsync()
    {
        _schedulingRepo.Setup(r => r.GetAllSchedulesAsync()).ReturnsAsync(new List<Schedule>());

        await _jim.Seeding.SeedBuiltInSchedulesAsync();

        Assert.That(_createdSchedule, Is.Not.Null, "the built-in schedule must be created");
        Assert.That(_createdSchedule!.BuiltIn, Is.True);
        Assert.That(_createdSchedule.IsEnabled, Is.True, "the built-in schedule must be seeded enabled");
        Assert.That(_createdSchedule.CreatedByType, Is.EqualTo(ActivityInitiatorType.System));
        Assert.That(_createdSchedule.Steps.Any(s => s.StepType == ScheduleStepType.TemporalScopeReconciliation), Is.True);

        // The creation must be auditable: a Create Activity attributed to System, so the portal's change
        // history shows how the schedule came to exist rather than starting at the first later update.
        Assert.That(_createdActivity, Is.Not.Null, "seeding must record a Create Activity for the built-in schedule");
        Assert.That(_createdActivity!.TargetType, Is.EqualTo(ActivityTargetType.Schedule));
        Assert.That(_createdActivity.TargetOperationType, Is.EqualTo(ActivityTargetOperationType.Create));
        Assert.That(_createdActivity.InitiatedByType, Is.EqualTo(ActivityInitiatorType.System));
        Assert.That(_createdActivity.InitiatedByName, Is.EqualTo("System"));

        Assert.That(_completedActivity, Is.Not.Null);
        Assert.That(_completedActivity!.ConfigurationChangeVersion, Is.EqualTo(1),
            "the seeded creation must be version 1 of the schedule's configuration change history");
        Assert.That(_completedActivity.ConfigurationChangeSnapshot, Does.Contain("\"objectType\":\"Schedule\""));
        Assert.That(_completedActivity.ChangeReason, Is.Not.Null.And.Not.Empty,
            "the seeded creation should explain its provenance in the change history");

        // The seeded creation must be grouped under a single System Initialisation parent Activity, so a fresh
        // deployment's built-in configuration appears as one top-level Activity, not one row per seeded object.
        var scheduleActivity = _createdActivities.Single(a => a.TargetType == ActivityTargetType.Schedule);
        var parentActivity = _createdActivities.SingleOrDefault(a => a.TargetType == ActivityTargetType.SystemInitialisation);
        Assert.That(parentActivity, Is.Not.Null,
            "seeding must record a parent System Initialisation Activity when it creates the built-in schedule");
        Assert.That(scheduleActivity.ParentActivityId, Is.EqualTo(parentActivity!.Id));
    }

    [Test]
    public async Task SeedBuiltInSchedulesAsync_ScheduleAlreadyExists_DoesNothingAsync()
    {
        var existing = new Schedule
        {
            Id = Guid.NewGuid(),
            Name = "Temporal Scope Reconciliation",
            BuiltIn = true,
            IsEnabled = false, // an administrator's choice to disable it must be respected across restarts
            Steps = new List<ScheduleStep>
            {
                new() { Id = Guid.NewGuid(), StepIndex = 0, Name = "Reconcile Temporal Scope", StepType = ScheduleStepType.TemporalScopeReconciliation }
            }
        };
        _schedulingRepo.Setup(r => r.GetAllSchedulesAsync()).ReturnsAsync(new List<Schedule> { existing });

        await _jim.Seeding.SeedBuiltInSchedulesAsync();

        _schedulingRepo.Verify(r => r.CreateScheduleAsync(It.IsAny<Schedule>()), Times.Never,
            "seeding is idempotent: an existing built-in schedule must not be recreated");
        _activityRepo.Verify(r => r.CreateActivityAsync(It.IsAny<Activity>()), Times.Never);
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
