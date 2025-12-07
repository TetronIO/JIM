using JIM.Api.Extensions;
using JIM.Api.Models;
using JIM.Application;
using JIM.Models.Core.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace JIM.Api.Controllers;

/// <summary>
/// API controller for managing Metaverse schema and objects.
/// </summary>
/// <remarks>
/// The Metaverse is the central identity store in JIM. This controller provides
/// endpoints for managing object types, attributes, and individual metaverse objects.
/// </remarks>
[Route("api/[controller]")]
[ApiController]
[Authorize(Roles = "Administrators")]
[Produces("application/json")]
public class MetaverseController : ControllerBase
{
    private readonly ILogger<MetaverseController> _logger;
    private readonly JimApplication _application;

    public MetaverseController(ILogger<MetaverseController> logger, JimApplication application)
    {
        _logger = logger;
        _application = application;
    }

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
}
