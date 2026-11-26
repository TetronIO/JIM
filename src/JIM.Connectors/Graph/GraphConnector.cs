// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Security.Cryptography.X509Certificates;
using JIM.Connectors.Http;
using JIM.Connectors.Resources;
using JIM.Models.Localisation;
using JIM.Models.Connectors;
using JIM.Models.Exceptions;
using JIM.Models.Interfaces;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using JIM.Utilities;
using Serilog;

namespace JIM.Connectors.Graph;

/// <summary>
/// Microsoft Graph Connector: synchronises users and groups with a Microsoft Entra ID tenant through the Microsoft
/// Graph API, authenticating as the customer's own app registration (client credentials, app-only).
/// Implementation plan: engineering/plans/doing/MICROSOFT_GRAPH_CONNECTOR_DESIGN.md (issue #361).
/// </summary>
/// <remarks>
/// Seeded as a built-in Connector. Register-JIMEntraIdConnector sets up the app registration it authenticates as.
/// </remarks>
public class GraphConnector : IConnector, IConnectorCapabilities, IConnectorSettings, IConnectorCredentialAware, IConnectorCertificateAware, IConnectorSchema, IConnectorImportUsingCalls, IConnectorImportWatermark, IConnectorExportUsingCalls, IConnectorPasswordManagement, IConnectorPasswordPolicyDiscovery, IConnectorPhases, IConnectorSecureEndpoint
{
    private static readonly string[] MultiTenantAliases = ["common", "organizations", "consumers"];

    private ICredentialProtection? _credentialProtection;
    private ICertificateProvider? _certificateProvider;

    // Held for the length of an import run: JIM opens the connection once and then asks for pages.
    private GraphHttpClient? _importClient;
    private GraphConnectorImport? _import;

    // Held while the Password Delivery Service delivers to this Connected System.
    private GraphHttpClient? _passwordClient;
    private GraphConnectorPasswords? _passwords;

    // Held for the length of an export run.
    private GraphHttpClient? _exportClient;
    private GraphConnectorExport? _export;

    #region IConnector members
    public string Identifier => ConnectorConstants.GraphConnectorIdentifier;

    public string Name => ConnectorConstants.GraphConnectorName;

    public string? Description => "Enables bi-directional synchronisation of users and groups with Microsoft Entra ID, through the Microsoft Graph API.";

    public string? Url => "https://github.com/TetronIO/JIM";
    #endregion

    #region IConnectorCapabilities members
    public bool SupportsFullImport => true;

    /// <summary>Graph delta queries; the delta link is the watermark.</summary>
    public bool SupportsDeltaImport => true;

    public bool SupportsExport => true;

    /// <summary>A tenant has no partitions.</summary>
    public bool SupportsPartitions => false;

    public bool SupportsPartitionContainers => false;
    public bool SupportsSecondaryExternalId => false;

    /// <summary>The External ID is the object's <c>id</c>, which Entra ID assigns and never changes.</summary>
    public bool SupportsUserSelectedExternalId => false;

    public bool SupportsUserSelectedAttributeTypes => false;
    public bool SupportsUserSelectedAttributePlurality => false;

    /// <summary>Graph is eventually consistent, so only the confirming import proves a write landed.</summary>
    public bool SupportsAutoConfirmExport => false;

    /// <summary>Throughput comes from <c>$batch</c> and pacing; parallel export would only race the write quota.</summary>
    public bool SupportsParallelExport => false;

    public bool SupportsPaging => true;
    public bool SupportsFilePaths => false;
    /// <summary>JIM's Initial Password, Set Password and Password Synchronisation reach Entra ID through the password channel.</summary>
    public bool SupportsPasswordSet => true;

    /// <summary>Entra ID's password rules, which are the same in every tenant.</summary>
    public bool SupportsPasswordPolicyDiscovery => true;
    public bool SupportsUniquenessProbe => false;
    public bool SupportsUniquenessRejectionClassification => false;

    /// <summary>Disables a user by setting accountEnabled to false. A group cannot be disabled.</summary>
    public bool SupportsDisable => true;

    /// <summary>A directory object's <c>id</c> is unique across the tenant, whether the object is a user or a group.</summary>
    public bool ExternalIdsUniqueAcrossObjectTypes => true;
    #endregion

    #region IConnectorSchema members
    /// <summary>
    /// The curated user and group schema, with the tenant's directory extensions added. Discovery shortfalls travel on
    /// the schema's warnings so the refresh can show them, and are logged for the operator too.
    /// </summary>
    public async Task<ConnectorSchema> GetSchemaAsync(List<ConnectedSystemSettingValue> settings, ILogger logger)
    {
        using var client = await CreateClientAsync(settings, logger);
        var schema = await new GraphConnectorSchema(client, logger).GetSchemaAsync(CancellationToken.None);
        foreach (var warning in schema.Warnings)
            logger.Warning("Microsoft Graph schema: {Warning}", warning);

        return schema;
    }
    #endregion

    #region IConnectorImportWatermark members
    /// <summary>
    /// The watermark is each Object Type's delta link, which Graph keeps opaque, so it is described by when it was taken:
    /// the start of the import that took it.
    /// </summary>
    public ImportWatermarkDescription? DescribeImportWatermark(string? persistedConnectorData, ILogger logger)
    {
        var state = GraphImportState.Read(persistedConnectorData, logger);
        return state is not { DeltaLinks.Count: > 0 }
            ? null
            : new ImportWatermarkDescription
            {
                Kind = "delta link",
                Points = state.DeltaLinks.Keys.Order(StringComparer.OrdinalIgnoreCase)
                    .Select(objectType => new ImportWatermarkPoint { Scope = objectType, Time = state.CapturedAt.UtcDateTime })
                    .ToList()
            };
    }

    /// <summary>
    /// Everything this Connector persists (<see cref="GraphImportState"/>) is the watermark or what it was taken to track,
    /// so nothing remains once it is removed.
    /// </summary>
    public string? RemoveImportWatermark(string persistedConnectorData, ILogger logger) => null;
    #endregion

    #region IConnectorPasswordManagement members
    /// <summary>Entra ID honours a forced change and "never expires" alike, and both together.</summary>
    public IReadOnlyCollection<PasswordExpiryBehaviour> SupportedExpiryBehaviours { get; } =
        [PasswordExpiryBehaviour.RequireChangeAtNextSignIn, PasswordExpiryBehaviour.ExpiresAccordingToTargetPolicy, PasswordExpiryBehaviour.NeverExpires];

    /// <summary>Microsoft Graph is HTTPS with TLS 1.2 or later; the client refuses anything else.</summary>
    public bool IsPasswordChannelSecure => true;

    public void OpenPasswordConnection(IList<ConnectedSystemSettingValue> settings)
    {
        var logger = Log.ForContext<GraphConnector>();
        var settingValues = settings.ToList();
        _passwordClient = Task.Run(async () => await CreateClientAsync(settingValues, logger)).GetAwaiter().GetResult();
        _passwords = new GraphConnectorPasswords(_passwordClient, logger);
    }

    public Task<PasswordSetResult> SetPasswordAsync(ConnectedSystemObject target, string password, PasswordSetOptions options, CancellationToken cancellationToken)
    {
        if (_passwords == null)
            throw new InvalidOperationException("OpenPasswordConnection must be called before SetPasswordAsync.");

        return _passwords.SetPasswordAsync(target, password, options, cancellationToken);
    }

    public void ClosePasswordConnection()
    {
        _passwordClient?.Dispose();
        _passwordClient = null;
        _passwords = null;
    }

    public Task<PasswordPreflightResult> RunPasswordPreflightAsync(List<ConnectedSystemSettingValue> settings, IReadOnlyList<string> containerExternalIds, ILogger logger, CancellationToken cancellationToken) =>
        GraphConnectorPasswords.PreflightAsync(() => CreateClientAsync(settings, logger), cancellationToken);
    #endregion

    #region IConnectorPasswordPolicyDiscovery members
    /// <summary>Entra ID's rules for a cloud user's password, which every tenant applies alike, so nothing is read.</summary>
    public Task<ConnectedSystemPasswordPolicy?> GetPasswordPolicyAsync(List<ConnectedSystemSettingValue> settings, ILogger logger) =>
        Task.FromResult<ConnectedSystemPasswordPolicy?>(GraphConnectorPasswords.Policy());
    #endregion

    #region IConnectorExportUsingCalls members
    /// <summary>Opens the connection every batch of this export run is written through. Nothing is sent until the first batch.</summary>
    public void OpenExportConnection(IList<ConnectedSystemSettingValue> settings, string? persistedConnectorData)
    {
        var logger = Log.ForContext<GraphConnector>();
        var settingValues = settings.ToList();
        _exportClient = Task.Run(async () => await CreateClientAsync(settingValues, logger)).GetAwaiter().GetResult();
        _export = new GraphConnectorExport(_exportClient, logger);
    }

    /// <summary>Writes a batch of Pending Exports, returning one result for each, in the same order.</summary>
    public Task<List<ConnectedSystemExportResult>> ExportAsync(IList<PendingExport> pendingExports, CancellationToken cancellationToken, IConnectorProgress progress)
    {
        if (_export == null)
            throw new InvalidOperationException("OpenExportConnection must be called before ExportAsync.");

        return _export.ExportAsync(pendingExports, cancellationToken);
    }

    /// <returns>Always null: an export changes nothing the Connector persists.</returns>
    public string? CloseExportConnection()
    {
        _exportClient?.Dispose();
        _exportClient = null;
        _export = null;
        return null;
    }
    #endregion

    #region IConnectorImportUsingCalls members
    /// <summary>
    /// Opens the connection every page of this run is read through. Nothing is sent until the first page.
    /// </summary>
    /// <param name="persistedConnectorData">Unused here: <see cref="ImportAsync"/> receives it directly.</param>
    public void OpenImportConnection(List<ConnectedSystemSettingValue> settingValues, string? persistedConnectorData, ILogger logger)
    {
        // Task.Run keeps the blocking wait off any caller's synchronisation context; the interface is synchronous but
        // building the client loads JIM's trusted certificates asynchronously.
        _importClient = Task.Run(async () => await CreateClientAsync(settingValues, logger)).GetAwaiter().GetResult();
        _import = new GraphConnectorImport(_importClient, logger);
    }

    /// <summary>Reads one page. JIM calls this repeatedly until no pagination tokens come back.</summary>
    public Task<ConnectedSystemImportResult> ImportAsync(
        ConnectedSystem connectedSystem,
        ConnectedSystemRunProfile runProfile,
        List<ConnectedSystemPaginationToken> paginationTokens,
        string? persistedConnectorData,
        ILogger logger,
        CancellationToken cancellationToken,
        IConnectorProgress progress)
    {
        if (_import == null)
            throw new InvalidOperationException("OpenImportConnection must be called before ImportAsync.");

        return _import.ImportPageAsync(connectedSystem, runProfile, persistedConnectorData, GraphImportPosition.FromTokens(paginationTokens), progress, cancellationToken);
    }

    /// <returns>Always null: the delta links travel on the last page's result, and nothing else is kept at close.</returns>
    public string? CloseImportConnection()
    {
        _importClient?.Dispose();
        _importClient = null;
        _import = null;
        return null;
    }
    #endregion

    #region IConnectorPhases members
    /// <summary>
    /// An import prepares each Object Type (the delta baseline, and which synchronised objects are cloud-managed) and then
    /// fetches it, both round trips to a service that may be slow or throttling. Export declares nothing: JIM already
    /// reports accurate per-batch counts around the call.
    /// </summary>
    public IReadOnlyList<ConnectorPhase> GetPhases(ConnectedSystem connectedSystem, ConnectedSystemRunProfile runProfile) => runProfile.RunType switch
    {
        ConnectedSystemRunType.FullImport or ConnectedSystemRunType.DeltaImport =>
        [
            new ConnectorPhase(GraphConnectorPhases.Prepare, GraphConnectorPhases.PrepareName),
            new ConnectorPhase(GraphConnectorPhases.Fetch, GraphConnectorPhases.FetchName)
        ],
        _ => []
    };
    #endregion

    #region IConnectorCredentialAware members
    public void SetCredentialProtection(ICredentialProtection? credentialProtection) => _credentialProtection = credentialProtection;
    #endregion

    #region IConnectorCertificateAware members
    /// <summary>
    /// Graph's certificates chain to a public CA, so JIM's certificate store matters only where a TLS-inspecting proxy
    /// re-signs traffic: its CA is added there and trusted for Graph and the token service alike.
    /// </summary>
    public void SetCertificateProvider(ICertificateProvider? certificateProvider) => _certificateProvider = certificateProvider;
    #endregion

    #region IConnectorSettings members
    public List<ConnectorSetting> GetSettings()
    {
        return
        [
            new() { Name = "Microsoft Entra ID Tenant", Category = ConnectedSystemSettingCategory.Connectivity, Type = ConnectedSystemSettingType.Heading },
            new() { Name = "Microsoft Entra ID Tenant Info", Description = "Enter the details of the tenant to synchronise with, and of the app registration JIM authenticates as. The app registration needs Microsoft Graph application permissions, granted with admin consent.", Category = ConnectedSystemSettingCategory.Connectivity, Type = ConnectedSystemSettingType.Label },
            new() { Name = GraphConnectorConstants.SettingTenantId, Required = true, Description = "The tenant's ID (a GUID), or one of its verified domain names, i.e. contoso.onmicrosoft.com. Shown as Directory (tenant) ID on the app registration's Overview page.", Category = ConnectedSystemSettingCategory.Connectivity, Type = ConnectedSystemSettingType.String },
            new()
            {
                Name = GraphConnectorConstants.SettingCloud,
                Required = false,
                Description = "The Microsoft cloud the tenant is in. Choose Global for Microsoft 365 GCC (Moderate) tenants too, which use the global endpoints.",
                Category = ConnectedSystemSettingCategory.Connectivity,
                Type = ConnectedSystemSettingType.DropDown,
                DropDownValues = GraphCloud.All.Select(c => c.Name).ToList(),
                DefaultStringValue = GraphConnectorConstants.CloudGlobal
            },

            new() { Name = "Authentication", Category = ConnectedSystemSettingCategory.Connectivity, Type = ConnectedSystemSettingType.Heading },
            new() { Name = GraphConnectorConstants.SettingClientId, Required = true, Description = "The app registration's Application (client) ID, a GUID shown on its Overview page.", Category = ConnectedSystemSettingCategory.Connectivity, Type = ConnectedSystemSettingType.String },
            new()
            {
                Name = GraphConnectorConstants.SettingAuthenticationMethod,
                Required = true,
                Description = "How JIM proves it is the app registration. A certificate, as Microsoft Entra Connect now uses, is recommended: its private key never leaves JIM, whereas a client secret is sent with every token request. A federated credential holds no secret at all: where JIM runs on an Azure virtual machine with a user-assigned managed identity, the app registration trusts that identity's token instead. Use a client secret for a quick trial.",
                Category = ConnectedSystemSettingCategory.Connectivity,
                Type = ConnectedSystemSettingType.DropDown,
                DropDownValues = [GraphConnectorConstants.AuthMethodCertificate, GraphConnectorConstants.AuthMethodFederatedCredential, GraphConnectorConstants.AuthMethodClientSecret],
                DefaultStringValue = GraphConnectorConstants.AuthMethodCertificate
            },
            new() { Name = GraphConnectorConstants.SettingClientSecret, RequiredWhenSetting = GraphConnectorConstants.SettingAuthenticationMethod, RequiredWhenValue = GraphConnectorConstants.AuthMethodClientSecret, Description = "A client secret created under Certificates & secrets on the app registration. Supply the secret's Value, not its Secret ID.", Category = ConnectedSystemSettingCategory.Connectivity, Type = ConnectedSystemSettingType.StringEncrypted },
            new() { Name = GraphConnectorConstants.SettingCertificate, RequiredWhenSetting = GraphConnectorConstants.SettingAuthenticationMethod, RequiredWhenValue = GraphConnectorConstants.AuthMethodCertificate, Description = $"The certificate and its private key, as a PFX file's contents Base64-encoded, i.e. the output of [Convert]::ToBase64String([IO.File]::ReadAllBytes('app.pfx')) in PowerShell. Upload the same certificate (without its private key) under Certificates & secrets on the app registration. It needs an RSA key of at least {GraphConnectorConstants.MinimumRsaKeySizeBits} bits.", Category = ConnectedSystemSettingCategory.Connectivity, Type = ConnectedSystemSettingType.StringEncrypted },
            new() { Name = GraphConnectorConstants.SettingManagedIdentityClientId, RequiredWhenSetting = GraphConnectorConstants.SettingAuthenticationMethod, RequiredWhenValue = GraphConnectorConstants.AuthMethodFederatedCredential, Description = "The client id (a GUID) of the user-assigned managed identity attached to the Azure virtual machine JIM runs on. A system-assigned identity cannot be a federated credential. Add a federated credential for this identity under Certificates & secrets on the app registration.", Category = ConnectedSystemSettingCategory.Connectivity, Type = ConnectedSystemSettingType.String },
            new() { Name = GraphConnectorConstants.SettingCertificatePassword, Required = false, Description = "The password protecting the PFX, if it has one. Only used with the Certificate authentication method.", Category = ConnectedSystemSettingCategory.Connectivity, Type = ConnectedSystemSettingType.StringEncrypted },
            new() { Name = GraphConnectorConstants.SettingConnectionTimeout, Required = false, DefaultIntValue = GraphConnectorConstants.DefaultConnectionTimeoutSeconds, Description = $"How long to wait, in seconds, for Microsoft Graph or the token service to answer a request before giving up. Up to {GraphConnectorConstants.MaximumConnectionTimeoutSeconds}.", Category = ConnectedSystemSettingCategory.Connectivity, Type = ConnectedSystemSettingType.Integer },

            new() { Name = "Rate Limiting", Category = ConnectedSystemSettingCategory.General, Type = ConnectedSystemSettingType.Heading },
            new() { Name = GraphConnectorConstants.SettingMaximumWritesPerSecond, Required = false, DefaultIntValue = GraphConnectorConstants.DefaultMaximumWritesPerSecond, Description = $"The most writes JIM sends to the tenant each second, from 1 to {(int)GraphRequestPacer.WriteQuotaPerSecond}. Microsoft Graph allows each application {(int)GraphRequestPacer.WriteQuotaPerSecond} writes a second in a tenant, and every application in the tenant shares a wider limit, so the default of {GraphConnectorConstants.DefaultMaximumWritesPerSecond} leaves room for the tenant's other applications. JIM slows down further by itself while Graph reports the tenant is near its limit. Reads are not limited here; JIM waits whenever Graph asks it to.", Category = ConnectedSystemSettingCategory.General, Type = ConnectedSystemSettingType.Integer },
            new() { Name = GraphConnectorConstants.SettingMaxRetries, Required = false, DefaultIntValue = GraphConnectorConstants.DefaultMaxRetries, Description = $"How many times to retry a request that failed for a temporary reason, such as throttling (HTTP 429) or Graph being briefly unavailable, from 0 to {GraphConnectorConstants.MaximumMaxRetries}. JIM waits as long as Graph asks before each retry.", Category = ConnectedSystemSettingCategory.General, Type = ConnectedSystemSettingType.Integer },

            new() { Name = "Source of Authority", Category = ConnectedSystemSettingCategory.Import, Type = ConnectedSystemSettingType.Heading },
            new() { Name = GraphConnectorConstants.SettingTrackSourceOfAuthority, Required = false, DefaultCheckboxValue = false, Description = "Whether to read, for each object synchronised from Active Directory, whether its Source of Authority has been switched to Entra ID, or switched back, so sourceOfAuthority shows where it is managed now. Turn it on in a hybrid tenant whose objects have their Source of Authority switched. It needs the User-OnPremisesSyncBehavior.ReadWrite.All and Group-OnPremisesSyncBehavior.ReadWrite.All permissions, the only ones Microsoft offers for reading it, which also allow changing it. Off, an object synchronised from Active Directory always reads as onPremisesDirectory.", Category = ConnectedSystemSettingCategory.Import, Type = ConnectedSystemSettingType.CheckBox },
        ];
    }

    /// <summary>
    /// Validates the Microsoft Graph Connector's setting values. Missing required values are reported centrally by
    /// ConnectorSettingValidator before this runs, so only the shape of supplied values is checked here.
    /// </summary>
    public List<ConnectorSettingValueValidationResult> ValidateSettingValues(List<ConnectedSystemSettingValue> settingValues, ILogger logger)
    {
        logger.Verbose($"ValidateSettingValues() called for {Name}");
        var response = new List<ConnectorSettingValueValidationResult>();

        var tenantId = Find(settingValues, GraphConnectorConstants.SettingTenantId);
        if (!string.IsNullOrWhiteSpace(tenantId?.StringValue))
        {
            var tenantError = CheckTenantId(tenantId.StringValue.Trim());
            if (tenantError != null)
                response.Add(Failure(tenantId, tenantError));
        }

        var clientId = Find(settingValues, GraphConnectorConstants.SettingClientId);
        if (!string.IsNullOrWhiteSpace(clientId?.StringValue) && !Guid.TryParse(clientId.StringValue.Trim(), out _))
        {
            response.Add(Failure(clientId, Message(nameof(ConnectorMessages.GraphClientIdInvalid), GraphConnectorConstants.SettingClientId)));
        }

        var cloud = Find(settingValues, GraphConnectorConstants.SettingCloud);
        if (cloud != null && !GraphCloud.TryResolve(cloud.StringValue, out _))
        {
            response.Add(Failure(cloud, Message(nameof(ConnectorMessages.GraphCloudUnknown), cloud.StringValue, string.Join(", ", GraphCloud.All.Select(c => c.Name)))));
        }

        CheckRange(settingValues, GraphConnectorConstants.SettingConnectionTimeout, 1, GraphConnectorConstants.MaximumConnectionTimeoutSeconds, response);
        CheckRange(settingValues, GraphConnectorConstants.SettingMaximumWritesPerSecond, 1, (int)GraphRequestPacer.WriteQuotaPerSecond, response);
        CheckRange(settingValues, GraphConnectorConstants.SettingMaxRetries, 0, GraphConnectorConstants.MaximumMaxRetries, response);

        var managedIdentityClientId = Find(settingValues, GraphConnectorConstants.SettingManagedIdentityClientId);
        if (!string.IsNullOrWhiteSpace(managedIdentityClientId?.StringValue) && !Guid.TryParse(managedIdentityClientId.StringValue.Trim(), out _))
        {
            response.Add(Failure(managedIdentityClientId, Message(nameof(ConnectorMessages.GraphManagedIdentityIdInvalid), GraphConnectorConstants.SettingManagedIdentityClientId)));
        }

        var authenticationMethod = Find(settingValues, GraphConnectorConstants.SettingAuthenticationMethod)?.StringValue ?? GraphConnectorConstants.AuthMethodCertificate;
        var certificate = Find(settingValues, GraphConnectorConstants.SettingCertificate);
        if (authenticationMethod == GraphConnectorConstants.AuthMethodCertificate && !string.IsNullOrWhiteSpace(certificate?.StringEncryptedValue))
        {
            var certificateResult = CheckCertificate(settingValues, certificate);
            if (certificateResult != null)
                response.Add(certificateResult);
        }

        // The connectivity test acquires a token, so it runs only once the settings are well formed and complete:
        // nothing, the credential included, is sent while validation has any reason to refuse the configuration.
        if (response.Any(r => !r.IsValid) || !HasEverythingToConnect(settingValues, authenticationMethod) || tenantId == null)
            return response;

        response.AddRange(TestConnectivity(settingValues, tenantId, logger));
        return response;
    }
    #endregion

    /// <summary>
    /// A tenant ID is a GUID or a verified domain name. The multi-tenant aliases that sign-in pages accept are refused:
    /// app-only authentication acts in one specific tenant, and Entra ID rejects client credentials requests to them.
    /// </summary>
    private static JimMessage? CheckTenantId(string tenantId)
    {
        if (Guid.TryParse(tenantId, out _))
            return null;

        if (MultiTenantAliases.Contains(tenantId, StringComparer.OrdinalIgnoreCase))
        {
            return Message(nameof(ConnectorMessages.GraphTenantAlias), tenantId);
        }

        if (Uri.CheckHostName(tenantId) == UriHostNameType.Dns && tenantId.Contains('.'))
            return null;

        return Message(nameof(ConnectorMessages.GraphTenantInvalid), GraphConnectorConstants.SettingTenantId);
    }

    private ConnectorSettingValueValidationResult? CheckCertificate(List<ConnectedSystemSettingValue> settingValues, ConnectedSystemSettingValue certificate)
    {
        var pfx = Decrypt(certificate.StringEncryptedValue);
        if (string.IsNullOrWhiteSpace(pfx))
            return Failure(certificate, Message(nameof(ConnectorMessages.GraphSettingNotDecrypted), GraphConnectorConstants.SettingCertificate));

        var password = Decrypt(Find(settingValues, GraphConnectorConstants.SettingCertificatePassword)?.StringEncryptedValue);

        using var result = GraphClientCertificate.Load(pfx, password, DateTime.UtcNow);
        if (result.Error != null)
            return Error(certificate, result.Error);

        return result.Warning != null
            ? ConnectorSettingValueValidationResult.Warning(certificate, result.Warning)
            : null;
    }

    /// <summary>Decrypts stored secrets; null where JIM has not configured credential protection.</summary>
    internal ICredentialProtection? CredentialProtection => _credentialProtection;

    /// <summary>
    /// Builds a client for the given Connected System, with JIM's trusted certificates. Overridden by tests to stub
    /// the transport.
    /// </summary>
    internal virtual async Task<GraphHttpClient> CreateClientAsync(List<ConnectedSystemSettingValue> settingValues, ILogger logger,
        GraphClientPurpose purpose = GraphClientPurpose.Run)
    {
        var trustedCertificates = _certificateProvider != null
            ? await _certificateProvider.GetTrustedCertificatesAsync()
            : [];

        return GraphHttpClientFactory.Create(settingValues, trustedCertificates, _credentialProtection, logger, purpose,
            failure => DescribeCertificateRejection(settingValues, failure, logger));
    }

    #region IConnectorSecureEndpoint members
    /// <summary>
    /// Microsoft Entra ID's token service for the configured cloud, read through the outbound proxy as the Connector
    /// reaches it. Every connection starts there, and its certificate, like Microsoft Graph's, chains to a public
    /// certificate authority: a refused one is in practice a TLS-inspecting proxy presenting its own, which is the
    /// same proxy certificate authority for Microsoft Graph.
    /// </summary>
    /// <returns>The endpoint, or null where the Cloud setting names a cloud this Connector does not know.</returns>
    public SecureEndpoint? ResolveSecureEndpoint(List<ConnectedSystemSettingValue> settingValues)
    {
        if (!GraphCloud.TryResolve(Find(settingValues, GraphConnectorConstants.SettingCloud)?.StringValue, out var cloud))
            return null;

        var timeoutSeconds = Find(settingValues, GraphConnectorConstants.SettingConnectionTimeout)?.IntValue ?? GraphConnectorConstants.DefaultConnectionTimeoutSeconds;
        return new SecureEndpoint(
            cloud.AuthorityHost.Host,
            cloud.AuthorityHost.IsDefaultPort ? 443 : cloud.AuthorityHost.Port,
            TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 1, GraphConnectorConstants.MaximumConnectionTimeoutSeconds)),
            "Microsoft Entra ID",
            "HTTPS",
            SecureHandshakeFraming.Https);
    }
    #endregion

    /// <summary>
    /// Describes the certificate behind a failed connection, when that is what refused it, so the administrator is
    /// shown it with the option to trust it rather than a transport error indistinguishable from a firewall.
    /// </summary>
    /// <returns>The rejection to report, or null when the certificate is not what refused the connection.</returns>
    internal ServerCertificateRejectedException? DescribeCertificateRejection(List<ConnectedSystemSettingValue> settingValues, Exception failure, ILogger logger)
    {
        if (!TlsTrustFailure.IsTlsTrustFailure(failure))
            return null;

        return ServerCertificateDiagnosis.Describe(this, settingValues, _certificateProvider, failure, logger,
            (endpoint, trusted) => ProbeCertificate(endpoint, trusted, logger));
    }

    /// <summary>Looks at the certificate the endpoint presents. Virtual so a test can supply one without a TLS server.</summary>
    internal virtual ServerCertificateDiagnostic? ProbeCertificate(SecureEndpoint endpoint, IReadOnlyCollection<X509Certificate2> trustedCertificates, ILogger logger) =>
        ServerCertificateProbe.Probe(endpoint.Host, endpoint.Port, trustedCertificates, endpoint.Timeout, logger,
            endpoint.ServerDescription, endpoint.SecureTransportName, endpoint.HandshakeFraming);

    /// <summary>
    /// Acquires a token, reads which application permissions it grants, and asks Graph for one object, which proves the
    /// tenant, the cloud, the credential and the permissions all work together. Results are reported against the
    /// Tenant ID, the setting that names where JIM connects.
    /// </summary>
    private List<ConnectorSettingValueValidationResult> TestConnectivity(
        List<ConnectedSystemSettingValue> settingValues,
        ConnectedSystemSettingValue tenantId,
        ILogger logger)
    {
        try
        {
            // Task.Run keeps the blocking wait off the caller's synchronisation context. Setting validation is invoked
            // from Blazor Server circuits, which have one, and blocking on it directly would deadlock the circuit.
            return Task.Run(async () =>
            {
                using var client = await CreateClientAsync(settingValues, logger, GraphClientPurpose.ConnectionTest);
                var token = await client.GetAccessTokenAsync(CancellationToken.None);
                var permissions = GraphPermissions.Check(GraphAccessToken.ReadRoles(token), TracksSourceOfAuthority(settingValues));
                if (permissions.Error != null)
                    return [Error(tenantId, permissions.Error)];

                var collection = permissions.CanReadUsers ? "users" : "groups";
                using (await client.SendAsync(HttpMethod.Get, $"{collection}?$select=id&$top=1", null, null, CancellationToken.None))
                {
                }

                return permissions.Warning != null
                    ? [ConnectorSettingValueValidationResult.Warning(tenantId, permissions.Warning)]
                    : new List<ConnectorSettingValueValidationResult>();
            }).GetAwaiter().GetResult();
        }
        catch (ServerCertificateRejectedException ex)
        {
            // Carried on the result, which is what the portal reads to show the certificate and offer to trust it.
            return [new ConnectorSettingValueValidationResult { IsValid = false, ErrorMessage = ex.Message, SettingValue = tenantId, Exception = ex }];
        }
        catch (HttpAuthenticationException ex)
        {
            logger.Warning(ex, "The Microsoft Graph connectivity test could not acquire an access token.");
            return [ex.Detail != null ? Failure(tenantId, ex.Detail) : Error(tenantId, ex.Message)];
        }
        catch (GraphRequestException ex)
        {
            logger.Warning(ex, "The Microsoft Graph connectivity test failed.");
            return [Failure(tenantId, Message(nameof(ConnectorMessages.GraphTestRequestRefused), LogSanitiser.Sanitise(ex.Message)))];
        }
        catch (Exception ex) when (ex is HttpRequestException or TimeoutException or TaskCanceledException)
        {
            logger.Warning(ex, "The Microsoft Graph connectivity test could not connect.");
            return [Failure(tenantId, Message(nameof(ConnectorMessages.GraphCouldNotConnect)))];
        }
    }

    /// <summary>
    /// Whether every value the connectivity test needs is present. Missing ones are reported by the generic validator,
    /// so the test simply does not run.
    /// </summary>
    private static bool HasEverythingToConnect(List<ConnectedSystemSettingValue> settingValues, string authenticationMethod)
    {
        var identified = !string.IsNullOrWhiteSpace(Find(settingValues, GraphConnectorConstants.SettingTenantId)?.StringValue)
                         && !string.IsNullOrWhiteSpace(Find(settingValues, GraphConnectorConstants.SettingClientId)?.StringValue);
        if (authenticationMethod == GraphConnectorConstants.AuthMethodFederatedCredential)
        {
            return identified && !string.IsNullOrWhiteSpace(Find(settingValues, GraphConnectorConstants.SettingManagedIdentityClientId)?.StringValue);
        }

        var credential = authenticationMethod == GraphConnectorConstants.AuthMethodClientSecret
            ? GraphConnectorConstants.SettingClientSecret
            : GraphConnectorConstants.SettingCertificate;

        return identified && !string.IsNullOrWhiteSpace(Find(settingValues, credential)?.StringEncryptedValue);
    }

    /// <summary>Whether Track Source of Authority Changes is on.</summary>
    internal static bool TracksSourceOfAuthority(IEnumerable<ConnectedSystemSettingValue>? settingValues) =>
        settingValues?.FirstOrDefault(s => s.Setting?.Name == GraphConnectorConstants.SettingTrackSourceOfAuthority)?.CheckboxValue == true;

    private static void CheckRange(List<ConnectedSystemSettingValue> settingValues, string name, int minimum, int maximum, List<ConnectorSettingValueValidationResult> response)
    {
        var setting = Find(settingValues, name);
        if (setting?.IntValue is { } value && (value < minimum || value > maximum))
            response.Add(Failure(setting, Message(nameof(ConnectorMessages.GraphNumberOutOfRange), name,
                minimum.ToString(System.Globalization.CultureInfo.InvariantCulture), maximum.ToString(System.Globalization.CultureInfo.InvariantCulture))));
    }

    /// <summary>The stored value as entered, decrypting it where JIM has encrypted it at rest.</summary>
    private string? Decrypt(string? stored) =>
        string.IsNullOrEmpty(stored) ? stored : _credentialProtection?.Unprotect(stored) ?? stored;

    private static ConnectedSystemSettingValue? Find(List<ConnectedSystemSettingValue> settingValues, string name) =>
        settingValues.SingleOrDefault(s => s.Setting.Name == name);

    private static ConnectorSettingValueValidationResult Error(ConnectedSystemSettingValue settingValue, string message) =>
        new() { IsValid = false, ErrorMessage = message, SettingValue = settingValue };

    private static ConnectorSettingValueValidationResult Failure(ConnectedSystemSettingValue settingValue, JimMessage message) =>
        ConnectorSettingValueValidationResult.Failure(settingValue, message);

    private static JimMessage Message(string key, params MessageArgument[] arguments) =>
        JimMessage.Create(ConnectorMessageCatalogues.Connectors, key, arguments);
}
