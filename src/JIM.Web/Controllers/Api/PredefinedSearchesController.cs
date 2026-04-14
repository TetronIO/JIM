// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using Asp.Versioning;
using JIM.Application;
using JIM.Models.Search.DTOs;
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
public class PredefinedSearchesController(ILogger<PredefinedSearchesController> logger, JimApplication application) : ControllerBase
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

        await _application.Search.UpdatePredefinedSearchAsync(existing);
        return NoContent();
    }
}
