using JIM.Models.Interfaces;
using JIM.Models.Staging;

namespace JIM.Connectors.LDAP
{
    public class LdapConnector : IConnector, IConnectorCapabilities, IConnectorSettings, IConnectorPartitions, IConnectorContainers, IConnectorImportUsingCalls
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
        private readonly string _settingAdForestName = "Forest Name";
        private readonly string _settingAdDomainName = "Domain Name";
        private readonly string _settingAdDomainController = "Domain Controller";
        private readonly string _settingLdapHostname = "Hostname";
        private readonly string _settingLdapPort = "Port";
        private readonly string _settingLdapUseEncryptedConnection = "Use An Encrypted Connection?";
        private readonly string _settingUsername = "Username";
        private readonly string _settingPassword = "Password";
        private readonly string _settingCreateContainersAsNeeded = "Create containers as needed?";

        public IList<ConnectorSetting> GetSettings()
        {
            var settings = new List<ConnectorSetting>
            {
                new ConnectorSetting { Name = "Active Directory", Category = ConnectedSystemSettingCategory.Connectivity, Type = ConnectedSystemSettingType.Heading },
                new ConnectorSetting { Name = _settingAdForestName, Description = "What's the fully-qualified domain name of the Forest? i.e. lab.tetron.io", Category = ConnectedSystemSettingCategory.Connectivity, Type = ConnectedSystemSettingType.String },
                new ConnectorSetting { Name = _settingAdDomainName, Description = "What's the name (aka NETBIOS name) for the domain you want to synchronise with in the forest? i.e. lab", Category = ConnectedSystemSettingCategory.Connectivity, Type = ConnectedSystemSettingType.String },
                new ConnectorSetting { Name = _settingAdDomainController, Description = "When connecting to an untrusted domain, supply a domain controller hostname or ip address here.", Category = ConnectedSystemSettingCategory.Connectivity, Type = ConnectedSystemSettingType.String },
                new ConnectorSetting { Category = ConnectedSystemSettingCategory.Connectivity, Type = ConnectedSystemSettingType.Divider },

                new ConnectorSetting { Name = "LDAP", Category = ConnectedSystemSettingCategory.Connectivity, Type = ConnectedSystemSettingType.Heading },
                new ConnectorSetting { Name = _settingLdapHostname, Description = "The hostname to connect to the directory service with, i.e. addls-01.lab.tetron.io", Category = ConnectedSystemSettingCategory.Connectivity, Type = ConnectedSystemSettingType.String },
                new ConnectorSetting { Name = _settingLdapPort, Description = "The port to connect to the directory service on, i.e. 636", DefaultStringValue = "636", Category = ConnectedSystemSettingCategory.Connectivity, Type = ConnectedSystemSettingType.String },
                new ConnectorSetting { Name = _settingLdapUseEncryptedConnection, DefaultCheckboxValue = true, Category = ConnectedSystemSettingCategory.Connectivity, Type = ConnectedSystemSettingType.CheckBox },
                new ConnectorSetting { Category = ConnectedSystemSettingCategory.Connectivity, Type = ConnectedSystemSettingType.Divider },

                new ConnectorSetting { Name = "Credentials", Category = ConnectedSystemSettingCategory.Connectivity, Type = ConnectedSystemSettingType.Heading },
                new ConnectorSetting { Name = _settingUsername, Description = "What's the username for the service account you want to use to connect to the direcory service using? i.e. svc-jimadc", Category = ConnectedSystemSettingCategory.Connectivity, Type = ConnectedSystemSettingType.String, Required = true },
                new ConnectorSetting { Name = _settingPassword, Description = "What's the password for the service account you want to use to connect to the directory service with?", Category = ConnectedSystemSettingCategory.Connectivity, Type = ConnectedSystemSettingType.StringEncrypted, Required = true },

                new ConnectorSetting { Name = "Container Provisioning", Category = ConnectedSystemSettingCategory.General, Type = ConnectedSystemSettingType.Heading },
                new ConnectorSetting { Name = _settingCreateContainersAsNeeded, Description = "i.e. create OUs as needed when provisioning new objects.", DefaultCheckboxValue = false, Category = ConnectedSystemSettingCategory.General, Type = ConnectedSystemSettingType.CheckBox }
            };

            return settings;
        }

        /// <summary>
        /// Validates LdapConnector setting values using custom business logic.
        /// </summary>
        public IList<ConnectorSettingValueValidationResult> ValidateSettingValues(IList<ConnectedSystemSettingValue> settingValues)
        {
            var response = new List<ConnectorSettingValueValidationResult>();

            var usingActiveDirectory = !string.IsNullOrEmpty(settingValues.Single(q => q.Setting.Name == _settingAdForestName).StringValue) ||
                                       !string.IsNullOrEmpty(settingValues.Single(q => q.Setting.Name == _settingAdDomainName).StringValue) ||
                                       !string.IsNullOrEmpty(settingValues.Single(q => q.Setting.Name == _settingAdDomainController).StringValue);

            var usingLdap = !string.IsNullOrEmpty(settingValues.Single(q => q.Setting.Name == _settingLdapHostname).StringValue) ||
                            !string.IsNullOrEmpty(settingValues.Single(q => q.Setting.Name == _settingLdapPort).StringValue) ||
                            !string.IsNullOrEmpty(settingValues.Single(q => q.Setting.Name == _settingLdapUseEncryptedConnection).StringValue);

            if (usingActiveDirectory && usingLdap)
            {
                // cannot use both AD and LDAP settings
                response.Add(new ConnectorSettingValueValidationResult { ErrorMessage = "Please supply EITHER values for Active Directory OR LDAP, not both.", IsValid = false });
            }
            else if (!usingActiveDirectory && !usingLdap)
            {
                // neither AD, nor LDAP setting values have been provided
                response.Add(new ConnectorSettingValueValidationResult { ErrorMessage = "Please supply values for Active Directory OR LDAP.", IsValid = false });
            }
            else if (usingActiveDirectory)
            {
                // make sure all required AD setting values have been supplied
                if (string.IsNullOrEmpty(settingValues.Single(q => q.Setting.Name == _settingAdForestName).StringValue))
                    response.Add(new ConnectorSettingValueValidationResult { ErrorMessage = $"Please supply a value for {_settingAdForestName}.", IsValid = false });

                if (string.IsNullOrEmpty(settingValues.Single(q => q.Setting.Name == _settingAdDomainName).StringValue))
                    response.Add(new ConnectorSettingValueValidationResult { ErrorMessage = $"Please supply a value for {_settingAdDomainName}.", IsValid = false });
            }
            else if (usingLdap)
            {
                // make sure all required LDAP setting values have been supplied
                if (string.IsNullOrEmpty(settingValues.Single(q => q.Setting.Name == _settingLdapHostname).StringValue))
                    response.Add(new ConnectorSettingValueValidationResult { ErrorMessage = $"Please supply a value for {_settingLdapHostname}.", IsValid = false });

                if (string.IsNullOrEmpty(settingValues.Single(q => q.Setting.Name == _settingLdapPort).StringValue))
                    response.Add(new ConnectorSettingValueValidationResult { ErrorMessage = $"Please supply a value for {_settingLdapPort}.", IsValid = false });
            }

            // general required setting value validation
            foreach (var requiredSettingValue in settingValues.Where(q => q.Setting.Required))
            {
                if (requiredSettingValue.Setting.Type == ConnectedSystemSettingType.String && string.IsNullOrEmpty(requiredSettingValue.StringValue))
                    response.Add(new ConnectorSettingValueValidationResult { ErrorMessage = $"Please supply a value for {requiredSettingValue.Setting.Name}.", IsValid = false, SettingValue = requiredSettingValue });

                // keeping this separate for now, as encrypted strings are going to have to improve their implementation at some point
                if (requiredSettingValue.Setting.Type == ConnectedSystemSettingType.StringEncrypted && string.IsNullOrEmpty(requiredSettingValue.StringEncryptedValue))
                    response.Add(new ConnectorSettingValueValidationResult { ErrorMessage = $"Please supply a value for {requiredSettingValue.Setting.Name}.", IsValid = false, SettingValue = requiredSettingValue });
            }

            return response;
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
    }
}