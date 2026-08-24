// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Text;
using JIM.Application;
using JIM.Application.Interfaces;
using JIM.Application.Servers;
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
    private List<Schedule> _createdSchedules = null!;
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
        _createdSchedules = new List<Schedule>();
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
            .Callback<Schedule>(s =>
            {
                _createdSchedule = s;
                _createdSchedules.Add(s);
            })
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

        Assert.That(_createdSchedules.Select(s => s.Name),
            Is.EquivalentTo(SeedingServer.BuiltInSchedules().Select(s => s.Name)),
            "every entry in the built-in catalogue must be created on a virgin database");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(_createdSchedules.Select(s => s.BuiltIn), Is.All.True);
            Assert.That(_createdSchedules.Select(s => s.IsEnabled), Is.All.True, "built-in schedules are seeded enabled");
            Assert.That(_createdSchedules.Select(s => s.CreatedByType), Is.All.EqualTo(ActivityInitiatorType.System));
            Assert.That(_createdSchedules.SelectMany(s => s.Steps).Select(s => s.StepType),
                Is.EquivalentTo(new[] { ScheduleStepType.TemporalScopeReconciliation, ScheduleStepType.HistoryRetentionCleanup }));
        }

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
        var scheduleActivities = _createdActivities.Where(a => a.TargetType == ActivityTargetType.Schedule).ToList();
        var parentActivity = _createdActivities.SingleOrDefault(a => a.TargetType == ActivityTargetType.SystemInitialisation);
        Assert.That(parentActivity, Is.Not.Null,
            "seeding must record a parent System Initialisation Activity when it creates the built-in schedules");
        Assert.That(scheduleActivities.Select(a => a.ParentActivityId), Is.All.EqualTo(parentActivity!.Id));
    }

    [Test]
    public async Task SeedBuiltInSchedulesAsync_ScheduleAlreadyExists_DoesNothingAsync()
    {
        // Disabled on purpose: an administrator's choice to turn a built-in schedule off must be respected
        // across restarts, so convergence may only create what is missing, never re-create or re-enable.
        var existing = SeedingServer.BuiltInSchedules()
            .Select(s => new Schedule { Id = Guid.NewGuid(), Name = s.Name, BuiltIn = true, IsEnabled = false, Steps = s.Steps })
            .ToList();
        _schedulingRepo.Setup(r => r.GetAllSchedulesAsync()).ReturnsAsync(existing);

        await _jim.Seeding.SeedBuiltInSchedulesAsync();

        _schedulingRepo.Verify(r => r.CreateScheduleAsync(It.IsAny<Schedule>()), Times.Never,
            "seeding is idempotent: an existing built-in schedule must not be recreated");
        _activityRepo.Verify(r => r.CreateActivityAsync(It.IsAny<Activity>()), Times.Never);
    }

    [Test]
    public async Task SeedBuiltInSchedulesAsync_DeploymentPredatesACatalogueEntry_CreatesOnlyTheMissingOneAsync()
    {
        // The convergence case issue #916 exists for: a deployment that has been running since before a built-in
        // Schedule was added must gain it on the next startup, without the ones it already has being disturbed.
        var catalogue = SeedingServer.BuiltInSchedules().ToList();
        var alreadyPresent = catalogue.First();
        var expectedNew = catalogue.Skip(1).Single();
        _schedulingRepo.Setup(r => r.GetAllSchedulesAsync()).ReturnsAsync(new List<Schedule>
        {
            new() { Id = Guid.NewGuid(), Name = alreadyPresent.Name, BuiltIn = true, Steps = alreadyPresent.Steps }
        });

        await _jim.Seeding.SeedBuiltInSchedulesAsync();

        Assert.That(_createdSchedules.Select(s => s.Name), Is.EqualTo(new[] { expectedNew.Name }),
            "only the catalogue entry the deployment lacks is created");
    }

    [Test]
    public void BuiltInSchedules_HistoryRetentionCleanup_RunsDailyOffPeakWithOneCleanupStep()
    {
        // The retention pass is bounded by the cleanup batch size rather than by how much has accumulated, so
        // running it more often would not drain a backlog faster; daily and off-peak keeps it away from the
        // synchronisation hot path, which competes for the same tables.
        var schedule = SeedingServer.BuiltInSchedules().Single(s => s.Name == "History Retention Cleanup");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(schedule.BuiltIn, Is.True);
            Assert.That(schedule.IsEnabled, Is.True);
            Assert.That(schedule.CronExpression, Is.EqualTo("30 2 * * *"));
            Assert.That(schedule.DaysOfWeek, Is.EqualTo("0,1,2,3,4,5,6"), "retention must not skip a day of the week");
            Assert.That(schedule.Steps, Has.Count.EqualTo(1));
            Assert.That(schedule.Steps.Single().StepType, Is.EqualTo(ScheduleStepType.HistoryRetentionCleanup));
        }
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
