// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using Asp.Versioning;
using JIM.Application;
using JIM.Models.Core;
using JIM.Models.Transactional;
using JIM.Models.Transactional.DTOs;
using JIM.Web.Models.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace JIM.Web.Controllers.Api;

/// <summary>
/// API controller for the Exchange queue: the recipient changes JIM has queued for Exchange, where each stands, and the
/// retry over them.
/// </summary>
/// <remarks>
/// #2021, Phase 6b. Behind the in-development <c>Features.ExchangeRecipients</c> flag: while it is off, every action
/// answers 400 naming the feature. An Exchange change cannot be cancelled (PRD decision 16): a change is stopped or
/// corrected by changing what is declared for the account.
/// </remarks>
[Route("api/v{version:apiVersion}/exchange")]
[ApiController]
[ApiVersion("1.0")]
[Authorize(Roles = "Administrator")]
[Produces("application/json")]
public class ExchangeController(
    ILogger<ExchangeController> logger,
    JimApplication application) : ApiControllerBase(application, logger)
{
    private readonly ILogger<ExchangeController> _logger = logger;
    private readonly JimApplication _application = application;

    /// <summary>
    /// The columns the queue may be sorted by, named so an unrecognised one is refused rather than silently ignored.
    /// </summary>
    private static readonly string[] SortableColumns = ["queued", "identity", "system", "attempts", "nextattempt", "lastattempt"];

    /// <summary>
    /// List queued Exchange changes
    /// </summary>
    /// <remarks>
    /// Returns one row per account with a change on the Exchange queue, oldest first, naming the Connected System and the
    /// Metaverse Object so a caller does not need a second request to report them. A delivered change leaves the queue;
    /// its outcome stays on its Activity.
    ///
    /// Criteria combine: `?state=Parked&amp;connectedSystemId=3` is "parked changes for system 3". Sort with `sortBy` set
    /// to one of `queued` (the default), `identity`, `system`, `attempts`, `nextAttempt` or `lastAttempt`.
    /// </remarks>
    /// <param name="pagination">Pagination and sort parameters (page, pageSize, sortBy, sortDirection).</param>
    /// <param name="connectedSystemId">Optional. Restrict to one Connected System.</param>
    /// <param name="state">Optional. Restrict to one state: Queued, Delivering, Retrying, Parked, Refused or NotMade.</param>
    /// <param name="failureKind">Optional. Restrict to changes whose last attempt failed this way.</param>
    /// <param name="metaverseObjectId">Optional. Restrict to one Metaverse Object's changes.</param>
    /// <param name="connectedSystemObjectId">Optional. Restrict to one account's change.</param>
    /// <param name="search">Optional. Free-text search over the Metaverse Object and Connected System names.</param>
    /// <response code="200">The requested window of the queue.</response>
    /// <response code="400">An unparseable filter value, an unknown sort column, or the Exchange recipients feature is not enabled.</response>
    /// <response code="404">No such Connected System.</response>
    [HttpGet("queue", Name = "GetPendingExchangeChanges")]
    [ProducesResponseType(typeof(PaginatedResponse<PendingExchangeChangeResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetPendingExchangeChangesAsync(
        [FromQuery] PaginationRequest pagination,
        [FromQuery] int? connectedSystemId = null,
        [FromQuery] ExchangeQueueState? state = null,
        [FromQuery] ExchangeChangeFailureKind? failureKind = null,
        [FromQuery] Guid? metaverseObjectId = null,
        [FromQuery] Guid? connectedSystemObjectId = null,
        [FromQuery] string? search = null)
    {
        ArgumentNullException.ThrowIfNull(pagination);
        await _application.FeatureFlags.EnsureEnabledAsync(FeatureFlagCatalogue.ExchangeRecipients.Key);

        var sortBy = string.IsNullOrWhiteSpace(pagination.SortBy) ? "queued" : pagination.SortBy;
        if (!SortableColumns.Contains(sortBy, StringComparer.OrdinalIgnoreCase))
        {
            return BadRequest(ApiErrorResponse.BadRequest(
                $"'{pagination.SortBy}' is not a sortable column. Valid values are: {string.Join(", ", SortableColumns)}."));
        }

        if (connectedSystemId.HasValue && await _application.ConnectedSystems.GetConnectedSystemCoreAsync(connectedSystemId.Value) == null)
            return NotFound(ApiErrorResponse.NotFound($"Connected System {connectedSystemId.Value} was not found."));

        var filter = new PendingExchangeChangeFilter
        {
            ConnectedSystemId = connectedSystemId,
            State = state?.ToChangeState(),
            FailureKind = failureKind,
            MetaverseObjectId = metaverseObjectId,
            ConnectedSystemObjectId = connectedSystemObjectId,
            SearchText = search
        };

        // One clock reading, so every row in one response answers "is this due?" as at the same instant.
        var asOf = DateTime.UtcNow;
        var result = await _application.ExchangeDelivery.GetPendingChangesAsync(
            filter, pagination.Skip, pagination.PageSize, sortBy, pagination.IsDescending, includeTotalCount: true);
        var items = result.Results.Select(h => PendingExchangeChangeResponse.FromHeader(h, asOf)).ToList();

        return Ok(PaginatedResponse<PendingExchangeChangeResponse>.Create(items, result.TotalResults ?? items.Count, pagination.Page, pagination.PageSize));
    }

    /// <summary>
    /// Summarise the Exchange queue
    /// </summary>
    /// <remarks>
    /// Counts the whole queue by state, for a caller that wants to know whether anything needs attention without reading
    /// the rows. `waitingOnAPersonCount` is the parked, refused and not-made changes together; `dueCount` is how many the
    /// Exchange delivery lane would attempt now.
    /// </remarks>
    /// <response code="200">The queue's counts by state.</response>
    /// <response code="400">The Exchange recipients feature is not enabled.</response>
    [HttpGet("queue/summary", Name = "GetExchangeQueueSummary")]
    [ProducesResponseType(typeof(ExchangeQueueSummary), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetExchangeQueueSummaryAsync()
    {
        await _application.FeatureFlags.EnsureEnabledAsync(FeatureFlagCatalogue.ExchangeRecipients.Key);
        return Ok(await _application.ExchangeDelivery.GetQueueSummaryAsync());
    }

    /// <summary>
    /// Retry queued Exchange changes
    /// </summary>
    /// <remarks>
    /// Makes every matching change that is parked, or retrying after an attempt, due at once, with its attempts and
    /// 24-hour park clock reset, and wakes the Exchange delivery lane for it. This is what an administrator does once the
    /// reason Exchange refused or failed a change has been dealt with.
    ///
    /// A change not yet attempted is already due and is left as it is; one being delivered is left to finish; a refused
    /// or not-made change holds until what is declared for the account changes, and a retry leaves it alone.
    ///
    /// The retry is recorded as one Activity, whether or not anything matched, and under each retried change's own
    /// Activity.
    /// </remarks>
    /// <param name="request">Which changes to retry. Must name at least one criterion, or set `applyToAllChanges`.</param>
    /// <response code="200">How many changes were made due again. Zero is a valid outcome, not an error.</response>
    /// <response code="400">The request names no changes or too many, or the Exchange recipients feature is not enabled.</response>
    /// <response code="404">No such Connected System.</response>
    [HttpPost("queue/retry", Name = "RetryPendingExchangeChanges")]
    [ProducesResponseType(typeof(ExchangeQueueActionResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> RetryPendingExchangeChangesAsync([FromBody] ExchangeQueueActionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        await _application.FeatureFlags.EnsureEnabledAsync(FeatureFlagCatalogue.ExchangeRecipients.Key);

        if (request.ConnectedSystemId.HasValue && await _application.ConnectedSystems.GetConnectedSystemCoreAsync(request.ConnectedSystemId.Value) == null)
            return NotFound(ApiErrorResponse.NotFound($"Connected System {request.ConnectedSystemId.Value} was not found."));

        // Attributed to whoever authenticated: an administrator at a screen, or the API key an automation presented.
        var initiatedBy = await GetCurrentUserAsync();
        var apiKey = await GetCurrentApiKeyAsync();
        if (initiatedBy == null && apiKey == null)
        {
            _logger.LogWarning("Could not identify the caller of an Exchange queue retry");
            return Unauthorized(ApiErrorResponse.Unauthorised("Could not identify user from authentication token."));
        }

        _logger.LogInformation("Exchange queue retry requested (Connected System: {ConnectedSystemId}, State: {State}, Named changes: {NamedChanges})",
            request.ConnectedSystemId, request.State, request.Ids?.Count ?? 0);

        var affected = await _application.ExchangeDelivery.RetryAsync(request.ToFilter(), initiatedBy, apiKey);
        return Ok(new ExchangeQueueActionResponse { AffectedCount = affected });
    }
}
