using JIM.Application;
using JIM.Models.DataGeneration;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace JIM.Api.Controllers
{
    [Route("api/data-generation")]
    [ApiController]
    [Authorize]
    public class DataGenerationController : ControllerBase
    {
        private readonly ILogger<DataGenerationController> _logger;
        private readonly JimApplication _application;

        public DataGenerationController(ILogger<DataGenerationController> logger, JimApplication application)
        {
            _logger = logger;
            _application = application;
        }

        [HttpGet("example-data-sets")]
        public async Task<IEnumerable<ExampleDataSet>> GetExampleDataSetsAsync()
        {
            _logger.LogTrace($"Someone requested the example data set");
            return await _application.DataGeneration.GetExampleDataSetsAsync();
        }

        [HttpGet("templates")]
        public async Task<IEnumerable<DataGenerationTemplate>> GetTemplatesAsync()
        {
            _logger.LogTrace($"Someone requested the data generation templates");
            return await _application.DataGeneration.GetTemplatesAsync();
        }

        [HttpGet("templates/{id}")]
        public async Task<DataGenerationTemplate?> GetTemplateAsync(int id)
        {
            _logger.LogTrace($"Someone requested a specific data generation template: {id}");
            return await _application.DataGeneration.GetTemplateAsync(id);
        }

        [HttpPost("templates/{id}/execute")]
        [ProducesResponseType(StatusCodes.Status202Accepted)]
        public async Task<IActionResult> ExecuteTemplateAsync(int id, CancellationToken cancellationToken)
        {
            await _application.DataGeneration.ExecuteTemplateAsync(id, cancellationToken);
            return Accepted();
        }
    }
}
