using JIM.Models.Exceptions;
using JIM.Models.Interfaces;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using Serilog;
using System.DirectoryServices.Protocols;
using System.Net;
using System.Security.Cryptography.X509Certificates;
namespace JIM.Connectors.LDAP;

public class LdapConnector : IConnector, IConnectorCapabilities, IConnectorSettings, IConnectorSchema, IConnectorPartitions, IConnectorImportUsingCalls, IConnectorExportUsingCalls, IConnectorCertificateAware, IDisposable
{
    private LdapConnection? _connection;
    private bool _disposed;
    private ICertificateProvider? _certificateProvider;
    private List<X509Certificate2>? _trustedCertificates;

    #region IConnector members
    public string Name => ConnectorConstants.LdapConnectorName;

    public string? Description => "Enables bi-directional synchronisation with LDAP compliant directories, including Microsoft Active Directory.";

    public string? Url => "https://github.com/TetronIO/JIM";
    #endregion

    #region IConnectorCapability members
    public bool SupportsFullImport => true;
    public bool SupportsDeltaImport => true;
    public bool SupportsExport => true;
    public bool SupportsPartitions => true;
    public bool SupportsPartitionContainers => true;
    public bool SupportsSecondaryExternalId => true;
    public bool SupportsUserSelectedExternalId => false;
    public bool SupportsUserSelectedAttributeTypes => false;
    public bool SupportsAutoConfirmExport => false;
    #endregion

    #region IConnectorSettings members
    // variablising the names to reduce repetition later on, i.e. when we go to consume setting values JIM passes in, or when validating administrator-supplied settings
    private readonly string _settingDirectoryServer = "Host";
    private readonly string _settingDirectoryServerPort = "Port";
    private readonly string _settingUseSecureConnection = "Use Secure Connection (LDAPS)?";
    private readonly string _settingCertificateValidation = "Certificate Validation";
    private readonly string _settingConnectionTimeout = "Connection Timeout";
    private readonly string _settingUsername = "Username";
    private readonly string _settingPassword = "Password";
    private readonly string _settingAuthType = "Authentication Type";
    private readonly string _settingSearchTimeout = "Search Timeout";
    private readonly string _settingCreateContainersAsNeeded = "Create containers as needed?";
    private readonly string _settingMaxRetries = "Maximum Retries";
    private readonly string _settingRetryDelay = "Retry Delay (ms)";

    // Export settings
    private readonly string _settingDeleteBehaviour = "Delete Behaviour";
    private readonly string _settingDisableAttribute = "Disable Attribute";

    public List<ConnectorSetting> GetSettings()
    {
        return new List<ConnectorSetting>
        {
            new() { Name = "Directory Server", Category = ConnectedSystemSettingCategory.Connectivity, Type = ConnectedSystemSettingType.Heading },
            new() { Name = "Directory Server Info", Description = "Enter Active Directory domain controller, or LDAP server details below.", Category = ConnectedSystemSettingCategory.Connectivity, Type = ConnectedSystemSettingType.Label },
            new() { Name = _settingDirectoryServer, Required = true, Description = "Supply a directory server/domain controller hostname or IP address. IP address is fastest.", Category = ConnectedSystemSettingCategory.Connectivity, Type = ConnectedSystemSettingType.String },
            new() { Name = _settingDirectoryServerPort, Required = true, Description = "The port to connect to the directory service on. Use 389 for LDAP or 636 for LDAPS.", DefaultIntValue = LdapConnectorConstants.DEFAULT_LDAP_PORT, Category = ConnectedSystemSettingCategory.Connectivity, Type = ConnectedSystemSettingType.Integer },
            new() { Name = _settingUseSecureConnection, Description = "Enable LDAPS (SSL/TLS) for encrypted communication. Requires appropriate port (typically 636).", DefaultCheckboxValue = false, Category = ConnectedSystemSettingCategory.Connectivity, Type = ConnectedSystemSettingType.CheckBox },
            new() { Name = _settingCertificateValidation, Required = false, Description = "How to validate the server's SSL certificate. Full validation uses system CA store plus any certificates added in Admin > Certificates.", Type = ConnectedSystemSettingType.DropDown, DropDownValues = new() { LdapConnectorConstants.CERT_VALIDATION_FULL, LdapConnectorConstants.CERT_VALIDATION_SKIP }, Category = ConnectedSystemSettingCategory.Connectivity },
            new() { Name = _settingConnectionTimeout, Required = true, Description = "How long to wait, in seconds, before giving up on trying to connect", DefaultIntValue = 10, Category = ConnectedSystemSettingCategory.Connectivity, Type = ConnectedSystemSettingType.Integer },

            new() { Category = ConnectedSystemSettingCategory.Connectivity, Type = ConnectedSystemSettingType.Divider },

            new() { Name = "Credentials", Category = ConnectedSystemSettingCategory.Connectivity, Type = ConnectedSystemSettingType.Heading },
            new() { Name = _settingUsername, Required = true, Description = "What's the username for the service account you want to use to connect to the directory service using? i.e. corp\\svc-jim-adc", Category = ConnectedSystemSettingCategory.Connectivity, Type = ConnectedSystemSettingType.String  },
            new() { Name = _settingPassword, Required = true, Description = "What's the password for the service account you want to use to connect to the directory service with?", Category = ConnectedSystemSettingCategory.Connectivity, Type = ConnectedSystemSettingType.StringEncrypted },
            new() { Name = _settingAuthType, Required = true, Description = "What type of authentication is required for this credential?", Category = ConnectedSystemSettingCategory.Connectivity, Type = ConnectedSystemSettingType.DropDown, DropDownValues = new() { LdapConnectorConstants.SETTING_AUTH_TYPE_SIMPLE, LdapConnectorConstants.SETTING_AUTH_TYPE_NTLM }},

            new() { Name = "Import Settings", Category = ConnectedSystemSettingCategory.General, Type = ConnectedSystemSettingType.Heading },
            new() { Name = _settingSearchTimeout, Required = false, Description = "Maximum time in seconds to wait for LDAP search results. Default is 300 (5 minutes).", DefaultIntValue = 300, Category = ConnectedSystemSettingCategory.General, Type = ConnectedSystemSettingType.Integer },

            new() { Name = "Retry Settings", Category = ConnectedSystemSettingCategory.General, Type = ConnectedSystemSettingType.Heading },
            new() { Name = _settingMaxRetries, Required = false, Description = "Maximum number of retry attempts for transient failures. Default is 3.", DefaultIntValue = LdapConnectorConstants.DEFAULT_MAX_RETRIES, Category = ConnectedSystemSettingCategory.General, Type = ConnectedSystemSettingType.Integer },
            new() { Name = _settingRetryDelay, Required = false, Description = "Initial delay between retries in milliseconds. Uses exponential backoff. Default is 1000ms.", DefaultIntValue = LdapConnectorConstants.DEFAULT_RETRY_DELAY_MS, Category = ConnectedSystemSettingCategory.General, Type = ConnectedSystemSettingType.Integer },

            new() { Name = "Container Provisioning", Category = ConnectedSystemSettingCategory.General, Type = ConnectedSystemSettingType.Heading },
            new() { Name = _settingCreateContainersAsNeeded, Description = "i.e. create OUs as needed when provisioning new objects.", DefaultCheckboxValue = false, Category = ConnectedSystemSettingCategory.General, Type = ConnectedSystemSettingType.CheckBox },

            // Export settings
            new() { Name = "Export Settings", Category = ConnectedSystemSettingCategory.Export, Type = ConnectedSystemSettingType.Heading },
            new() { Name = _settingDeleteBehaviour, Required = false, Description = "How to handle object deletions.", Type = ConnectedSystemSettingType.DropDown, DropDownValues = new() { LdapConnectorConstants.DELETE_BEHAVIOUR_DELETE, LdapConnectorConstants.DELETE_BEHAVIOUR_DISABLE }, Category = ConnectedSystemSettingCategory.Export },
            new() { Name = _settingDisableAttribute, Required = false, Description = "Attribute to set when disabling objects (e.g., userAccountControl for AD). Only used when Delete Behaviour is 'Disable'.", DefaultStringValue = "userAccountControl", Category = ConnectedSystemSettingCategory.Export, Type = ConnectedSystemSettingType.String }
        };
    }

    /// <summary>
    /// Validates LdapConnector setting values using custom business logic.
    /// </summary>
    public List<ConnectorSettingValueValidationResult> ValidateSettingValues(List<ConnectedSystemSettingValue> settingValues, ILogger logger)
    {
        logger.Verbose($"ValidateSettingValues() called for {Name}");
        var response = new List<ConnectorSettingValueValidationResult>();

        // validate that we can connect to the directory service with the supplied setting credentials
        var connectivityTestResult = TestDirectoryConnectivity(settingValues, logger);
        if (!connectivityTestResult.IsValid)
            response.Add(connectivityTestResult);

        // general required setting value validation
        foreach (var requiredSettingValue in settingValues.Where(q => q.Setting.Required))
        {
            if (requiredSettingValue.Setting.Type == ConnectedSystemSettingType.String && string.IsNullOrEmpty(requiredSettingValue.StringValue))
                response.Add(new ConnectorSettingValueValidationResult { ErrorMessage = $"Please supply a value for {requiredSettingValue.Setting.Name}", IsValid = false, SettingValue = requiredSettingValue });

            // keeping this separate for now, as encrypted strings are going to have to improve their implementation at some point
            if (requiredSettingValue.Setting.Type == ConnectedSystemSettingType.StringEncrypted && string.IsNullOrEmpty(requiredSettingValue.StringEncryptedValue))
                response.Add(new ConnectorSettingValueValidationResult { ErrorMessage = $"Please supply a value for {requiredSettingValue.Setting.Name}", IsValid = false, SettingValue = requiredSettingValue });

            if (requiredSettingValue.Setting.Type == ConnectedSystemSettingType.Integer && !requiredSettingValue.IntValue.HasValue)
                response.Add(new ConnectorSettingValueValidationResult { ErrorMessage = $"Please supply a value for {requiredSettingValue.Setting.Name}", IsValid = false, SettingValue = requiredSettingValue });
        }

        return response;
    }
    #endregion

    #region IConnectorSchema members
    public async Task<ConnectorSchema> GetSchemaAsync(List<ConnectedSystemSettingValue> settingValues, ILogger logger)
    {
        OpenImportConnection(settingValues, logger);
        if (_connection == null)
            throw new Exception("No connection available to get schema with");

        var ldapConnectorSchema = new LdapConnectorSchema(_connection, logger);
        var schema = await ldapConnectorSchema.GetSchemaAsync();
        CloseImportConnection();
        return schema;
    }
    #endregion

    #region IConnectorPartitions members
    public async Task<List<ConnectorPartition>> GetPartitionsAsync(List<ConnectedSystemSettingValue> settingValues, ILogger logger)
    {
        OpenImportConnection(settingValues, logger);
        if (_connection == null)
            throw new Exception("No connection available to get partitions with");

        var ldapConnectorPartitions = new LdapConnectorPartitions(_connection, logger);
        var partitions = await ldapConnectorPartitions.GetPartitionsAsync();
        CloseImportConnection();
        return partitions;
    }
    #endregion

    #region IConnectorImportUsingCalls members
    public void OpenImportConnection(List<ConnectedSystemSettingValue> settingValues, ILogger logger)
    {
        logger.Verbose("OpenImportConnection() called");
        var directoryServer = settingValues.SingleOrDefault(q => q.Setting.Name == _settingDirectoryServer);
        var directoryServerPort = settingValues.SingleOrDefault(q => q.Setting.Name == _settingDirectoryServerPort);
        var timeoutSeconds = settingValues.SingleOrDefault(q => q.Setting.Name == _settingConnectionTimeout);
        var username = settingValues.SingleOrDefault(q => q.Setting.Name == _settingUsername);
        var password = settingValues.SingleOrDefault(q => q.Setting.Name == _settingPassword);
        var authTypeSettingValue = settingValues.SingleOrDefault(q => q.Setting.Name == _settingAuthType);
        var useSecureConnection = settingValues.SingleOrDefault(q => q.Setting.Name == _settingUseSecureConnection);
        var certificateValidation = settingValues.SingleOrDefault(q => q.Setting.Name == _settingCertificateValidation);
        var maxRetriesSetting = settingValues.SingleOrDefault(q => q.Setting.Name == _settingMaxRetries);
        var retryDelaySetting = settingValues.SingleOrDefault(q => q.Setting.Name == _settingRetryDelay);

        if (username == null || string.IsNullOrEmpty(username.StringValue) ||
            password == null || string.IsNullOrEmpty(password.StringEncryptedValue) ||
            authTypeSettingValue == null || string.IsNullOrEmpty(authTypeSettingValue.StringValue) ||
            directoryServer == null || string.IsNullOrEmpty(directoryServer.StringValue) ||
            directoryServerPort is not { IntValue: not null } ||
            timeoutSeconds is not { IntValue: not null })
            throw new InvalidSettingValuesException($"Missing setting values for {_settingDirectoryServer}, {_settingDirectoryServerPort}, {_settingConnectionTimeout}, {_settingUsername},{_settingPassword}, or {_settingAuthType}.");

        var useSsl = useSecureConnection?.CheckboxValue ?? false;
        var skipCertValidation = certificateValidation?.StringValue == LdapConnectorConstants.CERT_VALIDATION_SKIP;
        var maxRetries = maxRetriesSetting?.IntValue ?? LdapConnectorConstants.DEFAULT_MAX_RETRIES;
        var retryDelayMs = retryDelaySetting?.IntValue ?? LdapConnectorConstants.DEFAULT_RETRY_DELAY_MS;

        logger.Debug("OpenImportConnection() Trying to connect to '{Server}' on port '{Port}' with username '{Username}' via auth type {AuthType}. SSL: {UseSsl}, SkipCertValidation: {SkipCertValidation}",
            directoryServer.StringValue, directoryServerPort.IntValue, username.StringValue, authTypeSettingValue.StringValue, useSsl, skipCertValidation);

        // Load JIM certificates for full validation (supplements system CA store)
        if (useSsl && !skipCertValidation && _certificateProvider != null)
        {
            _trustedCertificates = _certificateProvider.GetTrustedCertificatesAsync().GetAwaiter().GetResult();
            if (_trustedCertificates.Count > 0)
                logger.Debug("Loaded {Count} additional trusted certificates from JIM Store", _trustedCertificates.Count);
        }

        var identifier = new LdapDirectoryIdentifier(directoryServer.StringValue, directoryServerPort.IntValue.Value);
        var credential = new NetworkCredential(username.StringValue, password.StringEncryptedValue);

        // allow the user to specify what type of authentication to perform against the supplied credential.
        var authTypeSettingValueString = authTypeSettingValue.StringValue;
        var authTypeEnumValue = AuthType.Anonymous;
        if (authTypeSettingValueString == LdapConnectorConstants.SETTING_AUTH_TYPE_SIMPLE)
            authTypeEnumValue = AuthType.Basic;
        else if (authTypeSettingValueString == LdapConnectorConstants.SETTING_AUTH_TYPE_NTLM)
            authTypeEnumValue = AuthType.Ntlm;

        // Execute connection with retry logic
        ExecuteWithRetry(() =>
        {
            _connection = new LdapConnection(identifier, credential, authTypeEnumValue);
            _connection.SessionOptions.ProtocolVersion = 3;
            _connection.Timeout = TimeSpan.FromSeconds(timeoutSeconds.IntValue.Value);

            // Configure LDAPS if enabled
            if (useSsl)
            {
                _connection.SessionOptions.SecureSocketLayer = true;

                if (skipCertValidation)
                {
                    logger.Warning("Certificate validation is disabled. This is not recommended for production environments.");
                    _connection.SessionOptions.VerifyServerCertificate = (connection, certificate) => true;
                }
                else if (_trustedCertificates != null && _trustedCertificates.Count > 0)
                {
                    // Full validation with JIM certificates supplementing system store
                    _connection.SessionOptions.VerifyServerCertificate = ValidateServerCertificate;
                }
                // else: use system default validation only
            }

            _connection.Bind();
        }, maxRetries, retryDelayMs, logger);
    }

    /// <summary>
    /// Executes an action with retry logic for transient failures.
    /// Uses exponential backoff between retries.
    /// </summary>
    private static void ExecuteWithRetry(Action action, int maxRetries, int baseDelayMs, ILogger logger)
    {
        var attempt = 0;
        while (true)
        {
            try
            {
                action();
                return;
            }
            catch (LdapException ex) when (IsTransientError(ex) && attempt < maxRetries)
            {
                attempt++;
                var delay = baseDelayMs * (int)Math.Pow(2, attempt - 1);
                logger.Warning(ex, "Transient LDAP error on attempt {Attempt}/{MaxRetries}. Retrying in {Delay}ms",
                    attempt, maxRetries, delay);
                Thread.Sleep(delay);
            }
        }
    }

    /// <summary>
    /// Determines if an LDAP exception represents a transient error that may succeed on retry.
    /// </summary>
    private static bool IsTransientError(LdapException ex)
    {
        // Common transient error codes
        return ex.ErrorCode switch
        {
            51 => true,  // Busy
            52 => true,  // Unavailable
            53 => true,  // Unwilling to perform (server overloaded)
            80 => true,  // Other (generic, often transient)
            81 => true,  // Server down
            -1 => true,  // Network/connection error
            _ => false
        };
    }

    public Task<ConnectedSystemImportResult> ImportAsync(ConnectedSystem connectedSystem, ConnectedSystemRunProfile runProfile, List<ConnectedSystemPaginationToken> paginationTokens, string? persistedConnectorData, ILogger logger, CancellationToken cancellationToken)
    {
        logger.Verbose("ImportAsync() called");
        // todo: wrap this in a task to eliminate the compiler warning. still needs to propagate exceptions and return values.

        if (_connection == null)
            throw new InvalidOperationException("Must call OpenImportConnection() before ImportAsync()!");

        // needs to filter by partitions
        // needs to filter by object types
        // needs to filter by attributes
        // needs to be able to stop processing at convenient points if cancellation has been requested

        var import = new LdapConnectorImport(connectedSystem, runProfile, _connection, paginationTokens, persistedConnectorData, logger, cancellationToken);

        switch (runProfile.RunType)
        {
            case ConnectedSystemRunType.FullImport:
                logger.Debug("ImportAsync: Full Import requested");
                return Task.FromResult(import.GetFullImportObjects());
            case ConnectedSystemRunType.DeltaImport:
                logger.Debug("ImportAsync: Delta Import requested");
                return Task.FromResult(import.GetDeltaImportObjects());
            case ConnectedSystemRunType.FullSynchronisation:
            case ConnectedSystemRunType.DeltaSynchronisation:
            case ConnectedSystemRunType.Export:
            default:
                throw new InvalidDataException($"Unsupported import run-type: {runProfile.RunType}");
        }
    }

    public void CloseImportConnection()
    {
        _connection?.Dispose();
    }
    #endregion

    #region IConnectorExportUsingCalls members
    private IList<ConnectedSystemSettingValue>? _exportSettings;

    public void OpenExportConnection(IList<ConnectedSystemSettingValue> settings)
    {
        _exportSettings = settings;

        // Reuse the same connection logic as import
        OpenImportConnection(settings.ToList(), Log.Logger);
    }

    public void Export(IList<PendingExport> pendingExports)
    {
        if (_connection == null)
            throw new InvalidOperationException("Must call OpenExportConnection() before Export()!");

        if (_exportSettings == null)
            throw new InvalidOperationException("Export settings not available. Call OpenExportConnection() first.");

        var export = new LdapConnectorExport(_connection, _exportSettings, Log.Logger);
        export.Execute(pendingExports);
    }

    public void CloseExportConnection()
    {
        _exportSettings = null;
        CloseImportConnection();
    }
    #endregion

    #region IConnectorCertificateAware members
    /// <summary>
    /// Sets the certificate provider for JIM Store certificate validation.
    /// </summary>
    public void SetCertificateProvider(ICertificateProvider? certificateProvider)
    {
        _certificateProvider = certificateProvider;
    }
    #endregion

    #region private methods
    /// <summary>
    /// Validates a server certificate against both system CA store and JIM certificate store.
    /// Returns true if the certificate is trusted by either store.
    /// </summary>
    private bool ValidateServerCertificate(LdapConnection connection, X509Certificate certificate)
    {
        try
        {
            var serverCert = new X509Certificate2(certificate);

            // First try standard system validation
            using var systemChain = new X509Chain();
            systemChain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            if (systemChain.Build(serverCert))
            {
                Log.Debug("Server certificate validated by system CA store");
                return true;
            }

            // System validation failed - try with JIM certificates
            if (_trustedCertificates == null || _trustedCertificates.Count == 0)
            {
                Log.Warning("Server certificate not trusted by system CA store and no JIM certificates available. Thumbprint: {Thumbprint}, Subject: {Subject}",
                    serverCert.Thumbprint, serverCert.Subject);
                return false;
            }

            // Build chain with JIM certificates as additional trust anchors
            using var jimChain = new X509Chain();
            jimChain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            jimChain.ChainPolicy.VerificationFlags = X509VerificationFlags.AllowUnknownCertificateAuthority;

            foreach (var trustedCert in _trustedCertificates)
            {
                jimChain.ChainPolicy.ExtraStore.Add(trustedCert);
            }

            jimChain.Build(serverCert);

            // Check if any certificate in the chain is in JIM's trusted store
            foreach (var chainElement in jimChain.ChainElements)
            {
                if (_trustedCertificates.Any(tc => tc.Thumbprint.Equals(chainElement.Certificate.Thumbprint, StringComparison.OrdinalIgnoreCase)))
                {
                    Log.Debug("Server certificate validated via JIM certificate store");
                    return true;
                }
            }

            Log.Warning("Server certificate validation failed. Not trusted by system or JIM store. Thumbprint: {Thumbprint}, Subject: {Subject}",
                serverCert.Thumbprint, serverCert.Subject);
            return false;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error validating server certificate");
            return false;
        }
    }

    private ConnectorSettingValueValidationResult TestDirectoryConnectivity(List<ConnectedSystemSettingValue> settingValues, ILogger logger)
    {
        try
        {
            OpenImportConnection(settingValues, logger);
            CloseImportConnection();
            return new ConnectorSettingValueValidationResult
            {
                IsValid = true
            };
        }
        catch (InvalidSettingValuesException)
        {
            return new ConnectorSettingValueValidationResult
            {
                ErrorMessage = "Unable to test connectivity due to missing directory server, port, username and/or password values"
            };
        }
        catch (Exception ex)
        {
            logger.Error(ex, $"TestDirectoryConnectivity failed");
            return new ConnectorSettingValueValidationResult
            {
                ErrorMessage = $"Unable to connect. Message: {ex.Message}",
                Exception = ex
            };
        }
    }
    #endregion

    #region IDisposable members
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (_disposed) return;

        if (disposing)
        {
            _connection?.Dispose();
            _connection = null;
        }

        _disposed = true;
    }
    #endregion
}