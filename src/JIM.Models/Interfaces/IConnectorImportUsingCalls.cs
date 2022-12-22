using JIM.Models.Staging;

namespace JIM.Models.Interfaces
{
    public interface IConnectorImportUsingCalls
    {
        public void OpenImportConnection(IList<ConnectedSystemSetting> settings);

        public ConnectedSystemImportResult Import(ConnectedSystemRunProfile runProfile);

        public void CloseImportConnection();
    }
}
