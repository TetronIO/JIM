using JIM.Models.Exceptions;
using JIM.Models.Interfaces;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using Serilog;
using System.DirectoryServices.Protocols;
using System.Net;
namespace JIM.Connectors.LDAP;

public class LdapConnector : IConnector, IConnectorCapabilities, IConnectorSettings, IConnectorSchema, IConnectorPartitions, IConnectorImportUsingCalls, IConnectorExportUsingCalls, IDisposable
{
    private LdapConnection? _connection;
    private bool _disposed;

    #region IConnector members
    public string Name => ConnectorConstants.LdapConnectorName;

    public string? Description => "Enables bi-directional synchronisation with LDAP compliant directories, including Microsoft Active Directory.";

    public string? Url => "https://github.com/TetronIO/JIM";
    #endregion

    #region IConnectorCapability members
    public bool SupportsFullImport => true;
    public bool SupportsDeltaImport => false;
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
    // TODO: Add LDAPS support - see Phase 5 in docs/LDAP_CONNECTOR_IMPROVEMENTS.md
    // private readonly string _settingUseSecureConnection = "Use a Secure Connection?";
    private readonly string _settingConnectionTimeout = "Connection Timeout";
    private readonly string _settingUsername = "Username";
    private readonly string _settingPassword = "Password";
    private readonly string _settingAuthType = "Authentication Type";
    private readonly string _settingSearchTimeout = "Search Timeout";
    private readonly string _settingCreateContainersAsNeeded = "Create containers as needed?";

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
            new() { Name = _settingDirectoryServerPort, Required = true, Description = "The port to connect to the directory service on, i.e. 389", DefaultIntValue = 389, Category = ConnectedSystemSettingCategory.Connectivity, Type = ConnectedSystemSettingType.Integer },
            // TODO: Add LDAPS setting here when implementing secure connection support
            new() { Name = _settingConnectionTimeout, Required = true, Description = "How long to wait, in seconds, before giving up on trying to connect", DefaultIntValue = 10, Category = ConnectedSystemSettingCategory.Connectivity, Type = ConnectedSystemSettingType.Integer },

            new() { Category = ConnectedSystemSettingCategory.Connectivity, Type = ConnectedSystemSettingType.Divider },

            new() { Name = "Credentials", Category = ConnectedSystemSettingCategory.Connectivity, Type = ConnectedSystemSettingType.Heading },
            new() { Name = _settingUsername, Required = true, Description = "What's the username for the service account you want to use to connect to the directory service using? i.e. corp\\svc-jim-adc", Category = ConnectedSystemSettingCategory.Connectivity, Type = ConnectedSystemSettingType.String  },
            new() { Name = _settingPassword, Required = true, Description = "What's the password for the service account you want to use to connect to the directory service with?", Category = ConnectedSystemSettingCategory.Connectivity, Type = ConnectedSystemSettingType.StringEncrypted },
            new() { Name = _settingAuthType, Required = true, Description = "What type of authentication is required for this credential?", Type = ConnectedSystemSettingType.DropDown, DropDownValues = new() { LdapConnectorConstants.SETTING_AUTH_TYPE_SIMPLE, LdapConnectorConstants.SETTING_AUTH_TYPE_NTLM }},

            new() { Name = "Import Settings", Category = ConnectedSystemSettingCategory.General, Type = ConnectedSystemSettingType.Heading },
            new() { Name = _settingSearchTimeout, Required = false, Description = "Maximum time in seconds to wait for LDAP search results. Default is 300 (5 minutes).", DefaultIntValue = 300, Category = ConnectedSystemSettingCategory.General, Type = ConnectedSystemSettingType.Integer },

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

        if (username == null || string.IsNullOrEmpty(username.StringValue) ||
            password == null || string.IsNullOrEmpty(password.StringEncryptedValue) ||
            authTypeSettingValue == null || string.IsNullOrEmpty(authTypeSettingValue.StringValue) ||
            directoryServer == null || string.IsNullOrEmpty(directoryServer.StringValue) ||
            directoryServerPort is not { IntValue: not null } ||
            timeoutSeconds is not { IntValue: not null })
            throw new InvalidSettingValuesException($"Missing setting values for {_settingDirectoryServer}, {_settingDirectoryServerPort}, {_settingConnectionTimeout}, {_settingUsername},{_settingPassword}, or {_settingAuthType}.");

        logger.Debug($"OpenImportConnection() Trying to connect to '{directoryServer.StringValue}' on port '{directoryServerPort.IntValue}' with username '{username.StringValue}' via auth type {authTypeSettingValue.StringValue}.");
        var identifier = new LdapDirectoryIdentifier(directoryServer.StringValue, directoryServerPort.IntValue.Value);
        var credential = new NetworkCredential(username.StringValue, password.StringEncryptedValue);

        // allow the user to specify what type of authentication to perform against the supplied credential.
        var authTypeSettingValueString = authTypeSettingValue.StringValue;
        var authTypeEnumValue = AuthType.Anonymous;
        if (authTypeSettingValueString == LdapConnectorConstants.SETTING_AUTH_TYPE_SIMPLE)
            authTypeEnumValue = AuthType.Basic;
        else if (authTypeSettingValueString == LdapConnectorConstants.SETTING_AUTH_TYPE_NTLM)
            authTypeEnumValue = AuthType.Ntlm;

        _connection = new LdapConnection(identifier, credential, authTypeEnumValue);
        _connection.SessionOptions.ProtocolVersion = 3;
        // TODO: Enable SSL options when LDAPS support is implemented (Phase 5)
        // _connection.SessionOptions.SecureSocketLayer = true;
        // _connection.SessionOptions.VerifyServerCertificate += (connection, certificate) => true; // Accept all certs - make configurable
        _connection.Timeout = TimeSpan.FromSeconds(timeoutSeconds.IntValue.Value); // doesn't seem to have any effect. consider wrapping this in a time-limited, cancellable task instead
        _connection.Bind();
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
                throw new NotSupportedException("Delta Imports are not yet currently supported by this Connector");
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

    #region private methods
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