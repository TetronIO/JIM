// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using Asp.Versioning;
using JIM.Application;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using JIM.Models.Transactional.DTOs;
using JIM.Web.Models.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace JIM.Web.Controllers.Api;

/// <summary>
/// API controller for the Password Synchronisation queue: what is waiting to be delivered, what needs a person,
/// and the retry and cancel actions over it (#1119, requirement 33).
/// </summary>
/// <remarks>
/// Queueing a password change is not here; it belongs to the identity whose password changed
/// (<c>POST /metaverse/objects/{id}/password</c>). This controller is the administrator's view of what happened
/// to those changes afterwards.
/// <para>
/// No endpoint on this controller returns a password. The queued value is encrypted in the database and has no
/// representation in any DTO, response or log line on this path.
/// </para>
/// </remarks>
[Route("api/v{version:apiVersion}/password-synchronisation")]
[ApiController]
[ApiVersion("1.0")]
[Authorize(Roles = "Administrator")]
[Produces("application/json")]
public class PasswordSynchronisationController(
    ILogger<PasswordSynchronisationController> logger,
    JimApplication application) : ApiControllerBase(application, logger)
{
    private readonly ILogger<PasswordSynchronisationController> _logger = logger;
    private readonly JimApplication _application = application;

    /// <summary>
    /// The columns the queue may be sorted by. Named here so an unrecognised value is refused rather than
    /// silently served in the default order, which would have a caller believe they sorted when they did not.
    /// </summary>
    private static readonly string[] SortableColumns =
        ["queued", "identity", "system", "status", "attempts", "nextattempt", "expires"];

    /// <summary>
    /// List queued password changes
    /// </summary>
    /// <remarks>
    /// Returns one row per identity per Connected System, newest state first, with the identity and Connected
    /// System named so a caller does not need a second request to report them.
    ///
    /// Criteria combine: `?status=Parked&amp;connectedSystemId=3` is "Parked changes for system 3". The `due`
    /// field on each row says whether a delivery pass would attempt it right now, which `status` alone cannot,
    /// since a Pending change may be waiting out a retry backoff.
    ///
    /// Sort with `sortBy` set to one of `queued` (the default), `identity`, `system`, `status`, `attempts`,
    /// `nextAttempt` or `expires`.
    /// </remarks>
    /// <param name="pagination">Pagination and sort parameters (page, pageSize, sortBy, sortDirection).</param>
    /// <param name="connectedSystemId">Optional. Restrict to one Connected System.</param>
    /// <param name="status">Optional. Restrict to one state: Pending, Parked, Expired or Cancelled.</param>
    /// <param name="failureReason">Optional. Restrict to changes whose last attempt failed this way.</param>
    /// <param name="metaverseObjectId">Optional. Restrict to one identity's queued changes.</param>
    /// <param name="search">Optional. Free-text search over the identity and Connected System names.</param>
    /// <response code="200">The requested window of the queue.</response>
    /// <response code="400">An unparseable filter value, an unknown sort column, or a request beyond the retrieval depth ceiling.</response>
    /// <response code="404">No such Connected System.</response>
    [HttpGet("queue", Name = "GetPendingPasswordChanges")]
    [ProducesResponseType(typeof(PaginatedResponse<PendingPasswordChangeResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetPendingPasswordChangesAsync(
        [FromQuery] PaginationRequest pagination,
        [FromQuery] int? connectedSystemId = null,
        [FromQuery] PendingPasswordChangeStatus? status = null,
        [FromQuery] PasswordSetFailureReason? failureReason = null,
        [FromQuery] Guid? metaverseObjectId = null,
        [FromQuery] string? search = null)
    {
        ArgumentNullException.ThrowIfNull(pagination);

        var sortBy = string.IsNullOrWhiteSpace(pagination.SortBy) ? "queued" : pagination.SortBy;
        if (!SortableColumns.Contains(sortBy, StringComparer.OrdinalIgnoreCase))
        {
            return BadRequest(ApiErrorResponse.BadRequest(
                $"'{pagination.SortBy}' is not a sortable column. Valid values are: {string.Join(", ", SortableColumns)}."));
        }

        if (connectedSystemId.HasValue)
        {
            var connectedSystem = await _application.ConnectedSystems.GetConnectedSystemCoreAsync(connectedSystemId.Value);
            if (connectedSystem == null)
                return NotFound(ApiErrorResponse.NotFound($"Connected System {connectedSystemId.Value} was not found."));
        }

        var filter = new PendingPasswordChangeFilter
        {
            ConnectedSystemId = connectedSystemId,
            Status = status,
            FailureReason = failureReason,
            MetaverseObjectId = metaverseObjectId,
            SearchText = search
        };

        // Read the clock once, so every row in one response answers "is this due?" as at the same instant.
        var asOf = DateTime.UtcNow;

        var result = await _application.PasswordSynchronisation.GetPendingPasswordChangesAsync(
            filter,
            pagination.Skip,
            pagination.PageSize,
            sortBy,
            pagination.IsDescending,
            includeTotalCount: true);

        var items = result.Results.Select(h => PendingPasswordChangeResponse.FromHeader(h, asOf)).ToList();

        return Ok(PaginatedResponse<PendingPasswordChangeResponse>.Create(
            items,
            result.TotalResults ?? items.Count,
            pagination.Page,
            pagination.PageSize));
    }

    /// <summary>
    /// Summarise the Password Synchronisation queue
    /// </summary>
    /// <remarks>
    /// Counts the whole queue by state, for a caller that wants to know whether anything needs attention without
    /// reading the rows.
    ///
    /// `waitingCount` and `dueCount` answer different questions and are both reported: a large waiting count with
    /// nothing due is a queue working through its retry backoffs, while a large due count is a queue that is not
    /// being drained.
    /// </remarks>
    /// <response code="200">The queue's counts by state.</response>
    [HttpGet("queue/summary", Name = "GetPasswordQueueSummary")]
    [ProducesResponseType(typeof(PasswordQueueSummary), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetPasswordQueueSummaryAsync()
    {
        return Ok(await _application.PasswordSynchronisation.GetQueueSummaryAsync());
    }

    /// <summary>
    /// Retry queued password changes
    /// </summary>
    /// <remarks>
    /// Makes every matching change due immediately and raises a delivery pass for it. This is what an
    /// administrator does once the reason a target refused the password has been dealt with.
    ///
    /// Applies to changes JIM could still deliver: Pending and Parked. An Expired change is not retried, because
    /// the password it carried is gone; queue a new password change instead.
    ///
    /// The action is recorded as one Activity, whether or not anything matched, so a retry that changed nothing
    /// can be told apart from a retry that never ran.
    /// </remarks>
    /// <param name="request">Which changes to retry. Must name at least one criterion, or set
    /// `applyToAllChanges`.</param>
    /// <response code="200">How many changes were made due again. Zero is a valid outcome, not an error.</response>
    /// <response code="400">The request names no changes, or names more than may be listed in one request.</response>
    /// <response code="404">No such Connected System.</response>
    [HttpPost("queue/retry", Name = "RetryPendingPasswordChanges")]
    [ProducesResponseType(typeof(PasswordQueueActionResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> RetryPendingPasswordChangesAsync([FromBody] PasswordQueueActionRequest request)
    {
        return await RunQueueActionAsync(
            request,
            static (application, filter, user, apiKey) =>
                application.PasswordSynchronisation.RetryAsync(filter, user, apiKey),
            "retry");
    }

    /// <summary>
    /// Cancel queued password changes
    /// </summary>
    /// <remarks>
    /// Records that JIM should stop trying to deliver every matching change.
    ///
    /// The rows are kept, marked Cancelled, with who cancelled them and when. They are not deleted: the identity's
    /// password is still divergent on that Connected System, and the cancelled row is the only thing that says so.
    /// Retention removes them on the same schedule as any other finished change.
    ///
    /// A cancelled change can be retried, which puts it back in the queue if it has not expired in the meantime.
    /// </remarks>
    /// <param name="request">Which changes to cancel. Must name at least one criterion, or set
    /// `applyToAllChanges`.</param>
    /// <response code="200">How many changes were cancelled. Zero is a valid outcome, not an error.</response>
    /// <response code="400">The request names no changes, or names more than may be listed in one request.</response>
    /// <response code="404">No such Connected System.</response>
    [HttpPost("queue/cancel", Name = "CancelPendingPasswordChanges")]
    [ProducesResponseType(typeof(PasswordQueueActionResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> CancelPendingPasswordChangesAsync([FromBody] PasswordQueueActionRequest request)
    {
        return await RunQueueActionAsync(
            request,
            static (application, filter, user, apiKey) =>
                application.PasswordSynchronisation.CancelAsync(filter, user, apiKey),
            "cancel");
    }

    /// <summary>
    /// Runs a retry or cancel: the two differ only in which application method they call, and sharing the
    /// validation and attribution keeps them from drifting apart.
    /// </summary>
    private async Task<IActionResult> RunQueueActionAsync(
        PasswordQueueActionRequest request,
        Func<JimApplication, PendingPasswordChangeFilter, JIM.Models.Core.MetaverseObject?, JIM.Models.Security.ApiKey?, Task<int>> action,
        string actionName)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.ConnectedSystemId.HasValue)
        {
            var connectedSystem = await _application.ConnectedSystems.GetConnectedSystemCoreAsync(request.ConnectedSystemId.Value);
            if (connectedSystem == null)
                return NotFound(ApiErrorResponse.NotFound($"Connected System {request.ConnectedSystemId.Value} was not found."));
        }

        // Attribution follows whoever authenticated: an administrator at a screen, or the API key an automation
        // presented. Neither is required to have a Metaverse Object, but one of the two must be identifiable, or
        // the Activity would record an anonymous change to the queue.
        var initiatedBy = await GetCurrentUserAsync();
        var apiKey = await GetCurrentApiKeyAsync();
        if (initiatedBy == null && apiKey == null)
        {
            _logger.LogWarning("Could not identify the caller of a Password Synchronisation queue {ActionName}", actionName);
            return Unauthorized(ApiErrorResponse.Unauthorised("Could not identify user from authentication token."));
        }

        _logger.LogInformation(
            "Password Synchronisation queue {ActionName} requested (Connected System: {ConnectedSystemId}, Status: {Status}, Named changes: {NamedChanges})",
            actionName,
            request.ConnectedSystemId,
            request.Status,
            request.Ids?.Count ?? 0);

        var affected = await action(_application, request.ToFilter(), initiatedBy, apiKey);

        return Ok(new PasswordQueueActionResponse { AffectedCount = affected });
    }
}
