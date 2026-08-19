// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using Asp.Versioning;
using JIM.Web.Extensions.Api;
using JIM.Web.Models.Api;
using JIM.Application;
using JIM.Application.Interfaces;
using JIM.Models.Core;
using JIM.Models.Exceptions;
using JIM.Models.Expressions;
using JIM.Models.Interfaces;
using JIM.Application.Services;
using JIM.Models.Activities;
using JIM.Models.Connectors;
using JIM.Models.Activities.DTOs;
using JIM.Models.Logic;
using JIM.Models.Logic.DTOs;
using JIM.Models.Preview;
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
    ICredentialProtectionService credentialProtection) : ApiControllerBase(application, logger)
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
        var configurationDrift = await _application.ConfigurationDrift.GetConnectedSystemDriftAsync(connectedSystemId);
        var initialPasswordAttention = await _application.InitialPasswords.GetAttentionByConnectedSystemAsync([connectedSystemId]);

        return Ok(ConnectedSystemDetailDto.FromEntity(system, pendingExportCount, objectCount, configurationDrift,
            initialPasswordAttention.GetValueOrDefault(connectedSystemId) ?? new InitialPasswordAttention()));
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
    /// Get a single Object Type for a Connected System
    /// </summary>
    /// <param name="connectedSystemId">The unique identifier of the Connected System.</param>
    /// <param name="objectTypeId">The unique identifier of the Object Type.</param>
    /// <returns>The Object Type with its Attributes.</returns>
    /// <response code="200">The Object Type was found and returned.</response>
    /// <response code="404">Connected System or Object Type not found.</response>
    /// <response code="401">User could not be identified from authentication token.</response>
    [HttpGet("connected-systems/{connectedSystemId:int}/object-types/{objectTypeId:int}", Name = "GetConnectedSystemObjectType")]
    [ProducesResponseType(typeof(ConnectedSystemObjectTypeDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetConnectedSystemObjectTypeAsync(int connectedSystemId, int objectTypeId)
    {
        _logger.LogTrace("Requested object type {ObjectTypeId} for Connected System {SystemId}", objectTypeId, connectedSystemId);

        // Verify Connected System exists (Core retrieval; we only need existence, not the full graph)
        var connectedSystem = await _application.ConnectedSystems.GetConnectedSystemCoreAsync(connectedSystemId);
        if (connectedSystem == null)
            return NotFound(ApiErrorResponse.NotFound($"Connected System with ID {connectedSystemId} not found."));

        // GetObjectTypeAsync includes the Attributes and ConnectedSystem navigations
        var objectType = await _application.ConnectedSystems.GetObjectTypeAsync(objectTypeId);
        if (objectType == null || objectType.ConnectedSystemId != connectedSystemId)
            return NotFound(ApiErrorResponse.NotFound($"Object type with ID {objectTypeId} not found in Connected System {connectedSystemId}."));

        return Ok(ConnectedSystemObjectTypeDto.FromEntity(objectType));
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
        try
        {
            if (apiKey != null)
                await _application.ConnectedSystems.UpdateObjectTypeAsync(objectType, apiKey);
            else
                await _application.ConnectedSystems.UpdateObjectTypeAsync(objectType, initiatedBy);
        }
        catch (InvalidSettingValuesException ex)
        {
            // the Connector refused the selection against the Connected System's settings (a Delta Import Mode the
            // Object Type is not equipped for, say); the message is the Connector's own and names what to change.
            _logger.LogInformation("Object type {ObjectTypeId} ({Name}) selection refused: {Reason}", objectType.Id, objectType.Name, ex.Message);
            return BadRequest(ApiErrorResponse.BadRequest(ex.Message));
        }

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
    /// - Type: Overrides the data type schema discovery inferred, where the Connector supports it
    ///
    /// A data type override is accepted only where the Connector declares
    /// SupportsUserSelectedAttributeTypes, and only while the attribute is neither referenced by a
    /// Synchronisation Rule nor holding values.
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

        // Validate: a credential attribute can never be managed by JIM. It either cannot be read back meaningfully
        // or holds credential material that must never enter the Metaverse; passwords are handled by JIM's
        // dedicated write-only password channel instead of Attribute Flow. Deselecting one stays allowed.
        if (CredentialAttributes.IsCredentialAttribute(attribute.Name) &&
            (request.Selected == true || request.IsExternalId == true || request.IsSecondaryExternalId == true))
        {
            _logger.LogWarning("Attempted to select credential attribute {AttributeId} ({Name})", attributeId, LogSanitiser.Sanitise(attribute.Name));
            return BadRequest(ApiErrorResponse.BadRequest(
                $"Attribute '{attribute.Name}' holds credential material and cannot be managed by JIM. " +
                "Passwords are synchronised through JIM's dedicated password channel, not through Attribute Flow."));
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

        // Validate: the data type may only be overridden where the Connector says its schema cannot state
        // one definitively, and only while nothing has been built on the type JIM inferred. A delimited file
        // names no types at all; Oracle has a single numeric type, so NUMBER(10) may be a whole number, a
        // counter or a fractional figure and only the administrator knows which (#1354).
        if (request.Type.HasValue)
        {
            if (request.Type.Value == AttributeDataType.NotSet)
            {
                return BadRequest(ApiErrorResponse.BadRequest(
                    "An attribute's data type cannot be set to NotSet. Choose the type the Connected System actually holds."));
            }

            // An absent Connector Definition is treated as not supporting the override: refusing a change
            // JIM cannot justify is always safer than applying one it cannot check.
            if (connectedSystem.ConnectorDefinition?.SupportsUserSelectedAttributeTypes != true)
            {
                _logger.LogWarning("Attempted to change the data type of attribute {AttributeId} on a Connector that does not support it", attributeId);
                return BadRequest(ApiErrorResponse.BadRequest(
                    $"The '{connectedSystem.ConnectorDefinition?.Name ?? connectedSystem.Name}' Connector states each attribute's data type from the " +
                    "Connected System's own schema, so it cannot be overridden."));
            }

            if (request.Type.Value != attribute.Type &&
                await _application.ConnectedSystems.IsObjectTypeAttributeBeingReferencedAsync(attribute))
            {
                _logger.LogWarning("Attempted to change the data type of referenced attribute {AttributeId} ({Name})", attributeId, LogSanitiser.Sanitise(attribute.Name));
                return BadRequest(ApiErrorResponse.BadRequest(
                    $"The data type of attribute '{attribute.Name}' cannot be changed because it is referenced by a Synchronisation Rule, or already holds values. " +
                    "Changing it would reinterpret data imported under the previous type. Remove the references, or clear the Connected System Objects, and try again."));
            }
        }

        // Apply updates
        if (request.Selected.HasValue)
            attribute.Selected = request.Selected.Value;

        if (request.Type.HasValue)
        {
            attribute.Type = request.Type.Value;

            // Recorded, not inferred from the value being different: a schema refresh must leave this type
            // alone even where the administrator happened to choose what discovery would have picked anyway,
            // because the Connector's inference can change between releases.
            attribute.TypeSetByAdministrator = true;
        }

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

        // A data type override is refused here rather than dropped. Bulk update applies its changes through a
        // single application-layer call that carries only the three selection flags, so honouring Type would
        // need that contract widened; accepting and ignoring it would let a scripted build report success
        // having changed nothing. The single-attribute endpoint applies it, one attribute at a time (#1354).
        var typeOverrides = request.Attributes.Where(kvp => kvp.Value.Type.HasValue).Select(kvp => kvp.Key).ToList();
        if (typeOverrides.Count > 0)
        {
            _logger.LogWarning("Rejected bulk attribute update carrying {Count} data type override(s)", typeOverrides.Count);
            return BadRequest(ApiErrorResponse.BadRequest(
                $"A data type cannot be set through a bulk attribute update ({typeOverrides.Count} attribute(s) requested one). " +
                "Update each attribute individually via PUT connected-systems/{connectedSystemId}/object-types/{objectTypeId}/attributes/{attributeId}."));
        }

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
    /// Get the password policy JIM discovered on a Connected System
    /// </summary>
    /// <remarks>
    /// What the system itself said it will accept, read during a previous connection. Nothing here opens a new
    /// connection or changes anything.
    ///
    /// Every field is nullable, and a null means JIM could not read that rule rather than that no such rule
    /// exists: a directory withholds what a caller may not see by omitting it rather than refusing. Check
    /// `hasAnyDiscoveredConstraint` before treating the figures as a description of what the system will accept.
    /// Where the domain has policies applying to only some accounts, the figures are a floor rather than a
    /// guarantee; `fineGrainedPolicySignal` says which case this is.
    /// </remarks>
    /// <param name="connectedSystemId">The unique identifier of the Connected System.</param>
    /// <response code="200">The discovered policy, or an empty one where nothing has been discovered.</response>
    /// <response code="404">No such Connected System.</response>
    [HttpGet("connected-systems/{connectedSystemId:int}/password-policy", Name = "GetConnectedSystemPasswordPolicy")]
    [ProducesResponseType(typeof(ConnectedSystemPasswordPolicyResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetConnectedSystemPasswordPolicyAsync(int connectedSystemId)
    {
        var system = await _application.ConnectedSystems.GetConnectedSystemCoreAsync(connectedSystemId);
        if (system == null)
            return NotFound(ApiErrorResponse.NotFound($"Connected System with ID {connectedSystemId} not found."));

        var policy = await _application.ConnectedSystems.GetPasswordPolicyAsync(connectedSystemId);

        // A system with nothing discovered is reported as such rather than as a 404: the system exists, and "we
        // could not read its policy" is a different answer from "there is no such system".
        return Ok(ConnectedSystemPasswordPolicyResponse.FromEntity(policy));
    }

    /// <summary>
    /// Generate a password that satisfies a Connected System's discovered policy
    /// </summary>
    /// <remarks>
    /// Produces a password and returns it. Nothing is set, staged or stored: the value exists in this response
    /// and nowhere else, and JIM cannot give it to you again.
    ///
    /// **This is the only endpoint in JIM whose response body carries a password**, and that is deliberate. What
    /// JIM never does is store a password, or return one nobody asked for; here the caller asked and is the only
    /// party that can use it, so withholding it would make the call pointless. The response is marked
    /// `no-store` so nothing between JIM and the caller keeps a copy.
    ///
    /// Pass the result to the set-password endpoint to apply it. The point of asking JIM rather than inventing
    /// one is that JIM knows what the target demands: `satisfiesDiscoveredPolicy` says whether the result was
    /// checked against a real policy, and is false where there is none to check against rather than where the
    /// password failed one.
    /// </remarks>
    /// <param name="connectedSystemId">The unique identifier of the Connected System.</param>
    /// <response code="200">The generated password, and what JIM can say about it.</response>
    /// <response code="404">No such Connected System.</response>
    [HttpPost("connected-systems/{connectedSystemId:int}/generate-password", Name = "GenerateConnectedSystemPassword")]
    [ProducesResponseType(typeof(GeneratedPasswordResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GenerateConnectedSystemPasswordAsync(int connectedSystemId)
    {
        // Logs that a password was generated and for which system, never anything about the value, including
        // its length.
        _logger.LogInformation("Generating a password against the discovered policy of Connected System {ConnectedSystemId}", connectedSystemId);

        var system = await _application.ConnectedSystems.GetConnectedSystemCoreAsync(connectedSystemId);
        if (system == null)
            return NotFound(ApiErrorResponse.NotFound($"Connected System with ID {connectedSystemId} not found."));

        var discoveredPolicy = await _application.ConnectedSystems.GetPasswordPolicyAsync(connectedSystemId);
        var generationPolicy = _application.PasswordGenerator.DeriveFrom(discoveredPolicy);

        var password = _application.PasswordGenerator.Generate(generationPolicy);
        var assessment = _application.PasswordGenerator.Assess(generationPolicy, discoveredPolicy);

        // The one response in JIM that must not be kept by anything on its way back to the caller.
        Response.Headers.CacheControl = "no-store, no-cache, max-age=0";
        Response.Headers.Pragma = "no-cache";

        return Ok(GeneratedPasswordResponse.FromGenerated(password, assessment, discoveredPolicy?.HasAnyDiscoveredConstraint == true));
    }


    /// <summary>
    /// Generate one password that satisfies every named Connected System
    /// </summary>
    /// <remarks>
    /// The counterpart of the single-system generate, for setting one password across a person's accounts. JIM
    /// reconciles the systems' discovered policies into one set of rules and generates against that: the longest
    /// minimum length any of them demands, and only the character categories all of them count, since a category
    /// one system does not recognise cannot help satisfy another's complexity rule.
    ///
    /// This is the case that most needs JIM rather than the caller: an administrator would otherwise have to
    /// guess a password acceptable to the strictest of several systems whose policies they cannot see.
    ///
    /// Where no single password can satisfy them all, that is reported as a refusal rather than by handing back
    /// a password that would be accepted on the first account and refused on the second, after the first has
    /// already been changed. A system JIM could read nothing from is named in the response rather than passed
    /// over: the password is about to be set there and JIM cannot promise it will be accepted.
    ///
    /// As with the single-system generate, the response body carries the password, is marked `no-store`, and
    /// nothing about the value is written down or logged.
    /// </remarks>
    /// <param name="request">The Connected Systems the password has to work on.</param>
    /// <response code="200">The generated password, and what JIM can say about it.</response>
    /// <response code="400">No Connected Systems were named, or their policies cannot be reconciled.</response>
    /// <response code="404">One of the named Connected Systems does not exist.</response>
    [HttpPost("connected-systems/generate-password", Name = "GeneratePasswordForSystems")]
    [ProducesResponseType(typeof(GeneratedPasswordResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GeneratePasswordForSystemsAsync([FromBody] GeneratePasswordForSystemsRequest request)
    {
        if (request.ConnectedSystemIds.Count == 0)
            return BadRequest(ApiErrorResponse.BadRequest("At least one Connected System is required."));

        _logger.LogInformation("Generating a password against the reconciled policies of {Count} Connected Systems",
            request.ConnectedSystemIds.Count);

        var policies = new List<PasswordPolicyForSystem>();
        foreach (var connectedSystemId in request.ConnectedSystemIds.Distinct())
        {
            var system = await _application.ConnectedSystems.GetConnectedSystemCoreAsync(connectedSystemId);
            if (system == null)
                return NotFound(ApiErrorResponse.NotFound($"Connected System with ID {connectedSystemId} not found."));

            policies.Add(new PasswordPolicyForSystem
            {
                ConnectedSystemName = system.Name,
                Policy = await _application.ConnectedSystems.GetPasswordPolicyAsync(connectedSystemId)
            });
        }

        var reconciliation = _application.PasswordGenerator.Reconcile(policies);
        if (!reconciliation.IsUsable)
            return BadRequest(ApiErrorResponse.BadRequest(
                "No single password can satisfy every named Connected System: " + string.Join(" ", reconciliation.Conflicts)));

        var password = _application.PasswordGenerator.Generate(reconciliation.Policy);
        var assessment = _application.PasswordGenerator.Assess(reconciliation.Policy, targetPolicy: null);

        Response.Headers.CacheControl = "no-store, no-cache, max-age=0";
        Response.Headers.Pragma = "no-cache";

        return Ok(GeneratedPasswordResponse.FromReconciled(password, assessment, reconciliation));
    }

    /// <summary>
    /// Set the password on a Connected System Object
    /// </summary>
    /// <remarks>
    /// Writes the password straight to the Connected System. Nothing is staged, retried or stored: there is
    /// nowhere in JIM to keep a password and no second attempt worth keeping one for. The attempt is recorded as
    /// an Activity against the object, carrying the outcome and, where the target refused, its verbatim reason.
    ///
    /// The password is supplied by the caller. To have JIM produce one that satisfies what the Connected System
    /// itself demands, call the generate endpoint first and pass the result here.
    ///
    /// This resets the password on whichever account it is pointed at: an administrator who can call it can
    /// reset any account in this connector space, subject only to what the Connected System's own service
    /// account is permitted to do.
    /// </remarks>
    /// <param name="connectedSystemId">The unique identifier of the Connected System.</param>
    /// <param name="csoId">The unique identifier (GUID) of the Connected System Object.</param>
    /// <param name="request">The password to set, and how to apply it.</param>
    /// <response code="200">The password was set. The body reports the expiry behaviour actually applied.</response>
    /// <response code="400">The password was empty, the Connector cannot set passwords, or the Connected System refused the password. The reason is the target's own where there is one.</response>
    /// <response code="404">No such Connected System, or no such object within it.</response>
    /// <response code="401">User could not be identified from authentication token.</response>
    /// <response code="502">The Connected System could not be reached, so it is not known whether the password would be accepted. Try again.</response>
    [HttpPost("connected-systems/{connectedSystemId:int}/connector-space/{csoId:guid}/password", Name = "SetConnectedSystemObjectPassword")]
    [ProducesResponseType(typeof(SetConnectedSystemObjectPasswordResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status502BadGateway)]
    public async Task<IActionResult> SetConnectedSystemObjectPasswordAsync(int connectedSystemId, Guid csoId, [FromBody] SetConnectedSystemObjectPasswordRequest request)
    {
        // Deliberately logs the object rather than anything about the password. There is nothing about a
        // password value that belongs in a log line, including its length.
        _logger.LogInformation("Setting the password on Connected System Object {CsoId} in Connected System {ConnectedSystemId}", csoId, connectedSystemId);

        var initiatedBy = await GetCurrentUserAsync();
        if (initiatedBy == null && !IsApiKeyAuthenticated())
        {
            _logger.LogWarning("Could not identify user from JWT claims for a Connected System Object password set");
            return Unauthorized(ApiErrorResponse.Unauthorised("Could not identify user from authentication token."));
        }

        if (string.IsNullOrWhiteSpace(request.Password))
            return BadRequest(ApiErrorResponse.BadRequest("A password is required."));

        var requestedExpiryBehaviour = request.ExpiryBehaviour ?? PasswordExpiryBehaviour.RequireChangeAtNextSignIn;
        var options = new PasswordSetOptions
        {
            ExpiryBehaviour = requestedExpiryBehaviour,
            EnableAccount = request.EnableAccount
        };

        PasswordSetResult result;
        try
        {
            var apiKey = await GetCurrentApiKeyAsync();
            result = apiKey != null
                ? await _application.ConnectedSystems.SetConnectedSystemObjectPasswordAsync(connectedSystemId, csoId, request.Password, options, apiKey, HttpContext.RequestAborted)
                : await _application.ConnectedSystems.SetConnectedSystemObjectPasswordAsync(connectedSystemId, csoId, request.Password, options, initiatedBy, HttpContext.RequestAborted);
        }
        catch (ArgumentException ex)
        {
            return NotFound(ApiErrorResponse.NotFound(ex.Message));
        }
        catch (NotSupportedException ex)
        {
            return BadRequest(ApiErrorResponse.BadRequest(ex.Message));
        }

        if (result.Success)
            return Ok(SetConnectedSystemObjectPasswordResponse.FromResult(result, requestedExpiryBehaviour));

        var reason = result.ErrorMessage ?? "The Connected System refused the password without saying why.";
        return result.FailureReason switch
        {
            // An account that is not there yet is a 404 like any other missing resource, and is commonly just
            // replication: the caller's move is to wait and repeat the request, not to change the password.
            PasswordSetFailureReason.TargetObjectNotFound => NotFound(ApiErrorResponse.NotFound(reason)),
            // Nothing was established about the password itself, so this must not read as a rejection of it.
            PasswordSetFailureReason.Transient => StatusCode(StatusCodes.Status502BadGateway, ApiErrorResponse.BadGateway(reason)),
            _ => BadRequest(ApiErrorResponse.BadRequest(reason))
        };
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
        var count = await _application.ConnectedSystems.GetConnectedSystemObjectCountAsync(
            connectedSystemId, objectTypeId, partitionId);
        return Ok(count);
    }

    /// <summary>
    /// List Connected System Objects
    /// </summary>
    /// <remarks>
    /// Returns lightweight header objects. Use the detail endpoint to retrieve full Attribute values for a specific Connected System Object.
    /// </remarks>
    /// <param name="connectedSystemId">The unique identifier of the Connected System.</param>
    /// <param name="page">Page number (1-based). Default: 1.</param>
    /// <param name="pageSize">Number of items per page (1-100). Default: 50.</param>
    /// <param name="search">Optional search text to filter by display name or external ID.</param>
    /// <param name="sortBy">Optional property name to sort by.</param>
    /// <param name="sortDescending">Whether to sort descending. Default: true.</param>
    /// <param name="status">Optional Connected System Object status values to filter by. Repeat the parameter for multiple values.</param>
    /// <param name="objectTypeId">Optional Object Type IDs to filter by. Repeat the parameter for multiple values.</param>
    /// <param name="joinType">Optional join type values to filter by. Repeat the parameter for multiple values.</param>
    /// <returns>A paginated set of Connected System Object headers.</returns>
    [HttpGet("connected-systems/{connectedSystemId:int}/connector-space", Name = "GetConnectedSystemObjects")]
    [ProducesResponseType(typeof(PaginatedResponse<ConnectedSystemObjectHeader>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetConnectedSystemObjectsAsync(
        int connectedSystemId,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        [FromQuery] string? search = null,
        [FromQuery] string? sortBy = null,
        [FromQuery] bool sortDescending = true,
        [FromQuery] List<ConnectedSystemObjectStatus>? status = null,
        [FromQuery] List<int>? objectTypeId = null,
        [FromQuery] List<ConnectedSystemObjectJoinType>? joinType = null)
    {
        if (page < 1) page = 1;
        if (pageSize < 1) pageSize = 50;
        if (pageSize > 100) pageSize = 100;

        var connectedSystem = await _application.ConnectedSystems.GetConnectedSystemCoreAsync(connectedSystemId);
        if (connectedSystem == null)
            return NotFound(ApiErrorResponse.NotFound($"Connected System with ID {connectedSystemId} not found."));

        var result = await _application.ConnectedSystems.GetConnectedSystemObjectHeadersAsync(
            connectedSystemId, page, pageSize, search, sortBy, sortDescending, status, objectTypeId, joinType);

        return Ok(PaginatedResponse<ConnectedSystemObjectHeader>.Create(
            result.Results, result.TotalResults, result.CurrentPage, result.PageSize));
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
        var count = await _application.ConnectedSystems.GetPendingExportsFilteredCountAsync(
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

        // Partition selection is configuration; the server records the change with an Activity and a versioned snapshot.
        var apiKey = await GetCurrentApiKeyAsync();
        if (apiKey != null)
            await _application.ConnectedSystems.UpdateConnectedSystemPartitionAsync(partition, connectedSystemId, apiKey);
        else
            await _application.ConnectedSystems.UpdateConnectedSystemPartitionAsync(partition, connectedSystemId, initiatedBy);

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
    /// <response code="400">The request would leave the Container both selected and excluded.</response>
    /// <response code="404">Connected System or Container not found.</response>
    /// <response code="401">User could not be identified from authentication token.</response>
    [HttpPut("connected-systems/{connectedSystemId:int}/containers/{containerId:int}", Name = "UpdateConnectedSystemContainer")]
    [ProducesResponseType(typeof(ConnectedSystemContainerDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
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

        // A Container states one thing about itself, and "manage this" and "do not manage this" cannot both be it.
        // The portal keeps the two apart by construction, so this is the only surface that can ask for both, and it
        // refuses rather than picking one: guessing which half the caller meant is how a branch ends up imported
        // that an administrator excluded. Evaluated against the state the request would leave behind, because a
        // request naming one half against a stored other is just as contradictory as one naming both.
        var wouldBeSelected = request.Selected ?? container.Selected;
        var wouldBeExcluded = request.Excluded ?? container.Excluded;
        if (wouldBeSelected && wouldBeExcluded)
        {
            return BadRequest(ApiErrorResponse.BadRequest(
                $"Container with ID {containerId} cannot be both selected and excluded. Send Selected as false in the same request to replace the selection with an exclusion, or Excluded as false to replace the exclusion with a selection."));
        }

        // Apply updates
        if (request.Selected.HasValue)
            container.Selected = request.Selected.Value;

        if (request.Excluded.HasValue)
            container.Excluded = request.Excluded.Value;

        if (request.Scope.HasValue)
            container.Scope = request.Scope.Value;

        // Container selection is configuration; the server records the change with an Activity and a versioned snapshot.
        var apiKey = await GetCurrentApiKeyAsync();
        if (apiKey != null)
            await _application.ConnectedSystems.UpdateConnectedSystemContainerAsync(container, connectedSystemId, apiKey);
        else
            await _application.ConnectedSystems.UpdateConnectedSystemContainerAsync(container, connectedSystemId, initiatedBy);

        // Reload to get full entity with relationships
        var updated = await _application.ConnectedSystems.GetConnectedSystemContainerAsync(containerId);
        return Ok(ConnectedSystemContainerDto.FromEntity(updated!));
    }

    /// <summary>
    /// Read a Connected System's Container Scope as text (Advanced Mode)
    /// </summary>
    /// <remarks>
    /// One statement per line, in hierarchy order: <c>include</c> or <c>exclude</c>, an optional <c>one-level</c>,
    /// then the Container's path. This is the canonical form, so text read here and sent straight back to
    /// <c>PUT connected-systems/{id}/container-scope-text</c> leaves the scope exactly as it was.
    ///
    /// A Connected System with nothing selected returns empty text rather than nothing at all.
    /// </remarks>
    /// <param name="connectedSystemId">The unique identifier of the Connected System.</param>
    /// <response code="200">The Container Scope, as text.</response>
    /// <response code="404">Connected System not found.</response>
    /// <response code="401">User not authenticated.</response>
    [HttpGet("connected-systems/{connectedSystemId:int}/container-scope-text", Name = "GetConnectedSystemContainerScopeText")]
    [ProducesResponseType(typeof(ConnectedSystemContainerScopeTextDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetConnectedSystemContainerScopeTextAsync(int connectedSystemId)
    {
        _logger.LogTrace("Requested Container Scope text for Connected System: {Id}", connectedSystemId);

        var text = await _application.ConnectedSystems.GetContainerScopeTextAsync(connectedSystemId);
        if (text == null)
            return NotFound(ApiErrorResponse.NotFound($"Connected System with ID {connectedSystemId} not found."));

        return Ok(new ConnectedSystemContainerScopeTextDto { Text = text });
    }

    /// <summary>
    /// State a Connected System's Container Scope as text (Advanced Mode)
    /// </summary>
    /// <remarks>
    /// Replaces the whole of Container Scope with what the text states, which is what makes it usable on a
    /// hierarchy too large to click through: a Container the text does not name states nothing, so empty text
    /// clears every selection and exclusion. Partition selection is left alone, except that naming a Container
    /// selects the partition holding it.
    ///
    /// Applied all-or-nothing, because a scope applied halfway takes objects out of import scope without anyone
    /// asking for it. A path naming no Container, a Container named twice, and a statement an ancestor already
    /// makes are each refused with the line that caused them, and nothing is changed. The response reports the
    /// canonical text now in force.
    ///
    /// The change is recorded as one Activity and one versioned configuration snapshot, exactly as saving the tree
    /// in the portal is. Preview what it would cost first with
    /// <c>POST connected-systems/{id}/scope-selection/preview</c>.
    /// </remarks>
    /// <param name="connectedSystemId">The unique identifier of the Connected System.</param>
    /// <param name="request">The Container Scope to apply.</param>
    /// <response code="200">The Container Scope was applied. The response carries the canonical text.</response>
    /// <response code="400">The text could not be applied. Nothing was changed.</response>
    /// <response code="404">Connected System not found.</response>
    /// <response code="401">User could not be identified from authentication token.</response>
    [HttpPut("connected-systems/{connectedSystemId:int}/container-scope-text", Name = "UpdateConnectedSystemContainerScopeText")]
    [ProducesResponseType(typeof(ConnectedSystemContainerScopeTextDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> UpdateConnectedSystemContainerScopeTextAsync(int connectedSystemId,
        [FromBody] UpdateConnectedSystemContainerScopeTextRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        _logger.LogInformation("Applying Container Scope text to Connected System {Id}", connectedSystemId);

        var initiatedBy = await GetCurrentUserAsync();
        if (initiatedBy == null && !IsApiKeyAuthenticated())
        {
            _logger.LogWarning("Could not identify user from JWT claims for Container Scope text update");
            return Unauthorized(ApiErrorResponse.Unauthorised("Could not identify user from authentication token."));
        }

        var apiKey = await GetCurrentApiKeyAsync();
        var result = apiKey != null
            ? await _application.ConnectedSystems.ApplyContainerScopeTextAsync(connectedSystemId, request.Text, apiKey)
            : await _application.ConnectedSystems.ApplyContainerScopeTextAsync(connectedSystemId, request.Text, initiatedBy);

        if (result == null)
            return NotFound(ApiErrorResponse.NotFound($"Connected System with ID {connectedSystemId} not found."));

        if (!result.Applied)
        {
            // Every problem at once, each tied to its line: an administrator correcting a scope of any size one
            // round trip at a time is how a half-corrected text gets saved.
            var detail = string.Join(" ", result.Errors.Select(e => $"line {e.LineNumber}: {e.Message}"));
            return BadRequest(ApiErrorResponse.BadRequest(
                $"The Container Scope text could not be applied, so nothing was changed. {detail}"));
        }

        return Ok(new ConnectedSystemContainerScopeTextDto { Text = result.Text });
    }

    /// <summary>
    /// Preview a change to a Connected System's partition and container selection
    /// </summary>
    /// <remarks>
    /// Answers what a proposed selection would do, without making it (#827/#1251): which Connected System Objects
    /// would leave import scope, which of those are joined and would disconnect from their Metaverse Object, which
    /// would come back into scope, and which Metaverse Objects would become eligible for automatic deletion once
    /// the disconnections land.
    ///
    /// This matters because a deselection is silently destructive. The objects beneath a deselected container stop
    /// being searched, so the next Full Import does not return them, so they are marked obsolete, and the following
    /// synchronisation disconnects them and recalls whatever they contributed to the Metaverse.
    ///
    /// Send the whole selection, not one flag: what a deselection costs depends on the rest of the selection, since
    /// an object leaves scope only when nothing else still covers it. Omitted lists preview the stored selection.
    /// Apply the previewed change through <c>PUT connected-systems/{id}/partitions/{partitionId}</c> and
    /// <c>PUT connected-systems/{id}/containers/{containerId}</c>.
    ///
    /// Evaluation is asynchronous. This returns as soon as the proposal itself has been validated, with the
    /// Activity id to poll; read progress and results from <c>GET /previews/{activityId}</c>, drill-down rows from
    /// <c>GET /previews/{activityId}/deltas</c>, and abandon a running preview with
    /// <c>DELETE /previews/{activityId}</c>.
    ///
    /// A selection that leaves nothing manageable comes back with a validation finding and is still evaluated; that
    /// is the answer the caller asked for, not a reason to refuse the request.
    /// </remarks>
    /// <param name="connectedSystemId">The unique identifier of the Connected System.</param>
    /// <param name="request">The proposed selection.</param>
    /// <response code="202">The preview was started. Poll the returned Activity id for results.</response>
    /// <response code="400">A named partition or container does not belong to this Connected System.</response>
    /// <response code="404">Connected System not found.</response>
    /// <response code="401">User not authenticated.</response>
    [HttpPost("connected-systems/{connectedSystemId:int}/scope-selection/preview", Name = "StartConnectedSystemScopeSelectionPreview")]
    [ProducesResponseType(typeof(ConfigurationChangePreviewStartResponse), StatusCodes.Status202Accepted)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> StartConnectedSystemScopeSelectionPreviewAsync(int connectedSystemId,
        [FromBody] StartConnectedSystemScopeSelectionPreviewRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var connectedSystem = await _application.ConnectedSystems.GetConnectedSystemAsync(connectedSystemId);
        if (connectedSystem == null)
            return NotFound(ApiErrorResponse.NotFound($"Connected System with ID {connectedSystemId} not found."));

        var currentSelection = ConnectedSystemScopeSelectionProposal.FromCurrentSelection(connectedSystem);

        // An omitted list means "leave this half of the selection as it stands", for all three: a caller changing
        // only the containers has not asked to lift every exclusion, and reading silence that way would preview
        // objects flooding back into scope. An explicitly empty list is a different statement and is honoured.
        var proposal = new ConnectedSystemScopeSelectionProposal(
            request.SelectedPartitionIds ?? currentSelection.SelectedPartitionIds,
            request.SelectedContainerIds ?? currentSelection.SelectedContainerIds,
            request.ExcludedContainerIds ?? currentSelection.ExcludedContainerIds);

        // An id naming nothing in this hierarchy is different in kind from a selection the preview disagrees with:
        // there is no coherent proposal to evaluate, and silently ignoring it would produce a confident answer to a
        // question the caller did not ask. Everything the selection could be *unwise* about is a validation finding.
        var unknownIds = UnknownScopeSelectionIds(connectedSystem, proposal);
        if (unknownIds != null)
            return BadRequest(unknownIds);

        var apiKey = await GetCurrentApiKeyAsync();
        var user = apiKey == null ? await GetCurrentUserAsync() : null;

        var previewRequest = new ConfigurationChangePreviewRequest
        {
            Surface = ConfigurationChangePreviewSurface.ConnectedSystem,
            TargetId = connectedSystem.Id,
            TargetName = connectedSystem.Name,
            ProposedConfiguration = proposal,
            DeltaPersistence = request.DeltaPersistence,
            InitiatedByType = apiKey != null ? ActivityInitiatorType.ApiKey : ActivityInitiatorType.User,
            InitiatedById = apiKey?.Id ?? user?.Id,
            InitiatedByName = apiKey?.Name ?? user?.Name
        };

        var result = await _application.ConfigurationChangePreviews.StartAndDispatchPreviewAsync(previewRequest);

        _logger.LogInformation("Started partition and container selection preview {ActivityId} for Connected System {Id}",
            result.ActivityId, connectedSystem.Id);

        return AcceptedAtRoute("GetConfigurationChangePreview", new { activityId = result.ActivityId },
            ConfigurationChangePreviewStartResponse.FromResult(result));
    }

    /// <summary>
    /// The error response for a proposal naming a partition or container this Connected System does not have, or
    /// stating that one Container is both managed and carved out, or null when the proposal is coherent.
    /// </summary>
    private static ApiErrorResponse? UnknownScopeSelectionIds(
        ConnectedSystem connectedSystem, ConnectedSystemScopeSelectionProposal proposal)
    {
        var partitions = connectedSystem.Partitions ?? [];
        var knownPartitionIds = partitions.Select(p => p.Id).ToHashSet();
        var knownContainerIds = partitions
            .Where(p => p.Containers != null)
            .SelectMany(p => FlattenContainerIds(p.Containers!))
            .ToHashSet();

        var unknownPartitions = proposal.SelectedPartitionIds.Where(id => !knownPartitionIds.Contains(id)).ToList();
        if (unknownPartitions.Count > 0)
        {
            return ApiErrorResponse.BadRequest(
                $"Partition ID(s) {string.Join(", ", unknownPartitions)} do not belong to Connected System {connectedSystem.Id}.");
        }

        var proposedExclusions = proposal.ExcludedContainerIds ?? [];
        var unknownContainers = proposal.SelectedContainerIds
            .Concat(proposedExclusions)
            .Where(id => !knownContainerIds.Contains(id))
            .Distinct()
            .ToList();

        if (unknownContainers.Count > 0)
        {
            return ApiErrorResponse.BadRequest(
                $"Container ID(s) {string.Join(", ", unknownContainers)} do not belong to Connected System {connectedSystem.Id}.");
        }

        // A Container is managed or carved out, never both (#1255). The write endpoints refuse the contradiction
        // rather than resolving it, and a preview has even less business picking a winner: it would go on to state
        // a confident object count for a configuration that could not be saved.
        var contradictory = proposedExclusions.Intersect(proposal.SelectedContainerIds).ToList();
        if (contradictory.Count > 0)
        {
            return ApiErrorResponse.BadRequest(
                $"Container ID(s) {string.Join(", ", contradictory)} are named as both selected and excluded. " +
                "A Container states one or the other, so name it in one list only.");
        }

        return null;
    }

    private static IEnumerable<int> FlattenContainerIds(IEnumerable<ConnectedSystemContainer> containers)
    {
        foreach (var container in containers)
        {
            yield return container.Id;

            foreach (var childId in FlattenContainerIds(container.ChildContainers))
                yield return childId;
        }
    }
    #endregion

    #region Capabilities

    /// <summary>
    /// Get a Connected System's detected capabilities
    /// </summary>
    /// <remarks>
    /// Returns the human-readable facts the Connector has detected about the target system, e.g. an LDAP
    /// directory's type, vendor, DNS host name, and paging support. These are discovered from the target
    /// during a previous connection and persisted by JIM; nothing here triggers a new connection. The list is
    /// empty when the Connector does not detect any capabilities, or when no data has been detected yet
    /// (for example, before the first successful connection).
    /// </remarks>
    /// <param name="connectedSystemId">The unique identifier of the Connected System.</param>
    /// <returns>An ordered list of detected capability facts.</returns>
    [HttpGet("connected-systems/{connectedSystemId:int}/capabilities", Name = "GetConnectedSystemCapabilities")]
    [ProducesResponseType(typeof(IEnumerable<ConnectorCapabilityDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetConnectedSystemCapabilitiesAsync(int connectedSystemId)
    {
        _logger.LogTrace("Requested detected capabilities for Connected System: {Id}", connectedSystemId);

        var connectedSystem = await _application.ConnectedSystems.GetConnectedSystemCoreAsync(connectedSystemId);
        if (connectedSystem == null)
            return NotFound(ApiErrorResponse.NotFound($"Connected System with ID {connectedSystemId} not found."));

        // Null means the Connector does not support capability detection; the API flattens that to an empty
        // list (the distinction only matters to the portal, which hides the card entirely).
        var capabilities = await _application.ConnectedSystems.GetConnectedSystemDetectedCapabilitiesAsync(connectedSystemId) ?? [];
        var dtos = capabilities.Select(ConnectorCapabilityDto.FromEntity);
        return Ok(dtos);
    }

    #endregion

    #region Directory Servers

    /// <summary>
    /// Discover the domain controllers in a Connected System's directory
    /// </summary>
    /// <remarks>
    /// Lists the domain controllers in an Active Directory or Samba AD forest, with the Active Directory Site
    /// each belongs to, using the Connected System's currently saved connectivity settings. Purely informational:
    /// this never writes to the Preferred Domain Controller setting; only an administrator's own subsequent
    /// update of the Connected System does that.
    /// </remarks>
    /// <param name="connectedSystemId">The Connected System whose directory to discover domain controllers in.</param>
    /// <returns>The discovered domain controllers.</returns>
    /// <response code="200">The domain controllers discovered.</response>
    /// <response code="400">The Connector does not support directory server discovery, or the connected directory is not AD-family.</response>
    /// <response code="404">No Connected System with that identifier exists.</response>
    /// <response code="502">JIM could not discover domain controllers, e.g. the directory was unreachable or refused the credentials.</response>
    [HttpGet("connected-systems/{connectedSystemId:int}/directory-servers", Name = "GetConnectedSystemDirectoryServers")]
    [ProducesResponseType(typeof(IEnumerable<ConnectedSystemDirectoryServerDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status502BadGateway)]
    public async Task<IActionResult> GetConnectedSystemDirectoryServersAsync(int connectedSystemId)
    {
        _logger.LogTrace("Discovering directory servers for Connected System: {Id}", connectedSystemId);

        var connectedSystem = await _application.ConnectedSystems.GetConnectedSystemCoreAsync(connectedSystemId);
        if (connectedSystem == null)
            return NotFound(ApiErrorResponse.NotFound($"Connected System with ID {connectedSystemId} not found."));

        if (!await _application.ConnectedSystems.SupportsDirectoryServerDiscoveryAsync(connectedSystemId))
            return BadRequest(ApiErrorResponse.BadRequest(
                $"The '{connectedSystem.ConnectorDefinition.Name}' connector does not support directory server discovery."));

        try
        {
            var directoryServers = await _application.ConnectedSystems.GetConnectedSystemDirectoryServersAsync(connectedSystemId);
            return Ok(directoryServers.Select(ConnectedSystemDirectoryServerDto.FromModel));
        }
        catch (NotSupportedException ex)
        {
            // The capability check above passed (the Connector implements IConnectorDirectoryServers), but the
            // Connector itself has refused: e.g. the LDAP Connector only discovers domain controllers for
            // AD-family directories, and the connected directory turned out not to be one.
            return BadRequest(ApiErrorResponse.BadRequest(ex.Message));
        }
        // Fallback dispatcher: any connectivity failure (unreachable directory, refused credentials, a malformed
        // response) becomes a 502 rather than the generic 500 the global exception handler would otherwise
        // return, and the cancellation exclusion keeps a genuinely aborted request propagating rather than
        // being reported as a discovery failure.
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to discover directory servers for Connected System {Id}: {Message}",
                connectedSystemId, LogSanitiser.Sanitise(ex.Message));
            return StatusCode(StatusCodes.Status502BadGateway,
                ApiErrorResponse.BadRequest($"JIM could not discover directory servers: {ex.Message}"));
        }
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
                await _application.ConnectedSystems.CreateConnectedSystemAsync(connectedSystem, apiKey, changeReason: request.ChangeReason);
            else
                await _application.ConnectedSystems.CreateConnectedSystemAsync(connectedSystem, initiatedBy, changeReason: request.ChangeReason);

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

        if (request.InitialPasswordTimeToLive.HasValue)
            connectedSystem.InitialPasswordTimeToLive = request.InitialPasswordTimeToLive.Value;

        if (request.UnresolvedReferenceHandling.HasValue)
            connectedSystem.UnresolvedReferenceHandling = request.UnresolvedReferenceHandling.Value;

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
                await _application.ConnectedSystems.UpdateConnectedSystemAsync(connectedSystem, apiKey, changeReason: request.ChangeReason);
            else
                await _application.ConnectedSystems.UpdateConnectedSystemAsync(connectedSystem, initiatedBy, changeReason: request.ChangeReason);

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
    /// <param name="changeReason">Optional reason for the deletion, recorded on the audit Activity and the configuration change history tombstone. Supplied as a query parameter because HTTP DELETE bodies are awkward for clients.</param>
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
        [FromQuery] bool deleteChangeHistory = false,
        [FromQuery] string? changeReason = null)
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
            ? await _application.ConnectedSystems.DeleteAsync(connectedSystemId, apiKey, deleteChangeHistory, changeReason)
            : await _application.ConnectedSystems.DeleteAsync(connectedSystemId, initiatedBy, deleteChangeHistory, changeReason);

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

    /// <summary>
    /// Read the certificate a Connected System's server presents
    /// </summary>
    /// <remarks>
    /// Opens a TLS connection to the endpoint this Connected System is configured for, purely to look at the
    /// certificate the server offers, and refuses it. Nothing is stored: trusting the certificate is a separate,
    /// explicit call. The endpoint comes from the Connected System's own settings and can never be supplied by the
    /// caller.
    /// </remarks>
    /// <param name="connectedSystemId">The Connected System whose server is asked.</param>
    /// <returns>The certificate the server presented and which check it fails.</returns>
    /// <response code="200">The certificate the server is presenting.</response>
    /// <response code="400">The Connected System is not configured to make an encrypted connection, so there is no certificate to look at.</response>
    /// <response code="404">No Connected System with that identifier exists.</response>
    /// <response code="502">The server could not be reached, which is a connectivity problem rather than a certificate one.</response>
    [HttpGet("connected-systems/{connectedSystemId:int}/server-certificate", Name = "GetConnectedSystemServerCertificate")]
    [ProducesResponseType(typeof(ServerCertificateResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status502BadGateway)]
    public async Task<IActionResult> GetServerCertificateAsync(int connectedSystemId)
    {
        return await ReadServerCertificateAsync(connectedSystemId, draftSettingValues: null);
    }

    /// <summary>
    /// Read the certificate a Connected System's server presents, using settings that have not been saved
    /// </summary>
    /// <remarks>
    /// The same read as the GET, but taking connectivity settings the caller has entered and not yet saved. JIM
    /// refuses to save settings that fail validation, and a certificate JIM does not trust is a validation failure,
    /// so an administrator configuring a new Connected System has an address on screen and nothing in the database;
    /// without this they could never reach the certificate that is blocking them. The endpoint is still derived by
    /// the Connected System's own connector, so no address is ever named directly. Nothing is stored, and the draft
    /// settings are never persisted.
    /// </remarks>
    /// <param name="connectedSystemId">The Connected System whose server is asked.</param>
    /// <param name="request">The unsaved setting values, keyed by Connector Definition Setting id.</param>
    /// <returns>The certificate the server presented and which check it fails.</returns>
    /// <response code="200">The certificate the server is presenting.</response>
    /// <response code="400">The settings do not describe an encrypted connection, so there is no certificate to look at.</response>
    /// <response code="404">No Connected System with that identifier exists.</response>
    /// <response code="502">The server could not be reached, which is a connectivity problem rather than a certificate one.</response>
    [HttpPost("connected-systems/{connectedSystemId:int}/server-certificate", Name = "ReadConnectedSystemServerCertificate")]
    [ProducesResponseType(typeof(ServerCertificateResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status502BadGateway)]
    public async Task<IActionResult> ReadServerCertificateAsync(int connectedSystemId, [FromBody] ReadServerCertificateRequest request)
    {
        return await ReadServerCertificateAsync(connectedSystemId, ServerCertificateDraftSettings.ToDrafts(request.SettingValues));
    }

    private async Task<IActionResult> ReadServerCertificateAsync(int connectedSystemId, IReadOnlyCollection<ConnectedSystemSettingValueDraft>? draftSettingValues)
    {
        _logger.LogDebug("Reading the server certificate for Connected System: {Id}", connectedSystemId);
        var result = await _application.Certificates.ReadServerCertificateAsync(connectedSystemId, draftSettingValues);

        switch (result.Outcome)
        {
            case ServerCertificateReadOutcome.Read:
                return Ok(new ServerCertificateResponse
                {
                    Certificate = result.Diagnostic!,
                    ReadAt = result.ReadAt ?? DateTime.UtcNow
                });
            case ServerCertificateReadOutcome.ConnectedSystemNotFound:
                return NotFound(ApiErrorResponse.NotFound(result.Message ?? $"Connected System with ID {connectedSystemId} not found."));
            case ServerCertificateReadOutcome.ServerUnreachable:
                return StatusCode(StatusCodes.Status502BadGateway, ApiErrorResponse.BadRequest(result.Message ?? "The server could not be reached."));
            default:
                return BadRequest(ApiErrorResponse.BadRequest(result.Message ?? "There is no server certificate to look at."));
        }
    }

    /// <summary>
    /// Trust the certificate a Connected System's server presents
    /// </summary>
    /// <remarks>
    /// Reads the certificate from the server again, checks it against the thumbprint supplied, and adds it to the
    /// JIM certificate store through the audited path. The thumbprint is required and a mismatch is refused: reading
    /// again at the moment of the decision is what makes a certificate that changed since it was shown detectable
    /// rather than waved through. Supplying the authority's thumbprint trusts the authority, which survives the
    /// server's own certificate being renewed.
    /// </remarks>
    /// <param name="connectedSystemId">The Connected System whose server is asked.</param>
    /// <param name="request">The thumbprint being trusted, and optionally why.</param>
    /// <returns>The outcome, including the certificate as it now sits in the store.</returns>
    /// <response code="201">The certificate was added to the JIM certificate store.</response>
    /// <response code="200">The certificate was already in the store, so there was nothing to do.</response>
    /// <response code="400">No thumbprint was supplied, or the Connected System is not configured to make an encrypted connection.</response>
    /// <response code="404">No Connected System with that identifier exists.</response>
    /// <response code="409">The server is presenting a different certificate from the one named, so nothing was trusted.</response>
    /// <response code="502">The server could not be reached to read its certificate again, so nothing was trusted.</response>
    [HttpPost("connected-systems/{connectedSystemId:int}/server-certificate/trust", Name = "TrustConnectedSystemServerCertificate")]
    [ProducesResponseType(typeof(TrustServerCertificateResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(TrustServerCertificateResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(TrustServerCertificateResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(TrustServerCertificateResponse), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(TrustServerCertificateResponse), StatusCodes.Status502BadGateway)]
    public async Task<IActionResult> TrustServerCertificateAsync(int connectedSystemId, [FromBody] TrustServerCertificateRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Thumbprint))
            return BadRequest(ApiErrorResponse.ValidationError("A thumbprint is required. JIM will not trust whatever a server happens to be presenting."));

        _logger.LogInformation("Trusting the server certificate for Connected System: {Id}", connectedSystemId);
        var drafts = ServerCertificateDraftSettings.ToDrafts(request.SettingValues);
        var apiKey = await GetCurrentApiKeyAsync();
        var result = apiKey != null
            ? await _application.Certificates.TrustServerCertificateAsync(connectedSystemId, request.Thumbprint, apiKey, request.ChangeReason, drafts)
            : await _application.Certificates.TrustServerCertificateAsync(connectedSystemId, request.Thumbprint, await GetCurrentUserAsync(), request.ChangeReason, drafts);

        var response = TrustServerCertificateResponse.FromResult(result);

        return result.Outcome switch
        {
            ServerCertificateTrustOutcome.Trusted => StatusCode(StatusCodes.Status201Created, response),
            ServerCertificateTrustOutcome.AlreadyTrusted => Ok(response),
            ServerCertificateTrustOutcome.ThumbprintMismatch => Conflict(response),
            ServerCertificateTrustOutcome.ConnectedSystemNotFound => NotFound(ApiErrorResponse.NotFound(result.Message ?? $"Connected System with ID {connectedSystemId} not found.")),
            ServerCertificateTrustOutcome.ServerUnreachable => StatusCode(StatusCodes.Status502BadGateway, response),
            _ => BadRequest(response)
        };
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
            workerTask = SynchronisationWorkerTask.ForUser(connectedSystemId, runProfileId, initiatedBy.Id, initiatedBy.NameOrId);
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

        // SPEC-1082 D10: Verification Mode only applies to Full Import runs.
        if (request.VerifyImportContentHashes && request.RunType != ConnectedSystemRunType.FullImport)
            return BadRequest(ApiErrorResponse.BadRequest("VerifyImportContentHashes can only be enabled on a Full Import Run Profile."));

        // Create the Run Profile
        var runProfile = new ConnectedSystemRunProfile
        {
            Name = request.Name,
            ConnectedSystemId = connectedSystemId,
            RunType = request.RunType,
            PageSize = request.PageSize,
            FilePath = request.FilePath,
            VerifyImportContentHashes = request.VerifyImportContentHashes
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

        // SPEC-1082 D10: Verification Mode only applies to Full Import runs. RunType itself is
        // immutable after create, so validate against the Run Profile's existing RunType.
        if (request.VerifyImportContentHashes.HasValue)
        {
            if (request.VerifyImportContentHashes.Value && runProfile.RunType != ConnectedSystemRunType.FullImport)
                return BadRequest(ApiErrorResponse.BadRequest("VerifyImportContentHashes can only be enabled on a Full Import Run Profile."));

            runProfile.VerifyImportContentHashes = request.VerifyImportContentHashes.Value;
        }

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

    #region Data Flow

    /// <summary>
    /// List attribute data flows
    /// </summary>
    /// <remarks>
    /// A system-wide map of every attribute data flow, in both directions: what contributes each Metaverse Attribute,
    /// and what each Connected System attribute is written from. One flow per Synchronisation Rule mapping.
    /// <para>
    /// An Import flow reads Connected System attributes and writes a single Metaverse Attribute, so it carries
    /// <c>targetMetaverseAttribute*</c>, its <c>priority</c> and its <c>nullIsValue</c> flag. An Export flow reads
    /// Metaverse Attributes and writes a single Connected System attribute, so it carries
    /// <c>targetConnectedSystemAttribute*</c> and the owning rule's <c>enforceState</c>. The fields that do not apply
    /// to a flow's direction are null rather than defaulted, so a caller never has to guess which are meaningful.
    /// </para>
    /// <para>
    /// Import flows also carry <c>contributorCount</c>: how many flows contribute to the same Metaverse Attribute,
    /// counted across the whole configuration rather than the filtered results, so filtering to one Connected System
    /// does not make a shared attribute look like a sole contributor.
    /// </para>
    /// </remarks>
    /// <param name="pagination">Pagination parameters (page, pageSize, sortBy, sortDirection, filter).</param>
    /// <param name="filter">Direction, Connected System, object type, attribute and free-text filters.</param>
    /// <returns>A paginated list of attribute data flows.</returns>
    [HttpGet("data-flows", Name = "GetDataFlows")]
    [ProducesResponseType(typeof(PaginatedResponse<DataFlowHeader>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetDataFlowsAsync([FromQuery] PaginationRequest pagination, [FromQuery] DataFlowFilterRequest filter)
    {
        _logger.LogTrace("Requested attribute data flows (Page: {Page}, PageSize: {PageSize})", pagination.Page, pagination.PageSize);

        var flows = await _application.ConnectedSystems.GetDataFlowsAsync(filter.ToQuery());

        var result = flows
            .AsQueryable()
            .ApplySortAndFilter(pagination)
            .ToPaginatedResponse(pagination);

        return Ok(result);
    }

    #endregion

    #region Synchronisation Rules

    /// <summary>
    /// List Synchronisation Rules
    /// </summary>
    /// <remarks>
    /// Narrow the list with the <c>connectedSystemIds</c>, <c>directions</c>, <c>actionTypes</c> and
    /// <c>statuses</c> facets, each of which is repeatable. Facets combine with AND, values within a
    /// facet combine with OR, and <c>search</c> narrows whatever the facets left.
    /// </remarks>
    /// <param name="pagination">Pagination parameters (page, pageSize, sortBy, sortDirection, filter).</param>
    /// <param name="filter">Connected System, Direction, Action type, Status and free-text filters.</param>
    /// <returns>A paginated list of Synchronisation Rule headers.</returns>
    [HttpGet("sync-rules", Name = "GetSyncRules")]
    [ProducesResponseType(typeof(PaginatedResponse<SyncRuleHeader>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetSyncRulesAsync([FromQuery] PaginationRequest pagination, [FromQuery] SyncRuleFilterRequest filter)
    {
        _logger.LogTrace("Requested Synchronisation Rules (Page: {Page}, PageSize: {PageSize})", pagination.Page, pagination.PageSize);

        // Header tier: a list endpoint has no use for each rule's Attribute Flows, Object Matching
        // Rules and schema graph, and the Synchronisation Rule set is small enough to filter in
        // memory through the shared SyncRuleFilter every JIM surface uses.
        var headers = await _application.ConnectedSystems.GetSyncRuleHeadersAsync();
        var syncRuleFilter = filter.ToFilter();
        var filtered = (syncRuleFilter.IsEmpty ? headers : headers.Where(syncRuleFilter.Matches)).AsQueryable();

        var result = filtered
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
            Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description,
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
            EnforceState = request.EnforceState,
            OutboundDeprovisionAction = request.OutboundDeprovisionAction ?? OutboundDeprovisionAction.Disconnect
        };

        var apiKey = await GetCurrentApiKeyAsync();
        bool success;
        if (apiKey != null)
            success = await _application.ConnectedSystems.CreateOrUpdateSyncRuleAsync(syncRule, apiKey, changeReason: request.ChangeReason);
        else
            success = await _application.ConnectedSystems.CreateOrUpdateSyncRuleAsync(syncRule, initiatedBy, changeReason: request.ChangeReason);
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

        // A null Description means "leave unchanged"; an empty or whitespace-only value clears it.
        if (request.Description != null)
            syncRule.Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description;

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
            success = await _application.ConnectedSystems.CreateOrUpdateSyncRuleAsync(syncRule, apiKey, changeReason: request.ChangeReason, previewActivityId: request.PreviewActivityId);
        else
            success = await _application.ConnectedSystems.CreateOrUpdateSyncRuleAsync(syncRule, initiatedBy, changeReason: request.ChangeReason, previewActivityId: request.PreviewActivityId);
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
    /// Preview what changing a Synchronisation Rule's destructive toggles would do
    /// </summary>
    /// <remarks>
    /// Evaluates a proposed Outbound Deprovision Action and/or Inbound Out-of-Scope Action against the rule's
    /// persisted configuration, without saving either: which joined objects the next synchronisation would
    /// disconnect, keep joined, or remove from the target Connected System, and which Metaverse Objects would
    /// become eligible for deletion once the disconnections land.
    ///
    /// This matters because both toggles are silently destructive. Flipping the Deprovisioning Action to Delete
    /// converts every future scope exit into a deletion in the target system; flipping the Out-of-Scope Action to
    /// Disconnect can mass-disconnect joined objects, recalling what they contributed to the Metaverse.
    ///
    /// An omitted or null field previews the stored rule's value, matching the update endpoint's semantics
    /// exactly. Apply the previewed change through <c>PUT sync-rules/{id}</c>.
    ///
    /// Evaluation is asynchronous. This returns as soon as the proposal itself has been validated, with the
    /// Activity id to poll; read progress and results from <c>GET /previews/{activityId}</c>, drill-down rows from
    /// <c>GET /previews/{activityId}/deltas</c>, and abandon a running preview with
    /// <c>DELETE /previews/{activityId}</c>.
    ///
    /// A toggle the rule's direction never reads (the Outbound Deprovision Action on an import rule, and the
    /// reverse) comes back with a validation finding saying so and no counted impact; that is the answer the
    /// caller asked for, not a reason to refuse the request.
    /// </remarks>
    /// <param name="syncRuleId">The unique identifier of the Synchronisation Rule.</param>
    /// <param name="request">The proposed toggles.</param>
    /// <response code="202">The preview was started. Poll the returned Activity id for results.</response>
    /// <response code="404">Synchronisation Rule not found.</response>
    /// <response code="401">User not authenticated.</response>
    [HttpPost("sync-rules/{syncRuleId:int}/destructive-toggles/preview", Name = "StartSyncRuleDestructiveTogglesPreview")]
    [ProducesResponseType(typeof(ConfigurationChangePreviewStartResponse), StatusCodes.Status202Accepted)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> StartSyncRuleDestructiveTogglesPreviewAsync(int syncRuleId,
        [FromBody] StartSyncRuleDestructiveTogglesPreviewRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var syncRule = await _application.ConnectedSystems.GetSyncRuleAsync(syncRuleId);
        if (syncRule == null)
            return NotFound(ApiErrorResponse.NotFound($"Synchronisation Rule with ID {syncRuleId} not found."));

        // An omitted toggle means "as the rule stands", so a caller proposing one change is never silently
        // proposing a second; the adapter then reports honestly that the unchanged one changes nothing.
        var proposal = new SyncRuleDestructiveToggleProposal(
            request.OutboundDeprovisionAction ?? syncRule.OutboundDeprovisionAction,
            request.InboundOutOfScopeAction ?? syncRule.InboundOutOfScopeAction);

        var apiKey = await GetCurrentApiKeyAsync();
        var user = apiKey == null ? await GetCurrentUserAsync() : null;

        var previewRequest = new ConfigurationChangePreviewRequest
        {
            Surface = ConfigurationChangePreviewSurface.SynchronisationRule,
            TargetId = syncRule.Id,
            TargetName = syncRule.Name,
            ProposedConfiguration = proposal,
            DeltaPersistence = request.DeltaPersistence,
            InitiatedByType = apiKey != null ? ActivityInitiatorType.ApiKey : ActivityInitiatorType.User,
            InitiatedById = apiKey?.Id ?? user?.Id,
            InitiatedByName = apiKey?.Name ?? user?.Name
        };

        var result = await _application.ConfigurationChangePreviews.StartAndDispatchPreviewAsync(previewRequest);

        _logger.LogInformation("Started destructive-toggle preview {ActivityId} for Synchronisation Rule {Id}",
            result.ActivityId, syncRule.Id);

        return AcceptedAtRoute("GetConfigurationChangePreview", new { activityId = result.ActivityId },
            ConfigurationChangePreviewStartResponse.FromResult(result));
    }

    /// <summary>
    /// Get a Synchronisation Rule's initial password configuration
    /// </summary>
    /// <remarks>
    /// Whether JIM sets an initial password on the accounts this rule provisions, and how it generates one.
    /// A rule with nothing configured reports the setting switched off with JIM's defaults, which is how it behaves.
    ///
    /// No password value is ever returned: passwords are generated at the moment they are set and are not stored.
    /// </remarks>
    /// <param name="id">The unique identifier of the Synchronisation Rule.</param>
    /// <response code="200">The initial password configuration.</response>
    /// <response code="404">Synchronisation Rule not found.</response>
    [HttpGet("sync-rules/{id:int}/initial-password", Name = "GetSyncRuleInitialPassword")]
    [ProducesResponseType(typeof(SyncRuleInitialPasswordResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetSyncRuleInitialPasswordAsync(int id)
    {
        var syncRule = await _application.ConnectedSystems.GetSyncRuleAsync(id);
        if (syncRule == null)
            return NotFound(ApiErrorResponse.NotFound($"Synchronisation Rule with ID {id} not found."));

        // The parked work is reported with the settings that caused it: an administrator scripting a check across
        // every rule wants the answer in the response they were already fetching, not a second call per rule.
        var parkedReasons = await _application.InitialPasswords.GetParkedReasonsAsync(id);
        var attention = await _application.InitialPasswords.GetAttentionBySyncRuleAsync([id]);

        return Ok(SyncRuleInitialPasswordResponse.FromEntity(
            syncRule.InitialPassword, parkedReasons, attention.GetValueOrDefault(id)));
    }

    /// <summary>
    /// Update a Synchronisation Rule's initial password configuration
    /// </summary>
    /// <remarks>
    /// Every field is optional; an omitted one leaves the stored value unchanged. Supplying `customPolicy`
    /// replaces the generator settings as a set rather than merging field by field, because they only make
    /// sense together.
    ///
    /// `staticPassword` is write-only: it is encrypted before it is stored and is never returned. Omit it to
    /// leave the stored password as it is. A rule using the `Static` source with no password stored is refused,
    /// because delivery would park every account it provisions.
    ///
    /// Only Export rules that provision can set an initial password: only an account JIM has just created has
    /// never had one, and resetting an existing account's password is not something a Synchronisation Rule does.
    /// </remarks>
    /// <param name="id">The unique identifier of the Synchronisation Rule.</param>
    /// <param name="request">The settings to change.</param>
    /// <response code="200">The updated initial password configuration.</response>
    /// <response code="400">The rule does not provision, or the settings cannot be satisfied.</response>
    /// <response code="404">Synchronisation Rule not found.</response>
    /// <response code="401">User could not be identified from authentication token.</response>
    [HttpPut("sync-rules/{id:int}/initial-password", Name = "UpdateSyncRuleInitialPassword")]
    [ProducesResponseType(typeof(SyncRuleInitialPasswordResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> UpdateSyncRuleInitialPasswordAsync(int id, [FromBody] UpdateSyncRuleInitialPasswordRequest request)
    {
        _logger.LogInformation("Updating the initial password configuration of Synchronisation Rule: {Id}", id);

        var initiatedBy = await GetCurrentUserAsync();
        if (initiatedBy == null && !IsApiKeyAuthenticated())
        {
            _logger.LogWarning("Could not identify user from JWT claims for a Synchronisation Rule initial password update");
            return Unauthorized(ApiErrorResponse.Unauthorised("Could not identify user from authentication token."));
        }

        var syncRule = await _application.ConnectedSystems.GetSyncRuleAsync(id);
        if (syncRule == null)
            return NotFound(ApiErrorResponse.NotFound($"Synchronisation Rule with ID {id} not found."));

        var configuration = syncRule.InitialPassword ??= new SyncRuleInitialPassword { SyncRuleId = syncRule.Id };

        if (request.Enabled.HasValue)
            configuration.Enabled = request.Enabled.Value;

        if (request.Source.HasValue)
            configuration.Source = request.Source.Value;

        request.CustomPolicy?.ApplyTo(configuration.CustomPolicy);

        if (request.ExpiryBehaviour.HasValue)
            configuration.ExpiryBehaviour = request.ExpiryBehaviour.Value;

        if (request.EnableAccount.HasValue)
            configuration.EnableAccount = request.EnableAccount.Value;

        // Assessed before it is stored, and against the same discovered policy the generator is checked against.
        // One static password goes to every account this rule provisions, so a value the target refuses is not
        // one account's problem, and the administrator sending it is the person who can fix it.
        if (!string.IsNullOrEmpty(request.StaticPassword))
        {
            var suppliedAssessment = _application.PasswordGenerator.AssessSupplied(
                request.StaticPassword,
                await _application.ConnectedSystems.GetPasswordPolicyAsync(syncRule.ConnectedSystemId));

            // The assessment's problems never quote the password, which is what makes them safe to return here.
            if (!suppliedAssessment.IsUsable)
                return BadRequest(ApiErrorResponse.BadRequest(
                    $"This password cannot be used: {string.Join(" ", suppliedAssessment.Problems)}"));

            configuration.StaticPasswordEncryptedValue = _application.InitialPasswords.ProtectStaticPassword(request.StaticPassword);
            configuration.StaticPasswordSetAt = DateTime.UtcNow;
        }

        // Refused rather than silently accepted: a rule that never creates an account has nothing to give a
        // first password to, and storing the setting anyway would have it do nothing while reading as configured.
        if (configuration.Enabled && !(syncRule.Direction == SyncRuleDirection.Export && syncRule.ProvisionToConnectedSystem == true))
            return BadRequest(ApiErrorResponse.BadRequest(
                "An initial password can only be set by an Export Synchronisation Rule that provisions to the Connected System."));

        // Checked here rather than left to fail per account: an unsatisfiable configuration parks every account
        // it touches, and the administrator saving it is the person who can fix it. The same assessment gates
        // the portal's Save, so the two surfaces accept and refuse exactly the same settings.
        var problems = _application.InitialPasswords.AssessConfiguration(
            configuration, await _application.ConnectedSystems.GetPasswordPolicyAsync(syncRule.ConnectedSystemId));

        if (problems.Count > 0)
            return BadRequest(ApiErrorResponse.BadRequest(
                $"These password settings cannot be satisfied: {string.Join(" ", problems)}"));

        var apiKey = await GetCurrentApiKeyAsync();
        var success = apiKey != null
            ? await _application.ConnectedSystems.CreateOrUpdateSyncRuleAsync(syncRule, apiKey, changeReason: request.ChangeReason)
            : await _application.ConnectedSystems.CreateOrUpdateSyncRuleAsync(syncRule, initiatedBy, changeReason: request.ChangeReason);

        if (!success)
        {
            var validationErrors = syncRule.Validate();
            return BadRequest(ApiErrorResponse.BadRequest(
                $"Synchronisation Rule validation failed: {string.Join("; ", validationErrors.Select(v => v.Message))}"));
        }

        _logger.LogInformation("Updated the initial password configuration of Synchronisation Rule: {Id}", id);

        var updated = await _application.ConnectedSystems.GetSyncRuleAsync(id);
        return Ok(SyncRuleInitialPasswordResponse.FromEntity(updated!.InitialPassword));
    }

    /// <summary>
    /// Delete a Synchronisation Rule
    /// </summary>
    /// <param name="id">The unique identifier of the Synchronisation Rule to delete.</param>
    /// <param name="changeReason">An optional reason for the deletion, recorded against the change history.</param>
    /// <returns>No content on success.</returns>
    /// <response code="204">Synchronisation Rule deleted successfully.</response>
    /// <response code="404">Synchronisation Rule not found.</response>
    /// <response code="401">User could not be identified from authentication token.</response>
    [HttpDelete("sync-rules/{id:int}", Name = "DeleteSyncRule")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> DeleteSyncRuleAsync(int id, [FromQuery] string? changeReason = null)
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
            await _application.ConnectedSystems.DeleteSyncRuleAsync(syncRule, apiKey, changeReason);
        else
            await _application.ConnectedSystems.DeleteSyncRuleAsync(syncRule, initiatedBy, changeReason);

        _logger.LogInformation("Deleted Synchronisation Rule: {Id}", id);

        return NoContent();
    }

    #endregion

    #region Configuration Change History

    /// <summary>
    /// List the change history for a Synchronisation Rule.
    /// </summary>
    /// <param name="id">The unique identifier of the Synchronisation Rule.</param>
    /// <param name="pagination">Pagination parameters.</param>
    /// <returns>A paged list of change-history entries, newest version first, each with a one-line summary.</returns>
    /// <response code="200">Change history returned (empty if the rule has no recorded configuration changes).</response>
    /// <response code="401">User could not be identified from authentication token.</response>
    [HttpGet("sync-rules/{id:int}/change-history", Name = "GetSyncRuleChangeHistory")]
    [ProducesResponseType(typeof(PaginatedResponse<ConfigurationChangeHistoryItem>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetSyncRuleChangeHistoryAsync(int id, [FromQuery] PaginationRequest pagination)
    {
        var result = await _application.ChangeHistory.GetConfigurationChangeHistoryAsync(ActivityTargetType.SynchronisationRule, id, pagination.Page, pagination.PageSize);
        return Ok(PaginatedResponse<ConfigurationChangeHistoryItem>.Create(result.Results, result.TotalResults, pagination.Page, pagination.PageSize));
    }

    /// <summary>
    /// Get a single version of a Synchronisation Rule's change history, with its snapshot and the diff against the previous version.
    /// </summary>
    /// <param name="id">The unique identifier of the Synchronisation Rule.</param>
    /// <param name="changeVersion">The per-object change version to retrieve.</param>
    /// <returns>The change detail: metadata, the redacted snapshot, and the diff against the previous version.</returns>
    /// <response code="200">The change detail.</response>
    /// <response code="404">No change with that version was found for the rule.</response>
    /// <response code="401">User could not be identified from authentication token.</response>
    [HttpGet("sync-rules/{id:int}/change-history/{changeVersion:int}", Name = "GetSyncRuleChange")]
    [ProducesResponseType(typeof(ConfigurationChangeDetail), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetSyncRuleChangeAsync(int id, int changeVersion)
    {
        var detail = await _application.ChangeHistory.GetConfigurationChangeAsync(ActivityTargetType.SynchronisationRule, id, changeVersion);
        if (detail == null)
            return NotFound(ApiErrorResponse.NotFound($"No change history found for Synchronisation Rule {id} version {changeVersion}."));
        return Ok(detail);
    }

    /// <summary>
    /// Compare two versions of a Synchronisation Rule's configuration.
    /// </summary>
    /// <param name="id">The unique identifier of the Synchronisation Rule.</param>
    /// <param name="fromVersion">The earlier version to compare from.</param>
    /// <param name="toVersion">The later version to compare to.</param>
    /// <returns>The structured diff of the later version against the earlier.</returns>
    /// <response code="200">The diff.</response>
    /// <response code="404">One of the requested versions was not found for the rule.</response>
    /// <response code="401">User could not be identified from authentication token.</response>
    [HttpGet("sync-rules/{id:int}/change-history/compare", Name = "CompareSyncRuleChanges")]
    [ProducesResponseType(typeof(ConfigurationDiff), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> CompareSyncRuleChangesAsync(int id, [FromQuery] int fromVersion, [FromQuery] int toVersion)
    {
        var diff = await _application.ChangeHistory.CompareConfigurationChangesAsync(ActivityTargetType.SynchronisationRule, id, fromVersion, toVersion);
        if (diff == null)
            return NotFound(ApiErrorResponse.NotFound($"Could not compare versions {fromVersion} and {toVersion} for Synchronisation Rule {id}."));
        return Ok(diff);
    }

    /// <summary>
    /// List the change history for a Connected System.
    /// </summary>
    /// <param name="connectedSystemId">The unique identifier of the Connected System.</param>
    /// <param name="pagination">Pagination parameters.</param>
    /// <returns>A paged list of change-history entries, newest version first, each with a one-line summary.</returns>
    /// <response code="200">Change history returned (empty if the Connected System has no recorded configuration changes).</response>
    /// <response code="401">User could not be identified from authentication token.</response>
    [HttpGet("connected-systems/{connectedSystemId:int}/change-history", Name = "GetConnectedSystemChangeHistory")]
    [ProducesResponseType(typeof(PaginatedResponse<ConfigurationChangeHistoryItem>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetConnectedSystemChangeHistoryAsync(int connectedSystemId, [FromQuery] PaginationRequest pagination)
    {
        var result = await _application.ChangeHistory.GetConfigurationChangeHistoryAsync(ActivityTargetType.ConnectedSystem, connectedSystemId, pagination.Page, pagination.PageSize);
        return Ok(PaginatedResponse<ConfigurationChangeHistoryItem>.Create(result.Results, result.TotalResults, pagination.Page, pagination.PageSize));
    }

    /// <summary>
    /// Get a single version of a Connected System's change history, with its snapshot and the diff against the previous version.
    /// </summary>
    /// <param name="connectedSystemId">The unique identifier of the Connected System.</param>
    /// <param name="changeVersion">The per-object change version to retrieve.</param>
    /// <returns>The change detail: metadata, the redacted snapshot, and the diff against the previous version.</returns>
    /// <response code="200">The change detail.</response>
    /// <response code="404">No change with that version was found for the Connected System.</response>
    /// <response code="401">User could not be identified from authentication token.</response>
    [HttpGet("connected-systems/{connectedSystemId:int}/change-history/{changeVersion:int}", Name = "GetConnectedSystemChange")]
    [ProducesResponseType(typeof(ConfigurationChangeDetail), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetConnectedSystemChangeAsync(int connectedSystemId, int changeVersion)
    {
        var detail = await _application.ChangeHistory.GetConfigurationChangeAsync(ActivityTargetType.ConnectedSystem, connectedSystemId, changeVersion);
        if (detail == null)
            return NotFound(ApiErrorResponse.NotFound($"No change history found for Connected System {connectedSystemId} version {changeVersion}."));
        return Ok(detail);
    }

    /// <summary>
    /// Compare two versions of a Connected System's configuration.
    /// </summary>
    /// <param name="connectedSystemId">The unique identifier of the Connected System.</param>
    /// <param name="fromVersion">The earlier version to compare from.</param>
    /// <param name="toVersion">The later version to compare to.</param>
    /// <returns>The structured diff of the later version against the earlier.</returns>
    /// <response code="200">The diff.</response>
    /// <response code="404">One of the requested versions was not found for the Connected System.</response>
    /// <response code="401">User could not be identified from authentication token.</response>
    [HttpGet("connected-systems/{connectedSystemId:int}/change-history/compare", Name = "CompareConnectedSystemChanges")]
    [ProducesResponseType(typeof(ConfigurationDiff), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> CompareConnectedSystemChangesAsync(int connectedSystemId, [FromQuery] int fromVersion, [FromQuery] int toVersion)
    {
        var diff = await _application.ChangeHistory.CompareConfigurationChangesAsync(ActivityTargetType.ConnectedSystem, connectedSystemId, fromVersion, toVersion);
        if (diff == null)
            return NotFound(ApiErrorResponse.NotFound($"Could not compare versions {fromVersion} and {toVersion} for Connected System {connectedSystemId}."));
        return Ok(diff);
    }

    /// <summary>
    /// List the change history for a Connector Definition.
    /// </summary>
    /// <param name="id">The unique identifier of the Connector Definition.</param>
    /// <param name="pagination">Pagination parameters.</param>
    /// <returns>A paged list of change-history entries, newest version first, each with a one-line summary.</returns>
    /// <response code="200">Change history returned (empty if the Connector Definition has no recorded configuration changes).</response>
    /// <response code="401">User could not be identified from authentication token.</response>
    [HttpGet("connector-definitions/{id:int}/change-history", Name = "GetConnectorDefinitionChangeHistory")]
    [ProducesResponseType(typeof(PaginatedResponse<ConfigurationChangeHistoryItem>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetConnectorDefinitionChangeHistoryAsync(int id, [FromQuery] PaginationRequest pagination)
    {
        var result = await _application.ChangeHistory.GetConfigurationChangeHistoryAsync(ActivityTargetType.ConnectorDefinition, id, pagination.Page, pagination.PageSize);
        return Ok(PaginatedResponse<ConfigurationChangeHistoryItem>.Create(result.Results, result.TotalResults, pagination.Page, pagination.PageSize));
    }

    /// <summary>
    /// Get a single version of a Connector Definition's change history, with its snapshot and the diff against the previous version.
    /// </summary>
    /// <param name="id">The unique identifier of the Connector Definition.</param>
    /// <param name="changeVersion">The per-object change version to retrieve.</param>
    /// <returns>The change detail: metadata, the redacted snapshot, and the diff against the previous version.</returns>
    /// <response code="200">The change detail.</response>
    /// <response code="404">No change with that version was found for the Connector Definition.</response>
    /// <response code="401">User could not be identified from authentication token.</response>
    [HttpGet("connector-definitions/{id:int}/change-history/{changeVersion:int}", Name = "GetConnectorDefinitionChange")]
    [ProducesResponseType(typeof(ConfigurationChangeDetail), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetConnectorDefinitionChangeAsync(int id, int changeVersion)
    {
        var detail = await _application.ChangeHistory.GetConfigurationChangeAsync(ActivityTargetType.ConnectorDefinition, id, changeVersion);
        if (detail == null)
            return NotFound(ApiErrorResponse.NotFound($"No change history found for Connector Definition {id} version {changeVersion}."));
        return Ok(detail);
    }

    /// <summary>
    /// Compare two versions of a Connector Definition's configuration.
    /// </summary>
    /// <param name="id">The unique identifier of the Connector Definition.</param>
    /// <param name="fromVersion">The earlier version to compare from.</param>
    /// <param name="toVersion">The later version to compare to.</param>
    /// <returns>The structured diff of the later version against the earlier.</returns>
    /// <response code="200">The diff.</response>
    /// <response code="404">One of the requested versions was not found for the Connector Definition.</response>
    /// <response code="401">User could not be identified from authentication token.</response>
    [HttpGet("connector-definitions/{id:int}/change-history/compare", Name = "CompareConnectorDefinitionChanges")]
    [ProducesResponseType(typeof(ConfigurationDiff), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> CompareConnectorDefinitionChangesAsync(int id, [FromQuery] int fromVersion, [FromQuery] int toVersion)
    {
        var diff = await _application.ChangeHistory.CompareConfigurationChangesAsync(ActivityTargetType.ConnectorDefinition, id, fromVersion, toVersion);
        if (diff == null)
            return NotFound(ApiErrorResponse.NotFound($"Could not compare versions {fromVersion} and {toVersion} for Connector Definition {id}."));
        return Ok(diff);
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

            if (CredentialAttributes.IsCredentialAttribute(csAttr.Name))
                return BadRequest(ApiErrorResponse.BadRequest(CredentialAttributeFlowRejection(csAttr.Name)));

            mapping.TargetConnectedSystemAttributeId = csAttr.Id;
            mapping.TargetConnectedSystemAttribute = csAttr;

            // Initial Export Only applies to export mappings only (#223). The entity default is false;
            // only override when the request supplies a value.
            if (request.InitialExportOnly.HasValue)
                mapping.InitialExportOnly = request.InitialExportOnly.Value;
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

                // Missing Input Behaviour applies to expression sources only: an attribute source has no inputs to
                // be missing. Left at the entity default (EvaluateAnyway) when the request omits it, so an existing
                // caller's mappings behave exactly as they did.
                if (sourceRequest.MissingInputBehaviour.HasValue)
                    source.MissingInputBehaviour = sourceRequest.MissingInputBehaviour.Value;
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

                if (CredentialAttributes.IsCredentialAttribute(csAttr.Name))
                    return BadRequest(ApiErrorResponse.BadRequest(CredentialAttributeFlowRejection(csAttr.Name)));

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
    /// The rejection message used whenever an Attribute Flow names a credential attribute as its source or target.
    /// </summary>
    private static string CredentialAttributeFlowRejection(string attributeName)
    {
        return $"Attribute '{attributeName}' holds credential material and cannot be used in an Attribute Flow. " +
               "Passwords are synchronised through JIM's dedicated password channel, which writes to the Connected System without ever reading the value back into the Metaverse.";
    }

    /// <summary>
    /// Update an Attribute Flow Mapping's settings
    /// </summary>
    /// <remarks>
    /// Changes how an existing Attribute Flow behaves, leaving what it reads and writes alone. Omit a field to
    /// leave that setting as it is. Retargeting a mapping, or swapping its source between an attribute and an
    /// Expression, is not supported here: delete the mapping and create it again.
    /// </remarks>
    /// <param name="syncRuleId">The unique identifier of the Synchronisation Rule.</param>
    /// <param name="mappingId">The unique identifier of the mapping to update.</param>
    /// <param name="request">The settings to change.</param>
    /// <returns>The updated mapping.</returns>
    /// <response code="200">Returns the updated mapping.</response>
    /// <response code="400">The request named no setting, named one that does not apply to this mapping, or carried an invalid Expression.</response>
    /// <response code="404">Synchronisation Rule or mapping not found.</response>
    /// <response code="401">User could not be identified from authentication token.</response>
    [HttpPatch("sync-rules/{syncRuleId:int}/mappings/{mappingId:int}", Name = "UpdateSyncRuleMapping")]
    [ProducesResponseType(typeof(SyncRuleMappingDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> UpdateSyncRuleMappingAsync(int syncRuleId, int mappingId, [FromBody] UpdateSyncRuleMappingRequest request)
    {
        _logger.LogInformation("Updating mapping {MappingId} for Synchronisation Rule {SyncRuleId}", mappingId, syncRuleId);

        var initiatedBy = await GetCurrentUserAsync();
        if (initiatedBy == null && !IsApiKeyAuthenticated())
        {
            _logger.LogWarning("Could not identify user from JWT claims for mapping update");
            return Unauthorized(ApiErrorResponse.Unauthorised("Could not identify user from authentication token."));
        }

        var syncRule = await _application.ConnectedSystems.GetSyncRuleAsync(syncRuleId);
        if (syncRule == null)
            return NotFound(ApiErrorResponse.NotFound($"Synchronisation Rule with ID {syncRuleId} not found."));

        var existing = await _application.ConnectedSystems.GetSyncRuleMappingAsync(mappingId);
        if (existing == null || (existing.SyncRule?.Id ?? existing.SyncRuleId) != syncRuleId)
            return NotFound(ApiErrorResponse.NotFound($"Mapping with ID {mappingId} not found in Synchronisation Rule {syncRuleId}."));

        // Validated here rather than in the application layer, so that a bad Expression is refused by the same
        // evaluator, with the same message, as when the mapping was created.
        if (request.Expression != null)
        {
            var validationResult = _expressionEvaluator.Validate(request.Expression);
            if (!validationResult.IsValid)
                return BadRequest(ApiErrorResponse.BadRequest($"Invalid expression: {validationResult.ErrorMessage}"));
        }

        try
        {
            var apiKey = await GetCurrentApiKeyAsync();
            var updated = apiKey != null
                ? await _application.ConnectedSystems.UpdateSyncRuleMappingSettingsAsync(mappingId, request.ToSettingsUpdate(), apiKey)
                : await _application.ConnectedSystems.UpdateSyncRuleMappingSettingsAsync(mappingId, request.ToSettingsUpdate(), initiatedBy);

            if (updated == null)
                return NotFound(ApiErrorResponse.NotFound($"Mapping with ID {mappingId} not found in Synchronisation Rule {syncRuleId}."));

            _logger.LogInformation("Updated mapping {MappingId} on Synchronisation Rule {SyncRuleId}", mappingId, syncRuleId);
            return Ok(SyncRuleMappingDto.FromEntity(updated));
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning(ex, "Failed to update Synchronisation Rule mapping: {Message}", ex.Message);
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

        var criterion = new SyncRuleScopingCriteria();
        var (error, notFound) = ApplyScopingCriterionRequest(syncRule, criterion, request);
        if (error != null)
            return notFound ? NotFound(ApiErrorResponse.NotFound(error)) : BadRequest(ApiErrorResponse.BadRequest(error));

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
    /// Update a criterion in a Scoping Criteria group (full replacement of attribute, operator and value).
    /// </summary>
    /// <remarks>
    /// For Export Synchronisation Rules, provide <c>MetaverseAttributeId</c>. For Import Synchronisation Rules, provide <c>ConnectedSystemAttributeId</c>.
    /// For DateTime attributes set <c>valueMode</c> to <c>Relative</c> and supply <c>relativeCount</c>/<c>relativeUnit</c>/<c>relativeDirection</c> to compare against a date relative to now.
    /// </remarks>
    /// <param name="syncRuleId">The unique identifier of the Synchronisation Rule.</param>
    /// <param name="groupId">The unique identifier of the criteria group.</param>
    /// <param name="criterionId">The unique identifier of the criterion to update.</param>
    /// <param name="request">The new criterion values.</param>
    /// <returns>The updated criterion.</returns>
    [HttpPut("sync-rules/{syncRuleId:int}/scoping-criteria/{groupId:int}/criteria/{criterionId:int}", Name = "UpdateScopingCriterion")]
    [ProducesResponseType(typeof(SyncRuleScopingCriteriaDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> UpdateScopingCriterionAsync(int syncRuleId, int groupId, int criterionId, [FromBody] CreateScopingCriterionRequest request)
    {
        _logger.LogInformation("Updating criterion {CriterionId} in group {GroupId} for Synchronisation Rule: {SyncRuleId}", criterionId, groupId, syncRuleId);

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

        var (error, notFound) = ApplyScopingCriterionRequest(syncRule, criterion, request);
        if (error != null)
            return notFound ? NotFound(ApiErrorResponse.NotFound(error)) : BadRequest(ApiErrorResponse.BadRequest(error));

        try
        {
            var apiKey = await GetCurrentApiKeyAsync();
            if (apiKey != null)
                await _application.ConnectedSystems.CreateOrUpdateSyncRuleAsync(syncRule, apiKey);
            else
                await _application.ConnectedSystems.CreateOrUpdateSyncRuleAsync(syncRule, initiatedBy);
            _logger.LogInformation("Updated criterion {CriterionId} in group {GroupId}", criterionId, groupId);
            return Ok(SyncRuleScopingCriteriaDto.FromEntity(criterion));
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning(ex, "Failed to update criterion: {Message}", ex.Message);
            return BadRequest(ApiErrorResponse.BadRequest(ex.Message));
        }
    }

    /// <summary>
    /// Resolves the attribute, validates and applies a scoping-criterion request onto <paramref name="criterion"/>
    /// (used by both create and update). The attribute is resolved from the Synchronisation Rule's already-tracked
    /// object-type graph so EF does not track a duplicate. Returns (error, notFound) where notFound distinguishes a
    /// 404 (attribute not found) from a 400 (bad request); null error means success.
    /// </summary>
    private static (string? error, bool notFound) ApplyScopingCriterionRequest(SyncRule syncRule, SyncRuleScopingCriteria criterion, CreateScopingCriterionRequest request)
    {
        if (!Enum.TryParse<SearchComparisonType>(request.ComparisonType, true, out var comparisonType) || comparisonType == SearchComparisonType.NotSet)
            return ($"Invalid comparison type '{request.ComparisonType}'.", false);

        AttributeDataType attributeType;
        if (syncRule.Direction == SyncRuleDirection.Export)
        {
            if (!request.MetaverseAttributeId.HasValue)
                return ("MetaverseAttributeId is required for export Synchronisation Rules.", false);

            var mvAttribute = syncRule.MetaverseObjectType?.Attributes
                .FirstOrDefault(a => a.Id == request.MetaverseAttributeId);
            if (mvAttribute == null)
                return ($"Metaverse attribute with ID {request.MetaverseAttributeId} not found on this Synchronisation Rule's Metaverse Object Type.", true);

            criterion.MetaverseAttribute = mvAttribute;
            criterion.ConnectedSystemAttribute = null;
            attributeType = mvAttribute.Type;
        }
        else
        {
            if (!request.ConnectedSystemAttributeId.HasValue)
                return ("ConnectedSystemAttributeId is required for import Synchronisation Rules.", false);

            var csAttribute = syncRule.ConnectedSystemObjectType?.Attributes
                .FirstOrDefault(a => a.Id == request.ConnectedSystemAttributeId);
            if (csAttribute == null)
                return ($"Connected System attribute with ID {request.ConnectedSystemAttributeId} not found in Synchronisation Rule's object type.", true);

            criterion.ConnectedSystemAttribute = csAttribute;
            criterion.MetaverseAttribute = null;
            attributeType = csAttribute.Type;
        }

        var relativeError = RelativeDateCriterionValidation.Validate(
            request.ValueMode, request.RelativeCount, request.RelativeUnit, request.RelativeDirection,
            attributeType, request.DateTimeValue.HasValue,
            out var valueMode, out var relativeCount, out var relativeUnit, out var relativeDirection);
        if (relativeError != null)
            return (relativeError, false);

        criterion.ComparisonType = comparisonType;
        criterion.CaseSensitive = request.CaseSensitive;
        criterion.ValueMode = valueMode;

        if (valueMode == DateCriteriaValueMode.Relative)
        {
            criterion.RelativeCount = relativeCount;
            criterion.RelativeUnit = relativeUnit;
            criterion.RelativeDirection = relativeDirection;
            // Relative criteria are DateTime-only; clear the absolute value carriers.
            criterion.StringValue = null;
            criterion.IntValue = null;
            criterion.LongValue = null;
            criterion.DecimalValue = null;
            criterion.DateTimeValue = null;
            criterion.BoolValue = null;
            criterion.GuidValue = null;
        }
        else
        {
            criterion.StringValue = request.StringValue;
            criterion.IntValue = request.IntValue;
            criterion.LongValue = request.LongValue;
            criterion.DecimalValue = request.DecimalValue;
            criterion.DateTimeValue = request.DateTimeValue;
            criterion.BoolValue = request.BoolValue;
            criterion.GuidValue = request.GuidValue;
            criterion.RelativeCount = null;
            criterion.RelativeUnit = null;
            criterion.RelativeDirection = null;
        }

        return (null, false);
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

        var rule = await _application.ConnectedSystems.GetObjectMatchingRuleAsync(ruleId);
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
            else
            {
                return BadRequest(ApiErrorResponse.BadRequest("Each source must specify ConnectedSystemAttributeId."));
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

        var rule = await _application.ConnectedSystems.GetObjectMatchingRuleAsync(ruleId);
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
                else
                {
                    return BadRequest(ApiErrorResponse.BadRequest("Each source must specify ConnectedSystemAttributeId."));
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

        var rule = await _application.ConnectedSystems.GetObjectMatchingRuleAsync(ruleId);
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
            else
            {
                return BadRequest(ApiErrorResponse.BadRequest("Each source must specify ConnectedSystemAttributeId."));
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

        var rule = await _application.ConnectedSystems.GetObjectMatchingRuleAsync(ruleId);
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
                else
                {
                    return BadRequest(ApiErrorResponse.BadRequest("Each source must specify ConnectedSystemAttributeId."));
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

        var rule = await _application.ConnectedSystems.GetObjectMatchingRuleAsync(ruleId);
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
