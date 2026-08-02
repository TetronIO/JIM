// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using Asp.Versioning;
using JIM.Application;
using JIM.Web.Models.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace JIM.Web.Controllers.Api;

/// <summary>
/// API controller for reading and cancelling configuration change previews (#827).
/// </summary>
/// <remarks>
/// A preview answers "what would this configuration change do?" without making it. These endpoints read and cancel
/// a preview that is already running or finished.
///
/// **Previews are started from the surface being changed**, not from here: the request body is the surface's own
/// update type, and there is one such endpoint per surface (for example a Metaverse Object Type's deletion
/// settings). A single generic start endpoint would have to accept a body whose type it could only learn from the
/// request itself, which is exactly the shape this framework refuses; the adapter for each surface declares the
/// type instead.
/// </remarks>
[Route("api/v{version:apiVersion}/[controller]")]
[ApiController]
[ApiVersion("1.0")]
[Authorize(Roles = "Administrator")]
[Produces("application/json")]
public class PreviewsController(ILogger<PreviewsController> logger, JimApplication application) : ControllerBase
{
    private readonly ILogger<PreviewsController> _logger = logger;
    private readonly JimApplication _application = application;

    /// <summary>
    /// Get a preview
    /// </summary>
    /// <remarks>
    /// Returns stage statuses, validation findings, impact counts and summary groups. Poll this while a preview is
    /// running; each stage's results appear as it completes, so there is something to read long before the whole
    /// preview finishes.
    /// </remarks>
    /// <param name="activityId">The preview's Activity id, returned when the preview was started.</param>
    /// <response code="200">Returns the preview.</response>
    /// <response code="401">If the caller is not authenticated.</response>
    /// <response code="404">If no preview exists for that Activity, including after retention has removed it.</response>
    [HttpGet("{activityId:guid}", Name = "GetConfigurationChangePreview")]
    [ProducesResponseType(typeof(ConfigurationChangePreviewResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetPreviewAsync(Guid activityId)
    {
        var preview = await _application.ConfigurationChangePreviews.GetPreviewAsync(activityId);
        if (preview is null)
            return NotFound();

        var activity = await _application.Activities.GetActivityAsync(activityId);
        if (activity is null)
        {
            // The preview cascades from its Activity, so this cannot happen through any supported path; if it ever
            // does, the preview row is orphaned and reporting it as absent is the honest answer.
            _logger.LogWarning("Configuration change preview {ActivityId} has no Activity", activityId);
            return NotFound();
        }

        var groups = await _application.ConfigurationChangePreviews.GetPreviewGroupsAsync(activityId);
        return Ok(ConfigurationChangePreviewResponse.FromEntity(preview, activity, groups));
    }

    /// <summary>
    /// Get a preview's object-level detail
    /// </summary>
    /// <remarks>
    /// The rows behind a summary group. Paginated server-side, because a preview can cover hundreds of thousands of
    /// objects. Where a group's <c>deltasSampled</c> is true these rows are a sample of that group, not all of it;
    /// the group's own count remains exact.
    /// </remarks>
    /// <param name="activityId">The preview's Activity id.</param>
    /// <param name="pagination">Pagination parameters.</param>
    /// <param name="groupId">Optional summary group to restrict the rows to.</param>
    /// <response code="200">Returns the requested page of object-level detail.</response>
    /// <response code="401">If the caller is not authenticated.</response>
    /// <response code="404">If no preview exists for that Activity.</response>
    [HttpGet("{activityId:guid}/deltas", Name = "GetConfigurationChangePreviewDeltas")]
    [ProducesResponseType(typeof(PaginatedResponse<ConfigurationChangePreviewDeltaResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetPreviewDeltasAsync(Guid activityId, [FromQuery] PaginationRequest pagination,
        [FromQuery] Guid? groupId = null)
    {
        var preview = await _application.ConfigurationChangePreviews.GetPreviewAsync(activityId);
        if (preview is null)
            return NotFound();

        var page = await _application.ConfigurationChangePreviews.GetPreviewDeltasAsync(
            activityId, groupId, pagination.Page, pagination.PageSize);

        return Ok(new PaginatedResponse<ConfigurationChangePreviewDeltaResponse>
        {
            Items = page.Results.Select(ConfigurationChangePreviewDeltaResponse.FromEntity),
            TotalCount = page.TotalResults,
            Page = page.CurrentPage,
            PageSize = page.PageSize
        });
    }

    /// <summary>
    /// Cancel a preview
    /// </summary>
    /// <remarks>
    /// Stops a running preview. Nothing is deleted: the preview and whatever it had recorded remain readable, with
    /// its Activity marked cancelled, because an administrator who stopped a preview after seeing the first stage
    /// usually stopped it *because* of what that stage said.
    /// </remarks>
    /// <param name="activityId">The preview's Activity id.</param>
    /// <response code="204">The preview was cancelled.</response>
    /// <response code="401">If the caller is not authenticated.</response>
    /// <response code="404">If no preview exists for that Activity.</response>
    /// <response code="409">If the preview is no longer running, so there is nothing to cancel.</response>
    [HttpDelete("{activityId:guid}", Name = "CancelConfigurationChangePreview")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> CancelPreviewAsync(Guid activityId)
    {
        var preview = await _application.ConfigurationChangePreviews.GetPreviewAsync(activityId);
        if (preview is null)
            return NotFound();

        var cancelled = await _application.ConfigurationChangePreviews.CancelPreviewAsync(activityId);
        if (!cancelled)
            return Conflict(new { message = "The preview is no longer running." });

        _logger.LogInformation("Configuration change preview {ActivityId} was cancelled", activityId);
        return NoContent();
    }
}
