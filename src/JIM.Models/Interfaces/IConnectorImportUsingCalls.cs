using JIM.Models.Staging;

namespace JIM.Models.Interfaces
{
    public interface IConnectorImportUsingCalls
    {
        public void OpenImportConnection(IList<ConnectedSystemSettingValue> settingValues);

        public ConnectedSystemImportResult Import(ConnectedSystemRunProfile runProfile);

        public void CloseImportConnection();
    }
}
