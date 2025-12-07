using JIM.Api.Extensions;
using JIM.Api.Models;
using JIM.Application;
using JIM.Models.Core.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace JIM.Api.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    [Authorize]
    public class MetaverseController : ControllerBase
    {
        private readonly ILogger<MetaverseController> _logger;
        private readonly JimApplication _application;

        public MetaverseController(ILogger<MetaverseController> logger, JimApplication application)
        {
            _logger = logger;
            _application = application;
        }

        [HttpGet("object-types")]
        [ProducesResponseType(typeof(PaginatedResponse<MetaverseObjectTypeHeader>), StatusCodes.Status200OK)]
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

        [HttpGet("object-types/{id:int}")]
        [ProducesResponseType(typeof(MetaverseObjectTypeDetailDto), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
        public async Task<IActionResult> GetObjectTypeAsync(int id, bool includeChildObjects)
        {
            _logger.LogTrace("Requested object type: {Id}", id);
            var objectType = await _application.Metaverse.GetMetaverseObjectTypeAsync(id, includeChildObjects);
            if (objectType == null)
                return NotFound(ApiErrorResponse.NotFound($"Object type with ID {id} not found."));

            return Ok(MetaverseObjectTypeDetailDto.FromEntity(objectType));
        }

        [HttpGet("attributes")]
        [ProducesResponseType(typeof(PaginatedResponse<MetaverseAttributeHeader>), StatusCodes.Status200OK)]
        public async Task<IActionResult> GetAttributesAsync([FromQuery] PaginationRequest pagination)
        {
            _logger.LogTrace("Requested metaverse attributes (Page: {Page}, PageSize: {PageSize})", pagination.Page, pagination.PageSize);
            var attributes = await _application.Metaverse.GetMetaverseAttributesAsync();
            var headers = attributes.Select(MetaverseAttributeHeader.FromEntity).AsQueryable();

            var result = headers
                .ApplySortAndFilter(pagination)
                .ToPaginatedResponse(pagination);

            return Ok(result);
        }

        [HttpGet("attributes/{id:int}")]
        [ProducesResponseType(typeof(MetaverseAttributeDetailDto), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
        public async Task<IActionResult> GetAttributeAsync(int id)
        {
            _logger.LogTrace("Requested attribute: {Id}", id);
            var attribute = await _application.Metaverse.GetMetaverseAttributeAsync(id);
            if (attribute == null)
                return NotFound(ApiErrorResponse.NotFound($"Attribute with ID {id} not found."));

            return Ok(MetaverseAttributeDetailDto.FromEntity(attribute));
        }

        [HttpGet("objects/{id:guid}")]
        [ProducesResponseType(typeof(MetaverseObjectDto), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
        public async Task<IActionResult> GetObjectAsync(Guid id)
        {
            _logger.LogTrace("Requested metaverse object: {Id}", id);
            var obj = await _application.Metaverse.GetMetaverseObjectAsync(id);
            if (obj == null)
                return NotFound(ApiErrorResponse.NotFound($"Metaverse object with ID {id} not found."));

            return Ok(MetaverseObjectDto.FromEntity(obj));
        }
    }
}
