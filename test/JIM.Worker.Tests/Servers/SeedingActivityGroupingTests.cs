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
/// Tests that JIM's own seeding of built-in configuration (issue #14, interim slice) groups every object it
/// actually creates during a single application startup under one "System Initialisation" parent Activity
/// (<see cref="ActivityTargetType.SystemInitialisation"/>), with each seeded object's own Create Activity as a
/// child via <see cref="Activity.ParentActivityId"/>, rather than leaving each seeded object as its own
/// top-level row. The parent is created lazily, only when a seed step is about to create something, so a
/// startup where every seed step no-ops (the normal case after the first deployment) records nothing at all.
/// Complements <see cref="BuiltInScheduleSeedingTests"/> and the SeedBuiltInRolesAsync tests in
/// <see cref="RoleConfigurationChangeCaptureTests"/>, which each cover a single seed step in isolation; this
/// fixture covers the cross-step grouping and the completion/no-op behaviour of the parent itself.
/// </summary>
[TestFixture]
public class SeedingActivityGroupingTests
{
    private Mock<IRepository> _repo = null!;
    private Mock<IActivityRepository> _activityRepo = null!;
    private Mock<IServiceSettingsRepository> _settingsRepo = null!;
    private Mock<ISchedulingRepository> _schedulingRepo = null!;
    private Mock<ISecurityRepository> _securityRepo = null!;
    private FakeProtection _protection = null!;
    private JimApplication _jim = null!;
    private List<Activity> _createdActivities = null!;
    private List<Activity> _updatedActivities = null!;
    private Schedule? _createdSchedule;
    private Role? _createdRole;

    [SetUp]
    public void SetUp()
    {
        TestUtilities.SetEnvironmentVariables();

        _repo = new Mock<IRepository>();
        _activityRepo = new Mock<IActivityRepository>();
        _settingsRepo = new Mock<IServiceSettingsRepository>();
        _schedulingRepo = new Mock<ISchedulingRepository>();
        _securityRepo = new Mock<ISecurityRepository>();
        _repo.Setup(r => r.Activity).Returns(_activityRepo.Object);
        _repo.Setup(r => r.ServiceSettings).Returns(_settingsRepo.Object);
        _repo.Setup(r => r.Scheduling).Returns(_schedulingRepo.Object);
        _repo.Setup(r => r.Security).Returns(_securityRepo.Object);

        _createdActivities = new List<Activity>();
        _updatedActivities = new List<Activity>();
        _createdSchedule = null;
        _createdRole = null;

        _activityRepo.Setup(r => r.CreateActivityAsync(It.IsAny<Activity>()))
            .Callback<Activity>(a => _createdActivities.Add(a))
            .Returns(Task.CompletedTask);
        _activityRepo.Setup(r => r.UpdateActivityAsync(It.IsAny<Activity>()))
            .Callback<Activity>(a => _updatedActivities.Add(a))
            .Returns(Task.CompletedTask);
        _activityRepo.Setup(r => r.GetMaxConfigurationChangeVersionAsync(ActivityTargetType.Schedule, It.IsAny<Guid>()))
            .ReturnsAsync(0);
        _activityRepo.Setup(r => r.GetMaxConfigurationChangeVersionAsync(ActivityTargetType.Role, It.IsAny<int>()))
            .ReturnsAsync(0);

        _schedulingRepo.Setup(r => r.CreateScheduleAsync(It.IsAny<Schedule>()))
            .Callback<Schedule>(s => _createdSchedule = s)
            .Returns(Task.CompletedTask);
        _schedulingRepo.Setup(r => r.GetScheduleWithStepsAsync(It.IsAny<Guid>()))
            .ReturnsAsync(() => _createdSchedule);

        _securityRepo.Setup(r => r.CreateRoleAsync(It.IsAny<Role>()))
            .Callback<Role>(r => _createdRole = r)
            .ReturnsAsync((Role r) => r);
        _securityRepo.Setup(r => r.GetRoleByIdAsync(It.IsAny<int>()))
            .ReturnsAsync(() => _createdRole);

        _protection = new FakeProtection();
        _jim = new JimApplication(_repo.Object) { CredentialProtection = _protection };

        SetupTrackingSetting(enabled: true);
        SetupHashKeySetting();
    }

    [TearDown]
    public void TearDown() => _jim?.Dispose();

    [Test]
    public async Task SeedBuiltInSchedulesAndRoles_BothCreate_ShareOneParentActivityAsync()
    {
        _schedulingRepo.Setup(r => r.GetAllSchedulesAsync()).ReturnsAsync(new List<Schedule>());
        _securityRepo.Setup(r => r.GetRoleAsync(Constants.BuiltInRoles.Administrator)).ReturnsAsync((Role?)null);

        await _jim.Seeding.SeedBuiltInSchedulesAsync();
        await _jim.Seeding.SeedBuiltInRolesAsync();

        var parentActivities = _createdActivities.Where(a => a.TargetType == ActivityTargetType.SystemInitialisation).ToList();
        Assert.That(parentActivities, Has.Count.EqualTo(1),
            "both seed steps created something in the same startup, so exactly one parent Activity must exist");

        var parent = parentActivities.Single();
        Assert.That(parent.TargetOperationType, Is.EqualTo(ActivityTargetOperationType.Create));
        Assert.That(parent.TargetName, Is.EqualTo("Built-in configuration"));
        Assert.That(parent.Message, Is.EqualTo("Applying built-in configuration"));
        Assert.That(parent.InitiatedByType, Is.EqualTo(ActivityInitiatorType.System));

        var scheduleActivity = _createdActivities.Single(a => a.TargetType == ActivityTargetType.Schedule);
        var roleActivity = _createdActivities.Single(a => a.TargetType == ActivityTargetType.Role);
        Assert.That(scheduleActivity.ParentActivityId, Is.EqualTo(parent.Id),
            "the schedule's Create Activity must be a child of the single seeding parent");
        Assert.That(roleActivity.ParentActivityId, Is.EqualTo(parent.Id),
            "the Role's Create Activity must be a child of the same seeding parent, not a second one");
    }

    [Test]
    public async Task SeedBuiltInSchedulesAndRoles_NothingToSeed_RecordsNoParentActivityAndCompleteIsNoOpAsync()
    {
        var existingSchedule = new Schedule
        {
            Id = Guid.NewGuid(),
            Name = "Temporal Scope Reconciliation",
            BuiltIn = true,
            Steps = new List<ScheduleStep>
            {
                new() { Id = Guid.NewGuid(), StepIndex = 0, Name = "Reconcile Temporal Scope", StepType = ScheduleStepType.TemporalScopeReconciliation }
            }
        };
        _schedulingRepo.Setup(r => r.GetAllSchedulesAsync()).ReturnsAsync(new List<Schedule> { existingSchedule });

        var existingRole = new Role { Id = 1, Name = Constants.BuiltInRoles.Administrator, BuiltIn = true };
        _securityRepo.Setup(r => r.GetRoleAsync(Constants.BuiltInRoles.Administrator)).ReturnsAsync(existingRole);

        await _jim.Seeding.SeedBuiltInSchedulesAsync();
        await _jim.Seeding.SeedBuiltInRolesAsync();
        await _jim.Seeding.CompleteSeedingActivityAsync();

        Assert.That(_createdActivities, Is.Empty,
            "a startup where nothing needs seeding must not record a System Initialisation Activity, or any other Activity");
        Assert.That(_updatedActivities, Is.Empty,
            "CompleteSeedingActivityAsync must be a no-op when no parent Activity was created this startup");
    }

    [Test]
    public async Task CompleteSeedingActivityAsync_AfterSeeding_CompletesTheParentActivityAsync()
    {
        _schedulingRepo.Setup(r => r.GetAllSchedulesAsync()).ReturnsAsync(new List<Schedule>());

        await _jim.Seeding.SeedBuiltInSchedulesAsync();
        await _jim.Seeding.CompleteSeedingActivityAsync();

        var parent = _createdActivities.Single(a => a.TargetType == ActivityTargetType.SystemInitialisation);
        var completedParent = _updatedActivities.Single(a => a.TargetType == ActivityTargetType.SystemInitialisation);
        Assert.That(completedParent.Id, Is.EqualTo(parent.Id));
        Assert.That(completedParent.Status, Is.EqualTo(ActivityStatus.Complete));
        Assert.That(completedParent.Message, Is.EqualTo("Applied built-in configuration"));
    }

    [Test]
    public async Task CompleteSeedingActivityAsync_CalledTwice_OnlyCompletesOnceAndStartsFreshNextTimeAsync()
    {
        _schedulingRepo.Setup(r => r.GetAllSchedulesAsync()).ReturnsAsync(new List<Schedule>());

        await _jim.Seeding.SeedBuiltInSchedulesAsync();
        await _jim.Seeding.CompleteSeedingActivityAsync();
        await _jim.Seeding.CompleteSeedingActivityAsync();

        Assert.That(_updatedActivities.Count(a => a.TargetType == ActivityTargetType.SystemInitialisation), Is.EqualTo(1),
            "a second call with nothing new to seed must not re-complete (or otherwise touch) the already-completed parent");
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
