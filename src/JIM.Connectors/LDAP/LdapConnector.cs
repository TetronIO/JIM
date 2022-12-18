using JIM.Models.Interfaces;
using JIM.Models.Staging;

namespace JIM.Connectors.LDAP
{
    public class LdapConnector : IConnector, IConnectorCapabilities, IConnectorSettings, IConnectorContainers
    {
        #region IConnector members
        public string Name => "JIM LDAP Connector";

        public string Description => "Enables bi-directional synchronisation with LDAP compliant directories, including Microsoft Active Directory.";

        public string Url => "https://github.com/TetronIO/JIM";
        #endregion

        #region IConnectorCapability members
        public bool SupportsFullImport { get => true; }
        public bool SupportsDeltaImport { get => false; }
        public bool SupportsExport { get => false; }
        #endregion

        #region IConnectorSettings members
        public IList<ConnectedSystemSetting> GetSettings()
        {
            var settings = new List<ConnectedSystemSetting>
            {
                new ConnectedSystemSetting("Forest Name", "What's the fully-qualified domain name of the Forest? i.e. lab.tetron.io", ConnectedSystemSettingCategory.Connectivity, ConnectedSystemSettingType.String),
                new ConnectedSystemSetting("Domain Name", "What's the name for the domain you want to synchronise with in the forest? i.e. lab", ConnectedSystemSettingCategory.Connectivity, ConnectedSystemSettingType.String),
                new ConnectedSystemSetting("Username", "What's the username for the service account you want to use to connect to the domain? i.e. svc-jimadc", ConnectedSystemSettingCategory.Connectivity, ConnectedSystemSettingType.String),
                new ConnectedSystemSetting("Password", "What's the password for the service account you want to use to connect to the domain?", ConnectedSystemSettingCategory.Connectivity, ConnectedSystemSettingType.StringEncrypted),
                new ConnectedSystemSetting("Use Encrypted Connection", true, ConnectedSystemSettingCategory.Connectivity, ConnectedSystemSettingType.CheckBox)
            };

            return settings;
        }
        #endregion

        #region IConnectorContainers
        public ConnectorContainer? GetContainers()
        {
            throw new NotImplementedException();
        }
        #endregion
    }
}