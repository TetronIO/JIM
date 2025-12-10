using Asp.Versioning;
using JIM.Application;
using JIM.Models.Activities;
using JIM.Models.Activities.DTOs;
using JIM.Web.Models.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace JIM.Web.Controllers.Api;

/// <summary>
/// API controller for viewing activity history and monitoring sync operations.
/// </summary>
/// <remarks>
/// Activities track all operations performed in JIM, including sync runs, data generation,
/// certificate management, and other administrative actions. This controller provides
/// read-only access to the activity history for monitoring and audit purposes.
/// </remarks>
[Route("api/v{version:apiVersion}/[controller]")]
[ApiController]
[ApiVersion("1.0")]
[Authorize(Roles = "Administrators")]
[Produces("application/json")]
public class ActivitiesController(ILogger<ActivitiesController> logger, JimApplication application) : ControllerBase
{
    private readonly ILogger<ActivitiesController> _logger = logger;
    private readonly JimApplication _application = application;

    /// <summary>
    /// Gets a paginated list of activities with optional filtering and sorting.
    /// </summary>
    /// <param name="pagination">Pagination parameters (page, pageSize, sortBy, sortDirection, filter).</param>
    /// <param name="search">Optional search query to filter by target name or type.</param>
    /// <returns>A paginated list of activity headers.</returns>
    /// <response code="200">Returns the paginated list of activities.</response>
    /// <response code="401">If the user is not authenticated.</response>
    [HttpGet(Name = "GetActivities")]
    [ProducesResponseType(typeof(PaginatedResponse<ActivityHeader>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetActivitiesAsync(
        [FromQuery] PaginationRequest pagination,
        [FromQuery] string? search = null)
    {
        _logger.LogDebug("Getting activities (Page: {Page}, PageSize: {PageSize}, Search: {Search})",
            pagination.Page, pagination.PageSize, search);

        // Map API pagination to application layer
        var sortBy = MapSortBy(pagination.SortBy);
        var sortDescending = pagination.IsDescending;

        var result = await _application.Activities.GetActivitiesAsync(
            page: pagination.Page,
            pageSize: pagination.PageSize,
            searchQuery: search,
            sortBy: sortBy,
            sortDescending: sortDescending);

        var headers = result.Results.Select(ActivityHeader.FromEntity);

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
    /// Gets detailed information about a specific activity.
    /// </summary>
    /// <param name="id">The unique identifier (GUID) of the activity.</param>
    /// <returns>The activity details including error information and execution statistics.</returns>
    /// <response code="200">Returns the activity details.</response>
    /// <response code="404">If the activity is not found.</response>
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

        // Get execution stats if this is a run profile activity
        ActivityRunProfileExecutionStats? stats = null;
        if (activity.TargetType == ActivityTargetType.ConnectedSystemRunProfile)
        {
            stats = await _application.Activities.GetActivityRunProfileExecutionStatsAsync(id);
        }

        return Ok(ActivityDetailDto.FromEntity(activity, stats));
    }

    /// <summary>
    /// Gets execution statistics for a run profile activity.
    /// </summary>
    /// <param name="id">The unique identifier (GUID) of the activity.</param>
    /// <returns>The execution statistics for the activity.</returns>
    /// <response code="200">Returns the execution statistics.</response>
    /// <response code="404">If the activity is not found.</response>
    /// <response code="400">If the activity is not a run profile activity.</response>
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
            return BadRequest(ApiErrorResponse.BadRequest("Execution statistics are only available for run profile activities."));
        }

        var stats = await _application.Activities.GetActivityRunProfileExecutionStatsAsync(id);
        return Ok(ActivityRunProfileExecutionStatsDto.FromEntity(stats));
    }

    /// <summary>
    /// Gets a paginated list of execution items for a run profile activity.
    /// </summary>
    /// <param name="id">The unique identifier (GUID) of the activity.</param>
    /// <param name="pagination">Pagination parameters.</param>
    /// <returns>A paginated list of execution item headers.</returns>
    /// <response code="200">Returns the paginated list of execution items.</response>
    /// <response code="404">If the activity is not found.</response>
    /// <response code="400">If the activity is not a run profile activity.</response>
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
            return BadRequest(ApiErrorResponse.BadRequest("Execution items are only available for run profile activities."));
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
