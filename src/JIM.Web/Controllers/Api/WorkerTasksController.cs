// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using Asp.Versioning;
using JIM.Application;
using JIM.Models.Tasking.DTOs;
using JIM.Web.Models.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace JIM.Web.Controllers.Api;

/// <summary>
/// API controller for monitoring and cancelling queued and in-progress worker tasks.
/// </summary>
/// <remarks>
/// Worker tasks are ephemeral: once a task completes, its record is deleted (the associated
/// Activity is the durable audit record). This controller therefore only ever surfaces
/// queued, processing, or cancellation-requested tasks.
/// </remarks>
[Route("api/v{version:apiVersion}/worker-tasks")]
[ApiController]
[ApiVersion("1.0")]
[Authorize(Roles = "Administrator")]
[Produces("application/json")]
public class WorkerTasksController(ILogger<WorkerTasksController> logger, JimApplication application) : ControllerBase
{
    private readonly ILogger<WorkerTasksController> _logger = logger;
    private readonly JimApplication _application = application;

    /// <summary>
    /// List Worker Tasks
    /// </summary>
    /// <remarks>
    /// Returns lightweight header objects for every currently queued, processing, or
    /// cancellation-requested task.
    /// </remarks>
    /// <param name="page">Page number (1-based). Default: 1.</param>
    /// <param name="pageSize">Number of items per page (1-100). Default: 50.</param>
    /// <returns>A paginated set of Worker Task headers, most recent first.</returns>
    [HttpGet(Name = "GetWorkerTasks")]
    [ProducesResponseType(typeof(PaginatedResponse<WorkerTaskHeader>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetWorkerTasksAsync(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50)
    {
        if (page < 1) page = 1;
        if (pageSize < 1) pageSize = 50;
        if (pageSize > 100) pageSize = 100;

        _logger.LogTrace("Requested worker tasks (Page: {Page}, PageSize: {PageSize})", page, pageSize);

        var headers = await _application.Tasking.GetWorkerTaskHeadersAsync();
        var pagedHeaders = headers.Skip((page - 1) * pageSize).Take(pageSize).ToList();

        return Ok(PaginatedResponse<WorkerTaskHeader>.Create(pagedHeaders, headers.Count, page, pageSize));
    }

    /// <summary>
    /// Get a Worker Task
    /// </summary>
    /// <param name="id">The unique identifier of the Worker Task.</param>
    /// <returns>The Worker Task header.</returns>
    [HttpGet("{id:guid}", Name = "GetWorkerTask")]
    [ProducesResponseType(typeof(WorkerTaskHeader), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetWorkerTaskAsync(Guid id)
    {
        _logger.LogTrace("Requested worker task: {Id}", id);

        var headers = await _application.Tasking.GetWorkerTaskHeadersAsync();
        var header = headers.FirstOrDefault(h => h.Id == id);
        if (header == null)
            return NotFound(ApiErrorResponse.NotFound($"Worker task with ID {id} not found."));

        return Ok(header);
    }

    /// <summary>
    /// Cancel a Worker Task
    /// </summary>
    /// <remarks>
    /// For a task actively being processed, this signals the worker to stop and clean up; the
    /// task remains visible with CancellationRequested status until the worker picks this up.
    /// For a queued task, it is cancelled and removed immediately.
    /// </remarks>
    /// <param name="id">The unique identifier of the Worker Task to cancel.</param>
    /// <returns>202 Accepted; cancellation completes asynchronously.</returns>
    [HttpPost("{id:guid}/cancel", Name = "CancelWorkerTask")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> CancelWorkerTaskAsync(Guid id)
    {
        _logger.LogInformation("Requesting cancellation of worker task: {Id}", id);

        var headers = await _application.Tasking.GetWorkerTaskHeadersAsync();
        if (headers.All(h => h.Id != id))
            return NotFound(ApiErrorResponse.NotFound($"Worker task with ID {id} not found."));

        await _application.Tasking.RequestWorkerTaskCancellationAsync(id);
        _logger.LogInformation("Requested cancellation of worker task: {Id}", id);

        return Accepted();
    }
}
