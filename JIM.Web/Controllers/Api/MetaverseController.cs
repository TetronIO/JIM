using System.Security.Claims;
using Asp.Versioning;
using JIM.Web.Extensions.Api;
using JIM.Web.Models.Api;
using JIM.Application;
using JIM.Models.Core;
using JIM.Models.Core.DTOs;
using JIM.Models.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace JIM.Web.Controllers.Api;

/// <summary>
/// API controller for managing Metaverse schema and objects.
/// </summary>
/// <remarks>
/// The Metaverse is the central identity store in JIM. This controller provides
/// endpoints for managing object types, attributes, and individual metaverse objects.
/// </remarks>
[Route("api/v{version:apiVersion}/[controller]")]
[ApiController]
[ApiVersion("1.0")]
[Authorize(Roles = "Administrator")]
[Produces("application/json")]
public class MetaverseController(ILogger<MetaverseController> logger, JimApplication application) : ControllerBase
{
    private readonly ILogger<MetaverseController> _logger = logger;
    private readonly JimApplication _application = application;

    /// <summary>
    /// Gets all metaverse object types with optional pagination, sorting, and filtering.
    /// </summary>
    /// <param name="pagination">Pagination parameters (page, pageSize, sortBy, sortDirection, filter).</param>
    /// <param name="includeChildObjects">Whether to include child object counts in the response.</param>
    /// <returns>A paginated list of metaverse object type headers.</returns>
    [HttpGet("object-types", Name = "GetObjectTypes")]
    [ProducesResponseType(typeof(PaginatedResponse<MetaverseObjectTypeHeader>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetObjectTypesAsync([FromQuery] PaginationRequest pagination, bool includeChildObjects = false)
    {
        _logger.LogTrace("Requested metaverse object types (Page: {Page}, PageSize: {PageSize})", pagination.Page, pagination.PageSize);
        var objectTypes = await _application.Metaverse.GetMetaverseObjectTypesAsync(includeChildObjects);
        var headers = objectTypes.Select(MetaverseObjectTypeHeader.FromEntity).AsQueryable();

        var result = headers
            .ApplySortAndFilter(pagination)
            .ToPaginatedResponse(pagination);

        return Ok(result);
    }

    /// <summary>
    /// Gets a specific metaverse object type by ID.
    /// </summary>
    /// <param name="id">The unique identifier of the object type.</param>
    /// <param name="includeChildObjects">Whether to include child object details in the response.</param>
    /// <returns>The object type details.</returns>
    [HttpGet("object-types/{id:int}", Name = "GetObjectType")]
    [ProducesResponseType(typeof(MetaverseObjectTypeDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetObjectTypeAsync(int id, bool includeChildObjects = false)
    {
        _logger.LogTrace("Requested object type: {Id}", id);
        var objectType = await _application.Metaverse.GetMetaverseObjectTypeAsync(id, includeChildObjects);
        if (objectType == null)
            return NotFound(ApiErrorResponse.NotFound($"Object type with ID {id} not found."));

        return Ok(MetaverseObjectTypeDetailDto.FromEntity(objectType));
    }

    /// <summary>
    /// Updates a metaverse object type's deletion rules.
    /// </summary>
    /// <param name="id">The unique identifier of the object type.</param>
    /// <param name="request">The update request containing deletion rule settings.</param>
    /// <returns>The updated object type details.</returns>
    /// <response code="200">Object type updated successfully.</response>
    /// <response code="400">Invalid request or validation failed.</response>
    /// <response code="404">Object type not found.</response>
    /// <response code="401">User not authenticated.</response>
    [HttpPut("object-types/{id:int}", Name = "UpdateObjectType")]
    [ProducesResponseType(typeof(MetaverseObjectTypeDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> UpdateObjectTypeAsync(int id, [FromBody] UpdateMetaverseObjectTypeRequest request)
    {
        _logger.LogInformation("Updating metaverse object type: {Id}", id);

        var objectType = await _application.Metaverse.GetMetaverseObjectTypeAsync(id, false);
        if (objectType == null)
            return NotFound(ApiErrorResponse.NotFound($"Object type with ID {id} not found."));

        // Apply updates
        if (request.DeletionRule.HasValue)
            objectType.DeletionRule = request.DeletionRule.Value;

        if (request.DeletionGracePeriodDays.HasValue)
        {
            if (request.DeletionGracePeriodDays.Value < 0)
                return BadRequest(ApiErrorResponse.BadRequest("DeletionGracePeriodDays cannot be negative."));
            objectType.DeletionGracePeriodDays = request.DeletionGracePeriodDays.Value == 0 ? null : request.DeletionGracePeriodDays.Value;
        }

        if (request.DeletionTriggerConnectedSystemIds != null)
        {
            // Validate that the connected system IDs exist
            foreach (var connectedSystemId in request.DeletionTriggerConnectedSystemIds)
            {
                var connectedSystem = await _application.ConnectedSystems.GetConnectedSystemAsync(connectedSystemId);
                if (connectedSystem == null)
                    return BadRequest(ApiErrorResponse.BadRequest($"Connected system with ID {connectedSystemId} not found."));
            }
            objectType.DeletionTriggerConnectedSystemIds = request.DeletionTriggerConnectedSystemIds;
        }

        // Validate WhenAuthoritativeSourceDisconnected requires at least one trigger system
        var effectiveDeletionRule = request.DeletionRule ?? objectType.DeletionRule;
        var effectiveTriggerIds = request.DeletionTriggerConnectedSystemIds ?? objectType.DeletionTriggerConnectedSystemIds;
        if (effectiveDeletionRule == MetaverseObjectDeletionRule.WhenAuthoritativeSourceDisconnected &&
            (effectiveTriggerIds == null || effectiveTriggerIds.Count == 0))
        {
            return BadRequest(ApiErrorResponse.BadRequest("WhenAuthoritativeSourceDisconnected deletion rule requires at least one authoritative source to be specified in DeletionTriggerConnectedSystemIds."));
        }

        await _application.Metaverse.UpdateMetaverseObjectTypeAsync(objectType);

        _logger.LogInformation("Updated metaverse object type: {Id} ({Name}) - DeletionRule: {DeletionRule}, GracePeriod: {GracePeriod}",
            objectType.Id, objectType.Name, objectType.DeletionRule, objectType.DeletionGracePeriodDays);

        var result = await _application.Metaverse.GetMetaverseObjectTypeAsync(objectType.Id, false);
        return Ok(MetaverseObjectTypeDetailDto.FromEntity(result!));
    }

    /// <summary>
    /// Gets all metaverse attributes with optional pagination, sorting, and filtering.
    /// </summary>
    /// <param name="pagination">Pagination parameters (page, pageSize, sortBy, sortDirection, filter).</param>
    /// <returns>A paginated list of metaverse attribute headers.</returns>
    [HttpGet("attributes", Name = "GetAttributes")]
    [ProducesResponseType(typeof(PaginatedResponse<MetaverseAttributeHeader>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetAttributesAsync([FromQuery] PaginationRequest pagination)
    {
        _logger.LogTrace("Requested metaverse attributes (Page: {Page}, PageSize: {PageSize})", pagination.Page, pagination.PageSize);
        var attributes = await _application.Metaverse.GetMetaverseAttributesAsync();
        var headers = attributes?.Select(MetaverseAttributeHeader.FromEntity).AsQueryable();

        var result = headers != null
            ? headers.ApplySortAndFilter(pagination).ToPaginatedResponse(pagination)
            : PaginatedResponse<MetaverseAttributeHeader>.Create([], 0, pagination.Page, pagination.PageSize);

        return Ok(result);
    }

    /// <summary>
    /// Gets a specific metaverse attribute by ID.
    /// </summary>
    /// <param name="id">The unique identifier of the attribute.</param>
    /// <returns>The attribute details.</returns>
    [HttpGet("attributes/{id:int}", Name = "GetAttribute")]
    [ProducesResponseType(typeof(MetaverseAttributeDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetAttributeAsync(int id)
    {
        _logger.LogTrace("Requested attribute: {Id}", id);
        var attribute = await _application.Metaverse.GetMetaverseAttributeAsync(id);
        if (attribute == null)
            return NotFound(ApiErrorResponse.NotFound($"Attribute with ID {id} not found."));

        return Ok(MetaverseAttributeDetailDto.FromEntity(attribute));
    }

    /// <summary>
    /// Creates a new metaverse attribute.
    /// </summary>
    /// <param name="request">The attribute creation request.</param>
    /// <returns>The created attribute details.</returns>
    /// <response code="201">Attribute created successfully.</response>
    /// <response code="400">Invalid request or validation failed.</response>
    /// <response code="401">User not authenticated.</response>
    [HttpPost("attributes", Name = "CreateAttribute")]
    [ProducesResponseType(typeof(MetaverseAttributeDetailDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> CreateAttributeAsync([FromBody] CreateMetaverseAttributeRequest request)
    {
        _logger.LogInformation("Creating metaverse attribute: {Name}", request.Name);

        // Check if attribute with same name already exists
        var existing = await _application.Metaverse.GetMetaverseAttributeAsync(request.Name);
        if (existing != null)
            return BadRequest(ApiErrorResponse.BadRequest($"Attribute with name '{request.Name}' already exists."));

        var attribute = new MetaverseAttribute
        {
            Name = request.Name,
            Type = request.Type,
            AttributePlurality = request.AttributePlurality,
            Created = DateTime.UtcNow,
            MetaverseObjectTypes = new List<MetaverseObjectType>()
        };

        // Associate with object types if specified
        if (request.ObjectTypeIds != null && request.ObjectTypeIds.Count > 0)
        {
            foreach (var objectTypeId in request.ObjectTypeIds)
            {
                var objectType = await _application.Metaverse.GetMetaverseObjectTypeAsync(objectTypeId, false);
                if (objectType == null)
                    return BadRequest(ApiErrorResponse.BadRequest($"Object type with ID {objectTypeId} not found."));

                attribute.MetaverseObjectTypes.Add(objectType);
            }
        }

        // Get the current API key for Activity attribution
        var apiKey = await GetCurrentApiKeyAsync();
        if (apiKey != null)
            await _application.Metaverse.CreateMetaverseAttributeAsync(attribute, apiKey);
        else
            await _application.Metaverse.CreateMetaverseAttributeAsync(attribute, (MetaverseObject?)null);

        _logger.LogInformation("Created metaverse attribute: {Id} ({Name})", attribute.Id, attribute.Name);

        var result = await _application.Metaverse.GetMetaverseAttributeAsync(attribute.Id);
        // Use Created with explicit URL instead of CreatedAtAction to avoid API versioning route generation issues
        return Created($"/api/v1/metaverse/attributes/{attribute.Id}", MetaverseAttributeDetailDto.FromEntity(result!));
    }

    /// <summary>
    /// Updates an existing metaverse attribute.
    /// </summary>
    /// <param name="id">The unique identifier of the attribute.</param>
    /// <param name="request">The attribute update request.</param>
    /// <returns>The updated attribute details.</returns>
    /// <response code="200">Attribute updated successfully.</response>
    /// <response code="400">Invalid request or validation failed.</response>
    /// <response code="404">Attribute not found.</response>
    /// <response code="401">User not authenticated.</response>
    [HttpPut("attributes/{id:int}", Name = "UpdateAttribute")]
    [ProducesResponseType(typeof(MetaverseAttributeDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> UpdateAttributeAsync(int id, [FromBody] UpdateMetaverseAttributeRequest request)
    {
        _logger.LogInformation("Updating metaverse attribute: {Id}", id);

        // If we're updating object type associations, include them in the query to properly manage the many-to-many relationship
        var attribute = request.ObjectTypeIds != null
            ? await _application.Metaverse.GetMetaverseAttributeWithObjectTypesAsync(id)
            : await _application.Metaverse.GetMetaverseAttributeAsync(id);

        if (attribute == null)
            return NotFound(ApiErrorResponse.NotFound($"Attribute with ID {id} not found."));

        if (attribute.BuiltIn)
            return BadRequest(ApiErrorResponse.BadRequest("Cannot modify built-in attributes."));

        // Apply updates
        if (!string.IsNullOrEmpty(request.Name))
        {
            // Check if new name conflicts with existing
            var existing = await _application.Metaverse.GetMetaverseAttributeAsync(request.Name);
            if (existing != null && existing.Id != id)
                return BadRequest(ApiErrorResponse.BadRequest($"Attribute with name '{request.Name}' already exists."));
            attribute.Name = request.Name;
        }

        if (request.Type.HasValue)
            attribute.Type = request.Type.Value;

        if (request.AttributePlurality.HasValue)
            attribute.AttributePlurality = request.AttributePlurality.Value;

        // Update object type associations if specified
        if (request.ObjectTypeIds != null)
        {
            // Collection is loaded from the query when ObjectTypeIds is specified
            attribute.MetaverseObjectTypes.Clear();
            foreach (var objectTypeId in request.ObjectTypeIds)
            {
                var objectType = await _application.Metaverse.GetMetaverseObjectTypeAsync(objectTypeId, false);
                if (objectType == null)
                    return BadRequest(ApiErrorResponse.BadRequest($"Object type with ID {objectTypeId} not found."));

                attribute.MetaverseObjectTypes.Add(objectType);
            }
        }

        // Get the current API key for Activity attribution
        var apiKey = await GetCurrentApiKeyAsync();
        if (apiKey != null)
            await _application.Metaverse.UpdateMetaverseAttributeAsync(attribute, apiKey);
        else
            await _application.Metaverse.UpdateMetaverseAttributeAsync(attribute, (MetaverseObject?)null);

        _logger.LogInformation("Updated metaverse attribute: {Id} ({Name})", attribute.Id, attribute.Name);

        var result = await _application.Metaverse.GetMetaverseAttributeAsync(attribute.Id);
        return Ok(MetaverseAttributeDetailDto.FromEntity(result!));
    }

    /// <summary>
    /// Deletes a metaverse attribute.
    /// </summary>
    /// <param name="id">The unique identifier of the attribute to delete.</param>
    /// <returns>No content on success.</returns>
    /// <response code="204">Attribute deleted successfully.</response>
    /// <response code="400">Cannot delete built-in or in-use attribute.</response>
    /// <response code="404">Attribute not found.</response>
    /// <response code="401">User not authenticated.</response>
    [HttpDelete("attributes/{id:int}", Name = "DeleteAttribute")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> DeleteAttributeAsync(int id)
    {
        _logger.LogInformation("Deleting metaverse attribute: {Id}", id);

        var attribute = await _application.Metaverse.GetMetaverseAttributeAsync(id);
        if (attribute == null)
            return NotFound(ApiErrorResponse.NotFound($"Attribute with ID {id} not found."));

        if (attribute.BuiltIn)
            return BadRequest(ApiErrorResponse.BadRequest("Cannot delete built-in attributes."));

        // Get the current API key for Activity attribution
        var apiKey = await GetCurrentApiKeyAsync();
        if (apiKey != null)
            await _application.Metaverse.DeleteMetaverseAttributeAsync(attribute, apiKey);
        else
            await _application.Metaverse.DeleteMetaverseAttributeAsync(attribute, (MetaverseObject?)null);

        _logger.LogInformation("Deleted metaverse attribute: {Id}", id);

        return NoContent();
    }

    /// <summary>
    /// Gets a paginated list of metaverse objects with optional filtering.
    /// </summary>
    /// <remarks>
    /// The DisplayName attribute is always included in the response. Use the `attributes` parameter
    /// to request additional attributes to be included. This follows a common pattern in APIs and
    /// PowerShell modules where clients can specify which properties to retrieve.
    ///
    /// Use `?attributes=*` to include all attributes.
    ///
    /// **Filtering:**
    /// - `search` - Searches display name (partial match, case-insensitive)
    /// - `filterAttributeName` + `filterAttributeValue` - Filters by specific attribute (exact match, case-insensitive)
    ///
    /// Examples:
    /// - `?attributes=FirstName&amp;attributes=LastName&amp;attributes=Email` - Include specific attributes
    /// - `?attributes=*` - Include all attributes
    /// - `?filterAttributeName=Account Name&amp;filterAttributeValue=jsmith` - Find by account name
    /// </remarks>
    /// <param name="pagination">Pagination parameters (page, pageSize, sortBy, sortDirection).</param>
    /// <param name="objectTypeId">Optional object type ID to filter by.</param>
    /// <param name="search">Optional search query to filter by display name.</param>
    /// <param name="attributes">Optional list of attribute names to include in the response. Use "*" for all attributes. DisplayName is always included.</param>
    /// <param name="filterAttributeName">Optional attribute name to filter by (must be used with filterAttributeValue).</param>
    /// <param name="filterAttributeValue">Optional attribute value to filter by (exact match, case-insensitive).</param>
    /// <returns>A paginated list of metaverse object headers.</returns>
    [HttpGet("objects", Name = "GetObjects")]
    [ProducesResponseType(typeof(PaginatedResponse<MetaverseObjectHeaderDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetObjectsAsync(
        [FromQuery] PaginationRequest pagination,
        [FromQuery] int? objectTypeId = null,
        [FromQuery] string? search = null,
        [FromQuery] IEnumerable<string>? attributes = null,
        [FromQuery] string? filterAttributeName = null,
        [FromQuery] string? filterAttributeValue = null)
    {
        _logger.LogDebug("Getting metaverse objects (Page: {Page}, PageSize: {PageSize}, TypeId: {TypeId}, Search: {Search}, FilterAttr: {FilterAttr}={FilterValue}, Attributes: {Attributes})",
            pagination.Page, pagination.PageSize, objectTypeId, search, filterAttributeName, filterAttributeValue,
            attributes != null ? string.Join(",", attributes) : "DisplayName only");

        var result = await _application.Metaverse.GetMetaverseObjectsAsync(
            page: pagination.Page,
            pageSize: pagination.PageSize,
            objectTypeId: objectTypeId,
            searchQuery: search,
            sortDescending: pagination.IsDescending,
            attributes: attributes,
            filterAttributeName: filterAttributeName,
            filterAttributeValue: filterAttributeValue);

        var headers = result.Results.Select(MetaverseObjectHeaderDto.FromHeader);

        var response = new PaginatedResponse<MetaverseObjectHeaderDto>
        {
            Items = headers,
            TotalCount = result.TotalResults,
            Page = result.CurrentPage,
            PageSize = result.PageSize
        };

        return Ok(response);
    }

    /// <summary>
    /// Gets a specific metaverse object by ID.
    /// </summary>
    /// <param name="id">The unique identifier (GUID) of the metaverse object.</param>
    /// <returns>The metaverse object details including all attribute values.</returns>
    [HttpGet("objects/{id:guid}", Name = "GetObject")]
    [ProducesResponseType(typeof(MetaverseObjectDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetObjectAsync(Guid id)
    {
        _logger.LogTrace("Requested metaverse object: {Id}", id);
        var obj = await _application.Metaverse.GetMetaverseObjectAsync(id);
        if (obj == null)
            return NotFound(ApiErrorResponse.NotFound($"Metaverse object with ID {id} not found."));

        return Ok(MetaverseObjectDto.FromEntity(obj));
    }

    /// <summary>
    /// Gets a paginated list of metaverse objects pending deletion.
    /// </summary>
    /// <remarks>
    /// Returns MVOs that have been disconnected from their last connector and are awaiting
    /// automatic deletion after their grace period expires. Use this endpoint to monitor
    /// identities scheduled for cleanup.
    /// </remarks>
    /// <param name="pagination">Pagination parameters (page, pageSize).</param>
    /// <param name="objectTypeId">Optional object type ID to filter by.</param>
    /// <returns>A paginated list of MVOs pending deletion.</returns>
    [HttpGet("pending-deletions", Name = "GetPendingDeletions")]
    [ProducesResponseType(typeof(PaginatedResponse<PendingDeletionDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetPendingDeletionsAsync(
        [FromQuery] PaginationRequest pagination,
        [FromQuery] int? objectTypeId = null)
    {
        _logger.LogDebug("Getting pending deletions (Page: {Page}, PageSize: {PageSize}, TypeId: {TypeId})",
            pagination.Page, pagination.PageSize, objectTypeId);

        var result = await _application.Repository.Metaverse.GetMetaverseObjectsPendingDeletionAsync(
            page: pagination.Page,
            pageSize: pagination.PageSize,
            objectTypeId: objectTypeId);

        var dtos = result.Results.Select(PendingDeletionDto.FromEntity);

        var response = new PaginatedResponse<PendingDeletionDto>
        {
            Items = dtos,
            TotalCount = result.TotalResults,
            Page = result.CurrentPage,
            PageSize = result.PageSize
        };

        return Ok(response);
    }

    /// <summary>
    /// Gets the count of metaverse objects pending deletion.
    /// </summary>
    /// <param name="objectTypeId">Optional object type ID to filter by.</param>
    /// <returns>The count of MVOs pending deletion.</returns>
    [HttpGet("pending-deletions/count", Name = "GetPendingDeletionsCount")]
    [ProducesResponseType(typeof(int), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetPendingDeletionsCountAsync([FromQuery] int? objectTypeId = null)
    {
        _logger.LogDebug("Getting pending deletions count (TypeId: {TypeId})", objectTypeId);
        var count = await _application.Repository.Metaverse.GetMetaverseObjectsPendingDeletionCountAsync(objectTypeId);
        return Ok(count);
    }

    /// <summary>
    /// Gets summary statistics for pending deletions.
    /// </summary>
    /// <remarks>
    /// Provides an overview of deletion status including total count and counts by status:
    /// - Deprovisioning: MVOs still connected to other systems, awaiting cascade deletion
    /// - AwaitingGracePeriod: MVOs fully disconnected, waiting for grace period to expire
    /// - ReadyForDeletion: MVOs eligible for deletion (grace period expired, no connectors)
    /// </remarks>
    /// <returns>Summary statistics for pending deletions.</returns>
    [HttpGet("pending-deletions/summary", Name = "GetPendingDeletionsSummary")]
    [ProducesResponseType(typeof(PendingDeletionSummary), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetPendingDeletionsSummaryAsync()
    {
        _logger.LogDebug("Getting pending deletions summary");

        // Get all pending deletions to calculate summary
        var result = await _application.Repository.Metaverse.GetMetaverseObjectsPendingDeletionAsync(
            page: 1,
            pageSize: 100,
            objectTypeId: null);

        var now = DateTime.UtcNow;
        var allPending = result.Results;

        // Get total count (may be more than 100)
        var totalCount = await _application.Repository.Metaverse.GetMetaverseObjectsPendingDeletionCountAsync();

        // Calculate status counts
        var deprovisioningCount = allPending.Count(m => m.ConnectedSystemObjects.Any());
        var readyForDeletionCount = allPending.Count(m =>
            !m.ConnectedSystemObjects.Any() &&
            (!m.DeletionEligibleDate.HasValue || m.DeletionEligibleDate.Value <= now));
        var awaitingGracePeriodCount = allPending.Count(m =>
            !m.ConnectedSystemObjects.Any() &&
            m.DeletionEligibleDate.HasValue &&
            m.DeletionEligibleDate.Value > now);

        var summary = new PendingDeletionSummary
        {
            TotalCount = totalCount,
            DeprovisioningCount = deprovisioningCount,
            AwaitingGracePeriodCount = awaitingGracePeriodCount,
            ReadyForDeletionCount = readyForDeletionCount
        };

        return Ok(summary);
    }

    #region Private Helpers

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
