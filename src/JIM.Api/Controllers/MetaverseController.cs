using JIM.Application;
using JIM.Models.Core;
using Microsoft.AspNetCore.Mvc;

namespace JIM.Api.Controllers
{
    [Route("[controller]")]
    [ApiController]
    public class MetaverseController : ControllerBase
    {
        private readonly ILogger<MetaverseController> _logger;
        private readonly JimApplication _application;

        public MetaverseController(ILogger<MetaverseController> logger, JimApplication application)
        {
            _logger = logger;
            _application = application;
        }

        [HttpGet("/metaverse/object_types")]
        public IEnumerable<MetaverseObjectType> GetObjectTypes()
        {
            _logger.LogTrace($"Someone requested the metaverse object types");
            return _application.Metaverse.GetMetaverseObjectTypes();
        }

        [HttpGet("/metaverse/object_types/{id}")]
        public MetaverseObjectType? GetObjectType(Guid id)
        {
            _logger.LogTrace($"Someone requested an object type: {id}");
            return _application.Metaverse.GetMetaverseObjectType(id);
        }

        [HttpGet("/metaverse/attributes")]
        public IEnumerable<MetaverseAttribute>? GetAttributes()
        {
            _logger.LogTrace($"Someone requested the metaverse attributes");
            return _application.Metaverse.GetMetaverseAttributes();
        }

        [HttpGet("/metaverse/attributes/{id}")]
        public MetaverseAttribute? GetAttribute(Guid id)
        {
            _logger.LogTrace($"Someone requested an attribute: {id}");
            return _application.Metaverse.GetMetaverseAttribute(id);
        }

        [HttpGet("/metaverse/objects/{id}")]
        public MetaverseObject? GetObject(Guid id)
        {
            _logger.LogTrace($"Someone requested a metaverse object: {id}");
            return _application.Metaverse.GetMetaverseObject(id);
        }
    }
}
