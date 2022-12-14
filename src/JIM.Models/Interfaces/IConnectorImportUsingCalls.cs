using JIM.Models.Staging;

namespace JIM.Models.Interfaces
{
    public interface IConnectorImportUsingCalls
    {
        public void OpenConnection(List<ConnectedSystemSetting> settings);

        public ConnectedSystemImportResult Import(ConnectedSystemRunProfile runProfile);

        public void CloseConnection();
    }
}
