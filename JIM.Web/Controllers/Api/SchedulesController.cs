using Asp.Versioning;
using JIM.Application;
using JIM.Models.Activities;
using JIM.Models.Scheduling;
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
public class SchedulesController(ILogger<SchedulesController> logger, JimApplication application) : ControllerBase
{
    private readonly ILogger<SchedulesController> _logger = logger;
    private readonly JimApplication _application = application;

    /// <summary>
    /// Gets all schedules with pagination.
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
        _logger.LogTrace("Requested schedules page {Page}, size {PageSize}, search '{Search}'", page, pageSize, search);

        var result = await _application.Scheduler.GetSchedulesAsync(page, pageSize, search, sortBy, sortDescending);
        var dtos = result.Results.Select(ScheduleDto.FromEntity).ToList();

        return Ok(PaginatedResponse<ScheduleDto>.Create(dtos, result.TotalResults, result.CurrentPage, result.PageSize));
    }

    /// <summary>
    /// Gets a specific schedule by ID.
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
    /// Creates a new schedule.
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
        _logger.LogInformation("Creating new schedule: {ScheduleName}", request.Name);

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

        await _application.Scheduler.CreateScheduleAsync(schedule);

        _logger.LogInformation("Created schedule {ScheduleId} with {StepCount} steps", schedule.Id, schedule.Steps.Count);

        // Reload to get navigation properties
        var createdSchedule = await _application.Scheduler.GetScheduleWithStepsAsync(schedule.Id);
        return CreatedAtRoute("GetSchedule", new { id = schedule.Id }, ScheduleDetailDto.FromEntity(createdSchedule!));
    }

    /// <summary>
    /// Updates an existing schedule.
    /// </summary>
    /// <remarks>
    /// Updates the schedule properties and replaces all steps with the provided list.
    /// Steps not included in the request will be deleted.
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

        // Validate request
        var validationError = ValidateScheduleRequest(request.TriggerType, request.CronExpression, request.Steps);
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
        existingSchedule.LastUpdated = DateTime.UtcNow;
        existingSchedule.LastUpdatedByType = initiatorType;
        existingSchedule.LastUpdatedById = initiatorId;
        existingSchedule.LastUpdatedByName = initiatorName;

        await _application.Scheduler.UpdateScheduleAsync(existingSchedule);

        // Handle steps - get existing steps
        var existingSteps = await _application.Scheduler.GetScheduleStepsAsync(id);
        var existingStepIds = existingSteps.Select(s => s.Id).ToHashSet();
        var requestStepIds = request.Steps
            .Where(s => s.Id.HasValue && s.Id.Value != Guid.Empty)
            .Select(s => s.Id!.Value)
            .ToHashSet();

        // Delete steps not in request
        foreach (var stepToDelete in existingSteps.Where(s => !requestStepIds.Contains(s.Id)))
        {
            await _application.Scheduler.DeleteScheduleStepAsync(stepToDelete);
        }

        // Update or create steps
        foreach (var stepRequest in request.Steps)
        {
            if (stepRequest.Id.HasValue && existingStepIds.Contains(stepRequest.Id.Value))
            {
                // Update existing step
                var existingStep = await _application.Scheduler.GetScheduleStepAsync(stepRequest.Id.Value);
                if (existingStep != null)
                {
                    existingStep.StepIndex = stepRequest.StepIndex;
                    existingStep.Name = stepRequest.Name ?? string.Empty;
                    existingStep.ExecutionMode = stepRequest.ExecutionMode;
                    existingStep.StepType = stepRequest.StepType;
                    existingStep.ContinueOnFailure = stepRequest.ContinueOnFailure;
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

        _logger.LogInformation("Updated schedule {ScheduleId}", id);

        // Reload to get updated data
        var updatedSchedule = await _application.Scheduler.GetScheduleWithStepsAsync(id);
        return Ok(ScheduleDetailDto.FromEntity(updatedSchedule!));
    }

    /// <summary>
    /// Deletes a schedule.
    /// </summary>
    /// <param name="id">The unique identifier of the schedule to delete.</param>
    /// <returns>No content on success.</returns>
    [HttpDelete("{id:guid}", Name = "DeleteSchedule")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteAsync(Guid id)
    {
        _logger.LogInformation("Deleting schedule {ScheduleId}", id);

        var existingSchedule = await _application.Scheduler.GetScheduleAsync(id);
        if (existingSchedule == null)
        {
            return NotFound(new ApiErrorResponse { Message = $"Schedule not found: {id}" });
        }

        await _application.Scheduler.DeleteScheduleAsync(existingSchedule);

        _logger.LogInformation("Deleted schedule {ScheduleId}", id);
        return NoContent();
    }

    /// <summary>
    /// Enables a schedule.
    /// </summary>
    /// <param name="id">The unique identifier of the schedule.</param>
    /// <returns>The updated schedule.</returns>
    [HttpPost("{id:guid}/enable", Name = "EnableSchedule")]
    [ProducesResponseType(typeof(ScheduleDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> EnableAsync(Guid id)
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

        await _application.Scheduler.UpdateScheduleAsync(schedule);

        _logger.LogInformation("Enabled schedule {ScheduleId}", id);
        return Ok(ScheduleDto.FromEntity(schedule));
    }

    /// <summary>
    /// Disables a schedule.
    /// </summary>
    /// <param name="id">The unique identifier of the schedule.</param>
    /// <returns>The updated schedule.</returns>
    [HttpPost("{id:guid}/disable", Name = "DisableSchedule")]
    [ProducesResponseType(typeof(ScheduleDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DisableAsync(Guid id)
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

        await _application.Scheduler.UpdateScheduleAsync(schedule);

        _logger.LogInformation("Disabled schedule {ScheduleId}", id);
        return Ok(ScheduleDto.FromEntity(schedule));
    }

    /// <summary>
    /// Manually triggers a schedule execution.
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

    /// <summary>
    /// Validates the schedule request.
    /// </summary>
    private static string? ValidateScheduleRequest(
        ScheduleTriggerType triggerType,
        string? cronExpression,
        List<ScheduleStepRequest> steps)
    {
        if (triggerType == ScheduleTriggerType.Cron && string.IsNullOrWhiteSpace(cronExpression))
        {
            return "Cron expression is required for scheduled triggers";
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
            return "Connected system ID is required for RunProfile steps";
        }
        if (!step.RunProfileId.HasValue || step.RunProfileId.Value == 0)
        {
            return "Run profile ID is required for RunProfile steps";
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

    /// <summary>
    /// Gets the initiator information from the current user context.
    /// </summary>
    private async Task<(ActivityInitiatorType Type, Guid? Id, string? Name)> GetInitiatorInfoAsync()
    {
        // Check for API key authentication
        if (HttpContext.Items.TryGetValue("ApiKeyId", out var apiKeyIdObj) && apiKeyIdObj is Guid apiKeyId)
        {
            var apiKey = await _application.Repository.ApiKeys.GetByIdAsync(apiKeyId);
            return (ActivityInitiatorType.ApiKey, apiKeyId, apiKey?.Name ?? "API Key");
        }

        // User authentication - get name from claims
        var userIdClaim = User.FindFirst("sub") ?? User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier);
        var nameClaim = User.FindFirst("name") ?? User.FindFirst(System.Security.Claims.ClaimTypes.Name);

        if (userIdClaim != null && Guid.TryParse(userIdClaim.Value, out var userId))
        {
            return (ActivityInitiatorType.User, userId, nameClaim?.Value ?? User.Identity?.Name);
        }

        return (ActivityInitiatorType.User, null, User.Identity?.Name);
    }
}
