using JIM.Api.Models;
using JIM.Application;
using JIM.Models.Core;
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
        [ProducesResponseType(typeof(IEnumerable<MetaverseObjectType>), StatusCodes.Status200OK)]
        public async Task<IActionResult> GetObjectTypesAsync(bool includeChildObjects)
        {
            _logger.LogTrace("Requested metaverse object types");
            var objectTypes = await _application.Metaverse.GetMetaverseObjectTypesAsync(includeChildObjects);
            return Ok(objectTypes);
        }

        [HttpGet("object-types/{id:int}")]
        [ProducesResponseType(typeof(MetaverseObjectType), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
        public async Task<IActionResult> GetObjectTypeAsync(int id, bool includeChildObjects)
        {
            _logger.LogTrace("Requested object type: {Id}", id);
            var objectType = await _application.Metaverse.GetMetaverseObjectTypeAsync(id, includeChildObjects);
            if (objectType == null)
                return NotFound(ApiErrorResponse.NotFound($"Object type with ID {id} not found."));

            return Ok(objectType);
        }

        [HttpGet("attributes")]
        [ProducesResponseType(typeof(IEnumerable<MetaverseAttribute>), StatusCodes.Status200OK)]
        public async Task<IActionResult> GetAttributesAsync()
        {
            _logger.LogTrace("Requested metaverse attributes");
            var attributes = await _application.Metaverse.GetMetaverseAttributesAsync();
            return Ok(attributes);
        }

        [HttpGet("attributes/{id:int}")]
        [ProducesResponseType(typeof(MetaverseAttribute), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
        public async Task<IActionResult> GetAttributeAsync(int id)
        {
            _logger.LogTrace("Requested attribute: {Id}", id);
            var attribute = await _application.Metaverse.GetMetaverseAttributeAsync(id);
            if (attribute == null)
                return NotFound(ApiErrorResponse.NotFound($"Attribute with ID {id} not found."));

            return Ok(attribute);
        }

        [HttpGet("objects/{id:guid}")]
        [ProducesResponseType(typeof(MetaverseObject), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
        public async Task<IActionResult> GetObjectAsync(Guid id)
        {
            _logger.LogTrace("Requested metaverse object: {Id}", id);
            var obj = await _application.Metaverse.GetMetaverseObjectAsync(id);
            if (obj == null)
                return NotFound(ApiErrorResponse.NotFound($"Metaverse object with ID {id} not found."));

            return Ok(obj);
        }
    }
}
