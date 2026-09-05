// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Operations;
using JIM.Models.Scheduling;
using JIM.Models.Utility;
using JIM.Utilities;
using JIM.Web.Controllers.Api;
using JIM.Web.Models.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
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
        // The reset re-runs the whole built-in configuration pipeline (issue #916), so every repository it touches
        // needs a stand-in even though these tests are about the HTTP contract rather than the restore. Everything
        // is wired to "nothing exists yet, creating it succeeds", which is the cheapest state the pipeline runs to
        // completion from. Configuration change tracking is disabled below, so the audited creates take their lean
        // path and the rebaseline pass returns immediately. Extend this helper, not the individual tests, when the
        // pipeline gains a step.
        var mockExampleDataRepo = new Mock<IExampleDataRepository>();
        var mockMetaverseRepo = new Mock<IMetaverseRepository>();
        var mockSchedulingRepo = new Mock<ISchedulingRepository>();
        var mockSearchRepo = new Mock<ISearchRepository>();
        var mockSeedingRepo = new Mock<ISeedingRepository>();
        var mockSecurityRepo = new Mock<ISecurityRepository>();
        var mockConnectedSystemRepo = new Mock<IConnectedSystemRepository>();

        mockSchedulingRepo.Setup(s => s.GetAllSchedulesAsync()).ReturnsAsync(new List<Schedule>());
        mockSchedulingRepo.Setup(s => s.CreateScheduleAsync(It.IsAny<Schedule>())).Returns(Task.CompletedTask);

        mockSeedingRepo.Setup(s => s.SeedDataAsync(
            It.IsAny<List<MetaverseAttribute>>(),
            It.IsAny<List<MetaverseObjectType>>(),
            It.IsAny<List<JIM.Models.Search.PredefinedSearch>>(),
            It.IsAny<List<JIM.Models.ExampleData.ExampleDataSet>>(),
            It.IsAny<List<JIM.Models.ExampleData.ExampleDataTemplate>>())).Returns(Task.CompletedTask);
        mockSeedingRepo.Setup(s => s.SaveBuiltInSchemaChangesAsync(It.IsAny<List<MetaverseAttribute>>())).Returns(Task.CompletedTask);

        // The built-in schema sync throws rather than creating a missing built-in Object Type, so these two must be
        // present for it; SeedAsync only prepares them, and the SeedDataAsync stand-in above persists nothing.
        mockMetaverseRepo.Setup(m => m.GetBuiltInMetaverseObjectTypesForSchemaSyncAsync())
            .ReturnsAsync(new List<MetaverseObjectType>
            {
                new() { Id = 1, Name = Constants.BuiltInObjectTypes.User, BuiltIn = true },
                new() { Id = 2, Name = Constants.BuiltInObjectTypes.Group, BuiltIn = true }
            });
        mockMetaverseRepo.Setup(m => m.GetMetaverseAttributesForSchemaSyncAsync()).ReturnsAsync(new List<MetaverseAttribute>());

        mockSecurityRepo.Setup(s => s.CreateRoleAsync(It.IsAny<JIM.Models.Security.Role>()))
            .ReturnsAsync((JIM.Models.Security.Role role) => role);

        mockConnectedSystemRepo.Setup(c => c.CreateConnectorDefinitionAsync(It.IsAny<JIM.Models.Staging.ConnectorDefinition>()))
            .Returns(Task.CompletedTask);

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

        mockServiceSettingsRepo.Setup(s => s.GetAllSettingsAsync()).ReturnsAsync(new List<ServiceSetting>());
        mockServiceSettingsRepo.Setup(s => s.CreateSettingAsync(It.IsAny<ServiceSetting>())).Returns(Task.CompletedTask);
        mockServiceSettingsRepo.Setup(s => s.UpdateSettingAsync(It.IsAny<ServiceSetting>())).Returns(Task.CompletedTask);
        mockServiceSettingsRepo.Setup(s => s.SettingExistsAsync(It.IsAny<string>())).ReturnsAsync(false);
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
        mockRepository.Setup(r => r.Search).Returns(mockSearchRepo.Object);
        mockRepository.Setup(r => r.Seeding).Returns(mockSeedingRepo.Object);
        mockRepository.Setup(r => r.Security).Returns(mockSecurityRepo.Object);
        mockRepository.Setup(r => r.ConnectedSystems).Returns(mockConnectedSystemRepo.Object);

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

    #region GetServiceHealthAsync tests

    private static SystemController BuildHealthController(params ServiceHeartbeat[] heartbeats)
    {
        // The health read is a pure function of the newest heartbeat rows, so only the system repository needs a
        // stand-in; the reset helper above wires the whole seeding pipeline, which health never touches.
        var mockRepository = new Mock<IRepository>();
        var mockSystemRepo = new Mock<ISystemRepository>();
        mockSystemRepo.Setup(s => s.GetLatestServiceHeartbeatsAsync()).ReturnsAsync(new List<ServiceHeartbeat>(heartbeats));
        mockRepository.Setup(r => r.System).Returns(mockSystemRepo.Object);

        var application = new JimApplication(mockRepository.Object);
        return new SystemController(NullLogger<SystemController>.Instance, application)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
    }

    private static ServiceHeartbeat Heartbeat(JimService service, DateTime lastSeenAt, string? currentWork = null, DateTime? lastProgressAt = null) => new()
    {
        Service = service,
        InstanceId = $"host-a:{(int)service}",
        HostName = "host-a",
        Version = "1.2.3",
        StartedAt = lastSeenAt.AddHours(-1),
        LastSeenAt = lastSeenAt,
        CurrentWork = currentWork,
        CurrentWorkStartedAt = currentWork == null ? null : lastSeenAt.AddMinutes(-30),
        LastProgressAt = lastProgressAt,
        Detail = "queue: 0"
    };

    [Test]
    public async Task GetServiceHealthAsync_WithFreshHeartbeats_ReturnsOkWithEveryServiceHealthy()
    {
        var now = DateTime.UtcNow;
        var controller = BuildHealthController(
            Heartbeat(JimService.WorkerSync, now.AddSeconds(-2)),
            Heartbeat(JimService.WorkerPasswordDelivery, now.AddSeconds(-3)),
            Heartbeat(JimService.Scheduler, now.AddSeconds(-4)));

        var result = await controller.GetServiceHealthAsync();

        Assert.That(result, Is.InstanceOf<OkObjectResult>());
        var response = (ServiceHealthResponse)((OkObjectResult)result).Value!;
        Assert.That(response.Overall, Is.EqualTo(ServiceHealthStatus.Healthy));
        Assert.That(response.WebVersion, Is.EqualTo(JimVersion.Current));
        Assert.That(response.GeneratedAt, Is.EqualTo(now).Within(TimeSpan.FromSeconds(5)));
        Assert.That(response.Services.Select(s => s.Service), Is.EqualTo(new[]
        {
            JimService.WorkerSync, JimService.WorkerPasswordDelivery, JimService.Scheduler
        }));
        Assert.That(response.Services.Select(s => s.Status), Is.All.EqualTo(ServiceHealthStatus.Healthy));
        Assert.That(response.Services.Select(s => s.Condition), Is.All.EqualTo(ServiceHealthCondition.Heartbeating));
    }

    [Test]
    public async Task GetServiceHealthAsync_MapsEveryHeartbeatFieldOntoTheService()
    {
        var now = DateTime.UtcNow;
        var lastSeen = now.AddSeconds(-2);
        var lastProgress = now.AddMinutes(-1);
        var controller = BuildHealthController(
            Heartbeat(JimService.WorkerSync, lastSeen, "Full Import: Corporate Directory", lastProgress));

        var result = await controller.GetServiceHealthAsync();

        var worker = ((ServiceHealthResponse)((OkObjectResult)result).Value!).Services.Single(s => s.Service == JimService.WorkerSync);
        Assert.Multiple(() =>
        {
            Assert.That(worker.Status, Is.EqualTo(ServiceHealthStatus.Healthy));
            Assert.That(worker.Condition, Is.EqualTo(ServiceHealthCondition.Heartbeating));
            Assert.That(worker.Reason, Does.StartWith("Heartbeat"));
            Assert.That(worker.InstanceId, Is.EqualTo("host-a:1"));
            Assert.That(worker.HostName, Is.EqualTo("host-a"));
            Assert.That(worker.Version, Is.EqualTo("1.2.3"));
            Assert.That(worker.StartedAt, Is.EqualTo(lastSeen.AddHours(-1)));
            Assert.That(worker.LastSeenAt, Is.EqualTo(lastSeen));
            Assert.That(worker.CurrentWork, Is.EqualTo("Full Import: Corporate Directory"));
            Assert.That(worker.CurrentWorkStartedAt, Is.EqualTo(lastSeen.AddMinutes(-30)));
            Assert.That(worker.LastProgressAt, Is.EqualTo(lastProgress));
            Assert.That(worker.Detail, Is.EqualTo("queue: 0"));
        });
    }

    [Test]
    public async Task GetServiceHealthAsync_WithNoHeartbeats_ReportsEveryServiceNeverStartedAndOverallUnhealthy()
    {
        var controller = BuildHealthController();

        var result = await controller.GetServiceHealthAsync();

        var response = (ServiceHealthResponse)((OkObjectResult)result).Value!;
        Assert.That(response.Overall, Is.EqualTo(ServiceHealthStatus.Unhealthy));
        Assert.That(response.Services.Select(s => s.Service), Is.EqualTo(new[] { JimService.WorkerSync, JimService.WorkerPasswordDelivery, JimService.Scheduler }));
        Assert.That(response.Services.Select(s => s.Status), Is.All.EqualTo(ServiceHealthStatus.Unhealthy));
        Assert.That(response.Services.Select(s => s.Condition), Is.All.EqualTo(ServiceHealthCondition.NeverStarted));
        Assert.That(response.Services.Select(s => s.Reason), Is.All.EqualTo("Never started"));
        Assert.That(response.Services.Select(s => s.LastSeenAt), Is.All.Null);
    }

    [Test]
    public async Task GetServiceHealthAsync_OverallIsTheWorstStatusPresent()
    {
        var now = DateTime.UtcNow;
        var controller = BuildHealthController(
            Heartbeat(JimService.WorkerSync, now.AddSeconds(-2)),
            Heartbeat(JimService.WorkerPasswordDelivery, now.AddSeconds(-2)),
            Heartbeat(JimService.Scheduler, now.AddSeconds(-30)));

        var result = await controller.GetServiceHealthAsync();

        var response = (ServiceHealthResponse)((OkObjectResult)result).Value!;
        var scheduler = response.Services.Single(s => s.Service == JimService.Scheduler);
        Assert.That(scheduler.Status, Is.EqualTo(ServiceHealthStatus.Degraded));
        Assert.That(scheduler.Condition, Is.EqualTo(ServiceHealthCondition.HeartbeatOverdue));
        Assert.That(response.Overall, Is.EqualTo(ServiceHealthStatus.Degraded));
    }

    [Test]
    public async Task GetServiceHealthAsync_MarksTheResponseNoStore()
    {
        var controller = BuildHealthController();

        await controller.GetServiceHealthAsync();

        Assert.That(controller.Response.Headers.CacheControl.ToString(), Does.Contain("no-store"));
    }

    [Test]
    public async Task GetServiceHealthAsync_SerialisesEnumsAsStringNames()
    {
        var now = DateTime.UtcNow;
        var controller = BuildHealthController(Heartbeat(JimService.WorkerSync, now.AddSeconds(-2)));
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        ApiJsonConfiguration.Configure(options);

        var result = await controller.GetServiceHealthAsync();
        var json = JsonSerializer.Serialize(((OkObjectResult)result).Value, options);

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.That(root.GetProperty("overall").GetString(), Is.EqualTo("Unhealthy"));
        var services = root.GetProperty("services").EnumerateArray().ToList();
        Assert.That(services[0].GetProperty("service").GetString(), Is.EqualTo("WorkerSync"));
        Assert.That(services[0].GetProperty("status").GetString(), Is.EqualTo("Healthy"));
        Assert.That(services[0].GetProperty("condition").GetString(), Is.EqualTo("Heartbeating"));
        Assert.That(services[1].GetProperty("service").GetString(), Is.EqualTo("WorkerPasswordDelivery"));
        Assert.That(services[1].GetProperty("status").GetString(), Is.EqualTo("Unhealthy"));
        Assert.That(services[2].GetProperty("service").GetString(), Is.EqualTo("Scheduler"));
        Assert.That(services[2].GetProperty("status").GetString(), Is.EqualTo("Unhealthy"));
        Assert.That(services[2].GetProperty("condition").GetString(), Is.EqualTo("NeverStarted"));
    }

    [Test]
    public void GetServiceHealthAsync_IsAdministratorOnly()
    {
        // The controller carries the Administrator requirement; the action must not relax it. The unauthenticated
        // liveness endpoints live on HealthController and report only the web tier.
        var controllerAuthorise = typeof(SystemController).GetCustomAttribute<AuthorizeAttribute>();
        var action = typeof(SystemController).GetMethod(nameof(SystemController.GetServiceHealthAsync))!;

        Assert.That(controllerAuthorise?.Roles, Is.EqualTo("Administrator"));
        Assert.That(action.GetCustomAttribute<AllowAnonymousAttribute>(), Is.Null);
        Assert.That(action.GetCustomAttribute<HttpGetAttribute>()?.Template, Is.EqualTo("health"));
        Assert.That(action.GetCustomAttribute<HttpGetAttribute>()?.Name, Is.EqualTo("GetServiceHealth"));
    }

    #endregion
}
