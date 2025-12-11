using Asp.Versioning;
using JIM.Web.Extensions.Api;
using JIM.Web.Models.Api;
using JIM.Application;
using JIM.Models.Core.DTOs;
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
    /// Gets a paginated list of metaverse objects with optional filtering.
    /// </summary>
    /// <remarks>
    /// The DisplayName attribute is always included in the response. Use the `attributes` parameter
    /// to request additional attributes to be included. This follows a common pattern in APIs and
    /// PowerShell modules where clients can specify which properties to retrieve.
    ///
    /// Use `?attributes=*` to include all attributes.
    ///
    /// Examples:
    /// - `?attributes=FirstName&amp;attributes=LastName&amp;attributes=Email` - Include specific attributes
    /// - `?attributes=*` - Include all attributes
    /// </remarks>
    /// <param name="pagination">Pagination parameters (page, pageSize, sortBy, sortDirection).</param>
    /// <param name="objectTypeId">Optional object type ID to filter by.</param>
    /// <param name="search">Optional search query to filter by display name.</param>
    /// <param name="attributes">Optional list of attribute names to include in the response. Use "*" for all attributes. DisplayName is always included.</param>
    /// <returns>A paginated list of metaverse object headers.</returns>
    [HttpGet("objects", Name = "GetObjects")]
    [ProducesResponseType(typeof(PaginatedResponse<MetaverseObjectHeaderDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetObjectsAsync(
        [FromQuery] PaginationRequest pagination,
        [FromQuery] int? objectTypeId = null,
        [FromQuery] string? search = null,
        [FromQuery] IEnumerable<string>? attributes = null)
    {
        _logger.LogDebug("Getting metaverse objects (Page: {Page}, PageSize: {PageSize}, TypeId: {TypeId}, Search: {Search}, Attributes: {Attributes})",
            pagination.Page, pagination.PageSize, objectTypeId, search, attributes != null ? string.Join(",", attributes) : "DisplayName only");

        var result = await _application.Metaverse.GetMetaverseObjectsAsync(
            page: pagination.Page,
            pageSize: pagination.PageSize,
            objectTypeId: objectTypeId,
            searchQuery: search,
            sortDescending: pagination.IsDescending,
            attributes: attributes);

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
}
