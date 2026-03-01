using JIM.Models.Staging;
using JIM.Models.Transactional;
namespace JIM.Models.Interfaces;

public interface IConnectorExportUsingCalls
{
    public void OpenExportConnection(IList<ConnectedSystemSettingValue> settings);

    /// <summary>
    /// Exports pending changes to the connected system.
    /// Returns a list of ConnectedSystemExportResult objects, one per pending export, in the same order.
    /// For Create operations, the ConnectedSystemExportResult should include the system-assigned ExternalId (e.g., objectGUID).
    /// </summary>
    /// <param name="pendingExports">The list of pending exports to process.</param>
    /// <param name="cancellationToken">Cancellation token to cancel the export operation.</param>
    /// <returns>A list of ConnectedSystemExportResult objects corresponding to each pending export.</returns>
    public Task<List<ConnectedSystemExportResult>> ExportAsync(IList<PendingExport> pendingExports, CancellationToken cancellationToken);

    public void CloseExportConnection();
}
