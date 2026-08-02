// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Net;
using System.Security.Cryptography.X509Certificates;
using JIM.Connectors.SCIM.Authentication;
using JIM.Models.Core;
using JIM.Models.Connectors;
using JIM.Models.Exceptions;
using JIM.Models.Interfaces;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using JIM.Scim;
using JIM.Scim.Messages;
using JIM.Utilities;
using Serilog;
namespace JIM.Connectors.SCIM;

/// <summary>
/// SCIM 2.0 client connector (RFC 7643/7644). JIM acts as the SCIM client: it initiates connections to external
/// SCIM service providers to discover schemas, import resources, and export provisioning changes.
/// Implementation plan: engineering/plans/doing/SCIM_CLIENT_CONNECTOR_DESIGN.md (issue #545).
/// </summary>
public class ScimConnector : IConnector, IConnectorCapabilities, IConnectorSettings, IConnectorSchema, IConnectorImportUsingCalls, IConnectorExportUsingCalls, IConnectorCredentialAware, IConnectorCertificateAware, IConnectorSecureEndpoint
{
    private ICredentialProtection? _credentialProtection;
    private ICertificateProvider? _certificateProvider;

    // Held for the length of an import run: JIM opens the connection once and then asks for pages.
    private ScimHttpClient? _importClient;
    private ScimDiscoveryResult? _importDiscovery;
    private ScimImportPlan? _importPlan;
    private ScimWatermarkTracker? _importWatermark;

    // Held for the length of an export run, on the same basis as the import fields above. The settings are kept
    // because a refused certificate is diagnosed from them, and the export contract does not pass them to ExportAsync.
    private ScimHttpClient? _exportClient;
    private ScimDiscoveryResult? _exportDiscovery;
    private List<ConnectedSystemSettingValue>? _exportSettings;

    #region IConnector members
    public string Name => ConnectorConstants.ScimClientConnectorName;

    public string? Description => "Enables bi-directional synchronisation with any system that exposes a SCIM 2.0 service provider interface. JIM acts as the SCIM client, connecting out to the service provider.";

    public string? Url => "https://github.com/TetronIO/JIM";
    #endregion

    #region IConnectorCapabilities members
    public bool SupportsFullImport => true;
    public bool SupportsDeltaImport => true;
    public bool SupportsExport => true;
    public bool SupportsPartitions => false;
    public bool SupportsPartitionContainers => false;
    public bool SupportsSecondaryExternalId => false;
    public bool SupportsUserSelectedExternalId => false;
    public bool SupportsUserSelectedAttributeTypes => false;
    public bool SupportsAutoConfirmExport => false;
    public bool SupportsParallelExport => true;
    public bool SupportsPaging => true;
    public bool SupportsFilePaths => false;

    /// <summary>
    /// SCIM defines a <c>password</c> attribute on User, but JIM's password channel is a separate contract
    /// (<see cref="IConnectorPasswordManagement"/>) that this Connector does not implement, and a provider's
    /// password policy is not discoverable over SCIM at all. Declaring either as supported would advertise a
    /// capability the rest of JIM would then call into and find missing.
    /// </summary>
    public bool SupportsPasswordSet => false;

    public bool SupportsPasswordPolicyDiscovery => false;

    /// <summary>
    /// The provider's schema is SCIM 2.0 by definition, which is what lets the advisory Standard Mappings
    /// offer Attribute Flow hints against a discovered schema rather than leaving an administrator to
    /// match every attribute by hand.
    /// </summary>
    public AttributeStandard SchemaStandard => AttributeStandard.Scim;
    #endregion

    #region IConnectorSettings members
    public List<ConnectorSetting> GetSettings()
    {
        return new List<ConnectorSetting>
        {
            new() { Name = "SCIM Service Provider", Category = ConnectedSystemSettingCategory.Connectivity, Type = ConnectedSystemSettingType.Heading },
            new() { Name = "SCIM Service Provider Info", Description = "Enter the details of the SCIM 2.0 service provider to connect to.", Category = ConnectedSystemSettingCategory.Connectivity, Type = ConnectedSystemSettingType.Label },
            new() { Name = ScimConnectorConstants.SettingBaseUrl, Required = true, Description = "The base URL of the SCIM 2.0 service provider, i.e. https://example.com/scim/v2. HTTPS is required, except for loopback addresses when testing locally.", Category = ConnectedSystemSettingCategory.Connectivity, Type = ConnectedSystemSettingType.String },

            new() { Name = "Authentication", Category = ConnectedSystemSettingCategory.Connectivity, Type = ConnectedSystemSettingType.Heading },
            new()
            {
                Name = ScimConnectorConstants.SettingAuthenticationMethod,
                Required = true,
                Description = "How to authenticate with the SCIM service provider. OAuth 2.0 Client Credentials is most common for cloud providers; Static Bearer Token suits providers that issue long-lived tokens; Custom Header suits providers with non-standard authentication headers.",
                Category = ConnectedSystemSettingCategory.Connectivity,
                Type = ConnectedSystemSettingType.DropDown,
                DropDownValues = new List<string> { ScimConnectorConstants.AuthMethodOAuthClientCredentials, ScimConnectorConstants.AuthMethodHttpBasic, ScimConnectorConstants.AuthMethodStaticBearerToken, ScimConnectorConstants.AuthMethodCustomHeader },
                DefaultStringValue = ScimConnectorConstants.AuthMethodOAuthClientCredentials
            },

            // OAuth 2.0 Client Credentials settings
            new() { Name = ScimConnectorConstants.SettingTokenEndpointUrl, RequiredWhenSetting = ScimConnectorConstants.SettingAuthenticationMethod, RequiredWhenValue = ScimConnectorConstants.AuthMethodOAuthClientCredentials, Description = "The OAuth 2.0 token endpoint URL used to acquire access tokens via the Client Credentials flow.", Category = ConnectedSystemSettingCategory.Connectivity, Type = ConnectedSystemSettingType.String },
            new() { Name = ScimConnectorConstants.SettingClientId, RequiredWhenSetting = ScimConnectorConstants.SettingAuthenticationMethod, RequiredWhenValue = ScimConnectorConstants.AuthMethodOAuthClientCredentials, Description = "The OAuth 2.0 client identifier.", Category = ConnectedSystemSettingCategory.Connectivity, Type = ConnectedSystemSettingType.String },
            new() { Name = ScimConnectorConstants.SettingClientSecret, RequiredWhenSetting = ScimConnectorConstants.SettingAuthenticationMethod, RequiredWhenValue = ScimConnectorConstants.AuthMethodOAuthClientCredentials, Description = "The OAuth 2.0 client secret.", Category = ConnectedSystemSettingCategory.Connectivity, Type = ConnectedSystemSettingType.StringEncrypted },
            new() { Name = ScimConnectorConstants.SettingOAuthScope, Required = false, Description = "Optional OAuth 2.0 scope(s) to request, space-separated. Only used with the OAuth 2.0 Client Credentials authentication method.", Category = ConnectedSystemSettingCategory.Connectivity, Type = ConnectedSystemSettingType.String },

            // HTTP Basic settings
            new() { Name = ScimConnectorConstants.SettingUsername, RequiredWhenSetting = ScimConnectorConstants.SettingAuthenticationMethod, RequiredWhenValue = ScimConnectorConstants.AuthMethodHttpBasic, Description = "The username to authenticate with when using HTTP Basic authentication.", Category = ConnectedSystemSettingCategory.Connectivity, Type = ConnectedSystemSettingType.String },
            new() { Name = ScimConnectorConstants.SettingPassword, RequiredWhenSetting = ScimConnectorConstants.SettingAuthenticationMethod, RequiredWhenValue = ScimConnectorConstants.AuthMethodHttpBasic, Description = "The password to authenticate with when using HTTP Basic authentication.", Category = ConnectedSystemSettingCategory.Connectivity, Type = ConnectedSystemSettingType.StringEncrypted },

            // Static Bearer Token settings
            new() { Name = ScimConnectorConstants.SettingBearerToken, RequiredWhenSetting = ScimConnectorConstants.SettingAuthenticationMethod, RequiredWhenValue = ScimConnectorConstants.AuthMethodStaticBearerToken, Description = "A pre-generated, long-lived bearer token issued by the SCIM service provider.", Category = ConnectedSystemSettingCategory.Connectivity, Type = ConnectedSystemSettingType.StringEncrypted },

            // Custom Header settings
            new() { Name = ScimConnectorConstants.SettingAuthenticationHeaderName, RequiredWhenSetting = ScimConnectorConstants.SettingAuthenticationMethod, RequiredWhenValue = ScimConnectorConstants.AuthMethodCustomHeader, Description = "The name of the HTTP header the SCIM service provider uses for authentication, i.e. X-Api-Key.", Category = ConnectedSystemSettingCategory.Connectivity, Type = ConnectedSystemSettingType.String },
            new() { Name = ScimConnectorConstants.SettingAuthenticationHeaderValue, RequiredWhenSetting = ScimConnectorConstants.SettingAuthenticationMethod, RequiredWhenValue = ScimConnectorConstants.AuthMethodCustomHeader, Description = "The value to send in the authentication header.", Category = ConnectedSystemSettingCategory.Connectivity, Type = ConnectedSystemSettingType.StringEncrypted },

            new() { Name = "Transport Security", Category = ConnectedSystemSettingCategory.Connectivity, Type = ConnectedSystemSettingType.Heading },
            new() { Name = ScimConnectorConstants.SettingCertificateValidation, Required = false, DefaultStringValue = ScimConnectorConstants.CertValidationFull, Description = "How to validate the service provider's TLS certificate. Full Validation uses the system CA store plus any certificates added in Admin > Certificates. When a connection test fails on trust, JIM shows you the certificate the provider presented so you can add it there: that trusts one specific certificate, and stops trusting it if the provider ever presents a different one. Skip Validation trusts whatever is presented, now and in future, and should be a last resort.", Category = ConnectedSystemSettingCategory.Connectivity, Type = ConnectedSystemSettingType.DropDown, DropDownValues = new List<string> { ScimConnectorConstants.CertValidationFull, ScimConnectorConstants.CertValidationSkip } },
            new() { Name = ScimConnectorConstants.SettingMinimumTlsVersion, Required = false, DefaultStringValue = ScimConnectorConstants.TlsVersion12, Description = "The minimum TLS protocol version to accept when connecting to the SCIM service provider.", Category = ConnectedSystemSettingCategory.Connectivity, Type = ConnectedSystemSettingType.DropDown, DropDownValues = new List<string> { ScimConnectorConstants.TlsVersion12, ScimConnectorConstants.TlsVersion13 } },
            new() { Name = ScimConnectorConstants.SettingConnectionTimeout, Required = true, DefaultIntValue = ScimConnectorConstants.DefaultConnectionTimeoutSeconds, Description = "How long to wait, in seconds, for a response from the SCIM service provider before giving up.", Category = ConnectedSystemSettingCategory.Connectivity, Type = ConnectedSystemSettingType.Integer },

            new() { Name = "Import", Category = ConnectedSystemSettingCategory.General, Type = ConnectedSystemSettingType.Heading },
            new()
            {
                Name = ScimConnectorConstants.SettingPaginationMode,
                Required = false,
                DefaultStringValue = ScimConnectorConstants.PaginationModeAuto,
                Description = "How to page through resources. Auto-detect starts with index-based paging (which every SCIM 2.0 service provider supports) and switches to cursors if the provider offers one. Choose Cursor-based for large or frequently-changing providers: index-based paging can miss or repeat objects when the data changes during an import.",
                Category = ConnectedSystemSettingCategory.General,
                Type = ConnectedSystemSettingType.DropDown,
                DropDownValues = new List<string> { ScimConnectorConstants.PaginationModeAuto, ScimConnectorConstants.PaginationModeIndex, ScimConnectorConstants.PaginationModeCursor }
            },
            new() { Name = ScimConnectorConstants.SettingExcludedAttributes, Required = false, Description = "Optional comma-separated list of SCIM attributes to ask the service provider not to return, i.e. photos, x509Certificates. Useful for large attributes JIM does not need. The id and meta attributes are always requested, because import cannot work without them.", Category = ConnectedSystemSettingCategory.General, Type = ConnectedSystemSettingType.String },
            new()
            {
                Name = ScimConnectorConstants.SettingChangeDetection,
                Required = false,
                DefaultStringValue = ScimConnectorConstants.ChangeDetectionAuto,
                Description = "How a Delta Import finds what changed. SCIM 2.0 has no change feed, so JIM asks the service provider for the resources modified since the last completed import, which needs the provider to support filtering. Auto-detect uses filtering where the provider advertises it; choose Last Modified Filter to use it anyway (some providers support filtering without advertising it), or Full Scan to read every resource every time. Deletions are only detected by a Full Import.",
                Category = ConnectedSystemSettingCategory.General,
                Type = ConnectedSystemSettingType.DropDown,
                DropDownValues = new List<string> { ScimConnectorConstants.ChangeDetectionAuto, ScimConnectorConstants.ChangeDetectionLastModified, ScimConnectorConstants.ChangeDetectionFullScan }
            },

            new() { Name = "Retry Settings", Category = ConnectedSystemSettingCategory.General, Type = ConnectedSystemSettingType.Heading },
            new() { Name = ScimConnectorConstants.SettingMaxRetries, Required = false, DefaultIntValue = ScimConnectorConstants.DefaultMaxRetries, Description = "Maximum number of retry attempts for transient failures (i.e. HTTP 429, 503, 504). Default is 3.", Category = ConnectedSystemSettingCategory.General, Type = ConnectedSystemSettingType.Integer },
            new() { Name = ScimConnectorConstants.SettingRetryDelay, Required = false, DefaultIntValue = ScimConnectorConstants.DefaultRetryDelayMs, Description = "Initial delay between retries in milliseconds. Uses exponential backoff with jitter, and honours Retry-After response headers. Default is 1000ms.", Category = ConnectedSystemSettingCategory.General, Type = ConnectedSystemSettingType.Integer }
        };
    }

    /// <summary>
    /// Validates ScimConnector setting values using custom business logic.
    /// </summary>
    public List<ConnectorSettingValueValidationResult> ValidateSettingValues(List<ConnectedSystemSettingValue> settingValues, ILogger logger)
    {
        logger.Verbose($"ValidateSettingValues() called for {Name}");
        var response = new List<ConnectorSettingValueValidationResult>();

        // generic required, required-group and required-when validation is handled centrally by ConnectorSettingValidator
        // (invoked by the application layer before this method); only SCIM-specific rules live here.

        var baseUrlSetting = settingValues.SingleOrDefault(q => q.Setting.Name == ScimConnectorConstants.SettingBaseUrl);
        var baseUrl = baseUrlSetting?.StringValue;

        // Base URL is required, but the generic validator already reports a missing value; the shape checks below
        // cannot run without one.
        if (baseUrlSetting == null || string.IsNullOrEmpty(baseUrl))
            return response;

        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri))
        {
            response.Add(new ConnectorSettingValueValidationResult
            {
                IsValid = false,
                ErrorMessage = $"Base URL '{baseUrl}' is not a valid absolute URL. Supply the full SCIM endpoint URL, i.e. https://example.com/scim/v2.",
                SettingValue = baseUrlSetting
            });
            return response;
        }

        if (baseUri.Scheme != Uri.UriSchemeHttps && baseUri.Scheme != Uri.UriSchemeHttp)
        {
            response.Add(new ConnectorSettingValueValidationResult
            {
                IsValid = false,
                ErrorMessage = $"Base URL '{baseUrl}' must use the https scheme (or http for loopback addresses only).",
                SettingValue = baseUrlSetting
            });
            return response;
        }

        // JIM is deployed in high-trust environments; identity data must not travel over cleartext HTTP.
        // Loopback is permitted to support local test service providers.
        if (baseUri.Scheme == Uri.UriSchemeHttp && !baseUri.IsLoopback)
        {
            response.Add(new ConnectorSettingValueValidationResult
            {
                IsValid = false,
                ErrorMessage = $"Base URL '{baseUrl}' uses http against a non-loopback host. HTTPS is required for SCIM service providers; http is only permitted for loopback addresses.",
                SettingValue = baseUrlSetting
            });
            return response;
        }

        // Only worth attempting once the URL is well formed, and only then does a failure mean anything.
        var connectivityResult = TestServiceProviderConnectivity(settingValues, baseUrlSetting, logger);
        if (connectivityResult != null)
            response.Add(connectivityResult);

        return response;
    }
    #endregion

    #region IConnectorSchema members
    /// <summary>
    /// Retrieves the schema by querying the service provider's discovery endpoints, falling back to the
    /// core RFC 7643 schemas for anything it does not publish.
    /// </summary>
    public async Task<ConnectorSchema> GetSchemaAsync(List<ConnectedSystemSettingValue> settingValues, ILogger logger)
    {
        using var client = await CreateClientAsync(settingValues, logger);
        var discovery = new ScimConnectorSchema(client, logger);
        var result = await WithCertificateDiagnosisAsync(settingValues, logger, () => discovery.DiscoverAsync(CancellationToken.None));

        // Discovery shortfalls are never absorbed: an administrator has to be able to tell a provider
        // gap from a JIM one when an expected attribute is missing.
        foreach (var warning in result.Warnings)
            logger.Warning("SCIM schema discovery: {Warning}", warning);

        return result.Schema;
    }
    #endregion

    #region IConnectorImportUsingCalls members
    /// <summary>
    /// Opens the connection JIM will read every page of this run through. Discovery is deferred to the
    /// first page, so a run that imports nothing costs no requests.
    /// </summary>
    public void OpenImportConnection(List<ConnectedSystemSettingValue> settingValues, ILogger logger)
    {
        // Task.Run keeps the blocking wait off any caller's synchronisation context; the interface is
        // synchronous but building the client loads JIM's trusted certificates asynchronously.
        _importClient = Task.Run(async () => await CreateClientAsync(settingValues, logger)).GetAwaiter().GetResult();
        _importDiscovery = null;
        _importPlan = null;
        _importWatermark = null;
    }

    /// <summary>
    /// Reads one page of resources. JIM calls this repeatedly until no pagination tokens come back.
    /// </summary>
    public async Task<ConnectedSystemImportResult> ImportAsync(
        ConnectedSystem connectedSystem,
        ConnectedSystemRunProfile runProfile,
        List<ConnectedSystemPaginationToken> paginationTokens,
        string? persistedConnectorData,
        ILogger logger,
        CancellationToken cancellationToken,
        IConnectorProgress progress)
    {
        if (_importClient == null)
            throw new InvalidOperationException("OpenImportConnection must be called before ImportAsync.");

        // Discovery runs once per run, on the first page. Capabilities and endpoints are read fresh each
        // run rather than persisted, so a provider that gained or lost a feature is followed immediately.
        // This is the run's first request, so it is where a refused certificate surfaces.
        _importDiscovery ??= await WithCertificateDiagnosisAsync(connectedSystem.SettingValues, logger,
            () => new ScimConnectorSchema(_importClient, logger).DiscoverAsync(cancellationToken));

        // Likewise decided once and held for the run: every page must ask the same question, or the
        // pages would not add up to one consistent view of what changed.
        var firstPage = _importPlan == null;
        if (firstPage)
        {
            var watermark = ScimImportState.Read(persistedConnectorData, logger)?.Watermark;
            _importPlan = ScimImportPlan.Create(runProfile.RunType, ReadDeltaStrategy(connectedSystem), _importDiscovery.Capabilities, watermark);
            _importWatermark = new ScimWatermarkTracker();

            if (_importPlan.WarningMessage != null)
                logger.Warning("SCIM import: {Warning}", _importPlan.WarningMessage);
        }

        var position = ScimImportPosition.FromTokens(paginationTokens, ReadPaginationMode(connectedSystem));
        var import = new ScimConnectorImport(_importClient, _importDiscovery, connectedSystem, runProfile, _importWatermark!, logger);

        ConnectedSystemImportResult result;
        try
        {
            result = await import.ImportPageAsync(position, _importPlan!.Filter, cancellationToken);
        }
        catch (ScimRequestException ex) when (firstPage && _importPlan!.Strategy == ScimDeltaStrategy.LastModifiedFilter && IsFilterRejected(ex))
        {
            // Retried without the filter rather than failing: the provider advertised filtering, so the
            // administrator has no reason to expect a failed run, and reading everything is still correct.
            logger.Warning(ex, "The SCIM service provider rejected the delta filter it advertises support for. Reading every resource instead.");
            _importPlan = ScimImportPlan.FilterRejected();
            result = await import.ImportPageAsync(position, filter: null, cancellationToken);
        }

        // Reported on the first page, because JIM keeps the first warning a run produces. Where the page
        // has one of its own, both are kept: a fallback to a full scan and an attribute JIM could not
        // hold are separate things an administrator needs to know about.
        if (firstPage && _importPlan.WarningMessage != null)
        {
            result.WarningMessage = result.WarningMessage == null
                ? _importPlan.WarningMessage
                : $"{_importPlan.WarningMessage} {result.WarningMessage}";
            result.WarningErrorType = _importPlan.WarningErrorType;
        }

        return result;
    }

    public void CloseImportConnection()
    {
        _importClient?.Dispose();
        _importClient = null;
        _importDiscovery = null;
        _importPlan = null;
        _importWatermark = null;
    }

    private static ScimPaginationMode ReadPaginationMode(ConnectedSystem connectedSystem)
    {
        var setting = connectedSystem.SettingValues?
            .SingleOrDefault(s => s.Setting.Name == ScimConnectorConstants.SettingPaginationMode)?.StringValue;

        return setting switch
        {
            ScimConnectorConstants.PaginationModeIndex => ScimPaginationMode.Index,
            ScimConnectorConstants.PaginationModeCursor => ScimPaginationMode.Cursor,
            // Auto, unset, or a value from an older configuration: the safe, universally supported style.
            _ => ScimPaginationMode.Auto
        };
    }

    /// <summary>
    /// Whether a failed request is the provider refusing the filter, as opposed to refusing the request.
    /// A 400 naming another SCIM error type is a different problem, and <c>tooMany</c> in particular says
    /// the filter matched too much, which reading everything would only make worse.
    /// </summary>
    private static bool IsFilterRejected(ScimRequestException exception)
    {
        if (exception.StatusCode == HttpStatusCode.NotImplemented)
            return true;

        return exception.StatusCode == HttpStatusCode.BadRequest
               && (exception.ScimType == null || string.Equals(exception.ScimType, ScimErrorTypes.InvalidFilter, StringComparison.OrdinalIgnoreCase));
    }

    private static ScimDeltaStrategy ReadDeltaStrategy(ConnectedSystem connectedSystem)
    {
        var setting = connectedSystem.SettingValues?
            .SingleOrDefault(s => s.Setting.Name == ScimConnectorConstants.SettingChangeDetection)?.StringValue;

        return setting switch
        {
            ScimConnectorConstants.ChangeDetectionLastModified => ScimDeltaStrategy.LastModifiedFilter,
            ScimConnectorConstants.ChangeDetectionFullScan => ScimDeltaStrategy.FullScan,
            // Auto, unset, or a value from an older configuration: follow what the provider advertises.
            _ => ScimDeltaStrategy.Auto
        };
    }
    #endregion

    #region IConnectorExportUsingCalls members
    /// <summary>
    /// Opens the connection every Pending Export in this run is sent through. Discovery is deferred to
    /// the first batch, so a run with nothing to export costs no requests.
    /// </summary>
    public void OpenExportConnection(IList<ConnectedSystemSettingValue> settings)
    {
        // Task.Run keeps the blocking wait off any caller's synchronisation context; the interface is
        // synchronous but building the client loads JIM's trusted certificates asynchronously.
        _exportSettings = settings.ToList();
        _exportClient = Task.Run(async () => await CreateClientAsync(_exportSettings, Log.Logger)).GetAwaiter().GetResult();
        _exportDiscovery = null;
    }

    /// <summary>
    /// Applies a batch of Pending Exports, returning one result per Pending Export in the same order.
    /// </summary>
    public async Task<List<ConnectedSystemExportResult>> ExportAsync(IList<PendingExport> pendingExports, CancellationToken cancellationToken, IConnectorProgress progress)
    {
        if (_exportClient == null)
            throw new InvalidOperationException("OpenExportConnection must be called before ExportAsync.");

        // The provider's schema decides how a value is written and its capabilities decide how a change
        // is sent, so both are read fresh each run rather than persisted; a provider that gained PATCH
        // support since the last run is followed immediately.
        _exportDiscovery ??= await WithCertificateDiagnosisAsync(_exportSettings ?? [], Log.Logger,
            () => new ScimConnectorSchema(_exportClient, Log.Logger).DiscoverAsync(cancellationToken));

        return await new ScimConnectorExport(_exportClient, _exportDiscovery, Log.Logger).ExecuteAsync(pendingExports, cancellationToken);
    }

    public void CloseExportConnection()
    {
        _exportClient?.Dispose();
        _exportClient = null;
        _exportDiscovery = null;
        _exportSettings = null;
    }
    #endregion

    #region IConnectorCredentialAware members
    /// <summary>
    /// Sets the service used to decrypt stored secrets (client secret, password, bearer token,
    /// authentication header value). Null is tolerated: the stored value is then used as-is, matching the
    /// LDAP connector so a value saved before credential protection existed still works.
    /// </summary>
    public void SetCredentialProtection(ICredentialProtection? credentialProtection)
    {
        _credentialProtection = credentialProtection;
    }
    #endregion

    #region IConnectorCertificateAware members
    /// <summary>
    /// Sets the provider of JIM's trusted certificates, which supplement the system CA store when
    /// validating a service provider's TLS certificate.
    /// </summary>
    public void SetCertificateProvider(ICertificateProvider? certificateProvider)
    {
        _certificateProvider = certificateProvider;
    }
    #endregion

    #region private methods
    /// <summary>
    /// Connects to the service provider and confirms it answers on at least one discovery endpoint,
    /// which proves the base URL, the TLS configuration and the credential all work together.
    /// </summary>
    /// <returns>A failed validation result, or null when the provider answered.</returns>
    private ConnectorSettingValueValidationResult? TestServiceProviderConnectivity(
        List<ConnectedSystemSettingValue> settingValues,
        ConnectedSystemSettingValue baseUrlSetting,
        ILogger logger)
    {
        try
        {
            // Task.Run keeps the blocking wait off the caller's synchronisation context. Setting
            // validation is invoked from Blazor Server circuits, which have one, and blocking on it
            // directly would deadlock the circuit rather than time out.
            var reachable = Task.Run(async () =>
            {
                using var client = await CreateClientAsync(settingValues, logger);
                return await new ScimConnectorSchema(client, logger).TestConnectivityAsync(CancellationToken.None);
            }).GetAwaiter().GetResult();

            if (reachable)
                return null;

            return Failure(baseUrlSetting,
                $"Connected to '{baseUrlSetting.StringValue}', but it did not answer on any SCIM discovery endpoint " +
                $"({ScimEndpoints.ServiceProviderConfig}, {ScimEndpoints.ResourceTypes} or {ScimEndpoints.Schemas}). " +
                "Check the Base URL points at the root of the SCIM service, not at a resource endpoint such as /Users.");
        }
        catch (ScimAuthenticationException ex)
        {
            logger.Warning(ex, "The SCIM connectivity test could not authenticate with the service provider.");
            return Failure(baseUrlSetting, $"Could not authenticate with the SCIM service provider: {LogSanitiser.Sanitise(ex.Message)}");
        }
        catch (ScimRequestException ex)
        {
            logger.Warning(ex, "The SCIM connectivity test failed.");
            if (DescribeCertificateRejection(settingValues, ex, logger) is { } rejection)
                return Failure(baseUrlSetting, rejection.Message, rejection);

            return Failure(baseUrlSetting, $"Could not connect to the SCIM service provider: {LogSanitiser.Sanitise(ex.Message)}");
        }
        catch (Exception ex) when (ex is HttpRequestException or TimeoutException or TaskCanceledException or UriFormatException or InvalidOperationException)
        {
            logger.Warning(ex, "The SCIM connectivity test failed.");
            if (DescribeCertificateRejection(settingValues, ex, logger) is { } rejection)
                return Failure(baseUrlSetting, rejection.Message, rejection);

            return Failure(baseUrlSetting, $"Could not connect to the SCIM service provider: {LogSanitiser.Sanitise(ex.Message)}");
        }
    }

    /// <summary>
    /// Runs an operation and, where it fails because of the provider's certificate, replaces the transport's opaque
    /// failure with one carrying the certificate.
    /// </summary>
    /// <remarks>
    /// Wrapped around the first request each path makes rather than around opening the connection, because building
    /// a <see cref="ScimHttpClient"/> connects to nothing: the handshake happens on the first call. Every path that
    /// talks to a provider needs this, not just setting validation. Without it a Run Profile that failed on TLS
    /// reported an opaque transport error and the failed Activity carried no certificate, which is precisely where
    /// an administrator looks after a run fails.
    /// </remarks>
    private async Task<T> WithCertificateDiagnosisAsync<T>(List<ConnectedSystemSettingValue> settingValues, ILogger logger, Func<Task<T>> operation)
    {
        try
        {
            return await operation();
        }
        catch (Exception ex) when (ex is ScimRequestException or HttpRequestException)
        {
            if (DescribeCertificateRejection(settingValues, ex, logger) is { } rejection)
                throw rejection;

            throw;
        }
    }

    /// <summary>
    /// Describes the certificate the service provider presents, when that is what refused the connection.
    /// </summary>
    /// <remarks>
    /// The two steps that cannot be shared live here. Recognising the failure at all: <see cref="HttpClient"/>
    /// reports a refused certificate as "The SSL connection could not be established", with the handshake failure
    /// buried in the inner exceptions, so it has to be walked for. And the administrator's own opt-out: where they
    /// have told JIM not to validate, a failure is not about trust and there is nothing to explain. Everything after
    /// that is <see cref="ServerCertificateDiagnosis"/>, shared with every other TLS connector.
    /// </remarks>
    /// <returns>The rejection to report, or null when the certificate is not what refused the connection.</returns>
    private ServerCertificateRejectedException? DescribeCertificateRejection(List<ConnectedSystemSettingValue> settingValues, Exception exception, ILogger logger)
    {
        if (!ScimCertificateDiagnosis.LooksLikeACertificateFailure(exception))
            return null;

        // Nothing to explain when the administrator has already told JIM not to validate: a failure then
        // is not about trust.
        var validation = settingValues.SingleOrDefault(s => s.Setting.Name == ScimConnectorConstants.SettingCertificateValidation)?.StringValue;
        if (validation == ScimConnectorConstants.CertValidationSkip)
            return null;

        // ProbeCertificate is passed rather than left to the default so a test can supply a diagnostic without
        // standing up a TLS server.
        return ServerCertificateDiagnosis.Describe(this, settingValues, _certificateProvider, exception, logger,
            (endpoint, trusted) => ProbeCertificate(endpoint.Host, endpoint.Port, trusted, endpoint.Timeout, logger));
    }

    /// <summary>
    /// Looks at the certificate the provider presents. Virtual so a test can supply one without standing
    /// up a TLS server.
    /// </summary>
    internal virtual ServerCertificateDiagnostic? ProbeCertificate(
        string host,
        int port,
        IReadOnlyCollection<X509Certificate2> trustedCertificates,
        TimeSpan timeout,
        ILogger logger)
    {
        return ServerCertificateProbe.Probe(host, port, trustedCertificates, timeout, logger,
            ScimCertificateDiagnosis.ServerDescription, ScimCertificateDiagnosis.SecureTransportName);
    }

    #region IConnectorSecureEndpoint members

    /// <summary>
    /// The service provider this system's settings connect to over HTTPS, so JIM can look at the certificate that
    /// provider presents without any caller naming a host of their own.
    /// </summary>
    public SecureEndpoint? ResolveSecureEndpoint(List<ConnectedSystemSettingValue> settingValues)
    {
        var baseUrl = settingValues.SingleOrDefault(s => s.Setting.Name == ScimConnectorConstants.SettingBaseUrl)?.StringValue;
        if (ScimCertificateDiagnosis.ResolveEndpoint(baseUrl) is not { } endpoint)
            return null;

        var timeoutSeconds = settingValues.SingleOrDefault(s => s.Setting.Name == ScimConnectorConstants.SettingConnectionTimeout)?.IntValue
            ?? ScimConnectorConstants.DefaultConnectionTimeoutSeconds;

        return new SecureEndpoint(
            endpoint.Host,
            endpoint.Port,
            TimeSpan.FromSeconds(timeoutSeconds),
            ScimCertificateDiagnosis.ServerDescription,
            ScimCertificateDiagnosis.SecureTransportName);
    }

    #endregion

    private static ConnectorSettingValueValidationResult Failure(ConnectedSystemSettingValue settingValue, string errorMessage, Exception? exception = null)
    {
        return new ConnectorSettingValueValidationResult
        {
            IsValid = false,
            ErrorMessage = errorMessage,
            SettingValue = settingValue,
            Exception = exception
        };
    }

    /// <summary>
    /// Builds a client for the given Connected System, loading JIM's trusted certificates when full
    /// certificate validation is in force.
    /// </summary>
    internal virtual async Task<ScimHttpClient> CreateClientAsync(List<ConnectedSystemSettingValue> settingValues, ILogger logger)
    {
        var trustedCertificates = _certificateProvider != null
            ? await _certificateProvider.GetTrustedCertificatesAsync()
            : [];

        return ScimHttpClientFactory.Create(settingValues, trustedCertificates, _credentialProtection, logger);
    }
    #endregion
}
