using JIM.Api.Extensions;
using JIM.Api.Models;
using JIM.Application;
using JIM.Models.Core;
using JIM.Models.Core.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace JIM.Api.Controllers;

/// <summary>
/// API controller for managing trusted certificates in the JIM certificate store.
/// </summary>
/// <remarks>
/// Certificates stored here are used for establishing trust with external systems,
/// such as LDAP servers using TLS/SSL. This controller provides full CRUD operations
/// for certificate management.
/// </remarks>
[Route("api/[controller]")]
[ApiController]
[Authorize]
[Produces("application/json")]
public class CertificatesController : ControllerBase
{
    private readonly ILogger<CertificatesController> _logger;
    private readonly JimApplication _application;

    public CertificatesController(ILogger<CertificatesController> logger, JimApplication application)
    {
        _logger = logger;
        _application = application;
    }

    /// <summary>
    /// Gets all trusted certificates with optional pagination, sorting, and filtering.
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
    /// Gets all enabled trusted certificates.
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
    /// Gets a trusted certificate by ID.
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
    /// Adds a certificate from uploaded data (Base64 encoded PEM or DER).
    /// </summary>
    /// <remarks>
    /// The certificate data should be Base64 encoded. Both PEM and DER formats are supported.
    /// </remarks>
    /// <param name="request">The certificate data and metadata.</param>
    /// <returns>The created certificate.</returns>
    [HttpPost("upload", Name = "UploadCertificate")]
    [ProducesResponseType(typeof(TrustedCertificate), StatusCodes.Status201Created)]
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
            _logger.LogInformation("Adding trusted certificate from uploaded data: {Name}", request.Name);
            var certificate = await _application.Certificates.AddFromDataAsync(
                request.Name,
                certificateData,
                notes: request.Notes);

            return CreatedAtAction(nameof(GetByIdAsync), new { id = certificate.Id }, certificate);
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
    /// Adds a certificate from a file path in the connector-files mount.
    /// </summary>
    /// <remarks>
    /// The file path should be relative to the connector-files volume mount.
    /// Both PEM and DER formats are supported.
    /// </remarks>
    /// <param name="request">The file path and certificate metadata.</param>
    /// <returns>The created certificate.</returns>
    [HttpPost("file", Name = "AddCertificateFromFile")]
    [ProducesResponseType(typeof(TrustedCertificate), StatusCodes.Status201Created)]
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
            _logger.LogInformation("Adding trusted certificate from file: {Name} ({FilePath})", request.Name, request.FilePath);
            var certificate = await _application.Certificates.AddFromFilePathAsync(
                request.Name,
                request.FilePath,
                notes: request.Notes);

            return CreatedAtAction(nameof(GetByIdAsync), new { id = certificate.Id }, certificate);
        }
        catch (FileNotFoundException ex)
        {
            _logger.LogWarning(ex, "Certificate file not found: {FilePath}", request.FilePath);
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
    /// Updates a certificate's editable properties (name, notes, enabled state).
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
            await _application.Certificates.UpdateAsync(
                id,
                name: request.Name,
                notes: request.Notes,
                isEnabled: request.IsEnabled);

            return NoContent();
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Failed to update certificate: {Message}", ex.Message);
            return BadRequest(ApiErrorResponse.BadRequest(ex.Message));
        }
    }

    /// <summary>
    /// Deletes a trusted certificate from the store.
    /// </summary>
    /// <param name="id">The unique identifier (GUID) of the certificate to delete.</param>
    /// <returns>204 No Content on success.</returns>
    [HttpDelete("{id:guid}", Name = "DeleteCertificate")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> DeleteAsync(Guid id)
    {
        var existing = await _application.Certificates.GetByIdAsync(id);
        if (existing == null)
            return NotFound(ApiErrorResponse.NotFound($"Certificate with ID {id} not found."));

        try
        {
            _logger.LogInformation("Deleting trusted certificate: {Id}", id);
            await _application.Certificates.DeleteAsync(id);
            return NoContent();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting certificate: {Id}", id);
            return BadRequest(ApiErrorResponse.BadRequest(ex.Message));
        }
    }

    /// <summary>
    /// Validates a certificate and returns any issues found.
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
}
