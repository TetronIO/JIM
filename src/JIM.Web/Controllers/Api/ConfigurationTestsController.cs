// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using Asp.Versioning;
using JIM.Application;
using JIM.Models.Expressions;
using JIM.Models.Logic;
using JIM.Web.Extensions.Api;
using JIM.Web.Models.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace JIM.Web.Controllers.Api;

/// <summary>
/// Saved configuration test cases: inputs, and what an Attribute Flow, Generated Value, Scope or Object Matching Rule
/// should give for them, run on demand to catch a change that breaks the expectation.
/// </summary>
/// <remarks>
/// A test case's inputs can hold personal data when they were copied from a real object. They are stored unencrypted,
/// as other configuration is, are never written to a log or an Activity, and Configuration History records only that
/// they changed. A list leaves them out unless it asks for <c>include=inputs</c>.
/// </remarks>
[Route("api/v{version:apiVersion}/configuration-tests")]
[ApiController]
[ApiVersion("1.0")]
[Authorize(Roles = "Administrator")]
[Produces("application/json")]
public class ConfigurationTestsController(ILogger<ConfigurationTestsController> logger, JimApplication application)
    : ApiControllerBase(application, logger)
{
    private const string IncludeInputs = "inputs";

    private readonly ILogger<ConfigurationTestsController> _logger = logger;
    private readonly JimApplication _application = application;

    /// <summary>
    /// List configuration test cases
    /// </summary>
    /// <remarks>
    /// Every test case, those covering one Synchronisation Rule (its Scope, its Attribute Flows and its own Object
    /// Matching Rules), or those under one Connected System (each of its Synchronisation Rules', and its Simple mode
    /// Object Matching Rules'), in id order.
    /// </remarks>
    /// <param name="syncRuleId">Only the cases covering this Synchronisation Rule.</param>
    /// <param name="connectedSystemId">Only the cases under this Connected System.</param>
    /// <param name="include">Give <c>inputs</c> to include each case's inputs, which are left out by default.</param>
    /// <response code="200">The test cases.</response>
    /// <response code="400">Both a Synchronisation Rule and a Connected System were given, or <c>include</c> names
    /// something other than <c>inputs</c>.</response>
    /// <response code="401">Authentication required.</response>
    [HttpGet(Name = "GetConfigurationTestCases")]
    [ProducesResponseType(typeof(List<ConfigurationTestCaseDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetAllAsync([FromQuery] int? syncRuleId, [FromQuery] int? connectedSystemId, [FromQuery] string? include)
    {
        if (syncRuleId.HasValue && connectedSystemId.HasValue)
            return BadRequest(ApiErrorResponse.BadRequest("Give syncRuleId or connectedSystemId, not both."));
        if (include != null && !string.Equals(include, IncludeInputs, StringComparison.OrdinalIgnoreCase))
            return BadRequest(ApiErrorResponse.BadRequest($"include can only be '{IncludeInputs}'."));

        var testCases = await _application.ConfigurationTests.GetTestCasesAsync(syncRuleId, connectedSystemId);
        return Ok(testCases.Select(c => ConfigurationTestCaseDto.FromModel(c, includeInputs: include != null)).ToList());
    }

    /// <summary>
    /// Get a configuration test case
    /// </summary>
    /// <param name="id">The test case's id.</param>
    /// <response code="200">The test case, with its inputs.</response>
    /// <response code="401">Authentication required.</response>
    /// <response code="404">There is no test case with that id.</response>
    [HttpGet("{id:int}", Name = "GetConfigurationTestCase")]
    [ProducesResponseType(typeof(ConfigurationTestCaseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetByIdAsync(int id)
    {
        var testCase = await _application.ConfigurationTests.GetTestCaseAsync(id);
        return testCase == null
            ? NotFound(ApiErrorResponse.NotFound($"Configuration test case with ID {id} not found."))
            : Ok(ConfigurationTestCaseDto.FromModel(testCase, includeInputs: true));
    }

    /// <summary>
    /// Create a configuration test case
    /// </summary>
    /// <remarks>
    /// The case covers exactly one thing: an Attribute Flow (<c>AttributeFlow</c> or <c>GeneratedValue</c>, with
    /// <c>syncRuleMappingId</c>), a Synchronisation Rule's Scope (<c>Scope</c>, with <c>syncRuleId</c>), or an Object
    /// Matching Rule (<c>ObjectMatchingRule</c>, with <c>objectMatchingRuleId</c>). It is deleted with what it
    /// covers, and recorded in that Synchronisation Rule's Configuration History, or the Connected System's for a
    /// Simple mode Object Matching Rule.
    /// </remarks>
    /// <param name="request">The case.</param>
    /// <response code="201">The case was created.</response>
    /// <response code="400">The case cannot be saved as it is; the message says why.</response>
    /// <response code="401">Authentication required.</response>
    [HttpPost(Name = "CreateConfigurationTestCase")]
    [ProducesResponseType(typeof(ConfigurationTestCaseDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> CreateAsync([FromBody] CreateConfigurationTestCaseRequest request)
    {
        var testCase = new ConfigurationTestCase
        {
            Kind = request.Kind!.Value,
            SyncRuleMappingId = request.SyncRuleMappingId,
            SyncRuleId = request.SyncRuleId,
            ObjectMatchingRuleId = request.ObjectMatchingRuleId
        };
        Apply(request, testCase);

        try
        {
            var apiKey = await GetCurrentApiKeyAsync();
            if (apiKey != null)
                await _application.ConfigurationTests.CreateTestCaseAsync(testCase, apiKey);
            else
                await _application.ConfigurationTests.CreateTestCaseAsync(testCase, await GetCurrentUserAsync());
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ApiErrorResponse.BadRequest(ex.Message));
        }

        _logger.LogInformation("Created configuration test case {TestCaseId}", testCase.Id);
        return CreatedAtRoute("GetConfigurationTestCase", new { id = testCase.Id }, ConfigurationTestCaseDto.FromModel(testCase, includeInputs: true));
    }

    /// <summary>
    /// Update a configuration test case
    /// </summary>
    /// <remarks>
    /// Changes the case's name, description, inputs, run-as-of time or expectation; anything the request leaves out
    /// stays as it is. What the case covers and what it tests cannot change; create another case to test something
    /// else.
    /// </remarks>
    /// <param name="id">The test case's id.</param>
    /// <param name="request">The case's new content.</param>
    /// <response code="200">The case was updated.</response>
    /// <response code="400">The case cannot be saved as it is; the message says why.</response>
    /// <response code="401">Authentication required.</response>
    /// <response code="404">There is no test case with that id.</response>
    [HttpPut("{id:int}", Name = "UpdateConfigurationTestCase")]
    [ProducesResponseType(typeof(ConfigurationTestCaseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpdateAsync(int id, [FromBody] UpdateConfigurationTestCaseRequest request)
    {
        var testCase = await _application.ConfigurationTests.GetTestCaseAsync(id);
        if (testCase == null)
            return NotFound(ApiErrorResponse.NotFound($"Configuration test case with ID {id} not found."));
        if (request.ClearRunAsOf && request.RunAsOf.HasValue)
            return BadRequest(ApiErrorResponse.BadRequest("Give runAsOf or clearRunAsOf, not both."));
        Apply(request, testCase);

        try
        {
            var apiKey = await GetCurrentApiKeyAsync();
            if (apiKey != null)
                await _application.ConfigurationTests.UpdateTestCaseAsync(testCase, apiKey);
            else
                await _application.ConfigurationTests.UpdateTestCaseAsync(testCase, await GetCurrentUserAsync());
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ApiErrorResponse.BadRequest(ex.Message));
        }

        _logger.LogInformation("Updated configuration test case {TestCaseId}", id);
        return Ok(ConfigurationTestCaseDto.FromModel(testCase, includeInputs: true));
    }

    /// <summary>
    /// Delete a configuration test case
    /// </summary>
    /// <param name="id">The test case's id.</param>
    /// <response code="204">The case was deleted.</response>
    /// <response code="400">What the case covers no longer exists.</response>
    /// <response code="401">Authentication required.</response>
    /// <response code="404">There is no test case with that id.</response>
    [HttpDelete("{id:int}", Name = "DeleteConfigurationTestCase")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteAsync(int id)
    {
        var testCase = await _application.ConfigurationTests.GetTestCaseAsync(id);
        if (testCase == null)
            return NotFound(ApiErrorResponse.NotFound($"Configuration test case with ID {id} not found."));

        try
        {
            var apiKey = await GetCurrentApiKeyAsync();
            if (apiKey != null)
                await _application.ConfigurationTests.DeleteTestCaseAsync(testCase, apiKey);
            else
                await _application.ConfigurationTests.DeleteTestCaseAsync(testCase, await GetCurrentUserAsync());
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ApiErrorResponse.BadRequest(ex.Message));
        }

        _logger.LogInformation("Deleted configuration test case {TestCaseId}", id);
        return NoContent();
    }

    /// <summary>
    /// Run configuration test cases
    /// </summary>
    /// <remarks>
    /// Runs the cases covering a Synchronisation Rule, those under a Connected System, or those with the given ids,
    /// against the configuration as saved. Each case gives <c>Passed</c>; <c>Failed</c>, with what was expected and
    /// what came; <c>NeedsReview</c>, when the configuration now reads an input the case gives no value for or an input
    /// left the schema; or <c>Error</c>. An Object Matching Rule case compares the rule's match values and never looks
    /// them up. Results are computed for this request and never stored.
    /// </remarks>
    /// <param name="request">Which cases to run: exactly one of <c>syncRuleId</c>, <c>connectedSystemId</c> or
    /// <c>ids</c>.</param>
    /// <response code="200">Each case's result, in id order.</response>
    /// <response code="400">Not exactly one of <c>syncRuleId</c>, <c>connectedSystemId</c> and <c>ids</c> was
    /// given.</response>
    /// <response code="401">Authentication required.</response>
    [HttpPost("run", Name = "RunConfigurationTests")]
    [ProducesResponseType(typeof(List<ConfigurationTestRunResultDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> RunAsync([FromBody] RunConfigurationTestsRequest request)
    {
        var selections = (request.SyncRuleId.HasValue ? 1 : 0) + (request.ConnectedSystemId.HasValue ? 1 : 0) + (request.Ids != null ? 1 : 0);
        if (selections != 1)
            return BadRequest(ApiErrorResponse.BadRequest("Give exactly one of syncRuleId, connectedSystemId or ids."));

        var results = request.SyncRuleId is { } syncRuleId
            ? await _application.ConfigurationTests.RunForSyncRuleAsync(syncRuleId)
            : request.ConnectedSystemId is { } connectedSystemId
                ? await _application.ConfigurationTests.RunForConnectedSystemAsync(connectedSystemId)
                : await _application.ConfigurationTests.RunByIdsAsync(request.Ids!);

        return Ok(results.Select(ConfigurationTestRunResultDto.FromModel).ToList());
    }

    private static void Apply(ConfigurationTestCaseContentRequest request, ConfigurationTestCase testCase)
    {
        testCase.Name = request.Name.Trim();
        testCase.Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim();
        testCase.RunAsOfUtc = AsUtc(request.RunAsOf);
        testCase.Inputs = request.ToInputs();
        testCase.Expectation = request.Expectation!.ToModel();
    }

    /// <summary>
    /// Applies what the update gives, leaving everything it leaves out as it is. Each input side given replaces the
    /// case's inputs on that side only.
    /// </summary>
    private static void Apply(UpdateConfigurationTestCaseRequest request, ConfigurationTestCase testCase)
    {
        if (request.Name != null)
            testCase.Name = request.Name.Trim();
        if (request.Description != null)
            testCase.Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim();
        if (request.ClearRunAsOf)
            testCase.RunAsOfUtc = null;
        else if (request.RunAsOf.HasValue)
            testCase.RunAsOfUtc = AsUtc(request.RunAsOf);

        var inputs = testCase.Inputs.ToList();
        Replace(request.CsAttributes, ExpressionInputSource.ConnectedSystem);
        Replace(request.MvAttributes, ExpressionInputSource.Metaverse);
        Replace(request.Attributes, null);
        testCase.Inputs = inputs;

        if (request.Expectation != null)
            testCase.Expectation = request.Expectation.ToModel();

        void Replace(Dictionary<string, object?>? attributes, ExpressionInputSource? source)
        {
            if (attributes == null)
                return;
            inputs.RemoveAll(input => input.Source == source);
            inputs.AddRange(TestExpressionSampleValues.ToTestCaseInputs(attributes, source));
        }
    }

    private static DateTime? AsUtc(DateTime? instant) => instant is not { } value
        ? null
        : value.Kind == DateTimeKind.Local ? value.ToUniversalTime() : DateTime.SpecifyKind(value, DateTimeKind.Utc);
}
