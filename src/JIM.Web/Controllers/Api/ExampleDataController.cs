// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using Asp.Versioning;
using JIM.Web.Extensions.Api;
using JIM.Web.Models.Api;
using JIM.Application;
using JIM.Models.ExampleData;
using JIM.Models.ExampleData.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace JIM.Web.Controllers.Api;

/// <summary>
/// API controller for data generation operations including templates and Example Data Sets.
/// </summary>
/// <remarks>
/// This controller provides endpoints for:
/// - Browsing available Data Generation Templates
/// - Viewing Example Data Sets that can be used for testing
/// - Executing templates to generate test data in the Metaverse
/// </remarks>
[Route("api/v{version:apiVersion}/example-data")]
[ApiController]
[ApiVersion("1.0")]
[Authorize(Roles = "Administrator")]
[Produces("application/json")]
public class ExampleDataController(ILogger<ExampleDataController> logger, JimApplication application) : ControllerBase
{
    private readonly ILogger<ExampleDataController> _logger = logger;
    private readonly JimApplication _application = application;

    /// <summary>
    /// List Example Data Sets
    /// </summary>
    /// <param name="pagination">Pagination parameters (page, pageSize, sortBy, sortDirection, filter).</param>
    /// <returns>A paginated list of Example Data Set headers.</returns>
    [HttpGet("example-data-sets", Name = "GetExampleDataSets")]
    [ProducesResponseType(typeof(PaginatedResponse<ExampleDataSetHeader>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetExampleDataSetsAsync([FromQuery] PaginationRequest pagination)
    {
        _logger.LogTrace("Requested example data sets (Page: {Page}, PageSize: {PageSize})", pagination.Page, pagination.PageSize);
        var dataSets = await _application.ExampleData.GetExampleDataSetsAsync();
        var headers = dataSets.Select(ExampleDataSetHeader.FromEntity).AsQueryable();

        var result = headers
            .ApplySortAndFilter(pagination)
            .ToPaginatedResponse(pagination);

        return Ok(result);
    }

    /// <summary>
    /// List Data Generation Templates
    /// </summary>
    /// <param name="pagination">Pagination parameters (page, pageSize, sortBy, sortDirection, filter).</param>
    /// <returns>A paginated list of template headers.</returns>
    [HttpGet("templates", Name = "GetExampleDataTemplates")]
    [ProducesResponseType(typeof(PaginatedResponse<ExampleDataTemplateHeader>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetTemplatesAsync([FromQuery] PaginationRequest pagination)
    {
        _logger.LogTrace("Requested data generation templates (Page: {Page}, PageSize: {PageSize})", pagination.Page, pagination.PageSize);
        var templates = await _application.ExampleData.GetTemplatesAsync();
        var headers = templates.Select(ExampleDataTemplateHeader.FromEntity).AsQueryable();

        var result = headers
            .ApplySortAndFilter(pagination)
            .ToPaginatedResponse(pagination);

        return Ok(result);
    }

    /// <summary>
    /// Get a Data Generation Template
    /// </summary>
    /// <param name="id">The unique identifier of the template.</param>
    /// <returns>The full template details including nested Object Type configurations.</returns>
    [HttpGet("templates/{id:int}", Name = "GetExampleDataTemplate")]
    [ProducesResponseType(typeof(ExampleDataTemplate), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetTemplateAsync(int id)
    {
        _logger.LogTrace("Requested data generation template: {Id}", id);
        var template = await _application.ExampleData.GetTemplateAsync(id);
        if (template == null)
            return NotFound(ApiErrorResponse.NotFound($"Data generation template with ID {id} not found."));

        // Return full entity for detail view - template includes nested ObjectTypes
        return Ok(template);
    }

    /// <summary>
    /// Execute a Data Generation Template
    /// </summary>
    /// <param name="id">The unique identifier of the template to execute.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>202 Accepted if the template execution has started.</returns>
    /// <response code="202">Template execution has been started.</response>
    /// <response code="404">Template not found.</response>
    [HttpPost("templates/{id:int}/execute", Name = "ExecuteExampleDataTemplate")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> ExecuteTemplateAsync(int id, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Executing data generation template: {Id}", id);

        // Check template exists before executing
        var template = await _application.ExampleData.GetTemplateAsync(id);
        if (template == null)
            return NotFound(ApiErrorResponse.NotFound($"Data generation template with ID {id} not found."));

        await _application.ExampleData.ExecuteTemplateAsync(id, cancellationToken);
        return Accepted();
    }
}
