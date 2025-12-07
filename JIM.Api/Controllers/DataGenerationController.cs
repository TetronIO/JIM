using JIM.Api.Models;
using JIM.Application;
using JIM.Models.DataGeneration;
using JIM.Models.DataGeneration.DTOs;
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
        [ProducesResponseType(typeof(IEnumerable<ExampleDataSetHeader>), StatusCodes.Status200OK)]
        public async Task<IActionResult> GetExampleDataSetsAsync()
        {
            _logger.LogTrace("Requested example data sets");
            var dataSets = await _application.DataGeneration.GetExampleDataSetsAsync();
            var headers = dataSets.Select(ExampleDataSetHeader.FromEntity);
            return Ok(headers);
        }

        [HttpGet("templates")]
        [ProducesResponseType(typeof(IEnumerable<DataGenerationTemplateHeader>), StatusCodes.Status200OK)]
        public async Task<IActionResult> GetTemplatesAsync()
        {
            _logger.LogTrace("Requested data generation templates");
            var templates = await _application.DataGeneration.GetTemplatesAsync();
            var headers = templates.Select(DataGenerationTemplateHeader.FromEntity);
            return Ok(headers);
        }

        [HttpGet("templates/{id:int}")]
        [ProducesResponseType(typeof(DataGenerationTemplate), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
        public async Task<IActionResult> GetTemplateAsync(int id)
        {
            _logger.LogTrace("Requested data generation template: {Id}", id);
            var template = await _application.DataGeneration.GetTemplateAsync(id);
            if (template == null)
                return NotFound(ApiErrorResponse.NotFound($"Data generation template with ID {id} not found."));

            // Return full entity for detail view - template includes nested ObjectTypes
            return Ok(template);
        }

        [HttpPost("templates/{id:int}/execute")]
        [ProducesResponseType(StatusCodes.Status202Accepted)]
        [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
        public async Task<IActionResult> ExecuteTemplateAsync(int id, CancellationToken cancellationToken)
        {
            _logger.LogInformation("Executing data generation template: {Id}", id);

            // Check template exists before executing
            var template = await _application.DataGeneration.GetTemplateAsync(id);
            if (template == null)
                return NotFound(ApiErrorResponse.NotFound($"Data generation template with ID {id} not found."));

            await _application.DataGeneration.ExecuteTemplateAsync(id, cancellationToken);
            return Accepted();
        }
    }
}
