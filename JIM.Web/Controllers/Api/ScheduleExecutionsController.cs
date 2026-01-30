using Asp.Versioning;
using JIM.Application;
using JIM.Models.Scheduling;
using JIM.Models.Tasking;
using JIM.Web.Models.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace JIM.Web.Controllers.Api;

/// <summary>
/// API controller for managing schedule executions.
/// </summary>
/// <remarks>
/// Schedule executions track the progress of running schedules,
/// including which steps have completed and any errors encountered.
/// </remarks>
[Route("api/v{version:apiVersion}/schedule-executions")]
[ApiController]
[ApiVersion("1.0")]
[Authorize(Roles = "Administrator")]
[Produces("application/json")]
public class ScheduleExecutionsController(ILogger<ScheduleExecutionsController> logger, JimApplication application) : ControllerBase
{
    private readonly ILogger<ScheduleExecutionsController> _logger = logger;
    private readonly JimApplication _application = application;

    /// <summary>
    /// Gets schedule executions with pagination.
    /// </summary>
    /// <param name="scheduleId">Optional filter by schedule ID.</param>
    /// <param name="page">The page number (1-based).</param>
    /// <param name="pageSize">The number of items per page.</param>
    /// <param name="sortBy">Optional field to sort by (queuedAt, startedAt, completedAt, status).</param>
    /// <param name="sortDescending">Whether to sort in descending order (default: true for newest first).</param>
    /// <returns>A paginated list of schedule executions.</returns>
    [HttpGet(Name = "GetScheduleExecutions")]
    [ProducesResponseType(typeof(PaginatedResponse<ScheduleExecutionDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetAllAsync(
        [FromQuery] Guid? scheduleId = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] string? sortBy = null,
        [FromQuery] bool sortDescending = true)
    {
        _logger.LogTrace("Requested schedule executions page {Page}, size {PageSize}, scheduleId {ScheduleId}",
            page, pageSize, scheduleId);

        var result = await _application.Repository.Scheduling.GetScheduleExecutionsAsync(
            scheduleId, page, pageSize, sortBy, sortDescending);

        var dtos = result.Results.Select(ScheduleExecutionDto.FromEntity).ToList();

        return Ok(PaginatedResponse<ScheduleExecutionDto>.Create(dtos, result.TotalResults, result.CurrentPage, result.PageSize));
    }

    /// <summary>
    /// Gets a specific schedule execution by ID.
    /// </summary>
    /// <param name="id">The unique identifier of the execution.</param>
    /// <returns>The execution details including step progress.</returns>
    [HttpGet("{id:guid}", Name = "GetScheduleExecution")]
    [ProducesResponseType(typeof(ScheduleExecutionDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetByIdAsync(Guid id)
    {
        _logger.LogTrace("Requested schedule execution {ExecutionId}", id);

        var execution = await _application.Repository.Scheduling.GetScheduleExecutionWithScheduleAsync(id);
        if (execution == null)
        {
            return NotFound(new ApiErrorResponse { Message = $"Schedule execution not found: {id}" });
        }

        var dto = ScheduleExecutionDetailDto.FromEntity(execution);

        // Get worker tasks to populate step details
        var workerTasks = await _application.Repository.Tasking.GetWorkerTasksByScheduleExecutionAsync(id);

        // Get the schedule steps for step names and types
        if (execution.Schedule != null)
        {
            var steps = await _application.Scheduler.GetScheduleStepsAsync(execution.ScheduleId);
            var stepsByIndex = steps.GroupBy(s => s.StepIndex).ToDictionary(g => g.Key, g => g.First());

            // Build step status list
            for (var i = 0; i < execution.TotalSteps; i++)
            {
                var step = stepsByIndex.GetValueOrDefault(i);
                var task = workerTasks.FirstOrDefault(t => t.ScheduleStepIndex == i);

                dto.Steps.Add(new ScheduleExecutionStepDto
                {
                    StepIndex = i,
                    Name = step?.Name ?? $"Step {i + 1}",
                    StepType = step?.StepType ?? ScheduleStepType.RunProfile,
                    Status = GetStepStatus(task, i, execution.CurrentStepIndex, execution.Status),
                    TaskId = task?.Id,
                    StartedAt = null, // WorkerTask doesn't track start time directly
                    CompletedAt = null, // WorkerTask doesn't track completion time directly
                    ErrorMessage = null
                });
            }
        }

        return Ok(dto);
    }

    /// <summary>
    /// Cancels a running schedule execution.
    /// </summary>
    /// <param name="id">The unique identifier of the execution to cancel.</param>
    /// <returns>The updated execution status.</returns>
    [HttpPost("{id:guid}/cancel", Name = "CancelScheduleExecution")]
    [ProducesResponseType(typeof(ScheduleExecutionDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> CancelAsync(Guid id)
    {
        _logger.LogInformation("Cancelling schedule execution {ExecutionId}", id);

        var execution = await _application.Repository.Scheduling.GetScheduleExecutionAsync(id);
        if (execution == null)
        {
            return NotFound(new ApiErrorResponse { Message = $"Schedule execution not found: {id}" });
        }

        if (execution.Status != ScheduleExecutionStatus.Queued &&
            execution.Status != ScheduleExecutionStatus.InProgress)
        {
            return BadRequest(new ApiErrorResponse
            {
                Message = $"Cannot cancel execution with status: {execution.Status}"
            });
        }

        execution.Status = ScheduleExecutionStatus.Cancelled;
        execution.CompletedAt = DateTime.UtcNow;
        execution.ErrorMessage = "Cancelled by user";

        await _application.Repository.Scheduling.UpdateScheduleExecutionAsync(execution);

        // Request cancellation for any queued worker tasks for this execution
        var pendingTasks = await _application.Repository.Tasking.GetWorkerTasksByScheduleExecutionAsync(id);
        foreach (var task in pendingTasks.Where(t => t.Status == WorkerTaskStatus.Queued))
        {
            task.Status = WorkerTaskStatus.CancellationRequested;
            await _application.Repository.Tasking.UpdateWorkerTaskAsync(task);
        }

        _logger.LogInformation("Cancelled schedule execution {ExecutionId}", id);

        // Reload to get schedule name
        var updatedExecution = await _application.Repository.Scheduling.GetScheduleExecutionWithScheduleAsync(id);
        return Ok(ScheduleExecutionDto.FromEntity(updatedExecution!));
    }

    /// <summary>
    /// Gets currently active (running) schedule executions.
    /// </summary>
    /// <returns>A list of active executions.</returns>
    [HttpGet("active", Name = "GetActiveScheduleExecutions")]
    [ProducesResponseType(typeof(IEnumerable<ScheduleExecutionDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetActiveAsync()
    {
        _logger.LogTrace("Requested active schedule executions");

        var executions = await _application.Scheduler.GetActiveExecutionsAsync();
        var dtos = executions.Select(ScheduleExecutionDto.FromEntity).ToList();

        return Ok(dtos);
    }

    /// <summary>
    /// Determines the display status for a step based on its task and execution state.
    /// </summary>
    private static string GetStepStatus(
        WorkerTask? task,
        int stepIndex,
        int currentStepIndex,
        ScheduleExecutionStatus executionStatus)
    {
        if (task == null)
        {
            if (stepIndex < currentStepIndex)
            {
                return "Completed";
            }
            if (stepIndex == currentStepIndex && executionStatus == ScheduleExecutionStatus.InProgress)
            {
                return "Waiting";
            }
            return "Pending";
        }

        return task.Status switch
        {
            WorkerTaskStatus.Queued => "Queued",
            WorkerTaskStatus.Processing => "Processing",
            WorkerTaskStatus.CancellationRequested => "Cancelling",
            _ => "Unknown"
        };
    }
}
