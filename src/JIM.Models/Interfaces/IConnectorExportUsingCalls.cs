using JIM.Models.Staging;
using JIM.Models.Transactional;

namespace JIM.Models.Interfaces
{
    public interface IConnectorExportUsingCalls
    {
        public void OpenConnection(List<ConnectedSystemSetting> settings);

        public void Export(List<PendingExport> pendingExports);

        public void CloseConnection();
    }
}
