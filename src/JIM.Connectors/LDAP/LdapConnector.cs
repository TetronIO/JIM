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
        private string _settingForestName = "Forest Name";
        private string _settingDomainName = "Domain Name";
        private string _settingDomainController = "Domain Controller";
        private string _settingHostname = "Hostname";
        private string _settingPort = "Port";
        private string _settingUseEncryptedConnection = "Use Encrypted Connection?";
        private string _settingUsername = "Username";
        private string _settingPassword = "Password";
        private string _settingCreateContainersAsNeeded = "Create containers as needed?";

        public IList<ConnectorSetting> GetSettings()
        {
            var settings = new List<ConnectorSetting>
            {
                new ConnectorSetting { Name = "Active Directory", Category = ConnectedSystemSettingCategory.Connectivity, Type = ConnectedSystemSettingType.Heading },
                new ConnectorSetting { Name = _settingForestName, Description = "What's the fully-qualified domain name of the Forest? i.e. lab.tetron.io", Category = ConnectedSystemSettingCategory.Connectivity, Type = ConnectedSystemSettingType.String },
                new ConnectorSetting { Name = _settingDomainName, Description = "What's the name for the domain you want to synchronise with in the forest? i.e. lab", Category = ConnectedSystemSettingCategory.Connectivity, Type = ConnectedSystemSettingType.String },
                new ConnectorSetting { Name = _settingDomainController, Description = "When connecting to an untrusted domain, supply a domain controller hostname or ip address here.", Category = ConnectedSystemSettingCategory.Connectivity, Type = ConnectedSystemSettingType.String },
                new ConnectorSetting { Category = ConnectedSystemSettingCategory.Connectivity, Type = ConnectedSystemSettingType.Divider },

                new ConnectorSetting { Name = "LDAP", Category = ConnectedSystemSettingCategory.Connectivity, Type = ConnectedSystemSettingType.Heading },
                new ConnectorSetting { Name = _settingHostname, Description = "The host for the directory, i.e. addls-01.lab.tetron.io", Category = ConnectedSystemSettingCategory.Connectivity, Type = ConnectedSystemSettingType.String },
                new ConnectorSetting { Name = _settingPort, Description = "The port for the directory, i.e. 636", DefaultStringValue = "636", Category = ConnectedSystemSettingCategory.Connectivity, Type = ConnectedSystemSettingType.String },
                new ConnectorSetting { Name = _settingUseEncryptedConnection, DefaultCheckboxValue = true, Category = ConnectedSystemSettingCategory.Connectivity, Type = ConnectedSystemSettingType.CheckBox },
                new ConnectorSetting { Category = ConnectedSystemSettingCategory.Connectivity, Type = ConnectedSystemSettingType.Divider },

                new ConnectorSetting { Name = "Credentials", Category = ConnectedSystemSettingCategory.Connectivity, Type = ConnectedSystemSettingType.Heading },
                new ConnectorSetting { Name = _settingUsername, Description = "What's the username for the service account you want to use to connect to the domain? i.e. svc-jimadc", Category = ConnectedSystemSettingCategory.Connectivity, Type = ConnectedSystemSettingType.String },
                new ConnectorSetting { Name = _settingPassword, Description = "What's the password for the service account you want to use to connect to the domain?", Category = ConnectedSystemSettingCategory.Connectivity, Type = ConnectedSystemSettingType.StringEncrypted },

                new ConnectorSetting { Name = "Container Provisioning", Category = ConnectedSystemSettingCategory.General, Type = ConnectedSystemSettingType.Heading },
                new ConnectorSetting { Name = _settingCreateContainersAsNeeded, Description = "i.e. create OUs as needed when provisioning new objects.", DefaultCheckboxValue = false, Category = ConnectedSystemSettingCategory.General, Type = ConnectedSystemSettingType.CheckBox }
            };

            return settings;
        }

        public IList<ConnectorSettingValueValidationResult> ValidateSettingValues(IList<ConnectedSystemSettingValue> settings)
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
    }
}