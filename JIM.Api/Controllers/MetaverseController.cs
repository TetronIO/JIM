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
        public async Task<IEnumerable<MetaverseObjectType>> GetObjectTypesAsync(bool includeChildObjects)
        {
            _logger.LogTrace($"Someone requested the metaverse object types");
            return await _application.Metaverse.GetMetaverseObjectTypesAsync(includeChildObjects);
        }

        [HttpGet("object-types/{id}")]
        public async Task<MetaverseObjectType?> GetObjectTypeAsync(int id, bool includeChildObjects)
        {
            _logger.LogTrace($"Someone requested an object type: {id}");
            return await _application.Metaverse.GetMetaverseObjectTypeAsync(id, includeChildObjects);
        }

        [HttpGet("attributes")]
        public async Task<IEnumerable<MetaverseAttribute>?> GetAttributesAsync()
        {
            _logger.LogTrace($"Someone requested the metaverse attributes");
            return await _application.Metaverse.GetMetaverseAttributesAsync();
        }

        [HttpGet("attributes/{id}")]
        public async Task<MetaverseAttribute?> GetAttributeAsync(int id)
        {
            _logger.LogTrace($"Someone requested an attribute: {id}");
            return await _application.Metaverse.GetMetaverseAttributeAsync(id);
        }

        [HttpGet("objects/{id}")]
        public async Task<MetaverseObject?> GetObjectAsync(Guid id)
        {
            _logger.LogTrace($"Someone requested a metaverse object: {id}");
            return await _application.Metaverse.GetMetaverseObjectAsync(id);
        }
    }
}
