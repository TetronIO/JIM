// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using Asp.Versioning;
using JIM.Web.Extensions.Api;
using JIM.Web.Models.Api;
using JIM.Application;
using JIM.Models.ExampleData;
using JIM.Models.ExampleData.DTOs;
using JIM.Utilities;
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
    /// Get an Example Data Set
    /// </summary>
    /// <param name="id">The unique identifier of the Example Data Set.</param>
    /// <returns>The full Example Data Set, including its values.</returns>
    [HttpGet("example-data-sets/{id:int}", Name = "GetExampleDataSet")]
    [ProducesResponseType(typeof(ExampleDataSet), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetExampleDataSetAsync(int id)
    {
        _logger.LogTrace("Requested example data set: {Id}", id);
        var dataSet = await _application.ExampleData.GetExampleDataSetAsync(id);
        if (dataSet == null)
            return NotFound(ApiErrorResponse.NotFound($"Example Data Set with ID {id} not found."));

        return Ok(dataSet);
    }

    /// <summary>
    /// Create an Example Data Set
    /// </summary>
    /// <param name="request">The Example Data Set to create.</param>
    /// <returns>The created Example Data Set.</returns>
    [HttpPost("example-data-sets", Name = "CreateExampleDataSet")]
    [ProducesResponseType(typeof(ExampleDataSet), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> CreateExampleDataSetAsync([FromBody] CreateExampleDataSetRequest request)
    {
        _logger.LogInformation("Creating Example Data Set: {Name}", LogSanitiser.Sanitise(request.Name));

        var dataSet = new ExampleDataSet
        {
            Name = request.Name,
            Culture = request.Culture,
            BuiltIn = false,
            Created = DateTime.UtcNow,
            Values = (request.Values ?? []).Select(v => new ExampleDataSetValue { StringValue = v }).ToList()
        };

        await _application.ExampleData.CreateExampleDataSetAsync(dataSet);
        _logger.LogInformation("Created Example Data Set {Id} ({Name})", dataSet.Id, LogSanitiser.Sanitise(dataSet.Name));

        var created = await _application.ExampleData.GetExampleDataSetAsync(dataSet.Id);
        return CreatedAtRoute("GetExampleDataSet", new { id = dataSet.Id }, created);
    }

    /// <summary>
    /// Update an Example Data Set
    /// </summary>
    /// <param name="id">The unique identifier of the Example Data Set to update.</param>
    /// <param name="request">The properties to update.</param>
    /// <returns>The updated Example Data Set.</returns>
    [HttpPut("example-data-sets/{id:int}", Name = "UpdateExampleDataSet")]
    [ProducesResponseType(typeof(ExampleDataSet), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> UpdateExampleDataSetAsync(int id, [FromBody] UpdateExampleDataSetRequest request)
    {
        _logger.LogInformation("Updating Example Data Set: {Id}", id);

        var dataSet = await _application.ExampleData.GetExampleDataSetAsync(id);
        if (dataSet == null)
            return NotFound(ApiErrorResponse.NotFound($"Example Data Set with ID {id} not found."));

        if (dataSet.BuiltIn)
            return BadRequest(ApiErrorResponse.BadRequest("Built-in Example Data Sets cannot be updated."));

        if (!string.IsNullOrWhiteSpace(request.Name))
            dataSet.Name = request.Name;

        if (!string.IsNullOrWhiteSpace(request.Culture))
            dataSet.Culture = request.Culture;

        if (request.Values != null)
        {
            dataSet.Values.Clear();
            dataSet.Values.AddRange(request.Values.Select(v => new ExampleDataSetValue { StringValue = v }));
        }

        await _application.ExampleData.UpdateExampleDataSetAsync(dataSet);
        _logger.LogInformation("Updated Example Data Set {Id}", id);

        return Ok(dataSet);
    }

    /// <summary>
    /// Delete an Example Data Set
    /// </summary>
    /// <param name="id">The unique identifier of the Example Data Set to delete.</param>
    /// <returns>204 No Content on success.</returns>
    [HttpDelete("example-data-sets/{id:int}", Name = "DeleteExampleDataSet")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> DeleteExampleDataSetAsync(int id)
    {
        _logger.LogInformation("Deleting Example Data Set: {Id}", id);

        var dataSet = await _application.ExampleData.GetExampleDataSetAsync(id);
        if (dataSet == null)
            return NotFound(ApiErrorResponse.NotFound($"Example Data Set with ID {id} not found."));

        if (dataSet.BuiltIn)
            return BadRequest(ApiErrorResponse.BadRequest("Built-in Example Data Sets cannot be deleted."));

        await _application.ExampleData.DeleteExampleDataSetAsync(id);
        _logger.LogInformation("Deleted Example Data Set {Id}", id);

        return NoContent();
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
