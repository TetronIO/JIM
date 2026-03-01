using JIM.Models.Staging;
using JIM.Models.Transactional;
namespace JIM.Models.Interfaces;

public interface IConnectorExportUsingFiles
{
    /// <summary>
    /// Exports changes to connected system objects to the connected system via a file.
    /// It's up to you to specify where the output file is written to.
    /// Recommend you have ConnectedSystemSettings that define the export file path that map to the Connector Files Docker volume.
    /// You can map a network share on the Docker host and expose this to JIM using the Connector Files volume.
    /// </summary>
    /// <param name="settings">The connected system settings the user has specified. Recommend this is where you pass in the output file path.</param>
    /// <param name="pendingExports">The connected system object pending exports that need to write to the output file for the connected system to consume.</param>
    /// <param name="cancellationToken">Cancellation token to cancel the export operation.</param>
    /// <returns>A list of ConnectedSystemExportResult objects corresponding to each pending export. For file-based exports, ExternalId is typically not available.</returns>
    public Task<List<ConnectedSystemExportResult>> ExportAsync(IList<ConnectedSystemSettingValue> settings, IList<PendingExport> pendingExports, CancellationToken cancellationToken);
}
