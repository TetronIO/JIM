// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using Asp.Versioning;
using JIM.Web.Extensions.Api;
using JIM.Web.Models.Api;
using JIM.Application;
using JIM.Models.Activities;
using JIM.Models.Activities.DTOs;
using JIM.Models.Core.DTOs;
using JIM.Utilities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace JIM.Web.Controllers.Api;

/// <summary>
/// API controller for managing Trusted Certificates in the JIM certificate store.
/// </summary>
/// <remarks>
/// Certificates stored here are used for establishing trust with external systems,
/// such as LDAP servers using TLS/SSL. This controller provides full CRUD operations
/// for certificate management.
/// </remarks>
[Route("api/v{version:apiVersion}/[controller]")]
[ApiController]
[ApiVersion("1.0")]
[Authorize(Roles = "Administrator")]
[Produces("application/json")]
public class CertificatesController(ILogger<CertificatesController> logger, JimApplication application) : ApiControllerBase(application, logger)
{
    private readonly ILogger<CertificatesController> _logger = logger;
    private readonly JimApplication _application = application;

    /// <summary>
    /// List Trusted Certificates
    /// </summary>
    /// <param name="pagination">Pagination parameters (page, pageSize, sortBy, sortDirection, filter).</param>
    /// <returns>A paginated list of certificate headers.</returns>
    [HttpGet(Name = "GetCertificates")]
    [ProducesResponseType(typeof(PaginatedResponse<TrustedCertificateHeader>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetAllAsync([FromQuery] PaginationRequest pagination)
    {
        _logger.LogDebug("Getting all trusted certificates (Page: {Page}, PageSize: {PageSize})", pagination.Page, pagination.PageSize);
        var certificates = await _application.Certificates.GetAllAsync();
        var headers = certificates.Select(TrustedCertificateHeader.FromEntity).AsQueryable();

        // Apply sorting and filtering, then paginate
        var result = headers
            .ApplySortAndFilter(pagination)
            .ToPaginatedResponse(pagination);

        return Ok(result);
    }

    /// <summary>
    /// List enabled Trusted Certificates
    /// </summary>
    /// <returns>A list of enabled certificate headers.</returns>
    [HttpGet("enabled", Name = "GetEnabledCertificates")]
    [ProducesResponseType(typeof(IEnumerable<TrustedCertificateHeader>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetEnabledAsync()
    {
        _logger.LogDebug("Getting enabled trusted certificates");
        var certificates = await _application.Certificates.GetEnabledAsync();
        var headers = certificates.Select(TrustedCertificateHeader.FromEntity);
        return Ok(headers);
    }

    /// <summary>
    /// Get a Trusted Certificate
    /// </summary>
    /// <param name="id">The unique identifier (GUID) of the certificate.</param>
    /// <returns>The certificate details including metadata but not the raw certificate bytes.</returns>
    [HttpGet("{id:guid}", Name = "GetCertificate")]
    [ProducesResponseType(typeof(TrustedCertificateDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetByIdAsync(Guid id)
    {
        _logger.LogDebug("Getting trusted certificate: {Id}", id);
        var certificate = await _application.Certificates.GetByIdAsync(id);
        if (certificate == null)
            return NotFound();

        return Ok(TrustedCertificateDetailDto.FromEntity(certificate));
    }

    /// <summary>
    /// Add a certificate from uploaded data
    /// </summary>
    /// <remarks>
    /// The certificate data should be Base64 encoded. Both PEM and DER formats are supported.
    /// </remarks>
    /// <param name="request">The certificate data and metadata.</param>
    /// <returns>The created certificate details (excluding raw certificate data).</returns>
    [HttpPost("upload", Name = "UploadCertificate")]
    [ProducesResponseType(typeof(TrustedCertificateDetailDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> AddFromDataAsync([FromBody] AddCertificateFromDataRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            return BadRequest(ApiErrorResponse.ValidationError("Name is required"));

        if (string.IsNullOrWhiteSpace(request.CertificateDataBase64))
            return BadRequest(ApiErrorResponse.ValidationError("Certificate data is required"));

        byte[] certificateData;
        try
        {
            certificateData = Convert.FromBase64String(request.CertificateDataBase64);
        }
        catch (FormatException)
        {
            return BadRequest(ApiErrorResponse.ValidationError("Invalid Base64 certificate data"));
        }

        try
        {
            _logger.LogInformation("Adding trusted certificate from uploaded data: {Name}", LogSanitiser.Sanitise(request.Name));
            var apiKey = await GetCurrentApiKeyAsync();
            var certificate = apiKey != null
                ? await _application.Certificates.AddFromDataAsync(request.Name, certificateData, apiKey, request.Notes, request.ChangeReason)
                : await _application.Certificates.AddFromDataAsync(request.Name, certificateData, await GetCurrentUserAsync(), request.Notes, request.ChangeReason);

            return CreatedAtAction(nameof(GetByIdAsync), new { id = certificate.Id }, TrustedCertificateDetailDto.FromEntity(certificate));
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Failed to add certificate: {Message}", ex.Message);
            return BadRequest(ApiErrorResponse.BadRequest(ex.Message));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error adding certificate from data");
            return BadRequest(ApiErrorResponse.BadRequest("Failed to parse certificate data. Ensure it is valid PEM or DER format."));
        }
    }

    /// <summary>
    /// Add a certificate from a file path
    /// </summary>
    /// <remarks>
    /// The file path should be relative to the connector-files volume mount.
    /// Both PEM and DER formats are supported.
    /// </remarks>
    /// <param name="request">The file path and certificate metadata.</param>
    /// <returns>The created certificate details (excluding raw certificate data).</returns>
    [HttpPost("file", Name = "AddCertificateFromFile")]
    [ProducesResponseType(typeof(TrustedCertificateDetailDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> AddFromFileAsync([FromBody] AddCertificateFromFileRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            return BadRequest(ApiErrorResponse.ValidationError("Name is required"));

        if (string.IsNullOrWhiteSpace(request.FilePath))
            return BadRequest(ApiErrorResponse.ValidationError("File path is required"));

        try
        {
            _logger.LogInformation("Adding trusted certificate from file: {Name} ({FilePath})", LogSanitiser.Sanitise(request.Name), LogSanitiser.Sanitise(request.FilePath));
            var apiKey = await GetCurrentApiKeyAsync();
            var certificate = apiKey != null
                ? await _application.Certificates.AddFromFilePathAsync(request.Name, request.FilePath, apiKey, request.Notes, request.ChangeReason)
                : await _application.Certificates.AddFromFilePathAsync(request.Name, request.FilePath, await GetCurrentUserAsync(), request.Notes, request.ChangeReason);

            return CreatedAtAction(nameof(GetByIdAsync), new { id = certificate.Id }, TrustedCertificateDetailDto.FromEntity(certificate));
        }
        catch (FileNotFoundException ex)
        {
            _logger.LogWarning(ex, "Certificate file not found: {FilePath}", LogSanitiser.Sanitise(request.FilePath));
            return NotFound(ApiErrorResponse.NotFound(ex.Message));
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Failed to add certificate: {Message}", ex.Message);
            return BadRequest(ApiErrorResponse.BadRequest(ex.Message));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error adding certificate from file");
            return BadRequest(ApiErrorResponse.BadRequest("Failed to parse certificate file. Ensure it is valid PEM or DER format."));
        }
    }

    /// <summary>
    /// Update a certificate
    /// </summary>
    /// <param name="id">The unique identifier (GUID) of the certificate to update.</param>
    /// <param name="request">The properties to update (all optional).</param>
    /// <returns>204 No Content on success.</returns>
    [HttpPatch("{id:guid}", Name = "UpdateCertificate")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> UpdateAsync(Guid id, [FromBody] UpdateCertificateRequest request)
    {
        var existing = await _application.Certificates.GetByIdAsync(id);
        if (existing == null)
            return NotFound();

        try
        {
            _logger.LogInformation("Updating trusted certificate: {Id}", id);
            var apiKey = await GetCurrentApiKeyAsync();
            if (apiKey != null)
                await _application.Certificates.UpdateAsync(id, apiKey, request.Name, request.Notes, request.IsEnabled, request.ChangeReason);
            else
                await _application.Certificates.UpdateAsync(id, await GetCurrentUserAsync(), request.Name, request.Notes, request.IsEnabled, request.ChangeReason);

            return NoContent();
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Failed to update certificate: {Message}", ex.Message);
            return BadRequest(ApiErrorResponse.BadRequest(ex.Message));
        }
    }

    /// <summary>
    /// Delete a Trusted Certificate
    /// </summary>
    /// <param name="id">The unique identifier (GUID) of the certificate to delete.</param>
    /// <param name="changeReason">Optional reason for the deletion, recorded on the audit Activity.</param>
    /// <returns>204 No Content on success.</returns>
    [HttpDelete("{id:guid}", Name = "DeleteCertificate")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> DeleteAsync(Guid id, [FromQuery] string? changeReason = null)
    {
        var existing = await _application.Certificates.GetByIdAsync(id);
        if (existing == null)
            return NotFound(ApiErrorResponse.NotFound($"Certificate with ID {id} not found."));

        try
        {
            _logger.LogInformation("Deleting trusted certificate: {Id}", id);
            var apiKey = await GetCurrentApiKeyAsync();
            if (apiKey != null)
                await _application.Certificates.DeleteAsync(id, apiKey, changeReason);
            else
                await _application.Certificates.DeleteAsync(id, await GetCurrentUserAsync(), changeReason);
            return NoContent();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting certificate: {Id}", id);
            return BadRequest(ApiErrorResponse.BadRequest(ex.Message));
        }
    }

    /// <summary>
    /// Validate a certificate
    /// </summary>
    /// <remarks>
    /// Checks include expiry date, chain validation, and other certificate properties.
    /// </remarks>
    /// <param name="id">The unique identifier (GUID) of the certificate to validate.</param>
    /// <returns>The validation result including any warnings or errors.</returns>
    [HttpGet("{id:guid}/validate", Name = "ValidateCertificate")]
    [ProducesResponseType(typeof(CertificateValidationResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> ValidateAsync(Guid id)
    {
        try
        {
            _logger.LogDebug("Validating trusted certificate: {Id}", id);
            var result = await _application.Certificates.ValidateAsync(id);
            return Ok(result);
        }
        catch (InvalidOperationException)
        {
            return NotFound();
        }
    }

    /// <summary>
    /// Download certificate data
    /// </summary>
    /// <remarks>
    /// Returns the certificate as a binary file download. The certificate is returned
    /// in DER (Distinguished Encoding Rules) format, which can be converted to PEM
    /// if needed using standard tools like OpenSSL.
    /// </remarks>
    /// <param name="id">The unique identifier (GUID) of the certificate to download.</param>
    /// <returns>The certificate file as application/x-x509-ca-cert.</returns>
    [HttpGet("{id:guid}/download", Name = "DownloadCertificate")]
    [ProducesResponseType(typeof(FileContentResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [Produces("application/x-x509-ca-cert")]
    public async Task<IActionResult> DownloadAsync(Guid id)
    {
        _logger.LogDebug("Downloading certificate data: {Id}", id);
        var certificate = await _application.Certificates.GetByIdAsync(id);

        if (certificate == null)
            return NotFound(ApiErrorResponse.NotFound($"Certificate with ID {id} not found."));

        if (certificate.CertificateData == null || certificate.CertificateData.Length == 0)
            return NotFound(ApiErrorResponse.NotFound("Certificate data is not available."));

        // Create a safe filename from the certificate name
        var safeFileName = string.Join("_", certificate.Name.Split(Path.GetInvalidFileNameChars())) + ".cer";

        return File(certificate.CertificateData, "application/x-x509-ca-cert", safeFileName);
    }

    #region Configuration Change History

    /// <summary>
    /// List the change history for a Trusted Certificate.
    /// </summary>
    /// <param name="id">The unique identifier (GUID) of the certificate.</param>
    /// <param name="pagination">Pagination parameters.</param>
    /// <returns>A paged list of change-history entries, newest version first, each with a one-line summary.</returns>
    /// <response code="200">Change history returned (empty if the certificate has no recorded configuration changes).</response>
    /// <response code="401">User could not be identified from authentication token.</response>
    [HttpGet("{id:guid}/change-history", Name = "GetCertificateChangeHistory")]
    [ProducesResponseType(typeof(PaginatedResponse<ConfigurationChangeHistoryItem>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetCertificateChangeHistoryAsync(Guid id, [FromQuery] PaginationRequest pagination)
    {
        var result = await _application.ChangeHistory.GetConfigurationChangeHistoryAsync(ActivityTargetType.TrustedCertificate, id, pagination.Page, pagination.PageSize);
        return Ok(PaginatedResponse<ConfigurationChangeHistoryItem>.Create(result.Results, result.TotalResults, pagination.Page, pagination.PageSize));
    }

    /// <summary>
    /// Get a single version of a Trusted Certificate's change history, with its snapshot and the diff against the previous version.
    /// </summary>
    /// <param name="id">The unique identifier (GUID) of the certificate.</param>
    /// <param name="changeVersion">The per-object change version to retrieve.</param>
    /// <returns>The change detail: metadata, the snapshot, and the diff against the previous version.</returns>
    /// <response code="200">The change detail.</response>
    /// <response code="404">No change with that version was found for the certificate.</response>
    /// <response code="401">User could not be identified from authentication token.</response>
    [HttpGet("{id:guid}/change-history/{changeVersion:int}", Name = "GetCertificateChange")]
    [ProducesResponseType(typeof(ConfigurationChangeDetail), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetCertificateChangeAsync(Guid id, int changeVersion)
    {
        var detail = await _application.ChangeHistory.GetConfigurationChangeAsync(ActivityTargetType.TrustedCertificate, id, changeVersion);
        if (detail == null)
            return NotFound(ApiErrorResponse.NotFound($"No change history found for Trusted Certificate {id} version {changeVersion}."));
        return Ok(detail);
    }

    /// <summary>
    /// Compare two versions of a Trusted Certificate's configuration.
    /// </summary>
    /// <param name="id">The unique identifier (GUID) of the certificate.</param>
    /// <param name="fromVersion">The earlier version to compare from.</param>
    /// <param name="toVersion">The later version to compare to.</param>
    /// <returns>The structured diff of the later version against the earlier.</returns>
    /// <response code="200">The diff.</response>
    /// <response code="404">One of the requested versions was not found for the certificate.</response>
    /// <response code="401">User could not be identified from authentication token.</response>
    [HttpGet("{id:guid}/change-history/compare", Name = "CompareCertificateChanges")]
    [ProducesResponseType(typeof(ConfigurationDiff), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> CompareCertificateChangesAsync(Guid id, [FromQuery] int fromVersion, [FromQuery] int toVersion)
    {
        var diff = await _application.ChangeHistory.CompareConfigurationChangesAsync(ActivityTargetType.TrustedCertificate, id, fromVersion, toVersion);
        if (diff == null)
            return NotFound(ApiErrorResponse.NotFound($"Could not compare versions {fromVersion} and {toVersion} for Trusted Certificate {id}."));
        return Ok(diff);
    }

    #endregion
}
