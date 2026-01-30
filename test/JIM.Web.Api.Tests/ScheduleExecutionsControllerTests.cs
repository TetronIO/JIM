using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using JIM.Application;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Scheduling;
using JIM.Models.Tasking;
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
public class ScheduleExecutionsControllerTests
{
    private Mock<IRepository> _mockRepository = null!;
    private Mock<ISchedulingRepository> _mockSchedulingRepository = null!;
    private Mock<ITaskingRepository> _mockTaskingRepository = null!;
    private Mock<ILogger<ScheduleExecutionsController>> _mockLogger = null!;
    private JimApplication _application = null!;
    private ScheduleExecutionsController _controller = null!;

    [SetUp]
    public void SetUp()
    {
        _mockRepository = new Mock<IRepository>();
        _mockSchedulingRepository = new Mock<ISchedulingRepository>();
        _mockTaskingRepository = new Mock<ITaskingRepository>();
        _mockLogger = new Mock<ILogger<ScheduleExecutionsController>>();

        _mockRepository.Setup(r => r.Scheduling).Returns(_mockSchedulingRepository.Object);
        _mockRepository.Setup(r => r.Tasking).Returns(_mockTaskingRepository.Object);

        _application = new JimApplication(_mockRepository.Object);
        _controller = new ScheduleExecutionsController(_mockLogger.Object, _application);

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
        var pagedResult = new PagedResultSet<ScheduleExecution>
        {
            Results = new List<ScheduleExecution>(),
            TotalResults = 0,
            CurrentPage = 1,
            PageSize = 20
        };
        _mockSchedulingRepository.Setup(r => r.GetScheduleExecutionsAsync(null, 1, 20, null, true))
            .ReturnsAsync(pagedResult);

        var result = await _controller.GetAllAsync();

        Assert.That(result, Is.InstanceOf<OkObjectResult>());
    }

    [Test]
    public async Task GetAllAsync_ReturnsEmptyListWhenNoExecutionsAsync()
    {
        var pagedResult = new PagedResultSet<ScheduleExecution>
        {
            Results = new List<ScheduleExecution>(),
            TotalResults = 0,
            CurrentPage = 1,
            PageSize = 20
        };
        _mockSchedulingRepository.Setup(r => r.GetScheduleExecutionsAsync(null, 1, 20, null, true))
            .ReturnsAsync(pagedResult);

        var result = await _controller.GetAllAsync() as OkObjectResult;
        var response = result?.Value as PaginatedResponse<ScheduleExecutionDto>;

        Assert.That(response, Is.Not.Null);
        Assert.That(response!.Items.Count(), Is.EqualTo(0));
        Assert.That(response.TotalCount, Is.EqualTo(0));
    }

    [Test]
    public async Task GetAllAsync_ReturnsAllExecutionsAsync()
    {
        var scheduleId = Guid.NewGuid();
        var executions = new List<ScheduleExecution>
        {
            new() { Id = Guid.NewGuid(), ScheduleId = scheduleId, Status = ScheduleExecutionStatus.Completed, Schedule = new Schedule { Name = "Schedule 1" } },
            new() { Id = Guid.NewGuid(), ScheduleId = scheduleId, Status = ScheduleExecutionStatus.InProgress, Schedule = new Schedule { Name = "Schedule 1" } }
        };
        var pagedResult = new PagedResultSet<ScheduleExecution>
        {
            Results = executions,
            TotalResults = 2,
            CurrentPage = 1,
            PageSize = 20
        };
        _mockSchedulingRepository.Setup(r => r.GetScheduleExecutionsAsync(null, 1, 20, null, true))
            .ReturnsAsync(pagedResult);

        var result = await _controller.GetAllAsync() as OkObjectResult;
        var response = result?.Value as PaginatedResponse<ScheduleExecutionDto>;

        Assert.That(response, Is.Not.Null);
        Assert.That(response!.Items.Count(), Is.EqualTo(2));
        Assert.That(response.TotalCount, Is.EqualTo(2));
    }

    [Test]
    public async Task GetAllAsync_WithScheduleIdFilter_PassesFilterToRepositoryAsync()
    {
        var scheduleId = Guid.NewGuid();
        var pagedResult = new PagedResultSet<ScheduleExecution>
        {
            Results = new List<ScheduleExecution>(),
            TotalResults = 0,
            CurrentPage = 1,
            PageSize = 20
        };
        _mockSchedulingRepository.Setup(r => r.GetScheduleExecutionsAsync(scheduleId, 1, 20, null, true))
            .ReturnsAsync(pagedResult);

        await _controller.GetAllAsync(scheduleId: scheduleId);

        _mockSchedulingRepository.Verify(r => r.GetScheduleExecutionsAsync(scheduleId, 1, 20, null, true), Times.Once);
    }

    [Test]
    public async Task GetAllAsync_WithPaginationParameters_PassesParametersToRepositoryAsync()
    {
        var pagedResult = new PagedResultSet<ScheduleExecution>
        {
            Results = new List<ScheduleExecution>(),
            TotalResults = 0,
            CurrentPage = 2,
            PageSize = 10
        };
        _mockSchedulingRepository.Setup(r => r.GetScheduleExecutionsAsync(null, 2, 10, "queuedAt", false))
            .ReturnsAsync(pagedResult);

        await _controller.GetAllAsync(page: 2, pageSize: 10, sortBy: "queuedAt", sortDescending: false);

        _mockSchedulingRepository.Verify(r => r.GetScheduleExecutionsAsync(null, 2, 10, "queuedAt", false), Times.Once);
    }

    #endregion

    #region GetByIdAsync tests

    [Test]
    public async Task GetByIdAsync_WithValidId_ReturnsOkResultAsync()
    {
        var id = Guid.NewGuid();
        var scheduleId = Guid.NewGuid();
        var execution = new ScheduleExecution
        {
            Id = id,
            ScheduleId = scheduleId,
            Status = ScheduleExecutionStatus.InProgress,
            TotalSteps = 3,
            CurrentStepIndex = 1,
            Schedule = new Schedule { Id = scheduleId, Name = "Test Schedule", Steps = new List<ScheduleStep>() }
        };
        _mockSchedulingRepository.Setup(r => r.GetScheduleExecutionWithScheduleAsync(id))
            .ReturnsAsync(execution);
        _mockSchedulingRepository.Setup(r => r.GetScheduleStepsAsync(scheduleId))
            .ReturnsAsync(new List<ScheduleStep>());
        _mockTaskingRepository.Setup(r => r.GetWorkerTasksByScheduleExecutionAsync(id))
            .ReturnsAsync(new List<WorkerTask>());

        var result = await _controller.GetByIdAsync(id);

        Assert.That(result, Is.InstanceOf<OkObjectResult>());
    }

    [Test]
    public async Task GetByIdAsync_WithValidId_ReturnsCorrectExecutionAsync()
    {
        var id = Guid.NewGuid();
        var scheduleId = Guid.NewGuid();
        var execution = new ScheduleExecution
        {
            Id = id,
            ScheduleId = scheduleId,
            Status = ScheduleExecutionStatus.InProgress,
            TotalSteps = 3,
            CurrentStepIndex = 1,
            Schedule = new Schedule { Id = scheduleId, Name = "Test Schedule", Steps = new List<ScheduleStep>() }
        };
        _mockSchedulingRepository.Setup(r => r.GetScheduleExecutionWithScheduleAsync(id))
            .ReturnsAsync(execution);
        _mockSchedulingRepository.Setup(r => r.GetScheduleStepsAsync(scheduleId))
            .ReturnsAsync(new List<ScheduleStep>());
        _mockTaskingRepository.Setup(r => r.GetWorkerTasksByScheduleExecutionAsync(id))
            .ReturnsAsync(new List<WorkerTask>());

        var result = await _controller.GetByIdAsync(id) as OkObjectResult;
        var dto = result?.Value as ScheduleExecutionDetailDto;

        Assert.That(dto, Is.Not.Null);
        Assert.That(dto!.Id, Is.EqualTo(id));
        Assert.That(dto.Status, Is.EqualTo(ScheduleExecutionStatus.InProgress));
        Assert.That(dto.TotalSteps, Is.EqualTo(3));
        Assert.That(dto.CurrentStepIndex, Is.EqualTo(1));
    }

    [Test]
    public async Task GetByIdAsync_WithInvalidId_ReturnsNotFoundAsync()
    {
        var id = Guid.NewGuid();
        _mockSchedulingRepository.Setup(r => r.GetScheduleExecutionWithScheduleAsync(id))
            .ReturnsAsync((ScheduleExecution?)null);

        var result = await _controller.GetByIdAsync(id);

        Assert.That(result, Is.InstanceOf<NotFoundObjectResult>());
    }

    [Test]
    public async Task GetByIdAsync_IncludesStepStatusesAsync()
    {
        var id = Guid.NewGuid();
        var scheduleId = Guid.NewGuid();
        var step1Id = Guid.NewGuid();
        var step2Id = Guid.NewGuid();
        var execution = new ScheduleExecution
        {
            Id = id,
            ScheduleId = scheduleId,
            Status = ScheduleExecutionStatus.InProgress,
            TotalSteps = 2,
            CurrentStepIndex = 1,
            Schedule = new Schedule
            {
                Id = scheduleId,
                Name = "Test Schedule",
                Steps = new List<ScheduleStep>
                {
                    new() { Id = step1Id, StepIndex = 0, Name = "Step 1", StepType = ScheduleStepType.RunProfile },
                    new() { Id = step2Id, StepIndex = 1, Name = "Step 2", StepType = ScheduleStepType.PowerShell }
                }
            }
        };
        _mockSchedulingRepository.Setup(r => r.GetScheduleExecutionWithScheduleAsync(id))
            .ReturnsAsync(execution);
        _mockSchedulingRepository.Setup(r => r.GetScheduleStepsAsync(scheduleId))
            .ReturnsAsync(execution.Schedule.Steps);
        _mockTaskingRepository.Setup(r => r.GetWorkerTasksByScheduleExecutionAsync(id))
            .ReturnsAsync(new List<WorkerTask>());

        var result = await _controller.GetByIdAsync(id) as OkObjectResult;
        var dto = result?.Value as ScheduleExecutionDetailDto;

        Assert.That(dto, Is.Not.Null);
        Assert.That(dto!.Steps.Count, Is.EqualTo(2));
        Assert.That(dto.Steps[0].StepIndex, Is.EqualTo(0));
        Assert.That(dto.Steps[1].StepIndex, Is.EqualTo(1));
    }

    #endregion

    #region CancelAsync tests

    [Test]
    public async Task CancelAsync_WithValidQueuedExecution_ReturnsOkResultAsync()
    {
        var id = Guid.NewGuid();
        var scheduleId = Guid.NewGuid();
        var execution = new ScheduleExecution
        {
            Id = id,
            ScheduleId = scheduleId,
            Status = ScheduleExecutionStatus.Queued
        };
        var executionWithSchedule = new ScheduleExecution
        {
            Id = id,
            ScheduleId = scheduleId,
            Status = ScheduleExecutionStatus.Cancelled,
            Schedule = new Schedule { Name = "Test Schedule" }
        };

        _mockSchedulingRepository.Setup(r => r.GetScheduleExecutionAsync(id))
            .ReturnsAsync(execution);
        _mockSchedulingRepository.Setup(r => r.UpdateScheduleExecutionAsync(It.IsAny<ScheduleExecution>()))
            .Returns(Task.CompletedTask);
        _mockTaskingRepository.Setup(r => r.GetWorkerTasksByScheduleExecutionAsync(id))
            .ReturnsAsync(new List<WorkerTask>());
        _mockSchedulingRepository.Setup(r => r.GetScheduleExecutionWithScheduleAsync(id))
            .ReturnsAsync(executionWithSchedule);

        var result = await _controller.CancelAsync(id);

        Assert.That(result, Is.InstanceOf<OkObjectResult>());
    }

    [Test]
    public async Task CancelAsync_WithValidInProgressExecution_ReturnsOkResultAsync()
    {
        var id = Guid.NewGuid();
        var scheduleId = Guid.NewGuid();
        var execution = new ScheduleExecution
        {
            Id = id,
            ScheduleId = scheduleId,
            Status = ScheduleExecutionStatus.InProgress
        };
        var executionWithSchedule = new ScheduleExecution
        {
            Id = id,
            ScheduleId = scheduleId,
            Status = ScheduleExecutionStatus.Cancelled,
            Schedule = new Schedule { Name = "Test Schedule" }
        };

        _mockSchedulingRepository.Setup(r => r.GetScheduleExecutionAsync(id))
            .ReturnsAsync(execution);
        _mockSchedulingRepository.Setup(r => r.UpdateScheduleExecutionAsync(It.IsAny<ScheduleExecution>()))
            .Returns(Task.CompletedTask);
        _mockTaskingRepository.Setup(r => r.GetWorkerTasksByScheduleExecutionAsync(id))
            .ReturnsAsync(new List<WorkerTask>());
        _mockSchedulingRepository.Setup(r => r.GetScheduleExecutionWithScheduleAsync(id))
            .ReturnsAsync(executionWithSchedule);

        var result = await _controller.CancelAsync(id);

        Assert.That(result, Is.InstanceOf<OkObjectResult>());
    }

    [Test]
    public async Task CancelAsync_SetsStatusToCancelledAsync()
    {
        var id = Guid.NewGuid();
        var scheduleId = Guid.NewGuid();
        var execution = new ScheduleExecution
        {
            Id = id,
            ScheduleId = scheduleId,
            Status = ScheduleExecutionStatus.Queued
        };

        ScheduleExecution? updatedExecution = null;
        _mockSchedulingRepository.Setup(r => r.GetScheduleExecutionAsync(id))
            .ReturnsAsync(execution);
        _mockSchedulingRepository.Setup(r => r.UpdateScheduleExecutionAsync(It.IsAny<ScheduleExecution>()))
            .Callback<ScheduleExecution>(e => updatedExecution = e)
            .Returns(Task.CompletedTask);
        _mockTaskingRepository.Setup(r => r.GetWorkerTasksByScheduleExecutionAsync(id))
            .ReturnsAsync(new List<WorkerTask>());
        _mockSchedulingRepository.Setup(r => r.GetScheduleExecutionWithScheduleAsync(id))
            .ReturnsAsync(new ScheduleExecution { Id = id, ScheduleId = scheduleId, Status = ScheduleExecutionStatus.Cancelled, Schedule = new Schedule { Name = "Test" } });

        await _controller.CancelAsync(id);

        Assert.That(updatedExecution, Is.Not.Null);
        Assert.That(updatedExecution!.Status, Is.EqualTo(ScheduleExecutionStatus.Cancelled));
        Assert.That(updatedExecution.ErrorMessage, Is.EqualTo("Cancelled by user"));
        Assert.That(updatedExecution.CompletedAt, Is.Not.Null);
    }

    [Test]
    public async Task CancelAsync_WithInvalidId_ReturnsNotFoundAsync()
    {
        var id = Guid.NewGuid();

        _mockSchedulingRepository.Setup(r => r.GetScheduleExecutionAsync(id))
            .ReturnsAsync((ScheduleExecution?)null);

        var result = await _controller.CancelAsync(id);

        Assert.That(result, Is.InstanceOf<NotFoundObjectResult>());
    }

    [Test]
    public async Task CancelAsync_WithCompletedExecution_ReturnsBadRequestAsync()
    {
        var id = Guid.NewGuid();
        var execution = new ScheduleExecution
        {
            Id = id,
            Status = ScheduleExecutionStatus.Completed
        };

        _mockSchedulingRepository.Setup(r => r.GetScheduleExecutionAsync(id))
            .ReturnsAsync(execution);

        var result = await _controller.CancelAsync(id);

        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
        var badRequest = result as BadRequestObjectResult;
        var error = badRequest?.Value as ApiErrorResponse;
        Assert.That(error?.Message, Does.Contain("Cannot cancel"));
    }

    [Test]
    public async Task CancelAsync_WithFailedExecution_ReturnsBadRequestAsync()
    {
        var id = Guid.NewGuid();
        var execution = new ScheduleExecution
        {
            Id = id,
            Status = ScheduleExecutionStatus.Failed
        };

        _mockSchedulingRepository.Setup(r => r.GetScheduleExecutionAsync(id))
            .ReturnsAsync(execution);

        var result = await _controller.CancelAsync(id);

        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
    }

    [Test]
    public async Task CancelAsync_CancelsQueuedWorkerTasksAsync()
    {
        var id = Guid.NewGuid();
        var scheduleId = Guid.NewGuid();
        var taskId = Guid.NewGuid();
        var execution = new ScheduleExecution
        {
            Id = id,
            ScheduleId = scheduleId,
            Status = ScheduleExecutionStatus.InProgress
        };
        var queuedTask = new SynchronisationWorkerTask
        {
            Id = taskId,
            ScheduleExecutionId = id,
            Status = WorkerTaskStatus.Queued
        };

        WorkerTask? updatedTask = null;
        _mockSchedulingRepository.Setup(r => r.GetScheduleExecutionAsync(id))
            .ReturnsAsync(execution);
        _mockSchedulingRepository.Setup(r => r.UpdateScheduleExecutionAsync(It.IsAny<ScheduleExecution>()))
            .Returns(Task.CompletedTask);
        _mockTaskingRepository.Setup(r => r.GetWorkerTasksByScheduleExecutionAsync(id))
            .ReturnsAsync(new List<WorkerTask> { queuedTask });
        _mockTaskingRepository.Setup(r => r.UpdateWorkerTaskAsync(It.IsAny<WorkerTask>()))
            .Callback<WorkerTask>(t => updatedTask = t)
            .Returns(Task.CompletedTask);
        _mockSchedulingRepository.Setup(r => r.GetScheduleExecutionWithScheduleAsync(id))
            .ReturnsAsync(new ScheduleExecution { Id = id, ScheduleId = scheduleId, Status = ScheduleExecutionStatus.Cancelled, Schedule = new Schedule { Name = "Test" } });

        await _controller.CancelAsync(id);

        Assert.That(updatedTask, Is.Not.Null);
        Assert.That(updatedTask!.Status, Is.EqualTo(WorkerTaskStatus.CancellationRequested));
    }

    #endregion

    #region GetActiveAsync tests

    [Test]
    public async Task GetActiveAsync_ReturnsOkResultAsync()
    {
        _mockSchedulingRepository.Setup(r => r.GetActiveScheduleExecutionsAsync())
            .ReturnsAsync(new List<ScheduleExecution>());

        var result = await _controller.GetActiveAsync();

        Assert.That(result, Is.InstanceOf<OkObjectResult>());
    }

    [Test]
    public async Task GetActiveAsync_ReturnsEmptyListWhenNoActiveExecutionsAsync()
    {
        _mockSchedulingRepository.Setup(r => r.GetActiveScheduleExecutionsAsync())
            .ReturnsAsync(new List<ScheduleExecution>());

        var result = await _controller.GetActiveAsync() as OkObjectResult;
        var executions = result?.Value as List<ScheduleExecutionDto>;

        Assert.That(executions, Is.Not.Null);
        Assert.That(executions!.Count, Is.EqualTo(0));
    }

    [Test]
    public async Task GetActiveAsync_ReturnsActiveExecutionsAsync()
    {
        var executions = new List<ScheduleExecution>
        {
            new() { Id = Guid.NewGuid(), Status = ScheduleExecutionStatus.InProgress, Schedule = new Schedule { Name = "Schedule 1" } },
            new() { Id = Guid.NewGuid(), Status = ScheduleExecutionStatus.Queued, Schedule = new Schedule { Name = "Schedule 2" } }
        };
        _mockSchedulingRepository.Setup(r => r.GetActiveScheduleExecutionsAsync())
            .ReturnsAsync(executions);

        var result = await _controller.GetActiveAsync() as OkObjectResult;
        var dtos = result?.Value as List<ScheduleExecutionDto>;

        Assert.That(dtos, Is.Not.Null);
        Assert.That(dtos!.Count, Is.EqualTo(2));
    }

    #endregion
}
