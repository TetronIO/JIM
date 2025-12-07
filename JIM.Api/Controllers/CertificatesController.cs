using JIM.Application;
using JIM.Models.Core;
using JIM.Models.Core.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace JIM.Api.Controllers;

/// <summary>
/// API controller for managing trusted certificates in the JIM certificate store.
/// </summary>
[Route("api/[controller]")]
[ApiController]
[Authorize]
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
    /// Gets all trusted certificates.
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(IEnumerable<TrustedCertificateHeader>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAllAsync()
    {
        _logger.LogDebug("Getting all trusted certificates");
        var certificates = await _application.Certificates.GetAllAsync();
        var headers = certificates.Select(TrustedCertificateHeader.FromEntity);
        return Ok(headers);
    }

    /// <summary>
    /// Gets all enabled trusted certificates.
    /// </summary>
    [HttpGet("enabled")]
    [ProducesResponseType(typeof(IEnumerable<TrustedCertificateHeader>), StatusCodes.Status200OK)]
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
    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(TrustedCertificate), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetByIdAsync(Guid id)
    {
        _logger.LogDebug("Getting trusted certificate: {Id}", id);
        var certificate = await _application.Certificates.GetByIdAsync(id);
        if (certificate == null)
            return NotFound();

        return Ok(certificate);
    }

    /// <summary>
    /// Adds a certificate from uploaded data (Base64 encoded PEM or DER).
    /// </summary>
    [HttpPost("upload")]
    [ProducesResponseType(typeof(TrustedCertificate), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> AddFromDataAsync([FromBody] AddCertificateFromDataRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            return BadRequest("Name is required");

        if (string.IsNullOrWhiteSpace(request.CertificateDataBase64))
            return BadRequest("Certificate data is required");

        byte[] certificateData;
        try
        {
            certificateData = Convert.FromBase64String(request.CertificateDataBase64);
        }
        catch (FormatException)
        {
            return BadRequest("Invalid Base64 certificate data");
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
            return BadRequest(ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error adding certificate from data");
            return BadRequest("Failed to parse certificate data. Ensure it is valid PEM or DER format.");
        }
    }

    /// <summary>
    /// Adds a certificate from a file path in the connector-files mount.
    /// </summary>
    [HttpPost("file")]
    [ProducesResponseType(typeof(TrustedCertificate), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> AddFromFileAsync([FromBody] AddCertificateFromFileRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            return BadRequest("Name is required");

        if (string.IsNullOrWhiteSpace(request.FilePath))
            return BadRequest("File path is required");

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
            return BadRequest(ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Failed to add certificate: {Message}", ex.Message);
            return BadRequest(ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error adding certificate from file");
            return BadRequest("Failed to parse certificate file. Ensure it is valid PEM or DER format.");
        }
    }

    /// <summary>
    /// Updates a certificate's editable properties (name, notes, enabled state).
    /// </summary>
    [HttpPatch("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
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
            return BadRequest(ex.Message);
        }
    }

    /// <summary>
    /// Deletes a trusted certificate from the store.
    /// </summary>
    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteAsync(Guid id)
    {
        var existing = await _application.Certificates.GetByIdAsync(id);
        if (existing == null)
            return NotFound();

        try
        {
            _logger.LogInformation("Deleting trusted certificate: {Id}", id);
            await _application.Certificates.DeleteAsync(id);
            return NoContent();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting certificate: {Id}", id);
            return BadRequest(ex.Message);
        }
    }

    /// <summary>
    /// Validates a certificate and returns any issues found.
    /// </summary>
    [HttpGet("{id:guid}/validate")]
    [ProducesResponseType(typeof(CertificateValidationResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
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
