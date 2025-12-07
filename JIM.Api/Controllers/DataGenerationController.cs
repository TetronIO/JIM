using Asp.Versioning;
using JIM.Api.Extensions;
using JIM.Api.Models;
using JIM.Application;
using JIM.Models.DataGeneration;
using JIM.Models.DataGeneration.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace JIM.Api.Controllers;

/// <summary>
/// API controller for data generation operations including templates and example data sets.
/// </summary>
/// <remarks>
/// This controller provides endpoints for:
/// - Browsing available data generation templates
/// - Viewing example data sets that can be used for testing
/// - Executing templates to generate test data in the Metaverse
/// </remarks>
[Route("api/v{version:apiVersion}/data-generation")]
[ApiController]
[ApiVersion("1.0")]
[Authorize(Roles = "Administrators")]
[Produces("application/json")]
public class DataGenerationController(ILogger<DataGenerationController> logger, JimApplication application) : ControllerBase
{
    private readonly ILogger<DataGenerationController> _logger = logger;
    private readonly JimApplication _application = application;

    /// <summary>
    /// Gets all example data sets with optional pagination, sorting, and filtering.
    /// </summary>
    /// <remarks>
    /// Example data sets contain pre-defined identity data that can be used for
    /// testing and demonstration purposes.
    /// </remarks>
    /// <param name="pagination">Pagination parameters (page, pageSize, sortBy, sortDirection, filter).</param>
    /// <returns>A paginated list of example data set headers.</returns>
    [HttpGet("example-data-sets", Name = "GetExampleDataSets")]
    [ProducesResponseType(typeof(PaginatedResponse<ExampleDataSetHeader>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetExampleDataSetsAsync([FromQuery] PaginationRequest pagination)
    {
        _logger.LogTrace("Requested example data sets (Page: {Page}, PageSize: {PageSize})", pagination.Page, pagination.PageSize);
        var dataSets = await _application.DataGeneration.GetExampleDataSetsAsync();
        var headers = dataSets.Select(ExampleDataSetHeader.FromEntity).AsQueryable();

        var result = headers
            .ApplySortAndFilter(pagination)
            .ToPaginatedResponse(pagination);

        return Ok(result);
    }

    /// <summary>
    /// Gets all data generation templates with optional pagination, sorting, and filtering.
    /// </summary>
    /// <remarks>
    /// Templates define how test data should be generated, including which object types
    /// to create and how many instances of each.
    /// </remarks>
    /// <param name="pagination">Pagination parameters (page, pageSize, sortBy, sortDirection, filter).</param>
    /// <returns>A paginated list of template headers.</returns>
    [HttpGet("templates", Name = "GetDataGenerationTemplates")]
    [ProducesResponseType(typeof(PaginatedResponse<DataGenerationTemplateHeader>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetTemplatesAsync([FromQuery] PaginationRequest pagination)
    {
        _logger.LogTrace("Requested data generation templates (Page: {Page}, PageSize: {PageSize})", pagination.Page, pagination.PageSize);
        var templates = await _application.DataGeneration.GetTemplatesAsync();
        var headers = templates.Select(DataGenerationTemplateHeader.FromEntity).AsQueryable();

        var result = headers
            .ApplySortAndFilter(pagination)
            .ToPaginatedResponse(pagination);

        return Ok(result);
    }

    /// <summary>
    /// Gets a specific data generation template by ID.
    /// </summary>
    /// <param name="id">The unique identifier of the template.</param>
    /// <returns>The full template details including nested object type configurations.</returns>
    [HttpGet("templates/{id:int}", Name = "GetDataGenerationTemplate")]
    [ProducesResponseType(typeof(DataGenerationTemplate), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetTemplateAsync(int id)
    {
        _logger.LogTrace("Requested data generation template: {Id}", id);
        var template = await _application.DataGeneration.GetTemplateAsync(id);
        if (template == null)
            return NotFound(ApiErrorResponse.NotFound($"Data generation template with ID {id} not found."));

        // Return full entity for detail view - template includes nested ObjectTypes
        return Ok(template);
    }

    /// <summary>
    /// Executes a data generation template to create test data.
    /// </summary>
    /// <remarks>
    /// This operation runs asynchronously and creates identity objects in the Metaverse
    /// according to the template configuration. The operation may take some time to complete
    /// depending on the number of objects being generated.
    /// </remarks>
    /// <param name="id">The unique identifier of the template to execute.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>202 Accepted if the template execution has started.</returns>
    /// <response code="202">Template execution has been started.</response>
    /// <response code="404">Template not found.</response>
    [HttpPost("templates/{id:int}/execute", Name = "ExecuteDataGenerationTemplate")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
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
