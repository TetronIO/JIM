// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Exceptions;
using JIM.Models.Interfaces;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using Serilog;
using System.DirectoryServices.Protocols;
using System.Net;
using System.Security.Cryptography.X509Certificates;
namespace JIM.Connectors.LDAP;

public class LdapConnector : IConnector, IConnectorCapabilities, IConnectorSettings, IConnectorSchema, IConnectorPartitions, IConnectorImportUsingCalls, IConnectorExportUsingCalls, IConnectorCertificateAware, IConnectorCredentialAware, IConnectorContainerCreation, IConnectorRecommendedExportParallelism, IDisposable
{
    private LdapConnection? _connection;
    private Func<LdapConnection>? _connectionFactory;
    private LdapDirectoryType _directoryType = LdapDirectoryType.Generic;
    private bool _disposed;
    private ICertificateProvider? _certificateProvider;
    private ICredentialProtection? _credentialProtection;
    private List<X509Certificate2>? _trustedCertificates;
    private LdapConnectorExport? _currentExport;

    #region IConnector members
    public string Name => ConnectorConstants.LdapConnectorName;

    public string? Description => "Enables bi-directional synchronisation with LDAP compliant directories, including Microsoft Active Directory, OpenLDAP, and Samba AD.";

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
    public bool SupportsParallelExport => true;
    public bool SupportsPaging => true;
    public bool SupportsFilePaths => false;
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

    // Schema settings
    private readonly string _settingIncludeAuxiliaryClasses = "Include Auxiliary Classes";

    // Hierarchy settings
    private readonly string _settingSkipHiddenPartitions = "Skip Hidden Partitions";

    // Import settings
    private readonly string _settingImportConcurrency = "Import Concurrency";

    // Export settings
    private readonly string _settingDeleteBehaviour = "Delete Behaviour";
    private readonly string _settingDisableAttribute = "Disable Attribute";
    private readonly string _settingExportConcurrency = "Export Concurrency";
    private readonly string _settingModifyBatchSize = "Modify Batch Size";
    private readonly string _settingGroupPlaceholderMemberDn = LdapConnectorConstants.SETTING_GROUP_PLACEHOLDER_MEMBER_DN;

    public List<ConnectorSetting> GetSettings()
    {
        return new List<ConnectorSetting>
        {
            new() { Name = "Directory Server", Category = ConnectedSystemSettingCategory.Connectivity, Type = ConnectedSystemSettingType.Heading },
            new() { Name = "Directory Server Info", Description = "Enter Active Directory domain controller, or LDAP server details below.", Category = ConnectedSystemSettingCategory.Connectivity, Type = ConnectedSystemSettingType.Label },
            new() { Name = _settingDirectoryServer, Required = true, Description = "Supply a directory server/domain controller hostname or IP address. IP address is fastest.", Category = ConnectedSystemSettingCategory.Connectivity, Type = ConnectedSystemSettingType.String },
            new() { Name = _settingDirectoryServerPort, Required = true, Description = "The port to connect to the directory service on. Use 389 for LDAP or 636 for LDAPS.", DefaultIntValue = LdapConnectorConstants.DEFAULT_LDAP_PORT, Category = ConnectedSystemSettingCategory.Connectivity, Type = ConnectedSystemSettingType.Integer },
            new() { Name = _settingUseSecureConnection, Description = "Enable LDAPS (SSL/TLS) for encrypted communication. Requires appropriate port (typically 636).", DefaultCheckboxValue = false, Category = ConnectedSystemSettingCategory.Connectivity, Type = ConnectedSystemSettingType.CheckBox },
            new() { Name = _settingCertificateValidation, Required = false, RequiredWhenSetting = _settingUseSecureConnection, RequiredWhenValue = "true", DefaultStringValue = LdapConnectorConstants.CERT_VALIDATION_FULL, Description = "How to validate the server's SSL certificate. Full validation uses system CA store plus any certificates added in Admin > Certificates.", Type = ConnectedSystemSettingType.DropDown, DropDownValues = new() { LdapConnectorConstants.CERT_VALIDATION_FULL, LdapConnectorConstants.CERT_VALIDATION_SKIP }, Category = ConnectedSystemSettingCategory.Connectivity },
            new() { Name = _settingConnectionTimeout, Required = true, Description = "How long to wait, in seconds, before giving up on trying to connect", DefaultIntValue = 10, Category = ConnectedSystemSettingCategory.Connectivity, Type = ConnectedSystemSettingType.Integer },

            new() { Name = "Credentials", Category = ConnectedSystemSettingCategory.Connectivity, Type = ConnectedSystemSettingType.Heading },
            new() { Name = _settingUsername, Required = true, Description = "What's the username for the service account you want to use to connect to the directory service using? i.e. corp\\svc-jim-adc", Category = ConnectedSystemSettingCategory.Connectivity, Type = ConnectedSystemSettingType.String  },
            new() { Name = _settingPassword, Required = true, Description = "What's the password for the service account you want to use to connect to the directory service with?", Category = ConnectedSystemSettingCategory.Connectivity, Type = ConnectedSystemSettingType.StringEncrypted },
            new() { Name = _settingAuthType, Required = true, Description = "What type of authentication is required for this credential?", Category = ConnectedSystemSettingCategory.Connectivity, Type = ConnectedSystemSettingType.DropDown, DropDownValues = new() { LdapConnectorConstants.SETTING_AUTH_TYPE_SIMPLE, LdapConnectorConstants.SETTING_AUTH_TYPE_NTLM }},

            new() { Name = "Import Settings", Category = ConnectedSystemSettingCategory.General, Type = ConnectedSystemSettingType.Heading },
            new() { Name = _settingSearchTimeout, Required = false, Description = "Maximum time in seconds to wait for LDAP search results. Default is 300 (5 minutes).", DefaultIntValue = 300, Category = ConnectedSystemSettingCategory.General, Type = ConnectedSystemSettingType.Integer },
            new() { Name = _settingImportConcurrency, Required = false, Description = "Maximum number of parallel LDAP connections used during full imports from OpenLDAP and Generic directories. Each connection handles one container and object type combination independently, avoiding RFC 2696 paging cookie limitations. Not used for Active Directory. Default is 4. Recommended range: 2-8.", DefaultIntValue = LdapConnectorConstants.DEFAULT_IMPORT_CONCURRENCY, Category = ConnectedSystemSettingCategory.General, Type = ConnectedSystemSettingType.Integer },

            new() { Name = "Retry Settings", Category = ConnectedSystemSettingCategory.General, Type = ConnectedSystemSettingType.Heading },
            new() { Name = _settingMaxRetries, Required = false, Description = "Maximum number of retry attempts for transient failures. Default is 3.", DefaultIntValue = LdapConnectorConstants.DEFAULT_MAX_RETRIES, Category = ConnectedSystemSettingCategory.General, Type = ConnectedSystemSettingType.Integer },
            new() { Name = _settingRetryDelay, Required = false, Description = "Initial delay between retries in milliseconds. Uses exponential backoff. Default is 1000ms.", DefaultIntValue = LdapConnectorConstants.DEFAULT_RETRY_DELAY_MS, Category = ConnectedSystemSettingCategory.General, Type = ConnectedSystemSettingType.Integer },

            new() { Name = "Schema Discovery", Category = ConnectedSystemSettingCategory.General, Type = ConnectedSystemSettingType.Heading },
            new() { Name = _settingIncludeAuxiliaryClasses, Description = "When enabled, auxiliary object classes are included in schema discovery alongside structural classes. Enable this if you need to import or export objects whose primary class is declared as auxiliary in the directory schema.", DefaultCheckboxValue = false, Category = ConnectedSystemSettingCategory.General, Type = ConnectedSystemSettingType.CheckBox },

            new() { Name = "Container Provisioning", Category = ConnectedSystemSettingCategory.General, Type = ConnectedSystemSettingType.Heading },
            new() { Name = _settingCreateContainersAsNeeded, Description = "i.e. create OUs as needed when provisioning new objects.", DefaultCheckboxValue = false, Category = ConnectedSystemSettingCategory.General, Type = ConnectedSystemSettingType.CheckBox },

            new() { Name = "Hierarchy Settings", Category = ConnectedSystemSettingCategory.General, Type = ConnectedSystemSettingType.Heading },
            new() { Name = _settingSkipHiddenPartitions, Description = "Skip hidden partitions (Configuration, Schema, DNS zones) when refreshing hierarchy. Improves performance significantly.", DefaultCheckboxValue = true, Category = ConnectedSystemSettingCategory.General, Type = ConnectedSystemSettingType.CheckBox },

            // Export settings
            new() { Name = "Export Settings", Category = ConnectedSystemSettingCategory.Export, Type = ConnectedSystemSettingType.Heading },
            new() { Name = _settingDeleteBehaviour, Required = false, Description = "How to handle object deletions.", Type = ConnectedSystemSettingType.DropDown, DropDownValues = new() { LdapConnectorConstants.DELETE_BEHAVIOUR_DELETE, LdapConnectorConstants.DELETE_BEHAVIOUR_DISABLE }, Category = ConnectedSystemSettingCategory.Export },
            new() { Name = _settingDisableAttribute, Required = false, RequiredWhenSetting = _settingDeleteBehaviour, RequiredWhenValue = LdapConnectorConstants.DELETE_BEHAVIOUR_DISABLE, Description = "Attribute to set when disabling objects (e.g., userAccountControl for AD). Only used when Delete Behaviour is 'Disable'.", DefaultStringValue = "userAccountControl", Category = ConnectedSystemSettingCategory.Export, Type = ConnectedSystemSettingType.String },
            new() { Name = _settingExportConcurrency, Required = false, Description = "Maximum number of concurrent LDAP operations during export. Higher values improve throughput but increase load on the target directory. Default is 4. Recommended range: 2-8. Values above 8 show diminishing returns and may overwhelm the directory server.", DefaultIntValue = LdapConnectorConstants.DEFAULT_EXPORT_CONCURRENCY, Category = ConnectedSystemSettingCategory.Export, Type = ConnectedSystemSettingType.Integer },
            new() { Name = _settingModifyBatchSize, Required = false, Description = "Maximum number of values per multi-valued attribute modification in a single LDAP request. When adding or removing many values from a multi-valued attribute (e.g., group members), changes are split into batches of this size. Lower values improve compatibility with constrained LDAP servers; higher values improve throughput. Default is 100. Recommended range: 50-500.", DefaultIntValue = LdapConnectorConstants.DEFAULT_MODIFY_BATCH_SIZE, Category = ConnectedSystemSettingCategory.Export, Type = ConnectedSystemSettingType.Integer },

            new() { Name = "Group Membership", Category = ConnectedSystemSettingCategory.Export, Type = ConnectedSystemSettingType.Heading },
            new() { Name = _settingGroupPlaceholderMemberDn, Required = false, Description = "Placeholder member DN used for group object classes that require at least one member (e.g. groupOfNames). When a group has no real members, this value is added to satisfy the schema constraint. It is automatically filtered out during import. Only applies to non-AD directories. Default: cn=placeholder. If your directory has referential integrity enabled, set this to an existing entry's DN.", DefaultStringValue = LdapConnectorConstants.DEFAULT_GROUP_PLACEHOLDER_MEMBER_DN, Category = ConnectedSystemSettingCategory.Export, Type = ConnectedSystemSettingType.String }
        };
    }

    /// <summary>
    /// Validates LdapConnector setting values using custom business logic.
    /// </summary>
    public List<ConnectorSettingValueValidationResult> ValidateSettingValues(List<ConnectedSystemSettingValue> settingValues, ILogger logger)
    {
        logger.Verbose($"ValidateSettingValues() called for {Name}");
        var response = new List<ConnectorSettingValueValidationResult>();

        // generic required, required-group and required-when validation is handled centrally by ConnectorSettingValidator
        // (invoked by the application layer before this method); only LDAP-specific rules live here.

        // validate that we can connect to the directory service with the supplied setting credentials
        var connectivityTestResult = TestDirectoryConnectivity(settingValues, logger);
        if (!connectivityTestResult.IsValid)
            response.Add(connectivityTestResult);

        return response;
    }
    #endregion

    #region IConnectorSchema members
    public async Task<ConnectorSchema> GetSchemaAsync(List<ConnectedSystemSettingValue> settingValues, ILogger logger)
    {
        OpenImportConnection(settingValues, logger);
        if (_connection == null)
            throw new Exception("No connection available to get schema with");

        var includeAuxiliaryClasses = settingValues.SingleOrDefault(q => q.Setting.Name == _settingIncludeAuxiliaryClasses)?.CheckboxValue ?? false;

        var rootDse = LdapConnectorUtilities.GetBasicRootDseInformation(_connection, logger);

        // Auto-tune settings based on the detected directory type.
        // This modifies setting values in-place; the application layer persists
        // the Connected System after schema import, saving any changes.
        AutoTuneExportConcurrency(settingValues, rootDse, logger);

        var ldapConnectorSchema = new LdapConnectorSchema(_connection, logger, rootDse, includeAuxiliaryClasses);
        var schema = await ldapConnectorSchema.GetSchemaAsync();
        CloseImportConnection();
        return schema;
    }
    #endregion

    #region Auto-tuning
    /// <summary>
    /// Auto-tunes export concurrency based on the detected directory type, but only if the
    /// administrator has not manually changed the value from the default. This respects
    /// intentional admin overrides while optimising performance for the specific directory.
    /// </summary>
    internal static void AutoTuneExportConcurrency(
        List<ConnectedSystemSettingValue> settingValues,
        LdapConnectorRootDse rootDse,
        ILogger logger)
    {
        var exportConcurrencySetting = settingValues
            .FirstOrDefault(s => s.Setting.Name == "Export Concurrency");

        if (exportConcurrencySetting == null)
            return;

        var currentValue = exportConcurrencySetting.IntValue
            ?? LdapConnectorConstants.DEFAULT_EXPORT_CONCURRENCY;

        // Only auto-tune if the current value matches the default.
        // If an admin has manually changed it, respect their choice.
        if (currentValue != LdapConnectorConstants.DEFAULT_EXPORT_CONCURRENCY)
        {
            logger.Debug(
                "Export Concurrency is {CurrentValue} (manually configured), skipping auto-tune",
                currentValue);
            return;
        }

        var recommended = rootDse.RecommendedExportConcurrency;
        if (recommended == currentValue)
            return;

        logger.Information(
            "Auto-tuning Export Concurrency from {OldValue} to {NewValue} for directory type {DirectoryType}",
            currentValue, recommended, rootDse.DirectoryType);

        exportConcurrencySetting.IntValue = recommended;
    }
    #endregion

    #region IConnectorPartitions members
    public async Task<List<ConnectorPartition>> GetPartitionsAsync(List<ConnectedSystemSettingValue> settingValues, ILogger logger)
    {
        OpenImportConnection(settingValues, logger);
        if (_connection == null)
            throw new Exception("No connection available to get partitions with");

        var skipHiddenPartitions = settingValues.SingleOrDefault(q => q.Setting.Name == _settingSkipHiddenPartitions)?.CheckboxValue ?? true;

        // Detect directory type so partition discovery can use the appropriate mechanism
        var rootDse = LdapConnectorUtilities.GetBasicRootDseInformation(_connection, logger);

        var ldapConnectorPartitions = new LdapConnectorPartitions(_connection, logger, rootDse.DirectoryType);
        var partitions = await ldapConnectorPartitions.GetPartitionsAsync(skipHiddenPartitions);
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

        // Decrypt the password if credential protection is available
        // If not available or password is plain text, it will be returned as-is
        var decryptedPassword = _credentialProtection?.Unprotect(password.StringEncryptedValue) ?? password.StringEncryptedValue;
        var credential = new NetworkCredential(username.StringValue, decryptedPassword);

        // allow the user to specify what type of authentication to perform against the supplied credential.
        var authTypeSettingValueString = authTypeSettingValue.StringValue;
        var authTypeEnumValue = AuthType.Anonymous;
        if (authTypeSettingValueString == LdapConnectorConstants.SETTING_AUTH_TYPE_SIMPLE)
            authTypeEnumValue = AuthType.Basic;
        else if (authTypeSettingValueString == LdapConnectorConstants.SETTING_AUTH_TYPE_NTLM)
            authTypeEnumValue = AuthType.Ntlm;

        // Build a reusable connection factory so LdapConnectorImport can create additional
        // connections for parallel imports (one connection per container+objectType combo).
        // Captured values are immutable for the duration of the import session.
        _connectionFactory = () => CreateConnection(identifier, credential, authTypeEnumValue,
            TimeSpan.FromSeconds(timeoutSeconds.IntValue.Value), useSsl, skipCertValidation, logger);

        // Execute connection with retry logic
        ExecuteWithRetry(() =>
        {
            _connection = _connectionFactory();
        }, maxRetries, retryDelayMs, logger);
    }

    /// <summary>
    /// Creates a new bound LdapConnection with the specified parameters.
    /// Used both for the primary import connection and for parallel import connections
    /// in OpenLDAP/Generic directories where each paged search needs its own connection.
    /// </summary>
    private LdapConnection CreateConnection(
        LdapDirectoryIdentifier identifier,
        NetworkCredential credential,
        AuthType authType,
        TimeSpan timeout,
        bool useSsl,
        bool skipCertValidation,
        ILogger logger)
    {
        var connection = new LdapConnection(identifier, credential, authType);
        connection.SessionOptions.ProtocolVersion = 3;
        connection.Timeout = timeout;

        // Configure LDAPS if enabled
        if (useSsl)
        {
            connection.SessionOptions.SecureSocketLayer = true;

            if (skipCertValidation)
            {
                logger.Warning("Certificate validation is disabled. This is not recommended for production environments.");
                // On Linux, setting VerifyServerCertificate can fail. Use LDAPTLS_REQCERT=never
                // environment variable instead. On Windows, set the callback directly.
                if (OperatingSystem.IsWindows())
                {
                    connection.SessionOptions.VerifyServerCertificate = (_, _) => true;
                }
                else
                {
                    logger.Debug("Skipping VerifyServerCertificate callback on Linux - using LDAPTLS_REQCERT environment variable");
                }
            }
            else if (_trustedCertificates != null && _trustedCertificates.Count > 0)
            {
                // Full validation with JIM certificates supplementing system store
                connection.SessionOptions.VerifyServerCertificate = ValidateServerCertificate;
            }
            // else: use system default validation only
        }

        connection.Bind();
        return connection;
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

        if (_connection == null)
            throw new InvalidOperationException("Must call OpenImportConnection() before ImportAsync()!");

        // needs to filter by partitions
        // needs to filter by object types
        // needs to filter by attributes
        // needs to be able to stop processing at convenient points if cancellation has been requested

        var importConcurrency = connectedSystem.SettingValues
            .SingleOrDefault(s => s.Setting.Name == _settingImportConcurrency)?.IntValue
            ?? LdapConnectorConstants.DEFAULT_IMPORT_CONCURRENCY;

        var import = new LdapConnectorImport(connectedSystem, runProfile, _connection, _connectionFactory, importConcurrency, paginationTokens, persistedConnectorData, logger, cancellationToken);

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

        // Detect directory type for export operations (external ID fetching, etc.)
        if (_connection != null)
        {
            var rootDse = LdapConnectorUtilities.GetBasicRootDseInformation(_connection, Log.Logger);
            _directoryType = rootDse.DirectoryType;
        }
    }

    public Task<List<ConnectedSystemExportResult>> ExportAsync(IList<PendingExport> pendingExports, CancellationToken cancellationToken)
    {
        if (_connection == null)
            throw new InvalidOperationException("Must call OpenExportConnection() before ExportAsync()!");

        if (_exportSettings == null)
            throw new InvalidOperationException("Export settings not available. Call OpenExportConnection() first.");

        var concurrency = _exportSettings
            .FirstOrDefault(s => s.Setting.Name == _settingExportConcurrency)?.IntValue
            ?? LdapConnectorConstants.DEFAULT_EXPORT_CONCURRENCY;

        var modifyBatchSize = _exportSettings
            .FirstOrDefault(s => s.Setting.Name == _settingModifyBatchSize)?.IntValue
            ?? LdapConnectorConstants.DEFAULT_MODIFY_BATCH_SIZE;

        var placeholderMemberDn = _exportSettings
            .FirstOrDefault(s => s.Setting.Name == _settingGroupPlaceholderMemberDn)?.StringValue
            ?? LdapConnectorConstants.DEFAULT_GROUP_PLACEHOLDER_MEMBER_DN;

        var executor = new LdapOperationExecutor(_connection);
        _currentExport = new LdapConnectorExport(executor, _exportSettings, Log.Logger, concurrency, modifyBatchSize, _directoryType, placeholderMemberDn);
        return _currentExport.ExecuteAsync(pendingExports, cancellationToken);
    }

    public void CloseExportConnection()
    {
        _exportSettings = null;
        _currentExport = null;
        CloseImportConnection();
    }
    #endregion

    #region IConnectorRecommendedExportParallelism members
    /// <summary>
    /// The Export Concurrency value at or above which the target is treated as a capable
    /// directory for batch-parallelism purposes. The auto-tune only sets 16 (well above this)
    /// for Active Directory and OpenLDAP; Samba AD and Generic directories stay at the
    /// default of 4.
    /// </summary>
    internal const int CAPABLE_DIRECTORY_CONCURRENCY_THRESHOLD = 8;

    /// <summary>
    /// The deliberately conservative batch-parallelism recommendation for capable directories.
    /// </summary>
    internal const int RECOMMENDED_EXPORT_PARALLELISM = 2;

    /// <summary>
    /// Recommends export batch parallelism for this Connected System (issue #985d).
    ///
    /// The two knobs MULTIPLY: each parallel batch pipeline gets its own connector instance,
    /// and each instance runs its own Export Concurrency concurrent LDAP operations (see
    /// <see cref="ExportAsync"/>), so total in-flight operations = parallelism x per-instance
    /// concurrency. Recommending anything near Export Concurrency itself would square the load
    /// (16 x 16 = 256 in-flight operations, against a setting whose own description warns that
    /// values above 8 may overwhelm the directory), so the recommendation is a flat, mild 2:
    /// with an auto-tuned concurrency of 16 that is 2 x 16 = 32 in-flight operations, a safe
    /// default.
    ///
    /// The directory type is not persisted anywhere readable without opening a connection
    /// (which this method must not do), so Active Directory cannot be distinguished from
    /// OpenLDAP here; OpenLDAP's mdb backend is single-writer and gains little from batch
    /// parallelism, a further reason the value is deliberately conservative. An Export
    /// Concurrency of 8 or above is used as the capable-directory signal (the auto-tune only
    /// sets 16, for Active Directory and OpenLDAP); below that, no recommendation is made and
    /// the resolver falls back to sequential. Issue #845 (connector-agnostic classification
    /// storage) is the future enabler of a genuinely per-directory-type recommendation.
    /// </summary>
    public int? GetRecommendedExportParallelism(List<ConnectedSystemSettingValue> settingValues)
    {
        var exportConcurrency = settingValues
            .FirstOrDefault(s => s.Setting.Name == _settingExportConcurrency)?.IntValue;

        return exportConcurrency >= CAPABLE_DIRECTORY_CONCURRENCY_THRESHOLD
            ? RECOMMENDED_EXPORT_PARALLELISM
            : null;
    }
    #endregion

    #region IConnectorContainerCreation members
    /// <summary>
    /// Gets the list of container external IDs (DNs) that were created during the current export session.
    /// </summary>
    public IReadOnlyList<string> CreatedContainerExternalIds =>
        _currentExport?.CreatedContainerExternalIds ?? Array.Empty<string>();

    /// <summary>
    /// Verifies that a container exists in LDAP using a lightweight base-scope search.
    /// </summary>
    /// <param name="containerExternalId">The container DN to verify.</param>
    /// <returns>True if the container exists, false otherwise.</returns>
    public async Task<bool> VerifyContainerExistsAsync(string containerExternalId)
    {
        if (_connection == null)
            throw new InvalidOperationException("No connection available. Call OpenExportConnection() first.");

        return await Task.Run(() =>
        {
            try
            {
                // Simple base-scope search to check if the DN exists
                var request = new SearchRequest(
                    containerExternalId,
                    "(objectClass=*)",
                    SearchScope.Base);
                request.Attributes.Add("objectClass"); // Request minimal attribute

                var response = (SearchResponse)_connection.SendRequest(request);
                return response.Entries.Count > 0;
            }
            catch (DirectoryOperationException ex) when (ex.Response?.ResultCode == ResultCode.NoSuchObject)
            {
                // Container doesn't exist
                return false;
            }
            catch (LdapException ex) when (ex.ErrorCode == 32) // LDAP_NO_SUCH_OBJECT
            {
                return false;
            }
        });
    }

    /// <summary>
    /// Gets the parent container's DN from a child container's DN.
    /// </summary>
    /// <param name="containerExternalId">The child container's DN.</param>
    /// <returns>The parent container's DN, or null if at root level.</returns>
    public string? GetParentContainerExternalId(string containerExternalId)
    {
        if (string.IsNullOrEmpty(containerExternalId))
            return null;

        // Split off the leaf RDN (honouring escaped/quoted separators) to get the parent DN; null at the root.
        return LdapConnectorUtilities.ParseDistinguishedName(containerExternalId).ParentDn;
    }

    /// <summary>
    /// Extracts a human-readable display name from a container's DN.
    /// </summary>
    /// <param name="containerExternalId">The container's DN.</param>
    /// <returns>The container name (e.g., "Sales" from "OU=Sales,DC=example,DC=com").</returns>
    public string GetContainerDisplayName(string containerExternalId)
    {
        if (string.IsNullOrEmpty(containerExternalId))
            return string.Empty;

        // The display name is the (unescaped) value of the leaf RDN's first component, e.g. "Sales" from "OU=Sales".
        if (LdapDistinguishedName.TryParse(containerExternalId, out var parsedDn) && parsedDn.LeafRdn.Components.Count > 0)
            return parsedDn.LeafRdn.Components[0].Value;

        return containerExternalId;
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

    #region IConnectorCredentialAware members
    /// <summary>
    /// Sets the credential protection service for decrypting stored passwords.
    /// </summary>
    public void SetCredentialProtection(ICredentialProtection? credentialProtection)
    {
        _credentialProtection = credentialProtection;
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