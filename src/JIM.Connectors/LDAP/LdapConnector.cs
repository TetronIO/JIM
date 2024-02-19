using JIM.Models.Exceptions;
using JIM.Models.Interfaces;
using JIM.Models.Staging;
using Serilog;
using System.DirectoryServices.Protocols;
using System.Net;

namespace JIM.Connectors.LDAP
{
    public class LdapConnector : IConnector, IConnectorCapabilities, IConnectorSettings, IConnectorSchema, IConnectorPartitions, IConnectorImportUsingCalls
    {
        private LdapConnection? _connection;

        #region IConnector members
        public string Name => ConnectorConstants.LdapConnectorName;

        public string? Description => "Enables bi-directional synchronisation with LDAP compliant directories, including Microsoft Active Directory.";

        public string? Url => "https://github.com/TetronIO/JIM";
        #endregion

        #region IConnectorCapability members
        public bool SupportsFullImport { get => true; }
        public bool SupportsDeltaImport { get => false; }
        public bool SupportsExport { get => false; }
        public bool SupportsPartitions { get => true; }
        public bool SupportsPartitionContainers { get => true; }
        public bool SupportsSecondaryExternalId { get => true; }
        #endregion

        #region IConnectorSettings members
        // variablising the names to reduce repitition later on, i.e. when we go to consume setting values JIM passes in, or when validating administrator-supplied settings
        private readonly string _settingDirectoryServer = "Host";
        private readonly string _settingDirectoryServerPort = "Port";
        //private readonly string _settingUseSecureConnection = "Use a Secure Connection?";
        private readonly string _settingConnectionTimeout = "Connection Timeout";
        private readonly string _settingRootDn = "Root DN";
        private readonly string _settingUsername = "Username";
        private readonly string _settingPassword = "Password";
        private readonly string _settingAuthType = "Authentication Type";
        private readonly string _settingCreateContainersAsNeeded = "Create containers as needed?";

        public List<ConnectorSetting> GetSettings()
        {
            return new List<ConnectorSetting>
            {
                new() { Name = "Directory Server", Category = ConnectedSystemSettingCategory.Connectivity, Type = ConnectedSystemSettingType.Heading },
                new() { Name = "Directory Server Info", Description = "Enter Active Directory domain controller, or LDAP server details below.", Category = ConnectedSystemSettingCategory.Connectivity, Type = ConnectedSystemSettingType.Label },
                new() { Name = _settingDirectoryServer, Required = true, Description = "Supply a directory server/domain controller hostname or IP address. IP address is fastest.", Category = ConnectedSystemSettingCategory.Connectivity, Type = ConnectedSystemSettingType.String },
                new() { Name = _settingDirectoryServerPort, Required = true, Description = "The port to connect to the directory service on, i.e. 389", DefaultIntValue = 389, Category = ConnectedSystemSettingCategory.Connectivity, Type = ConnectedSystemSettingType.Integer },
                //new() { Name = _settingUseSecureConnection, Category = ConnectedSystemSettingCategory.Connectivity, Type = ConnectedSystemSettingType.CheckBox },
                new() { Name = _settingConnectionTimeout, Required = true, Description = "How long to wait, in seconds, before giving up on trying to connect", DefaultIntValue = 10, Category = ConnectedSystemSettingCategory.Connectivity, Type = ConnectedSystemSettingType.Integer },
                new() { Name = _settingRootDn, Required = true, Description = "The forest/domain/partition root in DN format, i.e. DC=corp,DC=subatomic,DC=com", Category = ConnectedSystemSettingCategory.Connectivity, Type = ConnectedSystemSettingType.String },

                new() { Category = ConnectedSystemSettingCategory.Connectivity, Type = ConnectedSystemSettingType.Divider },

                new() { Name = "Credentials", Category = ConnectedSystemSettingCategory.Connectivity, Type = ConnectedSystemSettingType.Heading },
                new() { Name = _settingUsername, Required = true, Description = "What's the username for the service account you want to use to connect to the direcory service using? i.e. corp\\svc-jim-adc", Category = ConnectedSystemSettingCategory.Connectivity, Type = ConnectedSystemSettingType.String  },
                new() { Name = _settingPassword, Required = true, Description = "What's the password for the service account you want to use to connect to the directory service with?", Category = ConnectedSystemSettingCategory.Connectivity, Type = ConnectedSystemSettingType.StringEncrypted },
                new() { Name = _settingAuthType, Required = true, Description = "What type of authentication is required for this credential?", Type = ConnectedSystemSettingType.DropDown, DropDownValues = new() {"Simple", "NTLM"}},

                new() { Name = "Container Provisioning", Category = ConnectedSystemSettingCategory.General, Type = ConnectedSystemSettingType.Heading },
                new() { Name = _settingCreateContainersAsNeeded, Description = "i.e. create OUs as needed when provisioning new objects.", DefaultCheckboxValue = false, Category = ConnectedSystemSettingCategory.General, Type = ConnectedSystemSettingType.CheckBox }
            };
        }

        /// <summary>
        /// Validates LdapConnector setting values using custom business logic.
        /// </summary>
        public List<ConnectorSettingValueValidationResult> ValidateSettingValues(List<ConnectedSystemSettingValue> settingValues, ILogger logger)
        {
            Log.Verbose($"ValidateSettingValues() called for {Name}");
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

            var rootDnSettingValue = settingValues.SingleOrDefault(q => q.Setting.Name == _settingRootDn);
            if (rootDnSettingValue == null || string.IsNullOrEmpty(rootDnSettingValue.StringValue))
                throw new InvalidSettingValuesException($"No setting value for {_settingRootDn}!");

            var ldapConnectorSchema = new LdapConnectorSchema(_connection, rootDnSettingValue.StringValue);
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

            var rootDnSettingValue = settingValues.SingleOrDefault(q => q.Setting.Name == _settingRootDn);
            if (rootDnSettingValue == null || string.IsNullOrEmpty(rootDnSettingValue.StringValue))
                throw new InvalidSettingValuesException($"No setting value for {_settingRootDn}!");

            var ldapConnectorSchema = new LdapConnectorPartitions(_connection, rootDnSettingValue.StringValue);
            var partitions = await ldapConnectorSchema.GetPartitionsAsync();
            CloseImportConnection();
            return partitions;
        }
        #endregion

        #region IConnectorImportUsingCalls members
        public void OpenImportConnection(List<ConnectedSystemSettingValue> settingValues, ILogger logger)
        {
            Log.Verbose("OpenImportConnection() called");
            var directoryServer = settingValues.SingleOrDefault(q => q.Setting.Name == _settingDirectoryServer);
            var directoryServerPort = settingValues.SingleOrDefault(q => q.Setting.Name == _settingDirectoryServerPort);
            var timeoutSeconds = settingValues.SingleOrDefault(q => q.Setting.Name == _settingConnectionTimeout);
            var username = settingValues.SingleOrDefault(q => q.Setting.Name == _settingUsername);
            var password = settingValues.SingleOrDefault(q => q.Setting.Name == _settingPassword);            

            if (username == null || string.IsNullOrEmpty(username.StringValue) ||
                password == null || string.IsNullOrEmpty(password.StringEncryptedValue) ||
                directoryServer == null || string.IsNullOrEmpty(directoryServer.StringValue) ||
                directoryServerPort == null || !directoryServerPort.IntValue.HasValue ||
                timeoutSeconds == null || !timeoutSeconds.IntValue.HasValue)
                throw new InvalidSettingValuesException($"Missing setting values for {_settingDirectoryServer}, {_settingDirectoryServerPort}, {_settingConnectionTimeout}, {_settingUsername}, or {_settingPassword}");

            logger.Debug($"OpenImportConnection() Trying to connect to '{directoryServer.StringValue}' on port '{directoryServerPort.IntValue}' with username '{username.StringValue}'");
            var identifier = new LdapDirectoryIdentifier(directoryServer.StringValue, directoryServerPort.IntValue.Value);
            var credential = new NetworkCredential(username.StringValue, password.StringEncryptedValue);
            _connection = new LdapConnection(identifier, credential, AuthType.Basic);
            _connection.SessionOptions.ProtocolVersion = 3;
            //_connection.SessionOptions.SecureSocketLayer = false; // experimental
            //_connection.SessionOptions.VerifyServerCertificate += delegate { return true; }; // experimental
            _connection.Timeout = TimeSpan.FromSeconds(timeoutSeconds.IntValue.Value); // doesn't seem to have any effect. consider wrapping this in a time-limited, cancellable task instead
            _connection.Bind();
        }

        public async Task<ConnectedSystemImportResult> ImportAsync(ConnectedSystem connectedSystem, ConnectedSystemRunProfile runProfile, List<ConnectedSystemPaginationToken> paginationTokens, string? persistedConnectorData, ILogger logger, CancellationToken cancellationToken)
        {
            if (_connection == null)
                throw new InvalidOperationException("Must call OpenImportConnection() before ImportAsync()!");

            // needs to filter by partitions
            // needs to filter by object types
            // needs to filter by attributes
            // needs to be able to stop processing at convenient points if cancellation has been requested

            var import = new LdapConnectorImport(connectedSystem, runProfile, _connection, paginationTokens, persistedConnectorData, logger, cancellationToken);

            if (runProfile.RunType == ConnectedSystemRunType.FullImport)
            {
                logger.Debug("ImportAsync: Full Import requested");
                return import.GetFullImportObjects();
            }
            else if (runProfile.RunType == ConnectedSystemRunType.DeltaImport)
            {
                logger.Debug("ImportAsync: Delta Import requested");
                throw new NotSupportedException("Delta Imports are not yet currently supported by this Connector");
            }
            else
            {
                throw new InvalidDataException($"Unsupported import run-type: {runProfile.RunType}");
            }
        }

        public void CloseImportConnection()
        {
            _connection?.Dispose();
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
                    ErrorMessage = "Unable to test connectivity due to missing diretory server, port, username and/or password values"
                };
            }
            catch (Exception ex)
            {
                Log.Error(ex, $"TestDirectoryConnectivity failed");
                return new ConnectorSettingValueValidationResult
                {
                    ErrorMessage = $"Unable to connect. Message: {ex.Message}",
                    Exception = ex
                };
            }
        }
        #endregion
    }
}