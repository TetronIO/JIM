using JIM.Models.Staging;
using JIM.Models.Transactional;
namespace JIM.Models.Interfaces;

public interface IConnectorExportUsingCalls
{
    public void OpenExportConnection(IList<ConnectedSystemSettingValue> settings);

    /// <summary>
    /// Exports pending changes to the connected system.
    /// Returns a list of ExportResult objects, one per pending export, in the same order.
    /// For Create operations, the ExportResult should include the system-assigned ExternalId (e.g., objectGUID).
    /// </summary>
    /// <param name="pendingExports">The list of pending exports to process.</param>
    /// <returns>A list of ExportResult objects corresponding to each pending export.</returns>
    public List<ExportResult> Export(IList<PendingExport> pendingExports);

    public void CloseExportConnection();
}