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

        [HttpGet("/data_generation/example_data_sets")]
        public async Task<IEnumerable<ExampleDataSet>> GetExampleDataSetsAsync()
        {
            _logger.LogTrace($"Someone requested the example data set");
            return await _application.DataGeneration.GetExampleDataSetsAsync();
        }

        [HttpGet("/data_generation/templates")]
        public async Task<IEnumerable<DataGenerationTemplate>> GetTemplatesAsync()
        {
            _logger.LogTrace($"Someone requested the data generation templates");
            return await _application.DataGeneration.GetTemplatesAsync();
        }

        [HttpGet("/data_generation/templates/{id}")]
        public async Task<DataGenerationTemplate?> GetTemplateAsync(int id)
        {
            _logger.LogTrace($"Someone requested a specific data generation template: {id}");
            return await _application.DataGeneration.GetTemplateAsync(id, false);
        }

        [HttpPost("/data_generation/templates/{id}/execute")]
        public async Task ExecuteTemplateAsync(int id)
        {
            await _application.DataGeneration.ExecuteTemplateAsync(id);
        }
    }
}
