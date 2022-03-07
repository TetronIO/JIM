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

        [HttpGet("/data-generation/example-data-sets")]
        public async Task<IEnumerable<ExampleDataSet>> GetExampleDataSetsAsync()
        {
            _logger.LogTrace($"Someone requested the example data set");
            return await _application.DataGeneration.GetExampleDataSetsAsync();
        }

        [HttpGet("/data-generation/templates")]
        public async Task<IEnumerable<DataGenerationTemplate>> GetTemplatesAsync()
        {
            _logger.LogTrace($"Someone requested the data generation templates");
            return await _application.DataGeneration.GetTemplatesAsync();
        }
    }
}
