// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using Asp.Versioning;
using JIM.Application;
using JIM.Models.Activities;
using JIM.Models.Scheduling;
using JIM.Models.Tasking;
using JIM.Utilities;
using JIM.Web.Models.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace JIM.Web.Controllers.Api;

/// <summary>
/// API controller for managing Schedule Executions.
/// </summary>
/// <remarks>
/// Schedule Executions track the progress of running schedules,
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
    /// List Schedule Executions
    /// </summary>
    /// <param name="scheduleId">Optional filter by schedule ID.</param>
    /// <param name="page">The page number (1-based).</param>
    /// <param name="pageSize">The number of items per page.</param>
    /// <param name="sortBy">Optional field to sort by (queuedAt, startedAt, completedAt, status).</param>
    /// <param name="sortDescending">Whether to sort in descending order (default: true for newest first).</param>
    /// <returns>A paginated list of Schedule Executions.</returns>
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
            (int)page, (int)pageSize, scheduleId?.ToString());

        var result = await _application.Scheduler.GetScheduleExecutionsAsync(
            scheduleId, page, pageSize, sortBy, sortDescending);

        var dtos = result.Results.Select(ScheduleExecutionDto.FromEntity).ToList();

        return Ok(PaginatedResponse<ScheduleExecutionDto>.Create(dtos, result.TotalResults, result.CurrentPage, result.PageSize));
    }

    /// <summary>
    /// Get Schedule Execution details
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

        // The per-step assembly lives in SchedulerServer so this endpoint and the portal's Schedule Execution
        // detail page cannot drift apart.
        var detail = await _application.Scheduler.GetScheduleExecutionDetailAsync(id);
        if (detail == null)
        {
            return NotFound(new ApiErrorResponse { Message = $"Schedule execution not found: {id}" });
        }

        var dto = ScheduleExecutionDetailDto.FromEntity(detail.Execution);
        dto.Steps.AddRange(detail.Steps.Select(ScheduleExecutionStepDto.FromModel));

        return Ok(dto);
    }

    /// <summary>
    /// Cancel a Schedule Execution
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

        var execution = await _application.Scheduler.GetScheduleExecutionAsync(id);
        if (execution == null)
        {
            return NotFound(new ApiErrorResponse { Message = $"Schedule execution not found: {id}" });
        }

        var cancelled = await _application.Scheduler.CancelScheduleExecutionAsync(id);
        if (!cancelled)
        {
            return BadRequest(new ApiErrorResponse
            {
                Message = $"Cannot cancel execution with status: {execution.Status}"
            });
        }

        _logger.LogInformation("Cancelled schedule execution {ExecutionId}", id);

        // Reload to get schedule name
        var updatedExecution = await _application.Scheduler.GetScheduleExecutionWithScheduleAsync(id);
        return Ok(ScheduleExecutionDto.FromEntity(updatedExecution!));
    }

    /// <summary>
    /// List active Schedule Executions
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

}
