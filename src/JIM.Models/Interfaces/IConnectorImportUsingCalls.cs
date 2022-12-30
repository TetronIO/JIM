using JIM.Models.Staging;

namespace JIM.Models.Interfaces
{
    public interface IConnectorImportUsingCalls
    {
        public void OpenImportConnection(IList<ConnectedSystemSettingValue> settings);

        public ConnectedSystemImportResult Import(ConnectedSystemRunProfile runProfile);

        public void CloseImportConnection();
    }
}
