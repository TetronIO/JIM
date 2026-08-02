// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Connectors;
using JIM.Models.Core;
using JIM.Models.Exceptions;
using JIM.Models.Interfaces;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using JIM.Utilities;
using Serilog;
using System.DirectoryServices.Protocols;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
namespace JIM.Connectors.LDAP;

public class LdapConnector : IConnector, IConnectorCapabilities, IConnectorDetectedCapabilities, IConnectorSettings, IConnectorSchema, IConnectorPartitions, IConnectorImportUsingCalls, IConnectorExportUsingCalls, IConnectorPasswordManagement, IConnectorPasswordPolicyDiscovery, IConnectorCertificateAware, IConnectorCredentialAware, IConnectorContainerCreation, IConnectorRecommendedExportParallelism, IConnectorPhases, IConnectorSecureEndpoint, IDisposable
{
    private LdapConnection? _connection;
    private Func<LdapConnection>? _connectionFactory;
    private LdapDirectoryType _directoryType = LdapDirectoryType.Generic;
    private bool _disposed;
    private ICertificateProvider? _certificateProvider;
    private ICredentialProtection? _credentialProtection;
    private LdapTrustedCertificateDirectory? _trustDirectory;
    private LdapConnectorExport? _currentExport;

    /// <summary>
    /// The persisted connector state replayed by JIM at connection open (issue #230), including any
    /// pinned domain controller. Read by <see cref="OpenImportConnection"/> to resolve the effective
    /// server, and re-read by <see cref="CloseImportConnection"/>/<see cref="CloseExportConnection"/> as
    /// the base to merge a pin update into, so any other persisted field (USN/changelog/accesslog
    /// watermarks, invocationId) survives untouched.
    /// </summary>
    private string? _persistedConnectorData;

    /// <summary>
    /// Where the server used by the most recent <see cref="OpenImportConnection"/> call came from (issue
    /// #230 Phase 2). Only a connection resolved via <see cref="LdapServerResolutionSource.Pinned"/> can
    /// have its pin invalidated on failure; the other two sources are administrator-supplied or unpinned.
    /// </summary>
    private LdapServerResolutionSource? _lastResolutionSource;

    /// <summary>
    /// Set when a connection opened via a pinned domain controller fails after retries are exhausted.
    /// Read (and cleared) by <see cref="CloseImportConnection"/>, which returns persisted connector data
    /// with the pin removed so the next run re-discovers and re-pins via Host.
    /// </summary>
    private bool _pinInvalidatedByConnectionFailure;

    /// <summary>
    /// A newly discovered domain controller to pin, captured by <see cref="OpenExportConnection"/> when an
    /// AD-family directory has no Preferred Domain Controller configured and no pin yet exists. Read (and
    /// cleared) by <see cref="CloseExportConnection"/>. Import establishes/self-heals its own pin through
    /// the ordinary import-result persistence channel, so this field is export-only.
    /// </summary>
    private string? _exportDiscoveredPinnedServer;

    /// <summary>
    /// The directory type detected alongside <see cref="_exportDiscoveredPinnedServer"/>, used as the
    /// fallback directory type if no previous persisted connector data exists to merge the new pin into.
    /// </summary>
    private LdapDirectoryType? _exportDiscoveredDirectoryTypeForPin;

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

    public bool SupportsPasswordSet => true;

    public bool SupportsPasswordPolicyDiscovery => true;

    // Attribute names in an LDAP directory come from the LDAP/AD vocabulary, so the Attribute Flow editor
    // can show the LDAP counterpart of each Metaverse Attribute. Advisory only; never read at sync time.
    public AttributeStandard SchemaStandard => AttributeStandard.Ldap;
    #endregion

    #region IConnectorSettings members
    // variablising the names to reduce repetition later on, i.e. when we go to consume setting values JIM passes in, or when validating administrator-supplied settings
    private readonly string _settingDirectoryServer = "Host";
    private readonly string _settingPreferredDomainController = "Preferred Domain Controller";
    private readonly string _settingDirectoryServerPort = "Port";
    private readonly string _settingUseSecureConnection = "Use Secure Connection (LDAPS)?";
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
            new() { Name = _settingPreferredDomainController, Required = false, Description = "Applies to Active Directory and Samba AD. A specific domain controller FQDN to always connect to. When left blank, JIM automatically discovers and pins the domain controller it reaches via the Host value. For LDAPS, use a name present in the domain controller's certificate.", Category = ConnectedSystemSettingCategory.Connectivity, Type = ConnectedSystemSettingType.String },
            new() { Name = _settingDirectoryServerPort, Required = true, Description = "The port to connect to the directory service on. Use 389 for LDAP or 636 for LDAPS.", DefaultIntValue = LdapConnectorConstants.DEFAULT_LDAP_PORT, Category = ConnectedSystemSettingCategory.Connectivity, Type = ConnectedSystemSettingType.Integer },
            new() { Name = _settingUseSecureConnection, Description = "Enable LDAPS (SSL/TLS) for encrypted communication. Requires appropriate port (typically 636).", DefaultCheckboxValue = false, Category = ConnectedSystemSettingCategory.Connectivity, Type = ConnectedSystemSettingType.CheckBox },
            new() { Name = _settingConnectionTimeout, Required = true, Description = "How long to wait, in seconds, before giving up on trying to connect", DefaultIntValue = LdapConnectorConstants.DEFAULT_CONNECTION_TIMEOUT_SECONDS, Category = ConnectedSystemSettingCategory.Connectivity, Type = ConnectedSystemSettingType.Integer },

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
            new() { Name = _settingModifyBatchSize, Required = false, Description = "Maximum number of values per multi-valued attribute modification in a single LDAP request. When adding or removing many values from a multi-valued attribute (e.g., group members), changes are split into batches of this size. Lower values improve compatibility with constrained LDAP servers; higher values improve throughput, especially for very large groups. Default is 1000. Recommended range: 100-2000.", DefaultIntValue = LdapConnectorConstants.DEFAULT_MODIFY_BATCH_SIZE, Category = ConnectedSystemSettingCategory.Export, Type = ConnectedSystemSettingType.Integer },

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
        // No persisted connector state applies to a schema-only connection.
        OpenImportConnection(settingValues, null, logger);

        try
        {
            if (_connection == null)
                throw new InvalidOperationException("No connection available to get schema with");

            var includeAuxiliaryClasses = settingValues.SingleOrDefault(q => q.Setting.Name == _settingIncludeAuxiliaryClasses)?.CheckboxValue ?? false;

            var rootDse = LdapConnectorUtilities.GetBasicRootDseInformation(_connection, logger);

            // Auto-tune settings based on the detected directory type.
            // This modifies setting values in-place; the application layer persists
            // the Connected System after schema import, saving any changes.
            AutoTuneExportConcurrency(settingValues, rootDse, logger);

            var ldapConnectorSchema = new LdapConnectorSchema(_connection, logger, rootDse, includeAuxiliaryClasses);
            return await ldapConnectorSchema.GetSchemaAsync();
        }
        finally
        {
            CloseImportConnection();
        }
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
        // No persisted connector state applies to a partition-discovery-only connection.
        OpenImportConnection(settingValues, null, logger);

        try
        {
            if (_connection == null)
                throw new InvalidOperationException("No connection available to get partitions with");

            var skipHiddenPartitions = settingValues.SingleOrDefault(q => q.Setting.Name == _settingSkipHiddenPartitions)?.CheckboxValue ?? true;

            // Detect directory type so partition discovery can use the appropriate mechanism
            var rootDse = LdapConnectorUtilities.GetBasicRootDseInformation(_connection, logger);

            var ldapConnectorPartitions = new LdapConnectorPartitions(_connection, logger, rootDse.DirectoryType);
            return await ldapConnectorPartitions.GetPartitionsAsync(skipHiddenPartitions);
        }
        finally
        {
            CloseImportConnection();
        }
    }
    #endregion

    #region IConnectorDetectedCapabilities members
    /// <summary>
    /// Maps the rootDSE facts JIM already persists between synchronisation runs (issue #230's
    /// <see cref="LdapConnectorRootDse"/>) to human-readable capability facts for display on the Connected
    /// System details page. Tolerates null/empty/corrupt persisted data (returns an empty list) and old
    /// baselines missing newer properties (those simply deserialise to their defaults and, where the
    /// property is optional, are omitted below rather than shown blank).
    /// </summary>
    public List<ConnectorCapability> GetDetectedCapabilities(string? persistedConnectorData, ILogger logger)
    {
        if (string.IsNullOrEmpty(persistedConnectorData))
            return [];

        LdapConnectorRootDse? rootDse;
        try
        {
            rootDse = JsonSerializer.Deserialize<LdapConnectorRootDse>(persistedConnectorData);
        }
        catch (JsonException ex)
        {
            logger.Warning(ex, "GetDetectedCapabilities: Failed to deserialise persisted connector data. Returning no detected capabilities.");
            return [];
        }

        if (rootDse == null)
            return [];

        var capabilities = new List<ConnectorCapability>
        {
            new() { Name = "Directory Type", Value = DescribeDirectoryTypeForCapabilities(rootDse.DirectoryType) }
        };

        if (!string.IsNullOrEmpty(rootDse.VendorName))
            capabilities.Add(new ConnectorCapability { Name = "Vendor", Value = rootDse.VendorName });

        if (!string.IsNullOrEmpty(rootDse.DnsHostName))
            capabilities.Add(new ConnectorCapability { Name = "DNS Host Name", Value = rootDse.DnsHostName });

        // Boolean fact: always shown, unlike the optional string facts above, because "Not Supported" is
        // itself useful information and there is no "not yet known" state distinct from it here.
        capabilities.Add(new ConnectorCapability { Name = "Paging", Value = rootDse.SupportsPaging ? "Supported" : "Not Supported" });

        if (!string.IsNullOrEmpty(rootDse.PinnedDirectoryServer))
            capabilities.Add(new ConnectorCapability { Name = "Pinned Directory Server", Value = rootDse.PinnedDirectoryServer });

        if (rootDse.InvocationId.HasValue)
            capabilities.Add(new ConnectorCapability { Name = "Invocation Id", Value = rootDse.InvocationId.Value.ToString() });

        return capabilities;
    }

    private static string DescribeDirectoryTypeForCapabilities(LdapDirectoryType directoryType) => directoryType switch
    {
        LdapDirectoryType.ActiveDirectory => "Active Directory",
        LdapDirectoryType.SambaAD => "Samba AD",
        LdapDirectoryType.OpenLDAP => "OpenLDAP",
        LdapDirectoryType.Generic => "Generic",
        _ => "Generic"
    };
    #endregion

    #region IConnectorImportUsingCalls members
    public void OpenImportConnection(List<ConnectedSystemSettingValue> settingValues, string? persistedConnectorData, ILogger logger)
    {
        // Replayed by CloseImportConnection/CloseExportConnection as the base to merge a pin update into
        // (issue #230), and consulted below to resolve the effective server for this connection.
        _persistedConnectorData = persistedConnectorData;

        logger.Verbose("OpenImportConnection() called");
        var plan = BuildConnectionPlan(settingValues, logger);
        _connectionFactory = plan.Factory;
        _connection = OpenConnection(plan, logger);
    }

    /// <summary>
    /// How to open a bound connection to the directory, derived from the Connected System's settings.
    /// <para>
    /// Separated from opening one so that a caller needing its own connection (the password channel, which is
    /// bound separately because it has stricter requirements than import and export do) can build one without
    /// replacing the connection an import or export session is already using.
    /// </para>
    /// </summary>
    private sealed record ConnectionPlan(
        Func<LdapConnection> Factory,
        int MaxRetries,
        int RetryDelayMs,
        List<ConnectedSystemSettingValue> SettingValues,
        string EffectiveServer,
        LdapServerResolutionSource ResolutionSource);

    private ConnectionPlan BuildConnectionPlan(List<ConnectedSystemSettingValue> settingValues, ILogger logger)
    {
        var directoryServer = settingValues.SingleOrDefault(q => q.Setting.Name == _settingDirectoryServer);
        var preferredDomainControllerSetting = settingValues.SingleOrDefault(q => q.Setting.Name == _settingPreferredDomainController);
        var directoryServerPort = settingValues.SingleOrDefault(q => q.Setting.Name == _settingDirectoryServerPort);
        var timeoutSeconds = settingValues.SingleOrDefault(q => q.Setting.Name == _settingConnectionTimeout);
        var username = settingValues.SingleOrDefault(q => q.Setting.Name == _settingUsername);
        var password = settingValues.SingleOrDefault(q => q.Setting.Name == _settingPassword);
        var authTypeSettingValue = settingValues.SingleOrDefault(q => q.Setting.Name == _settingAuthType);
        var useSecureConnection = settingValues.SingleOrDefault(q => q.Setting.Name == _settingUseSecureConnection);
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
        var maxRetries = maxRetriesSetting?.IntValue ?? LdapConnectorConstants.DEFAULT_MAX_RETRIES;
        var retryDelayMs = retryDelaySetting?.IntValue ?? LdapConnectorConstants.DEFAULT_RETRY_DELAY_MS;

        // Hoisted once for the log line and the identifier below; the guard above has already thrown when
        // this setting has no value, and the null-forgiving operator says so to the analyser, which does
        // not carry null-state out of a pattern guard (same rationale as connectionTimeout further down).
        var directoryServerPortValue = directoryServerPort.IntValue!.Value;

        // Resolve which server this plan actually opens against (issue #230 Phase 2): the Preferred
        // Domain Controller setting when configured, else a domain controller pinned in the persisted
        // connector state most recently replayed to this connector instance, else the configured Host.
        // Living here rather than in OpenImportConnection means every plan consumer (import, export, the
        // password channel and the preflight) resolves to the same server, and the factory below hands
        // that same server to every parallel connection built from this plan.
        var (effectiveServer, resolutionSource) = LdapConnectorUtilities.ResolveEffectiveServer(
            preferredDomainControllerSetting?.StringValue, _persistedConnectorData, directoryServer.StringValue, logger);
        _lastResolutionSource = resolutionSource;

        logger.Debug("BuildConnectionPlan() Preparing to connect to '{Server}' on port '{Port}' with username '{Username}' via auth type {AuthType}. SSL: {UseSsl}",
            LogSanitiser.Sanitise(effectiveServer), directoryServerPortValue,
            LogSanitiser.Sanitise(username.StringValue), LogSanitiser.Sanitise(authTypeSettingValue.StringValue), useSsl);

        // Supply the certificates from the JIM certificate store as additional trust anchors for LDAPS. The platform
        // LDAP client still performs the validation itself, so the chain, the validity period and the certificate's
        // name are all checked against the Directory Server value above.
        if (useSsl && _certificateProvider != null)
            PrepareTrustedCertificateDirectory(logger);

        var identifier = new LdapDirectoryIdentifier(effectiveServer, directoryServerPortValue);

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

        // Resolved once, and reused by the connection factory below and the failure path further down. The guard
        // above has already thrown if this setting has no value; the null-forgiving operator says so to the
        // analyser, which does not carry null-state out of a pattern guard. A second check here would be a
        // redundant condition, which is its own code-quality finding.
        var connectionTimeout = TimeSpan.FromSeconds(timeoutSeconds.IntValue!.Value);

        // Build a reusable connection factory so LdapConnectorImport can create additional
        // connections for parallel imports (one connection per container+objectType combo).
        // Captured values are immutable for the duration of the import session.
        return new ConnectionPlan(
            () => CreateConnection(identifier, credential, authTypeEnumValue, connectionTimeout, useSsl, logger),
            maxRetries,
            retryDelayMs,
            settingValues,
            effectiveServer,
            resolutionSource);
    }

    /// <summary>
    /// Opens a bound connection from a plan, retrying transient failures.
    /// <para>
    /// Shared by every caller that needs a connection (import, the password channel, the preflight) so that all
    /// of them clean up after a failure and all of them get the same account of why it failed. A failure leaves
    /// nothing behind: the trust directory is only of use to a connection that was established, so without this
    /// every refused LDAPS attempt would leave one on disk.
    /// </para>
    /// </summary>
    private LdapConnection OpenConnection(ConnectionPlan plan, ILogger logger)
    {
        LdapConnection? connection = null;
        try
        {
            ExecuteWithRetry(() => { connection = plan.Factory(); }, plan.MaxRetries, plan.RetryDelayMs, logger);
            return connection!;
        }
        catch (LdapException ex)
        {
            _trustDirectory?.Dispose();
            _trustDirectory = null;
            InvalidatePinOnConnectionFailure(plan.ResolutionSource, plan.EffectiveServer, logger);

            // "The LDAP server is unavailable" is what a refused certificate looks like, so before reporting a
            // connectivity failure, go and look at what the server actually presented. No LDAPS check is needed
            // here: ResolveSecureEndpoint returns nothing unless the system is configured for it.
            if (ServerCertificateDiagnosis.Describe(this, plan.SettingValues, _certificateProvider, ex, logger) is { } rejection)
                throw rejection;

            throw;
        }
        catch
        {
            _trustDirectory?.Dispose();
            _trustDirectory = null;
            InvalidatePinOnConnectionFailure(plan.ResolutionSource, plan.EffectiveServer, logger);
            throw;
        }
    }

    /// <summary>
    /// Records that the pin must be invalidated, when the connection just attempted (and about to fail
    /// past retries) was resolved via a pinned domain controller (issue #230 Phase 2). The exception this
    /// wraps is always rethrown unchanged by the caller: there is no mid-run failover, by design (the run
    /// must fail), so this only leaves a note for <see cref="CloseImportConnection"/> to act on. The other
    /// two resolution sources are administrator-supplied or unpinned, so their failures leave no pin state
    /// to touch.
    /// </summary>
    private void InvalidatePinOnConnectionFailure(LdapServerResolutionSource resolutionSource, string effectiveServer, ILogger logger)
    {
        if (resolutionSource != LdapServerResolutionSource.Pinned)
            return;

        logger.Warning("OpenConnection: The connection to the pinned domain controller {Server} failed after exhausting retries. Invalidating the pin; the next run will re-discover and re-pin a domain controller via Host.",
            LogSanitiser.Sanitise(effectiveServer));
        _pinInvalidatedByConnectionFailure = true;
    }

    #region IConnectorSecureEndpoint members

    /// <summary>
    /// The directory server this system's settings connect to over LDAPS, so JIM can look at the certificate that
    /// server presents without any caller naming a host of their own. Resolves the same effective server a
    /// connection would use (Preferred Domain Controller setting, else a pinned domain controller from persisted
    /// connector state replayed to this instance, else Host), so a certificate diagnosis probes the server that
    /// actually refused the connection rather than whatever Host resolves to (issue #230 Phase 2).
    /// </summary>
    public SecureEndpoint? ResolveSecureEndpoint(List<ConnectedSystemSettingValue> settingValues)
    {
        // Nothing encrypted is being attempted, so there is no certificate to look at.
        if (settingValues.SingleOrDefault(q => q.Setting.Name == _settingUseSecureConnection)?.CheckboxValue != true)
            return null;

        var hostSetting = settingValues.SingleOrDefault(q => q.Setting.Name == _settingDirectoryServer)?.StringValue;
        var port = settingValues.SingleOrDefault(q => q.Setting.Name == _settingDirectoryServerPort)?.IntValue;
        if (string.IsNullOrWhiteSpace(hostSetting) || !port.HasValue)
            return null;

        var preferredDomainController = settingValues.SingleOrDefault(q => q.Setting.Name == _settingPreferredDomainController)?.StringValue;
        var (host, _) = LdapConnectorUtilities.ResolveEffectiveServer(preferredDomainController, _persistedConnectorData, hostSetting, Log.Logger);

        var timeoutSeconds = settingValues.SingleOrDefault(q => q.Setting.Name == _settingConnectionTimeout)?.IntValue
            ?? LdapConnectorConstants.DEFAULT_CONNECTION_TIMEOUT_SECONDS;

        return new SecureEndpoint(host, port.Value, TimeSpan.FromSeconds(timeoutSeconds), "directory server", "LDAPS");
    }

    #endregion

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
        ILogger logger)
    {
        var connection = new LdapConnection(identifier, credential, authType);
        connection.SessionOptions.ProtocolVersion = 3;
        connection.Timeout = timeout;

        // Configure LDAPS if enabled
        if (useSsl)
        {
            connection.SessionOptions.SecureSocketLayer = true;

            // The platform guard is a compile-time requirement, not a behavioural choice: both members below are
            // unsupported on Windows. PrepareTrustedCertificateDirectory warns when that costs an administrator
            // anything, so there is nothing further to say here.
            if (_trustDirectory != null && !OperatingSystem.IsWindows())
            {
                // Adds the certificates from the JIM certificate store, alongside the operating system's own bundle,
                // to what this connection will trust. A new TLS session context is mandatory: without it the platform
                // LDAP client accepts the directory silently and carries on using the trust anchors it already had.
                connection.SessionOptions.TrustedCertificatesDirectory = _trustDirectory.DirectoryPath;
                connection.SessionOptions.StartNewTlsSessionContext();
                logger.Debug("LDAPS: supplied the JIM certificate store as additional trust anchors for this connection");
            }
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

    public Task<ConnectedSystemImportResult> ImportAsync(ConnectedSystem connectedSystem, ConnectedSystemRunProfile runProfile, List<ConnectedSystemPaginationToken> paginationTokens, string? persistedConnectorData, ILogger logger, CancellationToken cancellationToken, IConnectorProgress progress)
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

        // Needed so GetRootDseInformation can decide whether to (re-)pin the domain controller it just
        // connected to, or clear a pin left over from a previous configuration (issue #230 Phase 2).
        var preferredDomainController = connectedSystem.SettingValues
            .SingleOrDefault(s => s.Setting.Name == _settingPreferredDomainController)?.StringValue;

        var import = new LdapConnectorImport(connectedSystem, runProfile, _connection, _connectionFactory, importConcurrency, paginationTokens, persistedConnectorData, preferredDomainController, logger, cancellationToken, progress);

        switch (runProfile.RunType)
        {
            case ConnectedSystemRunType.FullImport:
                logger.Debug("ImportAsync: Full Import requested");
                return import.GetFullImportObjectsAsync();
            case ConnectedSystemRunType.DeltaImport:
                logger.Debug("ImportAsync: Delta Import requested");
                return import.GetDeltaImportObjectsAsync();
            case ConnectedSystemRunType.FullSynchronisation:
            case ConnectedSystemRunType.DeltaSynchronisation:
            case ConnectedSystemRunType.Export:
            default:
                throw new InvalidDataException($"Unsupported import run-type: {runProfile.RunType}");
        }
    }

    public string? CloseImportConnection()
    {
        _connection?.Dispose();

        // The trust directory is only read while a TLS session is being established, so it is safe to remove as soon
        // as the connection is closed. A later Open call prepares a fresh one.
        _trustDirectory?.Dispose();
        _trustDirectory = null;

        // Pin invalidation (issue #230 Phase 2): a connection through a pinned domain controller failed
        // past retries in OpenImportConnection. Return the replayed persisted data with the pin removed so
        // the next run resolves via Host, re-discovers a domain controller, and re-pins. Import sessions
        // that reached ImportAsync already carried any pin change through the import result's own
        // PersistedConnectorData, so this only returns non-null for the invalidation case.
        if (!_pinInvalidatedByConnectionFailure)
            return null;

        _pinInvalidatedByConnectionFailure = false;
        return LdapConnectorUtilities.MergePinnedDirectoryServerIntoPersistedData(
            _persistedConnectorData, null, LdapDirectoryType.Generic, Log.Logger);
    }
    #endregion

    #region IConnectorExportUsingCalls members
    private IList<ConnectedSystemSettingValue>? _exportSettings;

    public void OpenExportConnection(IList<ConnectedSystemSettingValue> settings, string? persistedConnectorData)
    {
        _exportSettings = settings;

        // Reuse the same connection logic as import
        OpenImportConnection(settings.ToList(), persistedConnectorData, Log.Logger);

        // Detect directory type for export operations (external ID fetching, etc.)
        if (_connection != null)
        {
            var rootDse = LdapConnectorUtilities.GetBasicRootDseInformation(_connection, Log.Logger);
            _directoryType = rootDse.DirectoryType;

            // Pin creation (issue #230 Phase 2): export does not re-query rootDSE and re-pin on every run
            // the way import self-heals via LdapConnectorImport.GetRootDseInformation; it only needs to
            // establish a pin the first time an AD-family directory has none. _lastResolutionSource is
            // Pinned only when a usable pin already existed, so this only fires when one genuinely does
            // not: no Preferred Domain Controller configured, and either no persisted data, malformed
            // persisted data, or persisted data whose pin is null (for example, a baseline recorded while
            // a Preferred Domain Controller was configured, since cleared).
            var preferredDomainController = settings
                .FirstOrDefault(s => s.Setting.Name == _settingPreferredDomainController)?.StringValue;

            if (rootDse.UseUsnDeltaImport &&
                string.IsNullOrWhiteSpace(preferredDomainController) &&
                _lastResolutionSource != LdapServerResolutionSource.Pinned &&
                !string.IsNullOrEmpty(rootDse.DnsHostName))
            {
                Log.Logger.Information("OpenExportConnection: No pinned domain controller exists for this AD-family directory. Establishing one at {Server}.",
                    LogSanitiser.Sanitise(rootDse.DnsHostName));
                _exportDiscoveredPinnedServer = rootDse.DnsHostName;
                _exportDiscoveredDirectoryTypeForPin = rootDse.DirectoryType;
            }
        }
    }

    public Task<List<ConnectedSystemExportResult>> ExportAsync(IList<PendingExport> pendingExports, CancellationToken cancellationToken, IConnectorProgress progress)
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

        // progress is deliberately not used for export: LDAP export iterates per item, and JIM already
        // reports accurate per-batch counts around this call. The connector's only internal phase
        // (parent container creation) happens per object rather than as a pre-flight step, so emitting
        // from it would replace a moving "N of M" with a message that says less. See
        // engineering/notes/CONNECTOR_SUB_PHASE_PROGRESS.md.
        var executor = new LdapOperationExecutor(_connection);
        _currentExport = new LdapConnectorExport(executor, _exportSettings, Log.Logger, concurrency, modifyBatchSize, _directoryType, placeholderMemberDn);
        return _currentExport.ExecuteAsync(pendingExports, cancellationToken);
    }

    public string? CloseExportConnection()
    {
        _exportSettings = null;
        _currentExport = null;

        // Pin invalidation takes priority: if the connection never succeeded, OpenExportConnection never
        // reached the pin-creation check below either, so the two cases cannot both apply.
        var closeImportResult = CloseImportConnection();
        if (closeImportResult != null)
            return closeImportResult;

        if (_exportDiscoveredPinnedServer == null)
            return null;

        var updated = LdapConnectorUtilities.MergePinnedDirectoryServerIntoPersistedData(
            _persistedConnectorData, _exportDiscoveredPinnedServer,
            _exportDiscoveredDirectoryTypeForPin ?? LdapDirectoryType.Generic, Log.Logger);

        _exportDiscoveredPinnedServer = null;
        _exportDiscoveredDirectoryTypeForPin = null;
        return updated;
    }
    #endregion

    #region IConnectorPasswordPolicyDiscovery members
    /// <summary>
    /// Reads the directory's password policy. Called during schema import, so it opens and closes its own
    /// connection the same way schema discovery does.
    /// </summary>
    public async Task<ConnectedSystemPasswordPolicy?> GetPasswordPolicyAsync(List<ConnectedSystemSettingValue> settings, ILogger logger)
    {
        // No persisted connector state applies to a policy-discovery-only connection.
        OpenImportConnection(settings, null, logger);
        if (_connection == null)
            throw new InvalidOperationException("No connection available to read the password policy with.");

        try
        {
            var rootDse = LdapConnectorUtilities.GetBasicRootDseInformation(_connection, logger);
            var domainRootDn = GetDefaultNamingContext(_connection);

            var policyReader = new LdapConnectorPasswordPolicy(new LdapOperationExecutor(_connection), logger, rootDse.DirectoryType);
            return await policyReader.GetPasswordPolicyAsync(domainRootDn ?? string.Empty);
        }
        finally
        {
            CloseImportConnection();
        }
    }

    /// <summary>
    /// Reads defaultNamingContext from the rootDSE, which is where Active Directory holds its domain-wide
    /// password policy. Directories that are not Active Directory do not publish this, and do not need to: their
    /// policy is not discoverable anyway.
    /// </summary>
    private static string? GetDefaultNamingContext(LdapConnection connection)
    {
        var request = new SearchRequest { Scope = SearchScope.Base };
        request.Attributes.Add("defaultNamingContext");

        var response = (SearchResponse)connection.SendRequest(request);
        if (response.Entries.Count == 0)
            return null;

        var attribute = response.Entries[0].Attributes["defaultNamingContext"];
        return attribute == null || attribute.Count == 0 ? null : attribute[0]?.ToString();
    }
    #endregion

    #region IConnectorPasswordManagement members
    private LdapConnectorPassword? _passwordChannel;

    /// <summary>
    /// The password channel binds its own connection rather than sharing the import and export one.
    /// <para>
    /// Delivering an initial password happens partway through an export session, so borrowing that session's
    /// connection would leave the export using a connection it did not open.
    /// </para>
    /// </summary>
    private LdapConnection? _passwordConnection;

    /// <summary>
    /// All three states are declared because the LDAP Connector serves Active Directory as well as directories
    /// that have no per-entry expiry control. Which of them a given Connected System can actually honour is not
    /// knowable without connecting to it, so a target that cannot honour the chosen state reports a downgrade on
    /// the result rather than the state being withheld here.
    /// </summary>
    public IReadOnlyCollection<PasswordExpiryBehaviour> SupportedExpiryBehaviours { get; } =
    [
        PasswordExpiryBehaviour.RequireChangeAtNextSignIn,
        PasswordExpiryBehaviour.ExpiresAccordingToTargetPolicy,
        PasswordExpiryBehaviour.NeverExpires
    ];

    /// <summary>
    /// Opens the password channel.
    /// <para>
    /// LDAPS is strongly recommended and not required. A password set puts the password on the wire in the clear
    /// at the LDAP layer, so an unencrypted connection exposes it to anyone on the network path, and JIM warns
    /// prominently when the channel opens that way. It is not refused outright because some deployments genuinely
    /// cannot offer TLS on the directory (an isolated or air-gapped network with a directory that does not serve
    /// it), and locking those sites out of password management entirely helps nobody. The choice belongs to the
    /// administrator, who is told plainly what it costs.
    /// </para>
    /// <para>
    /// Active Directory makes its own decision regardless: it refuses a password write unless the connection is
    /// encrypted or the bind is signed and sealed. That refusal surfaces as a classified failure naming
    /// encryption as the fix, rather than being pre-empted here, because a signed and sealed bind is a legitimate
    /// alternative that JIM cannot detect from the settings alone.
    /// </para>
    /// </summary>
    public void OpenPasswordConnection(IList<ConnectedSystemSettingValue> settings)
    {
        var useSecureConnection = settings.SingleOrDefault(q => q.Setting.Name == _settingUseSecureConnection);
        var isConnectionEncrypted = useSecureConnection?.CheckboxValue == true;

        if (!isConnectionEncrypted)
            Log.Warning("OpenPasswordConnection: Passwords will be sent to this Connected System over an UNENCRYPTED connection, " +
                        "where anyone on the network path can read them. Enable the '{Setting}' setting on the Connected System " +
                        "(and set '{PortSetting}' to {LdapsPort} unless the directory listens elsewhere) to protect them.",
                _settingUseSecureConnection, _settingDirectoryServerPort, LdapConnectorConstants.DEFAULT_LDAPS_PORT);

        var plan = BuildConnectionPlan(settings.ToList(), Log.Logger);
        var connection = OpenConnection(plan, Log.Logger);
        _passwordConnection = connection;

        var rootDse = LdapConnectorUtilities.GetBasicRootDseInformation(connection, Log.Logger);
        var directoryType = rootDse.DirectoryType;
        var supportsPasswordModifyExtension = DirectorySupportsPasswordModifyExtension(connection);

        _passwordChannel = new LdapConnectorPassword(new LdapOperationExecutor(connection), Log.Logger, directoryType, supportsPasswordModifyExtension, isConnectionEncrypted);

        Log.Debug("OpenPasswordConnection: Password channel open. DirectoryType={DirectoryType}, PasswordModifyExtensionSupported={Supported}, Encrypted={Encrypted}",
            directoryType, supportsPasswordModifyExtension, isConnectionEncrypted);
    }

    public Task<PasswordSetResult> SetPasswordAsync(ConnectedSystemObject target, string password, PasswordSetOptions options, CancellationToken cancellationToken)
    {
        if (_passwordChannel == null)
            throw new InvalidOperationException("Must call OpenPasswordConnection() before SetPasswordAsync()!");

        ArgumentNullException.ThrowIfNull(target);

        // LDAP addresses an entry by its Distinguished Name, which JIM holds as the secondary external id.
        var distinguishedName = target.SecondaryExternalIdAttributeValue?.ToStringNoName();
        if (string.IsNullOrEmpty(distinguishedName))
            return Task.FromResult(PasswordSetResult.Failed(PasswordSetFailureReason.TargetObjectNotFound,
                "The Connected System Object has no Distinguished Name, so JIM cannot locate it in the directory to set a password on it."));

        return _passwordChannel.SetPasswordAsync(distinguishedName, password, options, cancellationToken);
    }

    public void ClosePasswordConnection()
    {
        _passwordChannel = null;
        _passwordConnection?.Dispose();
        _passwordConnection = null;
    }

    /// <summary>
    /// Runs the non-destructive password channel checks, opening and closing its own connection.
    /// <para>
    /// Nothing here throws for a target that cannot be reached or refuses a read. An administrator running this is
    /// asking what is wrong, so a failure to connect is the answer rather than an error to raise; the checks that
    /// depend on a connection are reported as undetermined rather than quietly dropped, so it stays visible what
    /// JIM would have looked at.
    /// </para>
    /// </summary>
    public async Task<PasswordPreflightResult> RunPasswordPreflightAsync(List<ConnectedSystemSettingValue> settings, IReadOnlyList<string> containerExternalIds, ILogger logger, CancellationToken cancellationToken)
    {
        var useSecureConnection = settings.SingleOrDefault(q => q.Setting.Name == _settingUseSecureConnection);
        var isConnectionEncrypted = useSecureConnection?.CheckboxValue == true;

        LdapConnection connection;
        try
        {
            connection = OpenConnection(BuildConnectionPlan(settings, logger), logger);
        }
        catch (InvalidSettingValuesException ex)
        {
            return CouldNotConnect("This Connected System is missing settings JIM needs in order to connect: " + ex.Message);
        }
        catch (LdapException ex)
        {
            logger.Warning("RunPasswordPreflightAsync: Could not connect to the Connected System: {Message}", LogSanitiser.Sanitise(ex.Message));
            return CouldNotConnect($"JIM could not connect to this Connected System: {ex.Message}");
        }
        catch (DirectoryOperationException ex)
        {
            logger.Warning("RunPasswordPreflightAsync: The directory refused the connection: {Message}", LogSanitiser.Sanitise(ex.Message));
            return CouldNotConnect($"The directory refused JIM's connection: {ex.Message}");
        }

        using (connection)
        {
            // A successful bind does not mean the directory will answer questions about itself. Reading the rootDSE
            // can still be refused or fail, and when it does, every remaining check depends on what it would have
            // said, so there is nothing left to establish. That is a finding to report, not an exception to raise:
            // this method is called by an administrator asking what is wrong.
            LdapConnectorRootDse rootDse;
            bool supportsPasswordModifyExtension;
            string? domainRootDn;
            try
            {
                rootDse = LdapConnectorUtilities.GetBasicRootDseInformation(connection, logger);
                supportsPasswordModifyExtension = DirectorySupportsPasswordModifyExtension(connection);
                domainRootDn = GetDefaultNamingContext(connection);
            }
            catch (DirectoryOperationException ex)
            {
                logger.Warning("RunPasswordPreflightAsync: The directory refused to describe itself: {Message}", LogSanitiser.Sanitise(ex.Message));
                return CouldNotReadTheDirectory($"The directory refused to describe itself: {ex.Message}");
            }
            catch (LdapException ex)
            {
                logger.Warning("RunPasswordPreflightAsync: Could not read the directory's rootDSE: {Message}", LogSanitiser.Sanitise(ex.Message));
                return CouldNotReadTheDirectory($"JIM connected, but could not read the directory's basic information: {ex.Message}");
            }

            var preflight = new LdapConnectorPreflight(new LdapOperationExecutor(connection), logger,
                rootDse.DirectoryType, supportsPasswordModifyExtension, isConnectionEncrypted);

            var checks = new List<PasswordPreflightCheckResult>
            {
                PasswordPreflightCheckResult.Passed(PasswordPreflightCheck.Connection,
                    "JIM connected to this Connected System and authenticated successfully.")
            };
            checks.AddRange(await preflight.RunAsync(containerExternalIds, domainRootDn, cancellationToken));

            return new PasswordPreflightResult
            {
                TargetDescription = DescribeDirectory(rootDse.DirectoryType),
                Checks = checks
            };
        }
    }

    /// <summary>
    /// Builds the result for a preflight that could not get far enough to check anything. The checks that were
    /// never reached are reported as undetermined rather than omitted, so it stays visible what JIM would have
    /// looked at, and so the outcome cannot read as a pass on the strength of an empty list.
    /// </summary>
    private static PasswordPreflightResult UnfinishedPreflight(PasswordPreflightCheckResult connectionCheck, string notCheckedReason) =>
        new()
        {
            Checks =
            [
                connectionCheck,
                PasswordPreflightCheckResult.CouldNotDetermine(PasswordPreflightCheck.Encryption, notCheckedReason),
                PasswordPreflightCheckResult.CouldNotDetermine(PasswordPreflightCheck.PasswordMechanism, notCheckedReason),
                PasswordPreflightCheckResult.CouldNotDetermine(PasswordPreflightCheck.ResetRights, notCheckedReason),
                PasswordPreflightCheckResult.CouldNotDetermine(PasswordPreflightCheck.PolicyDiscovery, notCheckedReason)
            ]
        };

    private static PasswordPreflightResult CouldNotConnect(string message) =>
        UnfinishedPreflight(PasswordPreflightCheckResult.Failed(PasswordPreflightCheck.Connection, message),
            "Not checked, because JIM could not connect to the Connected System.");

    /// <summary>
    /// The bind succeeded but the directory would not describe itself, so the connection check passes and
    /// everything downstream of it is unknown.
    /// </summary>
    private static PasswordPreflightResult CouldNotReadTheDirectory(string message) =>
        UnfinishedPreflight(PasswordPreflightCheckResult.Passed(PasswordPreflightCheck.Connection,
                $"JIM connected and authenticated successfully. {message}"),
            "Not checked, because JIM could not read the directory's basic information.");

    private static string DescribeDirectory(LdapDirectoryType directoryType) => directoryType switch
    {
        LdapDirectoryType.ActiveDirectory => "Active Directory",
        LdapDirectoryType.SambaAD => "Samba Active Directory",
        LdapDirectoryType.OpenLDAP => "OpenLDAP",
        _ => "an LDAP directory"
    };

    /// <summary>
    /// Whether the directory advertises the RFC 3062 Password Modify extended operation on its rootDSE.
    /// Active Directory never does; it uses its own unicodePwd attribute instead.
    /// </summary>
    private static bool DirectorySupportsPasswordModifyExtension(LdapConnection connection)
    {
        var request = new SearchRequest { Scope = SearchScope.Base };
        request.Attributes.Add("supportedExtension");

        var response = (SearchResponse)connection.SendRequest(request);
        if (response.Entries.Count == 0)
            return false;

        var supportedExtensions = response.Entries[0].Attributes["supportedExtension"];
        if (supportedExtensions == null)
            return false;

        return supportedExtensions.GetValues(typeof(string))
            .OfType<string>()
            .Any(oid => oid == LdapConnectorPassword.PasswordModifyExtensionOid);
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

    #region IConnectorPhases members
    /// <summary>
    /// The steps this Connector performs, so an administrator watching a directory import can see
    /// where the time is going rather than one message at a time. A Delta Import asks the directory
    /// what has changed before fetching anything, and against Active Directory asks separately for
    /// deleted objects, so it has a longer journey than a Full Import.
    /// </summary>
    /// <remarks>
    /// Export declares nothing: it iterates per object, and JIM already reports accurate per-batch
    /// counts around the call, so a step would say less than the counts already do.
    /// </remarks>
    public IReadOnlyList<ConnectorPhase> GetPhases(ConnectedSystem connectedSystem, ConnectedSystemRunProfile runProfile)
    {
        return runProfile.RunType switch
        {
            ConnectedSystemRunType.FullImport =>
            [
                new ConnectorPhase(LdapConnectorPhases.RootDse, LdapConnectorPhases.RootDseName),
                new ConnectorPhase(LdapConnectorPhases.Fetch, LdapConnectorPhases.FetchName)
            ],
            ConnectedSystemRunType.DeltaImport =>
            [
                new ConnectorPhase(LdapConnectorPhases.RootDse, LdapConnectorPhases.RootDseName),
                new ConnectorPhase(LdapConnectorPhases.QueryChanges, LdapConnectorPhases.QueryChangesName),
                new ConnectorPhase(LdapConnectorPhases.Fetch, LdapConnectorPhases.FetchName),
                new ConnectorPhase(LdapConnectorPhases.QueryDeletions, LdapConnectorPhases.QueryDeletionsName)
            ],
            _ => []
        };
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
    /// Materialises the certificates held in the JIM certificate store into a temporary trust directory that LDAPS
    /// connections from this connector instance will consult, on top of the operating system's own trust anchors.
    /// </summary>
    /// <remarks>
    /// A failure here is deliberately not fatal: the connection still proceeds under the operating system's trust
    /// anchors alone, which is stricter than proceeding without them, and the administrator gets a warning naming the
    /// cause rather than a connection that silently trusts less than they configured.
    /// </remarks>
    private void PrepareTrustedCertificateDirectory(ILogger logger)
    {
        if (_certificateProvider == null)
            return;

        if (OperatingSystem.IsWindows())
        {
            // JIM ships as Linux containers; the platform LDAP client on Windows offers no supported way to add trust
            // anchors to a connection. Running JIM directly on Windows is a UI development loop only, so warn and
            // leave the operating system's own trust anchors in charge rather than pretending the store was applied.
            logger.Warning("LDAPS: certificates from the JIM certificate store cannot be applied when JIM runs directly on Windows. The connection will use the operating system trust anchors only");
            return;
        }

        var trustedCertificates = ServerCertificateDiagnosis.LoadTrustedCertificates(_certificateProvider);

        try
        {
            if (trustedCertificates.Count == 0)
            {
                logger.Debug("No certificates in the JIM certificate store; LDAPS validation will use the operating system trust anchors only");
                return;
            }

            _trustDirectory?.Dispose();
            _trustDirectory = LdapTrustedCertificateDirectory.Create(trustedCertificates, logger);
            logger.Debug("Loaded {Count} trusted certificate(s) from the JIM certificate store for LDAPS validation", trustedCertificates.Count);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            _trustDirectory = null;
            logger.Warning(ex, "Could not prepare the trusted certificate directory for LDAPS. Certificates from the JIM certificate store will not be used for this connection");
        }
        finally
        {
            foreach (var certificate in trustedCertificates)
                certificate.Dispose();
        }
    }

    private ConnectorSettingValueValidationResult TestDirectoryConnectivity(List<ConnectedSystemSettingValue> settingValues, ILogger logger)
    {
        try
        {
            try
            {
                // This is a connectivity test only; no persisted connector state applies.
                OpenImportConnection(settingValues, null, logger);
            }
            finally
            {
                CloseImportConnection();
            }

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

            _passwordConnection?.Dispose();
            _passwordConnection = null;
            _passwordChannel = null;

            _trustDirectory?.Dispose();
            _trustDirectory = null;
        }

        _disposed = true;
    }
    #endregion
}