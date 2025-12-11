using Asp.Versioning;
using JIM.Web.Extensions.Api;
using JIM.Web.Models.Api;
using JIM.Application;
using JIM.Models.Logic;
using JIM.Models.Logic.DTOs;
using JIM.Models.Staging;
using JIM.Models.Staging.DTOs;
using JIM.Models.Tasking;
using JIM.Utilities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace JIM.Web.Controllers.Api;

/// <summary>
/// API controller for managing synchronisation configuration including Connected Systems and Sync Rules.
/// </summary>
/// <remarks>
/// This controller provides endpoints for managing the synchronisation infrastructure:
/// - Connected Systems: External identity stores that sync with the Metaverse
/// - Sync Rules: Configuration for how data flows between Connected Systems and the Metaverse
/// </remarks>
[Route("api/v{version:apiVersion}/[controller]")]
[ApiController]
[ApiVersion("1.0")]
[Authorize(Roles = "Administrator")]
[Produces("application/json")]
public class SynchronisationController(ILogger<SynchronisationController> logger, JimApplication application) : ControllerBase
{
    private readonly ILogger<SynchronisationController> _logger = logger;
    private readonly JimApplication _application = application;

    #region Connected Systems

    /// <summary>
    /// Gets all connected systems with optional pagination, sorting, and filtering.
    /// </summary>
    /// <param name="pagination">Pagination parameters (page, pageSize, sortBy, sortDirection, filter).</param>
    /// <returns>A paginated list of connected system headers.</returns>
    [HttpGet("connected-systems", Name = "GetConnectedSystems")]
    [ProducesResponseType(typeof(PaginatedResponse<ConnectedSystemHeader>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetConnectedSystemsAsync([FromQuery] PaginationRequest pagination)
    {
        _logger.LogTrace("Requested connected systems (Page: {Page}, PageSize: {PageSize})", pagination.Page, pagination.PageSize);
        var systems = await _application.ConnectedSystems.GetConnectedSystemsAsync();
        var headers = systems.Select(ConnectedSystemHeader.FromEntity).AsQueryable();

        var result = headers
            .ApplySortAndFilter(pagination)
            .ToPaginatedResponse(pagination);

        return Ok(result);
    }

    /// <summary>
    /// Gets a specific connected system by ID.
    /// </summary>
    /// <param name="connectedSystemId">The unique identifier of the connected system.</param>
    /// <returns>The connected system details including configuration and schema.</returns>
    [HttpGet("connected-systems/{connectedSystemId:int}", Name = "GetConnectedSystem")]
    [ProducesResponseType(typeof(ConnectedSystemDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetConnectedSystemAsync(int connectedSystemId)
    {
        _logger.LogTrace("Requested connected system: {Id}", connectedSystemId);
        var system = await _application.ConnectedSystems.GetConnectedSystemAsync(connectedSystemId);
        if (system == null)
            return NotFound(ApiErrorResponse.NotFound($"Connected system with ID {connectedSystemId} not found."));

        return Ok(ConnectedSystemDetailDto.FromEntity(system));
    }

    /// <summary>
    /// Gets all object types defined in a connected system's schema.
    /// </summary>
    /// <param name="connectedSystemId">The unique identifier of the connected system.</param>
    /// <returns>A list of object types with their attributes.</returns>
    [HttpGet("connected-systems/{connectedSystemId:int}/object-types", Name = "GetConnectedSystemObjectTypes")]
    [ProducesResponseType(typeof(IEnumerable<ConnectedSystemObjectTypeDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetConnectedSystemObjectTypesAsync(int connectedSystemId)
    {
        _logger.LogTrace("Requested object types for connected system: {Id}", connectedSystemId);
        var objectTypes = await _application.ConnectedSystems.GetObjectTypesAsync(connectedSystemId);
        if (objectTypes == null)
            return NotFound(ApiErrorResponse.NotFound($"Connected system with ID {connectedSystemId} not found."));

        var dtos = objectTypes.Select(ConnectedSystemObjectTypeDto.FromEntity);
        return Ok(dtos);
    }

    /// <summary>
    /// Gets a specific connected system object by ID.
    /// </summary>
    /// <param name="connectedSystemId">The unique identifier of the connected system.</param>
    /// <param name="id">The unique identifier (GUID) of the connected system object.</param>
    /// <returns>The connected system object details including all attribute values.</returns>
    [HttpGet("connected-systems/{connectedSystemId:int}/objects/{id:guid}", Name = "GetConnectedSystemObject")]
    [ProducesResponseType(typeof(ConnectedSystemObjectDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetConnectedSystemObjectAsync(int connectedSystemId, Guid id)
    {
        _logger.LogTrace("Requested object {ObjectId} for connected system: {SystemId}", id, connectedSystemId);
        var obj = await _application.ConnectedSystems.GetConnectedSystemObjectAsync(connectedSystemId, id);
        if (obj == null)
            return NotFound(ApiErrorResponse.NotFound($"Object with ID {id} not found in connected system {connectedSystemId}."));

        return Ok(ConnectedSystemObjectDetailDto.FromEntity(obj));
    }

    /// <summary>
    /// Gets a preview of what will be affected by deleting a Connected System.
    /// </summary>
    /// <remarks>
    /// Call this before DeleteConnectedSystemAsync to inform the user of the impact.
    /// The preview includes counts of:
    /// - Connected System Objects that will be deleted
    /// - Sync Rules that will be removed
    /// - Metaverse Objects that will be disconnected
    /// - Pending exports that will be cancelled
    /// </remarks>
    /// <param name="connectedSystemId">The unique identifier of the connected system.</param>
    /// <returns>A preview showing counts of affected objects and any warnings.</returns>
    [HttpGet("connected-systems/{connectedSystemId:int}/deletion-preview", Name = "GetConnectedSystemDeletionPreview")]
    [ProducesResponseType(typeof(ConnectedSystemDeletionPreview), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetConnectedSystemDeletionPreviewAsync(int connectedSystemId)
    {
        _logger.LogInformation("Deletion preview requested for connected system: {Id}", connectedSystemId);

        var preview = await _application.ConnectedSystems.GetDeletionPreviewAsync(connectedSystemId);
        if (preview == null)
            return NotFound(ApiErrorResponse.NotFound($"Connected system with ID {connectedSystemId} not found."));

        return Ok(preview);
    }

    /// <summary>
    /// Creates a new Connected System.
    /// </summary>
    /// <remarks>
    /// Creates a new Connected System with the specified connector type. The connector's default settings
    /// will be applied automatically. Use the Update endpoint to configure the settings after creation.
    /// </remarks>
    /// <param name="request">The connected system creation request.</param>
    /// <returns>The created connected system details.</returns>
    /// <response code="201">Connected system created successfully.</response>
    /// <response code="400">Invalid request or connector definition not found.</response>
    /// <response code="401">User could not be identified from authentication token.</response>
    [HttpPost("connected-systems", Name = "CreateConnectedSystem")]
    [ProducesResponseType(typeof(ConnectedSystemDetailDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> CreateConnectedSystemAsync([FromBody] CreateConnectedSystemRequest request)
    {
        _logger.LogInformation("Creating connected system: {Name} with connector {ConnectorId}", request.Name, request.ConnectorDefinitionId);

        // Get the current user from the JWT claims
        var initiatedBy = await GetCurrentUserAsync();
        if (initiatedBy == null)
        {
            _logger.LogWarning("Could not identify user from JWT claims for connected system creation");
            return Unauthorized(ApiErrorResponse.Unauthorised("Could not identify user from authentication token."));
        }

        // Get the connector definition
        var connectorDefinition = await _application.ConnectedSystems.GetConnectorDefinitionAsync(request.ConnectorDefinitionId);
        if (connectorDefinition == null)
            return BadRequest(ApiErrorResponse.BadRequest($"Connector definition with ID {request.ConnectorDefinitionId} not found."));

        // Create the connected system
        var connectedSystem = new ConnectedSystem
        {
            Name = request.Name,
            Description = request.Description,
            ConnectorDefinition = connectorDefinition
        };

        try
        {
            await _application.ConnectedSystems.CreateConnectedSystemAsync(connectedSystem, initiatedBy);

            _logger.LogInformation("Created connected system: {Id} ({Name})", connectedSystem.Id, connectedSystem.Name);

            // Retrieve the created system to get all populated fields
            var created = await _application.ConnectedSystems.GetConnectedSystemAsync(connectedSystem.Id);
            return CreatedAtRoute("GetConnectedSystem", new { connectedSystemId = connectedSystem.Id }, ConnectedSystemDetailDto.FromEntity(created!));
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning(ex, "Failed to create connected system: {Message}", ex.Message);
            return BadRequest(ApiErrorResponse.BadRequest(ex.Message));
        }
    }

    /// <summary>
    /// Updates an existing Connected System.
    /// </summary>
    /// <remarks>
    /// Updates the name, description, and/or setting values of an existing Connected System.
    /// Only the fields provided in the request will be updated.
    /// </remarks>
    /// <param name="connectedSystemId">The unique identifier of the connected system to update.</param>
    /// <param name="request">The update request with new values.</param>
    /// <returns>The updated connected system details.</returns>
    /// <response code="200">Connected system updated successfully.</response>
    /// <response code="400">Invalid request.</response>
    /// <response code="404">Connected system not found.</response>
    /// <response code="401">User could not be identified from authentication token.</response>
    [HttpPut("connected-systems/{connectedSystemId:int}", Name = "UpdateConnectedSystem")]
    [ProducesResponseType(typeof(ConnectedSystemDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> UpdateConnectedSystemAsync(int connectedSystemId, [FromBody] UpdateConnectedSystemRequest request)
    {
        _logger.LogInformation("Updating connected system: {Id}", connectedSystemId);

        // Get the current user from the JWT claims
        var initiatedBy = await GetCurrentUserAsync();
        if (initiatedBy == null)
        {
            _logger.LogWarning("Could not identify user from JWT claims for connected system update");
            return Unauthorized(ApiErrorResponse.Unauthorised("Could not identify user from authentication token."));
        }

        // Get the existing connected system
        var connectedSystem = await _application.ConnectedSystems.GetConnectedSystemAsync(connectedSystemId);
        if (connectedSystem == null)
            return NotFound(ApiErrorResponse.NotFound($"Connected system with ID {connectedSystemId} not found."));

        // Apply updates
        if (!string.IsNullOrEmpty(request.Name))
            connectedSystem.Name = request.Name;

        if (request.Description != null)
            connectedSystem.Description = request.Description;

        // Update setting values if provided
        if (request.SettingValues != null)
        {
            foreach (var (settingId, update) in request.SettingValues)
            {
                var settingValue = connectedSystem.SettingValues.FirstOrDefault(sv => sv.Setting?.Id == settingId);
                if (settingValue != null)
                {
                    if (update.StringValue != null)
                        settingValue.StringValue = update.StringValue;
                    if (update.IntValue.HasValue)
                        settingValue.IntValue = update.IntValue.Value;
                    if (update.CheckboxValue.HasValue)
                        settingValue.CheckboxValue = update.CheckboxValue.Value;
                }
            }
        }

        try
        {
            await _application.ConnectedSystems.UpdateConnectedSystemAsync(connectedSystem, initiatedBy);

            _logger.LogInformation("Updated connected system: {Id} ({Name})", connectedSystem.Id, connectedSystem.Name);

            // Retrieve the updated system
            var updated = await _application.ConnectedSystems.GetConnectedSystemAsync(connectedSystemId);
            return Ok(ConnectedSystemDetailDto.FromEntity(updated!));
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning(ex, "Failed to update connected system: {Message}", ex.Message);
            return BadRequest(ApiErrorResponse.BadRequest(ex.Message));
        }
    }

    /// <summary>
    /// Deletes a Connected System and all its related data.
    /// </summary>
    /// <remarks>
    /// This operation may execute synchronously or be queued as a background job depending on system size:
    /// - Small systems (less than 1000 CSOs): Deleted immediately, returns 200 OK
    /// - Large systems: Queued as background job, returns 202 Accepted with tracking IDs
    /// - Systems with running sync: Queued to run after sync completes, returns 202 Accepted
    ///
    /// Use the deletion-preview endpoint first to understand the impact before calling this endpoint.
    /// </remarks>
    /// <param name="connectedSystemId">The unique identifier of the connected system to delete.</param>
    /// <returns>The result of the deletion request including outcome and tracking IDs.</returns>
    /// <response code="200">Deletion completed immediately.</response>
    /// <response code="202">Deletion has been queued as a background job.</response>
    /// <response code="400">Deletion failed.</response>
    /// <response code="401">User could not be identified from authentication token.</response>
    [HttpDelete("connected-systems/{connectedSystemId:int}", Name = "DeleteConnectedSystem")]
    [ProducesResponseType(typeof(ConnectedSystemDeletionResult), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ConnectedSystemDeletionResult), StatusCodes.Status202Accepted)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> DeleteConnectedSystemAsync(int connectedSystemId)
    {
        _logger.LogInformation("Deletion requested for connected system: {Id}", connectedSystemId);

        // Get the current user from the JWT claims
        var initiatedBy = await GetCurrentUserAsync();
        if (initiatedBy == null)
        {
            _logger.LogWarning("Could not identify user from JWT claims for deletion request");
            return Unauthorized(ApiErrorResponse.Unauthorised("Could not identify user from authentication token."));
        }

        var result = await _application.ConnectedSystems.DeleteAsync(connectedSystemId, initiatedBy);

        if (!result.Success)
            return BadRequest(ApiErrorResponse.BadRequest(result.ErrorMessage ?? "Deletion failed."));

        // Return 202 Accepted for queued operations, 200 OK for immediate completion
        if (result.Outcome == DeletionOutcome.QueuedAsBackgroundJob ||
            result.Outcome == DeletionOutcome.QueuedAfterSync)
        {
            return Accepted(result);
        }

        return Ok(result);
    }

    #endregion

    #region Run Profiles

    /// <summary>
    /// Gets all run profiles for a connected system.
    /// </summary>
    /// <param name="connectedSystemId">The unique identifier of the connected system.</param>
    /// <returns>A list of run profiles configured for the connected system.</returns>
    [HttpGet("connected-systems/{connectedSystemId:int}/run-profiles", Name = "GetRunProfiles")]
    [ProducesResponseType(typeof(IEnumerable<RunProfileDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetRunProfilesAsync(int connectedSystemId)
    {
        _logger.LogTrace("Requested run profiles for connected system: {Id}", connectedSystemId);

        var system = await _application.ConnectedSystems.GetConnectedSystemAsync(connectedSystemId);
        if (system == null)
            return NotFound(ApiErrorResponse.NotFound($"Connected system with ID {connectedSystemId} not found."));

        var runProfiles = await _application.ConnectedSystems.GetConnectedSystemRunProfilesAsync(connectedSystemId);
        var dtos = runProfiles.Select(RunProfileDto.FromEntity);
        return Ok(dtos);
    }

    /// <summary>
    /// Executes a run profile to trigger a synchronisation operation.
    /// </summary>
    /// <remarks>
    /// This endpoint queues a synchronisation task (Full Import, Delta Import, Full Sync, Delta Sync, or Export)
    /// for execution by the worker service. The task runs asynchronously and can be monitored via the Activities API.
    ///
    /// Returns 202 Accepted with the Activity ID and Task ID for tracking the execution.
    /// </remarks>
    /// <param name="connectedSystemId">The unique identifier of the connected system.</param>
    /// <param name="runProfileId">The unique identifier of the run profile to execute.</param>
    /// <returns>The execution response with activity and task IDs for tracking.</returns>
    /// <response code="202">Run profile execution has been queued.</response>
    /// <response code="404">Connected system or run profile not found.</response>
    /// <response code="401">User could not be identified from authentication token.</response>
    [HttpPost("connected-systems/{connectedSystemId:int}/run-profiles/{runProfileId:int}/execute", Name = "ExecuteRunProfile")]
    [ProducesResponseType(typeof(RunProfileExecutionResponse), StatusCodes.Status202Accepted)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> ExecuteRunProfileAsync(int connectedSystemId, int runProfileId)
    {
        _logger.LogInformation("Run profile execution requested: ConnectedSystem={SystemId}, RunProfile={ProfileId}",
            connectedSystemId, runProfileId);

        // Verify connected system exists
        var system = await _application.ConnectedSystems.GetConnectedSystemAsync(connectedSystemId);
        if (system == null)
            return NotFound(ApiErrorResponse.NotFound($"Connected system with ID {connectedSystemId} not found."));

        // Verify run profile exists and belongs to this connected system
        var runProfiles = await _application.ConnectedSystems.GetConnectedSystemRunProfilesAsync(connectedSystemId);
        var runProfile = runProfiles.FirstOrDefault(rp => rp.Id == runProfileId);
        if (runProfile == null)
            return NotFound(ApiErrorResponse.NotFound($"Run profile with ID {runProfileId} not found for connected system {connectedSystemId}."));

        // Get the current user from the JWT claims
        var initiatedBy = await GetCurrentUserAsync();
        if (initiatedBy == null)
        {
            _logger.LogWarning("Could not identify user from JWT claims for run profile execution");
            return Unauthorized(ApiErrorResponse.Unauthorised("Could not identify user from authentication token."));
        }

        // Create and queue the synchronisation task
        var workerTask = new SynchronisationWorkerTask(connectedSystemId, runProfileId, initiatedBy);

        await _application.Tasking.CreateWorkerTaskAsync(workerTask);

        _logger.LogInformation("Run profile execution queued: ConnectedSystem={SystemId}, RunProfile={ProfileId}, TaskId={TaskId}, ActivityId={ActivityId}",
            connectedSystemId, runProfileId, workerTask.Id, workerTask.Activity?.Id);

        var response = new RunProfileExecutionResponse
        {
            ActivityId = workerTask.Activity?.Id ?? Guid.Empty,
            TaskId = workerTask.Id,
            Message = $"Run profile '{runProfile.Name}' has been queued for execution."
        };

        return Accepted(response);
    }

    /// <summary>
    /// Creates a new Run Profile for a Connected System.
    /// </summary>
    /// <param name="connectedSystemId">The unique identifier of the connected system.</param>
    /// <param name="request">The run profile creation request.</param>
    /// <returns>The created run profile details.</returns>
    /// <response code="201">Run profile created successfully.</response>
    /// <response code="400">Invalid request or run type not supported by connector.</response>
    /// <response code="404">Connected system not found.</response>
    /// <response code="401">User could not be identified from authentication token.</response>
    [HttpPost("connected-systems/{connectedSystemId:int}/run-profiles", Name = "CreateRunProfile")]
    [ProducesResponseType(typeof(RunProfileDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> CreateRunProfileAsync(int connectedSystemId, [FromBody] CreateRunProfileRequest request)
    {
        _logger.LogInformation("Creating run profile: {Name} for connected system {SystemId}", request.Name, connectedSystemId);

        // Get the current user from the JWT claims
        var initiatedBy = await GetCurrentUserAsync();
        if (initiatedBy == null)
        {
            _logger.LogWarning("Could not identify user from JWT claims for run profile creation");
            return Unauthorized(ApiErrorResponse.Unauthorised("Could not identify user from authentication token."));
        }

        // Verify connected system exists
        var system = await _application.ConnectedSystems.GetConnectedSystemAsync(connectedSystemId);
        if (system == null)
            return NotFound(ApiErrorResponse.NotFound($"Connected system with ID {connectedSystemId} not found."));

        // Create the run profile
        var runProfile = new ConnectedSystemRunProfile
        {
            Name = request.Name,
            ConnectedSystemId = connectedSystemId,
            RunType = request.RunType,
            PageSize = request.PageSize,
            FilePath = request.FilePath
        };

        // Set partition if provided
        if (request.PartitionId.HasValue)
        {
            var partitions = await _application.ConnectedSystems.GetConnectedSystemPartitionsAsync(system);
            var partition = partitions.FirstOrDefault(p => p.Id == request.PartitionId.Value);
            if (partition == null)
                return BadRequest(ApiErrorResponse.BadRequest($"Partition with ID {request.PartitionId.Value} not found."));
            runProfile.Partition = partition;
        }

        try
        {
            await _application.ConnectedSystems.CreateConnectedSystemRunProfileAsync(runProfile, initiatedBy);

            _logger.LogInformation("Created run profile: {Id} ({Name})", runProfile.Id, runProfile.Name);

            return CreatedAtRoute("GetRunProfiles", new { connectedSystemId }, RunProfileDto.FromEntity(runProfile));
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning(ex, "Failed to create run profile: {Message}", ex.Message);
            return BadRequest(ApiErrorResponse.BadRequest(ex.Message));
        }
    }

    /// <summary>
    /// Updates an existing Run Profile.
    /// </summary>
    /// <param name="connectedSystemId">The unique identifier of the connected system.</param>
    /// <param name="runProfileId">The unique identifier of the run profile to update.</param>
    /// <param name="request">The update request with new values.</param>
    /// <returns>The updated run profile details.</returns>
    /// <response code="200">Run profile updated successfully.</response>
    /// <response code="400">Invalid request.</response>
    /// <response code="404">Connected system or run profile not found.</response>
    /// <response code="401">User could not be identified from authentication token.</response>
    [HttpPut("connected-systems/{connectedSystemId:int}/run-profiles/{runProfileId:int}", Name = "UpdateRunProfile")]
    [ProducesResponseType(typeof(RunProfileDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> UpdateRunProfileAsync(int connectedSystemId, int runProfileId, [FromBody] UpdateRunProfileRequest request)
    {
        _logger.LogInformation("Updating run profile: {Id} for connected system {SystemId}", runProfileId, connectedSystemId);

        // Get the current user from the JWT claims
        var initiatedBy = await GetCurrentUserAsync();
        if (initiatedBy == null)
        {
            _logger.LogWarning("Could not identify user from JWT claims for run profile update");
            return Unauthorized(ApiErrorResponse.Unauthorised("Could not identify user from authentication token."));
        }

        // Verify connected system exists
        var system = await _application.ConnectedSystems.GetConnectedSystemAsync(connectedSystemId);
        if (system == null)
            return NotFound(ApiErrorResponse.NotFound($"Connected system with ID {connectedSystemId} not found."));

        // Get the run profile
        var runProfiles = await _application.ConnectedSystems.GetConnectedSystemRunProfilesAsync(connectedSystemId);
        var runProfile = runProfiles.FirstOrDefault(rp => rp.Id == runProfileId);
        if (runProfile == null)
            return NotFound(ApiErrorResponse.NotFound($"Run profile with ID {runProfileId} not found for connected system {connectedSystemId}."));

        // Apply updates
        if (!string.IsNullOrEmpty(request.Name))
            runProfile.Name = request.Name;

        if (request.PageSize.HasValue)
            runProfile.PageSize = request.PageSize.Value;

        if (request.FilePath != null)
            runProfile.FilePath = request.FilePath;

        // Update partition if provided
        if (request.PartitionId.HasValue)
        {
            var partitions = await _application.ConnectedSystems.GetConnectedSystemPartitionsAsync(system);
            var partition = partitions.FirstOrDefault(p => p.Id == request.PartitionId.Value);
            if (partition == null)
                return BadRequest(ApiErrorResponse.BadRequest($"Partition with ID {request.PartitionId.Value} not found."));
            runProfile.Partition = partition;
        }

        try
        {
            await _application.ConnectedSystems.UpdateConnectedSystemRunProfileAsync(runProfile, initiatedBy);

            _logger.LogInformation("Updated run profile: {Id} ({Name})", runProfile.Id, runProfile.Name);

            return Ok(RunProfileDto.FromEntity(runProfile));
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning(ex, "Failed to update run profile: {Message}", ex.Message);
            return BadRequest(ApiErrorResponse.BadRequest(ex.Message));
        }
    }

    /// <summary>
    /// Deletes a Run Profile.
    /// </summary>
    /// <param name="connectedSystemId">The unique identifier of the connected system.</param>
    /// <param name="runProfileId">The unique identifier of the run profile to delete.</param>
    /// <returns>No content on success.</returns>
    /// <response code="204">Run profile deleted successfully.</response>
    /// <response code="404">Connected system or run profile not found.</response>
    /// <response code="401">User could not be identified from authentication token.</response>
    [HttpDelete("connected-systems/{connectedSystemId:int}/run-profiles/{runProfileId:int}", Name = "DeleteRunProfile")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> DeleteRunProfileAsync(int connectedSystemId, int runProfileId)
    {
        _logger.LogInformation("Deleting run profile: {Id} for connected system {SystemId}", runProfileId, connectedSystemId);

        // Get the current user from the JWT claims
        var initiatedBy = await GetCurrentUserAsync();
        if (initiatedBy == null)
        {
            _logger.LogWarning("Could not identify user from JWT claims for run profile deletion");
            return Unauthorized(ApiErrorResponse.Unauthorised("Could not identify user from authentication token."));
        }

        // Verify connected system exists
        var system = await _application.ConnectedSystems.GetConnectedSystemAsync(connectedSystemId);
        if (system == null)
            return NotFound(ApiErrorResponse.NotFound($"Connected system with ID {connectedSystemId} not found."));

        // Get the run profile
        var runProfiles = await _application.ConnectedSystems.GetConnectedSystemRunProfilesAsync(connectedSystemId);
        var runProfile = runProfiles.FirstOrDefault(rp => rp.Id == runProfileId);
        if (runProfile == null)
            return NotFound(ApiErrorResponse.NotFound($"Run profile with ID {runProfileId} not found for connected system {connectedSystemId}."));

        await _application.ConnectedSystems.DeleteConnectedSystemRunProfileAsync(runProfile, initiatedBy);

        _logger.LogInformation("Deleted run profile: {Id}", runProfileId);

        return NoContent();
    }

    #endregion

    #region Sync Rules

    /// <summary>
    /// Gets all synchronisation rules with optional pagination, sorting, and filtering.
    /// </summary>
    /// <param name="pagination">Pagination parameters (page, pageSize, sortBy, sortDirection, filter).</param>
    /// <returns>A paginated list of sync rule headers.</returns>
    [HttpGet("sync-rules", Name = "GetSyncRules")]
    [ProducesResponseType(typeof(PaginatedResponse<SyncRuleHeader>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetSyncRulesAsync([FromQuery] PaginationRequest pagination)
    {
        _logger.LogTrace("Requested synchronisation rules (Page: {Page}, PageSize: {PageSize})", pagination.Page, pagination.PageSize);
        var rules = await _application.ConnectedSystems.GetSyncRulesAsync();
        var headers = rules.Select(SyncRuleHeader.FromEntity).AsQueryable();

        var result = headers
            .ApplySortAndFilter(pagination)
            .ToPaginatedResponse(pagination);

        return Ok(result);
    }

    /// <summary>
    /// Gets a specific synchronisation rule by ID.
    /// </summary>
    /// <param name="id">The unique identifier of the sync rule.</param>
    /// <returns>The sync rule details including attribute flow configuration.</returns>
    [HttpGet("sync-rules/{id:int}", Name = "GetSyncRule")]
    [ProducesResponseType(typeof(SyncRuleHeader), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetSyncRuleAsync(int id)
    {
        _logger.LogTrace("Requested sync rule: {Id}", id);
        var rule = await _application.ConnectedSystems.GetSyncRuleAsync(id);
        if (rule == null)
            return NotFound(ApiErrorResponse.NotFound($"Sync rule with ID {id} not found."));

        return Ok(SyncRuleHeader.FromEntity(rule));
    }

    /// <summary>
    /// Creates a new Sync Rule.
    /// </summary>
    /// <remarks>
    /// Creates a sync rule that defines how data flows between a Connected System and the Metaverse.
    /// For Import rules, set ProjectToMetaverse to true to create Metaverse objects from imported data.
    /// For Export rules, set ProvisionToConnectedSystem to true to create Connected System objects.
    /// </remarks>
    /// <param name="request">The sync rule creation request.</param>
    /// <returns>The created sync rule details.</returns>
    /// <response code="201">Sync rule created successfully.</response>
    /// <response code="400">Invalid request or validation failed.</response>
    /// <response code="401">User could not be identified from authentication token.</response>
    [HttpPost("sync-rules", Name = "CreateSyncRule")]
    [ProducesResponseType(typeof(SyncRuleHeader), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> CreateSyncRuleAsync([FromBody] CreateSyncRuleRequest request)
    {
        _logger.LogInformation("Creating sync rule: {Name}", request.Name);

        // Get the current user from the JWT claims
        var initiatedBy = await GetCurrentUserAsync();
        if (initiatedBy == null)
        {
            _logger.LogWarning("Could not identify user from JWT claims for sync rule creation");
            return Unauthorized(ApiErrorResponse.Unauthorised("Could not identify user from authentication token."));
        }

        // Verify connected system exists
        var connectedSystem = await _application.ConnectedSystems.GetConnectedSystemAsync(request.ConnectedSystemId);
        if (connectedSystem == null)
            return BadRequest(ApiErrorResponse.BadRequest($"Connected system with ID {request.ConnectedSystemId} not found."));

        // Get connected system object type
        var csObjectTypes = await _application.ConnectedSystems.GetObjectTypesAsync(request.ConnectedSystemId);
        var csObjectType = csObjectTypes?.FirstOrDefault(t => t.Id == request.ConnectedSystemObjectTypeId);
        if (csObjectType == null)
            return BadRequest(ApiErrorResponse.BadRequest($"Connected system object type with ID {request.ConnectedSystemObjectTypeId} not found."));

        // Get metaverse object type
        var mvObjectType = await _application.Metaverse.GetMetaverseObjectTypeAsync(request.MetaverseObjectTypeId, false);
        if (mvObjectType == null)
            return BadRequest(ApiErrorResponse.BadRequest($"Metaverse object type with ID {request.MetaverseObjectTypeId} not found."));

        // Create the sync rule
        var syncRule = new SyncRule
        {
            Name = request.Name,
            ConnectedSystem = connectedSystem,
            ConnectedSystemId = request.ConnectedSystemId,
            ConnectedSystemObjectType = csObjectType,
            ConnectedSystemObjectTypeId = request.ConnectedSystemObjectTypeId,
            MetaverseObjectType = mvObjectType,
            MetaverseObjectTypeId = request.MetaverseObjectTypeId,
            Direction = request.Direction,
            ProjectToMetaverse = request.ProjectToMetaverse,
            ProvisionToConnectedSystem = request.ProvisionToConnectedSystem,
            Enabled = request.Enabled
        };

        var success = await _application.ConnectedSystems.CreateOrUpdateSyncRuleAsync(syncRule, initiatedBy);
        if (!success)
        {
            var validationErrors = syncRule.Validate();
            var errorMessage = string.Join("; ", validationErrors.Select(v => v.Message));
            return BadRequest(ApiErrorResponse.BadRequest($"Sync rule validation failed: {errorMessage}"));
        }

        _logger.LogInformation("Created sync rule: {Id} ({Name})", syncRule.Id, syncRule.Name);

        // Retrieve the created sync rule
        var created = await _application.ConnectedSystems.GetSyncRuleAsync(syncRule.Id);
        return CreatedAtRoute("GetSyncRule", new { id = syncRule.Id }, SyncRuleHeader.FromEntity(created!));
    }

    /// <summary>
    /// Updates an existing Sync Rule.
    /// </summary>
    /// <param name="id">The unique identifier of the sync rule to update.</param>
    /// <param name="request">The update request with new values.</param>
    /// <returns>The updated sync rule details.</returns>
    /// <response code="200">Sync rule updated successfully.</response>
    /// <response code="400">Invalid request or validation failed.</response>
    /// <response code="404">Sync rule not found.</response>
    /// <response code="401">User could not be identified from authentication token.</response>
    [HttpPut("sync-rules/{id:int}", Name = "UpdateSyncRule")]
    [ProducesResponseType(typeof(SyncRuleHeader), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> UpdateSyncRuleAsync(int id, [FromBody] UpdateSyncRuleRequest request)
    {
        _logger.LogInformation("Updating sync rule: {Id}", id);

        // Get the current user from the JWT claims
        var initiatedBy = await GetCurrentUserAsync();
        if (initiatedBy == null)
        {
            _logger.LogWarning("Could not identify user from JWT claims for sync rule update");
            return Unauthorized(ApiErrorResponse.Unauthorised("Could not identify user from authentication token."));
        }

        // Get the existing sync rule
        var syncRule = await _application.ConnectedSystems.GetSyncRuleAsync(id);
        if (syncRule == null)
            return NotFound(ApiErrorResponse.NotFound($"Sync rule with ID {id} not found."));

        // Apply updates
        if (!string.IsNullOrEmpty(request.Name))
            syncRule.Name = request.Name;

        if (request.Enabled.HasValue)
            syncRule.Enabled = request.Enabled.Value;

        if (request.ProjectToMetaverse.HasValue)
            syncRule.ProjectToMetaverse = request.ProjectToMetaverse.Value;

        if (request.ProvisionToConnectedSystem.HasValue)
            syncRule.ProvisionToConnectedSystem = request.ProvisionToConnectedSystem.Value;

        var success = await _application.ConnectedSystems.CreateOrUpdateSyncRuleAsync(syncRule, initiatedBy);
        if (!success)
        {
            var validationErrors = syncRule.Validate();
            var errorMessage = string.Join("; ", validationErrors.Select(v => v.Message));
            return BadRequest(ApiErrorResponse.BadRequest($"Sync rule validation failed: {errorMessage}"));
        }

        _logger.LogInformation("Updated sync rule: {Id} ({Name})", syncRule.Id, syncRule.Name);

        // Retrieve the updated sync rule
        var updated = await _application.ConnectedSystems.GetSyncRuleAsync(id);
        return Ok(SyncRuleHeader.FromEntity(updated!));
    }

    /// <summary>
    /// Deletes a Sync Rule.
    /// </summary>
    /// <param name="id">The unique identifier of the sync rule to delete.</param>
    /// <returns>No content on success.</returns>
    /// <response code="204">Sync rule deleted successfully.</response>
    /// <response code="404">Sync rule not found.</response>
    /// <response code="401">User could not be identified from authentication token.</response>
    [HttpDelete("sync-rules/{id:int}", Name = "DeleteSyncRule")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> DeleteSyncRuleAsync(int id)
    {
        _logger.LogInformation("Deleting sync rule: {Id}", id);

        // Get the current user from the JWT claims
        var initiatedBy = await GetCurrentUserAsync();
        if (initiatedBy == null)
        {
            _logger.LogWarning("Could not identify user from JWT claims for sync rule deletion");
            return Unauthorized(ApiErrorResponse.Unauthorised("Could not identify user from authentication token."));
        }

        // Get the sync rule
        var syncRule = await _application.ConnectedSystems.GetSyncRuleAsync(id);
        if (syncRule == null)
            return NotFound(ApiErrorResponse.NotFound($"Sync rule with ID {id} not found."));

        await _application.ConnectedSystems.DeleteSyncRuleAsync(syncRule, initiatedBy);

        _logger.LogInformation("Deleted sync rule: {Id}", id);

        return NoContent();
    }

    #endregion

    #region Private Helpers

    /// <summary>
    /// Resolves the current user from JWT claims by looking up their SSO identifier in the Metaverse.
    /// </summary>
    private async Task<JIM.Models.Core.MetaverseObject?> GetCurrentUserAsync()
    {
        if (User.Identity?.IsAuthenticated != true)
            return null;

        // Get the service settings to know which claim type contains the unique identifier
        var serviceSettings = await _application.ServiceSettings.GetServiceSettingsAsync();
        if (serviceSettings?.SSOUniqueIdentifierClaimType == null ||
            serviceSettings.SSOUniqueIdentifierMetaverseAttribute == null)
        {
            _logger.LogError("Service settings are not configured for SSO claim mapping");
            return null;
        }

        // Get the unique identifier from the JWT claims
        var uniqueIdClaimValue = IdentityUtilities.GetSsoUniqueIdentifier(
            User,
            serviceSettings.SSOUniqueIdentifierClaimType);

        if (string.IsNullOrEmpty(uniqueIdClaimValue))
        {
            _logger.LogWarning("JWT does not contain the expected claim: {ClaimType}",
                serviceSettings.SSOUniqueIdentifierClaimType);
            return null;
        }

        // Look up the user in the Metaverse
        var userType = await _application.Metaverse.GetMetaverseObjectTypeAsync(
            JIM.Models.Core.Constants.BuiltInObjectTypes.User,
            false);

        if (userType == null)
        {
            _logger.LogError("Could not find User object type in Metaverse");
            return null;
        }

        return await _application.Metaverse.GetMetaverseObjectByTypeAndAttributeAsync(
            userType,
            serviceSettings.SSOUniqueIdentifierMetaverseAttribute,
            uniqueIdClaimValue);
    }

    #endregion
}
