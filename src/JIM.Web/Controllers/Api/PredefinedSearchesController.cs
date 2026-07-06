// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using Asp.Versioning;
using JIM.Application;
using JIM.Models.Activities;
using JIM.Models.Activities.DTOs;
using JIM.Models.Core;
using JIM.Models.Search;
using JIM.Models.Search.DTOs;
using JIM.Utilities;
using JIM.Web.Models.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace JIM.Web.Controllers.Api;

/// <summary>
/// API controller for administering predefined searches.
/// </summary>
/// <remarks>
/// Predefined searches are configured, named searches that drive end-user list views in the portal
/// and the <c>Search-JIMMetaverseObject</c> PowerShell cmdlet. Disabled searches are hidden from
/// the portal and the end-user search API; administrators can still manage them via this controller
/// and the admin UI.
/// </remarks>
[Route("api/v{version:apiVersion}/predefined-searches")]
[ApiController]
[ApiVersion("1.0")]
[Authorize(Roles = "Administrator")]
[Produces("application/json")]
public class PredefinedSearchesController(ILogger<PredefinedSearchesController> logger, JimApplication application) : ApiControllerBase(application, logger)
{
    private readonly ILogger<PredefinedSearchesController> _logger = logger;
    private readonly JimApplication _application = application;

    /// <summary>
    /// List predefined searches.
    /// </summary>
    /// <remarks>
    /// Returns all predefined searches, including those that are currently disabled, so that
    /// administrators can discover their IDs for subsequent update operations.
    /// </remarks>
    /// <returns>All predefined searches as lightweight headers.</returns>
    [HttpGet(Name = "GetPredefinedSearches")]
    [ProducesResponseType(typeof(IList<PredefinedSearchHeader>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetAllAsync()
    {
        _logger.LogTrace("Listing predefined searches");

        var headers = await _application.Search.GetPredefinedSearchHeadersAsync();
        return Ok(headers);
    }

    /// <summary>
    /// Get a predefined search by ID.
    /// </summary>
    /// <remarks>
    /// Returns the full predefined search graph including the displayed attributes and the
    /// criteria-group tree. ID is the canonical identifier; for lookup by the human-readable
    /// slug use <c>GET /by-uri/{uri}</c>.
    /// </remarks>
    /// <param name="id">The unique identifier of the predefined search.</param>
    /// <returns>The predefined search; 404 Not Found if no search has that ID.</returns>
    [HttpGet("{id:int}", Name = "GetPredefinedSearchById")]
    [ProducesResponseType(typeof(PredefinedSearch), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetByIdAsync([FromRoute] int id)
    {
        _logger.LogTrace("Getting predefined search {Id}", id);

        var search = await _application.Search.GetPredefinedSearchAsync(id);
        if (search == null)
            return NotFound(ApiErrorResponse.NotFound($"Predefined search with ID {id} not found."));

        return Ok(search);
    }

    /// <summary>
    /// Get a predefined search by URI.
    /// </summary>
    /// <remarks>
    /// Convenience lookup by the predefined search's stable, human-readable slug (for example
    /// <c>people</c> or <c>security-groups</c>). The canonical identifier is the integer ID;
    /// callers performing subsequent updates should resolve to ID via this endpoint and PATCH by ID.
    /// </remarks>
    /// <param name="uri">The URI slug of the predefined search.</param>
    /// <returns>The predefined search; 404 Not Found if no search has that URI.</returns>
    [HttpGet("by-uri/{uri}", Name = "GetPredefinedSearchByUri")]
    [ProducesResponseType(typeof(PredefinedSearch), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetByUriAsync([FromRoute] string uri)
    {
        if (string.IsNullOrWhiteSpace(uri))
            return BadRequest(ApiErrorResponse.BadRequest("URI must not be empty."));

        _logger.LogTrace("Getting predefined search by URI {Uri}", LogSanitiser.Sanitise(uri));

        var search = await _application.Search.GetPredefinedSearchAsync(uri);
        if (search == null)
            return NotFound(ApiErrorResponse.NotFound($"Predefined search with URI '{uri}' not found."));

        return Ok(search);
    }

    /// <summary>
    /// Update a predefined search.
    /// </summary>
    /// <remarks>
    /// Partial update: only the fields provided in the request body are applied.
    /// </remarks>
    /// <param name="id">The unique identifier of the predefined search.</param>
    /// <param name="request">The fields to update (all optional).</param>
    /// <returns>204 No Content when the search was updated; 404 Not Found if no search has that ID.</returns>
    [HttpPatch("{id:int}", Name = "UpdatePredefinedSearch")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> UpdateAsync([FromRoute] int id, [FromBody] UpdatePredefinedSearchRequest request)
    {
        _logger.LogInformation("Updating predefined search {Id}", id);

        var existing = await _application.Search.GetPredefinedSearchCoreAsync(id);
        if (existing == null)
            return NotFound(ApiErrorResponse.NotFound($"Predefined search with ID {id} not found."));

        if (request.IsEnabled.HasValue)
            existing.IsEnabled = request.IsEnabled.Value;

        var apiKey = await GetCurrentApiKeyAsync();
        if (apiKey != null)
            await _application.Search.UpdatePredefinedSearchAsync(existing, apiKey, request.ChangeReason);
        else
            await _application.Search.UpdatePredefinedSearchAsync(existing, await GetCurrentUserAsync(), request.ChangeReason);

        return NoContent();
    }

    // ─── Criteria groups ───

    /// <summary>
    /// List the criteria groups (and their criteria) for a predefined search.
    /// </summary>
    /// <remarks>
    /// Criteria filter the objects a search returns. A group combines its criteria and child groups with AND
    /// (type All) or OR (type Any); top-level groups are combined with OR, and groups can nest one level deep.
    /// </remarks>
    /// <param name="id">The unique identifier of the predefined search.</param>
    /// <returns>The criteria groups; 404 Not Found if no search has that ID.</returns>
    [HttpGet("{id:int}/criteria-groups", Name = "GetPredefinedSearchCriteriaGroups")]
    [ProducesResponseType(typeof(List<PredefinedSearchCriteriaGroupDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetCriteriaGroupsAsync([FromRoute] int id)
    {
        var search = await _application.Search.GetPredefinedSearchAsync(id);
        if (search == null)
            return NotFound(ApiErrorResponse.NotFound($"Predefined search with ID {id} not found."));

        var groups = search.CriteriaGroups
            .OrderBy(g => g.Position)
            .Select(PredefinedSearchCriteriaGroupDto.FromEntity)
            .ToList();
        return Ok(groups);
    }

    /// <summary>
    /// Add a criteria group to a predefined search.
    /// </summary>
    /// <param name="id">The unique identifier of the predefined search.</param>
    /// <param name="request">The group to create.</param>
    /// <returns>The created group; 404 Not Found if no search has that ID.</returns>
    [HttpPost("{id:int}/criteria-groups", Name = "CreatePredefinedSearchCriteriaGroup")]
    [ProducesResponseType(typeof(PredefinedSearchCriteriaGroupDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> CreateCriteriaGroupAsync([FromRoute] int id, [FromBody] CreatePredefinedSearchCriteriaGroupRequest request)
    {
        _logger.LogInformation("Creating criteria group for predefined search {Id}", id);

        var search = await _application.Search.GetPredefinedSearchCoreAsync(id);
        if (search == null)
            return NotFound(ApiErrorResponse.NotFound($"Predefined search with ID {id} not found."));

        if (!Enum.TryParse<SearchGroupType>(request.Type, true, out var groupType))
            return BadRequest(ApiErrorResponse.BadRequest($"Invalid group type '{request.Type}'. Use 'All' or 'Any'."));

        var apiKey = await GetCurrentApiKeyAsync();
        var created = apiKey != null
            ? await _application.Search.CreatePredefinedSearchCriteriaGroupAsync(id, null, groupType, request.Position, apiKey, request.ChangeReason)
            : await _application.Search.CreatePredefinedSearchCriteriaGroupAsync(id, null, groupType, request.Position, await GetCurrentUserAsync(), request.ChangeReason);

        return CreatedAtRoute("GetPredefinedSearchCriteriaGroups", new { id }, PredefinedSearchCriteriaGroupDto.FromEntity(created));
    }

    /// <summary>
    /// Add a nested child group to an existing criteria group.
    /// </summary>
    /// <remarks>
    /// Child groups let you express mixed logic, for example <c>(A OR B) AND C</c>: a parent group with type
    /// All containing criterion C and a child group with type Any containing A and B.
    /// </remarks>
    /// <param name="id">The unique identifier of the predefined search.</param>
    /// <param name="groupId">The unique identifier of the parent criteria group.</param>
    /// <param name="request">The child group to create.</param>
    /// <returns>The created child group; 404 Not Found if the search or parent group does not exist.</returns>
    [HttpPost("{id:int}/criteria-groups/{groupId:int}/child-groups", Name = "CreatePredefinedSearchChildCriteriaGroup")]
    [ProducesResponseType(typeof(PredefinedSearchCriteriaGroupDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> CreateChildCriteriaGroupAsync([FromRoute] int id, [FromRoute] int groupId, [FromBody] CreatePredefinedSearchCriteriaGroupRequest request)
    {
        _logger.LogInformation("Creating child criteria group under group {GroupId} for predefined search {Id}", groupId, id);

        var search = await _application.Search.GetPredefinedSearchAsync(id);
        if (search == null)
            return NotFound(ApiErrorResponse.NotFound($"Predefined search with ID {id} not found."));

        if (FindCriteriaGroup(search, groupId) == null)
            return NotFound(ApiErrorResponse.NotFound($"Criteria group with ID {groupId} not found on predefined search {id}."));

        if (!Enum.TryParse<SearchGroupType>(request.Type, true, out var groupType))
            return BadRequest(ApiErrorResponse.BadRequest($"Invalid group type '{request.Type}'. Use 'All' or 'Any'."));

        var apiKey = await GetCurrentApiKeyAsync();
        var created = apiKey != null
            ? await _application.Search.CreatePredefinedSearchCriteriaGroupAsync(id, groupId, groupType, request.Position, apiKey, request.ChangeReason)
            : await _application.Search.CreatePredefinedSearchCriteriaGroupAsync(id, groupId, groupType, request.Position, await GetCurrentUserAsync(), request.ChangeReason);

        return CreatedAtRoute("GetPredefinedSearchCriteriaGroups", new { id }, PredefinedSearchCriteriaGroupDto.FromEntity(created));
    }

    /// <summary>
    /// Update a criteria group's logic type or position.
    /// </summary>
    /// <param name="id">The unique identifier of the predefined search.</param>
    /// <param name="groupId">The unique identifier of the criteria group.</param>
    /// <param name="request">The fields to update (all optional).</param>
    /// <returns>The updated group; 404 Not Found if the search or group does not exist.</returns>
    [HttpPut("{id:int}/criteria-groups/{groupId:int}", Name = "UpdatePredefinedSearchCriteriaGroup")]
    [ProducesResponseType(typeof(PredefinedSearchCriteriaGroupDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> UpdateCriteriaGroupAsync([FromRoute] int id, [FromRoute] int groupId, [FromBody] UpdatePredefinedSearchCriteriaGroupRequest request)
    {
        _logger.LogInformation("Updating criteria group {GroupId} for predefined search {Id}", groupId, id);

        var search = await _application.Search.GetPredefinedSearchAsync(id);
        if (search == null)
            return NotFound(ApiErrorResponse.NotFound($"Predefined search with ID {id} not found."));

        var group = FindCriteriaGroup(search, groupId);
        if (group == null)
            return NotFound(ApiErrorResponse.NotFound($"Criteria group with ID {groupId} not found on predefined search {id}."));

        var groupType = group.Type;
        if (request.Type != null && !Enum.TryParse(request.Type, true, out groupType))
            return BadRequest(ApiErrorResponse.BadRequest($"Invalid group type '{request.Type}'. Use 'All' or 'Any'."));

        var position = request.Position ?? group.Position;

        var apiKey = await GetCurrentApiKeyAsync();
        var updated = apiKey != null
            ? await _application.Search.UpdatePredefinedSearchCriteriaGroupAsync(groupId, groupType, position, apiKey, request.ChangeReason)
            : await _application.Search.UpdatePredefinedSearchCriteriaGroupAsync(groupId, groupType, position, await GetCurrentUserAsync(), request.ChangeReason);
        if (updated == null)
            return NotFound(ApiErrorResponse.NotFound($"Criteria group with ID {groupId} not found."));

        return Ok(PredefinedSearchCriteriaGroupDto.FromEntity(updated));
    }

    /// <summary>
    /// Delete a criteria group and its entire subtree (nested groups and contained criteria).
    /// </summary>
    /// <param name="id">The unique identifier of the predefined search.</param>
    /// <param name="groupId">The unique identifier of the criteria group.</param>
    /// <param name="changeReason">Optional reason for the deletion, recorded on the audit Activity.</param>
    /// <returns>No content on success; 404 Not Found if the search or group does not exist.</returns>
    [HttpDelete("{id:int}/criteria-groups/{groupId:int}", Name = "DeletePredefinedSearchCriteriaGroup")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> DeleteCriteriaGroupAsync([FromRoute] int id, [FromRoute] int groupId, [FromQuery] string? changeReason = null)
    {
        _logger.LogInformation("Deleting criteria group {GroupId} from predefined search {Id}", groupId, id);

        var search = await _application.Search.GetPredefinedSearchAsync(id);
        if (search == null)
            return NotFound(ApiErrorResponse.NotFound($"Predefined search with ID {id} not found."));

        if (FindCriteriaGroup(search, groupId) == null)
            return NotFound(ApiErrorResponse.NotFound($"Criteria group with ID {groupId} not found on predefined search {id}."));

        var apiKey = await GetCurrentApiKeyAsync();
        var deleted = apiKey != null
            ? await _application.Search.DeletePredefinedSearchCriteriaGroupAsync(groupId, apiKey, changeReason)
            : await _application.Search.DeletePredefinedSearchCriteriaGroupAsync(groupId, await GetCurrentUserAsync(), changeReason);
        if (!deleted)
            return NotFound(ApiErrorResponse.NotFound($"Criteria group with ID {groupId} not found."));

        return NoContent();
    }

    // ─── Criteria ───

    /// <summary>
    /// Add a criterion to a criteria group.
    /// </summary>
    /// <remarks>
    /// Provide the value carrier that matches the attribute's data type. The attribute must belong to the
    /// predefined search's Metaverse Object Type, and the operator must be applicable to the attribute's data type.
    /// </remarks>
    /// <param name="id">The unique identifier of the predefined search.</param>
    /// <param name="groupId">The unique identifier of the criteria group.</param>
    /// <param name="request">The criterion to create.</param>
    /// <returns>The created criterion; 400 Bad Request on invalid input; 404 Not Found if the search or group does not exist.</returns>
    [HttpPost("{id:int}/criteria-groups/{groupId:int}/criteria", Name = "CreatePredefinedSearchCriterion")]
    [ProducesResponseType(typeof(PredefinedSearchCriteriaDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> CreateCriterionAsync([FromRoute] int id, [FromRoute] int groupId, [FromBody] PredefinedSearchCriterionRequest request)
    {
        _logger.LogInformation("Creating criterion in group {GroupId} for predefined search {Id}", groupId, id);

        var search = await _application.Search.GetPredefinedSearchAsync(id);
        if (search == null)
            return NotFound(ApiErrorResponse.NotFound($"Predefined search with ID {id} not found."));

        if (FindCriteriaGroup(search, groupId) == null)
            return NotFound(ApiErrorResponse.NotFound($"Criteria group with ID {groupId} not found on predefined search {id}."));

        var (attribute, attributeError) = await ResolveCriterionAttributeAsync(search, request.MetaverseAttributeId);
        if (attribute == null)
            return BadRequest(ApiErrorResponse.BadRequest(attributeError!));

        var (criterion, validationError) = BuildCriterion(attribute, request);
        if (criterion == null)
            return BadRequest(ApiErrorResponse.BadRequest(validationError!));

        var apiKey = await GetCurrentApiKeyAsync();
        var created = apiKey != null
            ? await _application.Search.CreatePredefinedSearchCriterionAsync(groupId, criterion, apiKey, request.ChangeReason)
            : await _application.Search.CreatePredefinedSearchCriterionAsync(groupId, criterion, await GetCurrentUserAsync(), request.ChangeReason);
        if (created == null)
            return NotFound(ApiErrorResponse.NotFound($"Criteria group with ID {groupId} not found."));

        created.MetaverseAttribute = attribute;
        return CreatedAtRoute("GetPredefinedSearchCriteriaGroups", new { id }, PredefinedSearchCriteriaDto.FromEntity(created));
    }

    /// <summary>
    /// Update a criterion (full replacement of operator, attribute and value).
    /// </summary>
    /// <param name="id">The unique identifier of the predefined search.</param>
    /// <param name="groupId">The unique identifier of the criteria group.</param>
    /// <param name="criterionId">The unique identifier of the criterion.</param>
    /// <param name="request">The new criterion values.</param>
    /// <returns>The updated criterion; 400 Bad Request on invalid input; 404 Not Found if the search, group or criterion does not exist.</returns>
    [HttpPut("{id:int}/criteria-groups/{groupId:int}/criteria/{criterionId:int}", Name = "UpdatePredefinedSearchCriterion")]
    [ProducesResponseType(typeof(PredefinedSearchCriteriaDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> UpdateCriterionAsync([FromRoute] int id, [FromRoute] int groupId, [FromRoute] int criterionId, [FromBody] PredefinedSearchCriterionRequest request)
    {
        _logger.LogInformation("Updating criterion {CriterionId} in group {GroupId} for predefined search {Id}", criterionId, groupId, id);

        var search = await _application.Search.GetPredefinedSearchAsync(id);
        if (search == null)
            return NotFound(ApiErrorResponse.NotFound($"Predefined search with ID {id} not found."));

        var group = FindCriteriaGroup(search, groupId);
        if (group == null)
            return NotFound(ApiErrorResponse.NotFound($"Criteria group with ID {groupId} not found on predefined search {id}."));

        if (group.Criteria.All(c => c.Id != criterionId))
            return NotFound(ApiErrorResponse.NotFound($"Criterion with ID {criterionId} not found in group {groupId}."));

        var (attribute, attributeError) = await ResolveCriterionAttributeAsync(search, request.MetaverseAttributeId);
        if (attribute == null)
            return BadRequest(ApiErrorResponse.BadRequest(attributeError!));

        var (criterion, validationError) = BuildCriterion(attribute, request);
        if (criterion == null)
            return BadRequest(ApiErrorResponse.BadRequest(validationError!));

        criterion.Id = criterionId;
        var apiKey = await GetCurrentApiKeyAsync();
        var updated = apiKey != null
            ? await _application.Search.UpdatePredefinedSearchCriterionAsync(criterion, apiKey, request.ChangeReason)
            : await _application.Search.UpdatePredefinedSearchCriterionAsync(criterion, await GetCurrentUserAsync(), request.ChangeReason);
        if (updated == null)
            return NotFound(ApiErrorResponse.NotFound($"Criterion with ID {criterionId} not found."));

        updated.MetaverseAttribute = attribute;
        return Ok(PredefinedSearchCriteriaDto.FromEntity(updated));
    }

    /// <summary>
    /// Delete a criterion from a criteria group.
    /// </summary>
    /// <param name="id">The unique identifier of the predefined search.</param>
    /// <param name="groupId">The unique identifier of the criteria group.</param>
    /// <param name="criterionId">The unique identifier of the criterion.</param>
    /// <param name="changeReason">Optional reason for the deletion, recorded on the audit Activity.</param>
    /// <returns>No content on success; 404 Not Found if the search, group or criterion does not exist.</returns>
    [HttpDelete("{id:int}/criteria-groups/{groupId:int}/criteria/{criterionId:int}", Name = "DeletePredefinedSearchCriterion")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> DeleteCriterionAsync([FromRoute] int id, [FromRoute] int groupId, [FromRoute] int criterionId, [FromQuery] string? changeReason = null)
    {
        _logger.LogInformation("Deleting criterion {CriterionId} from group {GroupId} for predefined search {Id}", criterionId, groupId, id);

        var search = await _application.Search.GetPredefinedSearchAsync(id);
        if (search == null)
            return NotFound(ApiErrorResponse.NotFound($"Predefined search with ID {id} not found."));

        var group = FindCriteriaGroup(search, groupId);
        if (group == null)
            return NotFound(ApiErrorResponse.NotFound($"Criteria group with ID {groupId} not found on predefined search {id}."));

        if (group.Criteria.All(c => c.Id != criterionId))
            return NotFound(ApiErrorResponse.NotFound($"Criterion with ID {criterionId} not found in group {groupId}."));

        var apiKey = await GetCurrentApiKeyAsync();
        var deleted = apiKey != null
            ? await _application.Search.DeletePredefinedSearchCriterionAsync(criterionId, apiKey, changeReason)
            : await _application.Search.DeletePredefinedSearchCriterionAsync(criterionId, await GetCurrentUserAsync(), changeReason);
        if (!deleted)
            return NotFound(ApiErrorResponse.NotFound($"Criterion with ID {criterionId} not found."));

        return NoContent();
    }

    #region Configuration Change History

    /// <summary>
    /// List the change history for a Predefined Search.
    /// </summary>
    /// <remarks>
    /// Covers changes to the search's own definition (e.g. enabled/disabled) as well as every criteria-group
    /// and criterion mutation, since those roll up into the same search's configuration change history.
    /// </remarks>
    /// <param name="id">The unique identifier of the predefined search.</param>
    /// <param name="pagination">Pagination parameters.</param>
    /// <returns>A paged list of change-history entries, newest version first, each with a one-line summary.</returns>
    /// <response code="200">Change history returned (empty if the search has no recorded configuration changes).</response>
    /// <response code="401">User could not be identified from authentication token.</response>
    [HttpGet("{id:int}/change-history", Name = "GetPredefinedSearchChangeHistory")]
    [ProducesResponseType(typeof(PaginatedResponse<ConfigurationChangeHistoryItem>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetPredefinedSearchChangeHistoryAsync([FromRoute] int id, [FromQuery] PaginationRequest pagination)
    {
        var result = await _application.ChangeHistory.GetConfigurationChangeHistoryAsync(ActivityTargetType.PredefinedSearch, id, pagination.Page, pagination.PageSize);
        return Ok(PaginatedResponse<ConfigurationChangeHistoryItem>.Create(result.Results, result.TotalResults, pagination.Page, pagination.PageSize));
    }

    /// <summary>
    /// Get a single version of a Predefined Search's change history, with its snapshot and the diff against the previous version.
    /// </summary>
    /// <param name="id">The unique identifier of the predefined search.</param>
    /// <param name="changeVersion">The per-object change version to retrieve.</param>
    /// <returns>The change detail: metadata, the snapshot, and the diff against the previous version.</returns>
    /// <response code="200">The change detail.</response>
    /// <response code="404">No change with that version was found for the Predefined Search.</response>
    /// <response code="401">User could not be identified from authentication token.</response>
    [HttpGet("{id:int}/change-history/{changeVersion:int}", Name = "GetPredefinedSearchChange")]
    [ProducesResponseType(typeof(ConfigurationChangeDetail), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetPredefinedSearchChangeAsync([FromRoute] int id, [FromRoute] int changeVersion)
    {
        var detail = await _application.ChangeHistory.GetConfigurationChangeAsync(ActivityTargetType.PredefinedSearch, id, changeVersion);
        if (detail == null)
            return NotFound(ApiErrorResponse.NotFound($"No change history found for Predefined Search {id} version {changeVersion}."));
        return Ok(detail);
    }

    /// <summary>
    /// Compare two versions of a Predefined Search's configuration.
    /// </summary>
    /// <param name="id">The unique identifier of the predefined search.</param>
    /// <param name="fromVersion">The earlier version to compare from.</param>
    /// <param name="toVersion">The later version to compare to.</param>
    /// <returns>The structured diff of the later version against the earlier.</returns>
    /// <response code="200">The diff.</response>
    /// <response code="404">One of the requested versions was not found for the Predefined Search.</response>
    /// <response code="401">User could not be identified from authentication token.</response>
    [HttpGet("{id:int}/change-history/compare", Name = "ComparePredefinedSearchChanges")]
    [ProducesResponseType(typeof(ConfigurationDiff), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> ComparePredefinedSearchChangesAsync([FromRoute] int id, [FromQuery] int fromVersion, [FromQuery] int toVersion)
    {
        var diff = await _application.ChangeHistory.CompareConfigurationChangesAsync(ActivityTargetType.PredefinedSearch, id, fromVersion, toVersion);
        if (diff == null)
            return NotFound(ApiErrorResponse.NotFound($"Could not compare versions {fromVersion} and {toVersion} for Predefined Search {id}."));
        return Ok(diff);
    }

    #endregion

    // ─── Helpers ───

    /// <summary>
    /// Finds a criteria group by ID within a predefined search's group tree (top-level and nested).
    /// </summary>
    private static PredefinedSearchCriteriaGroup? FindCriteriaGroup(PredefinedSearch search, int groupId)
    {
        return search.CriteriaGroups
            .Select(group => FindCriteriaGroup(group, groupId))
            .FirstOrDefault(match => match != null);
    }

    private static PredefinedSearchCriteriaGroup? FindCriteriaGroup(PredefinedSearchCriteriaGroup group, int groupId)
    {
        if (group.Id == groupId)
            return group;

        return group.ChildGroups
            .Select(child => FindCriteriaGroup(child, groupId))
            .FirstOrDefault(match => match != null);
    }

    /// <summary>
    /// Resolves and validates the Metaverse attribute referenced by a criterion: it must exist and be
    /// associated with the predefined search's Metaverse Object Type.
    /// </summary>
    private async Task<(MetaverseAttribute? attribute, string? error)> ResolveCriterionAttributeAsync(PredefinedSearch search, int metaverseAttributeId)
    {
        var attribute = await _application.Metaverse.GetMetaverseAttributeWithObjectTypesAsync(metaverseAttributeId);
        if (attribute == null)
            return (null, $"Metaverse attribute with ID {metaverseAttributeId} not found.");

        if (attribute.MetaverseObjectTypes == null || attribute.MetaverseObjectTypes.All(t => t.Id != search.MetaverseObjectType.Id))
            return (null, $"Metaverse attribute '{attribute.Name}' is not part of the '{search.MetaverseObjectType.Name}' Metaverse Object Type.");

        return (attribute, null);
    }

    /// <summary>
    /// Builds a validated criterion entity from a request for the given attribute, enforcing that the
    /// comparison operator applies to the attribute's data type and the matching value carrier is provided.
    /// Returns (null, error) on any validation failure.
    /// </summary>
    private static (PredefinedSearchCriteria? criterion, string? error) BuildCriterion(MetaverseAttribute attribute, PredefinedSearchCriterionRequest request)
    {
        if (!Enum.TryParse<SearchComparisonType>(request.ComparisonType, true, out var op) || op == SearchComparisonType.NotSet)
            return (null, $"Invalid comparison type '{request.ComparisonType}'.");

        var criterion = new PredefinedSearchCriteria
        {
            MetaverseAttributeId = attribute.Id,
            ComparisonType = op,
            CaseSensitive = request.CaseSensitive
        };

        // Relative-date validation (rejects relative-on-non-date, missing/negative offset, both-modes-set).
        var relativeError = RelativeDateCriterionValidation.Validate(
            request.ValueMode, request.RelativeCount, request.RelativeUnit, request.RelativeDirection,
            attribute.Type, request.DateTimeValue.HasValue,
            out var valueMode, out var relativeCount, out var relativeUnit, out var relativeDirection);
        if (relativeError != null)
            return (null, relativeError);

        if (valueMode == DateCriteriaValueMode.Relative)
        {
            // Validation has confirmed the attribute is DateTime; only date operators apply.
            if (!SearchComparisonOperators.IsValid(op, attribute.Type))
                return (null, $"Operator '{op}' is not valid for the DateTime attribute '{attribute.Name}'.");
            criterion.ValueMode = DateCriteriaValueMode.Relative;
            criterion.RelativeCount = relativeCount;
            criterion.RelativeUnit = relativeUnit;
            criterion.RelativeDirection = relativeDirection;
            return (criterion, null);
        }

        switch (attribute.Type)
        {
            case AttributeDataType.Text:
                if (!SearchComparisonOperators.IsValid(op, attribute.Type))
                    return (null, $"Operator '{op}' is not valid for the Text attribute '{attribute.Name}'.");
                if (request.StringValue == null)
                    return (null, "StringValue is required for a Text criterion.");
                criterion.StringValue = request.StringValue;
                break;
            case AttributeDataType.Number:
                if (!SearchComparisonOperators.IsValid(op, attribute.Type))
                    return (null, $"Operator '{op}' is not valid for the Number attribute '{attribute.Name}'.");
                if (!request.IntValue.HasValue)
                    return (null, "IntValue is required for a Number criterion.");
                criterion.IntValue = request.IntValue;
                break;
            case AttributeDataType.LongNumber:
                if (!SearchComparisonOperators.IsValid(op, attribute.Type))
                    return (null, $"Operator '{op}' is not valid for the LongNumber attribute '{attribute.Name}'.");
                if (!request.LongValue.HasValue)
                    return (null, "LongValue is required for a LongNumber criterion.");
                criterion.LongValue = request.LongValue;
                break;
            case AttributeDataType.DateTime:
                if (!SearchComparisonOperators.IsValid(op, attribute.Type))
                    return (null, $"Operator '{op}' is not valid for the DateTime attribute '{attribute.Name}'.");
                if (!request.DateTimeValue.HasValue)
                    return (null, "DateTimeValue is required for a DateTime criterion.");
                criterion.DateTimeValue = ToUtc(request.DateTimeValue.Value);
                break;
            case AttributeDataType.Boolean:
                if (!SearchComparisonOperators.IsValid(op, attribute.Type))
                    return (null, $"Operator '{op}' is not valid for the Boolean attribute '{attribute.Name}'.");
                if (!request.BoolValue.HasValue)
                    return (null, "BoolValue is required for a Boolean criterion.");
                criterion.BoolValue = request.BoolValue;
                break;
            case AttributeDataType.Guid:
                if (!SearchComparisonOperators.IsValid(op, attribute.Type))
                    return (null, $"Operator '{op}' is not valid for the Guid attribute '{attribute.Name}'.");
                if (!request.GuidValue.HasValue)
                    return (null, "GuidValue is required for a Guid criterion.");
                criterion.GuidValue = request.GuidValue;
                break;
            default:
                return (null, $"Search criteria are not supported for {attribute.Type} attributes.");
        }

        return (criterion, null);
    }

    /// <summary>
    /// Normalises a DateTime to UTC before persistence (JIM stores DateTime values in UTC).
    /// </summary>
    private static DateTime ToUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
    };
}
