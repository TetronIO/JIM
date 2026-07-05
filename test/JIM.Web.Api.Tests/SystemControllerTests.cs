// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Scheduling;
using JIM.Models.Utility;
using JIM.Web.Controllers.Api;
using JIM.Web.Models.Api;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading.Tasks;

namespace JIM.Web.Api.Tests;

[TestFixture]
public class SystemControllerTests
{
    private const string SsoInitialAdminVar = "JIM_SSO_INITIAL_ADMIN";

    private static (SystemController Controller, Mock<ISystemRepository> SystemRepo, Mock<IActivityRepository> ActivityRepo, Mock<IServiceSettingsRepository> ServiceSettingsRepo)
        BuildController(
            int inProgressActivityCount,
            SystemResetResult? resetResult = null,
            ServiceSettings? serviceSettings = null)
    {
        var mockRepository = new Mock<IRepository>();
        var mockActivityRepo = new Mock<IActivityRepository>();
        var mockSystemRepo = new Mock<ISystemRepository>();
        var mockServiceSettingsRepo = new Mock<IServiceSettingsRepository>();
        // The reset restores the built-in example data template (EnsureBuiltInExampleDataTemplateAsync), which reads
        // the example data and Metaverse repositories. Unset methods return null Tasks, so the restore finds no
        // template / object types and returns early, which is all these reset-flow tests need.
        var mockExampleDataRepo = new Mock<IExampleDataRepository>();
        var mockMetaverseRepo = new Mock<IMetaverseRepository>();
        // The reset also re-seeds the built-in Temporal Scope Reconciliation schedule (the wipe truncates
        // the Schedules table). An empty schedule list makes the seeder recreate it; configuration change
        // tracking is disabled so the audited create takes the lean, deterministic path.
        var mockSchedulingRepo = new Mock<ISchedulingRepository>();
        mockSchedulingRepo.Setup(s => s.GetAllSchedulesAsync()).ReturnsAsync(new List<Schedule>());
        mockSchedulingRepo.Setup(s => s.CreateScheduleAsync(It.IsAny<Schedule>())).Returns(Task.CompletedTask);

        mockActivityRepo.Setup(a => a.GetActivitiesAsync(
            It.IsAny<int>(),
            It.IsAny<int>(),
            It.IsAny<string?>(),
            It.IsAny<string?>(),
            It.IsAny<bool>(),
            It.IsAny<System.Guid?>(),
            It.IsAny<IEnumerable<ActivityTargetOperationType>?>(),
            It.IsAny<IEnumerable<ActivityOutcomeType>?>(),
            It.IsAny<IEnumerable<ActivityTargetType>?>(),
            It.IsAny<IEnumerable<ActivityStatus>?>(),
            It.IsAny<bool?>()))
            .ReturnsAsync(new PagedResultSet<Activity>
            {
                TotalResults = inProgressActivityCount,
                Results = new List<Activity>()
            });

        mockActivityRepo.Setup(a => a.CreateActivityAsync(It.IsAny<Activity>())).Returns(Task.CompletedTask);
        mockActivityRepo.Setup(a => a.UpdateActivityAsync(It.IsAny<Activity>())).Returns(Task.CompletedTask);

        mockSystemRepo.Setup(s => s.ResetSystemAsync(It.IsAny<bool>()))
            .ReturnsAsync(resetResult ?? new SystemResetResult());

        mockServiceSettingsRepo.Setup(s => s.GetServiceSettingsAsync()).ReturnsAsync(serviceSettings);
        mockServiceSettingsRepo.Setup(s => s.UpdateServiceSettingsAsync(It.IsAny<ServiceSettings>())).Returns(Task.CompletedTask);
        mockServiceSettingsRepo.Setup(s => s.GetSettingAsync(Constants.SettingKeys.ChangeTrackingConfigurationChangesEnabled))
            .ReturnsAsync(new ServiceSetting
            {
                Key = Constants.SettingKeys.ChangeTrackingConfigurationChangesEnabled,
                DisplayName = "Track configuration changes",
                ValueType = ServiceSettingValueType.Boolean,
                Value = "false"
            });

        mockRepository.Setup(r => r.Activity).Returns(mockActivityRepo.Object);
        mockRepository.Setup(r => r.System).Returns(mockSystemRepo.Object);
        mockRepository.Setup(r => r.ServiceSettings).Returns(mockServiceSettingsRepo.Object);
        mockRepository.Setup(r => r.ExampleData).Returns(mockExampleDataRepo.Object);
        mockRepository.Setup(r => r.Metaverse).Returns(mockMetaverseRepo.Object);
        mockRepository.Setup(r => r.Scheduling).Returns(mockSchedulingRepo.Object);

        var application = new JimApplication(mockRepository.Object);
        var controller = new SystemController(NullLogger<SystemController>.Instance, application)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    // Include a "sub" claim so the initiator triad has a principal id (required for
                    // activity attribution); without it the reset activity would fail validation.
                    User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                    {
                        new Claim("sub", Guid.NewGuid().ToString()),
                        new Claim(ClaimTypes.Name, "test-admin")
                    }, "test"))
                }
            }
        };

        return (controller, mockSystemRepo, mockActivityRepo, mockServiceSettingsRepo);
    }

    [Test]
    public async Task ResetAsync_WhenNoActivitiesInProgress_ReturnsOkWithResult()
    {
        var expectedResult = new SystemResetResult
        {
            ConnectedSystemsRemoved = 3,
            MetaverseObjectsRemoved = 42,
            AdministratorsRetained = 1
        };
        var (controller, _, _, _) = BuildController(inProgressActivityCount: 0, resetResult: expectedResult);

        var result = await controller.ResetAsync(new SystemResetRequest());

        Assert.That(result, Is.InstanceOf<OkObjectResult>());
        var ok = (OkObjectResult)result;
        Assert.That(ok.Value, Is.SameAs(expectedResult));
    }

    [Test]
    public async Task ResetAsync_DefaultRequest_CallsRepositoryResetPreservingAdministrators()
    {
        var (controller, systemRepo, _, _) = BuildController(inProgressActivityCount: 0);

        await controller.ResetAsync(new SystemResetRequest());

        systemRepo.Verify(s => s.ResetSystemAsync(false), Times.Once);
        systemRepo.Verify(s => s.ResetSystemAsync(true), Times.Never);
    }

    [Test]
    public async Task ResetAsync_NullBody_DefaultsToPreservingAdministrators()
    {
        var (controller, systemRepo, _, _) = BuildController(inProgressActivityCount: 0);

        await controller.ResetAsync(null);

        systemRepo.Verify(s => s.ResetSystemAsync(false), Times.Once);
    }

    [Test]
    public async Task ResetAsync_IncludeAdministrators_PassesFlagToRepository()
    {
        Environment.SetEnvironmentVariable(SsoInitialAdminVar, "admin@example.com");
        try
        {
            var (controller, systemRepo, _, _) = BuildController(inProgressActivityCount: 0);

            await controller.ResetAsync(new SystemResetRequest { IncludeAdministrators = true });

            systemRepo.Verify(s => s.ResetSystemAsync(true), Times.Once);
        }
        finally
        {
            Environment.SetEnvironmentVariable(SsoInitialAdminVar, null);
        }
    }

    [Test]
    public async Task ResetAsync_AlwaysCreatesAndCompletesResetActivity()
    {
        var (controller, _, activityRepo, _) = BuildController(inProgressActivityCount: 0);

        await controller.ResetAsync(new SystemResetRequest());

        activityRepo.Verify(a => a.CreateActivityAsync(It.Is<Activity>(act =>
            act.TargetType == ActivityTargetType.System &&
            act.TargetOperationType == ActivityTargetOperationType.Reset &&
            act.InitiatedByName == "test-admin")), Times.Once);
        // Scoped to the Reset activity: the reset also re-seeds the built-in schedule, whose own
        // Create activity completes through the same repository method.
        activityRepo.Verify(a => a.UpdateActivityAsync(It.Is<Activity>(act =>
            act.TargetOperationType == ActivityTargetOperationType.Reset &&
            act.Status == ActivityStatus.Complete)), Times.Once);
    }

    [Test]
    public async Task ResetAsync_AdvancesAuthenticationEpoch()
    {
        var settings = new ServiceSettings { SessionsValidFromUtc = null };
        var (controller, _, _, serviceSettingsRepo) = BuildController(inProgressActivityCount: 0, serviceSettings: settings);

        await controller.ResetAsync(new SystemResetRequest());

        serviceSettingsRepo.Verify(s => s.UpdateServiceSettingsAsync(It.Is<ServiceSettings>(ss => ss.SessionsValidFromUtc != null)), Times.Once);
        Assert.That(settings.SessionsValidFromUtc, Is.Not.Null);
    }

    [Test]
    public async Task ResetAsync_WhenActivitiesInProgress_Returns409()
    {
        var (controller, _, _, _) = BuildController(inProgressActivityCount: 2);

        var result = await controller.ResetAsync(new SystemResetRequest());

        Assert.That(result, Is.InstanceOf<ConflictObjectResult>());
        var conflict = (ConflictObjectResult)result;
        Assert.That(conflict.Value, Is.InstanceOf<ApiErrorResponse>());
    }

    [Test]
    public async Task ResetAsync_WhenActivitiesInProgress_DoesNotCallRepositoryReset()
    {
        var (controller, systemRepo, _, _) = BuildController(inProgressActivityCount: 1);

        await controller.ResetAsync(new SystemResetRequest());

        systemRepo.Verify(s => s.ResetSystemAsync(It.IsAny<bool>()), Times.Never);
    }

    [Test]
    public async Task ResetAsync_IncludeAdministratorsWithNoInitialAdmin_Returns409AndDoesNotReset()
    {
        Environment.SetEnvironmentVariable(SsoInitialAdminVar, null);
        var (controller, systemRepo, _, _) = BuildController(inProgressActivityCount: 0);

        var result = await controller.ResetAsync(new SystemResetRequest { IncludeAdministrators = true });

        Assert.That(result, Is.InstanceOf<ConflictObjectResult>());
        systemRepo.Verify(s => s.ResetSystemAsync(It.IsAny<bool>()), Times.Never);
    }

    [Test]
    public async Task ResetAsync_IncludeAdministratorsWithLockoutAcknowledged_Resets()
    {
        Environment.SetEnvironmentVariable(SsoInitialAdminVar, null);
        var (controller, systemRepo, _, _) = BuildController(inProgressActivityCount: 0);

        var result = await controller.ResetAsync(new SystemResetRequest
        {
            IncludeAdministrators = true,
            AcknowledgeAdministratorLockout = true
        });

        Assert.That(result, Is.InstanceOf<OkObjectResult>());
        systemRepo.Verify(s => s.ResetSystemAsync(true), Times.Once);
    }

    [Test]
    public async Task ResetAsync_FiltersActivityCheckToInProgressStatus()
    {
        var (controller, _, activityRepo, _) = BuildController(inProgressActivityCount: 0);

        await controller.ResetAsync(new SystemResetRequest());

        activityRepo.Verify(a => a.GetActivitiesAsync(
            It.IsAny<int>(),
            It.IsAny<int>(),
            It.IsAny<string?>(),
            It.IsAny<string?>(),
            It.IsAny<bool>(),
            It.IsAny<System.Guid?>(),
            It.IsAny<IEnumerable<ActivityTargetOperationType>?>(),
            It.IsAny<IEnumerable<ActivityOutcomeType>?>(),
            It.IsAny<IEnumerable<ActivityTargetType>?>(),
            It.Is<IEnumerable<ActivityStatus>?>(s => s != null && System.Linq.Enumerable.Contains(s, ActivityStatus.InProgress)),
            It.IsAny<bool?>()), Times.Once);
    }
}
