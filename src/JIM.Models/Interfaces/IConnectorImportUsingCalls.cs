using JIM.Models.Staging;

namespace JIM.Models.Interfaces
{
    public interface IConnectorImportUsingCalls
    {
        public void OpenImportConnection(IList<ConnectedSystemSettingValue> settingValues);

        public Task<ConnectedSystemImportResult> ImportAsync(ConnectedSystemRunProfile runProfile, CancellationToken cancellationToken);

        public void CloseImportConnection();
    }
}
