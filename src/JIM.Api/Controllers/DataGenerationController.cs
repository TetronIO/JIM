using JIM.Application;
using JIM.Models.DataGeneration;
using Microsoft.AspNetCore.Mvc;

namespace JIM.Api.Controllers
{
    [Route("[controller]")]
    [ApiController]
    public class DataGenerationController : ControllerBase
    {
        private readonly ILogger<DataGenerationController> _logger;
        private readonly JimApplication _application;

        public DataGenerationController(ILogger<DataGenerationController> logger, JimApplication application)
        {
            _logger = logger;
            _application = application;
        }

        [HttpGet("/data-generation/templates")]
        public IEnumerable<DataGenerationTemplate> GetTemplates()
        {
            _logger.LogTrace($"Someone requested the roles");
            return _application.DataGeneration.GetTemplates();
        }
    }
}
