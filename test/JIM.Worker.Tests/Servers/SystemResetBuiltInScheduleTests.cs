// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.ExampleData;
using JIM.Models.Scheduling;
using JIM.Models.Utility;
using Moq;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// Tests that a factory reset restores the built-in Temporal Scope Reconciliation schedule (issue #892).
/// The wipe truncates the Schedules table, and built-in data is meant to survive a reset; without an
/// immediate re-seed the schedule only reappears on the next worker restart, leaving date-based scope
/// reconciliation silently inoperative until then.
/// </summary>
[TestFixture]
public class SystemResetBuiltInScheduleTests
{
    private Mock<IRepository> _repo = null!;
    private Mock<IActivityRepository> _activityRepo = null!;
    private Mock<IServiceSettingsRepository> _settingsRepo = null!;
    private Mock<ISchedulingRepository> _schedulingRepo = null!;
    private Mock<ISystemRepository> _systemRepo = null!;
    private Mock<IExampleDataRepository> _exampleDataRepo = null!;
    private JimApplication _jim = null!;

    [SetUp]
    public void SetUp()
    {
        TestUtilities.SetEnvironmentVariables();

        _repo = new Mock<IRepository>();
        _activityRepo = new Mock<IActivityRepository>();
        _settingsRepo = new Mock<IServiceSettingsRepository>();
        _schedulingRepo = new Mock<ISchedulingRepository>();
        _systemRepo = new Mock<ISystemRepository>();
        _exampleDataRepo = new Mock<IExampleDataRepository>();
        _repo.Setup(r => r.Activity).Returns(_activityRepo.Object);
        _repo.Setup(r => r.ServiceSettings).Returns(_settingsRepo.Object);
        _repo.Setup(r => r.Scheduling).Returns(_schedulingRepo.Object);
        _repo.Setup(r => r.System).Returns(_systemRepo.Object);
        _repo.Setup(r => r.ExampleData).Returns(_exampleDataRepo.Object);

        // No activities in progress, so the reset's integrity guard passes.
        _activityRepo.Setup(r => r.GetActivitiesAsync(
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<bool>(),
                It.IsAny<Guid?>(), It.IsAny<IEnumerable<ActivityTargetOperationType>?>(),
                It.IsAny<IEnumerable<ActivityOutcomeType>?>(), It.IsAny<IEnumerable<ActivityTargetType>?>(),
                It.IsAny<IEnumerable<ActivityStatus>?>(), It.IsAny<bool?>()))
            .ReturnsAsync(new PagedResultSet<Activity> { Results = new List<Activity>(), TotalResults = 0, PageSize = 1, CurrentPage = 1 });
        _activityRepo.Setup(r => r.CreateActivityAsync(It.IsAny<Activity>())).Returns(Task.CompletedTask);
        _activityRepo.Setup(r => r.UpdateActivityAsync(It.IsAny<Activity>())).Returns(Task.CompletedTask);

        _systemRepo.Setup(r => r.ResetSystemAsync(It.IsAny<bool>())).ReturnsAsync(new SystemResetResult());

        // The built-in example data template is intact, so its repair path is a no-op.
        var intactTemplate = new ExampleDataTemplate { Name = "Users & Groups", BuiltIn = true };
        var objectType = new ExampleDataObjectType { MetaverseObjectType = new MetaverseObjectType { Name = "User" } };
        objectType.TemplateAttributes.Add(new ExampleDataTemplateAttribute());
        intactTemplate.ObjectTypes.Add(objectType);
        _exampleDataRepo.Setup(r => r.GetTemplateAsync("Users & Groups")).ReturnsAsync(intactTemplate);

        // The wipe has removed every schedule, including the built-in one.
        _schedulingRepo.Setup(r => r.GetAllSchedulesAsync()).ReturnsAsync(new List<Schedule>());
        _schedulingRepo.Setup(r => r.CreateScheduleAsync(It.IsAny<Schedule>())).Returns(Task.CompletedTask);

        // Change-history capture is not under test here; disable tracking to keep the path lean.
        _settingsRepo.Setup(r => r.GetSettingAsync(Constants.SettingKeys.ChangeTrackingConfigurationChangesEnabled))
            .ReturnsAsync(new ServiceSetting
            {
                Key = Constants.SettingKeys.ChangeTrackingConfigurationChangesEnabled,
                DisplayName = "Track configuration changes",
                ValueType = ServiceSettingValueType.Boolean,
                Value = "false"
            });
        _settingsRepo.Setup(r => r.GetServiceSettingsAsync()).ReturnsAsync(new ServiceSettings());
        _settingsRepo.Setup(r => r.UpdateServiceSettingsAsync(It.IsAny<ServiceSettings>())).Returns(Task.CompletedTask);

        _jim = new JimApplication(_repo.Object);
    }

    [TearDown]
    public void TearDown() => _jim?.Dispose();

    [Test]
    public async Task ResetSystemAsync_SchedulesWiped_ReseedsBuiltInScheduleAsync()
    {
        await _jim.System.ResetSystemAsync(
            ActivityInitiatorType.ApiKey, Guid.NewGuid(), "Infrastructure Key", includeAdministrators: false);

        _schedulingRepo.Verify(r => r.CreateScheduleAsync(It.Is<Schedule>(s =>
                s.BuiltIn &&
                s.IsEnabled &&
                s.Steps.Any(st => st.StepType == ScheduleStepType.TemporalScopeReconciliation))),
            Times.Once,
            "a factory reset must restore the built-in Temporal Scope Reconciliation schedule immediately, not on the next worker restart");
    }

    [Test]
    public async Task ResetSystemAsync_ReseedCreatesSeedingParentActivity_ParentIsCompletedAsync()
    {
        // The reseed of the built-in schedule lazily creates the "System Initialisation" parent Activity that
        // groups seeded objects. Track every activity the reset path completes so we can prove the parent does
        // not get left permanently InProgress (an in-flight activity would also block any subsequent reset via
        // the in-progress guard).
        var completedActivities = new List<Activity>();
        _activityRepo.Setup(r => r.UpdateActivityAsync(It.IsAny<Activity>()))
            .Callback<Activity>(a => completedActivities.Add(a))
            .Returns(Task.CompletedTask);

        await _jim.System.ResetSystemAsync(
            ActivityInitiatorType.ApiKey, Guid.NewGuid(), "Infrastructure Key", includeAdministrators: false);

        var seedingParent = completedActivities.SingleOrDefault(a => a.TargetType == ActivityTargetType.SystemInitialisation);
        Assert.That(seedingParent, Is.Not.Null,
            "the reseed's System Initialisation parent Activity must be completed by the reset path, not left permanently InProgress");
        Assert.That(seedingParent!.Status, Is.EqualTo(ActivityStatus.Complete));
    }
}
