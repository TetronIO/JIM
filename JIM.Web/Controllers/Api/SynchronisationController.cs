using Asp.Versioning;
using JIM.Web.Extensions.Api;
using JIM.Web.Models.Api;
using JIM.Application;
using JIM.Application.Expressions;
using JIM.Application.Services;
using JIM.Models.Logic;
using JIM.Models.Logic.DTOs;
using JIM.Models.Search;
using JIM.Models.Staging;
using JIM.Models.Staging.DTOs;
using JIM.Models.Tasking;
using JIM.Utilities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace JIM.Web.Controllers.Api;

/// <summary>
/// API controller for managing synchronisation configuration including Connected Systems and Sync Rules.
/// </summary>
/// <remarks>
/// This controller provides endpoints for managing the synchronisation infrastructure:
/// - Connected Systems: External identity stores that sync with the Metaverse
/// - Sync Rules: Configuration for how data flows between Connected Systems and the Metaverse
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
    /// Gets all connected systems with optional pagination, sorting, and filtering.
    /// </summary>
    /// <param name="pagination">Pagination parameters (page, pageSize, sortBy, sortDirection, filter).</param>
    /// <returns>A paginated list of connected system headers.</returns>
    [HttpGet("connected-systems", Name = "GetConnectedSystems")]
    [ProducesResponseType(typeof(PaginatedResponse<ConnectedSystemHeader>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetConnectedSystemsAsync([FromQuery] PaginationRequest pagination)
    {
        _logger.LogTrace("Requested connected systems (Page: {Page}, PageSize: {PageSize})", pagination.Page, pagination.PageSize);
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
    /// Gets a specific connected system by ID.
    /// </summary>
    /// <param name="connectedSystemId">The unique identifier of the connected system.</param>
    /// <returns>The connected system details including configuration and schema.</returns>
    [HttpGet("connected-systems/{connectedSystemId:int}", Name = "GetConnectedSystem")]
    [ProducesResponseType(typeof(ConnectedSystemDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetConnectedSystemAsync(int connectedSystemId)
    {
        _logger.LogTrace("Requested connected system: {Id}", connectedSystemId);
        var system = await _application.ConnectedSystems.GetConnectedSystemAsync(connectedSystemId);
        if (system == null)
            return NotFound(ApiErrorResponse.NotFound($"Connected system with ID {connectedSystemId} not found."));

        return Ok(ConnectedSystemDetailDto.FromEntity(system));
    }

    /// <summary>
    /// Gets all object types defined in a connected system's schema.
    /// </summary>
    /// <param name="connectedSystemId">The unique identifier of the connected system.</param>
    /// <returns>A list of object types with their attributes.</returns>
    [HttpGet("connected-systems/{connectedSystemId:int}/object-types", Name = "GetConnectedSystemObjectTypes")]
    [ProducesResponseType(typeof(IEnumerable<ConnectedSystemObjectTypeDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetConnectedSystemObjectTypesAsync(int connectedSystemId)
    {
        _logger.LogTrace("Requested object types for connected system: {Id}", connectedSystemId);
        var objectTypes = await _application.ConnectedSystems.GetObjectTypesAsync(connectedSystemId);
        if (objectTypes == null)
            return NotFound(ApiErrorResponse.NotFound($"Connected system with ID {connectedSystemId} not found."));

        var dtos = objectTypes.Select(ConnectedSystemObjectTypeDto.FromEntity);
        return Ok(dtos);
    }

    /// <summary>
    /// Updates a Connected System Object Type.
    /// </summary>
    /// <remarks>
    /// Use this endpoint to update properties of an object type, such as:
    /// - Selected: Whether the object type is managed by JIM
    /// - RemoveContributedAttributesOnObsoletion: Whether MVO attributes are removed when CSO is obsoleted
    /// </remarks>
    /// <param name="connectedSystemId">The unique identifier of the connected system.</param>
    /// <param name="objectTypeId">The unique identifier of the object type.</param>
    /// <param name="request">The update request with new values.</param>
    /// <returns>The updated object type details.</returns>
    /// <response code="200">Object type updated successfully.</response>
    /// <response code="400">Invalid request or validation failed.</response>
    /// <response code="404">Connected system or object type not found.</response>
    /// <response code="401">User could not be identified from authentication token.</response>
    [HttpPut("connected-systems/{connectedSystemId:int}/object-types/{objectTypeId:int}", Name = "UpdateConnectedSystemObjectType")]
    [ProducesResponseType(typeof(ConnectedSystemObjectTypeDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> UpdateConnectedSystemObjectTypeAsync(int connectedSystemId, int objectTypeId, [FromBody] UpdateConnectedSystemObjectTypeRequest request)
    {
        _logger.LogInformation("Updating object type {ObjectTypeId} for connected system {SystemId}", objectTypeId, connectedSystemId);

        var initiatedBy = await GetCurrentUserAsync();
        if (initiatedBy == null && !IsApiKeyAuthenticated())
        {
            _logger.LogWarning("Could not identify user from JWT claims for object type update");
            return Unauthorized(ApiErrorResponse.Unauthorised("Could not identify user from authentication token."));
        }

        // Verify connected system exists
        var connectedSystem = await _application.ConnectedSystems.GetConnectedSystemAsync(connectedSystemId);
        if (connectedSystem == null)
            return NotFound(ApiErrorResponse.NotFound($"Connected system with ID {connectedSystemId} not found."));

        // Get the object type
        var objectType = await _application.ConnectedSystems.GetObjectTypeAsync(objectTypeId);
        if (objectType == null || objectType.ConnectedSystemId != connectedSystemId)
            return NotFound(ApiErrorResponse.NotFound($"Object type with ID {objectTypeId} not found in connected system {connectedSystemId}."));

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
    /// Updates a Connected System Attribute.
    /// </summary>
    /// <remarks>
    /// Use this endpoint to update properties of an attribute, such as:
    /// - Selected: Whether the attribute is managed by JIM
    /// - IsExternalId: Whether this is the unique identifier for objects
    /// - IsSecondaryExternalId: Whether this is a secondary identifier (e.g., DN for LDAP)
    /// </remarks>
    /// <param name="connectedSystemId">The unique identifier of the connected system.</param>
    /// <param name="objectTypeId">The unique identifier of the object type.</param>
    /// <param name="attributeId">The unique identifier of the attribute.</param>
    /// <param name="request">The update request with new values.</param>
    /// <returns>The updated attribute details.</returns>
    /// <response code="200">Attribute updated successfully.</response>
    /// <response code="400">Invalid request or validation failed.</response>
    /// <response code="404">Connected system, object type, or attribute not found.</response>
    /// <response code="401">User could not be identified from authentication token.</response>
    [HttpPut("connected-systems/{connectedSystemId:int}/object-types/{objectTypeId:int}/attributes/{attributeId:int}", Name = "UpdateConnectedSystemAttribute")]
    [ProducesResponseType(typeof(ConnectedSystemAttributeDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> UpdateConnectedSystemAttributeAsync(int connectedSystemId, int objectTypeId, int attributeId, [FromBody] UpdateConnectedSystemAttributeRequest request)
    {
        _logger.LogInformation("Updating attribute {AttributeId} for object type {ObjectTypeId} in connected system {SystemId}", attributeId, objectTypeId, connectedSystemId);

        var initiatedBy = await GetCurrentUserAsync();
        if (initiatedBy == null && !IsApiKeyAuthenticated())
        {
            _logger.LogWarning("Could not identify user from JWT claims for attribute update");
            return Unauthorized(ApiErrorResponse.Unauthorised("Could not identify user from authentication token."));
        }

        // Verify connected system exists
        var connectedSystem = await _application.ConnectedSystems.GetConnectedSystemAsync(connectedSystemId);
        if (connectedSystem == null)
            return NotFound(ApiErrorResponse.NotFound($"Connected system with ID {connectedSystemId} not found."));

        // Get the attribute
        var attribute = await _application.ConnectedSystems.GetAttributeAsync(attributeId);
        if (attribute == null)
            return NotFound(ApiErrorResponse.NotFound($"Attribute with ID {attributeId} not found."));

        // Verify attribute belongs to the specified object type and connected system
        if (attribute.ConnectedSystemObjectType.Id != objectTypeId ||
            attribute.ConnectedSystemObjectType.ConnectedSystemId != connectedSystemId)
        {
            return NotFound(ApiErrorResponse.NotFound($"Attribute with ID {attributeId} not found in object type {objectTypeId} of connected system {connectedSystemId}."));
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
    /// Bulk updates multiple Connected System Attributes in a single operation.
    /// This creates a single Activity record for the entire batch operation, rather than
    /// individual activities for each attribute update.
    /// </summary>
    /// <param name="connectedSystemId">The unique identifier of the connected system.</param>
    /// <param name="objectTypeId">The unique identifier of the object type containing the attributes.</param>
    /// <param name="request">Dictionary of attribute updates keyed by attribute ID.</param>
    /// <returns>Response containing the activity ID, updated count, updated attributes, and any errors.</returns>
    /// <response code="200">Attributes updated successfully (may include partial success with errors).</response>
    /// <response code="400">Invalid request or empty attributes dictionary.</response>
    /// <response code="404">Connected system or object type not found.</response>
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
        _logger.LogInformation("Bulk updating {Count} attributes for object type {ObjectTypeId} in connected system {SystemId}",
            request.Attributes?.Count ?? 0, objectTypeId, connectedSystemId);

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

        // Verify connected system exists
        var connectedSystem = await _application.ConnectedSystems.GetConnectedSystemAsync(connectedSystemId);
        if (connectedSystem == null)
            return NotFound(ApiErrorResponse.NotFound($"Connected system with ID {connectedSystemId} not found."));

        // Get the object type with attributes
        var objectType = await _application.ConnectedSystems.GetObjectTypeAsync(objectTypeId);
        if (objectType == null || objectType.ConnectedSystemId != connectedSystemId)
            return NotFound(ApiErrorResponse.NotFound($"Object type with ID {objectTypeId} not found in connected system {connectedSystemId}."));

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
    /// Gets a specific connected system object by ID.
    /// </summary>
    /// <param name="connectedSystemId">The unique identifier of the connected system.</param>
    /// <param name="id">The unique identifier (GUID) of the connected system object.</param>
    /// <returns>The connected system object details including all attribute values.</returns>
    [HttpGet("connected-systems/{connectedSystemId:int}/objects/{id:guid}", Name = "GetConnectedSystemObject")]
    [ProducesResponseType(typeof(ConnectedSystemObjectDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetConnectedSystemObjectAsync(int connectedSystemId, Guid id)
    {
        _logger.LogTrace("Requested object {ObjectId} for connected system: {SystemId}", id, connectedSystemId);
        var obj = await _application.ConnectedSystems.GetConnectedSystemObjectAsync(connectedSystemId, id);
        if (obj == null)
            return NotFound(ApiErrorResponse.NotFound($"Object with ID {id} not found in connected system {connectedSystemId}."));

        return Ok(ConnectedSystemObjectDetailDto.FromEntity(obj));
    }

    /// <summary>
    /// Gets a preview of what will be affected by deleting a Connected System.
    /// </summary>
    /// <remarks>
    /// Call this before DeleteConnectedSystemAsync to inform the user of the impact.
    /// The preview includes counts of:
    /// - Connected System Objects that will be deleted
    /// - Sync Rules that will be removed
    /// - Metaverse Objects that will be disconnected
    /// - Pending exports that will be cancelled
    /// </remarks>
    /// <param name="connectedSystemId">The unique identifier of the connected system.</param>
    /// <returns>A preview showing counts of affected objects and any warnings.</returns>
    [HttpGet("connected-systems/{connectedSystemId:int}/deletion-preview", Name = "GetConnectedSystemDeletionPreview")]
    [ProducesResponseType(typeof(ConnectedSystemDeletionPreview), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetConnectedSystemDeletionPreviewAsync(int connectedSystemId)
    {
        _logger.LogInformation("Deletion preview requested for connected system: {Id}", connectedSystemId);

        var preview = await _application.ConnectedSystems.GetDeletionPreviewAsync(connectedSystemId);
        if (preview == null)
            return NotFound(ApiErrorResponse.NotFound($"Connected system with ID {connectedSystemId} not found."));

        return Ok(preview);
    }

    #region Partitions and Containers
    /// <summary>
    /// Gets all partitions for a Connected System.
    /// </summary>
    /// <remarks>
    /// Partitions represent logical divisions within a connected system (e.g., LDAP naming contexts).
    /// Each partition contains containers that can be selected for import operations.
    /// </remarks>
    /// <param name="connectedSystemId">The unique identifier of the connected system.</param>
    /// <returns>A list of partitions with their containers.</returns>
    [HttpGet("connected-systems/{connectedSystemId:int}/partitions", Name = "GetConnectedSystemPartitions")]
    [ProducesResponseType(typeof(IEnumerable<ConnectedSystemPartitionDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetConnectedSystemPartitionsAsync(int connectedSystemId)
    {
        _logger.LogTrace("Requested partitions for connected system: {Id}", connectedSystemId);

        var connectedSystem = await _application.ConnectedSystems.GetConnectedSystemAsync(connectedSystemId);
        if (connectedSystem == null)
            return NotFound(ApiErrorResponse.NotFound($"Connected system with ID {connectedSystemId} not found."));

        var partitions = await _application.ConnectedSystems.GetConnectedSystemPartitionsAsync(connectedSystem);
        var dtos = partitions.Select(ConnectedSystemPartitionDto.FromEntity);
        return Ok(dtos);
    }

    /// <summary>
    /// Updates a Connected System Partition.
    /// </summary>
    /// <remarks>
    /// Use this endpoint to select or deselect a partition for import operations.
    /// When a partition is selected, objects within it (and its selected containers) will be imported during sync.
    /// </remarks>
    /// <param name="connectedSystemId">The unique identifier of the connected system.</param>
    /// <param name="partitionId">The unique identifier of the partition.</param>
    /// <param name="request">The update request with new values.</param>
    /// <returns>The updated partition details.</returns>
    /// <response code="200">Partition updated successfully.</response>
    /// <response code="404">Connected system or partition not found.</response>
    /// <response code="401">User could not be identified from authentication token.</response>
    [HttpPut("connected-systems/{connectedSystemId:int}/partitions/{partitionId:int}", Name = "UpdateConnectedSystemPartition")]
    [ProducesResponseType(typeof(ConnectedSystemPartitionDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> UpdateConnectedSystemPartitionAsync(int connectedSystemId, int partitionId, [FromBody] UpdateConnectedSystemPartitionRequest request)
    {
        _logger.LogInformation("Updating partition {PartitionId} for connected system {SystemId}", partitionId, connectedSystemId);

        var initiatedBy = await GetCurrentUserAsync();
        if (initiatedBy == null && !IsApiKeyAuthenticated())
        {
            _logger.LogWarning("Could not identify user from JWT claims for partition update");
            return Unauthorized(ApiErrorResponse.Unauthorised("Could not identify user from authentication token."));
        }

        // Verify connected system exists
        var connectedSystem = await _application.ConnectedSystems.GetConnectedSystemAsync(connectedSystemId);
        if (connectedSystem == null)
            return NotFound(ApiErrorResponse.NotFound($"Connected system with ID {connectedSystemId} not found."));

        // Get the partition
        var partition = await _application.ConnectedSystems.GetConnectedSystemPartitionAsync(partitionId);
        if (partition == null || partition.ConnectedSystem?.Id != connectedSystemId)
            return NotFound(ApiErrorResponse.NotFound($"Partition with ID {partitionId} not found in connected system {connectedSystemId}."));

        // Apply updates
        if (request.Selected.HasValue)
            partition.Selected = request.Selected.Value;

        await _application.ConnectedSystems.UpdateConnectedSystemPartitionAsync(partition);

        // Reload to get full entity with relationships
        var updated = await _application.ConnectedSystems.GetConnectedSystemPartitionAsync(partitionId);
        return Ok(ConnectedSystemPartitionDto.FromEntity(updated!));
    }

    /// <summary>
    /// Updates a Connected System Container.
    /// </summary>
    /// <remarks>
    /// Use this endpoint to select or deselect a container for import operations.
    /// When a container is selected, objects within it will be imported during sync.
    /// The parent partition must also be selected for the container selection to take effect.
    /// </remarks>
    /// <param name="connectedSystemId">The unique identifier of the connected system.</param>
    /// <param name="containerId">The unique identifier of the container.</param>
    /// <param name="request">The update request with new values.</param>
    /// <returns>The updated container details.</returns>
    /// <response code="200">Container updated successfully.</response>
    /// <response code="404">Connected system or container not found.</response>
    /// <response code="401">User could not be identified from authentication token.</response>
    [HttpPut("connected-systems/{connectedSystemId:int}/containers/{containerId:int}", Name = "UpdateConnectedSystemContainer")]
    [ProducesResponseType(typeof(ConnectedSystemContainerDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> UpdateConnectedSystemContainerAsync(int connectedSystemId, int containerId, [FromBody] UpdateConnectedSystemContainerRequest request)
    {
        _logger.LogInformation("Updating container {ContainerId} for connected system {SystemId}", containerId, connectedSystemId);

        var initiatedBy = await GetCurrentUserAsync();
        if (initiatedBy == null && !IsApiKeyAuthenticated())
        {
            _logger.LogWarning("Could not identify user from JWT claims for container update");
            return Unauthorized(ApiErrorResponse.Unauthorised("Could not identify user from authentication token."));
        }

        // Verify connected system exists
        var connectedSystem = await _application.ConnectedSystems.GetConnectedSystemAsync(connectedSystemId);
        if (connectedSystem == null)
            return NotFound(ApiErrorResponse.NotFound($"Connected system with ID {connectedSystemId} not found."));

        // Get the container
        var container = await _application.ConnectedSystems.GetConnectedSystemContainerAsync(containerId);
        if (container == null)
            return NotFound(ApiErrorResponse.NotFound($"Container with ID {containerId} not found."));

        // Verify container belongs to the connected system (via partition, directly, or through parent container chain)
        var belongsToSystem = ContainerBelongsToConnectedSystem(container, connectedSystemId);
        if (!belongsToSystem)
            return NotFound(ApiErrorResponse.NotFound($"Container with ID {containerId} not found in connected system {connectedSystemId}."));

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
    /// Creates a new Connected System.
    /// </summary>
    /// <remarks>
    /// Creates a new Connected System with the specified connector type. The connector's default settings
    /// will be applied automatically. Use the Update endpoint to configure the settings after creation.
    /// </remarks>
    /// <param name="request">The connected system creation request.</param>
    /// <returns>The created connected system details.</returns>
    /// <response code="201">Connected system created successfully.</response>
    /// <response code="400">Invalid request or connector definition not found.</response>
    /// <response code="401">User could not be identified from authentication token.</response>
    [HttpPost("connected-systems", Name = "CreateConnectedSystem")]
    [ProducesResponseType(typeof(ConnectedSystemDetailDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> CreateConnectedSystemAsync([FromBody] CreateConnectedSystemRequest request)
    {
        _logger.LogInformation("Creating connected system: {Name} with connector {ConnectorId}", request.Name, request.ConnectorDefinitionId);

        // Get the current user from the JWT claims (may be null for API key auth)
        var initiatedBy = await GetCurrentUserAsync();
        if (initiatedBy == null && !IsApiKeyAuthenticated())
        {
            _logger.LogWarning("Could not identify user from JWT claims for connected system creation");
            return Unauthorized(ApiErrorResponse.Unauthorised("Could not identify user from authentication token."));
        }

        if (IsApiKeyAuthenticated())
        {
            _logger.LogInformation("Connected system creation initiated via API key: {ApiKeyName}", GetApiKeyName());
        }

        // Get the connector definition
        var connectorDefinition = await _application.ConnectedSystems.GetConnectorDefinitionAsync(request.ConnectorDefinitionId);
        if (connectorDefinition == null)
            return BadRequest(ApiErrorResponse.BadRequest($"Connector definition with ID {request.ConnectorDefinitionId} not found."));

        // Create the connected system
        var connectedSystem = new ConnectedSystem
        {
            Name = request.Name,
            Description = request.Description,
            ConnectorDefinition = connectorDefinition
        };

        try
        {
            // Get the current API key for Activity attribution if authenticated via API key
            var apiKey = await GetCurrentApiKeyAsync();
            if (apiKey != null)
                await _application.ConnectedSystems.CreateConnectedSystemAsync(connectedSystem, apiKey);
            else
                await _application.ConnectedSystems.CreateConnectedSystemAsync(connectedSystem, initiatedBy);

            _logger.LogInformation("Created connected system: {Id} ({Name})", connectedSystem.Id, connectedSystem.Name);

            // Retrieve the created system to get all populated fields
            var created = await _application.ConnectedSystems.GetConnectedSystemAsync(connectedSystem.Id);
            return CreatedAtRoute("GetConnectedSystem", new { connectedSystemId = connectedSystem.Id }, ConnectedSystemDetailDto.FromEntity(created!));
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning(ex, "Failed to create connected system: {Message}", ex.Message);
            return BadRequest(ApiErrorResponse.BadRequest(ex.Message));
        }
    }

    /// <summary>
    /// Updates an existing Connected System.
    /// </summary>
    /// <remarks>
    /// Updates the name, description, and/or setting values of an existing Connected System.
    /// Only the fields provided in the request will be updated.
    /// </remarks>
    /// <param name="connectedSystemId">The unique identifier of the connected system to update.</param>
    /// <param name="request">The update request with new values.</param>
    /// <returns>The updated connected system details.</returns>
    /// <response code="200">Connected system updated successfully.</response>
    /// <response code="400">Invalid request.</response>
    /// <response code="404">Connected system not found.</response>
    /// <response code="401">User could not be identified from authentication token.</response>
    [HttpPut("connected-systems/{connectedSystemId:int}", Name = "UpdateConnectedSystem")]
    [ProducesResponseType(typeof(ConnectedSystemDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> UpdateConnectedSystemAsync(int connectedSystemId, [FromBody] UpdateConnectedSystemRequest request)
    {
        _logger.LogInformation("Updating connected system: {Id}", connectedSystemId);

        // Get the current user from the JWT claims (may be null for API key auth)
        var initiatedBy = await GetCurrentUserAsync();
        if (initiatedBy == null && !IsApiKeyAuthenticated())
        {
            _logger.LogWarning("Could not identify user from JWT claims for connected system update");
            return Unauthorized(ApiErrorResponse.Unauthorised("Could not identify user from authentication token."));
        }

        // Get the existing connected system
        var connectedSystem = await _application.ConnectedSystems.GetConnectedSystemAsync(connectedSystemId);
        if (connectedSystem == null)
            return NotFound(ApiErrorResponse.NotFound($"Connected system with ID {connectedSystemId} not found."));

        // Apply updates
        if (!string.IsNullOrEmpty(request.Name))
            connectedSystem.Name = request.Name;

        if (request.Description != null)
            connectedSystem.Description = request.Description;

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
        }

        try
        {
            // Get the current API key for Activity attribution if authenticated via API key
            var apiKey = await GetCurrentApiKeyAsync();
            if (apiKey != null)
                await _application.ConnectedSystems.UpdateConnectedSystemAsync(connectedSystem, apiKey);
            else
                await _application.ConnectedSystems.UpdateConnectedSystemAsync(connectedSystem, initiatedBy);

            _logger.LogInformation("Updated connected system: {Id} ({Name})", connectedSystem.Id, connectedSystem.Name);

            // Retrieve the updated system
            var updated = await _application.ConnectedSystems.GetConnectedSystemAsync(connectedSystemId);
            return Ok(ConnectedSystemDetailDto.FromEntity(updated!));
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning(ex, "Failed to update connected system: {Message}", ex.Message);
            return BadRequest(ApiErrorResponse.BadRequest(ex.Message));
        }
    }

    /// <summary>
    /// Imports the schema from the Connected System.
    /// </summary>
    /// <remarks>
    /// Connects to the external system and retrieves its schema (object types and attributes).
    /// This is required before creating sync rules, as sync rules reference object type IDs.
    ///
    /// **Note:** This operation is destructive - it will replace any existing schema configuration.
    /// Any sync rules referencing removed object types/attributes will need to be updated.
    /// </remarks>
    /// <param name="connectedSystemId">The unique identifier of the connected system.</param>
    /// <returns>The updated connected system with imported schema.</returns>
    /// <response code="200">Schema imported successfully.</response>
    /// <response code="400">Schema import failed (e.g., connection error, invalid settings).</response>
    /// <response code="404">Connected system not found.</response>
    /// <response code="401">User could not be identified from authentication token.</response>
    [HttpPost("connected-systems/{connectedSystemId:int}/import-schema", Name = "ImportConnectedSystemSchema")]
    [ProducesResponseType(typeof(ConnectedSystemDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> ImportConnectedSystemSchemaAsync(int connectedSystemId)
    {
        _logger.LogInformation("Schema import requested for connected system: {Id}", connectedSystemId);

        // Get the current user from the JWT claims (may be null for API key auth)
        var initiatedBy = await GetCurrentUserAsync();
        if (initiatedBy == null && !IsApiKeyAuthenticated())
        {
            _logger.LogWarning("Could not identify user from JWT claims for schema import");
            return Unauthorized(ApiErrorResponse.Unauthorised("Could not identify user from authentication token."));
        }

        // Get the connected system
        var connectedSystem = await _application.ConnectedSystems.GetConnectedSystemAsync(connectedSystemId);
        if (connectedSystem == null)
            return NotFound(ApiErrorResponse.NotFound($"Connected system with ID {connectedSystemId} not found."));

        try
        {
            // Get the current API key for Activity attribution if authenticated via API key
            var apiKey = await GetCurrentApiKeyAsync();
            if (apiKey != null)
                await _application.ConnectedSystems.ImportConnectedSystemSchemaAsync(connectedSystem, apiKey);
            else
                await _application.ConnectedSystems.ImportConnectedSystemSchemaAsync(connectedSystem, initiatedBy);

            _logger.LogInformation("Schema imported for connected system: {Id} ({Name}), {Count} object types",
                connectedSystemId, connectedSystem.Name, connectedSystem.ObjectTypes?.Count ?? 0);

            // Retrieve the updated system
            var updated = await _application.ConnectedSystems.GetConnectedSystemAsync(connectedSystemId);
            return Ok(ConnectedSystemDetailDto.FromEntity(updated!));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to import schema for connected system: {Id}", connectedSystemId);
            return BadRequest(ApiErrorResponse.BadRequest($"Schema import failed: {ex.Message}"));
        }
    }

    /// <summary>
    /// Imports hierarchy (partitions and containers) from the connected system.
    /// </summary>
    /// <remarks>
    /// This endpoint connects to the external system and retrieves its partition and container hierarchy.
    /// For LDAP connectors, this retrieves naming contexts and organisational units.
    ///
    /// After importing the hierarchy, you can select which partitions and containers to include
    /// in import operations using the partition and container update endpoints.
    ///
    /// This operation uses a match-and-merge approach that preserves existing partition and container
    /// selections where possible. Items are matched by their ExternalId (e.g., LDAP DN). The response
    /// includes details about what changed: added items, removed items, renamed items, and moved containers.
    ///
    /// If any previously selected items were removed (no longer exist in the external system),
    /// the `hasSelectedItemsRemoved` flag will be true in the response as a warning.
    /// </remarks>
    /// <param name="connectedSystemId">The unique identifier of the connected system.</param>
    /// <returns>A result object describing what changed during the hierarchy refresh.</returns>
    /// <response code="200">Hierarchy imported successfully.</response>
    /// <response code="400">Hierarchy import failed (e.g., connection error, invalid settings).</response>
    /// <response code="404">Connected system not found.</response>
    /// <response code="401">User could not be identified from authentication token.</response>
    [HttpPost("connected-systems/{connectedSystemId:int}/import-hierarchy", Name = "ImportConnectedSystemHierarchy")]
    [ProducesResponseType(typeof(HierarchyRefreshResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> ImportConnectedSystemHierarchyAsync(int connectedSystemId)
    {
        _logger.LogInformation("Hierarchy import requested for connected system: {Id}", connectedSystemId);

        // Get the current user from the JWT claims (may be null for API key auth)
        var initiatedBy = await GetCurrentUserAsync();
        if (initiatedBy == null && !IsApiKeyAuthenticated())
        {
            _logger.LogWarning("Could not identify user from JWT claims for hierarchy import");
            return Unauthorized(ApiErrorResponse.Unauthorised("Could not identify user from authentication token."));
        }

        // Get the connected system
        var connectedSystem = await _application.ConnectedSystems.GetConnectedSystemAsync(connectedSystemId);
        if (connectedSystem == null)
            return NotFound(ApiErrorResponse.NotFound($"Connected system with ID {connectedSystemId} not found."));

        try
        {
            // Call the appropriate overload based on authentication method
            JIM.Models.Staging.DTOs.HierarchyRefreshResult result;
            var apiKey = await GetCurrentApiKeyAsync();
            if (apiKey != null)
                result = await _application.ConnectedSystems.ImportConnectedSystemHierarchyAsync(connectedSystem, apiKey);
            else
                result = await _application.ConnectedSystems.ImportConnectedSystemHierarchyAsync(connectedSystem, initiatedBy);

            _logger.LogInformation("Hierarchy imported for connected system: {Id} ({Name}). Summary: {Summary}",
                connectedSystemId, connectedSystem.Name, result.GetSummary());

            return Ok(HierarchyRefreshResultDto.FromModel(result));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to import hierarchy for connected system: {Id}", connectedSystemId);
            return BadRequest(ApiErrorResponse.BadRequest($"Hierarchy import failed: {ex.Message}"));
        }
    }

    /// <summary>
    /// Deletes a Connected System and all its related data.
    /// </summary>
    /// <remarks>
    /// This operation may execute synchronously or be queued as a background job depending on system size:
    /// - Small systems (less than 1000 CSOs): Deleted immediately, returns 200 OK
    /// - Large systems: Queued as background job, returns 202 Accepted with tracking IDs
    /// - Systems with running sync: Queued to run after sync completes, returns 202 Accepted
    ///
    /// Use the deletion-preview endpoint first to understand the impact before calling this endpoint.
    /// </remarks>
    /// <param name="connectedSystemId">The unique identifier of the connected system to delete.</param>
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
    public async Task<IActionResult> DeleteConnectedSystemAsync(int connectedSystemId)
    {
        _logger.LogInformation("Deletion requested for connected system: {Id}", connectedSystemId);

        // Get the current user from the JWT claims (may be null for API key auth)
        var initiatedBy = await GetCurrentUserAsync();
        if (initiatedBy == null && !IsApiKeyAuthenticated())
        {
            _logger.LogWarning("Could not identify user from JWT claims for deletion request");
            return Unauthorized(ApiErrorResponse.Unauthorised("Could not identify user from authentication token."));
        }

        var result = await _application.ConnectedSystems.DeleteAsync(connectedSystemId, initiatedBy);

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

    #endregion

    #region Connector Definitions

    /// <summary>
    /// Gets all available connector definitions.
    /// </summary>
    /// <remarks>
    /// Connector definitions describe the available connector types that can be used when creating Connected Systems.
    /// Each connector definition includes metadata about capabilities, settings, and configuration options.
    /// </remarks>
    /// <returns>A list of all available connector definitions.</returns>
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
    /// Gets a specific connector definition by ID.
    /// </summary>
    /// <param name="id">The unique identifier of the connector definition.</param>
    /// <returns>The connector definition details including all settings and capabilities.</returns>
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
    /// Gets a specific connector definition by name.
    /// </summary>
    /// <param name="name">The name of the connector definition (e.g., "CSV File", "LDAP").</param>
    /// <returns>The connector definition details including all settings and capabilities.</returns>
    [HttpGet("connector-definitions/by-name/{name}", Name = "GetConnectorDefinitionByName")]
    [ProducesResponseType(typeof(ConnectorDefinition), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetConnectorDefinitionByNameAsync(string name)
    {
        _logger.LogTrace("Requested connector definition by name: {Name}", name);
        var definition = await _application.ConnectedSystems.GetConnectorDefinitionAsync(name);
        if (definition == null)
            return NotFound(ApiErrorResponse.NotFound($"Connector definition with name '{name}' not found."));

        return Ok(definition);
    }

    #endregion

    #region Run Profiles

    /// <summary>
    /// Gets all run profiles for a connected system.
    /// </summary>
    /// <param name="connectedSystemId">The unique identifier of the connected system.</param>
    /// <returns>A list of run profiles configured for the connected system.</returns>
    [HttpGet("connected-systems/{connectedSystemId:int}/run-profiles", Name = "GetRunProfiles")]
    [ProducesResponseType(typeof(IEnumerable<RunProfileDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetRunProfilesAsync(int connectedSystemId)
    {
        _logger.LogTrace("Requested run profiles for connected system: {Id}", connectedSystemId);

        var system = await _application.ConnectedSystems.GetConnectedSystemAsync(connectedSystemId);
        if (system == null)
            return NotFound(ApiErrorResponse.NotFound($"Connected system with ID {connectedSystemId} not found."));

        var runProfiles = await _application.ConnectedSystems.GetConnectedSystemRunProfilesAsync(connectedSystemId);
        var dtos = runProfiles.Select(RunProfileDto.FromEntity);
        return Ok(dtos);
    }

    /// <summary>
    /// Executes a run profile to trigger a synchronisation operation.
    /// </summary>
    /// <remarks>
    /// This endpoint queues a synchronisation task (Full Import, Delta Import, Full Sync, Delta Sync, or Export)
    /// for execution by the worker service. The task runs asynchronously and can be monitored via the Activities API.
    ///
    /// Returns 202 Accepted with the Activity ID and Task ID for tracking the execution.
    /// </remarks>
    /// <param name="connectedSystemId">The unique identifier of the connected system.</param>
    /// <param name="runProfileId">The unique identifier of the run profile to execute.</param>
    /// <returns>The execution response with activity and task IDs for tracking.</returns>
    /// <response code="202">Run profile execution has been queued.</response>
    /// <response code="404">Connected system or run profile not found.</response>
    /// <response code="401">User could not be identified from authentication token.</response>
    [HttpPost("connected-systems/{connectedSystemId:int}/run-profiles/{runProfileId:int}/execute", Name = "ExecuteRunProfile")]
    [ProducesResponseType(typeof(RunProfileExecutionResponse), StatusCodes.Status202Accepted)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> ExecuteRunProfileAsync(int connectedSystemId, int runProfileId)
    {
        _logger.LogInformation("Run profile execution requested: ConnectedSystem={SystemId}, RunProfile={ProfileId}",
            connectedSystemId, runProfileId);

        // Verify connected system exists
        var system = await _application.ConnectedSystems.GetConnectedSystemAsync(connectedSystemId);
        if (system == null)
            return NotFound(ApiErrorResponse.NotFound($"Connected system with ID {connectedSystemId} not found."));

        // Verify run profile exists and belongs to this connected system
        var runProfiles = await _application.ConnectedSystems.GetConnectedSystemRunProfilesAsync(connectedSystemId);
        var runProfile = runProfiles.FirstOrDefault(rp => rp.Id == runProfileId);
        if (runProfile == null)
            return NotFound(ApiErrorResponse.NotFound($"Run profile with ID {runProfileId} not found for connected system {connectedSystemId}."));

        // Get the current user from the JWT claims (may be null for API key auth)
        var initiatedBy = await GetCurrentUserAsync();
        if (initiatedBy == null && !IsApiKeyAuthenticated())
        {
            _logger.LogWarning("Could not identify user from JWT claims for run profile execution");
            return Unauthorized(ApiErrorResponse.Unauthorised("Could not identify user from authentication token."));
        }

        // Create and queue the synchronisation task
        // Use API key for attribution when authenticated via API key
        SynchronisationWorkerTask workerTask;
        if (initiatedBy != null)
        {
            workerTask = new SynchronisationWorkerTask(connectedSystemId, runProfileId, initiatedBy);
        }
        else
        {
            var apiKey = await GetCurrentApiKeyAsync();
            if (apiKey == null)
            {
                _logger.LogError("Failed to resolve API key for run profile execution");
                return BadRequest(new { error = "Failed to identify initiating API key" });
            }
            workerTask = new SynchronisationWorkerTask(connectedSystemId, runProfileId, apiKey);
        }

        var result = await _application.Tasking.CreateWorkerTaskAsync(workerTask);
        if (!result.Success)
        {
            _logger.LogWarning("Run profile execution blocked: {Error}", result.ErrorMessage);
            return BadRequest(ApiErrorResponse.BadRequest(result.ErrorMessage ?? "Validation failed."));
        }

        _logger.LogInformation("Run profile execution queued: ConnectedSystem={SystemId}, RunProfile={ProfileId}, TaskId={TaskId}, ActivityId={ActivityId}",
            connectedSystemId, runProfileId, workerTask.Id, workerTask.Activity?.Id);

        var response = new RunProfileExecutionResponse
        {
            ActivityId = workerTask.Activity?.Id ?? Guid.Empty,
            TaskId = workerTask.Id,
            Message = $"Run profile '{runProfile.Name}' has been queued for execution.",
            Warnings = result.Warnings
        };

        return Accepted(response);
    }

    /// <summary>
    /// Creates a new Run Profile for a Connected System.
    /// </summary>
    /// <param name="connectedSystemId">The unique identifier of the connected system.</param>
    /// <param name="request">The run profile creation request.</param>
    /// <returns>The created run profile details.</returns>
    /// <response code="201">Run profile created successfully.</response>
    /// <response code="400">Invalid request or run type not supported by connector.</response>
    /// <response code="404">Connected system not found.</response>
    /// <response code="401">User could not be identified from authentication token.</response>
    [HttpPost("connected-systems/{connectedSystemId:int}/run-profiles", Name = "CreateRunProfile")]
    [ProducesResponseType(typeof(RunProfileDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> CreateRunProfileAsync(int connectedSystemId, [FromBody] CreateRunProfileRequest request)
    {
        _logger.LogInformation("Creating run profile: {Name} for connected system {SystemId}", request.Name, connectedSystemId);

        // Get the current user from the JWT claims (may be null for API key auth)
        var initiatedBy = await GetCurrentUserAsync();
        if (initiatedBy == null && !IsApiKeyAuthenticated())
        {
            _logger.LogWarning("Could not identify user from JWT claims for run profile creation");
            return Unauthorized(ApiErrorResponse.Unauthorised("Could not identify user from authentication token."));
        }

        // Verify connected system exists
        var system = await _application.ConnectedSystems.GetConnectedSystemAsync(connectedSystemId);
        if (system == null)
            return NotFound(ApiErrorResponse.NotFound($"Connected system with ID {connectedSystemId} not found."));

        // Create the run profile
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

            _logger.LogInformation("Created run profile: {Id} ({Name})", runProfile.Id, runProfile.Name);

            return CreatedAtRoute("GetRunProfiles", new { connectedSystemId }, RunProfileDto.FromEntity(runProfile));
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning(ex, "Failed to create run profile: {Message}", ex.Message);
            return BadRequest(ApiErrorResponse.BadRequest(ex.Message));
        }
    }

    /// <summary>
    /// Updates an existing Run Profile.
    /// </summary>
    /// <param name="connectedSystemId">The unique identifier of the connected system.</param>
    /// <param name="runProfileId">The unique identifier of the run profile to update.</param>
    /// <param name="request">The update request with new values.</param>
    /// <returns>The updated run profile details.</returns>
    /// <response code="200">Run profile updated successfully.</response>
    /// <response code="400">Invalid request.</response>
    /// <response code="404">Connected system or run profile not found.</response>
    /// <response code="401">User could not be identified from authentication token.</response>
    [HttpPut("connected-systems/{connectedSystemId:int}/run-profiles/{runProfileId:int}", Name = "UpdateRunProfile")]
    [ProducesResponseType(typeof(RunProfileDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> UpdateRunProfileAsync(int connectedSystemId, int runProfileId, [FromBody] UpdateRunProfileRequest request)
    {
        _logger.LogInformation("Updating run profile: {Id} for connected system {SystemId}", runProfileId, connectedSystemId);

        // Get the current user from the JWT claims (may be null for API key auth)
        var initiatedBy = await GetCurrentUserAsync();
        if (initiatedBy == null && !IsApiKeyAuthenticated())
        {
            _logger.LogWarning("Could not identify user from JWT claims for run profile update");
            return Unauthorized(ApiErrorResponse.Unauthorised("Could not identify user from authentication token."));
        }

        // Verify connected system exists
        var system = await _application.ConnectedSystems.GetConnectedSystemAsync(connectedSystemId);
        if (system == null)
            return NotFound(ApiErrorResponse.NotFound($"Connected system with ID {connectedSystemId} not found."));

        // Get the run profile
        var runProfiles = await _application.ConnectedSystems.GetConnectedSystemRunProfilesAsync(connectedSystemId);
        var runProfile = runProfiles.FirstOrDefault(rp => rp.Id == runProfileId);
        if (runProfile == null)
            return NotFound(ApiErrorResponse.NotFound($"Run profile with ID {runProfileId} not found for connected system {connectedSystemId}."));

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
            await _application.ConnectedSystems.UpdateConnectedSystemRunProfileAsync(runProfile, initiatedBy);

            _logger.LogInformation("Updated run profile: {Id} ({Name})", runProfile.Id, runProfile.Name);

            return Ok(RunProfileDto.FromEntity(runProfile));
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning(ex, "Failed to update run profile: {Message}", ex.Message);
            return BadRequest(ApiErrorResponse.BadRequest(ex.Message));
        }
    }

    /// <summary>
    /// Deletes a Run Profile.
    /// </summary>
    /// <param name="connectedSystemId">The unique identifier of the connected system.</param>
    /// <param name="runProfileId">The unique identifier of the run profile to delete.</param>
    /// <returns>No content on success.</returns>
    /// <response code="204">Run profile deleted successfully.</response>
    /// <response code="404">Connected system or run profile not found.</response>
    /// <response code="401">User could not be identified from authentication token.</response>
    [HttpDelete("connected-systems/{connectedSystemId:int}/run-profiles/{runProfileId:int}", Name = "DeleteRunProfile")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> DeleteRunProfileAsync(int connectedSystemId, int runProfileId)
    {
        _logger.LogInformation("Deleting run profile: {Id} for connected system {SystemId}", runProfileId, connectedSystemId);

        // Get the current user from the JWT claims (may be null for API key auth)
        var initiatedBy = await GetCurrentUserAsync();
        if (initiatedBy == null && !IsApiKeyAuthenticated())
        {
            _logger.LogWarning("Could not identify user from JWT claims for run profile deletion");
            return Unauthorized(ApiErrorResponse.Unauthorised("Could not identify user from authentication token."));
        }

        // Verify connected system exists
        var system = await _application.ConnectedSystems.GetConnectedSystemAsync(connectedSystemId);
        if (system == null)
            return NotFound(ApiErrorResponse.NotFound($"Connected system with ID {connectedSystemId} not found."));

        // Get the run profile
        var runProfiles = await _application.ConnectedSystems.GetConnectedSystemRunProfilesAsync(connectedSystemId);
        var runProfile = runProfiles.FirstOrDefault(rp => rp.Id == runProfileId);
        if (runProfile == null)
            return NotFound(ApiErrorResponse.NotFound($"Run profile with ID {runProfileId} not found for connected system {connectedSystemId}."));

        await _application.ConnectedSystems.DeleteConnectedSystemRunProfileAsync(runProfile, initiatedBy);

        _logger.LogInformation("Deleted run profile: {Id}", runProfileId);

        return NoContent();
    }

    #endregion

    #region Sync Rules

    /// <summary>
    /// Gets all synchronisation rules with optional pagination, sorting, and filtering.
    /// </summary>
    /// <param name="pagination">Pagination parameters (page, pageSize, sortBy, sortDirection, filter).</param>
    /// <returns>A paginated list of sync rule headers.</returns>
    [HttpGet("sync-rules", Name = "GetSyncRules")]
    [ProducesResponseType(typeof(PaginatedResponse<SyncRuleHeader>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetSyncRulesAsync([FromQuery] PaginationRequest pagination)
    {
        _logger.LogTrace("Requested synchronisation rules (Page: {Page}, PageSize: {PageSize})", pagination.Page, pagination.PageSize);
        var rules = await _application.ConnectedSystems.GetSyncRulesAsync();
        var headers = rules.Select(SyncRuleHeader.FromEntity).AsQueryable();

        var result = headers
            .ApplySortAndFilter(pagination)
            .ToPaginatedResponse(pagination);

        return Ok(result);
    }

    /// <summary>
    /// Gets a specific synchronisation rule by ID.
    /// </summary>
    /// <param name="id">The unique identifier of the sync rule.</param>
    /// <returns>The sync rule details including attribute flow configuration.</returns>
    [HttpGet("sync-rules/{id:int}", Name = "GetSyncRule")]
    [ProducesResponseType(typeof(SyncRuleHeader), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetSyncRuleAsync(int id)
    {
        _logger.LogTrace("Requested sync rule: {Id}", id);
        var rule = await _application.ConnectedSystems.GetSyncRuleAsync(id);
        if (rule == null)
            return NotFound(ApiErrorResponse.NotFound($"Sync rule with ID {id} not found."));

        return Ok(SyncRuleHeader.FromEntity(rule));
    }

    /// <summary>
    /// Creates a new Sync Rule.
    /// </summary>
    /// <remarks>
    /// Creates a sync rule that defines how data flows between a Connected System and the Metaverse.
    /// For Import rules, set ProjectToMetaverse to true to create Metaverse objects from imported data.
    /// For Export rules, set ProvisionToConnectedSystem to true to create Connected System objects.
    /// </remarks>
    /// <param name="request">The sync rule creation request.</param>
    /// <returns>The created sync rule details.</returns>
    /// <response code="201">Sync rule created successfully.</response>
    /// <response code="400">Invalid request or validation failed.</response>
    /// <response code="401">User could not be identified from authentication token.</response>
    [HttpPost("sync-rules", Name = "CreateSyncRule")]
    [ProducesResponseType(typeof(SyncRuleHeader), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> CreateSyncRuleAsync([FromBody] CreateSyncRuleRequest request)
    {
        _logger.LogInformation("Creating sync rule: {Name}", request.Name);

        // Get the current user from the JWT claims (may be null for API key auth)
        var initiatedBy = await GetCurrentUserAsync();
        if (initiatedBy == null && !IsApiKeyAuthenticated())
        {
            _logger.LogWarning("Could not identify user from JWT claims for sync rule creation");
            return Unauthorized(ApiErrorResponse.Unauthorised("Could not identify user from authentication token."));
        }

        // Verify connected system exists
        var connectedSystem = await _application.ConnectedSystems.GetConnectedSystemAsync(request.ConnectedSystemId);
        if (connectedSystem == null)
            return BadRequest(ApiErrorResponse.BadRequest($"Connected system with ID {request.ConnectedSystemId} not found."));

        // Get connected system object type
        var csObjectTypes = await _application.ConnectedSystems.GetObjectTypesAsync(request.ConnectedSystemId);
        var csObjectType = csObjectTypes?.FirstOrDefault(t => t.Id == request.ConnectedSystemObjectTypeId);
        if (csObjectType == null)
            return BadRequest(ApiErrorResponse.BadRequest($"Connected system object type with ID {request.ConnectedSystemObjectTypeId} not found."));

        // Get metaverse object type
        var mvObjectType = await _application.Metaverse.GetMetaverseObjectTypeAsync(request.MetaverseObjectTypeId, false);
        if (mvObjectType == null)
            return BadRequest(ApiErrorResponse.BadRequest($"Metaverse object type with ID {request.MetaverseObjectTypeId} not found."));

        // Create the sync rule
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
            return BadRequest(ApiErrorResponse.BadRequest($"Sync rule validation failed: {errorMessage}"));
        }

        _logger.LogInformation("Created sync rule: {Id} ({Name})", syncRule.Id, syncRule.Name);

        // Retrieve the created sync rule
        var created = await _application.ConnectedSystems.GetSyncRuleAsync(syncRule.Id);
        return CreatedAtRoute("GetSyncRule", new { id = syncRule.Id }, SyncRuleHeader.FromEntity(created!));
    }

    /// <summary>
    /// Updates an existing Sync Rule.
    /// </summary>
    /// <param name="id">The unique identifier of the sync rule to update.</param>
    /// <param name="request">The update request with new values.</param>
    /// <returns>The updated sync rule details.</returns>
    /// <response code="200">Sync rule updated successfully.</response>
    /// <response code="400">Invalid request or validation failed.</response>
    /// <response code="404">Sync rule not found.</response>
    /// <response code="401">User could not be identified from authentication token.</response>
    [HttpPut("sync-rules/{id:int}", Name = "UpdateSyncRule")]
    [ProducesResponseType(typeof(SyncRuleHeader), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> UpdateSyncRuleAsync(int id, [FromBody] UpdateSyncRuleRequest request)
    {
        _logger.LogInformation("Updating sync rule: {Id}", id);

        // Get the current user from the JWT claims (may be null for API key auth)
        var initiatedBy = await GetCurrentUserAsync();
        if (initiatedBy == null && !IsApiKeyAuthenticated())
        {
            _logger.LogWarning("Could not identify user from JWT claims for sync rule update");
            return Unauthorized(ApiErrorResponse.Unauthorised("Could not identify user from authentication token."));
        }

        // Get the existing sync rule
        var syncRule = await _application.ConnectedSystems.GetSyncRuleAsync(id);
        if (syncRule == null)
            return NotFound(ApiErrorResponse.NotFound($"Sync rule with ID {id} not found."));

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
            return BadRequest(ApiErrorResponse.BadRequest($"Sync rule validation failed: {errorMessage}"));
        }

        _logger.LogInformation("Updated sync rule: {Id} ({Name})", syncRule.Id, syncRule.Name);

        // Retrieve the updated sync rule
        var updated = await _application.ConnectedSystems.GetSyncRuleAsync(id);
        return Ok(SyncRuleHeader.FromEntity(updated!));
    }

    /// <summary>
    /// Deletes a Sync Rule.
    /// </summary>
    /// <param name="id">The unique identifier of the sync rule to delete.</param>
    /// <returns>No content on success.</returns>
    /// <response code="204">Sync rule deleted successfully.</response>
    /// <response code="404">Sync rule not found.</response>
    /// <response code="401">User could not be identified from authentication token.</response>
    [HttpDelete("sync-rules/{id:int}", Name = "DeleteSyncRule")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> DeleteSyncRuleAsync(int id)
    {
        _logger.LogInformation("Deleting sync rule: {Id}", id);

        // Get the current user from the JWT claims (may be null for API key auth)
        var initiatedBy = await GetCurrentUserAsync();
        if (initiatedBy == null && !IsApiKeyAuthenticated())
        {
            _logger.LogWarning("Could not identify user from JWT claims for sync rule deletion");
            return Unauthorized(ApiErrorResponse.Unauthorised("Could not identify user from authentication token."));
        }

        // Get the sync rule
        var syncRule = await _application.ConnectedSystems.GetSyncRuleAsync(id);
        if (syncRule == null)
            return NotFound(ApiErrorResponse.NotFound($"Sync rule with ID {id} not found."));

        await _application.ConnectedSystems.DeleteSyncRuleAsync(syncRule, initiatedBy);

        _logger.LogInformation("Deleted sync rule: {Id}", id);

        return NoContent();
    }

    #endregion

    #region Sync Rule Mappings

    /// <summary>
    /// Gets all attribute flow mappings for a sync rule.
    /// </summary>
    /// <param name="syncRuleId">The unique identifier of the sync rule.</param>
    /// <returns>A list of attribute flow mappings.</returns>
    [HttpGet("sync-rules/{syncRuleId:int}/mappings", Name = "GetSyncRuleMappings")]
    [ProducesResponseType(typeof(IEnumerable<SyncRuleMappingDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetSyncRuleMappingsAsync(int syncRuleId)
    {
        _logger.LogTrace("Requested mappings for sync rule: {Id}", syncRuleId);

        var syncRule = await _application.ConnectedSystems.GetSyncRuleAsync(syncRuleId);
        if (syncRule == null)
            return NotFound(ApiErrorResponse.NotFound($"Sync rule with ID {syncRuleId} not found."));

        var mappings = await _application.ConnectedSystems.GetSyncRuleMappingsAsync(syncRuleId);
        var dtos = mappings.Select(SyncRuleMappingDto.FromEntity);
        return Ok(dtos);
    }

    /// <summary>
    /// Gets a specific attribute flow mapping by ID.
    /// </summary>
    /// <param name="syncRuleId">The unique identifier of the sync rule.</param>
    /// <param name="mappingId">The unique identifier of the mapping.</param>
    /// <returns>The attribute flow mapping details.</returns>
    [HttpGet("sync-rules/{syncRuleId:int}/mappings/{mappingId:int}", Name = "GetSyncRuleMapping")]
    [ProducesResponseType(typeof(SyncRuleMappingDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetSyncRuleMappingAsync(int syncRuleId, int mappingId)
    {
        _logger.LogTrace("Requested mapping {MappingId} for sync rule: {SyncRuleId}", mappingId, syncRuleId);

        var syncRule = await _application.ConnectedSystems.GetSyncRuleAsync(syncRuleId);
        if (syncRule == null)
            return NotFound(ApiErrorResponse.NotFound($"Sync rule with ID {syncRuleId} not found."));

        var mapping = await _application.ConnectedSystems.GetSyncRuleMappingAsync(mappingId);
        if (mapping == null || mapping.SyncRule?.Id != syncRuleId)
            return NotFound(ApiErrorResponse.NotFound($"Mapping with ID {mappingId} not found in sync rule {syncRuleId}."));

        return Ok(SyncRuleMappingDto.FromEntity(mapping));
    }

    /// <summary>
    /// Creates a new attribute flow mapping for a sync rule.
    /// </summary>
    /// <remarks>
    /// For Import rules (direction = Import), specify TargetMetaverseAttributeId and source ConnectedSystemAttributeIds.
    /// For Export rules (direction = Export), specify TargetConnectedSystemAttributeId and source MetaverseAttributeIds.
    /// </remarks>
    /// <param name="syncRuleId">The unique identifier of the sync rule.</param>
    /// <param name="request">The mapping creation request.</param>
    /// <returns>The created attribute flow mapping.</returns>
    /// <response code="201">Mapping created successfully.</response>
    /// <response code="400">Invalid request or validation failed.</response>
    /// <response code="404">Sync rule or referenced attributes not found.</response>
    /// <response code="401">User could not be identified from authentication token.</response>
    [HttpPost("sync-rules/{syncRuleId:int}/mappings", Name = "CreateSyncRuleMapping")]
    [ProducesResponseType(typeof(SyncRuleMappingDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> CreateSyncRuleMappingAsync(int syncRuleId, [FromBody] CreateSyncRuleMappingRequest request)
    {
        _logger.LogInformation("Creating mapping for sync rule: {SyncRuleId}", syncRuleId);

        var initiatedBy = await GetCurrentUserAsync();
        if (initiatedBy == null && !IsApiKeyAuthenticated())
        {
            _logger.LogWarning("Could not identify user from JWT claims for mapping creation");
            return Unauthorized(ApiErrorResponse.Unauthorised("Could not identify user from authentication token."));
        }

        var syncRule = await _application.ConnectedSystems.GetSyncRuleAsync(syncRuleId);
        if (syncRule == null)
            return NotFound(ApiErrorResponse.NotFound($"Sync rule with ID {syncRuleId} not found."));

        // Create the mapping
        var mapping = new SyncRuleMapping
        {
            SyncRule = syncRule
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
        }
        else // Export
        {
            if (!request.TargetConnectedSystemAttributeId.HasValue)
                return BadRequest(ApiErrorResponse.BadRequest("TargetConnectedSystemAttributeId is required for export rules."));

            var csAttr = await _application.ConnectedSystems.GetAttributeAsync(request.TargetConnectedSystemAttributeId.Value);
            if (csAttr == null)
                return NotFound(ApiErrorResponse.NotFound($"Connected system attribute with ID {request.TargetConnectedSystemAttributeId} not found."));

            // Verify attribute belongs to the sync rule's object type
            if (csAttr.ConnectedSystemObjectType.Id != syncRule.ConnectedSystemObjectTypeId)
                return BadRequest(ApiErrorResponse.BadRequest($"Attribute {csAttr.Name} does not belong to the sync rule's object type."));

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
                    return NotFound(ApiErrorResponse.NotFound($"Connected system attribute with ID {sourceRequest.ConnectedSystemAttributeId} not found."));

                // Verify attribute belongs to the sync rule's object type
                if (csAttr.ConnectedSystemObjectType.Id != syncRule.ConnectedSystemObjectTypeId)
                    return BadRequest(ApiErrorResponse.BadRequest($"Attribute {csAttr.Name} does not belong to the sync rule's object type."));

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

            _logger.LogInformation("Created mapping {MappingId} for sync rule {SyncRuleId}", mapping.Id, syncRuleId);

            // Retrieve the created mapping to get all populated fields
            var created = await _application.ConnectedSystems.GetSyncRuleMappingAsync(mapping.Id);
            return CreatedAtRoute("GetSyncRuleMapping", new { syncRuleId, mappingId = mapping.Id }, SyncRuleMappingDto.FromEntity(created!));
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning(ex, "Failed to create sync rule mapping: {Message}", ex.Message);
            return BadRequest(ApiErrorResponse.BadRequest(ex.Message));
        }
    }

    /// <summary>
    /// Deletes an attribute flow mapping.
    /// </summary>
    /// <param name="syncRuleId">The unique identifier of the sync rule.</param>
    /// <param name="mappingId">The unique identifier of the mapping to delete.</param>
    /// <returns>No content on success.</returns>
    /// <response code="204">Mapping deleted successfully.</response>
    /// <response code="404">Sync rule or mapping not found.</response>
    /// <response code="401">User could not be identified from authentication token.</response>
    [HttpDelete("sync-rules/{syncRuleId:int}/mappings/{mappingId:int}", Name = "DeleteSyncRuleMapping")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> DeleteSyncRuleMappingAsync(int syncRuleId, int mappingId)
    {
        _logger.LogInformation("Deleting mapping {MappingId} for sync rule {SyncRuleId}", mappingId, syncRuleId);

        var initiatedBy = await GetCurrentUserAsync();
        if (initiatedBy == null && !IsApiKeyAuthenticated())
        {
            _logger.LogWarning("Could not identify user from JWT claims for mapping deletion");
            return Unauthorized(ApiErrorResponse.Unauthorised("Could not identify user from authentication token."));
        }

        var syncRule = await _application.ConnectedSystems.GetSyncRuleAsync(syncRuleId);
        if (syncRule == null)
            return NotFound(ApiErrorResponse.NotFound($"Sync rule with ID {syncRuleId} not found."));

        var mapping = await _application.ConnectedSystems.GetSyncRuleMappingAsync(mappingId);
        if (mapping == null || mapping.SyncRule?.Id != syncRuleId)
            return NotFound(ApiErrorResponse.NotFound($"Mapping with ID {mappingId} not found in sync rule {syncRuleId}."));

        // Get the current API key for Activity attribution if authenticated via API key
        var apiKey = await GetCurrentApiKeyAsync();
        if (apiKey != null)
            await _application.ConnectedSystems.DeleteSyncRuleMappingAsync(mapping, apiKey);
        else
            await _application.ConnectedSystems.DeleteSyncRuleMappingAsync(mapping, initiatedBy);

        _logger.LogInformation("Deleted mapping {MappingId} from sync rule {SyncRuleId}", mappingId, syncRuleId);

        return NoContent();
    }

    #endregion

    #region Sync Rule Scoping Criteria

    /// <summary>
    /// Gets all scoping criteria groups for a sync rule.
    /// </summary>
    /// <remarks>
    /// Scoping criteria define which Metaverse objects are included in an export sync rule.
    /// Only export sync rules support scoping criteria.
    /// </remarks>
    /// <param name="syncRuleId">The unique identifier of the sync rule.</param>
    /// <returns>A list of scoping criteria groups with their criteria.</returns>
    /// <response code="200">Returns the list of scoping criteria groups.</response>
    /// <response code="400">Sync rule is not an export rule.</response>
    /// <response code="404">Sync rule not found.</response>
    [HttpGet("sync-rules/{syncRuleId:int}/scoping-criteria", Name = "GetScopingCriteriaGroups")]
    [ProducesResponseType(typeof(IEnumerable<SyncRuleScopingCriteriaGroupDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetScopingCriteriaGroupsAsync(int syncRuleId)
    {
        _logger.LogTrace("Requested scoping criteria for sync rule: {Id}", syncRuleId);

        var syncRule = await _application.ConnectedSystems.GetSyncRuleAsync(syncRuleId);
        if (syncRule == null)
            return NotFound(ApiErrorResponse.NotFound($"Sync rule with ID {syncRuleId} not found."));

        var dtos = syncRule.ObjectScopingCriteriaGroups
            .Where(g => g.ParentGroup == null) // Only return root groups (children are nested)
            .Select(SyncRuleScopingCriteriaGroupDto.FromEntity);

        return Ok(dtos);
    }

    /// <summary>
    /// Gets a specific scoping criteria group by ID.
    /// </summary>
    /// <param name="syncRuleId">The unique identifier of the sync rule.</param>
    /// <param name="groupId">The unique identifier of the criteria group.</param>
    /// <returns>The scoping criteria group details.</returns>
    [HttpGet("sync-rules/{syncRuleId:int}/scoping-criteria/{groupId:int}", Name = "GetScopingCriteriaGroup")]
    [ProducesResponseType(typeof(SyncRuleScopingCriteriaGroupDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetScopingCriteriaGroupAsync(int syncRuleId, int groupId)
    {
        _logger.LogTrace("Requested scoping criteria group {GroupId} for sync rule: {SyncRuleId}", groupId, syncRuleId);

        var syncRule = await _application.ConnectedSystems.GetSyncRuleAsync(syncRuleId);
        if (syncRule == null)
            return NotFound(ApiErrorResponse.NotFound($"Sync rule with ID {syncRuleId} not found."));

        var group = FindScopingCriteriaGroup(syncRule.ObjectScopingCriteriaGroups, groupId);
        if (group == null)
            return NotFound(ApiErrorResponse.NotFound($"Scoping criteria group with ID {groupId} not found in sync rule {syncRuleId}."));

        return Ok(SyncRuleScopingCriteriaGroupDto.FromEntity(group));
    }

    /// <summary>
    /// Creates a new root scoping criteria group for a sync rule.
    /// </summary>
    /// <remarks>
    /// Creates a new criteria group at the root level. Use the child-groups endpoint to create nested groups.
    /// </remarks>
    /// <param name="syncRuleId">The unique identifier of the sync rule.</param>
    /// <param name="request">The criteria group creation request.</param>
    /// <returns>The created scoping criteria group.</returns>
    /// <response code="201">Group created successfully.</response>
    /// <response code="400">Invalid request.</response>
    /// <response code="404">Sync rule not found.</response>
    [HttpPost("sync-rules/{syncRuleId:int}/scoping-criteria", Name = "CreateScopingCriteriaGroup")]
    [ProducesResponseType(typeof(SyncRuleScopingCriteriaGroupDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> CreateScopingCriteriaGroupAsync(int syncRuleId, [FromBody] CreateScopingCriteriaGroupRequest request)
    {
        _logger.LogInformation("Creating scoping criteria group for sync rule: {SyncRuleId}", syncRuleId);

        var initiatedBy = await GetCurrentUserAsync();
        if (initiatedBy == null && !IsApiKeyAuthenticated())
            return Unauthorized(ApiErrorResponse.Unauthorised("Could not identify user from authentication token."));

        var syncRule = await _application.ConnectedSystems.GetSyncRuleAsync(syncRuleId);
        if (syncRule == null)
            return NotFound(ApiErrorResponse.NotFound($"Sync rule with ID {syncRuleId} not found."));

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
            _logger.LogInformation("Created scoping criteria group {GroupId} for sync rule {SyncRuleId}", group.Id, syncRuleId);
            return CreatedAtRoute("GetScopingCriteriaGroup", new { syncRuleId, groupId = group.Id }, SyncRuleScopingCriteriaGroupDto.FromEntity(group));
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning(ex, "Failed to create scoping criteria group: {Message}", ex.Message);
            return BadRequest(ApiErrorResponse.BadRequest(ex.Message));
        }
    }

    /// <summary>
    /// Creates a new child scoping criteria group nested within a parent group.
    /// </summary>
    /// <param name="syncRuleId">The unique identifier of the sync rule.</param>
    /// <param name="parentGroupId">The unique identifier of the parent criteria group.</param>
    /// <param name="request">The criteria group creation request.</param>
    /// <returns>The created scoping criteria group.</returns>
    [HttpPost("sync-rules/{syncRuleId:int}/scoping-criteria/{parentGroupId:int}/child-groups", Name = "CreateChildScopingCriteriaGroup")]
    [ProducesResponseType(typeof(SyncRuleScopingCriteriaGroupDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> CreateChildScopingCriteriaGroupAsync(int syncRuleId, int parentGroupId, [FromBody] CreateScopingCriteriaGroupRequest request)
    {
        _logger.LogInformation("Creating child scoping criteria group under {ParentId} for sync rule: {SyncRuleId}", parentGroupId, syncRuleId);

        var initiatedBy = await GetCurrentUserAsync();
        if (initiatedBy == null && !IsApiKeyAuthenticated())
            return Unauthorized(ApiErrorResponse.Unauthorised("Could not identify user from authentication token."));

        var syncRule = await _application.ConnectedSystems.GetSyncRuleAsync(syncRuleId);
        if (syncRule == null)
            return NotFound(ApiErrorResponse.NotFound($"Sync rule with ID {syncRuleId} not found."));

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
    /// Updates a scoping criteria group's type or position.
    /// </summary>
    /// <param name="syncRuleId">The unique identifier of the sync rule.</param>
    /// <param name="groupId">The unique identifier of the criteria group.</param>
    /// <param name="request">The update request.</param>
    /// <returns>The updated scoping criteria group.</returns>
    [HttpPut("sync-rules/{syncRuleId:int}/scoping-criteria/{groupId:int}", Name = "UpdateScopingCriteriaGroup")]
    [ProducesResponseType(typeof(SyncRuleScopingCriteriaGroupDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> UpdateScopingCriteriaGroupAsync(int syncRuleId, int groupId, [FromBody] UpdateScopingCriteriaGroupRequest request)
    {
        _logger.LogInformation("Updating scoping criteria group {GroupId} for sync rule: {SyncRuleId}", groupId, syncRuleId);

        var initiatedBy = await GetCurrentUserAsync();
        if (initiatedBy == null && !IsApiKeyAuthenticated())
            return Unauthorized(ApiErrorResponse.Unauthorised("Could not identify user from authentication token."));

        var syncRule = await _application.ConnectedSystems.GetSyncRuleAsync(syncRuleId);
        if (syncRule == null)
            return NotFound(ApiErrorResponse.NotFound($"Sync rule with ID {syncRuleId} not found."));

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
    /// Deletes a scoping criteria group and all its contents.
    /// </summary>
    /// <param name="syncRuleId">The unique identifier of the sync rule.</param>
    /// <param name="groupId">The unique identifier of the criteria group to delete.</param>
    /// <returns>No content on success.</returns>
    [HttpDelete("sync-rules/{syncRuleId:int}/scoping-criteria/{groupId:int}", Name = "DeleteScopingCriteriaGroup")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> DeleteScopingCriteriaGroupAsync(int syncRuleId, int groupId)
    {
        _logger.LogInformation("Deleting scoping criteria group {GroupId} for sync rule: {SyncRuleId}", groupId, syncRuleId);

        var initiatedBy = await GetCurrentUserAsync();
        if (initiatedBy == null && !IsApiKeyAuthenticated())
            return Unauthorized(ApiErrorResponse.Unauthorised("Could not identify user from authentication token."));

        var syncRule = await _application.ConnectedSystems.GetSyncRuleAsync(syncRuleId);
        if (syncRule == null)
            return NotFound(ApiErrorResponse.NotFound($"Sync rule with ID {syncRuleId} not found."));

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
    /// Adds a criterion to a scoping criteria group.
    /// </summary>
    /// <remarks>
    /// For Export sync rules: provide MetaverseAttributeId to evaluate MVO attributes.
    /// For Import sync rules: provide ConnectedSystemAttributeId to evaluate CSO attributes.
    /// </remarks>
    /// <param name="syncRuleId">The unique identifier of the sync rule.</param>
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
        _logger.LogInformation("Creating criterion in group {GroupId} for sync rule: {SyncRuleId}", groupId, syncRuleId);

        var initiatedBy = await GetCurrentUserAsync();
        if (initiatedBy == null && !IsApiKeyAuthenticated())
            return Unauthorized(ApiErrorResponse.Unauthorised("Could not identify user from authentication token."));

        var syncRule = await _application.ConnectedSystems.GetSyncRuleAsync(syncRuleId);
        if (syncRule == null)
            return NotFound(ApiErrorResponse.NotFound($"Sync rule with ID {syncRuleId} not found."));

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
            DateTimeValue = request.DateTimeValue,
            BoolValue = request.BoolValue,
            GuidValue = request.GuidValue,
            CaseSensitive = request.CaseSensitive
        };

        // Set the appropriate attribute based on sync rule direction
        if (syncRule.Direction == SyncRuleDirection.Export)
        {
            // Export rules evaluate Metaverse attributes
            if (!request.MetaverseAttributeId.HasValue)
                return BadRequest(ApiErrorResponse.BadRequest("MetaverseAttributeId is required for export sync rules."));

            var mvAttribute = await _application.Metaverse.GetMetaverseAttributeAsync(request.MetaverseAttributeId.Value);
            if (mvAttribute == null)
                return NotFound(ApiErrorResponse.NotFound($"Metaverse attribute with ID {request.MetaverseAttributeId} not found."));

            criterion.MetaverseAttribute = mvAttribute;
        }
        else
        {
            // Import rules evaluate Connected System attributes
            if (!request.ConnectedSystemAttributeId.HasValue)
                return BadRequest(ApiErrorResponse.BadRequest("ConnectedSystemAttributeId is required for import sync rules."));

            // Get the CS attribute from the sync rule's connected system object type
            var csAttribute = syncRule.ConnectedSystemObjectType?.Attributes
                .FirstOrDefault(a => a.Id == request.ConnectedSystemAttributeId.Value);

            if (csAttribute == null)
                return NotFound(ApiErrorResponse.NotFound($"Connected System attribute with ID {request.ConnectedSystemAttributeId} not found in sync rule's object type."));

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
    /// Deletes a criterion from a scoping criteria group.
    /// </summary>
    /// <param name="syncRuleId">The unique identifier of the sync rule.</param>
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
            return NotFound(ApiErrorResponse.NotFound($"Sync rule with ID {syncRuleId} not found."));

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
    /// Recursively finds a scoping criteria group by ID within a collection.
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
    /// Gets all object matching rules for a Connected System Object Type.
    /// </summary>
    /// <param name="connectedSystemId">The unique identifier of the connected system.</param>
    /// <param name="objectTypeId">The unique identifier of the object type.</param>
    /// <returns>A list of object matching rules.</returns>
    /// <response code="200">Returns the list of object matching rules.</response>
    /// <response code="404">Connected system or object type not found.</response>
    [HttpGet("connected-systems/{connectedSystemId:int}/object-types/{objectTypeId:int}/matching-rules", Name = "GetObjectMatchingRules")]
    [ProducesResponseType(typeof(IEnumerable<ObjectMatchingRuleDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetObjectMatchingRulesAsync(int connectedSystemId, int objectTypeId)
    {
        _logger.LogInformation("Getting object matching rules for connected system {SystemId}, object type {TypeId}", connectedSystemId, objectTypeId);

        var connectedSystem = await _application.ConnectedSystems.GetConnectedSystemAsync(connectedSystemId);
        if (connectedSystem == null)
            return NotFound(ApiErrorResponse.NotFound($"Connected system with ID {connectedSystemId} not found."));

        var objectType = connectedSystem.ObjectTypes?.FirstOrDefault(ot => ot.Id == objectTypeId);
        if (objectType == null)
            return NotFound(ApiErrorResponse.NotFound($"Object type with ID {objectTypeId} not found in connected system {connectedSystemId}."));

        var rules = objectType.ObjectMatchingRules
            .OrderBy(r => r.Order)
            .Select(ObjectMatchingRuleDto.FromEntity)
            .ToList();

        return Ok(rules);
    }

    /// <summary>
    /// Gets a specific object matching rule by ID.
    /// </summary>
    /// <param name="connectedSystemId">The unique identifier of the connected system.</param>
    /// <param name="ruleId">The unique identifier of the matching rule.</param>
    /// <returns>The object matching rule.</returns>
    /// <response code="200">Returns the object matching rule.</response>
    /// <response code="404">Connected system or matching rule not found.</response>
    [HttpGet("connected-systems/{connectedSystemId:int}/matching-rules/{ruleId:int}", Name = "GetObjectMatchingRule")]
    [ProducesResponseType(typeof(ObjectMatchingRuleDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetObjectMatchingRuleAsync(int connectedSystemId, int ruleId)
    {
        _logger.LogInformation("Getting object matching rule {RuleId} for connected system {SystemId}", ruleId, connectedSystemId);

        var connectedSystem = await _application.ConnectedSystems.GetConnectedSystemAsync(connectedSystemId);
        if (connectedSystem == null)
            return NotFound(ApiErrorResponse.NotFound($"Connected system with ID {connectedSystemId} not found."));

        var rule = await _application.Repository.ConnectedSystems.GetObjectMatchingRuleAsync(ruleId);
        if (rule == null)
            return NotFound(ApiErrorResponse.NotFound($"Object matching rule with ID {ruleId} not found."));

        // Verify the rule belongs to this connected system
        if (rule.ConnectedSystemObjectType?.ConnectedSystemId != connectedSystemId)
            return NotFound(ApiErrorResponse.NotFound($"Object matching rule with ID {ruleId} not found in connected system {connectedSystemId}."));

        return Ok(ObjectMatchingRuleDto.FromEntity(rule));
    }

    /// <summary>
    /// Creates a new object matching rule.
    /// </summary>
    /// <param name="connectedSystemId">The unique identifier of the connected system.</param>
    /// <param name="request">The rule creation request.</param>
    /// <returns>The created object matching rule.</returns>
    /// <response code="201">Object matching rule created successfully.</response>
    /// <response code="400">Invalid request data.</response>
    /// <response code="404">Connected system or referenced entities not found.</response>
    /// <response code="401">User could not be identified from authentication token.</response>
    [HttpPost("connected-systems/{connectedSystemId:int}/matching-rules", Name = "CreateObjectMatchingRule")]
    [ProducesResponseType(typeof(ObjectMatchingRuleDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> CreateObjectMatchingRuleAsync(int connectedSystemId, [FromBody] CreateObjectMatchingRuleRequest request)
    {
        _logger.LogInformation("Creating object matching rule for connected system {SystemId}", connectedSystemId);

        var initiatedBy = await GetCurrentUserAsync();
        if (initiatedBy == null && !IsApiKeyAuthenticated())
        {
            _logger.LogWarning("Could not identify user from JWT claims for matching rule creation");
            return Unauthorized(ApiErrorResponse.Unauthorised("Could not identify user from authentication token."));
        }

        var connectedSystem = await _application.ConnectedSystems.GetConnectedSystemAsync(connectedSystemId);
        if (connectedSystem == null)
            return NotFound(ApiErrorResponse.NotFound($"Connected system with ID {connectedSystemId} not found."));

        var objectType = connectedSystem.ObjectTypes?.FirstOrDefault(ot => ot.Id == request.ConnectedSystemObjectTypeId);
        if (objectType == null)
            return NotFound(ApiErrorResponse.NotFound($"Object type with ID {request.ConnectedSystemObjectTypeId} not found in connected system {connectedSystemId}."));

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

            _logger.LogInformation("Created object matching rule {RuleId} for connected system {SystemId}", rule.Id, connectedSystemId);

            return CreatedAtRoute("GetObjectMatchingRule",
                new { connectedSystemId, ruleId = rule.Id },
                ObjectMatchingRuleDto.FromEntity(rule));
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning(ex, "Failed to create object matching rule: {Message}", ex.Message);
            return BadRequest(ApiErrorResponse.BadRequest(ex.Message));
        }
    }

    /// <summary>
    /// Updates an existing object matching rule.
    /// </summary>
    /// <param name="connectedSystemId">The unique identifier of the connected system.</param>
    /// <param name="ruleId">The unique identifier of the matching rule.</param>
    /// <param name="request">The update request.</param>
    /// <returns>The updated object matching rule.</returns>
    /// <response code="200">Object matching rule updated successfully.</response>
    /// <response code="400">Invalid request data.</response>
    /// <response code="404">Connected system or matching rule not found.</response>
    /// <response code="401">User could not be identified from authentication token.</response>
    [HttpPut("connected-systems/{connectedSystemId:int}/matching-rules/{ruleId:int}", Name = "UpdateObjectMatchingRule")]
    [ProducesResponseType(typeof(ObjectMatchingRuleDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> UpdateObjectMatchingRuleAsync(int connectedSystemId, int ruleId, [FromBody] UpdateObjectMatchingRuleRequest request)
    {
        _logger.LogInformation("Updating object matching rule {RuleId} for connected system {SystemId}", ruleId, connectedSystemId);

        var initiatedBy = await GetCurrentUserAsync();
        if (initiatedBy == null && !IsApiKeyAuthenticated())
        {
            _logger.LogWarning("Could not identify user from JWT claims for matching rule update");
            return Unauthorized(ApiErrorResponse.Unauthorised("Could not identify user from authentication token."));
        }

        var connectedSystem = await _application.ConnectedSystems.GetConnectedSystemAsync(connectedSystemId);
        if (connectedSystem == null)
            return NotFound(ApiErrorResponse.NotFound($"Connected system with ID {connectedSystemId} not found."));

        var rule = await _application.Repository.ConnectedSystems.GetObjectMatchingRuleAsync(ruleId);
        if (rule == null)
            return NotFound(ApiErrorResponse.NotFound($"Object matching rule with ID {ruleId} not found."));

        // Verify the rule belongs to this connected system
        if (rule.ConnectedSystemObjectType?.ConnectedSystemId != connectedSystemId)
            return NotFound(ApiErrorResponse.NotFound($"Object matching rule with ID {ruleId} not found in connected system {connectedSystemId}."));

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
            await _application.ConnectedSystems.UpdateObjectMatchingRuleAsync(rule, initiatedBy);

            _logger.LogInformation("Updated object matching rule {RuleId} for connected system {SystemId}", ruleId, connectedSystemId);

            return Ok(ObjectMatchingRuleDto.FromEntity(rule));
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning(ex, "Failed to update object matching rule: {Message}", ex.Message);
            return BadRequest(ApiErrorResponse.BadRequest(ex.Message));
        }
    }

    /// <summary>
    /// Deletes an object matching rule.
    /// </summary>
    /// <param name="connectedSystemId">The unique identifier of the connected system.</param>
    /// <param name="ruleId">The unique identifier of the matching rule.</param>
    /// <returns>No content on success.</returns>
    /// <response code="204">Object matching rule deleted successfully.</response>
    /// <response code="404">Connected system or matching rule not found.</response>
    /// <response code="401">User could not be identified from authentication token.</response>
    [HttpDelete("connected-systems/{connectedSystemId:int}/matching-rules/{ruleId:int}", Name = "DeleteObjectMatchingRule")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> DeleteObjectMatchingRuleAsync(int connectedSystemId, int ruleId)
    {
        _logger.LogInformation("Deleting object matching rule {RuleId} for connected system {SystemId}", ruleId, connectedSystemId);

        var initiatedBy = await GetCurrentUserAsync();
        if (initiatedBy == null && !IsApiKeyAuthenticated())
        {
            _logger.LogWarning("Could not identify user from JWT claims for matching rule deletion");
            return Unauthorized(ApiErrorResponse.Unauthorised("Could not identify user from authentication token."));
        }

        var connectedSystem = await _application.ConnectedSystems.GetConnectedSystemAsync(connectedSystemId);
        if (connectedSystem == null)
            return NotFound(ApiErrorResponse.NotFound($"Connected system with ID {connectedSystemId} not found."));

        var rule = await _application.Repository.ConnectedSystems.GetObjectMatchingRuleAsync(ruleId);
        if (rule == null)
            return NotFound(ApiErrorResponse.NotFound($"Object matching rule with ID {ruleId} not found."));

        // Verify the rule belongs to this connected system
        if (rule.ConnectedSystemObjectType?.ConnectedSystemId != connectedSystemId)
            return NotFound(ApiErrorResponse.NotFound($"Object matching rule with ID {ruleId} not found in connected system {connectedSystemId}."));

        await _application.ConnectedSystems.DeleteObjectMatchingRuleAsync(rule, initiatedBy);

        _logger.LogInformation("Deleted object matching rule {RuleId} for connected system {SystemId}", ruleId, connectedSystemId);

        return NoContent();
    }

    #endregion

    #region Expression Testing

    /// <summary>
    /// Tests an expression with sample attribute data.
    /// </summary>
    /// <param name="request">The test expression request containing the expression and sample attribute values.</param>
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
        _logger.LogDebug("Testing expression: {Expression}", request.Expression);

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
    /// Checks if a container belongs to a connected system, traversing the parent container chain if necessary.
    /// </summary>
    /// <param name="container">The container to check.</param>
    /// <param name="connectedSystemId">The connected system ID to check against.</param>
    /// <returns>True if the container belongs to the connected system.</returns>
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
