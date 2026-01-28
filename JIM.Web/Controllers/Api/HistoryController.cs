using System.Security.Claims;
using Asp.Versioning;
using JIM.Application;
using JIM.Application.Servers;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Security;
using JIM.Web.Models.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace JIM.Web.Controllers.Api;

/// <summary>
/// API controller for managing change history and activity retention.
/// </summary>
/// <remarks>
/// Provides endpoints for manually triggering history cleanup operations and
/// retrieving change history statistics. Automatic cleanup runs via housekeeping.
/// </remarks>
[Route("api/v{version:apiVersion}/[controller]")]
[ApiController]
[ApiVersion("1.0")]
[Authorize(Roles = "Administrator")]
[Produces("application/json")]
public class HistoryController(ILogger<HistoryController> logger, JimApplication application) : ControllerBase
{
    private readonly ILogger<HistoryController> _logger = logger;
    private readonly JimApplication _application = application;

    /// <summary>
    /// Manually triggers change history cleanup based on retention policy.
    /// </summary>
    /// <remarks>
    /// Deletes expired CSO changes, MVO changes, and Activities older than the configured
    /// retention period. The cleanup is limited by the configured batch size to prevent
    /// long-running transactions. For large volumes, call this endpoint multiple times
    /// or rely on automatic housekeeping cleanup.
    ///
    /// This operation creates an Activity record to audit the cleanup.
    /// </remarks>
    /// <returns>Summary of deleted records including counts and date range.</returns>
    /// <response code="200">Returns cleanup result with deletion statistics.</response>
    /// <response code="401">If the user is not authenticated.</response>
    /// <response code="500">If cleanup operation fails.</response>
    [HttpPost("cleanup", Name = "CleanupHistory")]
    [ProducesResponseType(typeof(HistoryCleanupResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> CleanupHistoryAsync()
    {
        _logger.LogInformation("Manual history cleanup triggered by API");

        try
        {
            // Get retention policy settings
            var retentionPeriod = await _application.ServiceSettings.GetHistoryRetentionPeriodAsync();
            var batchSize = await _application.ServiceSettings.GetHistoryCleanupBatchSizeAsync();
            var cutoffDate = DateTime.UtcNow - retentionPeriod;

            // Get current API key for initiator tracking
            var apiKey = await GetCurrentApiKeyAsync();

            // Perform cleanup, attributing the activity to the calling API key (or System if no key)
            ChangeHistoryServer.ChangeHistoryCleanupResult result;
            if (apiKey != null)
                result = await _application.ChangeHistory.DeleteExpiredChangeHistoryAsync(cutoffDate, batchSize, apiKey);
            else
                result = await _application.ChangeHistory.DeleteExpiredChangeHistoryAsync(cutoffDate, batchSize);

            _logger.LogInformation(
                "History cleanup completed - CSO: {CsoCount}, MVO: {MvoCount}, Activity: {ActivityCount}",
                result.CsoChangesDeleted, result.MvoChangesDeleted, result.ActivitiesDeleted);

            var response = new HistoryCleanupResponse
            {
                CsoChangesDeleted = result.CsoChangesDeleted,
                MvoChangesDeleted = result.MvoChangesDeleted,
                ActivitiesDeleted = result.ActivitiesDeleted,
                OldestRecordDeleted = result.OldestRecordDeleted,
                NewestRecordDeleted = result.NewestRecordDeleted,
                CutoffDate = cutoffDate,
                RetentionPeriodDays = (int)retentionPeriod.TotalDays,
                BatchSize = batchSize
            };

            return Ok(response);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to cleanup history");
            return StatusCode(StatusCodes.Status500InternalServerError,
                ApiErrorResponse.InternalError($"History cleanup failed: {ex.Message}"));
        }
    }

    /// <summary>
    /// Gets the count of change history records for a specific connected system.
    /// </summary>
    /// <param name="connectedSystemId">The connected system ID.</param>
    /// <returns>Count of CSO change records for the system.</returns>
    /// <response code="200">Returns the count of change records.</response>
    /// <response code="404">If the connected system is not found.</response>
    /// <response code="401">If the user is not authenticated.</response>
    [HttpGet("connected-systems/{connectedSystemId:int}/count", Name = "GetConnectedSystemChangeCount")]
    [ProducesResponseType(typeof(HistoryCountResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetConnectedSystemChangeCountAsync(int connectedSystemId)
    {
        _logger.LogDebug("Getting change history count for connected system {ConnectedSystemId}", connectedSystemId);

        // Verify connected system exists
        var connectedSystem = await _application.ConnectedSystems.GetConnectedSystemAsync(connectedSystemId);
        if (connectedSystem == null)
        {
            return NotFound(ApiErrorResponse.NotFound($"Connected system with ID {connectedSystemId} not found."));
        }

        var count = await _application.Repository.ChangeHistory.GetCsoChangeCountAsync(connectedSystemId);

        var response = new HistoryCountResponse
        {
            ConnectedSystemId = connectedSystemId,
            ConnectedSystemName = connectedSystem.Name,
            ChangeRecordCount = count
        };

        return Ok(response);
    }

    /// <summary>
    /// Gets a paginated list of deleted CSOs (Connected System Objects).
    /// </summary>
    /// <remarks>
    /// Returns CSOs that have been deleted, with their identity preserved at deletion time.
    /// These records are retained for audit and compliance purposes.
    /// </remarks>
    /// <param name="connectedSystemId">Optional filter by Connected System ID.</param>
    /// <param name="externalIdSearch">Optional search term for external ID (contains match).</param>
    /// <param name="fromDate">Optional filter for deletions on or after this date (UTC).</param>
    /// <param name="toDate">Optional filter for deletions on or before this date (UTC).</param>
    /// <param name="page">Page number (1-based, default: 1).</param>
    /// <param name="pageSize">Items per page (default: 50, max: 1000).</param>
    /// <returns>Paginated list of deleted CSO records.</returns>
    /// <response code="200">Returns the paginated list of deleted CSOs.</response>
    /// <response code="401">If the user is not authenticated.</response>
    [HttpGet("deleted-objects/cso", Name = "GetDeletedCsos")]
    [ProducesResponseType(typeof(DeletedObjectsPagedResponse<DeletedCsoResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetDeletedCsosAsync(
        [FromQuery] int? connectedSystemId = null,
        [FromQuery] string? externalIdSearch = null,
        [FromQuery] DateTime? fromDate = null,
        [FromQuery] DateTime? toDate = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50)
    {
        _logger.LogDebug("Getting deleted CSOs - CS:{ConnectedSystemId}, Search:{Search}, Page:{Page}",
            connectedSystemId, externalIdSearch, page);

        pageSize = Math.Clamp(pageSize, 1, 1000);
        page = Math.Max(page, 1);

        var (items, totalCount) = await _application.Repository.ConnectedSystems.GetDeletedCsoChangesAsync(
            connectedSystemId: connectedSystemId,
            fromDate: fromDate,
            toDate: toDate,
            externalIdSearch: string.IsNullOrWhiteSpace(externalIdSearch) ? null : externalIdSearch,
            page: page,
            pageSize: pageSize);

        // Resolve connected system names
        var csHeaders = await _application.ConnectedSystems.GetConnectedSystemHeadersAsync();
        var csLookup = csHeaders.ToDictionary(h => h.Id, h => h.Name);

        var response = new DeletedObjectsPagedResponse<DeletedCsoResponse>
        {
            Items = items.Select(c => new DeletedCsoResponse
            {
                Id = c.Id,
                ExternalId = c.DeletedObjectExternalId,
                DisplayName = c.DeletedObjectDisplayName,
                ObjectTypeName = c.DeletedObjectType?.Name,
                ConnectedSystemId = c.ConnectedSystemId,
                ConnectedSystemName = csLookup.TryGetValue(c.ConnectedSystemId, out var name) ? name : null,
                ChangeTime = c.ChangeTime,
                InitiatedByType = MapInitiatorType(c.InitiatedByType),
                InitiatedByName = c.InitiatedByName
            }).ToList(),
            TotalCount = totalCount,
            Page = page,
            PageSize = pageSize
        };

        return Ok(response);
    }

    /// <summary>
    /// Gets a paginated list of deleted MVOs (Metaverse Objects).
    /// </summary>
    /// <remarks>
    /// Returns MVOs that have been deleted, with their identity preserved at deletion time.
    /// These records are retained for audit and compliance purposes.
    /// </remarks>
    /// <param name="objectTypeId">Optional filter by Metaverse Object Type ID.</param>
    /// <param name="displayNameSearch">Optional search term for display name (contains match).</param>
    /// <param name="fromDate">Optional filter for deletions on or after this date (UTC).</param>
    /// <param name="toDate">Optional filter for deletions on or before this date (UTC).</param>
    /// <param name="page">Page number (1-based, default: 1).</param>
    /// <param name="pageSize">Items per page (default: 50, max: 1000).</param>
    /// <returns>Paginated list of deleted MVO records.</returns>
    /// <response code="200">Returns the paginated list of deleted MVOs.</response>
    /// <response code="401">If the user is not authenticated.</response>
    [HttpGet("deleted-objects/mvo", Name = "GetDeletedMvos")]
    [ProducesResponseType(typeof(DeletedObjectsPagedResponse<DeletedMvoResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetDeletedMvosAsync(
        [FromQuery] int? objectTypeId = null,
        [FromQuery] string? displayNameSearch = null,
        [FromQuery] DateTime? fromDate = null,
        [FromQuery] DateTime? toDate = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50)
    {
        _logger.LogDebug("Getting deleted MVOs - Type:{ObjectTypeId}, Search:{Search}, Page:{Page}",
            objectTypeId, displayNameSearch, page);

        pageSize = Math.Clamp(pageSize, 1, 1000);
        page = Math.Max(page, 1);

        var (items, totalCount) = await _application.Repository.Metaverse.GetDeletedMvoChangesAsync(
            objectTypeId: objectTypeId,
            fromDate: fromDate,
            toDate: toDate,
            displayNameSearch: string.IsNullOrWhiteSpace(displayNameSearch) ? null : displayNameSearch,
            page: page,
            pageSize: pageSize);

        var response = new DeletedObjectsPagedResponse<DeletedMvoResponse>
        {
            Items = items.Select(c => new DeletedMvoResponse
            {
                Id = c.Id,
                DisplayName = c.DeletedObjectDisplayName,
                ObjectTypeName = c.DeletedObjectType?.Name,
                ObjectTypeId = c.DeletedObjectTypeId,
                ChangeTime = c.ChangeTime,
                InitiatedByType = MapInitiatorType(c.InitiatedByType),
                InitiatedByName = c.InitiatedByName
            }).ToList(),
            TotalCount = totalCount,
            Page = page,
            PageSize = pageSize
        };

        return Ok(response);
    }

    #region Private Helpers

    private static string MapInitiatorType(ActivityInitiatorType initiatorType)
    {
        return initiatorType switch
        {
            ActivityInitiatorType.User => "User",
            ActivityInitiatorType.ApiKey => "ApiKey",
            _ => "Unknown"
        };
    }

    /// <summary>
    /// Checks if the current authentication is via API key.
    /// </summary>
    private bool IsApiKeyAuthenticated()
    {
        return User.HasClaim("auth_method", "api_key");
    }

    /// <summary>
    /// Gets the current API key entity if authenticated via API key.
    /// </summary>
    private async Task<ApiKey?> GetCurrentApiKeyAsync()
    {
        if (!IsApiKeyAuthenticated())
            return null;

        // The API key ID is stored in the NameIdentifier claim
        var apiKeyIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrEmpty(apiKeyIdClaim) || !Guid.TryParse(apiKeyIdClaim, out var apiKeyId))
            return null;

        return await _application.Repository.ApiKeys.GetByIdAsync(apiKeyId);
    }

    #endregion
}
