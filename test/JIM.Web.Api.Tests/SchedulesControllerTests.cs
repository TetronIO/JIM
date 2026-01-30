using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using JIM.Application;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Scheduling;
using JIM.Models.Utility;
using JIM.Web.Controllers.Api;
using JIM.Web.Models.Api;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;

namespace JIM.Web.Api.Tests;

[TestFixture]
public class SchedulesControllerTests
{
    private Mock<IRepository> _mockRepository = null!;
    private Mock<ISchedulingRepository> _mockSchedulingRepository = null!;
    private Mock<IApiKeyRepository> _mockApiKeyRepository = null!;
    private Mock<ILogger<SchedulesController>> _mockLogger = null!;
    private JimApplication _application = null!;
    private SchedulesController _controller = null!;

    [SetUp]
    public void SetUp()
    {
        _mockRepository = new Mock<IRepository>();
        _mockSchedulingRepository = new Mock<ISchedulingRepository>();
        _mockApiKeyRepository = new Mock<IApiKeyRepository>();
        _mockLogger = new Mock<ILogger<SchedulesController>>();

        _mockRepository.Setup(r => r.Scheduling).Returns(_mockSchedulingRepository.Object);
        _mockRepository.Setup(r => r.ApiKeys).Returns(_mockApiKeyRepository.Object);

        _application = new JimApplication(_mockRepository.Object);
        _controller = new SchedulesController(_mockLogger.Object, _application);

        // Set up a default HTTP context with a user
        var claims = new List<Claim>
        {
            new("sub", Guid.NewGuid().ToString()),
            new("name", "Test User")
        };
        var identity = new ClaimsIdentity(claims, "Test");
        var principal = new ClaimsPrincipal(identity);

        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = principal }
        };
    }

    #region GetAllAsync tests

    [Test]
    public async Task GetAllAsync_ReturnsOkResultAsync()
    {
        var pagedResult = new PagedResultSet<Schedule>
        {
            Results = new List<Schedule>(),
            TotalResults = 0,
            CurrentPage = 1,
            PageSize = 20
        };
        _mockSchedulingRepository.Setup(r => r.GetSchedulesAsync(1, 20, null, null, false))
            .ReturnsAsync(pagedResult);

        var result = await _controller.GetAllAsync();

        Assert.That(result, Is.InstanceOf<OkObjectResult>());
    }

    [Test]
    public async Task GetAllAsync_ReturnsEmptyListWhenNoSchedulesAsync()
    {
        var pagedResult = new PagedResultSet<Schedule>
        {
            Results = new List<Schedule>(),
            TotalResults = 0,
            CurrentPage = 1,
            PageSize = 20
        };
        _mockSchedulingRepository.Setup(r => r.GetSchedulesAsync(1, 20, null, null, false))
            .ReturnsAsync(pagedResult);

        var result = await _controller.GetAllAsync() as OkObjectResult;
        var response = result?.Value as PaginatedResponse<ScheduleDto>;

        Assert.That(response, Is.Not.Null);
        Assert.That(response!.Items.Count(), Is.EqualTo(0));
        Assert.That(response.TotalCount, Is.EqualTo(0));
    }

    [Test]
    public async Task GetAllAsync_ReturnsAllSchedulesAsync()
    {
        var schedules = new List<Schedule>
        {
            new() { Id = Guid.NewGuid(), Name = "Schedule 1", Steps = new List<ScheduleStep>() },
            new() { Id = Guid.NewGuid(), Name = "Schedule 2", Steps = new List<ScheduleStep>() }
        };
        var pagedResult = new PagedResultSet<Schedule>
        {
            Results = schedules,
            TotalResults = 2,
            CurrentPage = 1,
            PageSize = 20
        };
        _mockSchedulingRepository.Setup(r => r.GetSchedulesAsync(1, 20, null, null, false))
            .ReturnsAsync(pagedResult);

        var result = await _controller.GetAllAsync() as OkObjectResult;
        var response = result?.Value as PaginatedResponse<ScheduleDto>;

        Assert.That(response, Is.Not.Null);
        Assert.That(response!.Items.Count(), Is.EqualTo(2));
        Assert.That(response.TotalCount, Is.EqualTo(2));
    }

    [Test]
    public async Task GetAllAsync_WithPaginationParameters_PassesParametersToRepositoryAsync()
    {
        var pagedResult = new PagedResultSet<Schedule>
        {
            Results = new List<Schedule>(),
            TotalResults = 0,
            CurrentPage = 2,
            PageSize = 10
        };
        _mockSchedulingRepository.Setup(r => r.GetSchedulesAsync(2, 10, "test", "name", true))
            .ReturnsAsync(pagedResult);

        await _controller.GetAllAsync(page: 2, pageSize: 10, search: "test", sortBy: "name", sortDescending: true);

        _mockSchedulingRepository.Verify(r => r.GetSchedulesAsync(2, 10, "test", "name", true), Times.Once);
    }

    #endregion

    #region GetByIdAsync tests

    [Test]
    public async Task GetByIdAsync_WithValidId_ReturnsOkResultAsync()
    {
        var id = Guid.NewGuid();
        var schedule = new Schedule
        {
            Id = id,
            Name = "Test Schedule",
            Steps = new List<ScheduleStep>()
        };
        _mockSchedulingRepository.Setup(r => r.GetScheduleWithStepsAsync(id))
            .ReturnsAsync(schedule);

        var result = await _controller.GetByIdAsync(id);

        Assert.That(result, Is.InstanceOf<OkObjectResult>());
    }

    [Test]
    public async Task GetByIdAsync_WithValidId_ReturnsCorrectScheduleAsync()
    {
        var id = Guid.NewGuid();
        var schedule = new Schedule
        {
            Id = id,
            Name = "Test Schedule",
            Description = "Test Description",
            Steps = new List<ScheduleStep>()
        };
        _mockSchedulingRepository.Setup(r => r.GetScheduleWithStepsAsync(id))
            .ReturnsAsync(schedule);

        var result = await _controller.GetByIdAsync(id) as OkObjectResult;
        var dto = result?.Value as ScheduleDetailDto;

        Assert.That(dto, Is.Not.Null);
        Assert.That(dto!.Id, Is.EqualTo(id));
        Assert.That(dto.Name, Is.EqualTo("Test Schedule"));
        Assert.That(dto.Description, Is.EqualTo("Test Description"));
    }

    [Test]
    public async Task GetByIdAsync_WithInvalidId_ReturnsNotFoundAsync()
    {
        var id = Guid.NewGuid();
        _mockSchedulingRepository.Setup(r => r.GetScheduleWithStepsAsync(id))
            .ReturnsAsync((Schedule?)null);

        var result = await _controller.GetByIdAsync(id);

        Assert.That(result, Is.InstanceOf<NotFoundObjectResult>());
    }

    [Test]
    public async Task GetByIdAsync_IncludesStepsInResponseAsync()
    {
        var id = Guid.NewGuid();
        var schedule = new Schedule
        {
            Id = id,
            Name = "Test Schedule",
            Steps = new List<ScheduleStep>
            {
                new() { Id = Guid.NewGuid(), StepIndex = 0, StepType = ScheduleStepType.RunProfile, ConnectedSystemId = 1, RunProfileId = 1 },
                new() { Id = Guid.NewGuid(), StepIndex = 1, StepType = ScheduleStepType.PowerShell, Name = "PowerShell Step", ScriptPath = "/scripts/test.ps1" }
            }
        };
        _mockSchedulingRepository.Setup(r => r.GetScheduleWithStepsAsync(id))
            .ReturnsAsync(schedule);

        var result = await _controller.GetByIdAsync(id) as OkObjectResult;
        var dto = result?.Value as ScheduleDetailDto;

        Assert.That(dto, Is.Not.Null);
        Assert.That(dto!.Steps.Count, Is.EqualTo(2));
        Assert.That(dto.Steps[0].StepType, Is.EqualTo(ScheduleStepType.RunProfile));
        Assert.That(dto.Steps[1].StepType, Is.EqualTo(ScheduleStepType.PowerShell));
    }

    #endregion

    #region CreateAsync tests

    [Test]
    public async Task CreateAsync_WithValidRequest_ReturnsCreatedResultAsync()
    {
        var request = new CreateScheduleRequest
        {
            Name = "New Schedule",
            TriggerType = ScheduleTriggerType.Manual,
            Steps = new List<ScheduleStepRequest>()
        };

        _mockSchedulingRepository.Setup(r => r.CreateScheduleAsync(It.IsAny<Schedule>()))
            .Returns(Task.CompletedTask);
        _mockSchedulingRepository.Setup(r => r.GetScheduleWithStepsAsync(It.IsAny<Guid>()))
            .ReturnsAsync((Guid id) => new Schedule { Id = id, Name = request.Name, Steps = new List<ScheduleStep>() });

        var result = await _controller.CreateAsync(request);

        Assert.That(result, Is.InstanceOf<CreatedAtRouteResult>());
    }

    [Test]
    public async Task CreateAsync_WithValidRequest_ReturnsScheduleDetailAsync()
    {
        var request = new CreateScheduleRequest
        {
            Name = "New Schedule",
            Description = "Test Description",
            TriggerType = ScheduleTriggerType.Manual,
            Steps = new List<ScheduleStepRequest>()
        };

        _mockSchedulingRepository.Setup(r => r.CreateScheduleAsync(It.IsAny<Schedule>()))
            .Returns(Task.CompletedTask);
        _mockSchedulingRepository.Setup(r => r.GetScheduleWithStepsAsync(It.IsAny<Guid>()))
            .ReturnsAsync((Guid id) => new Schedule { Id = id, Name = request.Name, Description = request.Description, Steps = new List<ScheduleStep>() });

        var result = await _controller.CreateAsync(request) as CreatedAtRouteResult;
        var dto = result?.Value as ScheduleDetailDto;

        Assert.That(dto, Is.Not.Null);
        Assert.That(dto!.Name, Is.EqualTo("New Schedule"));
    }

    [Test]
    public async Task CreateAsync_WithCronTriggerAndNoCronExpression_ReturnsBadRequestAsync()
    {
        var request = new CreateScheduleRequest
        {
            Name = "New Schedule",
            TriggerType = ScheduleTriggerType.Cron,
            CronExpression = null, // Missing cron expression
            Steps = new List<ScheduleStepRequest>()
        };

        var result = await _controller.CreateAsync(request);

        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
        var badRequest = result as BadRequestObjectResult;
        var error = badRequest?.Value as ApiErrorResponse;
        Assert.That(error?.Message, Does.Contain("Cron expression is required"));
    }

    [Test]
    public async Task CreateAsync_WithRunProfileStepMissingConnectedSystemId_ReturnsBadRequestAsync()
    {
        var request = new CreateScheduleRequest
        {
            Name = "New Schedule",
            TriggerType = ScheduleTriggerType.Manual,
            Steps = new List<ScheduleStepRequest>
            {
                new()
                {
                    StepIndex = 0,
                    StepType = ScheduleStepType.RunProfile,
                    RunProfileId = 1 // Missing ConnectedSystemId
                }
            }
        };

        var result = await _controller.CreateAsync(request);

        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
        var badRequest = result as BadRequestObjectResult;
        var error = badRequest?.Value as ApiErrorResponse;
        Assert.That(error?.Message, Does.Contain("Connected system ID is required"));
    }

    [Test]
    public async Task CreateAsync_WithRunProfileStepMissingRunProfileId_ReturnsBadRequestAsync()
    {
        var request = new CreateScheduleRequest
        {
            Name = "New Schedule",
            TriggerType = ScheduleTriggerType.Manual,
            Steps = new List<ScheduleStepRequest>
            {
                new()
                {
                    StepIndex = 0,
                    StepType = ScheduleStepType.RunProfile,
                    ConnectedSystemId = 1 // Missing RunProfileId
                }
            }
        };

        var result = await _controller.CreateAsync(request);

        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
        var badRequest = result as BadRequestObjectResult;
        var error = badRequest?.Value as ApiErrorResponse;
        Assert.That(error?.Message, Does.Contain("Run profile ID is required"));
    }

    [Test]
    public async Task CreateAsync_WithPowerShellStepMissingScriptPath_ReturnsBadRequestAsync()
    {
        var request = new CreateScheduleRequest
        {
            Name = "New Schedule",
            TriggerType = ScheduleTriggerType.Manual,
            Steps = new List<ScheduleStepRequest>
            {
                new()
                {
                    StepIndex = 0,
                    StepType = ScheduleStepType.PowerShell,
                    Name = "PS Step"
                    // Missing ScriptPath
                }
            }
        };

        var result = await _controller.CreateAsync(request);

        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
        var badRequest = result as BadRequestObjectResult;
        var error = badRequest?.Value as ApiErrorResponse;
        Assert.That(error?.Message, Does.Contain("Script path is required"));
    }

    [Test]
    public async Task CreateAsync_WithExecutableStepMissingExecutablePath_ReturnsBadRequestAsync()
    {
        var request = new CreateScheduleRequest
        {
            Name = "New Schedule",
            TriggerType = ScheduleTriggerType.Manual,
            Steps = new List<ScheduleStepRequest>
            {
                new()
                {
                    StepIndex = 0,
                    StepType = ScheduleStepType.Executable,
                    Name = "Exe Step"
                    // Missing ExecutablePath
                }
            }
        };

        var result = await _controller.CreateAsync(request);

        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
        var badRequest = result as BadRequestObjectResult;
        var error = badRequest?.Value as ApiErrorResponse;
        Assert.That(error?.Message, Does.Contain("Executable path is required"));
    }

    [Test]
    public async Task CreateAsync_WithSqlScriptStepMissingConnectionString_ReturnsBadRequestAsync()
    {
        var request = new CreateScheduleRequest
        {
            Name = "New Schedule",
            TriggerType = ScheduleTriggerType.Manual,
            Steps = new List<ScheduleStepRequest>
            {
                new()
                {
                    StepIndex = 0,
                    StepType = ScheduleStepType.SqlScript,
                    Name = "SQL Step",
                    SqlScriptPath = "/scripts/test.sql"
                    // Missing SqlConnectionString
                }
            }
        };

        var result = await _controller.CreateAsync(request);

        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
        var badRequest = result as BadRequestObjectResult;
        var error = badRequest?.Value as ApiErrorResponse;
        Assert.That(error?.Message, Does.Contain("Connection string is required"));
    }

    [Test]
    public async Task CreateAsync_WithValidRunProfileStep_CreatesScheduleAsync()
    {
        var request = new CreateScheduleRequest
        {
            Name = "New Schedule",
            TriggerType = ScheduleTriggerType.Manual,
            Steps = new List<ScheduleStepRequest>
            {
                new()
                {
                    StepIndex = 0,
                    StepType = ScheduleStepType.RunProfile,
                    ConnectedSystemId = 1,
                    RunProfileId = 1
                }
            }
        };

        Schedule? capturedSchedule = null;
        _mockSchedulingRepository.Setup(r => r.CreateScheduleAsync(It.IsAny<Schedule>()))
            .Callback<Schedule>(s => capturedSchedule = s)
            .Returns(Task.CompletedTask);
        _mockSchedulingRepository.Setup(r => r.GetScheduleWithStepsAsync(It.IsAny<Guid>()))
            .ReturnsAsync((Guid id) => new Schedule { Id = id, Name = request.Name, Steps = new List<ScheduleStep>() });

        await _controller.CreateAsync(request);

        Assert.That(capturedSchedule, Is.Not.Null);
        Assert.That(capturedSchedule!.Name, Is.EqualTo("New Schedule"));
        Assert.That(capturedSchedule.Steps.Count, Is.EqualTo(1));
        Assert.That(capturedSchedule.Steps[0].ConnectedSystemId, Is.EqualTo(1));
        Assert.That(capturedSchedule.Steps[0].RunProfileId, Is.EqualTo(1));
    }

    #endregion

    #region UpdateAsync tests

    [Test]
    public async Task UpdateAsync_WithValidRequest_ReturnsOkResultAsync()
    {
        var id = Guid.NewGuid();
        var existingSchedule = new Schedule { Id = id, Name = "Old Name", Steps = new List<ScheduleStep>() };
        var request = new UpdateScheduleRequest
        {
            Name = "New Name",
            TriggerType = ScheduleTriggerType.Manual,
            Steps = new List<ScheduleStepRequest>()
        };

        _mockSchedulingRepository.Setup(r => r.GetScheduleAsync(id))
            .ReturnsAsync(existingSchedule);
        _mockSchedulingRepository.Setup(r => r.UpdateScheduleAsync(It.IsAny<Schedule>()))
            .Returns(Task.CompletedTask);
        _mockSchedulingRepository.Setup(r => r.GetScheduleStepsAsync(id))
            .ReturnsAsync(new List<ScheduleStep>());
        _mockSchedulingRepository.Setup(r => r.GetScheduleWithStepsAsync(id))
            .ReturnsAsync(new Schedule { Id = id, Name = "New Name", Steps = new List<ScheduleStep>() });

        var result = await _controller.UpdateAsync(id, request);

        Assert.That(result, Is.InstanceOf<OkObjectResult>());
    }

    [Test]
    public async Task UpdateAsync_WithInvalidId_ReturnsNotFoundAsync()
    {
        var id = Guid.NewGuid();
        var request = new UpdateScheduleRequest { Name = "New Name", Steps = new List<ScheduleStepRequest>() };

        _mockSchedulingRepository.Setup(r => r.GetScheduleAsync(id))
            .ReturnsAsync((Schedule?)null);

        var result = await _controller.UpdateAsync(id, request);

        Assert.That(result, Is.InstanceOf<NotFoundObjectResult>());
    }

    [Test]
    public async Task UpdateAsync_UpdatesSchedulePropertiesAsync()
    {
        var id = Guid.NewGuid();
        var existingSchedule = new Schedule { Id = id, Name = "Old Name", Steps = new List<ScheduleStep>() };
        var request = new UpdateScheduleRequest
        {
            Name = "New Name",
            Description = "New Description",
            TriggerType = ScheduleTriggerType.Cron,
            CronExpression = "0 6 * * 1-5",
            IsEnabled = true,
            Steps = new List<ScheduleStepRequest>()
        };

        Schedule? updatedSchedule = null;
        _mockSchedulingRepository.Setup(r => r.GetScheduleAsync(id))
            .ReturnsAsync(existingSchedule);
        _mockSchedulingRepository.Setup(r => r.UpdateScheduleAsync(It.IsAny<Schedule>()))
            .Callback<Schedule>(s => updatedSchedule = s)
            .Returns(Task.CompletedTask);
        _mockSchedulingRepository.Setup(r => r.GetScheduleStepsAsync(id))
            .ReturnsAsync(new List<ScheduleStep>());
        _mockSchedulingRepository.Setup(r => r.GetScheduleWithStepsAsync(id))
            .ReturnsAsync(new Schedule { Id = id, Name = "New Name", Steps = new List<ScheduleStep>() });

        await _controller.UpdateAsync(id, request);

        Assert.That(updatedSchedule, Is.Not.Null);
        Assert.That(updatedSchedule!.Name, Is.EqualTo("New Name"));
        Assert.That(updatedSchedule.Description, Is.EqualTo("New Description"));
        Assert.That(updatedSchedule.TriggerType, Is.EqualTo(ScheduleTriggerType.Cron));
        Assert.That(updatedSchedule.CronExpression, Is.EqualTo("0 6 * * 1-5"));
        Assert.That(updatedSchedule.IsEnabled, Is.True);
    }

    [Test]
    public async Task UpdateAsync_DeletesStepsNotInRequestAsync()
    {
        var id = Guid.NewGuid();
        var existingStepId = Guid.NewGuid();
        var existingSchedule = new Schedule { Id = id, Name = "Schedule", Steps = new List<ScheduleStep>() };
        var existingStep = new ScheduleStep { Id = existingStepId, ScheduleId = id, StepIndex = 0 };
        var request = new UpdateScheduleRequest
        {
            Name = "Schedule",
            TriggerType = ScheduleTriggerType.Manual,
            Steps = new List<ScheduleStepRequest>() // No steps - existing should be deleted
        };

        _mockSchedulingRepository.Setup(r => r.GetScheduleAsync(id))
            .ReturnsAsync(existingSchedule);
        _mockSchedulingRepository.Setup(r => r.UpdateScheduleAsync(It.IsAny<Schedule>()))
            .Returns(Task.CompletedTask);
        _mockSchedulingRepository.Setup(r => r.GetScheduleStepsAsync(id))
            .ReturnsAsync(new List<ScheduleStep> { existingStep });
        _mockSchedulingRepository.Setup(r => r.DeleteScheduleStepAsync(It.IsAny<ScheduleStep>()))
            .Returns(Task.CompletedTask);
        _mockSchedulingRepository.Setup(r => r.GetScheduleWithStepsAsync(id))
            .ReturnsAsync(new Schedule { Id = id, Name = "Schedule", Steps = new List<ScheduleStep>() });

        await _controller.UpdateAsync(id, request);

        _mockSchedulingRepository.Verify(r => r.DeleteScheduleStepAsync(It.Is<ScheduleStep>(s => s.Id == existingStepId)), Times.Once);
    }

    #endregion

    #region DeleteAsync tests

    [Test]
    public async Task DeleteAsync_WithValidId_ReturnsNoContentAsync()
    {
        var id = Guid.NewGuid();
        var existingSchedule = new Schedule { Id = id, Name = "Test Schedule" };

        _mockSchedulingRepository.Setup(r => r.GetScheduleAsync(id))
            .ReturnsAsync(existingSchedule);
        _mockSchedulingRepository.Setup(r => r.DeleteScheduleAsync(It.IsAny<Schedule>()))
            .Returns(Task.CompletedTask);

        var result = await _controller.DeleteAsync(id);

        Assert.That(result, Is.InstanceOf<NoContentResult>());
    }

    [Test]
    public async Task DeleteAsync_WithInvalidId_ReturnsNotFoundAsync()
    {
        var id = Guid.NewGuid();

        _mockSchedulingRepository.Setup(r => r.GetScheduleAsync(id))
            .ReturnsAsync((Schedule?)null);

        var result = await _controller.DeleteAsync(id);

        Assert.That(result, Is.InstanceOf<NotFoundObjectResult>());
    }

    [Test]
    public async Task DeleteAsync_CallsRepositoryDeleteAsync()
    {
        var id = Guid.NewGuid();
        var existingSchedule = new Schedule { Id = id, Name = "Test Schedule" };

        _mockSchedulingRepository.Setup(r => r.GetScheduleAsync(id))
            .ReturnsAsync(existingSchedule);
        _mockSchedulingRepository.Setup(r => r.DeleteScheduleAsync(It.IsAny<Schedule>()))
            .Returns(Task.CompletedTask);

        await _controller.DeleteAsync(id);

        _mockSchedulingRepository.Verify(r => r.DeleteScheduleAsync(It.Is<Schedule>(s => s.Id == id)), Times.Once);
    }

    #endregion

    #region EnableAsync tests

    [Test]
    public async Task EnableAsync_WithValidId_ReturnsOkResultAsync()
    {
        var id = Guid.NewGuid();
        var schedule = new Schedule { Id = id, Name = "Test Schedule", IsEnabled = false };

        _mockSchedulingRepository.Setup(r => r.GetScheduleAsync(id))
            .ReturnsAsync(schedule);
        _mockSchedulingRepository.Setup(r => r.UpdateScheduleAsync(It.IsAny<Schedule>()))
            .Returns(Task.CompletedTask);

        var result = await _controller.EnableAsync(id);

        Assert.That(result, Is.InstanceOf<OkObjectResult>());
    }

    [Test]
    public async Task EnableAsync_SetsIsEnabledToTrueAsync()
    {
        var id = Guid.NewGuid();
        var schedule = new Schedule { Id = id, Name = "Test Schedule", IsEnabled = false };

        _mockSchedulingRepository.Setup(r => r.GetScheduleAsync(id))
            .ReturnsAsync(schedule);

        Schedule? updatedSchedule = null;
        _mockSchedulingRepository.Setup(r => r.UpdateScheduleAsync(It.IsAny<Schedule>()))
            .Callback<Schedule>(s => updatedSchedule = s)
            .Returns(Task.CompletedTask);

        await _controller.EnableAsync(id);

        Assert.That(updatedSchedule, Is.Not.Null);
        Assert.That(updatedSchedule!.IsEnabled, Is.True);
    }

    [Test]
    public async Task EnableAsync_WithInvalidId_ReturnsNotFoundAsync()
    {
        var id = Guid.NewGuid();

        _mockSchedulingRepository.Setup(r => r.GetScheduleAsync(id))
            .ReturnsAsync((Schedule?)null);

        var result = await _controller.EnableAsync(id);

        Assert.That(result, Is.InstanceOf<NotFoundObjectResult>());
    }

    #endregion

    #region DisableAsync tests

    [Test]
    public async Task DisableAsync_WithValidId_ReturnsOkResultAsync()
    {
        var id = Guid.NewGuid();
        var schedule = new Schedule { Id = id, Name = "Test Schedule", IsEnabled = true };

        _mockSchedulingRepository.Setup(r => r.GetScheduleAsync(id))
            .ReturnsAsync(schedule);
        _mockSchedulingRepository.Setup(r => r.UpdateScheduleAsync(It.IsAny<Schedule>()))
            .Returns(Task.CompletedTask);

        var result = await _controller.DisableAsync(id);

        Assert.That(result, Is.InstanceOf<OkObjectResult>());
    }

    [Test]
    public async Task DisableAsync_SetsIsEnabledToFalseAsync()
    {
        var id = Guid.NewGuid();
        var schedule = new Schedule { Id = id, Name = "Test Schedule", IsEnabled = true };

        _mockSchedulingRepository.Setup(r => r.GetScheduleAsync(id))
            .ReturnsAsync(schedule);

        Schedule? updatedSchedule = null;
        _mockSchedulingRepository.Setup(r => r.UpdateScheduleAsync(It.IsAny<Schedule>()))
            .Callback<Schedule>(s => updatedSchedule = s)
            .Returns(Task.CompletedTask);

        await _controller.DisableAsync(id);

        Assert.That(updatedSchedule, Is.Not.Null);
        Assert.That(updatedSchedule!.IsEnabled, Is.False);
    }

    [Test]
    public async Task DisableAsync_WithInvalidId_ReturnsNotFoundAsync()
    {
        var id = Guid.NewGuid();

        _mockSchedulingRepository.Setup(r => r.GetScheduleAsync(id))
            .ReturnsAsync((Schedule?)null);

        var result = await _controller.DisableAsync(id);

        Assert.That(result, Is.InstanceOf<NotFoundObjectResult>());
    }

    #endregion

    #region RunAsync tests

    [Test]
    public async Task RunAsync_WithInvalidId_ReturnsNotFoundAsync()
    {
        var id = Guid.NewGuid();

        _mockSchedulingRepository.Setup(r => r.GetScheduleWithStepsAsync(id))
            .ReturnsAsync((Schedule?)null);

        var result = await _controller.RunAsync(id);

        Assert.That(result, Is.InstanceOf<NotFoundObjectResult>());
    }

    [Test]
    public async Task RunAsync_WithNoSteps_ReturnsBadRequestAsync()
    {
        var id = Guid.NewGuid();
        var schedule = new Schedule
        {
            Id = id,
            Name = "Test Schedule",
            Steps = new List<ScheduleStep>() // No steps
        };

        _mockSchedulingRepository.Setup(r => r.GetScheduleWithStepsAsync(id))
            .ReturnsAsync(schedule);

        var result = await _controller.RunAsync(id);

        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
        var badRequest = result as BadRequestObjectResult;
        var error = badRequest?.Value as ApiErrorResponse;
        Assert.That(error?.Message, Does.Contain("no steps"));
    }

    // Note: Tests for successful schedule execution (RunAsync_WithValidId_ReturnsAcceptedResultAsync,
    // RunAsync_ReturnsExecutionIdAsync) require extensive mocking of the full execution pipeline
    // including ConnectedSystemRepository, TaskingRepository, and ActivityRepository.
    // These are better tested via integration tests.

    #endregion
}
