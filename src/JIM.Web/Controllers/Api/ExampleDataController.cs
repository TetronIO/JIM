// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using Asp.Versioning;
using JIM.Web.Extensions.Api;
using JIM.Web.Models.Api;
using JIM.Application;
using JIM.Models.Activities;
using JIM.Models.Activities.DTOs;
using JIM.Models.Core;
using JIM.Models.ExampleData;
using JIM.Models.ExampleData.DTOs;
using JIM.Models.Exceptions;
using JIM.Models.Staging;
using JIM.Models.Tasking;
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
public class ExampleDataController(ILogger<ExampleDataController> logger, JimApplication application) : ApiControllerBase(application, logger)
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
    [ProducesResponseType(typeof(ExampleDataSetDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetExampleDataSetAsync(int id)
    {
        _logger.LogTrace("Requested example data set: {Id}", id);
        var dataSet = await _application.ExampleData.GetExampleDataSetAsync(id);
        if (dataSet == null)
            return NotFound(ApiErrorResponse.NotFound($"Example Data Set with ID {id} not found."));

        return Ok(ExampleDataSetDto.FromEntity(dataSet));
    }

    /// <summary>
    /// Create an Example Data Set
    /// </summary>
    /// <param name="request">The Example Data Set to create.</param>
    /// <returns>The created Example Data Set.</returns>
    [HttpPost("example-data-sets", Name = "CreateExampleDataSet")]
    [ProducesResponseType(typeof(ExampleDataSetDto), StatusCodes.Status201Created)]
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

        var apiKey = await GetCurrentApiKeyAsync();
        if (apiKey != null)
            await _application.ExampleData.CreateExampleDataSetAsync(dataSet, apiKey, request.ChangeReason);
        else
            await _application.ExampleData.CreateExampleDataSetAsync(dataSet, await GetCurrentUserAsync(), request.ChangeReason);
        _logger.LogInformation("Created Example Data Set {Id} ({Name})", dataSet.Id, LogSanitiser.Sanitise(dataSet.Name));

        var created = await _application.ExampleData.GetExampleDataSetAsync(dataSet.Id);
        return CreatedAtRoute("GetExampleDataSet", new { id = dataSet.Id }, created == null ? null : ExampleDataSetDto.FromEntity(created));
    }

    /// <summary>
    /// Update an Example Data Set
    /// </summary>
    /// <param name="id">The unique identifier of the Example Data Set to update.</param>
    /// <param name="request">The properties to update.</param>
    /// <returns>The updated Example Data Set.</returns>
    [HttpPut("example-data-sets/{id:int}", Name = "UpdateExampleDataSet")]
    [ProducesResponseType(typeof(ExampleDataSetDto), StatusCodes.Status200OK)]
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

        var apiKey = await GetCurrentApiKeyAsync();
        if (apiKey != null)
            await _application.ExampleData.UpdateExampleDataSetAsync(dataSet, apiKey, request.ChangeReason);
        else
            await _application.ExampleData.UpdateExampleDataSetAsync(dataSet, await GetCurrentUserAsync(), request.ChangeReason);
        _logger.LogInformation("Updated Example Data Set {Id}", id);

        return Ok(ExampleDataSetDto.FromEntity(dataSet));
    }

    /// <summary>
    /// Delete an Example Data Set
    /// </summary>
    /// <param name="id">The unique identifier of the Example Data Set to delete.</param>
    /// <param name="changeReason">Optional reason for the deletion, recorded against this Example Data Set's change history.</param>
    /// <returns>204 No Content on success.</returns>
    [HttpDelete("example-data-sets/{id:int}", Name = "DeleteExampleDataSet")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> DeleteExampleDataSetAsync(int id, [FromQuery] string? changeReason = null)
    {
        _logger.LogInformation("Deleting Example Data Set: {Id}", id);

        var dataSet = await _application.ExampleData.GetExampleDataSetAsync(id);
        if (dataSet == null)
            return NotFound(ApiErrorResponse.NotFound($"Example Data Set with ID {id} not found."));

        if (dataSet.BuiltIn)
            return BadRequest(ApiErrorResponse.BadRequest("Built-in Example Data Sets cannot be deleted."));

        var apiKey = await GetCurrentApiKeyAsync();
        if (apiKey != null)
            await _application.ExampleData.DeleteExampleDataSetAsync(id, apiKey, changeReason);
        else
            await _application.ExampleData.DeleteExampleDataSetAsync(id, await GetCurrentUserAsync(), changeReason);
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
    [ProducesResponseType(typeof(ExampleDataTemplateDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetTemplateAsync(int id)
    {
        _logger.LogTrace("Requested data generation template: {Id}", id);
        var template = await _application.ExampleData.GetTemplateAsync(id);
        if (template == null)
            return NotFound(ApiErrorResponse.NotFound($"Data generation template with ID {id} not found."));

        return Ok(ExampleDataTemplateDto.FromEntity(template));
    }

    /// <summary>
    /// Create a Data Generation Template
    /// </summary>
    /// <remarks>
    /// Referenced objects (Metaverse Object Types, Metaverse Attributes, Connected System attributes and
    /// Example Data Sets) are identified by id; resolve names to ids via their respective GET endpoints first.
    /// </remarks>
    /// <param name="request">The template to create.</param>
    /// <returns>The created template.</returns>
    /// <response code="201">The template was created.</response>
    /// <response code="400">The template failed validation.</response>
    /// <response code="404">A referenced object could not be found.</response>
    /// <response code="409">A Data Generation Template with that name already exists.</response>
    /// <response code="401">The initiating principal could not be identified.</response>
    [HttpPost("templates", Name = "CreateExampleDataTemplate")]
    [ProducesResponseType(typeof(ExampleDataTemplateDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> CreateTemplateAsync([FromBody] CreateExampleDataTemplateRequest request)
    {
        _logger.LogInformation("Creating Data Generation Template: {Name}", LogSanitiser.Sanitise(request.Name));

        var (initiatorType, initiatorId, initiatorName) = await GetInitiatorInfoAsync();
        if (initiatorId == null && string.IsNullOrWhiteSpace(initiatorName))
        {
            _logger.LogWarning("Could not identify the initiating principal for Data Generation Template creation");
            return Unauthorized(ApiErrorResponse.Unauthorised("Could not identify user from authentication token."));
        }

        var (objectTypes, resolutionError) = await BuildTemplateObjectTypesAsync(request.ObjectTypes);
        if (resolutionError != null)
            return resolutionError;

        var template = new ExampleDataTemplate
        {
            Name = request.Name,
            BuiltIn = false,
            Created = DateTime.UtcNow
        };
        template.ObjectTypes.AddRange(objectTypes!);

        try
        {
            await _application.ExampleData.CreateTemplateAsync(template, initiatorType, initiatorId, initiatorName, request.ChangeReason);
        }
        catch (ExampleDataTemplateException ex)
        {
            return TemplateRejected("creation", ex);
        }
        catch (ExampleDataTemplateAttributeException ex)
        {
            return TemplateRejected("creation", ex);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning("Data Generation Template creation conflicted: {Message}", LogSanitiser.Sanitise(ex.Message));
            return Conflict(ApiErrorResponse.Conflict(ex.Message));
        }

        _logger.LogInformation("Created Data Generation Template {Id} ({Name})", template.Id, LogSanitiser.Sanitise(template.Name));

        var created = await _application.ExampleData.GetTemplateAsync(template.Id);
        return CreatedAtRoute("GetExampleDataTemplate", new { id = template.Id }, created == null ? null : ExampleDataTemplateDto.FromEntity(created));
    }

    /// <summary>
    /// Update a Data Generation Template
    /// </summary>
    /// <remarks>
    /// Omitted properties are left unchanged. Supplying ObjectTypes replaces the template's Object Type graph
    /// entirely; omitting it leaves the existing graph untouched, so a rename needs only the Name property.
    /// </remarks>
    /// <param name="id">The unique identifier of the template to update.</param>
    /// <param name="request">The properties to update.</param>
    /// <returns>The updated template.</returns>
    /// <response code="200">The template was updated.</response>
    /// <response code="400">The template is built in, or the update failed validation.</response>
    /// <response code="404">The template, or a referenced object, could not be found.</response>
    /// <response code="409">Another Data Generation Template already has the requested name.</response>
    /// <response code="401">The initiating principal could not be identified.</response>
    [HttpPut("templates/{id:int}", Name = "UpdateExampleDataTemplate")]
    [ProducesResponseType(typeof(ExampleDataTemplateDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> UpdateTemplateAsync(int id, [FromBody] UpdateExampleDataTemplateRequest request)
    {
        _logger.LogInformation("Updating Data Generation Template: {Id}", id);

        var (initiatorType, initiatorId, initiatorName) = await GetInitiatorInfoAsync();
        if (initiatorId == null && string.IsNullOrWhiteSpace(initiatorName))
        {
            _logger.LogWarning("Could not identify the initiating principal for Data Generation Template update");
            return Unauthorized(ApiErrorResponse.Unauthorised("Could not identify user from authentication token."));
        }

        var existing = await _application.ExampleData.GetTemplateAsync(id);
        if (existing == null)
            return NotFound(ApiErrorResponse.NotFound($"Data generation template with ID {id} not found."));

        if (existing.BuiltIn)
            return BadRequest(ApiErrorResponse.BadRequest("Built-in Data Generation Templates cannot be updated."));

        // A detached template carrying the existing identity and audit provenance; the server decides whether the
        // Object Type graph below it replaces the persisted one (see replaceObjectTypes).
        var template = new ExampleDataTemplate
        {
            Id = existing.Id,
            Name = request.Name ?? existing.Name,
            BuiltIn = existing.BuiltIn,
            Created = existing.Created,
            CreatedByType = existing.CreatedByType,
            CreatedById = existing.CreatedById,
            CreatedByName = existing.CreatedByName
        };

        if (request.ObjectTypes != null)
        {
            var (objectTypes, resolutionError) = await BuildTemplateObjectTypesAsync(request.ObjectTypes);
            if (resolutionError != null)
                return resolutionError;

            template.ObjectTypes.AddRange(objectTypes!);
        }

        try
        {
            await _application.ExampleData.UpdateTemplateAsync(template, initiatorType, initiatorId, initiatorName, request.ChangeReason, request.ObjectTypes != null);
        }
        catch (ExampleDataTemplateException ex)
        {
            return TemplateRejected("update", ex);
        }
        catch (ExampleDataTemplateAttributeException ex)
        {
            return TemplateRejected("update", ex);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning("Data Generation Template update conflicted: {Message}", LogSanitiser.Sanitise(ex.Message));
            return Conflict(ApiErrorResponse.Conflict(ex.Message));
        }

        _logger.LogInformation("Updated Data Generation Template {Id}", id);

        var updated = await _application.ExampleData.GetTemplateAsync(id) ?? template;
        return Ok(ExampleDataTemplateDto.FromEntity(updated));
    }

    /// <summary>
    /// Delete a Data Generation Template
    /// </summary>
    /// <param name="id">The unique identifier of the template to delete.</param>
    /// <param name="changeReason">Optional reason for the deletion, recorded against this template's change history.</param>
    /// <returns>204 No Content on success.</returns>
    /// <response code="204">The template was deleted.</response>
    /// <response code="400">The template is built in and cannot be deleted.</response>
    /// <response code="404">The template could not be found.</response>
    /// <response code="401">The initiating principal could not be identified.</response>
    [HttpDelete("templates/{id:int}", Name = "DeleteExampleDataTemplate")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> DeleteTemplateAsync(int id, [FromQuery] string? changeReason = null)
    {
        _logger.LogInformation("Deleting Data Generation Template: {Id}", id);

        var (initiatorType, initiatorId, initiatorName) = await GetInitiatorInfoAsync();
        if (initiatorId == null && string.IsNullOrWhiteSpace(initiatorName))
        {
            _logger.LogWarning("Could not identify the initiating principal for Data Generation Template deletion");
            return Unauthorized(ApiErrorResponse.Unauthorised("Could not identify user from authentication token."));
        }

        var template = await _application.ExampleData.GetTemplateAsync(id);
        if (template == null)
            return NotFound(ApiErrorResponse.NotFound($"Data generation template with ID {id} not found."));

        if (template.BuiltIn)
            return BadRequest(ApiErrorResponse.BadRequest("Built-in Data Generation Templates cannot be deleted."));

        await _application.ExampleData.DeleteTemplateAsync(id, initiatorType, initiatorId, initiatorName, changeReason);
        _logger.LogInformation("Deleted Data Generation Template {Id}", id);

        return NoContent();
    }

    /// <summary>
    /// Execute a Data Generation Template
    /// </summary>
    /// <remarks>
    /// Execution is queued as a worker task and tracked by an Activity, exactly like Run Profile execution
    /// via the portal. Follow progress via GET /activities/{id} or the lightweight
    /// GET /activities/{id}/progress endpoint using the returned Activity ID; any generation failure is
    /// recorded on the Activity.
    /// </remarks>
    /// <param name="id">The unique identifier of the template to execute.</param>
    /// <returns>202 Accepted with the tracking Activity and worker task IDs.</returns>
    /// <response code="202">Template execution has been queued.</response>
    /// <response code="404">Template not found.</response>
    /// <response code="401">User could not be identified from authentication token.</response>
    [HttpPost("templates/{id:int}/execute", Name = "ExecuteExampleDataTemplate")]
    [ProducesResponseType(typeof(ExampleDataTemplateExecutionResponse), StatusCodes.Status202Accepted)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> ExecuteTemplateAsync(int id)
    {
        _logger.LogInformation("Data generation template execution requested: {Id}", id);

        // Check template exists before queueing
        var template = await _application.ExampleData.GetTemplateAsync(id);
        if (template == null)
            return NotFound(ApiErrorResponse.NotFound($"Data generation template with ID {id} not found."));

        // Get the current user from the JWT claims (may be null for API key auth)
        var initiatedBy = await GetCurrentUserAsync();
        if (initiatedBy == null && !IsApiKeyAuthenticated())
        {
            _logger.LogWarning("Could not identify user from JWT claims for template execution");
            return Unauthorized(ApiErrorResponse.Unauthorised("Could not identify user from authentication token."));
        }

        // Queue the execution as a worker task; the Tasking layer records the tracking Activity.
        // Use API key for attribution when authenticated via API key.
        ExampleDataTemplateWorkerTask workerTask;
        if (initiatedBy != null)
        {
            workerTask = ExampleDataTemplateWorkerTask.ForUser(id, initiatedBy.Id, initiatedBy.NameOrId);
        }
        else
        {
            var apiKey = await GetCurrentApiKeyAsync();
            if (apiKey == null)
            {
                _logger.LogError("Failed to resolve API key for template execution");
                return BadRequest(ApiErrorResponse.BadRequest("Failed to identify initiating API key."));
            }
            workerTask = ExampleDataTemplateWorkerTask.ForApiKey(id, apiKey.Id, apiKey.Name);
        }

        var result = await _application.Tasking.CreateWorkerTaskAsync(workerTask);
        if (!result.Success)
        {
            _logger.LogWarning("Template execution blocked: {Error}", LogSanitiser.Sanitise(result.ErrorMessage));
            return BadRequest(ApiErrorResponse.BadRequest(result.ErrorMessage ?? "Validation failed."));
        }

        _logger.LogInformation("Data generation template execution queued: TemplateId={TemplateId}, TaskId={TaskId}, ActivityId={ActivityId}",
            id, workerTask.Id, workerTask.Activity?.Id);

        var response = new ExampleDataTemplateExecutionResponse
        {
            ActivityId = workerTask.Activity?.Id ?? Guid.Empty,
            TaskId = workerTask.Id,
            Message = $"Data Generation Template '{template.Name}' has been queued for execution."
        };

        return Accepted(response);
    }

    #region Configuration Change History

    /// <summary>
    /// List the change history for an Example Data Set.
    /// </summary>
    /// <param name="id">The unique identifier of the Example Data Set.</param>
    /// <param name="pagination">Pagination parameters.</param>
    /// <returns>A paged list of change-history entries, newest version first, each with a one-line summary.</returns>
    /// <response code="200">Change history returned (empty if the Example Data Set has no recorded configuration changes).</response>
    /// <response code="401">User could not be identified from authentication token.</response>
    [HttpGet("example-data-sets/{id:int}/change-history", Name = "GetExampleDataSetChangeHistory")]
    [ProducesResponseType(typeof(PaginatedResponse<ConfigurationChangeHistoryItem>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetExampleDataSetChangeHistoryAsync(int id, [FromQuery] PaginationRequest pagination)
    {
        var result = await _application.ChangeHistory.GetConfigurationChangeHistoryAsync(ActivityTargetType.ExampleDataSet, id, pagination.Page, pagination.PageSize);
        return Ok(PaginatedResponse<ConfigurationChangeHistoryItem>.Create(result.Results, result.TotalResults, pagination.Page, pagination.PageSize));
    }

    /// <summary>
    /// Get a single version of an Example Data Set's change history, with its snapshot and the diff against the previous version.
    /// </summary>
    /// <param name="id">The unique identifier of the Example Data Set.</param>
    /// <param name="changeVersion">The per-object change version to retrieve.</param>
    /// <returns>The change detail: metadata, the snapshot, and the diff against the previous version.</returns>
    /// <response code="200">The change detail.</response>
    /// <response code="404">No change with that version was found for the Example Data Set.</response>
    /// <response code="401">User could not be identified from authentication token.</response>
    [HttpGet("example-data-sets/{id:int}/change-history/{changeVersion:int}", Name = "GetExampleDataSetChange")]
    [ProducesResponseType(typeof(ConfigurationChangeDetail), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetExampleDataSetChangeAsync(int id, int changeVersion)
    {
        var detail = await _application.ChangeHistory.GetConfigurationChangeAsync(ActivityTargetType.ExampleDataSet, id, changeVersion);
        if (detail == null)
            return NotFound(ApiErrorResponse.NotFound($"No change history found for Example Data Set {id} version {changeVersion}."));
        return Ok(detail);
    }

    /// <summary>
    /// Compare two versions of an Example Data Set's configuration.
    /// </summary>
    /// <param name="id">The unique identifier of the Example Data Set.</param>
    /// <param name="fromVersion">The earlier version to compare from.</param>
    /// <param name="toVersion">The later version to compare to.</param>
    /// <returns>The structured diff of the later version against the earlier.</returns>
    /// <response code="200">The diff.</response>
    /// <response code="404">One of the requested versions was not found for the Example Data Set.</response>
    /// <response code="401">User could not be identified from authentication token.</response>
    [HttpGet("example-data-sets/{id:int}/change-history/compare", Name = "CompareExampleDataSetChanges")]
    [ProducesResponseType(typeof(ConfigurationDiff), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> CompareExampleDataSetChangesAsync(int id, [FromQuery] int fromVersion, [FromQuery] int toVersion)
    {
        var diff = await _application.ChangeHistory.CompareConfigurationChangesAsync(ActivityTargetType.ExampleDataSet, id, fromVersion, toVersion);
        if (diff == null)
            return NotFound(ApiErrorResponse.NotFound($"Could not compare versions {fromVersion} and {toVersion} for Example Data Set {id}."));
        return Ok(diff);
    }

    /// <summary>
    /// List the change history for an Example Data Template.
    /// </summary>
    /// <param name="id">The unique identifier of the Example Data Template.</param>
    /// <param name="pagination">Pagination parameters.</param>
    /// <returns>A paged list of change-history entries, newest version first, each with a one-line summary.</returns>
    /// <response code="200">Change history returned (empty if the Example Data Template has no recorded configuration changes).</response>
    /// <response code="401">User could not be identified from authentication token.</response>
    [HttpGet("templates/{id:int}/change-history", Name = "GetExampleDataTemplateChangeHistory")]
    [ProducesResponseType(typeof(PaginatedResponse<ConfigurationChangeHistoryItem>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetExampleDataTemplateChangeHistoryAsync(int id, [FromQuery] PaginationRequest pagination)
    {
        var result = await _application.ChangeHistory.GetConfigurationChangeHistoryAsync(ActivityTargetType.ExampleDataTemplate, id, pagination.Page, pagination.PageSize);
        return Ok(PaginatedResponse<ConfigurationChangeHistoryItem>.Create(result.Results, result.TotalResults, pagination.Page, pagination.PageSize));
    }

    /// <summary>
    /// Get a single version of an Example Data Template's change history, with its snapshot and the diff against the previous version.
    /// </summary>
    /// <param name="id">The unique identifier of the Example Data Template.</param>
    /// <param name="changeVersion">The per-object change version to retrieve.</param>
    /// <returns>The change detail: metadata, the snapshot, and the diff against the previous version.</returns>
    /// <response code="200">The change detail.</response>
    /// <response code="404">No change with that version was found for the Example Data Template.</response>
    /// <response code="401">User could not be identified from authentication token.</response>
    [HttpGet("templates/{id:int}/change-history/{changeVersion:int}", Name = "GetExampleDataTemplateChange")]
    [ProducesResponseType(typeof(ConfigurationChangeDetail), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetExampleDataTemplateChangeAsync(int id, int changeVersion)
    {
        var detail = await _application.ChangeHistory.GetConfigurationChangeAsync(ActivityTargetType.ExampleDataTemplate, id, changeVersion);
        if (detail == null)
            return NotFound(ApiErrorResponse.NotFound($"No change history found for Example Data Template {id} version {changeVersion}."));
        return Ok(detail);
    }

    /// <summary>
    /// Compare two versions of an Example Data Template's configuration.
    /// </summary>
    /// <param name="id">The unique identifier of the Example Data Template.</param>
    /// <param name="fromVersion">The earlier version to compare from.</param>
    /// <param name="toVersion">The later version to compare to.</param>
    /// <returns>The structured diff of the later version against the earlier.</returns>
    /// <response code="200">The diff.</response>
    /// <response code="404">One of the requested versions was not found for the Example Data Template.</response>
    /// <response code="401">User could not be identified from authentication token.</response>
    [HttpGet("templates/{id:int}/change-history/compare", Name = "CompareExampleDataTemplateChanges")]
    [ProducesResponseType(typeof(ConfigurationDiff), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> CompareExampleDataTemplateChangesAsync(int id, [FromQuery] int fromVersion, [FromQuery] int toVersion)
    {
        var diff = await _application.ChangeHistory.CompareConfigurationChangesAsync(ActivityTargetType.ExampleDataTemplate, id, fromVersion, toVersion);
        if (diff == null)
            return NotFound(ApiErrorResponse.NotFound($"Could not compare versions {fromVersion} and {toVersion} for Example Data Template {id}."));
        return Ok(diff);
    }

    #endregion

    /// <summary>
    /// Logs and builds the 400 response for a Data Generation Template the server refused as invalid. Shared by the
    /// create and update endpoints, which catch the same two validation exception types with the same handling.
    /// </summary>
    private BadRequestObjectResult TemplateRejected(string operation, OperationalException exception)
    {
        _logger.LogWarning("Data Generation Template {Operation} rejected: {Message}", operation, LogSanitiser.Sanitise(exception.Message));
        return BadRequest(ApiErrorResponse.BadRequest(exception.Message));
    }

    // ─── Data Generation Template request mapping ───

    /// <summary>
    /// Builds the Object Type graph a create or update request describes, resolving every referenced id to its
    /// persisted entity. Each id is resolved once per request and the resolved instance reused wherever the id
    /// reappears, so the repository attaches one instance per key. Returns (null, error) on the first failure,
    /// with the offending id named in the error.
    /// </summary>
    private async Task<(List<ExampleDataObjectType>? objectTypes, IActionResult? error)> BuildTemplateObjectTypesAsync(List<ExampleDataTemplateObjectTypeRequest> requests)
    {
        var metaverseObjectTypes = new Dictionary<int, MetaverseObjectType>();
        var metaverseAttributes = new Dictionary<int, MetaverseAttribute>();
        var connectedSystemAttributes = new Dictionary<int, ConnectedSystemObjectTypeAttribute>();
        var exampleDataSets = new Dictionary<int, ExampleDataSet>();

        var objectTypes = new List<ExampleDataObjectType>();
        foreach (var request in requests)
        {
            var metaverseObjectType = await ResolveMetaverseObjectTypeAsync(request.MetaverseObjectTypeId, metaverseObjectTypes);
            if (metaverseObjectType == null)
                return (null, NotFound(ApiErrorResponse.NotFound($"Metaverse Object Type with ID {request.MetaverseObjectTypeId} not found.")));

            var objectType = new ExampleDataObjectType
            {
                MetaverseObjectType = metaverseObjectType,
                ObjectsToCreate = request.ObjectsToCreate
            };

            foreach (var attributeRequest in request.Attributes)
            {
                var (attribute, error) = await BuildTemplateAttributeAsync(attributeRequest, metaverseObjectTypes, metaverseAttributes, connectedSystemAttributes, exampleDataSets);
                if (error != null)
                    return (null, error);

                objectType.TemplateAttributes.Add(attribute!);
            }

            objectTypes.Add(objectType);
        }

        return (objectTypes, null);
    }

    /// <summary>
    /// Builds one template attribute from its request, resolving the attribute being generated, any Example Data
    /// Sets, reference Object Types and the optional attribute dependency. Returns (null, error) on the first failure.
    /// </summary>
    private async Task<(ExampleDataTemplateAttribute? attribute, IActionResult? error)> BuildTemplateAttributeAsync(
        ExampleDataTemplateAttributeRequest request,
        Dictionary<int, MetaverseObjectType> metaverseObjectTypes,
        Dictionary<int, MetaverseAttribute> metaverseAttributes,
        Dictionary<int, ConnectedSystemObjectTypeAttribute> connectedSystemAttributes,
        Dictionary<int, ExampleDataSet> exampleDataSets)
    {
        var attribute = new ExampleDataTemplateAttribute
        {
            PopulatedValuesPercentage = request.PopulatedValuesPercentage,
            BoolTrueDistribution = request.BoolTrueDistribution,
            BoolShouldBeRandom = request.BoolShouldBeRandom,
            MinDate = request.MinDate,
            MaxDate = request.MaxDate,
            MinNumber = request.MinNumber,
            MaxNumber = request.MaxNumber,
            SequentialNumbers = request.SequentialNumbers,
            RandomNumbers = request.RandomNumbers,
            Pattern = request.Pattern,
            Expression = request.Expression,
            ManagerDepthPercentage = request.ManagerDepthPercentage,
            MvaRefMinAssignments = request.MvaRefMinAssignments,
            MvaRefMaxAssignments = request.MvaRefMaxAssignments
        };

        if (request.MetaverseAttributeId.HasValue)
        {
            var metaverseAttributeId = request.MetaverseAttributeId.Value;
            var metaverseAttribute = await ResolveMetaverseAttributeAsync(metaverseAttributeId, metaverseAttributes);
            if (metaverseAttribute == null)
                return (null, NotFound(ApiErrorResponse.NotFound($"Metaverse Attribute with ID {metaverseAttributeId} not found.")));

            attribute.MetaverseAttribute = metaverseAttribute;
        }

        if (request.ConnectedSystemObjectTypeAttributeId.HasValue)
        {
            var connectedSystemAttributeId = request.ConnectedSystemObjectTypeAttributeId.Value;
            var connectedSystemAttribute = await ResolveConnectedSystemAttributeAsync(connectedSystemAttributeId, connectedSystemAttributes);
            if (connectedSystemAttribute == null)
                return (null, NotFound(ApiErrorResponse.NotFound($"Connected System Object Type attribute with ID {connectedSystemAttributeId} not found.")));

            attribute.ConnectedSystemObjectTypeAttribute = connectedSystemAttribute;
        }

        foreach (var dataSetRequest in request.ExampleDataSets ?? [])
        {
            var exampleDataSet = await ResolveExampleDataSetAsync(dataSetRequest.ExampleDataSetId, exampleDataSets);
            if (exampleDataSet == null)
                return (null, NotFound(ApiErrorResponse.NotFound($"Example Data Set with ID {dataSetRequest.ExampleDataSetId} not found.")));

            attribute.ExampleDataSetInstances.Add(new ExampleDataSetInstance
            {
                ExampleDataSet = exampleDataSet,
                Order = dataSetRequest.Order
            });
        }

        if (request.WeightedStringValues != null)
        {
            attribute.WeightedStringValues = request.WeightedStringValues
                .Select(weightedValue => new ExampleDataTemplateAttributeWeightedValue { Value = weightedValue.Value, Weight = weightedValue.Weight })
                .ToList();
        }

        if (request.ReferenceMetaverseObjectTypeIds != null)
        {
            attribute.ReferenceMetaverseObjectTypes = [];
            foreach (var referenceObjectTypeId in request.ReferenceMetaverseObjectTypeIds)
            {
                var referenceObjectType = await ResolveMetaverseObjectTypeAsync(referenceObjectTypeId, metaverseObjectTypes);
                if (referenceObjectType == null)
                    return (null, NotFound(ApiErrorResponse.NotFound($"Metaverse Object Type with ID {referenceObjectTypeId} not found.")));

                attribute.ReferenceMetaverseObjectTypes.Add(referenceObjectType);
            }
        }

        if (request.AttributeDependency != null)
        {
            var (dependency, dependencyError) = await BuildAttributeDependencyAsync(request.AttributeDependency, metaverseAttributes);
            if (dependencyError != null)
                return (null, dependencyError);

            attribute.AttributeDependency = dependency;
        }

        return (attribute, null);
    }

    /// <summary>
    /// Builds an attribute's conditional dependency from its request, resolving the depended-on Metaverse Attribute
    /// and parsing the comparison operator. Returns (null, error) on failure.
    /// </summary>
    private async Task<(ExampleDataTemplateAttributeDependency? dependency, IActionResult? error)> BuildAttributeDependencyAsync(
        ExampleDataTemplateAttributeDependencyRequest request,
        Dictionary<int, MetaverseAttribute> metaverseAttributes)
    {
        var metaverseAttribute = await ResolveMetaverseAttributeAsync(request.MetaverseAttributeId, metaverseAttributes);
        if (metaverseAttribute == null)
            return (null, NotFound(ApiErrorResponse.NotFound($"Metaverse Attribute with ID {request.MetaverseAttributeId} not found.")));

        if (!Enum.TryParse<ComparisonType>(request.ComparisonType, ignoreCase: true, out var comparisonType) || !Enum.IsDefined(comparisonType))
            return (null, BadRequest(ApiErrorResponse.BadRequest($"Invalid comparison type '{request.ComparisonType}'.")));

        return (new ExampleDataTemplateAttributeDependency
        {
            MetaverseAttribute = metaverseAttribute,
            ComparisonType = comparisonType,
            StringValue = request.StringValue
        }, null);
    }

    private async Task<MetaverseObjectType?> ResolveMetaverseObjectTypeAsync(int id, Dictionary<int, MetaverseObjectType> cache)
    {
        if (cache.TryGetValue(id, out var cached))
            return cached;

        var metaverseObjectType = await _application.Metaverse.GetMetaverseObjectTypeAsync(id, false);
        if (metaverseObjectType != null)
            cache[id] = metaverseObjectType;

        return metaverseObjectType;
    }

    private async Task<MetaverseAttribute?> ResolveMetaverseAttributeAsync(int id, Dictionary<int, MetaverseAttribute> cache)
    {
        if (cache.TryGetValue(id, out var cached))
            return cached;

        var metaverseAttribute = await _application.Metaverse.GetMetaverseAttributeAsync(id);
        if (metaverseAttribute != null)
            cache[id] = metaverseAttribute;

        return metaverseAttribute;
    }

    private async Task<ConnectedSystemObjectTypeAttribute?> ResolveConnectedSystemAttributeAsync(int id, Dictionary<int, ConnectedSystemObjectTypeAttribute> cache)
    {
        if (cache.TryGetValue(id, out var cached))
            return cached;

        var connectedSystemAttribute = await _application.ConnectedSystems.GetAttributeAsync(id);
        if (connectedSystemAttribute != null)
            cache[id] = connectedSystemAttribute;

        return connectedSystemAttribute;
    }

    private async Task<ExampleDataSet?> ResolveExampleDataSetAsync(int id, Dictionary<int, ExampleDataSet> cache)
    {
        if (cache.TryGetValue(id, out var cached))
            return cached;

        var exampleDataSet = await _application.ExampleData.GetExampleDataSetAsync(id);
        if (exampleDataSet != null)
            cache[id] = exampleDataSet;

        return exampleDataSet;
    }
}
