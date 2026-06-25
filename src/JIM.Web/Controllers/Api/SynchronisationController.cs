// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using Asp.Versioning;
using JIM.Web.Extensions.Api;
using JIM.Web.Models.Api;
using JIM.Application;
using JIM.Application.Interfaces;
using JIM.Models.Expressions;
using JIM.Models.Interfaces;
using JIM.Application.Services;
using JIM.Models.Logic;
using JIM.Models.Logic.DTOs;
using JIM.Models.Search;
using JIM.Models.Staging;
using JIM.Models.Staging.DTOs;
using JIM.Models.Transactional;
using JIM.Models.Transactional.DTOs;
using JIM.Models.Tasking;
using JIM.Utilities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace JIM.Web.Controllers.Api;

/// <summary>
/// API controller for managing synchronisation configuration including Connected Systems and Synchronisation Rules.
/// </summary>
/// <remarks>
/// This controller provides endpoints for managing the synchronisation infrastructure:
/// - Connected Systems: External identity stores that sync with the Metaverse
/// - Synchronisation Rules: Configuration for how data flows between Connected Systems and the Metaverse
/// </remarks>
[Route("api/v{version:apiVersion}/[controller]")]
[ApiController]
[ApiVersion("1.0")]
[Authorize(Roles = "Administrator")]
[Produces("application/json")]
public class SynchronisationController(
    ILogger<SynchronisationController> logger,
    JimApplication application,
    IExpressionEvaluator expressionEvaluator,
    ICredentialProtectionService credentialProtection) : ControllerBase
{
    private readonly ILogger<SynchronisationController> _logger = logger;
    private readonly JimApplication _application = application;
    private readonly IExpressionEvaluator _expressionEvaluator = expressionEvaluator;
    private readonly ICredentialProtectionService _credentialProtection = credentialProtection;

    #region Connected Systems

    /// <summary>
    /// List Connected Systems
    /// </summary>
    /// <param name="pagination">Pagination parameters (page, pageSize, sortBy, sortDirection, filter).</param>
    /// <returns>A paginated list of Connected System headers.</returns>
    [HttpGet("connected-systems", Name = "GetConnectedSystems")]
    [ProducesResponseType(typeof(PaginatedResponse<ConnectedSystemHeader>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetConnectedSystemsAsync([FromQuery] PaginationRequest pagination)
    {
        _logger.LogTrace("Requested Connected Systems (Page: {Page}, PageSize: {PageSize})", pagination.Page, pagination.PageSize);
        // Use GetConnectedSystemHeadersAsync which correctly computes PendingExportObjectsCount via SQL COUNT subquery.
        // The previous implementation used GetConnectedSystemsAsync().Select(FromEntity) which didn't load
        // the PendingExports navigation property, resulting in PendingExportObjectsCount always being 0.
        var headers = await _application.ConnectedSystems.GetConnectedSystemHeadersAsync();

        var result = headers.AsQueryable()
            .ApplySortAndFilter(pagination)
            .ToPaginatedResponse(pagination);

        return Ok(result);
    }

    /// <summary>
    /// Get a Connected System
    /// </summary>
    /// <param name="connectedSystemId">The unique identifier of the Connected System.</param>
    /// <returns>The Connected System details including configuration and schema.</returns>
    [HttpGet("connected-systems/{connectedSystemId:int}", Name = "GetConnectedSystem")]
    [ProducesResponseType(typeof(ConnectedSystemDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetConnectedSystemAsync(int connectedSystemId)
    {
        _logger.LogTrace("Requested Connected System: {Id}", connectedSystemId);
        var system = await _application.ConnectedSystems.GetConnectedSystemAsync(connectedSystemId);
        if (system == null)
            return NotFound(ApiErrorResponse.NotFound($"Connected System with ID {connectedSystemId} not found."));

        // GetConnectedSystemAsync doesn't load PendingExports or Objects (too expensive for
        // the detail query and can be very large). Compute counts via dedicated queries,
        // matching how the Blazor UI does it.
        var pendingExportCount = await _application.ConnectedSystems.GetPendingExportsCountAsync(connectedSystemId);
        var objectCount = await _application.ConnectedSystems.GetConnectedSystemObjectCountAsync(connectedSystemId);

        return Ok(ConnectedSystemDetailDto.FromEntity(system, pendingExportCount, objectCount));
    }

    /// <summary>
    /// List Object Types for a Connected System
    /// </summary>
    /// <param name="connectedSystemId">The unique identifier of the Connected System.</param>
    /// <returns>A list of Object Types with their Attributes.</returns>
    [HttpGet("connected-systems/{connectedSystemId:int}/object-types", Name = "GetConnectedSystemObjectTypes")]
    [ProducesResponseType(typeof(IEnumerable<ConnectedSystemObjectTypeDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetConnectedSystemObjectTypesAsync(int connectedSystemId)
    {
        _logger.LogTrace("Requested object types for Connected System: {Id}", connectedSystemId);
        var objectTypes = await _application.ConnectedSystems.GetObjectTypesAsync(connectedSystemId);
        if (objectTypes == null)
            return NotFound(ApiErrorResponse.NotFound($"Connected System with ID {connectedSystemId} not found."));

        var dtos = objectTypes.Select(ConnectedSystemObjectTypeDto.FromEntity);
        return Ok(dtos);
    }

    /// <summary>
    /// Update an Object Type
    /// </summary>
    /// <remarks>
    /// Use this endpoint to update properties of an Object Type, such as:
    /// - Selected: Whether the Object Type is managed by JIM
    /// - RemoveContributedAttributesOnObsoletion: Whether MVO Attributes are removed when CSO is obsoleted
    /// </remarks>
    /// <param name="connectedSystemId">The unique identifier of the Connected System.</param>
    /// <param name="objectTypeId">The unique identifier of the Object Type.</param>
    /// <param name="request">The update request with new values.</param>
    /// <returns>The updated Object Type details.</returns>
    /// <response code="200">Object Type updated successfully.</response>
    /// <response code="400">Invalid request or validation failed.</response>
    /// <response code="404">Connected System or Object Type not found.</response>
    /// <response code="401">User could not be identified from authentication token.</response>
    [HttpPut("connected-systems/{connectedSystemId:int}/object-types/{objectTypeId:int}", Name = "UpdateConnectedSystemObjectType")]
    [ProducesResponseType(typeof(ConnectedSystemObjectTypeDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> UpdateConnectedSystemObjectTypeAsync(int connectedSystemId, int objectTypeId, [FromBody] UpdateConnectedSystemObjectTypeRequest request)
    {
        _logger.LogInformation("Updating object type {ObjectTypeId} for Connected System {SystemId}", objectTypeId, connectedSystemId);

        var initiatedBy = await GetCurrentUserAsync();
        if (initiatedBy == null && !IsApiKeyAuthenticated())
        {
            _logger.LogWarning("Could not identify user from JWT claims for object type update");
            return Unauthorized(ApiErrorResponse.Unauthorised("Could not identify user from authentication token."));
        }

        // Verify Connected System exists (Core retrieval — we only need existence, not the full graph)
        var connectedSystem = await _application.ConnectedSystems.GetConnectedSystemCoreAsync(connectedSystemId);
        if (connectedSystem == null)
            return NotFound(ApiErrorResponse.NotFound($"Connected System with ID {connectedSystemId} not found."));

        // Get the object type
        var objectType = await _application.ConnectedSystems.GetObjectTypeAsync(objectTypeId);
        if (objectType == null || objectType.ConnectedSystemId != connectedSystemId)
            return NotFound(ApiErrorResponse.NotFound($"Object type with ID {objectTypeId} not found in Connected System {connectedSystemId}."));

        // Apply updates
        if (request.Selected.HasValue)
            objectType.Selected = request.Selected.Value;

        if (request.RemoveContributedAttributesOnObsoletion.HasValue)
            objectType.RemoveContributedAttributesOnObsoletion = request.RemoveContributedAttributesOnObsoletion.Value;

        // Get the current API key for Activity attribution if authenticated via API key
        var apiKey = await GetCurrentApiKeyAsync();
        if (apiKey != null)
            await _application.ConnectedSystems.UpdateObjectTypeAsync(objectType, apiKey);
        else
            await _application.ConnectedSystems.UpdateObjectTypeAsync(objectType, initiatedBy);

        _logger.LogInformation("Updated object type {ObjectTypeId} ({Name})", objectType.Id, objectType.Name);

        // Return the updated object type
        var updated = await _application.ConnectedSystems.GetObjectTypeAsync(objectTypeId);
        return Ok(ConnectedSystemObjectTypeDto.FromEntity(updated!));
    }

    /// <summary>
    /// Update an Attribute
    /// </summary>
    /// <remarks>
    /// Use this endpoint to update properties of an Attribute, such as:
    /// - Selected: Whether the Attribute is managed by JIM
    /// - IsExternalId: Whether this is the unique identifier for objects
    /// - IsSecondaryExternalId: Whether this is a secondary identifier (e.g., DN for LDAP)
    /// </remarks>
    /// <param name="connectedSystemId">The unique identifier of the Connected System.</param>
    /// <param name="objectTypeId">The unique identifier of the Object Type.</param>
    /// <param name="attributeId">The unique identifier of the Attribute.</param>
    /// <param name="request">The update request with new values.</param>
    /// <returns>The updated Attribute details.</returns>
    /// <response code="200">Attribute updated successfully.</response>
    /// <response code="400">Invalid request or validation failed.</response>
    /// <response code="404">Connected System, Object Type, or Attribute not found.</response>
    /// <response code="401">User could not be identified from authentication token.</response>
    [HttpPut("connected-systems/{connectedSystemId:int}/object-types/{objectTypeId:int}/attributes/{attributeId:int}", Name = "UpdateConnectedSystemAttribute")]
    [ProducesResponseType(typeof(ConnectedSystemAttributeDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> UpdateConnectedSystemAttributeAsync(int connectedSystemId, int objectTypeId, int attributeId, [FromBody] UpdateConnectedSystemAttributeRequest request)
    {
        _logger.LogInformation("Updating attribute {AttributeId} for object type {ObjectTypeId} in Connected System {SystemId}", attributeId, objectTypeId, connectedSystemId);

        var initiatedBy = await GetCurrentUserAsync();
        if (initiatedBy == null && !IsApiKeyAuthenticated())
        {
            _logger.LogWarning("Could not identify user from JWT claims for attribute update");
            return Unauthorized(ApiErrorResponse.Unauthorised("Could not identify user from authentication token."));
        }

        // Verify Connected System exists (Core retrieval — we only need existence, not the full graph)
        var connectedSystem = await _application.ConnectedSystems.GetConnectedSystemCoreAsync(connectedSystemId);
        if (connectedSystem == null)
            return NotFound(ApiErrorResponse.NotFound($"Connected System with ID {connectedSystemId} not found."));

        // Get the attribute
        var attribute = await _application.ConnectedSystems.GetAttributeAsync(attributeId);
        if (attribute == null)
            return NotFound(ApiErrorResponse.NotFound($"Attribute with ID {attributeId} not found."));

        // Verify attribute belongs to the specified object type and Connected System
        if (attribute.ConnectedSystemObjectType.Id != objectTypeId ||
            attribute.ConnectedSystemObjectType.ConnectedSystemId != connectedSystemId)
        {
            return NotFound(ApiErrorResponse.NotFound($"Attribute with ID {attributeId} not found in object type {objectTypeId} of Connected System {connectedSystemId}."));
        }

        // Validate: Cannot unselect an External ID or Secondary External ID attribute
        if (request.Selected.HasValue && !request.Selected.Value && (attribute.IsExternalId || attribute.IsSecondaryExternalId))
        {
            var idType = attribute.IsExternalId ? "External ID" : "Secondary External ID";
            _logger.LogWarning("Attempted to unselect {IdType} attribute {AttributeId} ({Name})", idType, attributeId, attribute.Name);
            return BadRequest(ApiErrorResponse.BadRequest(
                $"Cannot unselect attribute '{attribute.Name}' because it is the {idType} attribute. " +
                "These attributes must remain selected to ensure sync operations function correctly."));
        }

        // Apply updates
        if (request.Selected.HasValue)
            attribute.Selected = request.Selected.Value;

        // Get the current API key for Activity attribution if authenticated via API key
        var apiKey = await GetCurrentApiKeyAsync();

        if (request.IsExternalId.HasValue && request.IsExternalId.Value)
        {
            // Clear existing external ID on other attributes in the same object type
            // There can only be one external ID per object type
            var objectType = await _application.ConnectedSystems.GetObjectTypeAsync(objectTypeId);
            if (objectType?.Attributes != null)
            {
                foreach (var attr in objectType.Attributes.Where(a => a.IsExternalId && a.Id != attributeId))
                {
                    attr.IsExternalId = false;
                    if (apiKey != null)
                        await _application.ConnectedSystems.UpdateAttributeAsync(attr, apiKey);
                    else
                        await _application.ConnectedSystems.UpdateAttributeAsync(attr, initiatedBy);
                }
            }
            attribute.IsExternalId = true;
            // External ID attributes must always be selected for sync operations to work
            attribute.Selected = true;
        }
        else if (request.IsExternalId.HasValue)
        {
            attribute.IsExternalId = request.IsExternalId.Value;
        }

        if (request.IsSecondaryExternalId.HasValue)
        {
            attribute.IsSecondaryExternalId = request.IsSecondaryExternalId.Value;
            // Secondary External ID attributes must always be selected for sync operations to work
            if (request.IsSecondaryExternalId.Value)
                attribute.Selected = true;
        }

        if (apiKey != null)
            await _application.ConnectedSystems.UpdateAttributeAsync(attribute, apiKey);
        else
            await _application.ConnectedSystems.UpdateAttributeAsync(attribute, initiatedBy);

        _logger.LogInformation("Updated attribute {AttributeId} ({Name})", attribute.Id, attribute.Name);

        // Return the updated attribute
        var updated = await _application.ConnectedSystems.GetAttributeAsync(attributeId);
        return Ok(ConnectedSystemAttributeDto.FromEntity(updated!));
    }

    /// <summary>
    /// Bulk update Attributes for an Object Type
    /// </summary>
    /// <remarks>
    /// Updates multiple Attributes in a single operation, creating one Activity record for the entire batch rather than individual records per Attribute.
    /// </remarks>
    /// <param name="connectedSystemId">The unique identifier of the Connected System.</param>
    /// <param name="objectTypeId">The unique identifier of the Object Type containing the Attributes.</param>
    /// <param name="request">Dictionary of Attribute updates keyed by Attribute ID.</param>
    /// <returns>Response containing the Activity ID, updated count, updated Attributes, and any errors.</returns>
    /// <response code="200">Attributes updated successfully (may include partial success with errors).</response>
    /// <response code="400">Invalid request or empty Attributes dictionary.</response>
    /// <response code="404">Connected System or Object Type not found.</response>
    /// <response code="401">User could not be identified from authentication token.</response>
    [HttpPut("connected-systems/{connectedSystemId:int}/object-types/{objectTypeId:int}/attributes", Name = "BulkUpdateConnectedSystemAttributes")]
    [ProducesResponseType(typeof(BulkUpdateConnectedSystemAttributesResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> BulkUpdateConnectedSystemAttributesAsync(
        int connectedSystemId,
        int objectTypeId,
        [FromBody] BulkUpdateConnectedSystemAttributesRequest request)
    {
        var attributeCount = request.Attributes?.Count ?? 0;
        _logger.LogInformation("Bulk updating {Count} attributes for object type {ObjectTypeId} in Connected System {SystemId}",
            attributeCount, objectTypeId, connectedSystemId);

        if (request.Attributes == null || request.Attributes.Count == 0)
        {
            return BadRequest(ApiErrorResponse.BadRequest("Attributes dictionary cannot be null or empty."));
        }

        var initiatedBy = await GetCurrentUserAsync();
        if (initiatedBy == null && !IsApiKeyAuthenticated())
        {
            _logger.LogWarning("Could not identify user from JWT claims for bulk attribute update");
            return Unauthorized(ApiErrorResponse.Unauthorised("Could not identify user from authentication token."));
        }

        // Verify Connected System exists (Core retrieval — BulkUpdateAttributesAsync only reads
        // the Connected System's Id/Name for activity attribution, not its full graph).
        var connectedSystem = await _application.ConnectedSystems.GetConnectedSystemCoreAsync(connectedSystemId);
        if (connectedSystem == null)
            return NotFound(ApiErrorResponse.NotFound($"Connected System with ID {connectedSystemId} not found."));

        // Get the object type with attributes
        var objectType = await _application.ConnectedSystems.GetObjectTypeAsync(objectTypeId);
        if (objectType == null || objectType.ConnectedSystemId != connectedSystemId)
            return NotFound(ApiErrorResponse.NotFound($"Object type with ID {objectTypeId} not found in Connected System {connectedSystemId}."));

        // Convert request DTOs to the format expected by the server
        var attributeUpdates = request.Attributes.ToDictionary(
            kvp => kvp.Key,
            kvp => (kvp.Value.Selected, kvp.Value.IsExternalId, kvp.Value.IsSecondaryExternalId)
        );

        // Get the current API key for Activity attribution if authenticated via API key
        var apiKey = await GetCurrentApiKeyAsync();

        // Call the bulk update method
        var (activity, updated, errors) = apiKey != null
            ? await _application.ConnectedSystems.BulkUpdateAttributesAsync(connectedSystem, objectType, attributeUpdates, apiKey)
            : await _application.ConnectedSystems.BulkUpdateAttributesAsync(connectedSystem, objectType, attributeUpdates, initiatedBy);

        _logger.LogInformation("Bulk update completed: {UpdatedCount} attributes updated, {ErrorCount} errors",
            updated.Count, errors.Count);

        // Build the response
        var response = new BulkUpdateConnectedSystemAttributesResponse
        {
            ActivityId = activity.Id,
            UpdatedCount = updated.Count,
            UpdatedAttributes = updated.Select(ConnectedSystemAttributeDto.FromEntity).ToList(),
            Errors = errors.Count > 0
                ? errors.Select(e => new BulkUpdateAttributeError { AttributeId = e.AttributeId, ErrorMessage = e.Error }).ToList()
                : null
        };

        return Ok(response);
    }

    /// <summary>
    /// Get a Connected System Object
    /// </summary>
    /// <param name="connectedSystemId">The unique identifier of the Connected System.</param>
    /// <param name="id">The unique identifier (GUID) of the Connected System Object.</param>
    /// <returns>The Connected System Object details with capped MVA values and per-attribute summaries.</returns>
    [HttpGet("connected-systems/{connectedSystemId:int}/connector-space/{id:guid}", Name = "GetConnectedSystemObject")]
    [ProducesResponseType(typeof(ConnectedSystemObjectDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetConnectedSystemObjectAsync(int connectedSystemId, Guid id)
    {
        _logger.LogTrace("Requested object {ObjectId} for Connected System: {SystemId}", id, connectedSystemId);
        var result = await _application.ConnectedSystems.GetConnectedSystemObjectDetailAsync(
            connectedSystemId, id, CsoAttributeLoadStrategy.CappedMva);
        if (result == null)
            return NotFound(ApiErrorResponse.NotFound($"Object with ID {id} not found in Connected System {connectedSystemId}."));

        return Ok(ConnectedSystemObjectDetailDto.FromDetailResult(result));
    }

    /// <summary>
    /// List the change history for a Connected System Object
    /// </summary>
    /// <remarks>
    /// Returns a paginated list of change records for the specified Connected System Object,
    /// ordered by change time descending (most recent first). Each row carries the initiator
    /// and Run Profile context, plus the per-attribute value changes.
    /// </remarks>
    /// <param name="connectedSystemId">The unique identifier of the Connected System.</param>
    /// <param name="csoId">The unique identifier (GUID) of the Connected System Object.</param>
    /// <param name="pagination">Pagination parameters (page, pageSize). Page size is clamped to [1, 100].</param>
    /// <returns>A paginated list of change-history records.</returns>
    [HttpGet("connected-systems/{connectedSystemId:int}/connector-space/{csoId:guid}/change-history", Name = "GetConnectedSystemObjectChangeHistory")]
    [ProducesResponseType(typeof(PaginatedResponse<CsoChangeHistoryDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetConnectedSystemObjectChangeHistoryAsync(int connectedSystemId, Guid csoId, [FromQuery] PaginationRequest pagination)
    {
        _logger.LogTrace("Requested change history for CSO {CsoId} in Connected System {SystemId}", csoId, connectedSystemId);

        // Verify the CSO exists in this Connected System so a missing id returns 404 rather than an empty page.
        var cso = await _application.ConnectedSystems.GetConnectedSystemObjectAsync(connectedSystemId, csoId);
        if (cso == null)
            return NotFound(ApiErrorResponse.NotFound($"Object with ID {csoId} not found in Connected System {connectedSystemId}."));

        var (items, totalCount) = await _application.ConnectedSystems.GetCsoChangeHistoryAsync(csoId, pagination.Page, pagination.PageSize);
        return Ok(PaginatedResponse<CsoChangeHistoryDto>.Create(items, totalCount, pagination.Page, pagination.PageSize));
    }

    /// <summary>
    /// List Attribute Values for a Connected System Object
    /// </summary>
    /// <remarks>
    /// Use this endpoint to retrieve large multi-valued Attribute data (e.g. group members)
    /// with server-side search and pagination. The CSO detail endpoint caps MVA values;
    /// use this endpoint to page through all values.
    /// </remarks>
    /// <param name="connectedSystemId">The unique identifier of the Connected System.</param>
    /// <param name="csoId">The unique identifier (GUID) of the Connected System Object.</param>
    /// <param name="attributeName">The Attribute name to retrieve values for.</param>
    /// <param name="page">Page number (1-based). Default: 1.</param>
    /// <param name="pageSize">Number of values per page (1-100). Default: 50.</param>
    /// <param name="search">Optional search text to filter values.</param>
    /// <returns>A paginated set of Attribute Values with total count.</returns>
    [HttpGet("connected-systems/{connectedSystemId:int}/connector-space/{csoId:guid}/attributes/{attributeName}/values", Name = "GetAttributeValuesPaged")]
    [ProducesResponseType(typeof(PaginatedResponse<ConnectedSystemObjectAttributeValueDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetAttributeValuesPagedAsync(
        int connectedSystemId,
        Guid csoId,
        string attributeName,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        [FromQuery] string? search = null)
    {
        if (page < 1) page = 1;
        if (pageSize < 1) pageSize = 50;
        if (pageSize > 100) pageSize = 100;

        var result = await _application.ConnectedSystems.GetAttributeValuesPagedAsync(
            csoId, attributeName, page, pageSize, search);

        return Ok(new PaginatedResponse<ConnectedSystemObjectAttributeValueDto>
        {
            Items = result.Results.Select(ConnectedSystemObjectAttributeValueDto.FromEntity),
            TotalCount = result.TotalResults,
            Page = result.CurrentPage,
            PageSize = result.PageSize
        });
    }

    /// <summary>
    /// Get a deletion preview for a Connected System
    /// </summary>
    /// <remarks>
    /// Call this before deleting a Connected System to understand the impact. The preview includes counts of Connected System Objects, Synchronisation Rules, Metaverse Objects, and Pending Exports that will be affected.
    /// </remarks>
    /// <param name="connectedSystemId">The unique identifier of the Connected System.</param>
    /// <returns>A preview showing counts of affected objects and any warnings.</returns>
    [HttpGet("connected-systems/{connectedSystemId:int}/deletion-preview", Name = "GetConnectedSystemDeletionPreview")]
    [ProducesResponseType(typeof(ConnectedSystemDeletionPreview), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetConnectedSystemDeletionPreviewAsync(int connectedSystemId)
    {
        _logger.LogInformation("Deletion preview requested for Connected System: {Id}", connectedSystemId);

        var preview = await _application.ConnectedSystems.GetDeletionPreviewAsync(connectedSystemId);
        if (preview == null)
            return NotFound(ApiErrorResponse.NotFound($"Connected System with ID {connectedSystemId} not found."));

        return Ok(preview);
    }

    /// <summary>
    /// Get the unresolved reference count for a Connected System
    /// </summary>
    /// <remarks>
    /// An unresolved reference occurs when a reference Attribute (e.g. group 'member') contains a value that could not be matched to another Connected System Object during the last import run. A non-zero count indicates data integrity issues that should be investigated.
    /// </remarks>
    /// <param name="connectedSystemId">The unique identifier of the Connected System.</param>
    /// <returns>The count of unresolved reference Attribute Values.</returns>
    [HttpGet("connected-systems/{connectedSystemId:int}/connector-space/unresolved-references/count", Name = "GetUnresolvedReferenceCount")]
    [ProducesResponseType(typeof(int), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetUnresolvedReferenceCountAsync(int connectedSystemId)
    {
        _logger.LogTrace("Requested unresolved reference count for Connected System: {ConnectedSystemId}", connectedSystemId);
        var count = await _application.ConnectedSystems.GetUnresolvedReferenceCountAsync(connectedSystemId);
        return Ok(count);
    }

    /// <summary>
    /// Get the connector space object count for a Connected System
    /// </summary>
    /// <param name="connectedSystemId">The unique identifier of the Connected System.</param>
    /// <param name="objectTypeId">Optional Object Type ID to filter by.</param>
    /// <param name="partitionId">Optional Partition ID to filter by.</param>
    /// <returns>The count of matching Connected System Objects.</returns>
    [HttpGet("connected-systems/{connectedSystemId:int}/connector-space/count", Name = "GetConnectorSpaceCount")]
    [ProducesResponseType(typeof(int), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetConnectorSpaceCountAsync(
        int connectedSystemId,
        [FromQuery] int? objectTypeId = null,
        [FromQuery] int? partitionId = null)
    {
        _logger.LogDebug("Getting connector space count for Connected System {ConnectedSystemId} (TypeId: {TypeId}, PartitionId: {PartitionId})",
            connectedSystemId, objectTypeId, partitionId);
        var count = await _application.Repository.ConnectedSystems.GetConnectedSystemObjectCountAsync(
            connectedSystemId, objectTypeId, partitionId);
        return Ok(count);
    }

    #region Pending Exports

    /// <summary>
    /// List Pending Exports for a Connected System
    /// </summary>
    /// <remarks>
    /// Returns lightweight header objects. Use the detail endpoint to retrieve full Attribute change data for a specific Pending Export.
    /// </remarks>
    /// <param name="connectedSystemId">The unique identifier of the Connected System.</param>
    /// <param name="page">Page number (1-based). Default: 1.</param>
    /// <param name="pageSize">Number of items per page (1-100). Default: 50.</param>
    /// <param name="search">Optional search text to filter by target object, source MVO, or error message.</param>
    /// <returns>A paginated set of Pending Export headers.</returns>
    [HttpGet("connected-systems/{connectedSystemId:int}/pending-exports", Name = "GetPendingExports")]
    [ProducesResponseType(typeof(PaginatedResponse<PendingExportHeader>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetPendingExportsAsync(
        int connectedSystemId,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        [FromQuery] string? search = null)
    {
        if (page < 1) page = 1;
        if (pageSize < 1) pageSize = 50;
        if (pageSize > 100) pageSize = 100;

        var result = await _application.ConnectedSystems.GetPendingExportHeadersAsync(
            connectedSystemId, page, pageSize, searchQuery: search);

        return Ok(new PaginatedResponse<PendingExportHeader>
        {
            Items = result.Results,
            TotalCount = result.TotalResults,
            Page = result.CurrentPage,
            PageSize = result.PageSize
        });
    }

    /// <summary>
    /// Get the Pending Export count for a Connected System
    /// </summary>
    /// <param name="connectedSystemId">The unique identifier of the Connected System.</param>
    /// <param name="changeType">Optional change type to filter by (Create = 0, Update = 1, Delete = 2).</param>
    /// <param name="status">Optional status to filter by (Pending = 0, ExportNotConfirmed = 1, Executing = 2, Failed = 3, Exported = 4).</param>
    /// <returns>The count of matching Pending Export objects.</returns>
    [HttpGet("connected-systems/{connectedSystemId:int}/pending-exports/count", Name = "GetPendingExportsCount")]
    [ProducesResponseType(typeof(int), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetPendingExportsCountAsync(
        int connectedSystemId,
        [FromQuery] PendingExportChangeType? changeType = null,
        [FromQuery] PendingExportStatus? status = null)
    {
        _logger.LogDebug("Getting Pending Exports count for Connected System {ConnectedSystemId} (ChangeType: {ChangeType}, Status: {Status})",
            connectedSystemId, changeType, status);
        var count = await _application.Repository.ConnectedSystems.GetPendingExportsFilteredCountAsync(
            connectedSystemId, changeType, status);
        return Ok(count);
    }

    /// <summary>
    /// Get a Pending Export
    /// </summary>
    /// <remarks>
    /// Multi-valued Attribute changes are capped at 10 per Attribute. Use the <c>attributeChangeSummaries</c> array to identify truncated Attributes, then call the paged Attribute changes endpoint to retrieve all values.
    /// </remarks>
    /// <param name="pendingExportId">The unique identifier (GUID) of the Pending Export.</param>
    /// <returns>The Pending Export details with capped Attribute changes and per-attribute summaries.</returns>
    [HttpGet("pending-exports/{pendingExportId:guid}", Name = "GetPendingExport")]
    [ProducesResponseType(typeof(PendingExportDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetPendingExportAsync(Guid pendingExportId)
    {
        _logger.LogTrace("Requested Pending Export: {PendingExportId}", pendingExportId);
        var result = await _application.ConnectedSystems.GetPendingExportDetailAsync(pendingExportId);
        if (result == null)
            return NotFound(ApiErrorResponse.NotFound($"Pending Export with ID {pendingExportId} not found."));

        return Ok(PendingExportDetailDto.FromDetailResult(result));
    }

    /// <summary>
    /// List Attribute Value changes for a Pending Export
    /// </summary>
    /// <remarks>
    /// Use this endpoint to page through large multi-valued Attribute changes (e.g. group member additions). The Pending Export detail endpoint caps multi-valued Attribute changes; use this endpoint to retrieve all values for a specific Attribute.
    /// </remarks>
    /// <param name="pendingExportId">The unique identifier (GUID) of the Pending Export.</param>
    /// <param name="attributeName">The Attribute name to retrieve changes for.</param>
    /// <param name="page">Page number (1-based). Default: 1.</param>
    /// <param name="pageSize">Number of changes per page (1-100). Default: 50.</param>
    /// <param name="search">Optional search text to filter changes by value.</param>
    /// <returns>A paginated set of Attribute Value changes with total count.</returns>
    [HttpGet("pending-exports/{pendingExportId:guid}/attribute-changes/{attributeName}/values", Name = "GetPendingExportAttributeChangesPaged")]
    [ProducesResponseType(typeof(PaginatedResponse<PendingExportAttributeValueChangeDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetPendingExportAttributeChangesPagedAsync(
        Guid pendingExportId,
        string attributeName,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        [FromQuery] string? search = null)
    {
        if (page < 1) page = 1;
        if (pageSize < 1) pageSize = 50;
        if (pageSize > 100) pageSize = 100;

        var result = await _application.ConnectedSystems.GetPendingExportAttributeChangesPagedAsync(
            pendingExportId, attributeName, page, pageSize, search);

        return Ok(new PaginatedResponse<PendingExportAttributeValueChangeDto>
        {
            Items = result.Results.Select(PendingExportAttributeValueChangeDto.FromEntity),
            TotalCount = result.TotalResults,
            Page = result.CurrentPage,
            PageSize = result.PageSize
        });
    }

    #endregion

    #region Partitions and Containers
    /// <summary>
    /// List Partitions for a Connected System
    /// </summary>
    /// <param name="connectedSystemId">The unique identifier of the Connected System.</param>
    /// <returns>A list of Partitions with their Containers.</returns>
    [HttpGet("connected-systems/{connectedSystemId:int}/partitions", Name = "GetConnectedSystemPartitions")]
    [ProducesResponseType(typeof(IEnumerable<ConnectedSystemPartitionDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetConnectedSystemPartitionsAsync(int connectedSystemId)
    {
        _logger.LogTrace("Requested partitions for Connected System: {Id}", connectedSystemId);

        // Core retrieval — partitions are then fetched separately with their own include chain.
        var connectedSystem = await _application.ConnectedSystems.GetConnectedSystemCoreAsync(connectedSystemId);
        if (connectedSystem == null)
            return NotFound(ApiErrorResponse.NotFound($"Connected System with ID {connectedSystemId} not found."));

        var partitions = await _application.ConnectedSystems.GetConnectedSystemPartitionsAsync(connectedSystem);
        var dtos = partitions.Select(ConnectedSystemPartitionDto.FromEntity);
        return Ok(dtos);
    }

    /// <summary>
    /// Update a Partition
    /// </summary>
    /// <param name="connectedSystemId">The unique identifier of the Connected System.</param>
    /// <param name="partitionId">The unique identifier of the Partition.</param>
    /// <param name="request">The update request with new values.</param>
    /// <returns>The updated Partition details.</returns>
    /// <response code="200">Partition updated successfully.</response>
    /// <response code="404">Connected System or Partition not found.</response>
    /// <response code="401">User could not be identified from authentication token.</response>
    [HttpPut("connected-systems/{connectedSystemId:int}/partitions/{partitionId:int}", Name = "UpdateConnectedSystemPartition")]
    [ProducesResponseType(typeof(ConnectedSystemPartitionDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> UpdateConnectedSystemPartitionAsync(int connectedSystemId, int partitionId, [FromBody] UpdateConnectedSystemPartitionRequest request)
    {
        _logger.LogInformation("Updating partition {PartitionId} for Connected System {SystemId}", partitionId, connectedSystemId);

        var initiatedBy = await GetCurrentUserAsync();
        if (initiatedBy == null && !IsApiKeyAuthenticated())
        {
            _logger.LogWarning("Could not identify user from JWT claims for partition update");
            return Unauthorized(ApiErrorResponse.Unauthorised("Could not identify user from authentication token."));
        }

        // Verify Connected System exists (Core retrieval — we only need existence, not the full graph)
        var connectedSystem = await _application.ConnectedSystems.GetConnectedSystemCoreAsync(connectedSystemId);
        if (connectedSystem == null)
            return NotFound(ApiErrorResponse.NotFound($"Connected System with ID {connectedSystemId} not found."));

        // Get the partition with change tracking since we modify and save it
        var partition = await _application.ConnectedSystems.GetConnectedSystemPartitionAsync(partitionId, withChangeTracking: true);
        if (partition == null || partition.ConnectedSystem?.Id != connectedSystemId)
            return NotFound(ApiErrorResponse.NotFound($"Partition with ID {partitionId} not found in Connected System {connectedSystemId}."));

        // Apply updates
        if (request.Selected.HasValue)
            partition.Selected = request.Selected.Value;

        await _application.ConnectedSystems.UpdateConnectedSystemPartitionAsync(partition);

        // Reload to get full entity with relationships
        var updated = await _application.ConnectedSystems.GetConnectedSystemPartitionAsync(partitionId);
        return Ok(ConnectedSystemPartitionDto.FromEntity(updated!));
    }

    /// <summary>
    /// Update a Container
    /// </summary>
    /// <param name="connectedSystemId">The unique identifier of the Connected System.</param>
    /// <param name="containerId">The unique identifier of the Container.</param>
    /// <param name="request">The update request with new values.</param>
    /// <returns>The updated Container details.</returns>
    /// <response code="200">Container updated successfully.</response>
    /// <response code="404">Connected System or Container not found.</response>
    /// <response code="401">User could not be identified from authentication token.</response>
    [HttpPut("connected-systems/{connectedSystemId:int}/containers/{containerId:int}", Name = "UpdateConnectedSystemContainer")]
    [ProducesResponseType(typeof(ConnectedSystemContainerDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> UpdateConnectedSystemContainerAsync(int connectedSystemId, int containerId, [FromBody] UpdateConnectedSystemContainerRequest request)
    {
        _logger.LogInformation("Updating container {ContainerId} for Connected System {SystemId}", containerId, connectedSystemId);

        var initiatedBy = await GetCurrentUserAsync();
        if (initiatedBy == null && !IsApiKeyAuthenticated())
        {
            _logger.LogWarning("Could not identify user from JWT claims for container update");
            return Unauthorized(ApiErrorResponse.Unauthorised("Could not identify user from authentication token."));
        }

        // Verify Connected System exists (Core retrieval — we only need existence, not the full graph)
        var connectedSystem = await _application.ConnectedSystems.GetConnectedSystemCoreAsync(connectedSystemId);
        if (connectedSystem == null)
            return NotFound(ApiErrorResponse.NotFound($"Connected System with ID {connectedSystemId} not found."));

        // Get the container
        var container = await _application.ConnectedSystems.GetConnectedSystemContainerAsync(containerId);
        if (container == null)
            return NotFound(ApiErrorResponse.NotFound($"Container with ID {containerId} not found."));

        // Verify container belongs to the Connected System (via partition, directly, or through parent container chain)
        var belongsToSystem = ContainerBelongsToConnectedSystem(container, connectedSystemId);
        if (!belongsToSystem)
            return NotFound(ApiErrorResponse.NotFound($"Container with ID {containerId} not found in Connected System {connectedSystemId}."));

        // Apply updates
        if (request.Selected.HasValue)
            container.Selected = request.Selected.Value;

        await _application.ConnectedSystems.UpdateConnectedSystemContainerAsync(container);

        // Reload to get full entity with relationships
        var updated = await _application.ConnectedSystems.GetConnectedSystemContainerAsync(containerId);
        return Ok(ConnectedSystemContainerDto.FromEntity(updated!));
    }
    #endregion

    /// <summary>
    /// Create a Connected System
    /// </summary>
    /// <remarks>
    /// The connector's default settings are applied automatically. Use the Update endpoint to configure settings after creation.
    /// </remarks>
    /// <param name="request">The Connected System creation request.</param>
    /// <returns>The created Connected System details.</returns>
    /// <response code="201">Connected System created successfully.</response>
    /// <response code="400">Invalid request or Connector Definition not found.</response>
    /// <response code="401">User could not be identified from authentication token.</response>
    [HttpPost("connected-systems", Name = "CreateConnectedSystem")]
    [ProducesResponseType(typeof(ConnectedSystemDetailDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> CreateConnectedSystemAsync([FromBody] CreateConnectedSystemRequest request)
    {
        _logger.LogInformation("Creating Connected System: {Name} with connector {ConnectorId}", LogSanitiser.Sanitise(request.Name), request.ConnectorDefinitionId);

        // Get the current user from the JWT claims (may be null for API key auth)
        var initiatedBy = await GetCurrentUserAsync();
        if (initiatedBy == null && !IsApiKeyAuthenticated())
        {
            _logger.LogWarning("Could not identify user from JWT claims for Connected System creation");
            return Unauthorized(ApiErrorResponse.Unauthorised("Could not identify user from authentication token."));
        }

        if (IsApiKeyAuthenticated())
        {
            _logger.LogInformation("Connected System creation initiated via API key: {ApiKeyName}", LogSanitiser.Sanitise(GetApiKeyName()));
        }

        // Validate the connector definition exists
        var connectorDefinition = await _application.ConnectedSystems.GetConnectorDefinitionAsync(request.ConnectorDefinitionId);
        if (connectorDefinition == null)
            return BadRequest(ApiErrorResponse.BadRequest($"Connector definition with ID {request.ConnectorDefinitionId} not found."));

        // Create the Connected System using the FK ID (not the nav property) to avoid
        // EF Core graph traversal inserting the untracked ConnectorDefinition as a new entity.
        var connectedSystem = new ConnectedSystem
        {
            Name = request.Name,
            Description = request.Description,
            ConnectorDefinitionId = request.ConnectorDefinitionId
        };

        try
        {
            // Get the current API key for Activity attribution if authenticated via API key
            var apiKey = await GetCurrentApiKeyAsync();
            if (apiKey != null)
                await _application.ConnectedSystems.CreateConnectedSystemAsync(connectedSystem, apiKey);
            else
                await _application.ConnectedSystems.CreateConnectedSystemAsync(connectedSystem, initiatedBy);

            _logger.LogInformation("Created Connected System: {Id} ({Name})", connectedSystem.Id, LogSanitiser.Sanitise(connectedSystem.Name));

            // Retrieve the created system to get all populated fields
            var created = await _application.ConnectedSystems.GetConnectedSystemAsync(connectedSystem.Id);
            return CreatedAtRoute("GetConnectedSystem", new { connectedSystemId = connectedSystem.Id }, ConnectedSystemDetailDto.FromEntity(created!));
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning(ex, "Failed to create Connected System: {Message}", ex.Message);
            return BadRequest(ApiErrorResponse.BadRequest(ex.Message));
        }
    }

    /// <summary>
    /// Update a Connected System
    /// </summary>
    /// <param name="connectedSystemId">The unique identifier of the Connected System to update.</param>
    /// <param name="request">The update request with new values.</param>
    /// <returns>The updated Connected System details.</returns>
    /// <response code="200">Connected System updated successfully.</response>
    /// <response code="400">Invalid request.</response>
    /// <response code="404">Connected System not found.</response>
    /// <response code="401">User could not be identified from authentication token.</response>
    [HttpPut("connected-systems/{connectedSystemId:int}", Name = "UpdateConnectedSystem")]
    [ProducesResponseType(typeof(ConnectedSystemDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> UpdateConnectedSystemAsync(int connectedSystemId, [FromBody] UpdateConnectedSystemRequest request)
    {
        _logger.LogInformation("Updating Connected System: {Id}", connectedSystemId);

        // Get the current user from the JWT claims (may be null for API key auth)
        var initiatedBy = await GetCurrentUserAsync();
        if (initiatedBy == null && !IsApiKeyAuthenticated())
        {
            _logger.LogWarning("Could not identify user from JWT claims for Connected System update");
            return Unauthorized(ApiErrorResponse.Unauthorised("Could not identify user from authentication token."));
        }

        // Get the existing Connected System with change tracking since we modify and save it
        var connectedSystem = await _application.ConnectedSystems.GetConnectedSystemAsync(connectedSystemId, withChangeTracking: true);
        if (connectedSystem == null)
            return NotFound(ApiErrorResponse.NotFound($"Connected System with ID {connectedSystemId} not found."));

        // Apply updates
        if (!string.IsNullOrEmpty(request.Name))
            connectedSystem.Name = request.Name;

        if (request.Description != null)
            connectedSystem.Description = request.Description;

        if (request.MaxExportParallelism.HasValue)
            connectedSystem.MaxExportParallelism = request.MaxExportParallelism.Value;

        // Update setting values if provided
        if (request.SettingValues != null)
        {
            foreach (var (settingId, update) in request.SettingValues)
            {
                var settingValue = connectedSystem.SettingValues.FirstOrDefault(sv => sv.Setting?.Id == settingId);
                if (settingValue != null)
                {
                    if (update.StringValue != null)
                    {
                        // For encrypted settings (like Password), encrypt and store in StringEncryptedValue
                        if (settingValue.Setting?.Type == ConnectedSystemSettingType.StringEncrypted)
                            settingValue.StringEncryptedValue = _credentialProtection.Protect(update.StringValue);
                        else
                            settingValue.StringValue = update.StringValue;
                    }
                    if (update.IntValue.HasValue)
                        settingValue.IntValue = update.IntValue.Value;
                    if (update.CheckboxValue.HasValue)
                        settingValue.CheckboxValue = update.CheckboxValue.Value;
                }
            }

            // the caller is writing settings, so validate before persisting and reject with structured per-setting
            // errors if invalid, mirroring the web form (which also blocks saving an invalid configuration). updates
            // that do not touch settings are not gated on pre-existing setting validity.
            var validationResults = _application.ConnectedSystems.ValidateConnectedSystemSettings(connectedSystem);
            var invalidResults = validationResults.Where(r => !r.IsValid).ToList();
            if (invalidResults.Count > 0)
            {
                _logger.LogInformation("Rejected settings update for Connected System {Id}: {Count} validation error(s)", connectedSystem.Id, invalidResults.Count);
                return BadRequest(ApiErrorResponse.ValidationError(
                    "One or more Connected System settings are invalid.",
                    BuildSettingValidationErrors(invalidResults)));
            }
        }

        try
        {
            // Get the current API key for Activity attribution if authenticated via API key
            var apiKey = await GetCurrentApiKeyAsync();
            if (apiKey != null)
                await _application.ConnectedSystems.UpdateConnectedSystemAsync(connectedSystem, apiKey);
            else
                await _application.ConnectedSystems.UpdateConnectedSystemAsync(connectedSystem, initiatedBy);

            _logger.LogInformation("Updated Connected System: {Id} ({Name})", connectedSystem.Id, LogSanitiser.Sanitise(connectedSystem.Name));

            // Retrieve the updated system
            var updated = await _application.ConnectedSystems.GetConnectedSystemAsync(connectedSystemId);
            return Ok(ConnectedSystemDetailDto.FromEntity(updated!));
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning(ex, "Failed to update Connected System: {Message}", ex.Message);
            return BadRequest(ApiErrorResponse.BadRequest(ex.Message));
        }
    }

    /// <summary>
    /// Groups Connected System setting validation failures into the API's field-keyed validation error shape.
    /// Failures tied to a specific setting are keyed by that setting's name; group-level failures (which have no
    /// single owning setting) are collected under the generic "settings" key.
    /// </summary>
    private static Dictionary<string, string[]> BuildSettingValidationErrors(IEnumerable<ConnectorSettingValueValidationResult> invalidResults)
    {
        var errors = new Dictionary<string, List<string>>();
        foreach (var result in invalidResults)
        {
            var key = result.SettingValue?.Setting?.Name ?? "settings";
            var message = result.ErrorMessage ?? "Invalid setting value.";
            if (!errors.TryGetValue(key, out var messages))
            {
                messages = new List<string>();
                errors[key] = messages;
            }
            messages.Add(message);
        }
        return errors.ToDictionary(kvp => kvp.Key, kvp => kvp.Value.ToArray());
    }

    /// <summary>
    /// Import schema from a Connected System
    /// </summary>
    /// <remarks>
    /// Connects to the external system and retrieves its Object Types and Attributes. This is required before creating Synchronisation Rules. Existing schema configuration will be replaced; Synchronisation Rules referencing removed Object Types or Attributes will need to be updated.
    /// </remarks>
    /// <param name="connectedSystemId">The unique identifier of the Connected System.</param>
    /// <returns>The updated Connected System with imported schema.</returns>
    /// <response code="200">Schema imported successfully.</response>
    /// <response code="400">Schema import failed (e.g., connection error, invalid settings).</response>
    /// <response code="404">Connected System not found.</response>
    /// <response code="401">User could not be identified from authentication token.</response>
    [HttpPost("connected-systems/{connectedSystemId:int}/import-schema", Name = "ImportConnectedSystemSchema")]
    [ProducesResponseType(typeof(ConnectedSystemDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> ImportConnectedSystemSchemaAsync(int connectedSystemId)
    {
        _logger.LogInformation("Schema import requested for Connected System: {Id}", connectedSystemId);

        // Get the current user from the JWT claims (may be null for API key auth)
        var initiatedBy = await GetCurrentUserAsync();
        if (initiatedBy == null && !IsApiKeyAuthenticated())
        {
            _logger.LogWarning("Could not identify user from JWT claims for schema import");
            return Unauthorized(ApiErrorResponse.Unauthorised("Could not identify user from authentication token."));
        }

        // Get the Connected System with change tracking since schema import modifies and saves it
        var connectedSystem = await _application.ConnectedSystems.GetConnectedSystemAsync(connectedSystemId, withChangeTracking: true);
        if (connectedSystem == null)
            return NotFound(ApiErrorResponse.NotFound($"Connected System with ID {connectedSystemId} not found."));

        try
        {
            // Get the current API key for Activity attribution if authenticated via API key
            var apiKey = await GetCurrentApiKeyAsync();
            if (apiKey != null)
                await _application.ConnectedSystems.ImportConnectedSystemSchemaAsync(connectedSystem, apiKey);
            else
                await _application.ConnectedSystems.ImportConnectedSystemSchemaAsync(connectedSystem, initiatedBy);

            _logger.LogInformation("Schema imported for Connected System: {Id} ({Name}), {Count} object types",
                connectedSystemId, connectedSystem.Name, connectedSystem.ObjectTypes?.Count ?? 0);

            // Retrieve the updated system
            var updated = await _application.ConnectedSystems.GetConnectedSystemAsync(connectedSystemId);
            return Ok(ConnectedSystemDetailDto.FromEntity(updated!));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to import schema for Connected System: {Id}", connectedSystemId);
            return BadRequest(ApiErrorResponse.BadRequest($"Schema import failed: {ex.Message}"));
        }
    }

    /// <summary>
    /// Import hierarchy from a Connected System
    /// </summary>
    /// <remarks>
    /// Connects to the external system and retrieves its Partition and Container hierarchy. Existing selections are preserved where possible using a match-and-merge approach. If previously selected items were removed, the <c>hasSelectedItemsRemoved</c> flag will be set in the response.
    /// </remarks>
    /// <param name="connectedSystemId">The unique identifier of the Connected System.</param>
    /// <returns>A result object describing what changed during the hierarchy refresh.</returns>
    /// <response code="200">Hierarchy imported successfully.</response>
    /// <response code="400">Hierarchy import failed (e.g., connection error, invalid settings).</response>
    /// <response code="404">Connected System not found.</response>
    /// <response code="401">User could not be identified from authentication token.</response>
    [HttpPost("connected-systems/{connectedSystemId:int}/import-hierarchy", Name = "ImportConnectedSystemHierarchy")]
    [ProducesResponseType(typeof(HierarchyRefreshResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> ImportConnectedSystemHierarchyAsync(int connectedSystemId)
    {
        _logger.LogInformation("Hierarchy import requested for Connected System: {Id}", connectedSystemId);

        // Get the current user from the JWT claims (may be null for API key auth)
        var initiatedBy = await GetCurrentUserAsync();
        if (initiatedBy == null && !IsApiKeyAuthenticated())
        {
            _logger.LogWarning("Could not identify user from JWT claims for hierarchy import");
            return Unauthorized(ApiErrorResponse.Unauthorised("Could not identify user from authentication token."));
        }

        // Get the Connected System with change tracking since hierarchy import modifies and saves it
        var connectedSystem = await _application.ConnectedSystems.GetConnectedSystemAsync(connectedSystemId, withChangeTracking: true);
        if (connectedSystem == null)
            return NotFound(ApiErrorResponse.NotFound($"Connected System with ID {connectedSystemId} not found."));

        try
        {
            // Call the appropriate overload based on authentication method
            JIM.Models.Staging.DTOs.HierarchyRefreshResult result;
            var apiKey = await GetCurrentApiKeyAsync();
            if (apiKey != null)
                result = await _application.ConnectedSystems.ImportConnectedSystemHierarchyAsync(connectedSystem, apiKey);
            else
                result = await _application.ConnectedSystems.ImportConnectedSystemHierarchyAsync(connectedSystem, initiatedBy);

            _logger.LogInformation("Hierarchy imported for Connected System: {Id} ({Name}). Summary: {Summary}",
                connectedSystemId, connectedSystem.Name, result.GetSummary());

            return Ok(HierarchyRefreshResultDto.FromModel(result));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to import hierarchy for Connected System: {Id}", connectedSystemId);
            return BadRequest(ApiErrorResponse.BadRequest($"Hierarchy import failed: {ex.Message}"));
        }
    }

    /// <summary>
    /// Delete a Connected System
    /// </summary>
    /// <remarks>
    /// Small systems (fewer than 1,000 CSOs) are deleted immediately and return 200 OK. Larger systems, or systems with a running sync, are queued as a background job and return 202 Accepted with tracking IDs. Use the deletion-preview endpoint first to understand the impact.
    /// </remarks>
    /// <param name="connectedSystemId">The unique identifier of the Connected System to delete.</param>
    /// <param name="deleteChangeHistory">Whether to delete change history for the deleted CSOs. Default: false (preserves audit trail).</param>
    /// <returns>The result of the deletion request including outcome and tracking IDs.</returns>
    /// <response code="200">Deletion completed immediately.</response>
    /// <response code="202">Deletion has been queued as a background job.</response>
    /// <response code="400">Deletion failed.</response>
    /// <response code="401">User could not be identified from authentication token.</response>
    [HttpDelete("connected-systems/{connectedSystemId:int}", Name = "DeleteConnectedSystem")]
    [ProducesResponseType(typeof(ConnectedSystemDeletionResult), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ConnectedSystemDeletionResult), StatusCodes.Status202Accepted)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> DeleteConnectedSystemAsync(
        int connectedSystemId,
        [FromQuery] bool deleteChangeHistory = false)
    {
        _logger.LogInformation("Deletion requested for Connected System: {Id}, deleteChangeHistory={DeleteHistory}",
            connectedSystemId, deleteChangeHistory);

        // Get the current user from the JWT claims (may be null for API key auth)
        var initiatedBy = await GetCurrentUserAsync();
        if (initiatedBy == null && !IsApiKeyAuthenticated())
        {
            _logger.LogWarning("Could not identify user from JWT claims for deletion request");
            return Unauthorized(ApiErrorResponse.Unauthorised("Could not identify user from authentication token."));
        }

        var apiKey = await GetCurrentApiKeyAsync();
        var result = apiKey != null
            ? await _application.ConnectedSystems.DeleteAsync(connectedSystemId, apiKey, deleteChangeHistory)
            : await _application.ConnectedSystems.DeleteAsync(connectedSystemId, initiatedBy, deleteChangeHistory);

        if (!result.Success)
            return BadRequest(ApiErrorResponse.BadRequest(result.ErrorMessage ?? "Deletion failed."));

        // Return 202 Accepted for queued operations, 200 OK for immediate completion
        if (result.Outcome == DeletionOutcome.QueuedAsBackgroundJob ||
            result.Outcome == DeletionOutcome.QueuedAfterSync)
        {
            return Accepted(result);
        }

        return Ok(result);
    }

    /// <summary>
    /// Clear the connector space for a Connected System
    /// </summary>
    /// <remarks>
    /// Removes all Connected System Objects and their Attributes from the connector space. Typically used before re-importing data. This is a destructive operation.
    /// </remarks>
    /// <param name="connectedSystemId">The unique identifier of the Connected System to clear.</param>
    /// <param name="deleteChangeHistory">Whether to delete change history for the cleared CSOs. Default: true (recommended for re-import scenarios).</param>
    /// <response code="200">Connector space cleared successfully.</response>
    /// <response code="400">Clear operation failed.</response>
    /// <response code="401">User is not authenticated.</response>
    /// <response code="404">Connected System not found.</response>
    [HttpPost("connected-systems/{connectedSystemId:int}/clear", Name = "ClearConnectorSpace")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ClearConnectorSpaceAsync(
        int connectedSystemId,
        [FromQuery] bool deleteChangeHistory = true)
    {
        _logger.LogInformation("Clear connector space requested for Connected System: {Id}, deleteChangeHistory={DeleteHistory}",
            connectedSystemId, deleteChangeHistory);

        try
        {
            // Verify Connected System exists (Core retrieval — we only need existence, not the full graph)
            var connectedSystem = await _application.ConnectedSystems.GetConnectedSystemCoreAsync(connectedSystemId);
            if (connectedSystem == null)
            {
                return NotFound(ApiErrorResponse.NotFound($"Connected System with ID {connectedSystemId} not found."));
            }

            await _application.ConnectedSystems.ClearConnectedSystemObjectsAsync(connectedSystemId, deleteChangeHistory);

            _logger.LogInformation("Connector space cleared for Connected System: {Id}", connectedSystemId);
            return Ok();
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Clear connector space failed for Connected System: {Id}", connectedSystemId);
            return BadRequest(ApiErrorResponse.BadRequest(ex.Message));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Clear connector space failed for Connected System: {Id}", connectedSystemId);
            return StatusCode(StatusCodes.Status500InternalServerError,
                ApiErrorResponse.InternalError($"Clear operation failed: {ex.Message}"));
        }
    }

    #endregion

    #region Connector Definitions

    /// <summary>
    /// List Connector Definitions
    /// </summary>
    /// <returns>A list of all available Connector Definitions.</returns>
    [HttpGet("connector-definitions", Name = "GetConnectorDefinitions")]
    [ProducesResponseType(typeof(IEnumerable<ConnectorDefinitionHeader>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetConnectorDefinitionsAsync()
    {
        _logger.LogTrace("Requested connector definitions");
        var headers = await _application.ConnectedSystems.GetConnectorDefinitionHeadersAsync();
        return Ok(headers);
    }

    /// <summary>
    /// Get a Connector Definition
    /// </summary>
    /// <param name="id">The unique identifier of the Connector Definition.</param>
    /// <returns>The Connector Definition details including all settings and capabilities.</returns>
    [HttpGet("connector-definitions/{id:int}", Name = "GetConnectorDefinition")]
    [ProducesResponseType(typeof(ConnectorDefinition), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetConnectorDefinitionAsync(int id)
    {
        _logger.LogTrace("Requested connector definition: {Id}", id);
        var definition = await _application.ConnectedSystems.GetConnectorDefinitionAsync(id);
        if (definition == null)
            return NotFound(ApiErrorResponse.NotFound($"Connector definition with ID {id} not found."));

        return Ok(definition);
    }

    /// <summary>
    /// Get a Connector Definition by name
    /// </summary>
    /// <param name="name">The name of the Connector Definition (e.g., "CSV File", "LDAP").</param>
    /// <returns>The Connector Definition details including all settings and capabilities.</returns>
    [HttpGet("connector-definitions/by-name/{name}", Name = "GetConnectorDefinitionByName")]
    [ProducesResponseType(typeof(ConnectorDefinition), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetConnectorDefinitionByNameAsync(string name)
    {
        _logger.LogTrace("Requested connector definition by name: {Name}", LogSanitiser.Sanitise(name));
        var definition = await _application.ConnectedSystems.GetConnectorDefinitionAsync(name);
        if (definition == null)
            return NotFound(ApiErrorResponse.NotFound($"Connector definition with name '{name}' not found."));

        return Ok(definition);
    }

    #endregion

    #region Run Profiles

    /// <summary>
    /// List Run Profiles for a Connected System
    /// </summary>
    /// <param name="connectedSystemId">The unique identifier of the Connected System.</param>
    /// <returns>A list of Run Profiles configured for the Connected System.</returns>
    [HttpGet("connected-systems/{connectedSystemId:int}/run-profiles", Name = "GetRunProfiles")]
    [ProducesResponseType(typeof(IEnumerable<RunProfileDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetRunProfilesAsync(int connectedSystemId)
    {
        _logger.LogTrace("Requested Run Profiles for Connected System: {Id}", connectedSystemId);

        // Core retrieval — we only need to verify existence before listing Run Profiles.
        var system = await _application.ConnectedSystems.GetConnectedSystemCoreAsync(connectedSystemId);
        if (system == null)
            return NotFound(ApiErrorResponse.NotFound($"Connected System with ID {connectedSystemId} not found."));

        var runProfiles = await _application.ConnectedSystems.GetConnectedSystemRunProfilesAsync(connectedSystemId);
        var dtos = runProfiles.Select(RunProfileDto.FromEntity);
        return Ok(dtos);
    }

    /// <summary>
    /// Execute a Run Profile
    /// </summary>
    /// <remarks>
    /// Queues a synchronisation task for execution by the worker service. Returns 202 Accepted with the Activity ID and Task ID for tracking.
    /// </remarks>
    /// <param name="connectedSystemId">The unique identifier of the Connected System.</param>
    /// <param name="runProfileId">The unique identifier of the Run Profile to execute.</param>
    /// <returns>The execution response with Activity and task IDs for tracking.</returns>
    /// <response code="202">Run Profile execution has been queued.</response>
    /// <response code="404">Connected System or Run Profile not found.</response>
    /// <response code="401">User could not be identified from authentication token.</response>
    [HttpPost("connected-systems/{connectedSystemId:int}/run-profiles/{runProfileId:int}/execute", Name = "ExecuteRunProfile")]
    [ProducesResponseType(typeof(RunProfileExecutionResponse), StatusCodes.Status202Accepted)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> ExecuteRunProfileAsync(int connectedSystemId, int runProfileId)
    {
        _logger.LogInformation("Run Profile execution requested: ConnectedSystem={SystemId}, RunProfile={ProfileId}",
            connectedSystemId, runProfileId);

        // Verify Connected System exists (Core retrieval — the sync task only needs the id).
        var system = await _application.ConnectedSystems.GetConnectedSystemCoreAsync(connectedSystemId);
        if (system == null)
            return NotFound(ApiErrorResponse.NotFound($"Connected System with ID {connectedSystemId} not found."));

        // Verify Run Profile exists and belongs to this Connected System
        var runProfiles = await _application.ConnectedSystems.GetConnectedSystemRunProfilesAsync(connectedSystemId);
        var runProfile = runProfiles.FirstOrDefault(rp => rp.Id == runProfileId);
        if (runProfile == null)
            return NotFound(ApiErrorResponse.NotFound($"Run Profile with ID {runProfileId} not found for Connected System {connectedSystemId}."));

        // Get the current user from the JWT claims (may be null for API key auth)
        var initiatedBy = await GetCurrentUserAsync();
        if (initiatedBy == null && !IsApiKeyAuthenticated())
        {
            _logger.LogWarning("Could not identify user from JWT claims for Run Profile execution");
            return Unauthorized(ApiErrorResponse.Unauthorised("Could not identify user from authentication token."));
        }

        // Create and queue the synchronisation task
        // Use API key for attribution when authenticated via API key
        SynchronisationWorkerTask workerTask;
        if (initiatedBy != null)
        {
            workerTask = SynchronisationWorkerTask.ForUser(connectedSystemId, runProfileId, initiatedBy.Id, initiatedBy.DisplayName ?? "Unknown User");
        }
        else
        {
            var apiKey = await GetCurrentApiKeyAsync();
            if (apiKey == null)
            {
                _logger.LogError("Failed to resolve API key for Run Profile execution");
                return BadRequest(new { error = "Failed to identify initiating API key" });
            }
            workerTask = SynchronisationWorkerTask.ForApiKey(connectedSystemId, runProfileId, apiKey.Id, apiKey.Name);
        }

        var result = await _application.Tasking.CreateWorkerTaskAsync(workerTask);
        if (!result.Success)
        {
            _logger.LogWarning("Run Profile execution blocked: {Error}", LogSanitiser.Sanitise(result.ErrorMessage));
            return BadRequest(ApiErrorResponse.BadRequest(result.ErrorMessage ?? "Validation failed."));
        }

        _logger.LogInformation("Run Profile execution queued: ConnectedSystem={SystemId}, RunProfile={ProfileId}, TaskId={TaskId}, ActivityId={ActivityId}",
            connectedSystemId, runProfileId, workerTask.Id, workerTask.Activity?.Id);

        var response = new RunProfileExecutionResponse
        {
            ActivityId = workerTask.Activity?.Id ?? Guid.Empty,
            TaskId = workerTask.Id,
            Message = $"Run Profile '{runProfile.Name}' has been queued for execution.",
            Warnings = result.Warnings
        };

        return Accepted(response);
    }

    /// <summary>
    /// Create a Run Profile
    /// </summary>
    /// <param name="connectedSystemId">The unique identifier of the Connected System.</param>
    /// <param name="request">The Run Profile creation request.</param>
    /// <returns>The created Run Profile details.</returns>
    /// <response code="201">Run Profile created successfully.</response>
    /// <response code="400">Invalid request or run type not supported by connector.</response>
    /// <response code="404">Connected System not found.</response>
    /// <response code="401">User could not be identified from authentication token.</response>
    [HttpPost("connected-systems/{connectedSystemId:int}/run-profiles", Name = "CreateRunProfile")]
    [ProducesResponseType(typeof(RunProfileDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> CreateRunProfileAsync(int connectedSystemId, [FromBody] CreateRunProfileRequest request)
    {
        _logger.LogInformation("Creating Run Profile: {Name} for Connected System {SystemId}", LogSanitiser.Sanitise(request.Name), connectedSystemId);

        // Get the current user from the JWT claims (may be null for API key auth)
        var initiatedBy = await GetCurrentUserAsync();
        if (initiatedBy == null && !IsApiKeyAuthenticated())
        {
            _logger.LogWarning("Could not identify user from JWT claims for Run Profile creation");
            return Unauthorized(ApiErrorResponse.Unauthorised("Could not identify user from authentication token."));
        }

        // Verify Connected System exists (Core retrieval — partitions are fetched separately below).
        var system = await _application.ConnectedSystems.GetConnectedSystemCoreAsync(connectedSystemId);
        if (system == null)
            return NotFound(ApiErrorResponse.NotFound($"Connected System with ID {connectedSystemId} not found."));

        // Create the Run Profile
        var runProfile = new ConnectedSystemRunProfile
        {
            Name = request.Name,
            ConnectedSystemId = connectedSystemId,
            RunType = request.RunType,
            PageSize = request.PageSize,
            FilePath = request.FilePath
        };

        // Set partition if provided
        if (request.PartitionId.HasValue)
        {
            var partitions = await _application.ConnectedSystems.GetConnectedSystemPartitionsAsync(system);
            var partition = partitions.FirstOrDefault(p => p.Id == request.PartitionId.Value);
            if (partition == null)
                return BadRequest(ApiErrorResponse.BadRequest($"Partition with ID {request.PartitionId.Value} not found."));
            runProfile.Partition = partition;
        }

        try
        {
            var apiKey = await GetCurrentApiKeyAsync();
            if (apiKey != null)
                await _application.ConnectedSystems.CreateConnectedSystemRunProfileAsync(runProfile, apiKey);
            else
                await _application.ConnectedSystems.CreateConnectedSystemRunProfileAsync(runProfile, initiatedBy);

            _logger.LogInformation("Created Run Profile: {Id} ({Name})", runProfile.Id, LogSanitiser.Sanitise(runProfile.Name));

            return CreatedAtRoute("GetRunProfiles", new { connectedSystemId }, RunProfileDto.FromEntity(runProfile));
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning(ex, "Failed to create Run Profile: {Message}", LogSanitiser.Sanitise(ex.Message));
            return BadRequest(ApiErrorResponse.BadRequest(ex.Message));
        }
    }

    /// <summary>
    /// Update a Run Profile
    /// </summary>
    /// <param name="connectedSystemId">The unique identifier of the Connected System.</param>
    /// <param name="runProfileId">The unique identifier of the Run Profile to update.</param>
    /// <param name="request">The update request with new values.</param>
    /// <returns>The updated Run Profile details.</returns>
    /// <response code="200">Run Profile updated successfully.</response>
    /// <response code="400">Invalid request.</response>
    /// <response code="404">Connected System or Run Profile not found.</response>
    /// <response code="401">User could not be identified from authentication token.</response>
    [HttpPut("connected-systems/{connectedSystemId:int}/run-profiles/{runProfileId:int}", Name = "UpdateRunProfile")]
    [ProducesResponseType(typeof(RunProfileDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> UpdateRunProfileAsync(int connectedSystemId, int runProfileId, [FromBody] UpdateRunProfileRequest request)
    {
        _logger.LogInformation("Updating Run Profile: {Id} for Connected System {SystemId}", runProfileId, connectedSystemId);

        // Get the current user from the JWT claims (may be null for API key auth)
        var initiatedBy = await GetCurrentUserAsync();
        if (initiatedBy == null && !IsApiKeyAuthenticated())
        {
            _logger.LogWarning("Could not identify user from JWT claims for Run Profile update");
            return Unauthorized(ApiErrorResponse.Unauthorised("Could not identify user from authentication token."));
        }

        // Verify Connected System exists (Core retrieval — partitions are fetched separately below).
        var system = await _application.ConnectedSystems.GetConnectedSystemCoreAsync(connectedSystemId);
        if (system == null)
            return NotFound(ApiErrorResponse.NotFound($"Connected System with ID {connectedSystemId} not found."));

        // Get the Run Profile
        var runProfiles = await _application.ConnectedSystems.GetConnectedSystemRunProfilesAsync(connectedSystemId);
        var runProfile = runProfiles.FirstOrDefault(rp => rp.Id == runProfileId);
        if (runProfile == null)
            return NotFound(ApiErrorResponse.NotFound($"Run Profile with ID {runProfileId} not found for Connected System {connectedSystemId}."));

        // Apply updates
        if (!string.IsNullOrEmpty(request.Name))
            runProfile.Name = request.Name;

        if (request.PageSize.HasValue)
            runProfile.PageSize = request.PageSize.Value;

        if (request.FilePath != null)
            runProfile.FilePath = request.FilePath;

        // Update partition if provided
        if (request.PartitionId.HasValue)
        {
            var partitions = await _application.ConnectedSystems.GetConnectedSystemPartitionsAsync(system);
            var partition = partitions.FirstOrDefault(p => p.Id == request.PartitionId.Value);
            if (partition == null)
                return BadRequest(ApiErrorResponse.BadRequest($"Partition with ID {request.PartitionId.Value} not found."));
            runProfile.Partition = partition;
        }

        try
        {
            var apiKey = await GetCurrentApiKeyAsync();
            if (apiKey != null)
                await _application.ConnectedSystems.UpdateConnectedSystemRunProfileAsync(runProfile, apiKey);
            else
                await _application.ConnectedSystems.UpdateConnectedSystemRunProfileAsync(runProfile, initiatedBy);

            _logger.LogInformation("Updated Run Profile: {Id} ({Name})", runProfile.Id, LogSanitiser.Sanitise(runProfile.Name));

            return Ok(RunProfileDto.FromEntity(runProfile));
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning(ex, "Failed to update Run Profile: {Message}", LogSanitiser.Sanitise(ex.Message));
            return BadRequest(ApiErrorResponse.BadRequest(ex.Message));
        }
    }

    /// <summary>
    /// Delete a Run Profile
    /// </summary>
    /// <param name="connectedSystemId">The unique identifier of the Connected System.</param>
    /// <param name="runProfileId">The unique identifier of the Run Profile to delete.</param>
    /// <returns>No content on success.</returns>
    /// <response code="204">Run Profile deleted successfully.</response>
    /// <response code="404">Connected System or Run Profile not found.</response>
    /// <response code="401">User could not be identified from authentication token.</response>
    [HttpDelete("connected-systems/{connectedSystemId:int}/run-profiles/{runProfileId:int}", Name = "DeleteRunProfile")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> DeleteRunProfileAsync(int connectedSystemId, int runProfileId)
    {
        _logger.LogInformation("Deleting Run Profile: {Id} for Connected System {SystemId}", runProfileId, connectedSystemId);

        // Get the current user from the JWT claims (may be null for API key auth)
        var initiatedBy = await GetCurrentUserAsync();
        if (initiatedBy == null && !IsApiKeyAuthenticated())
        {
            _logger.LogWarning("Could not identify user from JWT claims for Run Profile deletion");
            return Unauthorized(ApiErrorResponse.Unauthorised("Could not identify user from authentication token."));
        }

        // Verify Connected System exists (Core retrieval — we only need existence).
        var system = await _application.ConnectedSystems.GetConnectedSystemCoreAsync(connectedSystemId);
        if (system == null)
            return NotFound(ApiErrorResponse.NotFound($"Connected System with ID {connectedSystemId} not found."));

        // Get the Run Profile
        var runProfiles = await _application.ConnectedSystems.GetConnectedSystemRunProfilesAsync(connectedSystemId);
        var runProfile = runProfiles.FirstOrDefault(rp => rp.Id == runProfileId);
        if (runProfile == null)
            return NotFound(ApiErrorResponse.NotFound($"Run Profile with ID {runProfileId} not found for Connected System {connectedSystemId}."));

        var apiKey = await GetCurrentApiKeyAsync();
        if (apiKey != null)
            await _application.ConnectedSystems.DeleteConnectedSystemRunProfileAsync(runProfile, apiKey);
        else
            await _application.ConnectedSystems.DeleteConnectedSystemRunProfileAsync(runProfile, initiatedBy);

        _logger.LogInformation("Deleted Run Profile: {Id}", runProfileId);

        return NoContent();
    }

    #endregion

    #region Synchronisation Rules

    /// <summary>
    /// List Synchronisation Rules
    /// </summary>
    /// <param name="pagination">Pagination parameters (page, pageSize, sortBy, sortDirection, filter).</param>
    /// <returns>A paginated list of Synchronisation Rule headers.</returns>
    [HttpGet("sync-rules", Name = "GetSyncRules")]
    [ProducesResponseType(typeof(PaginatedResponse<SyncRuleHeader>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetSyncRulesAsync([FromQuery] PaginationRequest pagination)
    {
        _logger.LogTrace("Requested Synchronisation Rules (Page: {Page}, PageSize: {PageSize})", pagination.Page, pagination.PageSize);
        var rules = await _application.ConnectedSystems.GetSyncRulesAsync();
        var headers = rules.Select(SyncRuleHeader.FromEntity).AsQueryable();

        var result = headers
            .ApplySortAndFilter(pagination)
            .ToPaginatedResponse(pagination);

        return Ok(result);
    }

    /// <summary>
    /// Get a Synchronisation Rule
    /// </summary>
    /// <param name="id">The unique identifier of the Synchronisation Rule.</param>
    /// <returns>The Synchronisation Rule details including Attribute Flow configuration.</returns>
    [HttpGet("sync-rules/{id:int}", Name = "GetSyncRule")]
    [ProducesResponseType(typeof(SyncRuleHeader), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetSyncRuleAsync(int id)
    {
        _logger.LogTrace("Requested Synchronisation Rule: {Id}", id);
        var rule = await _application.ConnectedSystems.GetSyncRuleAsync(id);
        if (rule == null)
            return NotFound(ApiErrorResponse.NotFound($"Synchronisation Rule with ID {id} not found."));

        return Ok(SyncRuleHeader.FromEntity(rule));
    }

    /// <summary>
    /// Create a Synchronisation Rule
    /// </summary>
    /// <remarks>
    /// For Import rules, set <c>ProjectToMetaverse</c> to true to create Metaverse Objects from imported data. For Export rules, set <c>ProvisionToConnectedSystem</c> to true to create Connected System Objects.
    /// </remarks>
    /// <param name="request">The Synchronisation Rule creation request.</param>
    /// <returns>The created Synchronisation Rule details.</returns>
    /// <response code="201">Synchronisation Rule created successfully.</response>
    /// <response code="400">Invalid request or validation failed.</response>
    /// <response code="401">User could not be identified from authentication token.</response>
    [HttpPost("sync-rules", Name = "CreateSyncRule")]
    [ProducesResponseType(typeof(SyncRuleHeader), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> CreateSyncRuleAsync([FromBody] CreateSyncRuleRequest request)
    {
        _logger.LogInformation("Creating Synchronisation Rule: {Name}", LogSanitiser.Sanitise(request.Name));

        // Get the current user from the JWT claims (may be null for API key auth)
        var initiatedBy = await GetCurrentUserAsync();
        if (initiatedBy == null && !IsApiKeyAuthenticated())
        {
            _logger.LogWarning("Could not identify user from JWT claims for Synchronisation Rule creation");
            return Unauthorized(ApiErrorResponse.Unauthorised("Could not identify user from authentication token."));
        }

        // Verify Connected System exists (Core retrieval — only used as a FK reference on the new
        // Synchronisation Rule; object types are fetched separately below).
        var connectedSystem = await _application.ConnectedSystems.GetConnectedSystemCoreAsync(request.ConnectedSystemId);
        if (connectedSystem == null)
            return BadRequest(ApiErrorResponse.BadRequest($"Connected System with ID {request.ConnectedSystemId} not found."));

        // Get Connected System Object Type
        var csObjectTypes = await _application.ConnectedSystems.GetObjectTypesAsync(request.ConnectedSystemId);
        var csObjectType = csObjectTypes?.FirstOrDefault(t => t.Id == request.ConnectedSystemObjectTypeId);
        if (csObjectType == null)
            return BadRequest(ApiErrorResponse.BadRequest($"Connected System Object Type with ID {request.ConnectedSystemObjectTypeId} not found."));

        // Get Metaverse Object Type
        var mvObjectType = await _application.Metaverse.GetMetaverseObjectTypeAsync(request.MetaverseObjectTypeId, false);
        if (mvObjectType == null)
            return BadRequest(ApiErrorResponse.BadRequest($"Metaverse Object Type with ID {request.MetaverseObjectTypeId} not found."));

        // Create the Synchronisation Rule
        var syncRule = new SyncRule
        {
            Name = request.Name,
            ConnectedSystem = connectedSystem,
            ConnectedSystemId = request.ConnectedSystemId,
            ConnectedSystemObjectType = csObjectType,
            ConnectedSystemObjectTypeId = request.ConnectedSystemObjectTypeId,
            MetaverseObjectType = mvObjectType,
            MetaverseObjectTypeId = request.MetaverseObjectTypeId,
            Direction = request.Direction,
            ProjectToMetaverse = request.ProjectToMetaverse,
            ProvisionToConnectedSystem = request.ProvisionToConnectedSystem,
            Enabled = request.Enabled,
            EnforceState = request.EnforceState
        };

        var apiKey = await GetCurrentApiKeyAsync();
        bool success;
        if (apiKey != null)
            success = await _application.ConnectedSystems.CreateOrUpdateSyncRuleAsync(syncRule, apiKey);
        else
            success = await _application.ConnectedSystems.CreateOrUpdateSyncRuleAsync(syncRule, initiatedBy);
        if (!success)
        {
            var validationErrors = syncRule.Validate();
            var errorMessage = string.Join("; ", validationErrors.Select(v => v.Message));
            return BadRequest(ApiErrorResponse.BadRequest($"Synchronisation Rule validation failed: {errorMessage}"));
        }

        _logger.LogInformation("Created Synchronisation Rule: {Id} ({Name})", syncRule.Id, LogSanitiser.Sanitise(syncRule.Name));

        // Retrieve the created Synchronisation Rule
        var created = await _application.ConnectedSystems.GetSyncRuleAsync(syncRule.Id);
        return CreatedAtRoute("GetSyncRule", new { id = syncRule.Id }, SyncRuleHeader.FromEntity(created!));
    }

    /// <summary>
    /// Update a Synchronisation Rule
    /// </summary>
    /// <param name="id">The unique identifier of the Synchronisation Rule to update.</param>
    /// <param name="request">The update request with new values.</param>
    /// <returns>The updated Synchronisation Rule details.</returns>
    /// <response code="200">Synchronisation Rule updated successfully.</response>
    /// <response code="400">Invalid request or validation failed.</response>
    /// <response code="404">Synchronisation Rule not found.</response>
    /// <response code="401">User could not be identified from authentication token.</response>
    [HttpPut("sync-rules/{id:int}", Name = "UpdateSyncRule")]
    [ProducesResponseType(typeof(SyncRuleHeader), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> UpdateSyncRuleAsync(int id, [FromBody] UpdateSyncRuleRequest request)
    {
        _logger.LogInformation("Updating Synchronisation Rule: {Id}", id);

        // Get the current user from the JWT claims (may be null for API key auth)
        var initiatedBy = await GetCurrentUserAsync();
        if (initiatedBy == null && !IsApiKeyAuthenticated())
        {
            _logger.LogWarning("Could not identify user from JWT claims for Synchronisation Rule update");
            return Unauthorized(ApiErrorResponse.Unauthorised("Could not identify user from authentication token."));
        }

        // Get the existing Synchronisation Rule
        var syncRule = await _application.ConnectedSystems.GetSyncRuleAsync(id);
        if (syncRule == null)
            return NotFound(ApiErrorResponse.NotFound($"Synchronisation Rule with ID {id} not found."));

        // Apply updates
        if (!string.IsNullOrEmpty(request.Name))
            syncRule.Name = request.Name;

        if (request.Enabled.HasValue)
            syncRule.Enabled = request.Enabled.Value;

        if (request.ProjectToMetaverse.HasValue)
            syncRule.ProjectToMetaverse = request.ProjectToMetaverse.Value;

        if (request.ProvisionToConnectedSystem.HasValue)
            syncRule.ProvisionToConnectedSystem = request.ProvisionToConnectedSystem.Value;

        if (request.EnforceState.HasValue)
            syncRule.EnforceState = request.EnforceState.Value;

        if (request.InboundOutOfScopeAction.HasValue)
            syncRule.InboundOutOfScopeAction = request.InboundOutOfScopeAction.Value;

        if (request.OutboundDeprovisionAction.HasValue)
            syncRule.OutboundDeprovisionAction = request.OutboundDeprovisionAction.Value;

        var apiKey = await GetCurrentApiKeyAsync();
        bool success;
        if (apiKey != null)
            success = await _application.ConnectedSystems.CreateOrUpdateSyncRuleAsync(syncRule, apiKey);
        else
            success = await _application.ConnectedSystems.CreateOrUpdateSyncRuleAsync(syncRule, initiatedBy);
        if (!success)
        {
            var validationErrors = syncRule.Validate();
            var errorMessage = string.Join("; ", validationErrors.Select(v => v.Message));
            return BadRequest(ApiErrorResponse.BadRequest($"Synchronisation Rule validation failed: {errorMessage}"));
        }

        _logger.LogInformation("Updated Synchronisation Rule: {Id} ({Name})", syncRule.Id, LogSanitiser.Sanitise(syncRule.Name));

        // Retrieve the updated Synchronisation Rule
        var updated = await _application.ConnectedSystems.GetSyncRuleAsync(id);
        return Ok(SyncRuleHeader.FromEntity(updated!));
    }

    /// <summary>
    /// Delete a Synchronisation Rule
    /// </summary>
    /// <param name="id">The unique identifier of the Synchronisation Rule to delete.</param>
    /// <returns>No content on success.</returns>
    /// <response code="204">Synchronisation Rule deleted successfully.</response>
    /// <response code="404">Synchronisation Rule not found.</response>
    /// <response code="401">User could not be identified from authentication token.</response>
    [HttpDelete("sync-rules/{id:int}", Name = "DeleteSyncRule")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> DeleteSyncRuleAsync(int id)
    {
        _logger.LogInformation("Deleting Synchronisation Rule: {Id}", id);

        // Get the current user from the JWT claims (may be null for API key auth)
        var initiatedBy = await GetCurrentUserAsync();
        if (initiatedBy == null && !IsApiKeyAuthenticated())
        {
            _logger.LogWarning("Could not identify user from JWT claims for Synchronisation Rule deletion");
            return Unauthorized(ApiErrorResponse.Unauthorised("Could not identify user from authentication token."));
        }

        // Get the Synchronisation Rule
        var syncRule = await _application.ConnectedSystems.GetSyncRuleAsync(id);
        if (syncRule == null)
            return NotFound(ApiErrorResponse.NotFound($"Synchronisation Rule with ID {id} not found."));

        var apiKey = await GetCurrentApiKeyAsync();
        if (apiKey != null)
            await _application.ConnectedSystems.DeleteSyncRuleAsync(syncRule, apiKey);
        else
            await _application.ConnectedSystems.DeleteSyncRuleAsync(syncRule, initiatedBy);

        _logger.LogInformation("Deleted Synchronisation Rule: {Id}", id);

        return NoContent();
    }

    #endregion

    #region Synchronisation Rule Mappings

    /// <summary>
    /// List Attribute Flow Mappings for a Synchronisation Rule
    /// </summary>
    /// <param name="syncRuleId">The unique identifier of the Synchronisation Rule.</param>
    /// <returns>A list of Attribute Flow Mappings.</returns>
    [HttpGet("sync-rules/{syncRuleId:int}/mappings", Name = "GetSyncRuleMappings")]
    [ProducesResponseType(typeof(IEnumerable<SyncRuleMappingDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetSyncRuleMappingsAsync(int syncRuleId)
    {
        _logger.LogTrace("Requested mappings for Synchronisation Rule: {Id}", syncRuleId);

        var syncRule = await _application.ConnectedSystems.GetSyncRuleAsync(syncRuleId);
        if (syncRule == null)
            return NotFound(ApiErrorResponse.NotFound($"Synchronisation Rule with ID {syncRuleId} not found."));

        var mappings = await _application.ConnectedSystems.GetSyncRuleMappingsAsync(syncRuleId);
        var dtos = mappings.Select(SyncRuleMappingDto.FromEntity);
        return Ok(dtos);
    }

    /// <summary>
    /// Get an Attribute Flow Mapping
    /// </summary>
    /// <param name="syncRuleId">The unique identifier of the Synchronisation Rule.</param>
    /// <param name="mappingId">The unique identifier of the mapping.</param>
    /// <returns>The Attribute Flow Mapping details.</returns>
    [HttpGet("sync-rules/{syncRuleId:int}/mappings/{mappingId:int}", Name = "GetSyncRuleMapping")]
    [ProducesResponseType(typeof(SyncRuleMappingDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetSyncRuleMappingAsync(int syncRuleId, int mappingId)
    {
        _logger.LogTrace("Requested mapping {MappingId} for Synchronisation Rule: {SyncRuleId}", mappingId, syncRuleId);

        var syncRule = await _application.ConnectedSystems.GetSyncRuleAsync(syncRuleId);
        if (syncRule == null)
            return NotFound(ApiErrorResponse.NotFound($"Synchronisation Rule with ID {syncRuleId} not found."));

        var mapping = await _application.ConnectedSystems.GetSyncRuleMappingAsync(mappingId);
        if (mapping == null || mapping.SyncRule?.Id != syncRuleId)
            return NotFound(ApiErrorResponse.NotFound($"Mapping with ID {mappingId} not found in Synchronisation Rule {syncRuleId}."));

        return Ok(SyncRuleMappingDto.FromEntity(mapping));
    }

    /// <summary>
    /// Create an Attribute Flow Mapping
    /// </summary>
    /// <remarks>
    /// For Import rules, specify <c>TargetMetaverseAttributeId</c> and source <c>ConnectedSystemAttributeIds</c>. For Export rules, specify <c>TargetConnectedSystemAttributeId</c> and source <c>MetaverseAttributeIds</c>.
    /// </remarks>
    /// <param name="syncRuleId">The unique identifier of the Synchronisation Rule.</param>
    /// <param name="request">The mapping creation request.</param>
    /// <returns>The created Attribute Flow Mapping.</returns>
    /// <response code="201">Mapping created successfully.</response>
    /// <response code="400">Invalid request or validation failed.</response>
    /// <response code="404">Synchronisation Rule or referenced Attributes not found.</response>
    /// <response code="401">User could not be identified from authentication token.</response>
    [HttpPost("sync-rules/{syncRuleId:int}/mappings", Name = "CreateSyncRuleMapping")]
    [ProducesResponseType(typeof(SyncRuleMappingDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> CreateSyncRuleMappingAsync(int syncRuleId, [FromBody] CreateSyncRuleMappingRequest request)
    {
        _logger.LogInformation("Creating mapping for Synchronisation Rule: {SyncRuleId}", syncRuleId);

        var initiatedBy = await GetCurrentUserAsync();
        if (initiatedBy == null && !IsApiKeyAuthenticated())
        {
            _logger.LogWarning("Could not identify user from JWT claims for mapping creation");
            return Unauthorized(ApiErrorResponse.Unauthorised("Could not identify user from authentication token."));
        }

        var syncRule = await _application.ConnectedSystems.GetSyncRuleAsync(syncRuleId);
        if (syncRule == null)
            return NotFound(ApiErrorResponse.NotFound($"Synchronisation Rule with ID {syncRuleId} not found."));

        // Create the mapping using FK ID and nav property (nav property needed for validation;
        // cleared before save by ClearMappingNavigationProperties)
        var mapping = new SyncRuleMapping
        {
            SyncRule = syncRule,
            SyncRuleId = syncRule.Id
        };

        // Validate and set target attribute based on direction
        if (syncRule.Direction == SyncRuleDirection.Import)
        {
            if (!request.TargetMetaverseAttributeId.HasValue)
                return BadRequest(ApiErrorResponse.BadRequest("TargetMetaverseAttributeId is required for import rules."));

            var mvAttr = await _application.Metaverse.GetMetaverseAttributeAsync(request.TargetMetaverseAttributeId.Value);
            if (mvAttr == null)
                return NotFound(ApiErrorResponse.NotFound($"Metaverse attribute with ID {request.TargetMetaverseAttributeId} not found."));

            mapping.TargetMetaverseAttributeId = mvAttr.Id;
            mapping.TargetMetaverseAttribute = mvAttr;

            // Inbound value processing applies to import mappings only (#843). The entity carries the
            // defaults (TreatWhitespaceAsNoValue / None); only override when the request supplies a value.
            if (request.InboundValueProcessing.HasValue)
                mapping.InboundValueProcessing = request.InboundValueProcessing.Value;
            if (request.CaseNormalisation.HasValue)
                mapping.CaseNormalisation = request.CaseNormalisation.Value;

            // "Null is a value" is a property of this mapping (#91), set at creation. Priority is left at its
            // safe-addition default (int.MaxValue) so the new contribution never wins until ordered via the
            // attribute-priority-order endpoint.
            if (request.NullIsValue.HasValue)
                mapping.NullIsValue = request.NullIsValue.Value;
        }
        else // Export
        {
            if (!request.TargetConnectedSystemAttributeId.HasValue)
                return BadRequest(ApiErrorResponse.BadRequest("TargetConnectedSystemAttributeId is required for export rules."));

            var csAttr = await _application.ConnectedSystems.GetAttributeAsync(request.TargetConnectedSystemAttributeId.Value);
            if (csAttr == null)
                return NotFound(ApiErrorResponse.NotFound($"Connected System attribute with ID {request.TargetConnectedSystemAttributeId} not found."));

            // Verify attribute belongs to the Synchronisation Rule's object type
            if (csAttr.ConnectedSystemObjectType.Id != syncRule.ConnectedSystemObjectTypeId)
                return BadRequest(ApiErrorResponse.BadRequest($"Attribute {csAttr.Name} does not belong to the Synchronisation Rule's object type."));

            mapping.TargetConnectedSystemAttributeId = csAttr.Id;
            mapping.TargetConnectedSystemAttribute = csAttr;
        }

        // Add sources
        foreach (var sourceRequest in request.Sources)
        {
            var source = new SyncRuleMappingSource
            {
                Order = sourceRequest.Order
            };

            // Check if this is an expression-based source
            if (!string.IsNullOrWhiteSpace(sourceRequest.Expression))
            {
                // Expression-based source - validate the expression
                var validationResult = _expressionEvaluator.Validate(sourceRequest.Expression);
                if (!validationResult.IsValid)
                    return BadRequest(ApiErrorResponse.BadRequest($"Invalid expression: {validationResult.ErrorMessage}"));

                source.Expression = sourceRequest.Expression;
            }
            else if (syncRule.Direction == SyncRuleDirection.Import)
            {
                // Attribute-based import source
                if (!sourceRequest.ConnectedSystemAttributeId.HasValue)
                    return BadRequest(ApiErrorResponse.BadRequest("ConnectedSystemAttributeId or Expression is required for import rule sources."));

                var csAttr = await _application.ConnectedSystems.GetAttributeAsync(sourceRequest.ConnectedSystemAttributeId.Value);
                if (csAttr == null)
                    return NotFound(ApiErrorResponse.NotFound($"Connected System attribute with ID {sourceRequest.ConnectedSystemAttributeId} not found."));

                // Verify attribute belongs to the Synchronisation Rule's object type
                if (csAttr.ConnectedSystemObjectType.Id != syncRule.ConnectedSystemObjectTypeId)
                    return BadRequest(ApiErrorResponse.BadRequest($"Attribute {csAttr.Name} does not belong to the Synchronisation Rule's object type."));

                source.ConnectedSystemAttributeId = csAttr.Id;
                source.ConnectedSystemAttribute = csAttr;
            }
            else // Export
            {
                // Expression-based or attribute-based export source
                if (!sourceRequest.MetaverseAttributeId.HasValue && string.IsNullOrWhiteSpace(sourceRequest.Expression))
                    return BadRequest(ApiErrorResponse.BadRequest("MetaverseAttributeId or Expression is required for export rule sources."));

                // If attribute-based, validate the attribute exists
                if (sourceRequest.MetaverseAttributeId.HasValue)
                {
                    var mvAttr = await _application.Metaverse.GetMetaverseAttributeAsync(sourceRequest.MetaverseAttributeId.Value);
                    if (mvAttr == null)
                        return NotFound(ApiErrorResponse.NotFound($"Metaverse attribute with ID {sourceRequest.MetaverseAttributeId} not found."));

                    source.MetaverseAttributeId = mvAttr.Id;
                    source.MetaverseAttribute = mvAttr;
                }
                // Expression is already set on source from sourceRequest.Expression above
            }

            mapping.Sources.Add(source);
        }

        try
        {
            var apiKey = await GetCurrentApiKeyAsync();
            if (apiKey != null)
                await _application.ConnectedSystems.CreateSyncRuleMappingAsync(mapping, apiKey);
            else
                await _application.ConnectedSystems.CreateSyncRuleMappingAsync(mapping, initiatedBy);

            _logger.LogInformation("Created mapping {MappingId} for Synchronisation Rule {SyncRuleId}", mapping.Id, syncRuleId);

            // Retrieve the created mapping to get all populated fields
            var created = await _application.ConnectedSystems.GetSyncRuleMappingAsync(mapping.Id);
            return CreatedAtRoute("GetSyncRuleMapping", new { syncRuleId, mappingId = mapping.Id }, SyncRuleMappingDto.FromEntity(created!));
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning(ex, "Failed to create Synchronisation Rule mapping: {Message}", ex.Message);
            return BadRequest(ApiErrorResponse.BadRequest(ex.Message));
        }
    }

    /// <summary>
    /// Delete an Attribute Flow Mapping
    /// </summary>
    /// <param name="syncRuleId">The unique identifier of the Synchronisation Rule.</param>
    /// <param name="mappingId">The unique identifier of the mapping to delete.</param>
    /// <returns>No content on success.</returns>
    /// <response code="204">Mapping deleted successfully.</response>
    /// <response code="404">Synchronisation Rule or mapping not found.</response>
    /// <response code="401">User could not be identified from authentication token.</response>
    [HttpDelete("sync-rules/{syncRuleId:int}/mappings/{mappingId:int}", Name = "DeleteSyncRuleMapping")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> DeleteSyncRuleMappingAsync(int syncRuleId, int mappingId)
    {
        _logger.LogInformation("Deleting mapping {MappingId} for Synchronisation Rule {SyncRuleId}", mappingId, syncRuleId);

        var initiatedBy = await GetCurrentUserAsync();
        if (initiatedBy == null && !IsApiKeyAuthenticated())
        {
            _logger.LogWarning("Could not identify user from JWT claims for mapping deletion");
            return Unauthorized(ApiErrorResponse.Unauthorised("Could not identify user from authentication token."));
        }

        var syncRule = await _application.ConnectedSystems.GetSyncRuleAsync(syncRuleId);
        if (syncRule == null)
            return NotFound(ApiErrorResponse.NotFound($"Synchronisation Rule with ID {syncRuleId} not found."));

        var mapping = await _application.ConnectedSystems.GetSyncRuleMappingAsync(mappingId);
        if (mapping == null || mapping.SyncRule?.Id != syncRuleId)
            return NotFound(ApiErrorResponse.NotFound($"Mapping with ID {mappingId} not found in Synchronisation Rule {syncRuleId}."));

        // Get the current API key for Activity attribution if authenticated via API key
        var apiKey = await GetCurrentApiKeyAsync();
        if (apiKey != null)
            await _application.ConnectedSystems.DeleteSyncRuleMappingAsync(mapping, apiKey);
        else
            await _application.ConnectedSystems.DeleteSyncRuleMappingAsync(mapping, initiatedBy);

        _logger.LogInformation("Deleted mapping {MappingId} from Synchronisation Rule {SyncRuleId}", mappingId, syncRuleId);

        return NoContent();
    }

    /// <summary>
    /// Get an attribute's priority order
    /// </summary>
    /// <remarks>
    /// Returns the ordered list of import contributions to a single Metaverse attribute for a single Metaverse
    /// Object Type (#91), highest priority first. Disabled Synchronisation Rules are included; they hold position
    /// but never contribute during resolution. An empty list means the attribute has no import contributors.
    /// </remarks>
    /// <param name="metaverseObjectTypeId">The Metaverse Object Type that scopes the priority list.</param>
    /// <param name="metaverseAttributeId">The target Metaverse attribute.</param>
    [HttpGet("attribute-priority/{metaverseObjectTypeId:int}/{metaverseAttributeId:int}", Name = "GetAttributePriorityOrder")]
    [ProducesResponseType(typeof(AttributePriorityOrderDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetAttributePriorityOrderAsync(int metaverseObjectTypeId, int metaverseAttributeId)
    {
        _logger.LogTrace("Requested attribute priority order for Metaverse attribute {AttributeId} on object type {ObjectTypeId}", metaverseAttributeId, metaverseObjectTypeId);

        var attribute = await _application.Metaverse.GetMetaverseAttributeAsync(metaverseAttributeId);
        if (attribute == null)
            return NotFound(ApiErrorResponse.NotFound($"Metaverse attribute with ID {metaverseAttributeId} not found."));

        var mappings = await _application.ConnectedSystems.GetAttributePriorityOrderAsync(metaverseObjectTypeId, metaverseAttributeId);
        return Ok(AttributePriorityOrderDto.FromEntities(metaverseObjectTypeId, metaverseAttributeId, mappings));
    }

    /// <summary>
    /// Replace an attribute's priority order
    /// </summary>
    /// <remarks>
    /// Transactionally renumbers the priorities of all import contributions to a single Metaverse attribute for a
    /// single Metaverse Object Type (#91), and applies each contribution's "Null is a value" flag. The request must
    /// list every current contributing mapping for the attribute exactly once, in the desired priority order
    /// (highest first). To move a single mapping without restating the whole list, use the move endpoint instead.
    /// Returns the resulting order.
    /// </remarks>
    /// <param name="metaverseObjectTypeId">The Metaverse Object Type that scopes the priority list.</param>
    /// <param name="metaverseAttributeId">The target Metaverse attribute.</param>
    /// <param name="request">The contributors in the desired priority order.</param>
    /// <response code="200">Priority order updated successfully; returns the updated order.</response>
    /// <response code="400">The requested order does not match the attribute's current contributor set.</response>
    /// <response code="404">Metaverse attribute not found.</response>
    /// <response code="401">User could not be identified from authentication token.</response>
    [HttpPut("attribute-priority/{metaverseObjectTypeId:int}/{metaverseAttributeId:int}", Name = "SetAttributePriorityOrder")]
    [ProducesResponseType(typeof(AttributePriorityOrderDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> SetAttributePriorityOrderAsync(int metaverseObjectTypeId, int metaverseAttributeId, [FromBody] SetAttributePriorityOrderRequest request)
    {
        _logger.LogInformation("Setting attribute priority order for Metaverse attribute {AttributeId} on object type {ObjectTypeId}", metaverseAttributeId, metaverseObjectTypeId);

        var initiatedBy = await GetCurrentUserAsync();
        if (initiatedBy == null && !IsApiKeyAuthenticated())
        {
            _logger.LogWarning("Could not identify user from JWT claims for attribute priority order update");
            return Unauthorized(ApiErrorResponse.Unauthorised("Could not identify user from authentication token."));
        }

        var attribute = await _application.Metaverse.GetMetaverseAttributeAsync(metaverseAttributeId);
        if (attribute == null)
            return NotFound(ApiErrorResponse.NotFound($"Metaverse attribute with ID {metaverseAttributeId} not found."));

        var orderedContributors = request.Contributors
            .Select(c => (c.MappingId, c.NullIsValue))
            .ToList();

        try
        {
            var apiKey = await GetCurrentApiKeyAsync();
            var updated = apiKey != null
                ? await _application.ConnectedSystems.SetAttributePriorityOrderAsync(metaverseObjectTypeId, metaverseAttributeId, orderedContributors, apiKey)
                : await _application.ConnectedSystems.SetAttributePriorityOrderAsync(metaverseObjectTypeId, metaverseAttributeId, orderedContributors, initiatedBy);

            return Ok(AttributePriorityOrderDto.FromEntities(metaverseObjectTypeId, metaverseAttributeId, updated));
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning(ex, "Failed to set attribute priority order: {Message}", ex.Message);
            return BadRequest(ApiErrorResponse.BadRequest(ex.Message));
        }
    }

    /// <summary>
    /// Move a mapping to a new priority position
    /// </summary>
    /// <remarks>
    /// Repositions a single contributing mapping to the given 1-based priority position for a Metaverse attribute
    /// on a Metaverse Object Type (#91), shuffling the other contributors to keep the list contiguous, then
    /// renumbering all affected rows in one transaction. The caller states only the new position; the engine keeps
    /// the order gap-free and duplicate-free. Optionally also updates the moved mapping's "Null is a value" flag.
    /// Returns the resulting order, so the caller never has to renumber siblings or re-fetch.
    /// </remarks>
    /// <param name="metaverseObjectTypeId">The Metaverse Object Type that scopes the priority list.</param>
    /// <param name="metaverseAttributeId">The target Metaverse attribute.</param>
    /// <param name="mappingId">The contributing mapping to move.</param>
    /// <param name="request">The desired position (and optional "Null is a value" flag).</param>
    /// <response code="200">Mapping moved successfully; returns the resulting order.</response>
    /// <response code="400">The mapping is not a contributor to the attribute.</response>
    /// <response code="404">Metaverse attribute not found.</response>
    /// <response code="401">User could not be identified from authentication token.</response>
    [HttpPut("attribute-priority/{metaverseObjectTypeId:int}/{metaverseAttributeId:int}/mappings/{mappingId:int}", Name = "MoveAttributePriority")]
    [ProducesResponseType(typeof(AttributePriorityOrderDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> MoveAttributePriorityAsync(int metaverseObjectTypeId, int metaverseAttributeId, int mappingId, [FromBody] MoveAttributePriorityRequest request)
    {
        _logger.LogInformation("Moving mapping {MappingId} to position {Position} for Metaverse attribute {AttributeId} on object type {ObjectTypeId}", mappingId, request.Position, metaverseAttributeId, metaverseObjectTypeId);

        var initiatedBy = await GetCurrentUserAsync();
        if (initiatedBy == null && !IsApiKeyAuthenticated())
        {
            _logger.LogWarning("Could not identify user from JWT claims for attribute priority move");
            return Unauthorized(ApiErrorResponse.Unauthorised("Could not identify user from authentication token."));
        }

        var attribute = await _application.Metaverse.GetMetaverseAttributeAsync(metaverseAttributeId);
        if (attribute == null)
            return NotFound(ApiErrorResponse.NotFound($"Metaverse attribute with ID {metaverseAttributeId} not found."));

        try
        {
            var apiKey = await GetCurrentApiKeyAsync();
            var updated = apiKey != null
                ? await _application.ConnectedSystems.MoveAttributePriorityAsync(metaverseObjectTypeId, metaverseAttributeId, mappingId, request.Position, request.NullIsValue, apiKey)
                : await _application.ConnectedSystems.MoveAttributePriorityAsync(metaverseObjectTypeId, metaverseAttributeId, mappingId, request.Position, request.NullIsValue, initiatedBy);

            return Ok(AttributePriorityOrderDto.FromEntities(metaverseObjectTypeId, metaverseAttributeId, updated));
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning(ex, "Failed to move mapping in attribute priority order: {Message}", ex.Message);
            return BadRequest(ApiErrorResponse.BadRequest(ex.Message));
        }
    }

    #endregion

    #region Synchronisation Rule Scoping Criteria

    /// <summary>
    /// List Scoping Criteria groups for a Synchronisation Rule
    /// </summary>
    /// <param name="syncRuleId">The unique identifier of the Synchronisation Rule.</param>
    /// <returns>A list of Scoping Criteria groups with their criteria.</returns>
    /// <response code="200">Returns the list of Scoping Criteria groups.</response>
    /// <response code="400">Synchronisation Rule is not an export rule.</response>
    /// <response code="404">Synchronisation Rule not found.</response>
    [HttpGet("sync-rules/{syncRuleId:int}/scoping-criteria", Name = "GetScopingCriteriaGroups")]
    [ProducesResponseType(typeof(IEnumerable<SyncRuleScopingCriteriaGroupDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetScopingCriteriaGroupsAsync(int syncRuleId)
    {
        _logger.LogTrace("Requested scoping criteria for Synchronisation Rule: {Id}", syncRuleId);

        var syncRule = await _application.ConnectedSystems.GetSyncRuleAsync(syncRuleId);
        if (syncRule == null)
            return NotFound(ApiErrorResponse.NotFound($"Synchronisation Rule with ID {syncRuleId} not found."));

        var dtos = syncRule.ObjectScopingCriteriaGroups
            .Where(g => g.ParentGroup == null) // Only return root groups (children are nested)
            .Select(SyncRuleScopingCriteriaGroupDto.FromEntity);

        return Ok(dtos);
    }

    /// <summary>
    /// Get a Scoping Criteria group
    /// </summary>
    /// <param name="syncRuleId">The unique identifier of the Synchronisation Rule.</param>
    /// <param name="groupId">The unique identifier of the criteria group.</param>
    /// <returns>The Scoping Criteria group details.</returns>
    [HttpGet("sync-rules/{syncRuleId:int}/scoping-criteria/{groupId:int}", Name = "GetScopingCriteriaGroup")]
    [ProducesResponseType(typeof(SyncRuleScopingCriteriaGroupDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetScopingCriteriaGroupAsync(int syncRuleId, int groupId)
    {
        _logger.LogTrace("Requested scoping criteria group {GroupId} for Synchronisation Rule: {SyncRuleId}", groupId, syncRuleId);

        var syncRule = await _application.ConnectedSystems.GetSyncRuleAsync(syncRuleId);
        if (syncRule == null)
            return NotFound(ApiErrorResponse.NotFound($"Synchronisation Rule with ID {syncRuleId} not found."));

        var group = FindScopingCriteriaGroup(syncRule.ObjectScopingCriteriaGroups, groupId);
        if (group == null)
            return NotFound(ApiErrorResponse.NotFound($"Scoping criteria group with ID {groupId} not found in Synchronisation Rule {syncRuleId}."));

        return Ok(SyncRuleScopingCriteriaGroupDto.FromEntity(group));
    }

    /// <summary>
    /// Create a root Scoping Criteria group
    /// </summary>
    /// <remarks>
    /// Creates a group at the root level. Use the child-groups endpoint to create nested groups.
    /// </remarks>
    /// <param name="syncRuleId">The unique identifier of the Synchronisation Rule.</param>
    /// <param name="request">The criteria group creation request.</param>
    /// <returns>The created Scoping Criteria group.</returns>
    /// <response code="201">Group created successfully.</response>
    /// <response code="400">Invalid request.</response>
    /// <response code="404">Synchronisation Rule not found.</response>
    [HttpPost("sync-rules/{syncRuleId:int}/scoping-criteria", Name = "CreateScopingCriteriaGroup")]
    [ProducesResponseType(typeof(SyncRuleScopingCriteriaGroupDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> CreateScopingCriteriaGroupAsync(int syncRuleId, [FromBody] CreateScopingCriteriaGroupRequest request)
    {
        _logger.LogInformation("Creating scoping criteria group for Synchronisation Rule: {SyncRuleId}", syncRuleId);

        var initiatedBy = await GetCurrentUserAsync();
        if (initiatedBy == null && !IsApiKeyAuthenticated())
            return Unauthorized(ApiErrorResponse.Unauthorised("Could not identify user from authentication token."));

        var syncRule = await _application.ConnectedSystems.GetSyncRuleAsync(syncRuleId);
        if (syncRule == null)
            return NotFound(ApiErrorResponse.NotFound($"Synchronisation Rule with ID {syncRuleId} not found."));

        if (!Enum.TryParse<SearchGroupType>(request.Type, true, out var groupType))
            return BadRequest(ApiErrorResponse.BadRequest($"Invalid group type '{request.Type}'. Valid values: All, Any."));

        var group = new SyncRuleScopingCriteriaGroup
        {
            Type = groupType,
            Position = request.Position
        };

        syncRule.ObjectScopingCriteriaGroups.Add(group);

        try
        {
            var apiKey = await GetCurrentApiKeyAsync();
            if (apiKey != null)
                await _application.ConnectedSystems.CreateOrUpdateSyncRuleAsync(syncRule, apiKey);
            else
                await _application.ConnectedSystems.CreateOrUpdateSyncRuleAsync(syncRule, initiatedBy);
            _logger.LogInformation("Created scoping criteria group {GroupId} for Synchronisation Rule {SyncRuleId}", group.Id, syncRuleId);
            return CreatedAtRoute("GetScopingCriteriaGroup", new { syncRuleId, groupId = group.Id }, SyncRuleScopingCriteriaGroupDto.FromEntity(group));
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning(ex, "Failed to create scoping criteria group: {Message}", ex.Message);
            return BadRequest(ApiErrorResponse.BadRequest(ex.Message));
        }
    }

    /// <summary>
    /// Create a child Scoping Criteria group
    /// </summary>
    /// <param name="syncRuleId">The unique identifier of the Synchronisation Rule.</param>
    /// <param name="parentGroupId">The unique identifier of the parent criteria group.</param>
    /// <param name="request">The criteria group creation request.</param>
    /// <returns>The created Scoping Criteria group.</returns>
    [HttpPost("sync-rules/{syncRuleId:int}/scoping-criteria/{parentGroupId:int}/child-groups", Name = "CreateChildScopingCriteriaGroup")]
    [ProducesResponseType(typeof(SyncRuleScopingCriteriaGroupDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> CreateChildScopingCriteriaGroupAsync(int syncRuleId, int parentGroupId, [FromBody] CreateScopingCriteriaGroupRequest request)
    {
        _logger.LogInformation("Creating child scoping criteria group under {ParentId} for Synchronisation Rule: {SyncRuleId}", parentGroupId, syncRuleId);

        var initiatedBy = await GetCurrentUserAsync();
        if (initiatedBy == null && !IsApiKeyAuthenticated())
            return Unauthorized(ApiErrorResponse.Unauthorised("Could not identify user from authentication token."));

        var syncRule = await _application.ConnectedSystems.GetSyncRuleAsync(syncRuleId);
        if (syncRule == null)
            return NotFound(ApiErrorResponse.NotFound($"Synchronisation Rule with ID {syncRuleId} not found."));

        var parentGroup = FindScopingCriteriaGroup(syncRule.ObjectScopingCriteriaGroups, parentGroupId);
        if (parentGroup == null)
            return NotFound(ApiErrorResponse.NotFound($"Parent scoping criteria group with ID {parentGroupId} not found."));

        if (!Enum.TryParse<SearchGroupType>(request.Type, true, out var groupType))
            return BadRequest(ApiErrorResponse.BadRequest($"Invalid group type '{request.Type}'. Valid values: All, Any."));

        var childGroup = new SyncRuleScopingCriteriaGroup
        {
            Type = groupType,
            Position = request.Position,
            ParentGroup = parentGroup
        };

        parentGroup.ChildGroups.Add(childGroup);

        try
        {
            var apiKey = await GetCurrentApiKeyAsync();
            if (apiKey != null)
                await _application.ConnectedSystems.CreateOrUpdateSyncRuleAsync(syncRule, apiKey);
            else
                await _application.ConnectedSystems.CreateOrUpdateSyncRuleAsync(syncRule, initiatedBy);
            _logger.LogInformation("Created child scoping criteria group {GroupId} under {ParentId}", childGroup.Id, parentGroupId);
            return CreatedAtRoute("GetScopingCriteriaGroup", new { syncRuleId, groupId = childGroup.Id }, SyncRuleScopingCriteriaGroupDto.FromEntity(childGroup));
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning(ex, "Failed to create child scoping criteria group: {Message}", ex.Message);
            return BadRequest(ApiErrorResponse.BadRequest(ex.Message));
        }
    }

    /// <summary>
    /// Update a Scoping Criteria group
    /// </summary>
    /// <param name="syncRuleId">The unique identifier of the Synchronisation Rule.</param>
    /// <param name="groupId">The unique identifier of the criteria group.</param>
    /// <param name="request">The update request.</param>
    /// <returns>The updated Scoping Criteria group.</returns>
    [HttpPut("sync-rules/{syncRuleId:int}/scoping-criteria/{groupId:int}", Name = "UpdateScopingCriteriaGroup")]
    [ProducesResponseType(typeof(SyncRuleScopingCriteriaGroupDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> UpdateScopingCriteriaGroupAsync(int syncRuleId, int groupId, [FromBody] UpdateScopingCriteriaGroupRequest request)
    {
        _logger.LogInformation("Updating scoping criteria group {GroupId} for Synchronisation Rule: {SyncRuleId}", groupId, syncRuleId);

        var initiatedBy = await GetCurrentUserAsync();
        if (initiatedBy == null && !IsApiKeyAuthenticated())
            return Unauthorized(ApiErrorResponse.Unauthorised("Could not identify user from authentication token."));

        var syncRule = await _application.ConnectedSystems.GetSyncRuleAsync(syncRuleId);
        if (syncRule == null)
            return NotFound(ApiErrorResponse.NotFound($"Synchronisation Rule with ID {syncRuleId} not found."));

        var group = FindScopingCriteriaGroup(syncRule.ObjectScopingCriteriaGroups, groupId);
        if (group == null)
            return NotFound(ApiErrorResponse.NotFound($"Scoping criteria group with ID {groupId} not found."));

        if (!string.IsNullOrEmpty(request.Type))
        {
            if (!Enum.TryParse<SearchGroupType>(request.Type, true, out var groupType))
                return BadRequest(ApiErrorResponse.BadRequest($"Invalid group type '{request.Type}'. Valid values: All, Any."));
            group.Type = groupType;
        }

        if (request.Position.HasValue)
            group.Position = request.Position.Value;

        try
        {
            var apiKey = await GetCurrentApiKeyAsync();
            if (apiKey != null)
                await _application.ConnectedSystems.CreateOrUpdateSyncRuleAsync(syncRule, apiKey);
            else
                await _application.ConnectedSystems.CreateOrUpdateSyncRuleAsync(syncRule, initiatedBy);
            _logger.LogInformation("Updated scoping criteria group {GroupId}", groupId);
            return Ok(SyncRuleScopingCriteriaGroupDto.FromEntity(group));
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning(ex, "Failed to update scoping criteria group: {Message}", ex.Message);
            return BadRequest(ApiErrorResponse.BadRequest(ex.Message));
        }
    }

    /// <summary>
    /// Delete a Scoping Criteria group
    /// </summary>
    /// <param name="syncRuleId">The unique identifier of the Synchronisation Rule.</param>
    /// <param name="groupId">The unique identifier of the criteria group to delete.</param>
    /// <returns>No content on success.</returns>
    [HttpDelete("sync-rules/{syncRuleId:int}/scoping-criteria/{groupId:int}", Name = "DeleteScopingCriteriaGroup")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> DeleteScopingCriteriaGroupAsync(int syncRuleId, int groupId)
    {
        _logger.LogInformation("Deleting scoping criteria group {GroupId} for Synchronisation Rule: {SyncRuleId}", groupId, syncRuleId);

        var initiatedBy = await GetCurrentUserAsync();
        if (initiatedBy == null && !IsApiKeyAuthenticated())
            return Unauthorized(ApiErrorResponse.Unauthorised("Could not identify user from authentication token."));

        var syncRule = await _application.ConnectedSystems.GetSyncRuleAsync(syncRuleId);
        if (syncRule == null)
            return NotFound(ApiErrorResponse.NotFound($"Synchronisation Rule with ID {syncRuleId} not found."));

        var group = FindScopingCriteriaGroup(syncRule.ObjectScopingCriteriaGroups, groupId);
        if (group == null)
            return NotFound(ApiErrorResponse.NotFound($"Scoping criteria group with ID {groupId} not found."));

        // Remove from parent or root
        if (group.ParentGroup != null)
            group.ParentGroup.ChildGroups.Remove(group);
        else
            syncRule.ObjectScopingCriteriaGroups.Remove(group);

        try
        {
            var apiKey = await GetCurrentApiKeyAsync();
            if (apiKey != null)
                await _application.ConnectedSystems.CreateOrUpdateSyncRuleAsync(syncRule, apiKey);
            else
                await _application.ConnectedSystems.CreateOrUpdateSyncRuleAsync(syncRule, initiatedBy);
            _logger.LogInformation("Deleted scoping criteria group {GroupId}", groupId);
            return NoContent();
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning(ex, "Failed to delete scoping criteria group: {Message}", ex.Message);
            return BadRequest(ApiErrorResponse.BadRequest(ex.Message));
        }
    }

    /// <summary>
    /// Add a criterion to a Scoping Criteria group
    /// </summary>
    /// <remarks>
    /// For Export Synchronisation Rules, provide <c>MetaverseAttributeId</c>. For Import Synchronisation Rules, provide <c>ConnectedSystemAttributeId</c>.
    /// </remarks>
    /// <param name="syncRuleId">The unique identifier of the Synchronisation Rule.</param>
    /// <param name="groupId">The unique identifier of the criteria group.</param>
    /// <param name="request">The criterion creation request.</param>
    /// <returns>The created criterion.</returns>
    [HttpPost("sync-rules/{syncRuleId:int}/scoping-criteria/{groupId:int}/criteria", Name = "CreateScopingCriterion")]
    [ProducesResponseType(typeof(SyncRuleScopingCriteriaDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> CreateScopingCriterionAsync(int syncRuleId, int groupId, [FromBody] CreateScopingCriterionRequest request)
    {
        _logger.LogInformation("Creating criterion in group {GroupId} for Synchronisation Rule: {SyncRuleId}", groupId, syncRuleId);

        var initiatedBy = await GetCurrentUserAsync();
        if (initiatedBy == null && !IsApiKeyAuthenticated())
            return Unauthorized(ApiErrorResponse.Unauthorised("Could not identify user from authentication token."));

        var syncRule = await _application.ConnectedSystems.GetSyncRuleAsync(syncRuleId);
        if (syncRule == null)
            return NotFound(ApiErrorResponse.NotFound($"Synchronisation Rule with ID {syncRuleId} not found."));

        var group = FindScopingCriteriaGroup(syncRule.ObjectScopingCriteriaGroups, groupId);
        if (group == null)
            return NotFound(ApiErrorResponse.NotFound($"Scoping criteria group with ID {groupId} not found."));

        // Validate comparison type
        if (!Enum.TryParse<SearchComparisonType>(request.ComparisonType, true, out var comparisonType) || comparisonType == SearchComparisonType.NotSet)
            return BadRequest(ApiErrorResponse.BadRequest($"Invalid comparison type '{request.ComparisonType}'."));

        var criterion = new SyncRuleScopingCriteria
        {
            ComparisonType = comparisonType,
            StringValue = request.StringValue,
            IntValue = request.IntValue,
            LongValue = request.LongValue,
            DateTimeValue = request.DateTimeValue,
            BoolValue = request.BoolValue,
            GuidValue = request.GuidValue,
            CaseSensitive = request.CaseSensitive
        };

        // Set the appropriate attribute based on Synchronisation Rule direction
        if (syncRule.Direction == SyncRuleDirection.Export)
        {
            // Export rules evaluate Metaverse attributes. Resolve the attribute from the
            // Synchronisation Rule's already-tracked MetaverseObjectType.Attributes graph rather than
            // a separate Metaverse repository call: that would return a second untracked
            // instance with the same Id and throw "another instance with the same key value
            // is already being tracked" on SaveChanges. This mirrors the inbound path
            // immediately below.
            if (!request.MetaverseAttributeId.HasValue)
                return BadRequest(ApiErrorResponse.BadRequest("MetaverseAttributeId is required for export Synchronisation Rules."));

            var mvAttribute = syncRule.MetaverseObjectType?.Attributes
                .FirstOrDefault(a => a.Id == request.MetaverseAttributeId);

            if (mvAttribute == null)
                return NotFound(ApiErrorResponse.NotFound($"Metaverse attribute with ID {request.MetaverseAttributeId} not found on this Synchronisation Rule's Metaverse Object Type."));

            criterion.MetaverseAttribute = mvAttribute;
        }
        else
        {
            // Import rules evaluate Connected System attributes
            if (!request.ConnectedSystemAttributeId.HasValue)
                return BadRequest(ApiErrorResponse.BadRequest("ConnectedSystemAttributeId is required for import Synchronisation Rules."));

            // Get the CS attribute from the Synchronisation Rule's Connected System Object Type
            var csAttribute = syncRule.ConnectedSystemObjectType?.Attributes
                .FirstOrDefault(a => a.Id == request.ConnectedSystemAttributeId);

            if (csAttribute == null)
                return NotFound(ApiErrorResponse.NotFound($"Connected System attribute with ID {request.ConnectedSystemAttributeId} not found in Synchronisation Rule's object type."));

            criterion.ConnectedSystemAttribute = csAttribute;
        }

        group.Criteria.Add(criterion);

        try
        {
            var apiKey = await GetCurrentApiKeyAsync();
            if (apiKey != null)
                await _application.ConnectedSystems.CreateOrUpdateSyncRuleAsync(syncRule, apiKey);
            else
                await _application.ConnectedSystems.CreateOrUpdateSyncRuleAsync(syncRule, initiatedBy);
            _logger.LogInformation("Created criterion {CriterionId} in group {GroupId}", criterion.Id, groupId);
            return CreatedAtRoute("GetScopingCriteriaGroup", new { syncRuleId, groupId }, SyncRuleScopingCriteriaDto.FromEntity(criterion));
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning(ex, "Failed to create criterion: {Message}", ex.Message);
            return BadRequest(ApiErrorResponse.BadRequest(ex.Message));
        }
    }

    /// <summary>
    /// Delete a criterion from a Scoping Criteria group
    /// </summary>
    /// <param name="syncRuleId">The unique identifier of the Synchronisation Rule.</param>
    /// <param name="groupId">The unique identifier of the criteria group.</param>
    /// <param name="criterionId">The unique identifier of the criterion to delete.</param>
    /// <returns>No content on success.</returns>
    [HttpDelete("sync-rules/{syncRuleId:int}/scoping-criteria/{groupId:int}/criteria/{criterionId:int}", Name = "DeleteScopingCriterion")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> DeleteScopingCriterionAsync(int syncRuleId, int groupId, int criterionId)
    {
        _logger.LogInformation("Deleting criterion {CriterionId} from group {GroupId}", criterionId, groupId);

        var initiatedBy = await GetCurrentUserAsync();
        if (initiatedBy == null && !IsApiKeyAuthenticated())
            return Unauthorized(ApiErrorResponse.Unauthorised("Could not identify user from authentication token."));

        var syncRule = await _application.ConnectedSystems.GetSyncRuleAsync(syncRuleId);
        if (syncRule == null)
            return NotFound(ApiErrorResponse.NotFound($"Synchronisation Rule with ID {syncRuleId} not found."));

        var group = FindScopingCriteriaGroup(syncRule.ObjectScopingCriteriaGroups, groupId);
        if (group == null)
            return NotFound(ApiErrorResponse.NotFound($"Scoping criteria group with ID {groupId} not found."));

        var criterion = group.Criteria.FirstOrDefault(c => c.Id == criterionId);
        if (criterion == null)
            return NotFound(ApiErrorResponse.NotFound($"Criterion with ID {criterionId} not found in group {groupId}."));

        group.Criteria.Remove(criterion);

        try
        {
            var apiKey = await GetCurrentApiKeyAsync();
            if (apiKey != null)
                await _application.ConnectedSystems.CreateOrUpdateSyncRuleAsync(syncRule, apiKey);
            else
                await _application.ConnectedSystems.CreateOrUpdateSyncRuleAsync(syncRule, initiatedBy);
            _logger.LogInformation("Deleted criterion {CriterionId} from group {GroupId}", criterionId, groupId);
            return NoContent();
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning(ex, "Failed to delete criterion: {Message}", ex.Message);
            return BadRequest(ApiErrorResponse.BadRequest(ex.Message));
        }
    }

    /// <summary>
    /// Recursively finds a Scoping Criteria group by ID within a collection.
    /// </summary>
    private static SyncRuleScopingCriteriaGroup? FindScopingCriteriaGroup(IEnumerable<SyncRuleScopingCriteriaGroup> groups, int groupId)
    {
        foreach (var group in groups)
        {
            if (group.Id == groupId)
                return group;

            var found = FindScopingCriteriaGroup(group.ChildGroups, groupId);
            if (found != null)
                return found;
        }

        return null;
    }

    #endregion

    #region Object Matching Rules

    /// <summary>
    /// List Object Matching Rules for an Object Type
    /// </summary>
    /// <param name="connectedSystemId">The unique identifier of the Connected System.</param>
    /// <param name="objectTypeId">The unique identifier of the Object Type.</param>
    /// <returns>A list of Object Matching Rules.</returns>
    /// <response code="200">Returns the list of Object Matching Rules.</response>
    /// <response code="404">Connected System or Object Type not found.</response>
    [HttpGet("connected-systems/{connectedSystemId:int}/object-types/{objectTypeId:int}/matching-rules", Name = "GetObjectMatchingRules")]
    [ProducesResponseType(typeof(IEnumerable<ObjectMatchingRuleDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetObjectMatchingRulesAsync(int connectedSystemId, int objectTypeId)
    {
        _logger.LogInformation("Getting Object Matching Rules for Connected System {SystemId}, object type {TypeId}", connectedSystemId, objectTypeId);

        var connectedSystem = await _application.ConnectedSystems.GetConnectedSystemAsync(connectedSystemId);
        if (connectedSystem == null)
            return NotFound(ApiErrorResponse.NotFound($"Connected System with ID {connectedSystemId} not found."));

        var objectType = connectedSystem.ObjectTypes?.FirstOrDefault(ot => ot.Id == objectTypeId);
        if (objectType == null)
            return NotFound(ApiErrorResponse.NotFound($"Object type with ID {objectTypeId} not found in Connected System {connectedSystemId}."));

        var rules = objectType.ObjectMatchingRules
            .OrderBy(r => r.Order)
            .Select(ObjectMatchingRuleDto.FromEntity)
            .ToList();

        return Ok(rules);
    }

    /// <summary>
    /// Get an Object Matching Rule
    /// </summary>
    /// <param name="connectedSystemId">The unique identifier of the Connected System.</param>
    /// <param name="ruleId">The unique identifier of the Matching Rule.</param>
    /// <returns>The Object Matching Rule.</returns>
    /// <response code="200">Returns the Object Matching Rule.</response>
    /// <response code="404">Connected System or Matching Rule not found.</response>
    [HttpGet("connected-systems/{connectedSystemId:int}/matching-rules/{ruleId:int}", Name = "GetObjectMatchingRule")]
    [ProducesResponseType(typeof(ObjectMatchingRuleDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetObjectMatchingRuleAsync(int connectedSystemId, int ruleId)
    {
        _logger.LogInformation("Getting Object Matching Rule {RuleId} for Connected System {SystemId}", ruleId, connectedSystemId);

        // Core retrieval — the rule itself is loaded via its own repository method below.
        var connectedSystem = await _application.ConnectedSystems.GetConnectedSystemCoreAsync(connectedSystemId);
        if (connectedSystem == null)
            return NotFound(ApiErrorResponse.NotFound($"Connected System with ID {connectedSystemId} not found."));

        var rule = await _application.Repository.ConnectedSystems.GetObjectMatchingRuleAsync(ruleId);
        if (rule == null)
            return NotFound(ApiErrorResponse.NotFound($"Object Matching Rule with ID {ruleId} not found."));

        // Verify the rule belongs to this Connected System
        if (rule.ConnectedSystemObjectType?.ConnectedSystemId != connectedSystemId)
            return NotFound(ApiErrorResponse.NotFound($"Object Matching Rule with ID {ruleId} not found in Connected System {connectedSystemId}."));

        return Ok(ObjectMatchingRuleDto.FromEntity(rule));
    }

    /// <summary>
    /// Create an Object Matching Rule
    /// </summary>
    /// <param name="connectedSystemId">The unique identifier of the Connected System.</param>
    /// <param name="request">The rule creation request.</param>
    /// <returns>The created Object Matching Rule.</returns>
    /// <response code="201">Object Matching Rule created successfully.</response>
    /// <response code="400">Invalid request data.</response>
    /// <response code="404">Connected System or referenced entities not found.</response>
    /// <response code="401">User could not be identified from authentication token.</response>
    [HttpPost("connected-systems/{connectedSystemId:int}/matching-rules", Name = "CreateObjectMatchingRule")]
    [ProducesResponseType(typeof(ObjectMatchingRuleDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> CreateObjectMatchingRuleAsync(int connectedSystemId, [FromBody] CreateObjectMatchingRuleRequest request)
    {
        _logger.LogInformation("Creating Object Matching Rule for Connected System {SystemId}", connectedSystemId);

        var initiatedBy = await GetCurrentUserAsync();
        if (initiatedBy == null && !IsApiKeyAuthenticated())
        {
            _logger.LogWarning("Could not identify user from JWT claims for matching rule creation");
            return Unauthorized(ApiErrorResponse.Unauthorised("Could not identify user from authentication token."));
        }

        var connectedSystem = await _application.ConnectedSystems.GetConnectedSystemAsync(connectedSystemId);
        if (connectedSystem == null)
            return NotFound(ApiErrorResponse.NotFound($"Connected System with ID {connectedSystemId} not found."));

        var objectType = connectedSystem.ObjectTypes?.FirstOrDefault(ot => ot.Id == request.ConnectedSystemObjectTypeId);
        if (objectType == null)
            return NotFound(ApiErrorResponse.NotFound($"Object type with ID {request.ConnectedSystemObjectTypeId} not found in Connected System {connectedSystemId}."));

        // Validate Metaverse Object Type exists
        var metaverseObjectType = await _application.Metaverse.GetMetaverseObjectTypeAsync(request.MetaverseObjectTypeId, false);
        if (metaverseObjectType == null)
            return NotFound(ApiErrorResponse.NotFound($"Metaverse Object Type with ID {request.MetaverseObjectTypeId} not found."));

        // Validate target MV attribute exists
        var mvAttributes = await _application.Metaverse.GetMetaverseAttributesAsync();
        var targetMvAttr = mvAttributes?.FirstOrDefault(a => a.Id == request.TargetMetaverseAttributeId);
        if (targetMvAttr == null)
            return NotFound(ApiErrorResponse.NotFound($"Metaverse attribute with ID {request.TargetMetaverseAttributeId} not found."));

        // Calculate order if not specified
        var order = request.Order ?? (objectType.ObjectMatchingRules.Count > 0
            ? objectType.ObjectMatchingRules.Max(r => r.Order) + 1
            : 0);

        var rule = new ObjectMatchingRule
        {
            Order = order,
            ConnectedSystemObjectTypeId = objectType.Id,
            ConnectedSystemObjectType = objectType,
            MetaverseObjectTypeId = metaverseObjectType.Id,
            MetaverseObjectType = metaverseObjectType,
            TargetMetaverseAttributeId = targetMvAttr.Id,
            TargetMetaverseAttribute = targetMvAttr,
            CaseSensitive = request.CaseSensitive
        };

        // Add sources
        foreach (var sourceRequest in request.Sources)
        {
            var source = new ObjectMatchingRuleSource
            {
                Order = sourceRequest.Order
            };

            if (sourceRequest.ConnectedSystemAttributeId.HasValue)
            {
                var csAttr = objectType.Attributes.FirstOrDefault(a => a.Id == sourceRequest.ConnectedSystemAttributeId.Value);
                if (csAttr == null)
                    return NotFound(ApiErrorResponse.NotFound($"Connected System attribute with ID {sourceRequest.ConnectedSystemAttributeId} not found in object type."));
                source.ConnectedSystemAttributeId = csAttr.Id;
                source.ConnectedSystemAttribute = csAttr;
            }
            else if (sourceRequest.MetaverseAttributeId.HasValue)
            {
                var mvAttr = mvAttributes?.FirstOrDefault(a => a.Id == sourceRequest.MetaverseAttributeId.Value);
                if (mvAttr == null)
                    return NotFound(ApiErrorResponse.NotFound($"Metaverse attribute with ID {sourceRequest.MetaverseAttributeId} not found."));
                source.MetaverseAttributeId = mvAttr.Id;
                source.MetaverseAttribute = mvAttr;
            }
            else
            {
                return BadRequest(ApiErrorResponse.BadRequest("Each source must specify either ConnectedSystemAttributeId or MetaverseAttributeId."));
            }

            rule.Sources.Add(source);
        }

        try
        {
            var apiKey = await GetCurrentApiKeyAsync();
            if (apiKey != null)
                await _application.ConnectedSystems.CreateObjectMatchingRuleAsync(rule, apiKey);
            else
                await _application.ConnectedSystems.CreateObjectMatchingRuleAsync(rule, initiatedBy);

            _logger.LogInformation("Created Object Matching Rule {RuleId} for Connected System {SystemId}", rule.Id, connectedSystemId);

            return CreatedAtRoute("GetObjectMatchingRule",
                new { connectedSystemId, ruleId = rule.Id },
                ObjectMatchingRuleDto.FromEntity(rule));
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning(ex, "Failed to create Object Matching Rule: {Message}", ex.Message);
            return BadRequest(ApiErrorResponse.BadRequest(ex.Message));
        }
    }

    /// <summary>
    /// Update an Object Matching Rule
    /// </summary>
    /// <param name="connectedSystemId">The unique identifier of the Connected System.</param>
    /// <param name="ruleId">The unique identifier of the Matching Rule.</param>
    /// <param name="request">The update request.</param>
    /// <returns>The updated Object Matching Rule.</returns>
    /// <response code="200">Object Matching Rule updated successfully.</response>
    /// <response code="400">Invalid request data.</response>
    /// <response code="404">Connected System or Matching Rule not found.</response>
    /// <response code="401">User could not be identified from authentication token.</response>
    [HttpPut("connected-systems/{connectedSystemId:int}/matching-rules/{ruleId:int}", Name = "UpdateObjectMatchingRule")]
    [ProducesResponseType(typeof(ObjectMatchingRuleDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> UpdateObjectMatchingRuleAsync(int connectedSystemId, int ruleId, [FromBody] UpdateObjectMatchingRuleRequest request)
    {
        _logger.LogInformation("Updating Object Matching Rule {RuleId} for Connected System {SystemId}", ruleId, connectedSystemId);

        var initiatedBy = await GetCurrentUserAsync();
        if (initiatedBy == null && !IsApiKeyAuthenticated())
        {
            _logger.LogWarning("Could not identify user from JWT claims for matching rule update");
            return Unauthorized(ApiErrorResponse.Unauthorised("Could not identify user from authentication token."));
        }

        var connectedSystem = await _application.ConnectedSystems.GetConnectedSystemAsync(connectedSystemId);
        if (connectedSystem == null)
            return NotFound(ApiErrorResponse.NotFound($"Connected System with ID {connectedSystemId} not found."));

        var rule = await _application.Repository.ConnectedSystems.GetObjectMatchingRuleAsync(ruleId);
        if (rule == null)
            return NotFound(ApiErrorResponse.NotFound($"Object Matching Rule with ID {ruleId} not found."));

        // Verify the rule belongs to this Connected System
        if (rule.ConnectedSystemObjectType?.ConnectedSystemId != connectedSystemId)
            return NotFound(ApiErrorResponse.NotFound($"Object Matching Rule with ID {ruleId} not found in Connected System {connectedSystemId}."));

        // Update order if specified
        if (request.Order.HasValue)
            rule.Order = request.Order.Value;

        // Update Metaverse Object Type if specified
        if (request.MetaverseObjectTypeId.HasValue)
        {
            var metaverseObjectType = await _application.Metaverse.GetMetaverseObjectTypeAsync(request.MetaverseObjectTypeId.Value, false);
            if (metaverseObjectType == null)
                return NotFound(ApiErrorResponse.NotFound($"Metaverse Object Type with ID {request.MetaverseObjectTypeId} not found."));

            rule.MetaverseObjectTypeId = metaverseObjectType.Id;
            rule.MetaverseObjectType = metaverseObjectType;
        }

        // Update target MV attribute if specified
        if (request.TargetMetaverseAttributeId.HasValue)
        {
            var mvAttributes = await _application.Metaverse.GetMetaverseAttributesAsync();
            var targetMvAttr = mvAttributes?.FirstOrDefault(a => a.Id == request.TargetMetaverseAttributeId.Value);
            if (targetMvAttr == null)
                return NotFound(ApiErrorResponse.NotFound($"Metaverse attribute with ID {request.TargetMetaverseAttributeId} not found."));

            rule.TargetMetaverseAttributeId = targetMvAttr.Id;
            rule.TargetMetaverseAttribute = targetMvAttr;
        }

        // Update sources if specified
        if (request.Sources != null)
        {
            var objectType = connectedSystem.ObjectTypes?.FirstOrDefault(ot => ot.Id == rule.ConnectedSystemObjectTypeId);
            var mvAttributes = await _application.Metaverse.GetMetaverseAttributesAsync();

            // Clear existing sources and add new ones
            rule.Sources.Clear();

            foreach (var sourceRequest in request.Sources)
            {
                var source = new ObjectMatchingRuleSource
                {
                    Order = sourceRequest.Order,
                    ObjectMatchingRuleId = rule.Id
                };

                if (sourceRequest.ConnectedSystemAttributeId.HasValue)
                {
                    var csAttr = objectType?.Attributes?.FirstOrDefault(a => a.Id == sourceRequest.ConnectedSystemAttributeId.Value);
                    if (csAttr == null)
                        return NotFound(ApiErrorResponse.NotFound($"Connected System attribute with ID {sourceRequest.ConnectedSystemAttributeId} not found in object type."));
                    source.ConnectedSystemAttributeId = csAttr.Id;
                    source.ConnectedSystemAttribute = csAttr;
                }
                else if (sourceRequest.MetaverseAttributeId.HasValue)
                {
                    var mvAttr = mvAttributes?.FirstOrDefault(a => a.Id == sourceRequest.MetaverseAttributeId.Value);
                    if (mvAttr == null)
                        return NotFound(ApiErrorResponse.NotFound($"Metaverse attribute with ID {sourceRequest.MetaverseAttributeId} not found."));
                    source.MetaverseAttributeId = mvAttr.Id;
                    source.MetaverseAttribute = mvAttr;
                }
                else
                {
                    return BadRequest(ApiErrorResponse.BadRequest("Each source must specify either ConnectedSystemAttributeId or MetaverseAttributeId."));
                }

                rule.Sources.Add(source);
            }
        }

        // Update case sensitivity if specified
        if (request.CaseSensitive.HasValue)
            rule.CaseSensitive = request.CaseSensitive.Value;

        try
        {
            var apiKey = await GetCurrentApiKeyAsync();
            if (apiKey != null)
                await _application.ConnectedSystems.UpdateObjectMatchingRuleAsync(rule, apiKey);
            else
                await _application.ConnectedSystems.UpdateObjectMatchingRuleAsync(rule, initiatedBy);

            _logger.LogInformation("Updated Object Matching Rule {RuleId} for Connected System {SystemId}", ruleId, connectedSystemId);

            return Ok(ObjectMatchingRuleDto.FromEntity(rule));
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning(ex, "Failed to update Object Matching Rule: {Message}", ex.Message);
            return BadRequest(ApiErrorResponse.BadRequest(ex.Message));
        }
    }

    /// <summary>
    /// Delete an Object Matching Rule
    /// </summary>
    /// <param name="connectedSystemId">The unique identifier of the Connected System.</param>
    /// <param name="ruleId">The unique identifier of the Matching Rule.</param>
    /// <returns>No content on success.</returns>
    /// <response code="204">Object Matching Rule deleted successfully.</response>
    /// <response code="404">Connected System or Matching Rule not found.</response>
    /// <response code="401">User could not be identified from authentication token.</response>
    [HttpDelete("connected-systems/{connectedSystemId:int}/matching-rules/{ruleId:int}", Name = "DeleteObjectMatchingRule")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> DeleteObjectMatchingRuleAsync(int connectedSystemId, int ruleId)
    {
        _logger.LogInformation("Deleting Object Matching Rule {RuleId} for Connected System {SystemId}", ruleId, connectedSystemId);

        var initiatedBy = await GetCurrentUserAsync();
        if (initiatedBy == null && !IsApiKeyAuthenticated())
        {
            _logger.LogWarning("Could not identify user from JWT claims for matching rule deletion");
            return Unauthorized(ApiErrorResponse.Unauthorised("Could not identify user from authentication token."));
        }

        // Core retrieval — the rule itself is loaded via its own repository method below.
        var connectedSystem = await _application.ConnectedSystems.GetConnectedSystemCoreAsync(connectedSystemId);
        if (connectedSystem == null)
            return NotFound(ApiErrorResponse.NotFound($"Connected System with ID {connectedSystemId} not found."));

        var rule = await _application.Repository.ConnectedSystems.GetObjectMatchingRuleAsync(ruleId);
        if (rule == null)
            return NotFound(ApiErrorResponse.NotFound($"Object Matching Rule with ID {ruleId} not found."));

        // Verify the rule belongs to this Connected System
        if (rule.ConnectedSystemObjectType?.ConnectedSystemId != connectedSystemId)
            return NotFound(ApiErrorResponse.NotFound($"Object Matching Rule with ID {ruleId} not found in Connected System {connectedSystemId}."));

        var apiKey = await GetCurrentApiKeyAsync();
        if (apiKey != null)
            await _application.ConnectedSystems.DeleteObjectMatchingRuleAsync(rule, apiKey);
        else
            await _application.ConnectedSystems.DeleteObjectMatchingRuleAsync(rule, initiatedBy);

        _logger.LogInformation("Deleted Object Matching Rule {RuleId} for Connected System {SystemId}", ruleId, connectedSystemId);

        return NoContent();
    }

    #endregion

    #region Synchronisation Rule Object Matching Rules (Advanced Mode)

    /// <summary>
    /// List Object Matching Rules for a Synchronisation Rule
    /// </summary>
    /// <param name="syncRuleId">The unique identifier of the Synchronisation Rule.</param>
    /// <returns>A list of Object Matching Rules.</returns>
    /// <response code="200">Returns the list of Object Matching Rules.</response>
    /// <response code="404">Synchronisation Rule not found.</response>
    [HttpGet("sync-rules/{syncRuleId:int}/matching-rules", Name = "GetSyncRuleObjectMatchingRules")]
    [ProducesResponseType(typeof(IEnumerable<ObjectMatchingRuleDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetSyncRuleObjectMatchingRulesAsync(int syncRuleId)
    {
        _logger.LogInformation("Getting Object Matching Rules for Synchronisation Rule {SyncRuleId}", syncRuleId);

        var syncRule = await _application.ConnectedSystems.GetSyncRuleAsync(syncRuleId);
        if (syncRule == null)
            return NotFound(ApiErrorResponse.NotFound($"Synchronisation Rule with ID {syncRuleId} not found."));

        var rules = syncRule.ObjectMatchingRules
            .OrderBy(r => r.Order)
            .Select(ObjectMatchingRuleDto.FromEntity)
            .ToList();

        return Ok(rules);
    }

    /// <summary>
    /// Get an Object Matching Rule for a Synchronisation Rule
    /// </summary>
    /// <param name="syncRuleId">The unique identifier of the Synchronisation Rule.</param>
    /// <param name="ruleId">The unique identifier of the Matching Rule.</param>
    /// <returns>The Object Matching Rule.</returns>
    /// <response code="200">Returns the Object Matching Rule.</response>
    /// <response code="404">Synchronisation Rule or Matching Rule not found.</response>
    [HttpGet("sync-rules/{syncRuleId:int}/matching-rules/{ruleId:int}", Name = "GetSyncRuleObjectMatchingRule")]
    [ProducesResponseType(typeof(ObjectMatchingRuleDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetSyncRuleObjectMatchingRuleAsync(int syncRuleId, int ruleId)
    {
        _logger.LogInformation("Getting Object Matching Rule {RuleId} for Synchronisation Rule {SyncRuleId}", ruleId, syncRuleId);

        var syncRule = await _application.ConnectedSystems.GetSyncRuleAsync(syncRuleId);
        if (syncRule == null)
            return NotFound(ApiErrorResponse.NotFound($"Synchronisation Rule with ID {syncRuleId} not found."));

        var rule = syncRule.ObjectMatchingRules.FirstOrDefault(r => r.Id == ruleId);
        if (rule == null)
            return NotFound(ApiErrorResponse.NotFound($"Object Matching Rule with ID {ruleId} not found on Synchronisation Rule {syncRuleId}."));

        return Ok(ObjectMatchingRuleDto.FromEntity(rule));
    }

    /// <summary>
    /// Create an Object Matching Rule on a Synchronisation Rule
    /// </summary>
    /// <param name="syncRuleId">The unique identifier of the Synchronisation Rule.</param>
    /// <param name="request">The rule creation request.</param>
    /// <returns>The created Object Matching Rule.</returns>
    /// <response code="201">Object Matching Rule created successfully.</response>
    /// <response code="400">Invalid request data.</response>
    /// <response code="404">Synchronisation Rule or referenced entities not found.</response>
    /// <response code="401">User could not be identified from authentication token.</response>
    [HttpPost("sync-rules/{syncRuleId:int}/matching-rules", Name = "CreateSyncRuleObjectMatchingRule")]
    [ProducesResponseType(typeof(ObjectMatchingRuleDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> CreateSyncRuleObjectMatchingRuleAsync(int syncRuleId, [FromBody] CreateSyncRuleObjectMatchingRuleRequest request)
    {
        _logger.LogInformation("Creating Object Matching Rule for Synchronisation Rule {SyncRuleId}", syncRuleId);

        var initiatedBy = await GetCurrentUserAsync();
        if (initiatedBy == null && !IsApiKeyAuthenticated())
        {
            _logger.LogWarning("Could not identify user from JWT claims for matching rule creation");
            return Unauthorized(ApiErrorResponse.Unauthorised("Could not identify user from authentication token."));
        }

        var syncRule = await _application.ConnectedSystems.GetSyncRuleAsync(syncRuleId);
        if (syncRule == null)
            return NotFound(ApiErrorResponse.NotFound($"Synchronisation Rule with ID {syncRuleId} not found."));

        // Validate target MV attribute exists
        var mvAttributes = await _application.Metaverse.GetMetaverseAttributesAsync();
        var targetMvAttr = mvAttributes?.FirstOrDefault(a => a.Id == request.TargetMetaverseAttributeId);
        if (targetMvAttr == null)
            return NotFound(ApiErrorResponse.NotFound($"Metaverse attribute with ID {request.TargetMetaverseAttributeId} not found."));

        // Calculate order if not specified
        var order = request.Order ?? (syncRule.ObjectMatchingRules.Count > 0
            ? syncRule.ObjectMatchingRules.Max(r => r.Order) + 1
            : 0);

        var rule = new ObjectMatchingRule
        {
            Order = order,
            SyncRuleId = syncRule.Id,
            SyncRule = syncRule,
            TargetMetaverseAttributeId = targetMvAttr.Id,
            TargetMetaverseAttribute = targetMvAttr,
            CaseSensitive = request.CaseSensitive
        };

        // Add sources - for advanced mode, sources reference CS attributes from the Synchronisation Rule's object type
        var objectType = syncRule.ConnectedSystemObjectType;
        foreach (var sourceRequest in request.Sources)
        {
            var source = new ObjectMatchingRuleSource
            {
                Order = sourceRequest.Order
            };

            if (sourceRequest.ConnectedSystemAttributeId.HasValue)
            {
                var csAttr = objectType?.Attributes?.FirstOrDefault(a => a.Id == sourceRequest.ConnectedSystemAttributeId.Value);
                if (csAttr == null)
                    return NotFound(ApiErrorResponse.NotFound($"Connected System attribute with ID {sourceRequest.ConnectedSystemAttributeId} not found in object type."));
                source.ConnectedSystemAttributeId = csAttr.Id;
                source.ConnectedSystemAttribute = csAttr;
            }
            else if (sourceRequest.MetaverseAttributeId.HasValue)
            {
                var mvAttr = mvAttributes?.FirstOrDefault(a => a.Id == sourceRequest.MetaverseAttributeId.Value);
                if (mvAttr == null)
                    return NotFound(ApiErrorResponse.NotFound($"Metaverse attribute with ID {sourceRequest.MetaverseAttributeId} not found."));
                source.MetaverseAttributeId = mvAttr.Id;
                source.MetaverseAttribute = mvAttr;
            }
            else
            {
                return BadRequest(ApiErrorResponse.BadRequest("Each source must specify either ConnectedSystemAttributeId or MetaverseAttributeId."));
            }

            rule.Sources.Add(source);
        }

        try
        {
            var apiKey = await GetCurrentApiKeyAsync();
            if (apiKey != null)
                await _application.ConnectedSystems.CreateObjectMatchingRuleAsync(rule, apiKey);
            else
                await _application.ConnectedSystems.CreateObjectMatchingRuleAsync(rule, initiatedBy);

            _logger.LogInformation("Created Object Matching Rule {RuleId} for Synchronisation Rule {SyncRuleId}", rule.Id, syncRuleId);

            return CreatedAtRoute("GetSyncRuleObjectMatchingRule",
                new { syncRuleId, ruleId = rule.Id },
                ObjectMatchingRuleDto.FromEntity(rule));
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning(ex, "Failed to create Object Matching Rule: {Message}", ex.Message);
            return BadRequest(ApiErrorResponse.BadRequest(ex.Message));
        }
    }

    /// <summary>
    /// Update an Object Matching Rule on a Synchronisation Rule
    /// </summary>
    /// <param name="syncRuleId">The unique identifier of the Synchronisation Rule.</param>
    /// <param name="ruleId">The unique identifier of the Matching Rule.</param>
    /// <param name="request">The update request.</param>
    /// <returns>The updated Object Matching Rule.</returns>
    /// <response code="200">Object Matching Rule updated successfully.</response>
    /// <response code="400">Invalid request data.</response>
    /// <response code="404">Synchronisation Rule or Matching Rule not found.</response>
    /// <response code="401">User could not be identified from authentication token.</response>
    [HttpPut("sync-rules/{syncRuleId:int}/matching-rules/{ruleId:int}", Name = "UpdateSyncRuleObjectMatchingRule")]
    [ProducesResponseType(typeof(ObjectMatchingRuleDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> UpdateSyncRuleObjectMatchingRuleAsync(int syncRuleId, int ruleId, [FromBody] UpdateObjectMatchingRuleRequest request)
    {
        _logger.LogInformation("Updating Object Matching Rule {RuleId} for Synchronisation Rule {SyncRuleId}", ruleId, syncRuleId);

        var initiatedBy = await GetCurrentUserAsync();
        if (initiatedBy == null && !IsApiKeyAuthenticated())
        {
            _logger.LogWarning("Could not identify user from JWT claims for matching rule update");
            return Unauthorized(ApiErrorResponse.Unauthorised("Could not identify user from authentication token."));
        }

        var syncRule = await _application.ConnectedSystems.GetSyncRuleAsync(syncRuleId);
        if (syncRule == null)
            return NotFound(ApiErrorResponse.NotFound($"Synchronisation Rule with ID {syncRuleId} not found."));

        var rule = await _application.Repository.ConnectedSystems.GetObjectMatchingRuleAsync(ruleId);
        if (rule == null)
            return NotFound(ApiErrorResponse.NotFound($"Object Matching Rule with ID {ruleId} not found."));

        // Verify the rule belongs to this Synchronisation Rule
        if (rule.SyncRuleId != syncRuleId)
            return NotFound(ApiErrorResponse.NotFound($"Object Matching Rule with ID {ruleId} not found on Synchronisation Rule {syncRuleId}."));

        // Update order if specified
        if (request.Order.HasValue)
            rule.Order = request.Order.Value;

        // Update target MV attribute if specified
        if (request.TargetMetaverseAttributeId.HasValue)
        {
            var mvAttributes = await _application.Metaverse.GetMetaverseAttributesAsync();
            var targetMvAttr = mvAttributes?.FirstOrDefault(a => a.Id == request.TargetMetaverseAttributeId.Value);
            if (targetMvAttr == null)
                return NotFound(ApiErrorResponse.NotFound($"Metaverse attribute with ID {request.TargetMetaverseAttributeId} not found."));

            rule.TargetMetaverseAttributeId = targetMvAttr.Id;
            rule.TargetMetaverseAttribute = targetMvAttr;
        }

        // Update sources if specified
        if (request.Sources != null)
        {
            var objectType = syncRule.ConnectedSystemObjectType;
            var mvAttributes = await _application.Metaverse.GetMetaverseAttributesAsync();

            rule.Sources.Clear();

            foreach (var sourceRequest in request.Sources)
            {
                var source = new ObjectMatchingRuleSource
                {
                    Order = sourceRequest.Order,
                    ObjectMatchingRuleId = rule.Id
                };

                if (sourceRequest.ConnectedSystemAttributeId.HasValue)
                {
                    var csAttr = objectType?.Attributes?.FirstOrDefault(a => a.Id == sourceRequest.ConnectedSystemAttributeId.Value);
                    if (csAttr == null)
                        return NotFound(ApiErrorResponse.NotFound($"Connected System attribute with ID {sourceRequest.ConnectedSystemAttributeId} not found in object type."));
                    source.ConnectedSystemAttributeId = csAttr.Id;
                    source.ConnectedSystemAttribute = csAttr;
                }
                else if (sourceRequest.MetaverseAttributeId.HasValue)
                {
                    var mvAttr = mvAttributes?.FirstOrDefault(a => a.Id == sourceRequest.MetaverseAttributeId.Value);
                    if (mvAttr == null)
                        return NotFound(ApiErrorResponse.NotFound($"Metaverse attribute with ID {sourceRequest.MetaverseAttributeId} not found."));
                    source.MetaverseAttributeId = mvAttr.Id;
                    source.MetaverseAttribute = mvAttr;
                }
                else
                {
                    return BadRequest(ApiErrorResponse.BadRequest("Each source must specify either ConnectedSystemAttributeId or MetaverseAttributeId."));
                }

                rule.Sources.Add(source);
            }
        }

        // Update case sensitivity if specified
        if (request.CaseSensitive.HasValue)
            rule.CaseSensitive = request.CaseSensitive.Value;

        try
        {
            var apiKey = await GetCurrentApiKeyAsync();
            if (apiKey != null)
                await _application.ConnectedSystems.UpdateObjectMatchingRuleAsync(rule, apiKey);
            else
                await _application.ConnectedSystems.UpdateObjectMatchingRuleAsync(rule, initiatedBy);

            _logger.LogInformation("Updated Object Matching Rule {RuleId} for Synchronisation Rule {SyncRuleId}", ruleId, syncRuleId);

            return Ok(ObjectMatchingRuleDto.FromEntity(rule));
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning(ex, "Failed to update Object Matching Rule: {Message}", ex.Message);
            return BadRequest(ApiErrorResponse.BadRequest(ex.Message));
        }
    }

    /// <summary>
    /// Delete an Object Matching Rule from a Synchronisation Rule
    /// </summary>
    /// <param name="syncRuleId">The unique identifier of the Synchronisation Rule.</param>
    /// <param name="ruleId">The unique identifier of the Matching Rule.</param>
    /// <returns>No content on success.</returns>
    /// <response code="204">Object Matching Rule deleted successfully.</response>
    /// <response code="404">Synchronisation Rule or Matching Rule not found.</response>
    /// <response code="401">User could not be identified from authentication token.</response>
    [HttpDelete("sync-rules/{syncRuleId:int}/matching-rules/{ruleId:int}", Name = "DeleteSyncRuleObjectMatchingRule")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> DeleteSyncRuleObjectMatchingRuleAsync(int syncRuleId, int ruleId)
    {
        _logger.LogInformation("Deleting Object Matching Rule {RuleId} from Synchronisation Rule {SyncRuleId}", ruleId, syncRuleId);

        var initiatedBy = await GetCurrentUserAsync();
        if (initiatedBy == null && !IsApiKeyAuthenticated())
        {
            _logger.LogWarning("Could not identify user from JWT claims for matching rule deletion");
            return Unauthorized(ApiErrorResponse.Unauthorised("Could not identify user from authentication token."));
        }

        var syncRule = await _application.ConnectedSystems.GetSyncRuleAsync(syncRuleId);
        if (syncRule == null)
            return NotFound(ApiErrorResponse.NotFound($"Synchronisation Rule with ID {syncRuleId} not found."));

        var rule = await _application.Repository.ConnectedSystems.GetObjectMatchingRuleAsync(ruleId);
        if (rule == null)
            return NotFound(ApiErrorResponse.NotFound($"Object Matching Rule with ID {ruleId} not found."));

        // Verify the rule belongs to this Synchronisation Rule
        if (rule.SyncRuleId != syncRuleId)
            return NotFound(ApiErrorResponse.NotFound($"Object Matching Rule with ID {ruleId} not found on Synchronisation Rule {syncRuleId}."));

        var apiKey = await GetCurrentApiKeyAsync();
        if (apiKey != null)
            await _application.ConnectedSystems.DeleteObjectMatchingRuleAsync(rule, apiKey);
        else
            await _application.ConnectedSystems.DeleteObjectMatchingRuleAsync(rule, initiatedBy);

        _logger.LogInformation("Deleted Object Matching Rule {RuleId} from Synchronisation Rule {SyncRuleId}", ruleId, syncRuleId);

        return NoContent();
    }

    #endregion

    #region Object Matching Mode Switching

    /// <summary>
    /// Switch the Object Matching mode for a Connected System
    /// </summary>
    /// <remarks>
    /// When switching to advanced mode, Matching Rules are copied from Object Types to Synchronisation Rules. When switching to simple mode, Matching Rules are migrated from Synchronisation Rules to Object Types.
    /// </remarks>
    /// <param name="connectedSystemId">The unique identifier of the Connected System.</param>
    /// <param name="request">The mode switch request.</param>
    /// <returns>The result of the mode switch operation.</returns>
    /// <response code="200">Mode switched successfully.</response>
    /// <response code="400">Mode switch failed.</response>
    /// <response code="404">Connected System not found.</response>
    /// <response code="401">User could not be identified from authentication token.</response>
    [HttpPost("connected-systems/{connectedSystemId:int}/matching-mode", Name = "SwitchObjectMatchingMode")]
    [ProducesResponseType(typeof(ObjectMatchingModeSwitchResult), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> SwitchObjectMatchingModeAsync(int connectedSystemId, [FromBody] SwitchObjectMatchingModeRequest request)
    {
        _logger.LogInformation("Switching object matching mode for Connected System {SystemId} to {Mode}", connectedSystemId, request.Mode);

        var initiatedBy = await GetCurrentUserAsync();
        if (initiatedBy == null && !IsApiKeyAuthenticated())
        {
            _logger.LogWarning("Could not identify user from JWT claims for matching mode switch");
            return Unauthorized(ApiErrorResponse.Unauthorised("Could not identify user from authentication token."));
        }

        // Get the Connected System with change tracking since matching mode switch modifies and saves it
        var connectedSystem = await _application.ConnectedSystems.GetConnectedSystemAsync(connectedSystemId, withChangeTracking: true);
        if (connectedSystem == null)
            return NotFound(ApiErrorResponse.NotFound($"Connected System with ID {connectedSystemId} not found."));

        var result = await _application.ConnectedSystems.SwitchObjectMatchingModeAsync(connectedSystem, request.Mode, initiatedBy);

        if (!result.Success)
        {
            _logger.LogWarning("Failed to switch matching mode for Connected System {SystemId}: {Error}", connectedSystemId, LogSanitiser.Sanitise(result.ErrorMessage));
            return BadRequest(ApiErrorResponse.BadRequest(result.ErrorMessage ?? "Failed to switch object matching mode."));
        }

        _logger.LogInformation("Switched object matching mode for Connected System {SystemId} to {Mode}", connectedSystemId, result.NewMode);

        return Ok(result);
    }

    #endregion

    #region Expression Testing

    /// <summary>
    /// Test an expression with sample Attribute data
    /// </summary>
    /// <param name="request">The test expression request containing the expression and sample Attribute Values.</param>
    /// <returns>The result of evaluating the expression.</returns>
    /// <response code="200">Expression evaluated successfully.</response>
    /// <response code="400">Invalid expression or test data.</response>
    /// <response code="401">Authentication required.</response>
    [HttpPost("test-expression", Name = "TestExpression")]
    [ProducesResponseType(typeof(TestExpressionResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public IActionResult TestExpression([FromBody] TestExpressionRequest request)
    {
        _logger.LogDebug("Testing expression: {Expression}", LogSanitiser.Sanitise(request.Expression));

        if (string.IsNullOrWhiteSpace(request.Expression))
            return BadRequest(ApiErrorResponse.BadRequest("Expression is required."));

        // First validate the expression syntax
        var validationResult = _expressionEvaluator.Validate(request.Expression);
        if (!validationResult.IsValid)
        {
            return Ok(new TestExpressionResponse
            {
                IsValid = false,
                ErrorMessage = validationResult.ErrorMessage,
                ErrorPosition = validationResult.ErrorPosition
            });
        }

        // Build the context from the provided attribute values
        var mvAttributes = request.MvAttributes ?? new Dictionary<string, object?>();
        var csAttributes = request.CsAttributes ?? new Dictionary<string, object?>();
        var context = new ExpressionContext(mvAttributes, csAttributes);

        // Evaluate the expression
        var testResult = _expressionEvaluator.Test(request.Expression, context);

        return Ok(new TestExpressionResponse
        {
            IsValid = testResult.IsValid,
            Result = testResult.Result,
            ResultType = testResult.ResultType,
            ErrorMessage = testResult.ErrorMessage
        });
    }

    #endregion

    #region Private Helpers

    /// <summary>
    /// Checks if the current authentication is via API key.
    /// </summary>
    private bool IsApiKeyAuthenticated()
    {
        return User.HasClaim("auth_method", "api_key");
    }

    /// <summary>
    /// Gets the API key name if authenticated via API key.
    /// </summary>
    private string? GetApiKeyName()
    {
        if (!IsApiKeyAuthenticated())
            return null;

        return User.Identity?.Name;
    }

    /// <summary>
    /// Gets the current API key entity if authenticated via API key.
    /// </summary>
    private async Task<JIM.Models.Security.ApiKey?> GetCurrentApiKeyAsync()
    {
        if (!IsApiKeyAuthenticated())
            return null;

        // The API key ID is stored in the NameIdentifier claim
        var apiKeyIdClaim = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrEmpty(apiKeyIdClaim) || !Guid.TryParse(apiKeyIdClaim, out var apiKeyId))
            return null;

        return await _application.Repository.ApiKeys.GetByIdAsync(apiKeyId);
    }

    /// <summary>
    /// Resolves the current user from JWT claims by looking up their SSO identifier in the Metaverse.
    /// Returns null for API key authentication (which is valid - use IsApiKeyAuthenticated() to check).
    /// </summary>
    private async Task<JIM.Models.Core.MetaverseObject?> GetCurrentUserAsync()
    {
        if (User.Identity?.IsAuthenticated != true)
            return null;

        // API key authentication doesn't map to a Metaverse user object
        // This is valid - the caller should check IsApiKeyAuthenticated() separately
        if (IsApiKeyAuthenticated())
        {
            _logger.LogDebug("API key authentication detected - no Metaverse user lookup needed");
            return null;
        }

        // Get the service settings to know which claim type contains the unique identifier
        var serviceSettings = await _application.ServiceSettings.GetServiceSettingsAsync();
        if (serviceSettings?.SSOUniqueIdentifierClaimType == null ||
            serviceSettings.SSOUniqueIdentifierMetaverseAttribute == null)
        {
            _logger.LogError("Service settings are not configured for SSO claim mapping");
            return null;
        }

        // Get the unique identifier from the JWT claims
        var uniqueIdClaimValue = IdentityUtilities.GetSsoUniqueIdentifier(
            User,
            serviceSettings.SSOUniqueIdentifierClaimType);

        if (string.IsNullOrEmpty(uniqueIdClaimValue))
        {
            _logger.LogWarning("JWT does not contain the expected claim: {ClaimType}",
                serviceSettings.SSOUniqueIdentifierClaimType);
            return null;
        }

        // Look up the user in the Metaverse
        var userType = await _application.Metaverse.GetMetaverseObjectTypeAsync(
            JIM.Models.Core.Constants.BuiltInObjectTypes.User,
            false);

        if (userType == null)
        {
            _logger.LogError("Could not find User object type in Metaverse");
            return null;
        }

        return await _application.Metaverse.GetMetaverseObjectByTypeAndAttributeAsync(
            userType,
            serviceSettings.SSOUniqueIdentifierMetaverseAttribute,
            uniqueIdClaimValue);
    }

    /// <summary>
    /// Checks if a Container belongs to a Connected System, traversing the parent Container chain if necessary.
    /// </summary>
    /// <param name="container">The Container to check.</param>
    /// <param name="connectedSystemId">The Connected System ID to check against.</param>
    /// <returns>True if the Container belongs to the Connected System.</returns>
    private static bool ContainerBelongsToConnectedSystem(ConnectedSystemContainer container, int connectedSystemId)
    {
        // Check if directly connected to the system
        if (container.ConnectedSystem?.Id == connectedSystemId)
            return true;

        // Check if connected via partition
        if (container.Partition?.ConnectedSystem?.Id == connectedSystemId)
            return true;

        // For nested containers, walk up the parent chain
        var current = container.ParentContainer;
        while (current != null)
        {
            if (current.ConnectedSystem?.Id == connectedSystemId)
                return true;
            if (current.Partition?.ConnectedSystem?.Id == connectedSystemId)
                return true;
            current = current.ParentContainer;
        }

        return false;
    }

    #endregion
}
