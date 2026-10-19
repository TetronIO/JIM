// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using Asp.Versioning;
using JIM.Web.Extensions.Api;
using JIM.Web.Models.Api;
using JIM.Application;
using JIM.Models.Activities;
using JIM.Models.Activities.DTOs;
using JIM.Models.Core;
using JIM.Models.Core.DTOs;
using JIM.Models.Security;
using JIM.Utilities;
using JIM.Web.Authentication;
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
public class CertificatesController(ILogger<CertificatesController> logger, JimApplication application, IIdentityProviderTrustMonitor identityProviderTrust)
    : ApiControllerBase(application, logger)
{
    private const CertificateTrustPurpose KnownPurposes = CertificateTrustPurpose.Connectors | CertificateTrustPurpose.SignIn;
    private const string InvalidPurposeMessage = "TrustedFor must be Connectors, SignIn, or both (\"Connectors, SignIn\").";

    private readonly ILogger<CertificatesController> _logger = logger;
    private readonly JimApplication _application = application;
    private readonly IIdentityProviderTrustMonitor _identityProviderTrust = identityProviderTrust;

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
        var headers = certificates.Select(ToHeader).AsQueryable();

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
        var headers = certificates.Select(ToHeader);
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

        return Ok(ToDetail(certificate));
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

        if (!IsValidPurpose(request.TrustedFor))
            return BadRequest(ApiErrorResponse.ValidationError(InvalidPurposeMessage));

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
                ? await _application.Certificates.AddFromDataAsync(request.Name, certificateData, apiKey, request.Notes, request.ChangeReason, request.TrustedFor ?? CertificateTrustPurpose.Connectors)
                : await _application.Certificates.AddFromDataAsync(request.Name, certificateData, await GetCurrentUserAsync(), request.Notes, request.ChangeReason, request.TrustedFor ?? CertificateTrustPurpose.Connectors);

            return CreatedAtRoute("GetCertificate", new { id = certificate.Id }, ToDetail(certificate));
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

        if (!IsValidPurpose(request.TrustedFor))
            return BadRequest(ApiErrorResponse.ValidationError(InvalidPurposeMessage));

        try
        {
            _logger.LogInformation("Adding trusted certificate from file: {Name} ({FilePath})", LogSanitiser.Sanitise(request.Name), LogSanitiser.Sanitise(request.FilePath));
            var apiKey = await GetCurrentApiKeyAsync();
            var certificate = apiKey != null
                ? await _application.Certificates.AddFromFilePathAsync(request.Name, request.FilePath, apiKey, request.Notes, request.ChangeReason, request.TrustedFor ?? CertificateTrustPurpose.Connectors)
                : await _application.Certificates.AddFromFilePathAsync(request.Name, request.FilePath, await GetCurrentUserAsync(), request.Notes, request.ChangeReason, request.TrustedFor ?? CertificateTrustPurpose.Connectors);

            return CreatedAtRoute("GetCertificate", new { id = certificate.Id }, ToDetail(certificate));
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

        if (request.TrustedFor.HasValue && !IsValidPurpose(request.TrustedFor))
            return BadRequest(ApiErrorResponse.ValidationError(InvalidPurposeMessage));

        try
        {
            _logger.LogInformation("Updating trusted certificate: {Id}", id);
            var apiKey = await GetCurrentApiKeyAsync();
            if (apiKey != null)
                await _application.Certificates.UpdateAsync(id, apiKey, request.Name, request.Notes, request.IsEnabled, request.ChangeReason, request.TrustedFor);
            else
                await _application.Certificates.UpdateAsync(id, await GetCurrentUserAsync(), request.Name, request.Notes, request.IsEnabled, request.ChangeReason, request.TrustedFor);

            return NoContent();
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Failed to update certificate: {Message}", ex.Message);
            return BadRequest(ApiErrorResponse.BadRequest(ex.Message));
        }
    }

    /// <summary>
    /// Trust a certificate the identity provider presents
    /// </summary>
    /// <remarks>
    /// JIM connects to its identity provider again, finds the certificate with the given thumbprint in the chain it
    /// presents, and adds it to the JIM certificate store trusted for sign-in; sign-in then works without a restart.
    /// Name the highest certificate presented (<c>suggestedThumbprint</c> in
    /// <c>GET /api/v1/auth/identity-provider-trust</c>): a certificate authority outlasts the certificates it issues.
    ///
    /// Check the thumbprint against the one your certificate authority publishes before trusting it. Nothing is
    /// trusted that the identity provider is not presenting now, or that would not make it trusted.
    ///
    /// Before anyone can sign in, call this with the infrastructure API key.
    /// </remarks>
    /// <param name="request">The thumbprint to trust, and optionally a name and why.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The outcome, the certificate as it now sits in the store, and the identity provider's status afterwards.</returns>
    /// <response code="201">The certificate was added to the JIM certificate store, trusted for sign-in.</response>
    /// <response code="200">The certificate was already in the store: now trusted for sign-in as well (<c>TrustedForSignInAsWell</c>), or already trusted for it (<c>AlreadyTrusted</c>).</response>
    /// <response code="400">No thumbprint was given, the certificate does not name the identity provider's host (<c>NameMismatch</c>), or its chain is invalid however it is anchored (<c>InvalidChain</c>).</response>
    /// <response code="409">The identity provider is not presenting a certificate with that thumbprint now (<c>ThumbprintNotPresented</c>).</response>
    /// <response code="502">The identity provider could not be reached, uses plain HTTP, or none is configured (<c>IdentityProviderUnavailable</c>).</response>
    [HttpPost("identity-provider", Name = "TrustIdentityProviderCertificate")]
    [ProducesResponseType(typeof(TrustIdentityProviderCertificateResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(TrustIdentityProviderCertificateResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(TrustIdentityProviderCertificateResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(TrustIdentityProviderCertificateResponse), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(TrustIdentityProviderCertificateResponse), StatusCodes.Status502BadGateway)]
    public async Task<IActionResult> TrustIdentityProviderCertificateAsync([FromBody] TrustIdentityProviderCertificateRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Thumbprint))
            return BadRequest(ApiErrorResponse.ValidationError("A thumbprint is required. JIM will not trust whatever the identity provider happens to be presenting."));

        _logger.LogInformation("Trusting a certificate the identity provider presents: {Thumbprint}", LogSanitiser.Sanitise(request.Thumbprint));
        var apiKey = await GetCurrentApiKeyAsync();
        var result = apiKey != null
            ? await _application.IdentityProviderTrust.TrustPresentedCertificateAsync(request.Thumbprint, apiKey, request.Name, request.ChangeReason, cancellationToken)
            : await _application.IdentityProviderTrust.TrustPresentedCertificateAsync(request.Thumbprint, await GetCurrentUserAsync(), request.Name, request.ChangeReason, cancellationToken);

        var changed = result.Outcome is IdentityProviderCertificateTrustOutcome.Trusted or IdentityProviderCertificateTrustOutcome.TrustedForSignInAsWell;
        var status = changed ? await _identityProviderTrust.RecheckAsync(cancellationToken) : result.Status;
        var response = new TrustIdentityProviderCertificateResponse
        {
            Outcome = result.Outcome,
            Message = result.Message,
            Certificate = result.Certificate == null ? null : ToDetail(result.Certificate, status),
            Status = status == null ? null : IdentityProviderTrustStatusDto.FromStatus(status, includeStoreDetail: true)
        };

        return result.Outcome switch
        {
            IdentityProviderCertificateTrustOutcome.Trusted => StatusCode(StatusCodes.Status201Created, response),
            IdentityProviderCertificateTrustOutcome.TrustedForSignInAsWell or IdentityProviderCertificateTrustOutcome.AlreadyTrusted => Ok(response),
            IdentityProviderCertificateTrustOutcome.ThumbprintNotPresented => Conflict(response),
            IdentityProviderCertificateTrustOutcome.IdentityProviderUnavailable => StatusCode(StatusCodes.Status502BadGateway, response),
            _ => BadRequest(response)
        };
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
    /// if needed using standard tools like OpenSSL. A certificate added by file path
    /// is read from that file on the JIM server, as connections read it.
    /// </remarks>
    /// <param name="id">The unique identifier (GUID) of the certificate to download.</param>
    /// <returns>The certificate file as application/x-x509-ca-cert.</returns>
    /// <response code="200">The DER-encoded certificate.</response>
    /// <response code="404">No certificate has this ID, or its file could not be read as a certificate.</response>
    [HttpGet("{id:guid}/download", Name = "DownloadCertificate")]
    [ProducesResponseType(typeof(FileContentResult), StatusCodes.Status200OK, "application/x-x509-ca-cert")]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> DownloadAsync(Guid id)
    {
        // No [Produces] for the certificate type here: it would constrain the JSON error responses too, which MVC
        // then refuses with 406 Not Acceptable. A file result sets its own content type.
        _logger.LogDebug("Downloading certificate data: {Id}", id);
        var certificate = await _application.Certificates.GetByIdAsync(id);

        if (certificate == null)
            return NotFound(ApiErrorResponse.NotFound($"Certificate with ID {id} not found."));

        var derData = await _application.Certificates.GetDerEncodedDataAsync(certificate);
        if (derData == null)
        {
            var reason = certificate.SourceType == CertificateSourceType.FilePath
                ? $"Certificate file '{certificate.FilePath}' could not be read as a certificate on the JIM server."
                : "Certificate data could not be read.";
            return NotFound(ApiErrorResponse.NotFound(reason));
        }

        // Create a safe filename from the certificate name
        var safeFileName = string.Join("_", certificate.Name.Split(Path.GetInvalidFileNameChars())) + ".cer";

        return File(derData, "application/x-x509-ca-cert", safeFileName);
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

    private static bool IsValidPurpose(CertificateTrustPurpose? trustedFor) =>
        trustedFor is null || (trustedFor.Value != CertificateTrustPurpose.None && (trustedFor.Value & ~KnownPurposes) == 0);

    private TrustedCertificateHeader ToHeader(TrustedCertificate certificate)
    {
        var header = TrustedCertificateHeader.FromEntity(certificate);
        var status = _identityProviderTrust.Current;
        header.UsedBySignIn = status.IsUsedBySignIn(certificate.Id);
        header.SignInDependsOnIt = status.SignInDependsOn(certificate.Id);
        return header;
    }

    private TrustedCertificateDetailDto ToDetail(TrustedCertificate certificate) => ToDetail(certificate, _identityProviderTrust.Current);

    private static TrustedCertificateDetailDto ToDetail(TrustedCertificate certificate, IdentityProviderTrustStatus? status)
    {
        var detail = TrustedCertificateDetailDto.FromEntity(certificate);
        detail.UsedBySignIn = status?.IsUsedBySignIn(certificate.Id) == true;
        detail.SignInDependsOnIt = status?.SignInDependsOn(certificate.Id) == true;
        return detail;
    }
}
