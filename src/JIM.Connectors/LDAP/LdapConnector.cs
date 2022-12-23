using JIM.Models.Interfaces;
using JIM.Models.Staging;

namespace JIM.Connectors.LDAP
{
    public class LdapConnector : IConnector, IConnectorCapabilities, IConnectorSettings, IConnectorPartitions, IConnectorContainers, IConnectorImportUsingCalls
    {
        #region IConnector members
        public string Name => Constants.LdapConnectorName;

        public string Description => "Enables bi-directional synchronisation with LDAP compliant directories, including Microsoft Active Directory.";

        public string Url => "https://github.com/TetronIO/JIM";
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
                new ConnectorSetting("Active Directory", ConnectedSystemSettingCategory.Connectivity, ConnectedSystemSettingType.Heading),
                new ConnectorSetting(_settingForestName, "What's the fully-qualified domain name of the Forest? i.e. lab.tetron.io", ConnectedSystemSettingCategory.Connectivity, ConnectedSystemSettingType.String),
                new ConnectorSetting(_settingDomainName, "What's the name for the domain you want to synchronise with in the forest? i.e. lab", ConnectedSystemSettingCategory.Connectivity, ConnectedSystemSettingType.String),
                new ConnectorSetting(_settingDomainController, "When connecting to an untrusted domain, supply a domain controller hostname or ip address here.", ConnectedSystemSettingCategory.Connectivity, ConnectedSystemSettingType.String),
                new ConnectorSetting(ConnectedSystemSettingCategory.Connectivity, ConnectedSystemSettingType.Divider),

                new ConnectorSetting("LDAP", ConnectedSystemSettingCategory.Connectivity, ConnectedSystemSettingType.Heading),
                new ConnectorSetting(_settingHostname, "The host for the directory, i.e. addls-01.lab.tetron.io", ConnectedSystemSettingCategory.Connectivity, ConnectedSystemSettingType.String),
                new ConnectorSetting(_settingPort, "The port for the directory, i.e. 636", "636", ConnectedSystemSettingCategory.Connectivity, ConnectedSystemSettingType.String),
                new ConnectorSetting(_settingUseEncryptedConnection, true, ConnectedSystemSettingCategory.Connectivity, ConnectedSystemSettingType.CheckBox),
                new ConnectorSetting(ConnectedSystemSettingCategory.Connectivity, ConnectedSystemSettingType.Divider),

                new ConnectorSetting("Credentials", ConnectedSystemSettingCategory.Connectivity, ConnectedSystemSettingType.Heading),
                new ConnectorSetting(_settingUsername, "What's the username for the service account you want to use to connect to the domain? i.e. svc-jimadc", ConnectedSystemSettingCategory.Connectivity, ConnectedSystemSettingType.String),
                new ConnectorSetting(_settingPassword, "What's the password for the service account you want to use to connect to the domain?", ConnectedSystemSettingCategory.Connectivity, ConnectedSystemSettingType.StringEncrypted),

                new ConnectorSetting("Container Provisioning", ConnectedSystemSettingCategory.General, ConnectedSystemSettingType.Heading),
                new ConnectorSetting(_settingCreateContainersAsNeeded, "i.e. create OUs as needed when provisioning new objects.", false, ConnectedSystemSettingCategory.General, ConnectedSystemSettingType.CheckBox)
            };

            return settings;
        }

        public IList<ConnectorSettingValueValidationResult> ValidateSettingValues(IList<ConnectedSystemSetting> settings)
        {
            throw new NotImplementedException();
        }
        #endregion

        #region IConnectorPartitions members
        public IList<ConnectorPartition> GetPartitions(IList<ConnectedSystemSetting> settings)
        {
            throw new NotImplementedException();
        }
        #endregion

        #region IConnectorContainers members
        public ConnectorContainer? GetContainers(IList<ConnectedSystemSetting> settingss)
        {
            throw new NotImplementedException();
        }

        public ConnectorContainer? GetContainers(IList<ConnectedSystemSetting> settings, ConnectorPartition connectorPartition)
        {
            // require connection setting values. validate for presence...
            throw new NotImplementedException();
        }
        #endregion

        #region IConnectorImportUsingCalls members
        public void OpenImportConnection(IList<ConnectedSystemSetting> settings)
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