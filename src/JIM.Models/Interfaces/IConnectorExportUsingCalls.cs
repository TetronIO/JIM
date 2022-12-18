using JIM.Models.Staging;
using JIM.Models.Transactional;

namespace JIM.Models.Interfaces
{
    public interface IConnectorExportUsingCalls
    {
        public void OpenConnection(IList<ConnectedSystemSetting> settings);

        public void Export(IList<PendingExport> pendingExports);

        public void CloseConnection();
    }
}
