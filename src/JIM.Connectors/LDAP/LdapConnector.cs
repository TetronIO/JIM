using JIM.Models.Interfaces;
using JIM.Models.Staging;
using System.DirectoryServices.Protocols;
using System.Net;

namespace JIM.Connectors.LDAP
{
    public class LdapConnector : IConnector, IConnectorCapabilities, IConnectorSettings, IConnectorSchema, IConnectorPartitions, IConnectorContainers, IConnectorImportUsingCalls
    {
        #region IConnector members
        public string Name => Constants.LdapConnectorName;

        public string? Description => "Enables bi-directional synchronisation with LDAP compliant directories, including Microsoft Active Directory.";

        public string? Url => "https://github.com/TetronIO/JIM";
        #endregion

        #region IConnectorCapability members
        public bool SupportsFullImport { get => true; }
        public bool SupportsDeltaImport { get => false; }
        public bool SupportsExport { get => false; }
        #endregion

        #region IConnectorSettings members
        // variablising the names to reduce repitition later on, i.e. when we go to consume setting values JIM passes in, or when validating administrator-supplied settings
        private readonly string _settingDirectoryServer = "Host";
        private readonly string _settingDirectoryServerPort = "Port";
        private readonly string _settingUseSecureConnection = "Use a Secure Connection?";
        private readonly string _settingUsername = "Username";
        private readonly string _settingPassword = "Password";
        private readonly string _settingCreateContainersAsNeeded = "Create containers as needed?";

        public IList<ConnectorSetting> GetSettings()
        {
            return new List<ConnectorSetting>
            {
                new ConnectorSetting { Name = "Directory Server", Category = ConnectedSystemSettingCategory.Connectivity, Type = ConnectedSystemSettingType.Heading },
                new ConnectorSetting { Name = "Directory Server Info", Description = "Active Directory domain controller, or LDAP server details can be entered below.", Category = ConnectedSystemSettingCategory.Connectivity, Type = ConnectedSystemSettingType.Label },
                new ConnectorSetting { Name = _settingDirectoryServer, Required = true, Description = "Supply a directory server/domain controller hostname or IP address. IP address is fastest.", Category = ConnectedSystemSettingCategory.Connectivity, Type = ConnectedSystemSettingType.String },
                new ConnectorSetting { Name = _settingDirectoryServerPort, Required = true, Description = "The port to connect to the directory service on, i.e. 389", DefaultStringValue = "389", Category = ConnectedSystemSettingCategory.Connectivity, Type = ConnectedSystemSettingType.String },
                new ConnectorSetting { Name = _settingUseSecureConnection, Category = ConnectedSystemSettingCategory.Connectivity, Type = ConnectedSystemSettingType.CheckBox },
                new ConnectorSetting { Category = ConnectedSystemSettingCategory.Connectivity, Type = ConnectedSystemSettingType.Divider },

                new ConnectorSetting { Name = "Credentials", Category = ConnectedSystemSettingCategory.Connectivity, Type = ConnectedSystemSettingType.Heading },
                new ConnectorSetting { Name = _settingUsername, Required = true, Description = "What's the username for the service account you want to use to connect to the direcory service using? i.e. corp\\svc-jim-adc", Category = ConnectedSystemSettingCategory.Connectivity, Type = ConnectedSystemSettingType.String  },
                new ConnectorSetting { Name = _settingPassword, Required = true, Description = "What's the password for the service account you want to use to connect to the directory service with?", Category = ConnectedSystemSettingCategory.Connectivity, Type = ConnectedSystemSettingType.StringEncrypted },

                new ConnectorSetting { Name = "Container Provisioning", Category = ConnectedSystemSettingCategory.General, Type = ConnectedSystemSettingType.Heading },
                new ConnectorSetting { Name = _settingCreateContainersAsNeeded, Description = "i.e. create OUs as needed when provisioning new objects.", DefaultCheckboxValue = false, Category = ConnectedSystemSettingCategory.General, Type = ConnectedSystemSettingType.CheckBox }
            };
        }

        /// <summary>
        /// Validates LdapConnector setting values using custom business logic.
        /// </summary>
        public async Task<IList<ConnectorSettingValueValidationResult>> ValidateSettingValuesAsync(IList<ConnectedSystemSettingValue> settingValues)
        {
            var response = new List<ConnectorSettingValueValidationResult>();

            // validate that we can connect to the directory service with the supplied setting credentials
            var connectivityTestResult = TestDirectoryConnectivity(settingValues);
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
            }

            return response;
        }
        #endregion

        #region IConnectorSchema members
        public async Task<ConnectorSchema> GetSchemaAsync()
        {
            throw new NotImplementedException();
        }
        #endregion

        #region IConnectorPartitions members
        public IList<ConnectorPartition> GetPartitions(IList<ConnectedSystemSettingValue> settings)
        {
            throw new NotImplementedException();
        }
        #endregion

        #region IConnectorContainers members
        public ConnectorContainer? GetContainers(IList<ConnectedSystemSettingValue> settingss)
        {
            throw new NotImplementedException();
        }

        public ConnectorContainer? GetContainers(IList<ConnectedSystemSettingValue> settings, ConnectorPartition connectorPartition)
        {
            // require connection setting values. validate for presence...
            throw new NotImplementedException();
        }
        #endregion

        #region IConnectorImportUsingCalls members
        public void OpenImportConnection(IList<ConnectedSystemSettingValue> settings)
        {
            throw new NotImplementedException();
        }

        public ConnectedSystemImportResult Import(ConnectedSystemRunProfile runProfile)
        {
            throw new NotImplementedException();
        }

        public void CloseImportConnection()
        {
            throw new NotImplementedException();
        }
        #endregion

        #region private methods
        private ConnectorSettingValueValidationResult TestDirectoryConnectivity(IList<ConnectedSystemSettingValue> settingValues)
        {
            var username = settingValues.SingleOrDefault(q => q.Setting.Name == _settingUsername);
            var password = settingValues.SingleOrDefault(q => q.Setting.Name == _settingPassword);
            var directoryServer = settingValues.SingleOrDefault(q => q.Setting.Name == _settingDirectoryServer);
            var directoryServerPort = settingValues.SingleOrDefault(q => q.Setting.Name == _settingDirectoryServerPort);

            if (username == null || string.IsNullOrEmpty(username.StringValue) ||
                password == null || string.IsNullOrEmpty(password.StringEncryptedValue) ||
                directoryServer == null || string.IsNullOrEmpty(directoryServer.StringValue) ||
                directoryServerPort == null || string.IsNullOrEmpty(directoryServerPort.StringValue))
                return new ConnectorSettingValueValidationResult
                {
                    ErrorMessage = "Unable to test connectivity due to missing diretory server, port, username and/or password values"
                };

            try
            {
                //var identifier = new LdapDirectoryIdentifier(directoryServer.StringValue, int.Parse(directoryServerPort.StringValue));
                var identifier = new LdapDirectoryIdentifier(directoryServer.StringValue);
                var credential = new NetworkCredential(username.StringValue, password.StringEncryptedValue);
                using var connection = new LdapConnection(identifier, credential, AuthType.Basic);
                connection.SessionOptions.ProtocolVersion = 3;
                connection.Bind();

                return new ConnectorSettingValueValidationResult
                {
                    IsValid = true
                };

            }
            catch (Exception ex)
            {
                return new ConnectorSettingValueValidationResult
                {
                    ErrorMessage = $"Unable to connect to {directoryServer.StringValue}:{directoryServerPort.StringValue}. Message: {ex.Message}",
                    Exception = ex
                };
            }
        }
        #endregion
    }
}