// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Staging;
using Serilog;

namespace JIM.Models.Interfaces;

public interface IConnectorImportUsingCalls
{
    /// <summary>
    /// Opens a connection to the Connected System, ready for import operations.
    /// </summary>
    /// <param name="settingValues">The Connected System's configured setting values (host, credentials, etc).</param>
    /// <param name="persistedConnectorData">The previously persisted connector state, replayed so the connector can use it when establishing the connection (for example, a pinned directory server); null when nothing has been persisted yet.</param>
    /// <param name="logger">Use this log to record information in the JIM logs, i.e. debug, info, warnings, errors, etc.</param>
    public void OpenImportConnection(List<ConnectedSystemSettingValue> settingValues, string? persistedConnectorData, ILogger logger);

    /// <summary>
    /// Used by JIM.Service to retrieve data from the Connected System. This will be called multiple times, depending on the user-configured page size, and whether there are more results to retrieve after a page of results.
    /// </summary>
    /// <param name="connectedSystem">Contains informaton on the Connected System, i.e. schema, containers, etc.</param>
    /// <param name="runProfile">Contains information on what type of synchronisation run to perform.</param>
    /// <param name="paginationTokens">If you previously supplied pagination tokens as part of returning a page of results to JIM, then they will be played back to you on the next call to ImportAsync().</param>
    /// <param name="persistedConnectorData">If you have previously returned a value to JIM for ConnectedSystemImportResult.PersistedConnectorData, then this is the replayed value. Useful for knowing what the state of a previous synchronisarion run was, i.e. for determining where to query from in a Delta Import run.</param>
    /// <param name="logger">Use this log to record information in the JIM logs, i.e. debug, info, warnings, errort, etc.</param>
    /// <param name="cancellationToken">Connector operations are often long-running. To enable a user to cancel a task, or for the system to shut down gracefully, you should periodically check to see if cancellation has been requested via the token, and stop work if so.</param>
    /// <param name="progress">Narrates what you are doing, and moves between the phases you declared through <see cref="IConnectorPhases"/> (i.e. "Querying root DSE...", "Fetching User objects from Employees (page 3)..."). Never null. JIM already reports page-level progress around this call. The vocabulary is yours; emit on phase and page boundaries rather than per object.</param>
    /// <returns>A composite object that contains details of imported objects, and metadata about the import process.</returns>
    public Task<ConnectedSystemImportResult> ImportAsync(ConnectedSystem connectedSystem, ConnectedSystemRunProfile runProfile, List<ConnectedSystemPaginationToken> paginationTokens, string? persistedConnectorData, ILogger logger, CancellationToken cancellationToken, IConnectorProgress progress);

    /// <summary>
    /// Closes the connection to the Connected System opened by <see cref="OpenImportConnection"/>.
    /// </summary>
    /// <returns>Return null to leave the persisted connector state unchanged (the normal case); return a value only when the connector needs JIM to persist updated state that no import result carried (for example, connection-open failed in a way that must invalidate persisted state). A non-null return is persisted by the worker AFTER any import-result persistence, so only return non-null when that override is intended.</returns>
    public string? CloseImportConnection();
}