// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using Asp.Versioning;
using JIM.Application;
using JIM.Models.Activities;
using JIM.Models.Activities.DTOs;
using JIM.Utilities;
using JIM.Web.Models;
using JIM.Web.Models.Api;
using JIM.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace JIM.Web.Controllers.Api;

/// <summary>
/// API controller for viewing Activity history and monitoring sync operations.
/// </summary>
/// <remarks>
/// Activities track all operations performed in JIM, including sync runs, data generation,
/// certificate management, and other administrative actions. This controller provides
/// read-only access to the Activity history for monitoring and audit purposes.
/// </remarks>
[Route("api/v{version:apiVersion}/[controller]")]
[ApiController]
[ApiVersion("1.0")]
[Authorize(Roles = "Administrator")]
[Produces("application/json")]
public class ActivitiesController(ILogger<ActivitiesController> logger, JimApplication application, IActivityEtaTracker etaTracker) : ControllerBase
{
    private readonly ILogger<ActivitiesController> _logger = logger;
    private readonly JimApplication _application = application;
    private readonly IActivityEtaTracker _etaTracker = etaTracker;

    /// <summary>
    /// List Activities
    /// </summary>
    /// <param name="pagination">Pagination parameters (page, pageSize, sortBy, sortDirection, filter).</param>
    /// <param name="search">Optional search query to filter by target name or type.</param>
    /// <param name="targetType">Optional filter for target types (repeat the query parameter for multiple values,
    /// e.g. <c>?targetType=Authentication&amp;targetType=ConnectedSystem</c>; additive/OR within the filter). Use
    /// <c>Authentication</c> to poll security audit events (interactive sign-in success/failure, API key
    /// authentication failure) for SIEM integration. An unparseable value returns 400.</param>
    /// <param name="initiatorType">Optional filter for initiator types (User, ApiKey, System, Anonymous; repeat the
    /// query parameter for multiple values; additive/OR within the filter). An unparseable value returns 400.</param>
    /// <param name="operation">Optional filter for the operation the Activity performed (Create, Read, Update, Delete,
    /// Clear, Execute, ImportHierarchy and so on; repeat the query parameter for multiple values; additive/OR within
    /// the filter). An unparseable value returns 400.</param>
    /// <param name="outcome">Optional filter for Activities that recorded at least one of the given outcomes (Added,
    /// Updated, Deleted, Projected, Joined, AttributeFlows, Disconnected, DriftCorrections, Provisioned, Exported,
    /// Deprovisioned, Created, PendingExports, Errors; repeat the query parameter for multiple values; additive/OR
    /// within the filter). An unparseable value returns 400.</param>
    /// <param name="status">Optional filter for Activity statuses (InProgress, Complete, CompleteWithWarning,
    /// CompleteWithError, FailedWithError, Cancelled; repeat the query parameter for multiple values; additive/OR
    /// within the filter). An unparseable value returns 400.</param>
    /// <param name="initiatedById">Optional filter to the exact principal (Metaverse Object or API key) that
    /// initiated the Activity. Use <c>initiatedBy</c> to match on the recorded name instead.</param>
    /// <param name="hasChildActivities">Optional filter: <c>true</c> returns only Activities that have child
    /// Activities, <c>false</c> only those that have none, omitted returns both.</param>
    /// <param name="createdFrom">Optional inclusive lower bound on the Activity's creation time (UTC).</param>
    /// <param name="createdTo">Optional inclusive upper bound on the Activity's creation time (UTC).</param>
    /// <param name="connectedSystem">Optional filter for Connected System names, matched against the Activity's target
    /// context (repeat the query parameter for multiple values; additive/OR within the filter). Exact match.</param>
    /// <param name="runProfile">Optional filter for Run Profile names, matched against the Activity's target name
    /// (repeat the query parameter for multiple values; additive/OR within the filter). Exact match.</param>
    /// <param name="initiatedBy">Optional case-insensitive partial match on the initiator's recorded name. Distinct
    /// from <c>initiatedById</c>, which matches an exact principal.</param>
    /// <param name="scheduledOnly">Optional filter: <c>true</c> returns only Activities a Schedule produced,
    /// <c>false</c> only those no Schedule produced, omitted returns both.</param>
    /// <param name="scheduleId">Optional filter for the ids of the Schedules that produced the Activities (repeat the
    /// query parameter for multiple values; additive/OR within the filter).</param>
    /// <returns>A paginated list of Activity headers.</returns>
    /// <response code="200">Returns the paginated list of Activities.</response>
    /// <response code="400">If a filter value cannot be parsed.</response>
    /// <response code="401">If the user is not authenticated.</response>
    [HttpGet(Name = "GetActivities")]
    [ProducesResponseType(typeof(PaginatedResponse<ActivityHeader>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetActivitiesAsync(
        [FromQuery] PaginationRequest pagination,
        [FromQuery] string? search = null,
        [FromQuery] List<ActivityTargetType>? targetType = null,
        [FromQuery] List<ActivityInitiatorType>? initiatorType = null,
        [FromQuery] List<ActivityTargetOperationType>? operation = null,
        [FromQuery] List<ActivityOutcomeType>? outcome = null,
        [FromQuery] List<ActivityStatus>? status = null,
        [FromQuery] Guid? initiatedById = null,
        [FromQuery] bool? hasChildActivities = null,
        [FromQuery] DateTime? createdFrom = null,
        [FromQuery] DateTime? createdTo = null,
        [FromQuery] List<string>? connectedSystem = null,
        [FromQuery] List<string>? runProfile = null,
        [FromQuery] string? initiatedBy = null,
        [FromQuery] bool? scheduledOnly = null,
        [FromQuery] List<Guid>? scheduleId = null)
    {
        _logger.LogDebug("Getting activities (Page: {Page}, PageSize: {PageSize}, Search: {Search})",
            pagination.Page, pagination.PageSize, LogSanitiser.Sanitise(search));

        // Map API pagination to application layer
        var sortBy = MapSortBy(pagination.SortBy);
        var sortDescending = pagination.IsDescending;

        // Every filter is optional and they combine with AND; an omitted or empty one filters nothing.
        var result = await _application.Activities.GetActivitiesAsync(
            page: pagination.Page,
            pageSize: pagination.PageSize,
            searchQuery: search,
            sortBy: sortBy,
            sortDescending: sortDescending,
            initiatedById: initiatedById,
            operationFilter: operation,
            outcomeFilter: outcome,
            typeFilter: targetType,
            statusFilter: status,
            hasChildActivities: hasChildActivities,
            initiatorTypeFilter: initiatorType,
            createdFrom: createdFrom,
            createdTo: createdTo,
            connectedSystemFilter: connectedSystem,
            runProfileFilter: runProfile,
            initiatedByFilter: initiatedBy,
            initiatedBySchedule: scheduledOnly,
            scheduleFilter: scheduleId);

        var headers = result.Results.Select(a => ActivityHeader.FromEntity(a));

        var response = new PaginatedResponse<ActivityHeader>
        {
            Items = headers,
            TotalCount = result.TotalResults,
            Page = result.CurrentPage,
            PageSize = result.PageSize
        };

        return Ok(response);
    }

    /// <summary>
    /// Get Activity details
    /// </summary>
    /// <param name="id">The unique identifier (GUID) of the Activity.</param>
    /// <returns>The Activity details including error information and execution statistics.</returns>
    /// <response code="200">Returns the Activity details.</response>
    /// <response code="404">If the Activity is not found.</response>
    /// <response code="401">If the user is not authenticated.</response>
    [HttpGet("{id:guid}", Name = "GetActivity")]
    [ProducesResponseType(typeof(ActivityDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetActivityAsync(Guid id)
    {
        _logger.LogDebug("Getting activity: {Id}", id);

        var activity = await _application.Activities.GetActivityAsync(id);
        if (activity == null)
        {
            return NotFound(ApiErrorResponse.NotFound($"Activity with ID {id} not found."));
        }

        // Get execution stats and the run's steps if this is a Run Profile activity
        ActivityRunProfileExecutionStats? stats = null;
        List<ActivityPhase>? phases = null;
        if (activity.TargetType == ActivityTargetType.ConnectedSystemRunProfile)
        {
            stats = await _application.Activities.GetActivityRunProfileExecutionStatsAsync(id);
            phases = await _application.Activities.GetActivityPhasesAsync(id);
        }

        return Ok(ActivityDetailDto.FromEntity(activity, stats, phases));
    }

    /// <summary>
    /// Get Run Profile execution statistics
    /// </summary>
    /// <param name="id">The unique identifier (GUID) of the Activity.</param>
    /// <returns>The execution statistics for the Activity.</returns>
    /// <response code="200">Returns the execution statistics.</response>
    /// <response code="404">If the Activity is not found.</response>
    /// <response code="400">If the Activity is not a Run Profile Activity.</response>
    /// <response code="401">If the user is not authenticated.</response>
    [HttpGet("{id:guid}/stats", Name = "GetActivityStats")]
    [ProducesResponseType(typeof(ActivityRunProfileExecutionStatsDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetActivityStatsAsync(Guid id)
    {
        _logger.LogDebug("Getting activity stats: {Id}", id);

        var activity = await _application.Activities.GetActivityAsync(id);
        if (activity == null)
        {
            return NotFound(ApiErrorResponse.NotFound($"Activity with ID {id} not found."));
        }

        if (activity.TargetType != ActivityTargetType.ConnectedSystemRunProfile)
        {
            return BadRequest(ApiErrorResponse.BadRequest("Execution statistics are only available for Run Profile activities."));
        }

        var stats = await _application.Activities.GetActivityRunProfileExecutionStatsAsync(id);
        return Ok(ActivityRunProfileExecutionStatsDto.FromEntity(stats));
    }

    /// <summary>
    /// Get live Activity progress
    /// </summary>
    /// <remarks>
    /// A lightweight progress snapshot designed for frequent polling while a Run Profile
    /// executes: current status, phase message, object counts, percentage complete, throughput
    /// and estimated time remaining, plus a live operation-type breakdown (for example Added /
    /// Updated / Deleted counts). Much cheaper to serve than the full Activity detail response.
    /// Stop polling once <c>status</c> reaches a terminal value (Complete, CompleteWithWarning,
    /// CompleteWithError, FailedWithError or Cancelled).
    /// </remarks>
    /// <param name="id">The unique identifier (GUID) of the Activity.</param>
    /// <returns>The Activity's live progress snapshot.</returns>
    /// <response code="200">Returns the progress snapshot.</response>
    /// <response code="404">If the Activity is not found.</response>
    /// <response code="401">If the user is not authenticated.</response>
    [HttpGet("{id:guid}/progress", Name = "GetActivityProgress")]
    [ProducesResponseType(typeof(ActivityProgressDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetActivityProgressAsync(Guid id)
    {
        _logger.LogDebug("Getting activity progress: {Id}", id);

        var progress = await _application.Activities.GetActivityProgressAsync(id);
        if (progress == null)
        {
            return NotFound(ApiErrorResponse.NotFound($"Activity with ID {id} not found."));
        }

        // Feed the shared ETA tracker while the run is in flight so successive reads (from any
        // consumer) refine the rate; release the per-Activity state once the run has finished.
        ActivityEtaEstimate eta = default;
        if (progress.Status == ActivityStatus.InProgress)
            eta = _etaTracker.RecordSample(id, progress.ObjectsProcessed, progress.ObjectsToProcess);
        else if (progress.Status.IsTerminal())
            _etaTracker.Remove(id);

        return Ok(ActivityProgressDto.FromEntity(progress, eta, DateTime.UtcNow));
    }

    /// <summary>
    /// List Run Profile Execution Items
    /// </summary>
    /// <param name="id">The unique identifier (GUID) of the Activity.</param>
    /// <param name="pagination">Pagination parameters.</param>
    /// <returns>A paginated list of Execution Item headers.</returns>
    /// <response code="200">Returns the paginated list of Execution Items.</response>
    /// <response code="404">If the Activity is not found.</response>
    /// <response code="400">If the Activity is not a Run Profile Activity.</response>
    /// <response code="401">If the user is not authenticated.</response>
    [HttpGet("{id:guid}/items", Name = "GetActivityExecutionItems")]
    [ProducesResponseType(typeof(PaginatedResponse<ActivityRunProfileExecutionItemHeader>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetActivityExecutionItemsAsync(
        Guid id,
        [FromQuery] PaginationRequest pagination)
    {
        _logger.LogDebug("Getting activity execution items: {Id} (Page: {Page}, PageSize: {PageSize})",
            id, pagination.Page, pagination.PageSize);

        var activity = await _application.Activities.GetActivityAsync(id);
        if (activity == null)
        {
            return NotFound(ApiErrorResponse.NotFound($"Activity with ID {id} not found."));
        }

        if (activity.TargetType != ActivityTargetType.ConnectedSystemRunProfile)
        {
            return BadRequest(ApiErrorResponse.BadRequest("Execution items are only available for Run Profile activities."));
        }

        var result = await _application.Activities.GetActivityRunProfileExecutionItemHeadersAsync(
            id, pagination.Page, pagination.PageSize);

        var response = new PaginatedResponse<ActivityRunProfileExecutionItemHeader>
        {
            Items = result.Results,
            TotalCount = result.TotalResults,
            Page = result.CurrentPage,
            PageSize = result.PageSize
        };

        return Ok(response);
    }

    /// <summary>
    /// List child Activities
    /// </summary>
    /// <param name="id">The unique identifier (GUID) of the parent Activity.</param>
    /// <param name="pagination">Pagination parameters (page, pageSize).</param>
    /// <returns>A paginated list of child Activity headers, ordered by creation date ascending.</returns>
    /// <response code="200">Returns the paginated child Activities (empty page if none).</response>
    /// <response code="404">If the parent Activity is not found.</response>
    /// <response code="401">If the user is not authenticated.</response>
    [HttpGet("{id:guid}/children", Name = "GetChildActivities")]
    [ProducesResponseType(typeof(PaginatedResponse<ActivityHeader>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetChildActivitiesAsync(
        Guid id,
        [FromQuery] PaginationRequest pagination)
    {
        _logger.LogDebug("Getting child activities for parent: {Id} (Page: {Page}, PageSize: {PageSize})",
            id, pagination.Page, pagination.PageSize);

        var parent = await _application.Activities.GetActivityAsync(id);
        if (parent == null)
            return NotFound(ApiErrorResponse.NotFound($"Activity with ID {id} not found."));

        var result = await _application.Activities.GetChildActivitiesAsync(id, pagination.Page, pagination.PageSize);

        var response = PaginatedResponse<ActivityHeader>.Create(
            result.Results.Select(a => ActivityHeader.FromEntity(a)),
            result.TotalResults,
            result.CurrentPage,
            result.PageSize);

        return Ok(response);
    }

    /// <summary>
    /// Maps API sort property names to application layer sort keys.
    /// </summary>
    private static string? MapSortBy(string? sortBy)
    {
        if (string.IsNullOrEmpty(sortBy))
            return null;

        return sortBy.ToLowerInvariant() switch
        {
            "created" => "created",
            "executed" => "executed",
            "status" => "status",
            "targettype" or "target_type" or "type" => "type",
            "targetname" or "target_name" or "target" => "target",
            _ => null
        };
    }
}
