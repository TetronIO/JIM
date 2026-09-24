// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using Asp.Versioning;
using JIM.Application;
using JIM.Models.Activities;
using JIM.Models.Activities.DTOs;
using JIM.Models.Scheduling;
using JIM.Utilities;
using JIM.Web.Models.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace JIM.Web.Controllers.Api;

/// <summary>
/// API controller for managing synchronisation schedules.
/// </summary>
/// <remarks>
/// Schedules define automated execution plans for running synchronisation operations.
/// Each schedule contains one or more steps that execute sequentially or in parallel.
/// </remarks>
[Route("api/v{version:apiVersion}/[controller]")]
[ApiController]
[ApiVersion("1.0")]
[Authorize(Roles = "Administrator")]
[Produces("application/json")]
public class SchedulesController(ILogger<SchedulesController> logger, JimApplication application) : ApiControllerBase(application, logger)
{
    private readonly ILogger<SchedulesController> _logger = logger;
    private readonly JimApplication _application = application;

    /// <summary>
    /// List schedules
    /// </summary>
    /// <param name="page">The page number (1-based).</param>
    /// <param name="pageSize">The number of items per page.</param>
    /// <param name="search">Optional search query to filter by name or description.</param>
    /// <param name="sortBy">Optional field to sort by (name, created, nextRunTime).</param>
    /// <param name="sortDescending">Whether to sort in descending order.</param>
    /// <returns>A paginated list of schedules.</returns>
    [HttpGet(Name = "GetSchedules")]
    [ProducesResponseType(typeof(PaginatedResponse<ScheduleDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetAllAsync(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] string? search = null,
        [FromQuery] string? sortBy = null,
        [FromQuery] bool sortDescending = false)
    {
        _logger.LogTrace("Requested schedules page {Page}, size {PageSize}, search '{Search}'", page, pageSize, LogSanitiser.Sanitise(search));

        var result = await _application.Scheduler.GetScheduleHeadersAsync(page, pageSize, search, sortBy, sortDescending);
        var dtos = result.Results.Select(ScheduleDto.FromHeader).ToList();

        return Ok(PaginatedResponse<ScheduleDto>.Create(dtos, result.TotalResults, result.CurrentPage, result.PageSize));
    }

    /// <summary>
    /// Get schedule details
    /// </summary>
    /// <param name="id">The unique identifier of the schedule.</param>
    /// <returns>The schedule details including steps.</returns>
    [HttpGet("{id:guid}", Name = "GetSchedule")]
    [ProducesResponseType(typeof(ScheduleDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetByIdAsync(Guid id)
    {
        _logger.LogTrace("Requested schedule {ScheduleId}", id);
        var schedule = await _application.Scheduler.GetScheduleWithStepsAsync(id);

        if (schedule == null)
        {
            return NotFound(new ApiErrorResponse { Message = $"Schedule not found: {id}" });
        }

        return Ok(ScheduleDetailDto.FromEntity(schedule));
    }

    /// <summary>
    /// Create a schedule
    /// </summary>
    /// <param name="request">The schedule creation request.</param>
    /// <returns>The created schedule.</returns>
    [HttpPost(Name = "CreateSchedule")]
    [ProducesResponseType(typeof(ScheduleDetailDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> CreateAsync([FromBody] CreateScheduleRequest request)
    {
        _logger.LogInformation("Creating new schedule: {ScheduleName}", LogSanitiser.Sanitise(request.Name));

        // Validate request
        var validationError = ValidateScheduleRequest(request.TriggerType, request.CronExpression, request.Steps);
        if (validationError != null)
        {
            return BadRequest(new ApiErrorResponse { Message = validationError });
        }

        // Get initiator info
        var (initiatorType, initiatorId, initiatorName) = await GetInitiatorInfoAsync();

        // Create the schedule entity
        var schedule = new Schedule
        {
            Id = Guid.NewGuid(),
            Name = request.Name,
            Description = request.Description,
            TriggerType = request.TriggerType,
            CronExpression = request.CronExpression,
            IsEnabled = request.IsEnabled,
            OnStepFailure = request.OnStepFailure ?? ScheduleFailureBehaviour.Stop,
            Created = DateTime.UtcNow,
            CreatedByType = initiatorType,
            CreatedById = initiatorId,
            CreatedByName = initiatorName
        };

        // Create steps
        foreach (var stepRequest in request.Steps)
        {
            var step = stepRequest.ToEntity(schedule.Id);
            step.Created = DateTime.UtcNow;
            step.CreatedByType = initiatorType;
            step.CreatedById = initiatorId;
            step.CreatedByName = initiatorName;
            schedule.Steps.Add(step);
        }

        await _application.Scheduler.CreateScheduleAsync(schedule, initiatorType, initiatorId, initiatorName, changeReason: request.ChangeReason);

        _logger.LogInformation("Created schedule {ScheduleId} with {StepCount} steps", schedule.Id, schedule.Steps.Count);

        // Reload to get navigation properties
        var createdSchedule = await _application.Scheduler.GetScheduleWithStepsAsync(schedule.Id);
        return CreatedAtRoute("GetSchedule", new { id = schedule.Id }, ScheduleDetailDto.FromEntity(createdSchedule!));
    }

    /// <summary>
    /// Update a schedule
    /// </summary>
    /// <remarks>
    /// Replaces all schedule steps with the provided list; steps not included in the request will be deleted.
    /// </remarks>
    /// <param name="id">The unique identifier of the schedule.</param>
    /// <param name="request">The update request.</param>
    /// <returns>The updated schedule.</returns>
    [HttpPut("{id:guid}", Name = "UpdateSchedule")]
    [ProducesResponseType(typeof(ScheduleDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpdateAsync(Guid id, [FromBody] UpdateScheduleRequest request)
    {
        _logger.LogInformation("Updating schedule {ScheduleId}", id);

        var existingSchedule = await _application.Scheduler.GetScheduleAsync(id);
        if (existingSchedule == null)
        {
            return NotFound(new ApiErrorResponse { Message = $"Schedule not found: {id}" });
        }

        // A built-in schedule (for example the seeded Temporal Scope Reconciliation schedule) may be
        // re-timed and enabled/disabled, but its name is part of the product and cannot be changed.
        if (existingSchedule.BuiltIn && existingSchedule.Name != request.Name)
        {
            return BadRequest(new ApiErrorResponse { Message = $"The built-in schedule '{existingSchedule.Name}' cannot be renamed." });
        }

        // A built-in schedule's failure behaviour is JIM's, like its steps (#1787): it stays Stop. Sending the stored
        // value back is not a change, so a client that round-trips the whole Schedule still works.
        if (existingSchedule.BuiltIn && request.OnStepFailure.HasValue && request.OnStepFailure.Value != existingSchedule.OnStepFailure)
        {
            return BadRequest(new ApiErrorResponse { Message = $"The failure behaviour of the built-in schedule '{existingSchedule.Name}' cannot be changed." });
        }

        // Each requested step's failure setting is resolved here, before any field of the Schedule is touched, because
        // the write rule judges an existing step's continueOnFailure against the Schedule's setting as STORED, not as
        // this request is about to change it (ScheduleStepFailureWriteRule).
        var existingSteps = await _application.Scheduler.GetScheduleStepsAsync(id);
        var resolvedOnFailure = ResolveStepFailureBehaviours(request.Steps, existingSteps, existingSchedule);

        // A built-in schedule's steps are defined and maintained by JIM. Re-timing and enable/disable are
        // allowed, but steps cannot be added, removed, replaced or given a failure setting of their own. Enforced here
        // as a clean 400 and, as an authoritative backstop for any caller, in the application layer's step methods.
        if (existingSchedule.BuiltIn && BuiltInStepsWouldChange(existingSteps, request.Steps, resolvedOnFailure))
        {
            return BadRequest(new ApiErrorResponse { Message = $"The steps of the built-in schedule '{existingSchedule.Name}' cannot be changed." });
        }

        // Validate request. For a built-in schedule the steps are JIM-owned and immutable (guarded above), so
        // only the trigger is validated; the step validator does not recognise built-in step types.
        var validationError = existingSchedule.BuiltIn
            ? ValidateTrigger(request.TriggerType, request.CronExpression)
            : ValidateScheduleRequest(request.TriggerType, request.CronExpression, request.Steps);
        if (validationError != null)
        {
            return BadRequest(new ApiErrorResponse { Message = validationError });
        }

        // Get initiator info
        var (initiatorType, initiatorId, initiatorName) = await GetInitiatorInfoAsync();

        // Update schedule properties
        existingSchedule.Name = request.Name;
        existingSchedule.Description = request.Description;
        existingSchedule.TriggerType = request.TriggerType;
        existingSchedule.CronExpression = request.CronExpression;
        existingSchedule.IsEnabled = request.IsEnabled;
        if (request.OnStepFailure.HasValue)
        {
            existingSchedule.OnStepFailure = request.OnStepFailure.Value;
        }
        existingSchedule.LastUpdated = DateTime.UtcNow;
        existingSchedule.LastUpdatedByType = initiatorType;
        existingSchedule.LastUpdatedById = initiatorId;
        existingSchedule.LastUpdatedByName = initiatorName;

        // Reconcile steps before the audited schedule update, so the configuration snapshot captured by
        // UpdateScheduleAsync reflects this save's step changes too. A built-in schedule's steps are
        // immutable (guarded above); steps are only reconciled for user schedules.
        if (!existingSchedule.BuiltIn)
        {
            await ReconcileScheduleStepsAsync(id, request.Steps, existingSteps, resolvedOnFailure, initiatorType, initiatorId, initiatorName);
        }

        await _application.Scheduler.UpdateScheduleAsync(existingSchedule, initiatorType, initiatorId, initiatorName, changeReason: request.ChangeReason);

        _logger.LogInformation("Updated schedule {ScheduleId}", id);

        // Reload to get updated data
        var updatedSchedule = await _application.Scheduler.GetScheduleWithStepsAsync(id);
        return Ok(ScheduleDetailDto.FromEntity(updatedSchedule!));
    }

    /// <summary>
    /// Delete a schedule
    /// </summary>
    /// <param name="id">The unique identifier of the schedule to delete.</param>
    /// <param name="changeReason">An optional reason for the deletion, recorded against the change history.</param>
    /// <returns>No content on success.</returns>
    [HttpDelete("{id:guid}", Name = "DeleteSchedule")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteAsync(Guid id, [FromQuery] string? changeReason = null)
    {
        _logger.LogInformation("Deleting schedule {ScheduleId}", id);

        var existingSchedule = await _application.Scheduler.GetScheduleAsync(id);
        if (existingSchedule == null)
        {
            return NotFound(new ApiErrorResponse { Message = $"Schedule not found: {id}" });
        }

        // Built-in schedules are part of the product and cannot be deleted (only enabled/disabled/re-timed).
        if (existingSchedule.BuiltIn)
        {
            return BadRequest(new ApiErrorResponse { Message = $"The built-in schedule '{existingSchedule.Name}' cannot be deleted." });
        }

        var (initiatorType, initiatorId, initiatorName) = await GetInitiatorInfoAsync();
        await _application.Scheduler.DeleteScheduleAsync(existingSchedule, initiatorType, initiatorId, initiatorName, changeReason);

        _logger.LogInformation("Deleted schedule {ScheduleId}", id);
        return NoContent();
    }

    /// <summary>
    /// Enable a schedule
    /// </summary>
    /// <param name="id">The unique identifier of the schedule.</param>
    /// <param name="changeReason">An optional reason for the change, recorded against the change history.</param>
    /// <returns>The updated schedule.</returns>
    [HttpPost("{id:guid}/enable", Name = "EnableSchedule")]
    [ProducesResponseType(typeof(ScheduleDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> EnableAsync(Guid id, [FromQuery] string? changeReason = null)
    {
        _logger.LogInformation("Enabling schedule {ScheduleId}", id);

        var schedule = await _application.Scheduler.GetScheduleAsync(id);
        if (schedule == null)
        {
            return NotFound(new ApiErrorResponse { Message = $"Schedule not found: {id}" });
        }

        var (initiatorType, initiatorId, initiatorName) = await GetInitiatorInfoAsync();

        schedule.IsEnabled = true;
        schedule.LastUpdated = DateTime.UtcNow;
        schedule.LastUpdatedByType = initiatorType;
        schedule.LastUpdatedById = initiatorId;
        schedule.LastUpdatedByName = initiatorName;

        await _application.Scheduler.UpdateScheduleAsync(schedule, initiatorType, initiatorId, initiatorName, changeReason);

        _logger.LogInformation("Enabled schedule {ScheduleId}", id);
        return Ok(ScheduleDto.FromEntity(schedule));
    }

    /// <summary>
    /// Disable a schedule
    /// </summary>
    /// <param name="id">The unique identifier of the schedule.</param>
    /// <param name="changeReason">An optional reason for the change, recorded against the change history.</param>
    /// <returns>The updated schedule.</returns>
    [HttpPost("{id:guid}/disable", Name = "DisableSchedule")]
    [ProducesResponseType(typeof(ScheduleDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DisableAsync(Guid id, [FromQuery] string? changeReason = null)
    {
        _logger.LogInformation("Disabling schedule {ScheduleId}", id);

        var schedule = await _application.Scheduler.GetScheduleAsync(id);
        if (schedule == null)
        {
            return NotFound(new ApiErrorResponse { Message = $"Schedule not found: {id}" });
        }

        var (initiatorType, initiatorId, initiatorName) = await GetInitiatorInfoAsync();

        schedule.IsEnabled = false;
        schedule.LastUpdated = DateTime.UtcNow;
        schedule.LastUpdatedByType = initiatorType;
        schedule.LastUpdatedById = initiatorId;
        schedule.LastUpdatedByName = initiatorName;

        await _application.Scheduler.UpdateScheduleAsync(schedule, initiatorType, initiatorId, initiatorName, changeReason);

        _logger.LogInformation("Disabled schedule {ScheduleId}", id);
        return Ok(ScheduleDto.FromEntity(schedule));
    }

    /// <summary>
    /// Run a schedule
    /// </summary>
    /// <param name="id">The unique identifier of the schedule.</param>
    /// <returns>The execution ID and status.</returns>
    [HttpPost("{id:guid}/run", Name = "RunSchedule")]
    [ProducesResponseType(typeof(ScheduleRunResponse), StatusCodes.Status202Accepted)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> RunAsync(Guid id)
    {
        _logger.LogInformation("Manually triggering schedule {ScheduleId}", id);

        var schedule = await _application.Scheduler.GetScheduleWithStepsAsync(id);
        if (schedule == null)
        {
            return NotFound(new ApiErrorResponse { Message = $"Schedule not found: {id}" });
        }

        if (schedule.Steps.Count == 0)
        {
            return BadRequest(new ApiErrorResponse { Message = "Cannot run a schedule with no steps" });
        }

        var (initiatorType, initiatorId, initiatorName) = await GetInitiatorInfoAsync();

        var execution = await _application.Scheduler.StartScheduleExecutionAsync(
            schedule,
            initiatorType,
            initiatorId,
            initiatorName);

        if (execution == null)
        {
            return BadRequest(new ApiErrorResponse { Message = "Failed to start schedule execution. The schedule may already be running." });
        }

        _logger.LogInformation("Started schedule execution {ExecutionId} for schedule {ScheduleId}", execution.Id, id);

        return Accepted(new ScheduleRunResponse
        {
            ExecutionId = execution.Id,
            Message = $"Schedule '{schedule.Name}' started successfully"
        });
    }

    #region Configuration Change History

    /// <summary>
    /// List the change history for a Schedule.
    /// </summary>
    /// <param name="id">The unique identifier of the Schedule.</param>
    /// <param name="pagination">Pagination parameters.</param>
    /// <returns>A paged list of change-history entries, newest version first, each with a one-line summary.</returns>
    /// <response code="200">Change history returned (empty if the Schedule has no recorded configuration changes).</response>
    /// <response code="401">User could not be identified from authentication token.</response>
    [HttpGet("{id:guid}/change-history", Name = "GetScheduleChangeHistory")]
    [ProducesResponseType(typeof(PaginatedResponse<ConfigurationChangeHistoryItem>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetScheduleChangeHistoryAsync(Guid id, [FromQuery] PaginationRequest pagination)
    {
        var result = await _application.ChangeHistory.GetConfigurationChangeHistoryAsync(ActivityTargetType.Schedule, id, pagination.Page, pagination.PageSize);
        return Ok(PaginatedResponse<ConfigurationChangeHistoryItem>.Create(result.Results, result.TotalResults, pagination.Page, pagination.PageSize));
    }

    /// <summary>
    /// Get a single version of a Schedule's change history, with its snapshot and the diff against the previous version.
    /// </summary>
    /// <param name="id">The unique identifier of the Schedule.</param>
    /// <param name="changeVersion">The per-object change version to retrieve.</param>
    /// <returns>The change detail: metadata, the redacted snapshot, and the diff against the previous version.</returns>
    /// <response code="200">The change detail.</response>
    /// <response code="404">No change with that version was found for the Schedule.</response>
    /// <response code="401">User could not be identified from authentication token.</response>
    [HttpGet("{id:guid}/change-history/{changeVersion:int}", Name = "GetScheduleChange")]
    [ProducesResponseType(typeof(ConfigurationChangeDetail), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetScheduleChangeAsync(Guid id, int changeVersion)
    {
        var detail = await _application.ChangeHistory.GetConfigurationChangeAsync(ActivityTargetType.Schedule, id, changeVersion);
        if (detail == null)
            return NotFound(ApiErrorResponse.NotFound($"No change history found for Schedule {id} version {changeVersion}."));
        return Ok(detail);
    }

    /// <summary>
    /// Compare two versions of a Schedule's configuration.
    /// </summary>
    /// <param name="id">The unique identifier of the Schedule.</param>
    /// <param name="fromVersion">The earlier version to compare from.</param>
    /// <param name="toVersion">The later version to compare to.</param>
    /// <returns>The structured diff of the later version against the earlier.</returns>
    /// <response code="200">The diff.</response>
    /// <response code="404">One of the requested versions was not found for the Schedule.</response>
    /// <response code="401">User could not be identified from authentication token.</response>
    [HttpGet("{id:guid}/change-history/compare", Name = "CompareScheduleChanges")]
    [ProducesResponseType(typeof(ConfigurationDiff), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> CompareScheduleChangesAsync(Guid id, [FromQuery] int fromVersion, [FromQuery] int toVersion)
    {
        var diff = await _application.ChangeHistory.CompareConfigurationChangesAsync(ActivityTargetType.Schedule, id, fromVersion, toVersion);
        if (diff == null)
            return NotFound(ApiErrorResponse.NotFound($"Could not compare versions {fromVersion} and {toVersion} for Schedule {id}."));
        return Ok(diff);
    }

    #endregion

    /// <summary>
    /// Validates the schedule request (trigger and steps).
    /// </summary>
    private static string? ValidateScheduleRequest(
        ScheduleTriggerType triggerType,
        string? cronExpression,
        List<ScheduleStepRequest> steps)
    {
        var triggerError = ValidateTrigger(triggerType, cronExpression);
        if (triggerError != null)
        {
            return triggerError;
        }

        // Validate steps
        foreach (var step in steps)
        {
            var stepError = ValidateStep(step);
            if (stepError != null)
            {
                return stepError;
            }
        }

        return null;
    }

    /// <summary>
    /// Validates only the trigger configuration, independent of steps. Used for built-in schedules, whose steps
    /// are JIM-owned and not user-validated.
    /// </summary>
    private static string? ValidateTrigger(ScheduleTriggerType triggerType, string? cronExpression)
    {
        if (triggerType == ScheduleTriggerType.Cron && string.IsNullOrWhiteSpace(cronExpression))
        {
            return "Cron expression is required for scheduled triggers";
        }

        return null;
    }

    /// <summary>
    /// Resolves the failure setting each requested step that updates an existing step asks for, through the one write
    /// rule (<see cref="ScheduleStepFailureWriteRule"/>), judged against the Schedule as stored. New steps are not in the
    /// result; they resolve as new steps when they are created.
    /// </summary>
    private static Dictionary<ScheduleStepRequest, ScheduleStepFailureBehaviour> ResolveStepFailureBehaviours(
        List<ScheduleStepRequest> requestedSteps,
        List<ScheduleStep> existingSteps,
        Schedule storedSchedule)
    {
        var existingById = existingSteps.ToDictionary(s => s.Id);
        return requestedSteps
            .Where(stepRequest => stepRequest.Id.HasValue && existingById.ContainsKey(stepRequest.Id.Value))
            .ToDictionary<ScheduleStepRequest, ScheduleStepRequest, ScheduleStepFailureBehaviour>(
                stepRequest => stepRequest,
                stepRequest => ScheduleStepFailureWriteRule.Resolve(stepRequest, existingById[stepRequest.Id!.Value], storedSchedule),
                ReferenceEqualityComparer.Instance);
    }

    /// <summary>
    /// Determines whether an update request would add, remove or replace any step of a built-in schedule, or change an
    /// existing step's failure setting. The request is considered unchanged only when its steps map exactly onto the
    /// existing step Ids and each resolves to the failure setting it already has.
    /// </summary>
    private static bool BuiltInStepsWouldChange(
        List<ScheduleStep> existingSteps,
        List<ScheduleStepRequest> requestedSteps,
        Dictionary<ScheduleStepRequest, ScheduleStepFailureBehaviour> resolvedOnFailure)
    {
        var existingIds = existingSteps.Select(s => s.Id).ToHashSet();
        var requestedIds = requestedSteps
            .Where(s => s.Id.HasValue && s.Id.Value != Guid.Empty)
            .Select(s => s.Id!.Value)
            .ToHashSet();

        // A requested step with no matching existing Id is an addition or replacement.
        var addsOrReplaces = requestedSteps.Any(s => !s.Id.HasValue || s.Id.Value == Guid.Empty || !existingIds.Contains(s.Id.Value));
        // An existing step absent from the request is a removal.
        var removes = existingIds.Any(existingId => !requestedIds.Contains(existingId));
        // An existing step asked for a failure setting other than the one it has (the Built-in Schedule's steps follow it).
        var existingOnFailure = existingSteps.ToDictionary(s => s.Id, s => s.OnFailure);
        var changesFailureBehaviour = resolvedOnFailure.Any(r => r.Value != existingOnFailure[r.Key.Id!.Value]);

        return addsOrReplaces || removes || changesFailureBehaviour;
    }

    /// <summary>
    /// Reconciles a schedule's steps against the requested set: deletes steps absent from the request, updates
    /// steps present in both, and creates steps new to the request. Not called for built-in schedules, whose
    /// steps are immutable.
    /// </summary>
    private async Task ReconcileScheduleStepsAsync(
        Guid id,
        List<ScheduleStepRequest> requestedSteps,
        List<ScheduleStep> existingSteps,
        Dictionary<ScheduleStepRequest, ScheduleStepFailureBehaviour> resolvedOnFailure,
        ActivityInitiatorType initiatorType,
        Guid? initiatorId,
        string? initiatorName)
    {
        var existingStepIds = existingSteps.Select(s => s.Id).ToHashSet();
        var requestStepIds = requestedSteps
            .Where(s => s.Id.HasValue && s.Id.Value != Guid.Empty)
            .Select(s => s.Id!.Value)
            .ToHashSet();

        // Delete steps not in request
        foreach (var stepToDelete in existingSteps.Where(s => !requestStepIds.Contains(s.Id)))
        {
            await _application.Scheduler.DeleteScheduleStepAsync(stepToDelete);
        }

        // Update or create steps
        foreach (var stepRequest in requestedSteps)
        {
            if (stepRequest.Id.HasValue && existingStepIds.Contains(stepRequest.Id.Value))
            {
                // Update existing step
                var existingStep = await _application.Scheduler.GetScheduleStepAsync(stepRequest.Id.Value);
                if (existingStep != null)
                {
                    existingStep.StepIndex = stepRequest.StepIndex;
                    existingStep.Name = stepRequest.Name;
                    existingStep.ExecutionMode = stepRequest.ExecutionMode;
                    existingStep.StepType = stepRequest.StepType;
                    existingStep.OnFailure = resolvedOnFailure[stepRequest];
                    existingStep.Timeout = stepRequest.TimeoutSeconds.HasValue
                        ? TimeSpan.FromSeconds(stepRequest.TimeoutSeconds.Value)
                        : null;
                    existingStep.ConnectedSystemId = stepRequest.ConnectedSystemId;
                    existingStep.RunProfileId = stepRequest.RunProfileId;
                    existingStep.ScriptPath = stepRequest.ScriptPath;
                    existingStep.Arguments = stepRequest.Arguments;
                    existingStep.ExecutablePath = stepRequest.ExecutablePath;
                    existingStep.WorkingDirectory = stepRequest.WorkingDirectory;
                    existingStep.SqlConnectionString = stepRequest.SqlConnectionString;
                    existingStep.SqlScriptPath = stepRequest.SqlScriptPath;
                    existingStep.LastUpdated = DateTime.UtcNow;
                    existingStep.LastUpdatedByType = initiatorType;
                    existingStep.LastUpdatedById = initiatorId;
                    existingStep.LastUpdatedByName = initiatorName;
                    await _application.Scheduler.UpdateScheduleStepAsync(existingStep);
                }
            }
            else
            {
                // Create new step
                var newStep = stepRequest.ToEntity(id);
                newStep.Created = DateTime.UtcNow;
                newStep.CreatedByType = initiatorType;
                newStep.CreatedById = initiatorId;
                newStep.CreatedByName = initiatorName;
                await _application.Scheduler.CreateScheduleStepAsync(newStep);
            }
        }
    }

    /// <summary>
    /// Validates a step request based on its type.
    /// </summary>
    private static string? ValidateStep(ScheduleStepRequest step)
    {
        return step.StepType switch
        {
            ScheduleStepType.RunProfile => ValidateRunProfileStep(step),
            ScheduleStepType.PowerShell => ValidatePowerShellStep(step),
            ScheduleStepType.Executable => ValidateExecutableStep(step),
            ScheduleStepType.SqlScript => ValidateSqlScriptStep(step),
            _ => $"Unknown step type: {step.StepType}"
        };
    }

    private static string? ValidateRunProfileStep(ScheduleStepRequest step)
    {
        if (!step.ConnectedSystemId.HasValue || step.ConnectedSystemId.Value == 0)
        {
            return "Connected System ID is required for RunProfile steps";
        }
        if (!step.RunProfileId.HasValue || step.RunProfileId.Value == 0)
        {
            return "Run Profile ID is required for RunProfile steps";
        }
        return null;
    }

    private static string? ValidatePowerShellStep(ScheduleStepRequest step)
    {
        if (string.IsNullOrWhiteSpace(step.ScriptPath))
        {
            return "Script path is required for PowerShell steps";
        }
        return null;
    }

    private static string? ValidateExecutableStep(ScheduleStepRequest step)
    {
        if (string.IsNullOrWhiteSpace(step.ExecutablePath))
        {
            return "Executable path is required for Executable steps";
        }
        return null;
    }

    private static string? ValidateSqlScriptStep(ScheduleStepRequest step)
    {
        if (string.IsNullOrWhiteSpace(step.SqlConnectionString))
        {
            return "Connection string is required for SqlScript steps";
        }
        if (string.IsNullOrWhiteSpace(step.SqlScriptPath))
        {
            return "SQL script path is required for SqlScript steps";
        }
        return null;
    }
}
