// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Staging;
using JIM.Models.Transactional;
namespace JIM.Models.Interfaces;

public interface IConnectorExportUsingCalls
{
    public void OpenExportConnection(IList<ConnectedSystemSettingValue> settings);

    /// <summary>
    /// Exports pending changes to the Connected System.
    /// Returns a list of ConnectedSystemExportResult objects, one per Pending Export, in the same order.
    /// For Create operations, the ConnectedSystemExportResult should include the system-assigned ExternalId (e.g., objectGUID).
    /// </summary>
    /// <param name="pendingExports">The list of Pending Exports to process.</param>
    /// <param name="cancellationToken">Cancellation token to cancel the export operation.</param>
    /// <param name="progressCallback">Optional callback for narrating the connector's internal sub-phases (i.e. "Creating parent containers..."). JIM surfaces each message on the Activity, replacing the previous one, so operators can tell a healthy long-running export from a stuck one. JIM already reports per-item progress around this call, so reserve this for pre-flight or bulk work that per-item counts do not cover. The vocabulary is yours; emit on phase transitions rather than per item, and skip building messages entirely when this is null.</param>
    /// <returns>A list of ConnectedSystemExportResult objects corresponding to each Pending Export.</returns>
    public Task<List<ConnectedSystemExportResult>> ExportAsync(IList<PendingExport> pendingExports, CancellationToken cancellationToken, Func<string, Task>? progressCallback = null);

    public void CloseExportConnection();
}
