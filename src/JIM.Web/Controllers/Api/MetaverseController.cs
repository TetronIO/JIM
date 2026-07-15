// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using Asp.Versioning;
using JIM.Web.Extensions.Api;
using JIM.Web.Models.Api;
using JIM.Application;
using JIM.Application.Exceptions;
using JIM.Models.Activities;
using JIM.Models.Activities.DTOs;
using JIM.Models.Core;
using JIM.Models.Core.DTOs;
using JIM.Utilities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace JIM.Web.Controllers.Api;

/// <summary>
/// API controller for managing Metaverse schema and objects.
/// </summary>
/// <remarks>
/// The Metaverse is the central identity store in JIM. This controller provides
/// endpoints for managing Object Types, Attributes, and individual Metaverse Objects.
/// </remarks>
[Route("api/v{version:apiVersion}/[controller]")]
[ApiController]
[ApiVersion("1.0")]
[Authorize(Roles = "Administrator")]
[Produces("application/json")]
public class MetaverseController(ILogger<MetaverseController> logger, JimApplication application) : ApiControllerBase(application, logger)
{
    private readonly ILogger<MetaverseController> _logger = logger;
    private readonly JimApplication _application = application;

    /// <summary>
    /// List Metaverse Object Types
    /// </summary>
    /// <param name="pagination">Pagination parameters (page, pageSize, sortBy, sortDirection, filter).</param>
    /// <param name="includeChildObjects">Whether to include child object counts in the response.</param>
    /// <returns>A paginated list of Metaverse Object Type headers.</returns>
    [HttpGet("object-types", Name = "GetObjectTypes")]
    [ProducesResponseType(typeof(PaginatedResponse<MetaverseObjectTypeHeader>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetObjectTypesAsync([FromQuery] PaginationRequest pagination, bool includeChildObjects = false)
    {
        _logger.LogTrace("Requested Metaverse Object Types (Page: {Page}, PageSize: {PageSize})", pagination.Page, pagination.PageSize);
        var objectTypes = await _application.Metaverse.GetMetaverseObjectTypesAsync(includeChildObjects);
        var headers = objectTypes.Select(MetaverseObjectTypeHeader.FromEntity).AsQueryable();

        var result = headers
            .ApplySortAndFilter(pagination)
            .ToPaginatedResponse(pagination);

        return Ok(result);
    }

    /// <summary>
    /// Get a Metaverse Object Type
    /// </summary>
    /// <param name="id">The unique identifier of the Object Type.</param>
    /// <param name="includeChildObjects">Whether to include child object details in the response.</param>
    /// <returns>The Object Type details.</returns>
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
    /// Create a Metaverse Object Type
    /// </summary>
    /// <param name="request">The Object Type creation request.</param>
    /// <returns>The created Object Type details.</returns>
    /// <response code="201">Object Type created successfully.</response>
    /// <response code="400">Invalid request or validation failed.</response>
    /// <response code="401">User not authenticated.</response>
    [HttpPost("object-types", Name = "CreateObjectType")]
    [ProducesResponseType(typeof(MetaverseObjectTypeDetailDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> CreateObjectTypeAsync([FromBody] CreateMetaverseObjectTypeRequest request)
    {
        _logger.LogInformation("Creating Metaverse Object Type: {Name}", LogSanitiser.Sanitise(request.Name));

        // Reject collisions on Name or PluralName up front. The DB has unique constraints
        // but a 400 with a clear message is a better caller experience than a 500 from EF.
        var existingByName = await _application.Metaverse.GetMetaverseObjectTypeAsync(request.Name, includeChildObjects: false);
        if (existingByName != null)
            return BadRequest(ApiErrorResponse.BadRequest($"Object type with name '{request.Name}' already exists."));

        var existingByPluralName = await _application.Metaverse.GetMetaverseObjectTypeByPluralNameAsync(request.PluralName, includeChildObjects: false);
        if (existingByPluralName != null)
            return BadRequest(ApiErrorResponse.BadRequest($"Object type with plural name '{request.PluralName}' already exists."));

        // Negative grace period is never valid; the DB column tolerates it but the semantic is nonsense.
        if (request.DeletionGracePeriod.HasValue && request.DeletionGracePeriod.Value < TimeSpan.Zero)
            return BadRequest(ApiErrorResponse.BadRequest("DeletionGracePeriod cannot be negative."));

        var deletionRule = request.DeletionRule ?? MetaverseObjectDeletionRule.Manual;

        // WhenAuthoritativeSourceDisconnected requires at least one trigger system; same rule as Update.
        if (deletionRule == MetaverseObjectDeletionRule.WhenAuthoritativeSourceDisconnected &&
            (request.DeletionTriggerConnectedSystemIds == null || request.DeletionTriggerConnectedSystemIds.Count == 0))
        {
            return BadRequest(ApiErrorResponse.BadRequest("WhenAuthoritativeSourceDisconnected deletion rule requires at least one authoritative source to be specified in DeletionTriggerConnectedSystemIds."));
        }

        // Validate trigger system IDs exist if supplied (regardless of rule type, to surface
        // bad input early rather than silently storing dead IDs).
        if (request.DeletionTriggerConnectedSystemIds != null && request.DeletionTriggerConnectedSystemIds.Count > 0)
        {
            foreach (var connectedSystemId in request.DeletionTriggerConnectedSystemIds)
            {
                var connectedSystem = await _application.ConnectedSystems.GetConnectedSystemCoreAsync(connectedSystemId);
                if (connectedSystem == null)
                    return BadRequest(ApiErrorResponse.BadRequest($"Connected System with ID {connectedSystemId} not found."));
            }
        }

        var objectType = new MetaverseObjectType
        {
            Name = request.Name,
            PluralName = request.PluralName,
            Icon = request.Icon,
            BuiltIn = false,
            DeletionRule = deletionRule,
            DeletionGracePeriod = (request.DeletionGracePeriod.HasValue && request.DeletionGracePeriod.Value > TimeSpan.Zero)
                ? request.DeletionGracePeriod.Value
                : null,
            DeletionTriggerConnectedSystemIds = request.DeletionTriggerConnectedSystemIds ?? new List<int>(),
            Created = DateTime.UtcNow,
            Attributes = new List<MetaverseAttribute>()
        };

        // Associate with existing attributes if specified. Look each one up so we surface
        // a clear 400 rather than letting EF raise a confusing FK error inside the create call.
        if (request.AttributeIds != null && request.AttributeIds.Count > 0)
        {
            foreach (var attributeId in request.AttributeIds)
            {
                var attribute = await _application.Metaverse.GetMetaverseAttributeAsync(attributeId);
                if (attribute == null)
                    return BadRequest(ApiErrorResponse.BadRequest($"Attribute with ID {attributeId} not found."));

                objectType.Attributes.Add(attribute);
            }
        }

        var apiKey = await GetCurrentApiKeyAsync();
        if (apiKey != null)
            await _application.Metaverse.CreateMetaverseObjectTypeAsync(objectType, apiKey, request.ChangeReason);
        else
            await _application.Metaverse.CreateMetaverseObjectTypeAsync(objectType, await GetCurrentUserAsync(), request.ChangeReason);

        _logger.LogInformation("Created Metaverse Object Type: {Id} ({Name})", objectType.Id, LogSanitiser.Sanitise(objectType.Name));

        var result = await _application.Metaverse.GetMetaverseObjectTypeAsync(objectType.Id, includeChildObjects: false);
        return Created($"/api/v1/metaverse/object-types/{objectType.Id}", MetaverseObjectTypeDetailDto.FromEntity(result!));
    }

    /// <summary>
    /// Update a Metaverse Object Type
    /// </summary>
    /// <param name="id">The unique identifier of the Object Type.</param>
    /// <param name="request">The update request containing deletion rule settings.</param>
    /// <returns>The updated Object Type details.</returns>
    /// <response code="200">Object Type updated successfully.</response>
    /// <response code="400">Invalid request or validation failed.</response>
    /// <response code="404">Object Type not found.</response>
    /// <response code="401">User not authenticated.</response>
    [HttpPut("object-types/{id:int}", Name = "UpdateObjectType")]
    [ProducesResponseType(typeof(MetaverseObjectTypeDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> UpdateObjectTypeAsync(int id, [FromBody] UpdateMetaverseObjectTypeRequest request)
    {
        _logger.LogInformation("Updating Metaverse Object Type: {Id}", id);

        var objectType = await _application.Metaverse.GetMetaverseObjectTypeAsync(id, false);
        if (objectType == null)
            return NotFound(ApiErrorResponse.NotFound($"Object type with ID {id} not found."));

        // Identity fields (Name, PluralName, Icon). Built-in types reject changes to these; only their deletion rules
        // may be changed. Custom types apply the change with a case-insensitive uniqueness re-check. Icon follows the
        // clearable-field convention: null leaves it unchanged, empty/whitespace clears it.
        var trimmedName = request.Name?.Trim();
        var trimmedPluralName = request.PluralName?.Trim();
        var normalisedIcon = string.IsNullOrWhiteSpace(request.Icon) ? null : request.Icon.Trim();

        var wantsNameChange = !string.IsNullOrEmpty(trimmedName) && trimmedName != objectType.Name;
        var wantsPluralNameChange = !string.IsNullOrEmpty(trimmedPluralName) && trimmedPluralName != objectType.PluralName;
        var wantsIconChange = request.Icon != null && normalisedIcon != objectType.Icon;

        if (objectType.BuiltIn && (wantsNameChange || wantsPluralNameChange || wantsIconChange))
            return BadRequest(ApiErrorResponse.BadRequest("Built-in Metaverse Object Types cannot be renamed or re-iconed; only their deletion rules can be changed."));

        if (wantsNameChange)
        {
            var clashByName = await _application.Metaverse.GetMetaverseObjectTypeAsync(trimmedName!, includeChildObjects: false);
            if (clashByName != null && clashByName.Id != objectType.Id)
                return BadRequest(ApiErrorResponse.BadRequest($"Object type with name '{trimmedName}' already exists."));
            objectType.Name = trimmedName!;
        }

        if (wantsPluralNameChange)
        {
            var clashByPluralName = await _application.Metaverse.GetMetaverseObjectTypeByPluralNameAsync(trimmedPluralName!, includeChildObjects: false);
            if (clashByPluralName != null && clashByPluralName.Id != objectType.Id)
                return BadRequest(ApiErrorResponse.BadRequest($"Object type with plural name '{trimmedPluralName}' already exists."));
            objectType.PluralName = trimmedPluralName!;
        }

        if (wantsIconChange)
            objectType.Icon = normalisedIcon;

        // Apply updates
        if (request.DeletionRule.HasValue)
            objectType.DeletionRule = request.DeletionRule.Value;

        if (request.DeletionGracePeriod.HasValue)
        {
            if (request.DeletionGracePeriod.Value < TimeSpan.Zero)
                return BadRequest(ApiErrorResponse.BadRequest("DeletionGracePeriod cannot be negative."));
            objectType.DeletionGracePeriod = request.DeletionGracePeriod.Value == TimeSpan.Zero ? null : request.DeletionGracePeriod.Value;
        }

        if (request.DeletionTriggerConnectedSystemIds != null)
        {
            // Validate that the Connected System IDs exist (Core retrieval — we only need existence).
            foreach (var connectedSystemId in request.DeletionTriggerConnectedSystemIds)
            {
                var connectedSystem = await _application.ConnectedSystems.GetConnectedSystemCoreAsync(connectedSystemId);
                if (connectedSystem == null)
                    return BadRequest(ApiErrorResponse.BadRequest($"Connected System with ID {connectedSystemId} not found."));
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

        var apiKey = await GetCurrentApiKeyAsync();
        if (apiKey != null)
            await _application.Metaverse.UpdateMetaverseObjectTypeAsync(objectType, apiKey, request.ChangeReason);
        else
            await _application.Metaverse.UpdateMetaverseObjectTypeAsync(objectType, await GetCurrentUserAsync(), request.ChangeReason);

        _logger.LogInformation("Updated Metaverse Object Type: {Id} ({Name}) - DeletionRule: {DeletionRule}, GracePeriod: {GracePeriod}",
            objectType.Id, LogSanitiser.Sanitise(objectType.Name), objectType.DeletionRule.ToString(), objectType.DeletionGracePeriod?.ToString());

        var result = await _application.Metaverse.GetMetaverseObjectTypeAsync(objectType.Id, false);
        return Ok(MetaverseObjectTypeDetailDto.FromEntity(result!));
    }

    /// <summary>
    /// Check whether a Metaverse Object Type name and/or plural name are available
    /// </summary>
    /// <remarks>
    /// Backs the real-time create/edit dialog validator. Comparisons are case-insensitive. Supply
    /// <paramref name="excludeId"/> when validating an edit so the type does not clash with its own current names.
    /// At least one of <paramref name="name"/> or <paramref name="pluralName"/> must be supplied.
    /// </remarks>
    /// <param name="name">The candidate singular name.</param>
    /// <param name="pluralName">The candidate plural name.</param>
    /// <param name="excludeId">Optional Object Type ID to exclude (the type being edited).</param>
    [HttpGet("object-types/name-availability", Name = "GetObjectTypeNameAvailability")]
    [ProducesResponseType(typeof(MetaverseObjectTypeNameAvailabilityDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetObjectTypeNameAvailabilityAsync([FromQuery] string? name = null, [FromQuery] string? pluralName = null, [FromQuery] int? excludeId = null)
    {
        if (string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(pluralName))
            return BadRequest(ApiErrorResponse.BadRequest("Supply a name and/or pluralName to check."));

        var response = new MetaverseObjectTypeNameAvailabilityDto();

        if (!string.IsNullOrWhiteSpace(name))
        {
            response.Name = name;
            response.NameAvailable = await _application.Metaverse.IsMetaverseObjectTypeNameAvailableAsync(name, excludeId);
        }

        if (!string.IsNullOrWhiteSpace(pluralName))
        {
            response.PluralName = pluralName;
            response.PluralNameAvailable = await _application.Metaverse.IsMetaverseObjectTypePluralNameAvailableAsync(pluralName, excludeId);
        }

        return Ok(response);
    }

    /// <summary>
    /// Preview the impact of deleting a Metaverse Object Type
    /// </summary>
    /// <remarks>
    /// Returns the two hard blocks (Metaverse Objects of the type, and Synchronisation Rules targeting it) and the
    /// softer references (Predefined Searches, Example Data Templates, attribute bindings) that would be cascade-removed,
    /// with <c>Blocked</c> and <c>RequiresConfirmation</c> flags. No change is made.
    /// </remarks>
    /// <param name="id">The unique identifier of the Object Type.</param>
    [HttpGet("object-types/{id:int}/delete-preview", Name = "GetObjectTypeDeletePreview")]
    [ProducesResponseType(typeof(ObjectTypeDeletionImpact), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetObjectTypeDeletePreviewAsync(int id)
    {
        var objectType = await _application.Metaverse.GetMetaverseObjectTypeAsync(id, false);
        if (objectType == null)
            return NotFound(ApiErrorResponse.NotFound($"Object type with ID {id} not found."));

        var impact = await _application.Metaverse.EvaluateObjectTypeDeletionAsync(objectType);
        return Ok(impact);
    }

    /// <summary>
    /// Delete a Metaverse Object Type
    /// </summary>
    /// <remarks>
    /// Built-in types cannot be deleted (400). Metaverse Objects of the type, or Synchronisation Rules targeting it,
    /// are hard blocks (409 with the impact) and must be removed first. When the deletion would cascade softer
    /// references (Predefined Searches, Example Data Templates, attribute bindings) it requires
    /// <paramref name="confirmationName"/> to exactly match the Object Type's name (400 otherwise). The bound attributes
    /// themselves are not deleted; only their bindings to this type are removed.
    /// </remarks>
    /// <param name="id">The unique identifier of the Object Type.</param>
    /// <param name="confirmationName">Required only when the deletion will cascade references: the Object Type's exact name.</param>
    /// <param name="changeReason">Optional reason for the change, recorded on the audit Activity.</param>
    /// <response code="200">Deleted; returns the impact listing what was removed.</response>
    /// <response code="400">The Object Type is built-in, or a required confirmation name is missing or mismatched.</response>
    /// <response code="404">Object Type not found.</response>
    /// <response code="409">Refused because Metaverse Objects of the type or Synchronisation Rules targeting it exist; returns the blocking impact.</response>
    /// <response code="401">User not authenticated.</response>
    [HttpDelete("object-types/{id:int}", Name = "DeleteObjectType")]
    [ProducesResponseType(typeof(ObjectTypeDeletionImpact), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ObjectTypeDeletionImpact), StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> DeleteObjectTypeAsync(int id, [FromQuery] string? confirmationName = null, [FromQuery] string? changeReason = null)
    {
        _logger.LogInformation("Deleting Metaverse Object Type: {Id}", id);

        var objectType = await _application.Metaverse.GetMetaverseObjectTypeAsync(id, false);
        if (objectType == null)
            return NotFound(ApiErrorResponse.NotFound($"Object type with ID {id} not found."));

        if (objectType.BuiltIn)
            return BadRequest(ApiErrorResponse.BadRequest("Built-in Metaverse Object Types cannot be deleted."));

        var impact = await _application.Metaverse.EvaluateObjectTypeDeletionAsync(objectType);

        if (impact.Blocked)
            return Conflict(impact);

        if (impact.RequiresConfirmation && !string.Equals(confirmationName, objectType.Name, StringComparison.Ordinal))
            return BadRequest(ApiErrorResponse.BadRequest(
                $"This deletion will cascade-remove references. To confirm, supply confirmationName exactly matching the Object Type name '{objectType.Name}'."));

        var apiKey = await GetCurrentApiKeyAsync();
        var result = apiKey != null
            ? await _application.Metaverse.DeleteMetaverseObjectTypeAsync(objectType, apiKey, changeReason)
            : await _application.Metaverse.DeleteMetaverseObjectTypeAsync(objectType, await GetCurrentUserAsync(), changeReason);

        _logger.LogInformation("Deleted Metaverse Object Type: {Id} ({Name})", id, LogSanitiser.Sanitise(objectType.Name));
        return Ok(result);
    }

    /// <summary>
    /// List Metaverse Attributes
    /// </summary>
    /// <param name="pagination">Pagination parameters (page, pageSize, sortBy, sortDirection, filter).</param>
    /// <returns>A paginated list of Metaverse Attribute headers.</returns>
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
    /// Get a Metaverse Attribute
    /// </summary>
    /// <param name="id">The unique identifier of the Attribute.</param>
    /// <returns>The Attribute details.</returns>
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
    /// Create a Metaverse Attribute
    /// </summary>
    /// <param name="request">The Attribute creation request.</param>
    /// <returns>The created Attribute details.</returns>
    /// <response code="201">Attribute created successfully.</response>
    /// <response code="400">Invalid request or validation failed.</response>
    /// <response code="401">User not authenticated.</response>
    /// <response code="409">An attribute with the same name (compared case-insensitively) already exists.</response>
    [HttpPost("attributes", Name = "CreateAttribute")]
    [ProducesResponseType(typeof(MetaverseAttributeDetailDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> CreateAttributeAsync([FromBody] CreateMetaverseAttributeRequest request)
    {
        _logger.LogInformation("Creating metaverse attribute: {Name}", LogSanitiser.Sanitise(request.Name));

        // Enforce case-insensitive uniqueness (e.g. 'CostCentre' clashes with 'costCentre'). Names are stored as-is.
        if (!await _application.Metaverse.IsMetaverseAttributeNameUniqueAsync(request.Name))
            return Conflict(ApiErrorResponse.Conflict($"A Metaverse Attribute named '{request.Name}' already exists (names are compared case-insensitively)."));

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
            await _application.Metaverse.CreateMetaverseAttributeAsync(attribute, apiKey, request.ChangeReason);
        else
            await _application.Metaverse.CreateMetaverseAttributeAsync(attribute, await GetCurrentUserAsync(), request.ChangeReason);

        _logger.LogInformation("Created metaverse attribute: {Id} ({Name})", attribute.Id, LogSanitiser.Sanitise(attribute.Name));

        var result = await _application.Metaverse.GetMetaverseAttributeAsync(attribute.Id);
        // Use Created with explicit URL instead of CreatedAtAction to avoid API versioning route generation issues
        return Created($"/api/v1/metaverse/attributes/{attribute.Id}", MetaverseAttributeDetailDto.FromEntity(result!));
    }

    /// <summary>
    /// Check whether a Metaverse Attribute name is available
    /// </summary>
    /// <remarks>
    /// Backs the real-time create/rename validator. The comparison is case-insensitive, so <c>CostCentre</c> is taken
    /// if <c>costCentre</c> exists. Supply <paramref name="excludeId"/> when validating a rename so the attribute does
    /// not clash with its own current name.
    /// </remarks>
    /// <param name="name">The candidate name.</param>
    /// <param name="excludeId">Optional attribute ID to exclude (the attribute being renamed).</param>
    [HttpGet("attributes/name-availability", Name = "GetAttributeNameAvailability")]
    [ProducesResponseType(typeof(MetaverseAttributeNameAvailabilityDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetAttributeNameAvailabilityAsync([FromQuery] string name, [FromQuery] int? excludeId = null)
    {
        if (string.IsNullOrWhiteSpace(name))
            return BadRequest(ApiErrorResponse.BadRequest("A name to check is required."));

        var available = await _application.Metaverse.IsMetaverseAttributeNameUniqueAsync(name, excludeId);
        return Ok(new MetaverseAttributeNameAvailabilityDto { Name = name, Available = available });
    }

    /// <summary>
    /// Update a Metaverse Attribute's name and rendering configuration
    /// </summary>
    /// <remarks>
    /// Renames the attribute (subject to the same case-insensitive uniqueness check as creation) and/or updates its
    /// rendering hint. Type and plurality are changed via the schema endpoint; Object Type bindings via the bind /
    /// unassign endpoints. Built-in attributes cannot be modified.
    /// </remarks>
    /// <param name="id">The unique identifier of the Attribute.</param>
    /// <param name="request">The update request.</param>
    /// <response code="200">Attribute updated successfully.</response>
    /// <response code="400">Invalid request, or the attribute is built-in.</response>
    /// <response code="404">Attribute not found.</response>
    /// <response code="409">The requested name is already used by another attribute (compared case-insensitively).</response>
    /// <response code="401">User not authenticated.</response>
    [HttpPatch("attributes/{id:int}", Name = "UpdateAttribute")]
    [ProducesResponseType(typeof(MetaverseAttributeDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> UpdateAttributeAsync(int id, [FromBody] UpdateMetaverseAttributeRequest request)
    {
        _logger.LogInformation("Updating metaverse attribute: {Id}", id);

        if (string.IsNullOrWhiteSpace(request.Name) && !request.RenderingHint.HasValue)
            return BadRequest(ApiErrorResponse.BadRequest("Supply a new Name and/or RenderingHint to update."));

        var attribute = await _application.Metaverse.GetMetaverseAttributeAsync(id);
        if (attribute == null)
            return NotFound(ApiErrorResponse.NotFound($"Attribute with ID {id} not found."));

        if (attribute.BuiltIn)
            return BadRequest(ApiErrorResponse.BadRequest("Built-in attributes cannot be modified."));

        var apiKey = await GetCurrentApiKeyAsync();
        var user = apiKey == null ? await GetCurrentUserAsync() : null;

        // Rename via the audited rename path when a different name is supplied (re-checks case-insensitive uniqueness).
        if (!string.IsNullOrWhiteSpace(request.Name) && !string.Equals(request.Name, attribute.Name, StringComparison.Ordinal))
        {
            try
            {
                if (apiKey != null)
                    await _application.Metaverse.RenameMetaverseAttributeAsync(id, request.Name, apiKey, request.ChangeReason);
                else
                    await _application.Metaverse.RenameMetaverseAttributeAsync(id, request.Name, user, request.ChangeReason);
            }
            catch (MetaverseAttributeNameConflictException ex)
            {
                return Conflict(ApiErrorResponse.Conflict(ex.Message));
            }
        }

        // Rendering hint via the generic audited update path.
        if (request.RenderingHint.HasValue)
        {
            var tracked = await _application.Metaverse.GetMetaverseAttributeAsync(id, withChangeTracking: true);
            if (tracked != null)
            {
                tracked.RenderingHint = request.RenderingHint.Value;
                if (apiKey != null)
                    await _application.Metaverse.UpdateMetaverseAttributeAsync(tracked, apiKey, request.ChangeReason);
                else
                    await _application.Metaverse.UpdateMetaverseAttributeAsync(tracked, user, request.ChangeReason);
            }
        }

        _logger.LogInformation("Updated metaverse attribute: {Id}", id);

        var result = await _application.Metaverse.GetMetaverseAttributeWithObjectTypesAsync(id);
        return Ok(MetaverseAttributeDetailDto.FromEntity(result!));
    }

    /// <summary>
    /// Change a Metaverse Attribute's data type and/or plurality
    /// </summary>
    /// <remarks>
    /// Refused while any Metaverse Object holds a stored value for the attribute; in that case a 409 is returned with
    /// the blocking impact (the stored-value count). Built-in attributes cannot be changed.
    /// </remarks>
    /// <param name="id">The unique identifier of the Attribute.</param>
    /// <param name="request">The new type and plurality.</param>
    /// <response code="200">Schema changed successfully; returns the updated attribute.</response>
    /// <response code="400">Invalid request, or the attribute is built-in.</response>
    /// <response code="404">Attribute not found.</response>
    /// <response code="409">Refused because Metaverse Objects hold stored values; returns the blocking impact.</response>
    /// <response code="401">User not authenticated.</response>
    [HttpPatch("attributes/{id:int}/schema", Name = "ChangeAttributeSchema")]
    [ProducesResponseType(typeof(MetaverseAttributeDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(AttributeSchemaChangeImpact), StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> ChangeAttributeSchemaAsync(int id, [FromBody] ChangeMetaverseAttributeSchemaRequest request)
    {
        _logger.LogInformation("Changing schema of metaverse attribute: {Id}", id);

        var attribute = await _application.Metaverse.GetMetaverseAttributeAsync(id);
        if (attribute == null)
            return NotFound(ApiErrorResponse.NotFound($"Attribute with ID {id} not found."));

        if (attribute.BuiltIn)
            return BadRequest(ApiErrorResponse.BadRequest("Built-in attributes cannot be changed."));

        var apiKey = await GetCurrentApiKeyAsync();
        var impact = apiKey != null
            ? await _application.Metaverse.ChangeMetaverseAttributeSchemaAsync(id, request.Type, request.AttributePlurality, apiKey, request.ChangeReason)
            : await _application.Metaverse.ChangeMetaverseAttributeSchemaAsync(id, request.Type, request.AttributePlurality, await GetCurrentUserAsync(), request.ChangeReason);

        if (impact.BlockedByValues)
            return Conflict(impact);

        var result = await _application.Metaverse.GetMetaverseAttributeWithObjectTypesAsync(id);
        return Ok(MetaverseAttributeDetailDto.FromEntity(result!));
    }

    /// <summary>
    /// Preview the impact of deleting a Metaverse Attribute
    /// </summary>
    /// <remarks>
    /// Returns whether stored values block the deletion (with per-Object-Type counts) or, when only configuration
    /// references exist, the list of references that would be cascade-removed. No change is made. Use this to render
    /// the confirmation dialog; the delete itself enforces the same rules server-side.
    /// </remarks>
    /// <param name="id">The unique identifier of the Attribute.</param>
    [HttpGet("attributes/{id:int}/deletion-preview", Name = "GetAttributeDeletionPreview")]
    [ProducesResponseType(typeof(AttributeDeletionImpact), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetAttributeDeletionPreviewAsync(int id)
    {
        var attribute = await _application.Metaverse.GetMetaverseAttributeAsync(id);
        if (attribute == null)
            return NotFound(ApiErrorResponse.NotFound($"Attribute with ID {id} not found."));

        var impact = await _application.Metaverse.EvaluateAttributeDeletionAsync(attribute);
        return Ok(impact);
    }

    /// <summary>
    /// Delete a Metaverse Attribute
    /// </summary>
    /// <remarks>
    /// Stored values are the only hard block: if any Metaverse Object holds a value a 409 is returned with the
    /// per-Object-Type counts. When only configuration references exist they are cascade-removed, but only when
    /// <paramref name="confirmationName"/> exactly matches the attribute's name (the server-enforced type-the-name
    /// gate); a missing or mismatched confirmation returns 400. On success the response lists what was removed.
    /// Built-in attributes cannot be deleted.
    /// </remarks>
    /// <param name="id">The unique identifier of the Attribute to delete.</param>
    /// <param name="confirmationName">Required only when the deletion will cascade references: the attribute's exact name.</param>
    /// <param name="changeReason">Optional reason for the deletion, recorded on the audit Activity.</param>
    /// <response code="200">Attribute deleted; returns the impact listing any references removed.</response>
    /// <response code="400">The attribute is built-in, or a required confirmation name is missing or mismatched.</response>
    /// <response code="404">Attribute not found.</response>
    /// <response code="409">Refused because Metaverse Objects hold stored values; returns the blocking impact.</response>
    /// <response code="401">User not authenticated.</response>
    [HttpDelete("attributes/{id:int}", Name = "DeleteAttribute")]
    [ProducesResponseType(typeof(AttributeDeletionImpact), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(AttributeDeletionImpact), StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> DeleteAttributeAsync(int id, [FromQuery] string? confirmationName = null, [FromQuery] string? changeReason = null)
    {
        _logger.LogInformation("Deleting metaverse attribute: {Id}", id);

        var attribute = await _application.Metaverse.GetMetaverseAttributeAsync(id);
        if (attribute == null)
            return NotFound(ApiErrorResponse.NotFound($"Attribute with ID {id} not found."));

        if (attribute.BuiltIn)
            return BadRequest(ApiErrorResponse.BadRequest("Built-in attributes cannot be deleted."));

        var impact = await _application.Metaverse.EvaluateAttributeDeletionAsync(attribute);

        // Stored values are the only hard block.
        if (impact.BlockedByValues)
            return Conflict(impact);

        // Cascade deletes require the type-the-name confirmation, enforced here so REST/PowerShell callers cannot
        // accidentally mass-delete referenced attributes.
        if (impact.RequiresConfirmation && !string.Equals(confirmationName, attribute.Name, StringComparison.Ordinal))
            return BadRequest(ApiErrorResponse.BadRequest(
                $"This deletion will remove {impact.References.Count} configuration reference(s). To confirm, supply confirmationName exactly matching the attribute name '{attribute.Name}'."));

        var apiKey = await GetCurrentApiKeyAsync();
        var result = apiKey != null
            ? await _application.Metaverse.DeleteMetaverseAttributeWithCascadeAsync(attribute, apiKey, changeReason)
            : await _application.Metaverse.DeleteMetaverseAttributeWithCascadeAsync(attribute, await GetCurrentUserAsync(), changeReason);

        _logger.LogInformation("Deleted metaverse attribute: {Id} (removed {ReferenceCount} reference(s))", id, result.References.Count);
        return Ok(result);
    }

    /// <summary>
    /// Bind a Metaverse Attribute to a Metaverse Object Type
    /// </summary>
    /// <remarks>
    /// Idempotent. Built-in attributes cannot have their bindings modified.
    /// </remarks>
    /// <param name="id">The unique identifier of the Attribute.</param>
    /// <param name="objectTypeId">The Metaverse Object Type to bind the attribute to.</param>
    /// <param name="changeReason">Optional reason for the change, recorded on the audit Activity.</param>
    /// <response code="200">Bound successfully; returns the updated attribute.</response>
    /// <response code="400">The attribute is built-in.</response>
    /// <response code="404">Attribute or Object Type not found.</response>
    /// <response code="401">User not authenticated.</response>
    [HttpPost("attributes/{id:int}/object-types/{objectTypeId:int}", Name = "BindAttributeToObjectType")]
    [ProducesResponseType(typeof(MetaverseAttributeDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> BindAttributeToObjectTypeAsync(int id, int objectTypeId, [FromQuery] string? changeReason = null)
    {
        _logger.LogInformation("Binding metaverse attribute {Id} to object type {ObjectTypeId}", id, objectTypeId);

        var attribute = await _application.Metaverse.GetMetaverseAttributeAsync(id);
        if (attribute == null)
            return NotFound(ApiErrorResponse.NotFound($"Attribute with ID {id} not found."));

        if (attribute.BuiltIn)
            return BadRequest(ApiErrorResponse.BadRequest("Built-in attributes cannot have their bindings modified."));

        var objectType = await _application.Metaverse.GetMetaverseObjectTypeAsync(objectTypeId, false);
        if (objectType == null)
            return NotFound(ApiErrorResponse.NotFound($"Object type with ID {objectTypeId} not found."));

        var apiKey = await GetCurrentApiKeyAsync();
        if (apiKey != null)
            await _application.Metaverse.BindAttributeToObjectTypeAsync(id, objectTypeId, apiKey, changeReason);
        else
            await _application.Metaverse.BindAttributeToObjectTypeAsync(id, objectTypeId, await GetCurrentUserAsync(), changeReason);

        var result = await _application.Metaverse.GetMetaverseAttributeWithObjectTypesAsync(id);
        return Ok(MetaverseAttributeDetailDto.FromEntity(result!));
    }

    /// <summary>
    /// Preview the impact of unassigning a Metaverse Attribute from a Metaverse Object Type
    /// </summary>
    /// <remarks>
    /// Returns whether stored values of that type block the unassignment, and the type-scoped references (plus the
    /// binding) that would be removed, with a <c>RequiresConfirmation</c> flag. No change is made.
    /// </remarks>
    /// <param name="id">The unique identifier of the Attribute.</param>
    /// <param name="objectTypeId">The Metaverse Object Type to unassign from.</param>
    [HttpGet("attributes/{id:int}/object-types/{objectTypeId:int}/unassign-preview", Name = "GetAttributeUnassignPreview")]
    [ProducesResponseType(typeof(AttributeUnassignImpact), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetAttributeUnassignPreviewAsync(int id, int objectTypeId)
    {
        var attribute = await _application.Metaverse.GetMetaverseAttributeAsync(id);
        if (attribute == null)
            return NotFound(ApiErrorResponse.NotFound($"Attribute with ID {id} not found."));

        var impact = await _application.Metaverse.EvaluateAttributeUnassignAsync(id, objectTypeId);
        return Ok(impact);
    }

    /// <summary>
    /// Unassign a Metaverse Attribute from a Metaverse Object Type
    /// </summary>
    /// <remarks>
    /// Stored values held by Metaverse Objects of that type are the only hard block (409 with the count). When the
    /// unassignment would cascade type-scoped references it requires <paramref name="confirmationName"/> to exactly
    /// match the attribute's name (400 otherwise). Attribute-global references and the attribute itself are untouched.
    /// Built-in attributes cannot be unassigned.
    /// </remarks>
    /// <param name="id">The unique identifier of the Attribute.</param>
    /// <param name="objectTypeId">The Metaverse Object Type to unassign from.</param>
    /// <param name="confirmationName">Required only when the unassignment will cascade references: the attribute's exact name.</param>
    /// <param name="changeReason">Optional reason for the change, recorded on the audit Activity.</param>
    /// <response code="200">Unassigned; returns the impact listing what was removed.</response>
    /// <response code="400">The attribute is built-in, or a required confirmation name is missing or mismatched.</response>
    /// <response code="404">Attribute not found, or it is not bound to the Object Type.</response>
    /// <response code="409">Refused because Metaverse Objects of the type hold stored values; returns the blocking impact.</response>
    /// <response code="401">User not authenticated.</response>
    [HttpDelete("attributes/{id:int}/object-types/{objectTypeId:int}", Name = "UnassignAttributeFromObjectType")]
    [ProducesResponseType(typeof(AttributeUnassignImpact), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(AttributeUnassignImpact), StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> UnassignAttributeFromObjectTypeAsync(int id, int objectTypeId, [FromQuery] string? confirmationName = null, [FromQuery] string? changeReason = null)
    {
        _logger.LogInformation("Unassigning metaverse attribute {Id} from object type {ObjectTypeId}", id, objectTypeId);

        var attribute = await _application.Metaverse.GetMetaverseAttributeAsync(id);
        if (attribute == null)
            return NotFound(ApiErrorResponse.NotFound($"Attribute with ID {id} not found."));

        if (attribute.BuiltIn)
            return BadRequest(ApiErrorResponse.BadRequest("Built-in attributes cannot be unassigned."));

        var impact = await _application.Metaverse.EvaluateAttributeUnassignAsync(id, objectTypeId);

        if (!impact.WasBound)
            return NotFound(ApiErrorResponse.NotFound($"Attribute {id} is not bound to Object Type {objectTypeId}."));

        if (impact.BlockedByValues)
            return Conflict(impact);

        if (impact.RequiresConfirmation && !string.Equals(confirmationName, attribute.Name, StringComparison.Ordinal))
            return BadRequest(ApiErrorResponse.BadRequest(
                $"This unassignment will remove type-scoped references. To confirm, supply confirmationName exactly matching the attribute name '{attribute.Name}'."));

        var apiKey = await GetCurrentApiKeyAsync();
        var result = apiKey != null
            ? await _application.Metaverse.UnassignAttributeFromObjectTypeAsync(id, objectTypeId, apiKey, changeReason)
            : await _application.Metaverse.UnassignAttributeFromObjectTypeAsync(id, objectTypeId, await GetCurrentUserAsync(), changeReason);

        _logger.LogInformation("Unassigned metaverse attribute {Id} from object type {ObjectTypeId}", id, objectTypeId);
        return Ok(result);
    }

    #region Configuration Change History

    /// <summary>
    /// List the change history for a Metaverse Object Type.
    /// </summary>
    /// <param name="id">The unique identifier of the Object Type.</param>
    /// <param name="pagination">Pagination parameters.</param>
    /// <returns>A paged list of change-history entries, newest version first, each with a one-line summary.</returns>
    /// <response code="200">Change history returned (empty if the Object Type has no recorded configuration changes).</response>
    /// <response code="401">User could not be identified from authentication token.</response>
    [HttpGet("object-types/{id:int}/change-history", Name = "GetObjectTypeChangeHistory")]
    [ProducesResponseType(typeof(PaginatedResponse<ConfigurationChangeHistoryItem>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetObjectTypeChangeHistoryAsync(int id, [FromQuery] PaginationRequest pagination)
    {
        var result = await _application.ChangeHistory.GetConfigurationChangeHistoryAsync(ActivityTargetType.MetaverseObjectType, id, pagination.Page, pagination.PageSize);
        return Ok(PaginatedResponse<ConfigurationChangeHistoryItem>.Create(result.Results, result.TotalResults, pagination.Page, pagination.PageSize));
    }

    /// <summary>
    /// Get a single version of a Metaverse Object Type's change history, with its snapshot and the diff against the previous version.
    /// </summary>
    /// <param name="id">The unique identifier of the Object Type.</param>
    /// <param name="changeVersion">The per-object change version to retrieve.</param>
    /// <returns>The change detail: metadata, the snapshot, and the diff against the previous version.</returns>
    /// <response code="200">The change detail.</response>
    /// <response code="404">No change with that version was found for the Object Type.</response>
    /// <response code="401">User could not be identified from authentication token.</response>
    [HttpGet("object-types/{id:int}/change-history/{changeVersion:int}", Name = "GetObjectTypeChange")]
    [ProducesResponseType(typeof(ConfigurationChangeDetail), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetObjectTypeChangeAsync(int id, int changeVersion)
    {
        var detail = await _application.ChangeHistory.GetConfigurationChangeAsync(ActivityTargetType.MetaverseObjectType, id, changeVersion);
        if (detail == null)
            return NotFound(ApiErrorResponse.NotFound($"No change history found for Metaverse Object Type {id} version {changeVersion}."));
        return Ok(detail);
    }

    /// <summary>
    /// Compare two versions of a Metaverse Object Type's configuration.
    /// </summary>
    /// <param name="id">The unique identifier of the Object Type.</param>
    /// <param name="fromVersion">The earlier version to compare from.</param>
    /// <param name="toVersion">The later version to compare to.</param>
    /// <returns>The structured diff of the later version against the earlier.</returns>
    /// <response code="200">The diff.</response>
    /// <response code="404">One of the requested versions was not found for the Object Type.</response>
    /// <response code="401">User could not be identified from authentication token.</response>
    [HttpGet("object-types/{id:int}/change-history/compare", Name = "CompareObjectTypeChanges")]
    [ProducesResponseType(typeof(ConfigurationDiff), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> CompareObjectTypeChangesAsync(int id, [FromQuery] int fromVersion, [FromQuery] int toVersion)
    {
        var diff = await _application.ChangeHistory.CompareConfigurationChangesAsync(ActivityTargetType.MetaverseObjectType, id, fromVersion, toVersion);
        if (diff == null)
            return NotFound(ApiErrorResponse.NotFound($"Could not compare versions {fromVersion} and {toVersion} for Metaverse Object Type {id}."));
        return Ok(diff);
    }

    /// <summary>
    /// List the change history for a Metaverse Attribute.
    /// </summary>
    /// <param name="id">The unique identifier of the Attribute.</param>
    /// <param name="pagination">Pagination parameters.</param>
    /// <returns>A paged list of change-history entries, newest version first, each with a one-line summary.</returns>
    /// <response code="200">Change history returned (empty if the Attribute has no recorded configuration changes).</response>
    /// <response code="401">User could not be identified from authentication token.</response>
    [HttpGet("attributes/{id:int}/change-history", Name = "GetAttributeChangeHistory")]
    [ProducesResponseType(typeof(PaginatedResponse<ConfigurationChangeHistoryItem>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetAttributeChangeHistoryAsync(int id, [FromQuery] PaginationRequest pagination)
    {
        var result = await _application.ChangeHistory.GetConfigurationChangeHistoryAsync(ActivityTargetType.MetaverseAttribute, id, pagination.Page, pagination.PageSize);
        return Ok(PaginatedResponse<ConfigurationChangeHistoryItem>.Create(result.Results, result.TotalResults, pagination.Page, pagination.PageSize));
    }

    /// <summary>
    /// Get a single version of a Metaverse Attribute's change history, with its snapshot and the diff against the previous version.
    /// </summary>
    /// <param name="id">The unique identifier of the Attribute.</param>
    /// <param name="changeVersion">The per-object change version to retrieve.</param>
    /// <returns>The change detail: metadata, the snapshot, and the diff against the previous version.</returns>
    /// <response code="200">The change detail.</response>
    /// <response code="404">No change with that version was found for the Attribute.</response>
    /// <response code="401">User could not be identified from authentication token.</response>
    [HttpGet("attributes/{id:int}/change-history/{changeVersion:int}", Name = "GetAttributeChange")]
    [ProducesResponseType(typeof(ConfigurationChangeDetail), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetAttributeChangeAsync(int id, int changeVersion)
    {
        var detail = await _application.ChangeHistory.GetConfigurationChangeAsync(ActivityTargetType.MetaverseAttribute, id, changeVersion);
        if (detail == null)
            return NotFound(ApiErrorResponse.NotFound($"No change history found for Metaverse Attribute {id} version {changeVersion}."));
        return Ok(detail);
    }

    /// <summary>
    /// Compare two versions of a Metaverse Attribute's configuration.
    /// </summary>
    /// <param name="id">The unique identifier of the Attribute.</param>
    /// <param name="fromVersion">The earlier version to compare from.</param>
    /// <param name="toVersion">The later version to compare to.</param>
    /// <returns>The structured diff of the later version against the earlier.</returns>
    /// <response code="200">The diff.</response>
    /// <response code="404">One of the requested versions was not found for the Attribute.</response>
    /// <response code="401">User could not be identified from authentication token.</response>
    [HttpGet("attributes/{id:int}/change-history/compare", Name = "CompareAttributeChanges")]
    [ProducesResponseType(typeof(ConfigurationDiff), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> CompareAttributeChangesAsync(int id, [FromQuery] int fromVersion, [FromQuery] int toVersion)
    {
        var diff = await _application.ChangeHistory.CompareConfigurationChangesAsync(ActivityTargetType.MetaverseAttribute, id, fromVersion, toVersion);
        if (diff == null)
            return NotFound(ApiErrorResponse.NotFound($"Could not compare versions {fromVersion} and {toVersion} for Metaverse Attribute {id}."));
        return Ok(diff);
    }

    #endregion

    #region Attribute Priority

    /// <summary>
    /// Get an Attribute's priority order
    /// </summary>
    /// <remarks>
    /// Returns the ordered list of import contributions to this Metaverse Attribute for a given Metaverse Object
    /// Type (#91), highest priority first. Disabled Synchronisation Rules are included; they hold position but
    /// never contribute during resolution. An empty list means the Attribute has no import contributors.
    /// </remarks>
    /// <param name="metaverseAttributeId">The Metaverse Attribute.</param>
    /// <param name="metaverseObjectTypeId">The Metaverse Object Type that scopes the priority list.</param>
    [HttpGet("attributes/{metaverseAttributeId:int}/priorities/{metaverseObjectTypeId:int}", Name = "GetAttributePriorityOrder")]
    [ProducesResponseType(typeof(AttributePriorityOrderDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetAttributePriorityOrderAsync(int metaverseAttributeId, int metaverseObjectTypeId)
    {
        _logger.LogTrace("Requested attribute priority order for Metaverse attribute {AttributeId} on object type {ObjectTypeId}", metaverseAttributeId, metaverseObjectTypeId);

        var attribute = await _application.Metaverse.GetMetaverseAttributeAsync(metaverseAttributeId);
        if (attribute == null)
            return NotFound(ApiErrorResponse.NotFound($"Metaverse attribute with ID {metaverseAttributeId} not found."));

        var mappings = await _application.ConnectedSystems.GetAttributePriorityOrderAsync(metaverseObjectTypeId, metaverseAttributeId);
        return Ok(AttributePriorityOrderDto.FromEntities(metaverseObjectTypeId, metaverseAttributeId, mappings));
    }

    /// <summary>
    /// Replace an Attribute's priority order
    /// </summary>
    /// <remarks>
    /// Transactionally renumbers the priorities of all import contributions to this Metaverse Attribute for a
    /// given Metaverse Object Type (#91), and applies each contribution's "Null is a value" flag. The request must
    /// list every current contributing mapping for the Attribute exactly once, in the desired priority order
    /// (highest first). To move a single mapping without restating the whole list, use the move endpoint instead.
    /// Returns the resulting order.
    /// </remarks>
    /// <param name="metaverseAttributeId">The Metaverse Attribute.</param>
    /// <param name="metaverseObjectTypeId">The Metaverse Object Type that scopes the priority list.</param>
    /// <param name="request">The contributors in the desired priority order.</param>
    /// <response code="200">Priority order updated successfully; returns the updated order.</response>
    /// <response code="400">The requested order does not match the Attribute's current contributor set.</response>
    /// <response code="404">Metaverse Attribute not found.</response>
    /// <response code="401">User could not be identified from authentication token.</response>
    [HttpPut("attributes/{metaverseAttributeId:int}/priorities/{metaverseObjectTypeId:int}", Name = "SetAttributePriorityOrder")]
    [ProducesResponseType(typeof(AttributePriorityOrderDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> SetAttributePriorityOrderAsync(int metaverseAttributeId, int metaverseObjectTypeId, [FromBody] SetAttributePriorityOrderRequest request)
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
    /// Repositions a single contributing mapping to the given 1-based priority position for this Metaverse
    /// Attribute on a given Metaverse Object Type (#91), shuffling the other contributors to keep the list
    /// contiguous, then renumbering all affected rows in one transaction. The caller states only the new position;
    /// the engine keeps the order gap-free and duplicate-free. Optionally also updates the moved mapping's "Null is
    /// a value" flag. Returns the resulting order, so the caller never has to renumber siblings or re-fetch.
    /// </remarks>
    /// <param name="metaverseAttributeId">The Metaverse Attribute.</param>
    /// <param name="metaverseObjectTypeId">The Metaverse Object Type that scopes the priority list.</param>
    /// <param name="mappingId">The contributing mapping to move.</param>
    /// <param name="request">The desired position (and optional "Null is a value" flag).</param>
    /// <response code="200">Mapping moved successfully; returns the resulting order.</response>
    /// <response code="400">The mapping is not a contributor to the Attribute.</response>
    /// <response code="404">Metaverse Attribute not found.</response>
    /// <response code="401">User could not be identified from authentication token.</response>
    [HttpPut("attributes/{metaverseAttributeId:int}/priorities/{metaverseObjectTypeId:int}/mappings/{mappingId:int}", Name = "MoveAttributePriority")]
    [ProducesResponseType(typeof(AttributePriorityOrderDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> MoveAttributePriorityAsync(int metaverseAttributeId, int metaverseObjectTypeId, int mappingId, [FromBody] MoveAttributePriorityRequest request)
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

    /// <summary>
    /// List Metaverse Objects
    /// </summary>
    /// <remarks>
    /// The DisplayName Attribute is always included in the response. Use the `attributes` parameter
    /// to request additional Attributes to be included. This follows a common pattern in APIs and
    /// PowerShell modules where clients can specify which properties to retrieve.
    ///
    /// Use `?attributes=*` to include all Attributes.
    ///
    /// **Filtering:**
    /// - `search` - Searches display name (partial match, case-insensitive)
    /// - `filterAttributeName` + `filterAttributeValue` - Filters by specific Attribute (exact match, case-insensitive)
    ///
    /// Examples:
    /// - `?attributes=FirstName&amp;attributes=LastName&amp;attributes=Email` - Include specific Attributes
    /// - `?attributes=*` - Include all Attributes
    /// - `?filterAttributeName=Account Name&amp;filterAttributeValue=jsmith` - Find by account name
    /// </remarks>
    /// <param name="pagination">Pagination parameters (page, pageSize, sortBy, sortDirection).</param>
    /// <param name="objectTypeId">Optional Object Type ID to filter by.</param>
    /// <param name="search">Optional search query to filter by display name.</param>
    /// <param name="attributes">Optional list of Attribute names to include in the response. Use "*" for all Attributes. DisplayName is always included.</param>
    /// <param name="filterAttributeName">Optional Attribute name to filter by (must be used with filterAttributeValue).</param>
    /// <param name="filterAttributeValue">Optional Attribute Value to filter by (exact match, case-insensitive).</param>
    /// <returns>A paginated list of Metaverse Object headers.</returns>
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
        _logger.LogDebug("Getting Metaverse Objects (Page: {Page}, PageSize: {PageSize}, TypeId: {TypeId}, Search: {Search}, FilterAttr: {FilterAttr}={FilterValue}, Attributes: {Attributes})",
            pagination.Page, pagination.PageSize, objectTypeId, LogSanitiser.Sanitise(search), LogSanitiser.Sanitise(filterAttributeName), LogSanitiser.Sanitise(filterAttributeValue),
            LogSanitiser.Sanitise(attributes != null ? string.Join(",", attributes) : "DisplayName only"));

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
    /// Count Metaverse Objects
    /// </summary>
    /// <param name="objectTypeId">Optional Object Type ID to filter by.</param>
    /// <param name="search">Optional search text to filter by display name (partial match, case-insensitive).</param>
    /// <param name="filterAttributeName">Optional Attribute name to filter by (must be used with filterAttributeValue).</param>
    /// <param name="filterAttributeValue">Optional Attribute Value to filter by (exact match, case-insensitive).</param>
    /// <returns>The count of matching Metaverse Objects.</returns>
    [HttpGet("objects/count", Name = "GetObjectsCount")]
    [ProducesResponseType(typeof(int), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetObjectsCountAsync(
        [FromQuery] int? objectTypeId = null,
        [FromQuery] string? search = null,
        [FromQuery] string? filterAttributeName = null,
        [FromQuery] string? filterAttributeValue = null)
    {
        _logger.LogDebug("Getting Metaverse Objects count (TypeId: {TypeId}, Search: {Search}, FilterAttr: {FilterAttr}={FilterValue})",
            objectTypeId, LogSanitiser.Sanitise(search), LogSanitiser.Sanitise(filterAttributeName), LogSanitiser.Sanitise(filterAttributeValue));

        var count = await _application.Metaverse.GetMetaverseObjectsCountAsync(
            objectTypeId, search, filterAttributeName, filterAttributeValue);
        return Ok(count);
    }

    /// <summary>
    /// Search Metaverse Objects using a predefined search
    /// </summary>
    /// <remarks>
    /// Returns only the Attributes configured in the predefined search definition, making it significantly
    /// faster than the general objects endpoint for list views at scale (100k+ objects). Use the general
    /// GET /objects endpoint when you need full object details or custom Attribute selection.
    /// </remarks>
    /// <param name="predefinedSearchUri">The URI identifier of the predefined search (e.g. "users", "groups").</param>
    /// <param name="pagination">Pagination parameters (page, pageSize, sortBy, sortDirection).</param>
    /// <param name="search">Optional search query to filter across all string Attribute Values (case-insensitive).</param>
    /// <returns>A paginated list of Metaverse Object headers with the predefined search Attributes.</returns>
    [HttpGet("objects/search/{predefinedSearchUri}", Name = "SearchObjects")]
    [ProducesResponseType(typeof(PaginatedResponse<MetaverseObjectHeaderDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> SearchObjectsAsync(
        [FromRoute] string predefinedSearchUri,
        [FromQuery] PaginationRequest pagination,
        [FromQuery] string? search = null)
    {
        _logger.LogDebug("Searching Metaverse Objects via predefined search (Uri: {Uri}, Page: {Page}, PageSize: {PageSize}, Search: {Search})",
            LogSanitiser.Sanitise(predefinedSearchUri), pagination.Page, pagination.PageSize, LogSanitiser.Sanitise(search));

        var predefinedSearch = await _application.Search.GetPredefinedSearchAsync(predefinedSearchUri);
        if (predefinedSearch == null || !predefinedSearch.IsEnabled)
            return NotFound(ApiErrorResponse.NotFound($"Predefined search '{predefinedSearchUri}' not found."));

        var result = await _application.Metaverse.GetMetaverseObjectHeadersPagedAsync(
            predefinedSearch,
            page: pagination.Page,
            pageSize: pagination.PageSize,
            searchQuery: search,
            sortBy: pagination.SortBy,
            sortDescending: pagination.IsDescending);

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
    /// Get a Metaverse Object
    /// </summary>
    /// <param name="id">The unique identifier (GUID) of the Metaverse Object.</param>
    /// <returns>The Metaverse Object details including all Attribute Values.</returns>
    [HttpGet("objects/{id:guid}", Name = "GetObject")]
    [ProducesResponseType(typeof(MetaverseObjectDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetObjectAsync(Guid id)
    {
        _logger.LogTrace("Requested Metaverse Object: {Id}", id);
        // Provenance-loading retrieval so the DTO can surface each value's contributing Connected System
        // and Synchronisation Rule (#931); the lean GetMetaverseObjectAsync is reserved for the sync join
        // hot path and does not load those navigations.
        var obj = await _application.Metaverse.GetMetaverseObjectWithProvenanceAsync(id);
        if (obj == null)
            return NotFound(ApiErrorResponse.NotFound($"Metaverse Object with ID {id} not found."));

        return Ok(MetaverseObjectDto.FromEntity(obj));
    }

    /// <summary>
    /// List the change history for a Metaverse Object
    /// </summary>
    /// <remarks>
    /// Returns a paginated list of change records for the specified Metaverse Object,
    /// ordered by change time descending (most recent first). Each row carries the
    /// initiator, Synchronisation Rule, and Run Profile context, plus the per-attribute value changes.
    /// </remarks>
    /// <param name="id">The unique identifier (GUID) of the Metaverse Object.</param>
    /// <param name="pagination">Pagination parameters (page, pageSize). Page size is clamped to [1, 100].</param>
    /// <returns>A paginated list of change-history records.</returns>
    [HttpGet("objects/{id:guid}/change-history", Name = "GetObjectChangeHistory")]
    [ProducesResponseType(typeof(PaginatedResponse<MvoChangeHistoryDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetObjectChangeHistoryAsync(Guid id, [FromQuery] PaginationRequest pagination)
    {
        _logger.LogTrace("Requested change history for Metaverse Object: {Id}", id);

        // Verify the MVO exists so a missing id returns 404 rather than an empty page.
        var exists = await _application.Metaverse.GetMetaverseObjectHeaderAsync(id);
        if (exists == null)
            return NotFound(ApiErrorResponse.NotFound($"Metaverse Object with ID {id} not found."));

        var (items, totalCount) = await _application.Metaverse.GetMvoChangeHistoryAsync(id, pagination.Page, pagination.PageSize);
        return Ok(PaginatedResponse<MvoChangeHistoryDto>.Create(items, totalCount, pagination.Page, pagination.PageSize));
    }

    /// <summary>
    /// List Metaverse Objects pending deletion
    /// </summary>
    /// <remarks>
    /// Returns MVOs that have been disconnected from their last connector and are awaiting
    /// automatic deletion after their grace period expires. Use this endpoint to monitor
    /// identities scheduled for cleanup.
    /// </remarks>
    /// <param name="pagination">Pagination parameters (page, pageSize).</param>
    /// <param name="objectTypeId">Optional Object Type ID to filter by.</param>
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

        var result = await _application.Metaverse.GetMetaverseObjectsPendingDeletionAsync(
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
    /// Count Metaverse Objects pending deletion
    /// </summary>
    /// <param name="objectTypeId">Optional Object Type ID to filter by.</param>
    /// <returns>The count of MVOs pending deletion.</returns>
    [HttpGet("pending-deletions/count", Name = "GetPendingDeletionsCount")]
    [ProducesResponseType(typeof(int), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetPendingDeletionsCountAsync([FromQuery] int? objectTypeId = null)
    {
        _logger.LogDebug("Getting pending deletions count (TypeId: {TypeId})", objectTypeId);
        var count = await _application.Metaverse.GetMetaverseObjectsPendingDeletionCountAsync(objectTypeId);
        return Ok(count);
    }

    /// <summary>
    /// Get pending deletion summary statistics
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
        var result = await _application.Metaverse.GetMetaverseObjectsPendingDeletionAsync(
            page: 1,
            pageSize: 100,
            objectTypeId: null);

        var now = DateTime.UtcNow;
        var allPending = result.Results;

        // Get total count (may be more than 100)
        var totalCount = await _application.Metaverse.GetMetaverseObjectsPendingDeletionCountAsync();

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
}
