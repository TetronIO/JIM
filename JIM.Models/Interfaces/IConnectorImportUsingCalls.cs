using JIM.Models.Staging;
using Serilog;

namespace JIM.Models.Interfaces;

public interface IConnectorImportUsingCalls
{
    public void OpenImportConnection(List<ConnectedSystemSettingValue> settingValues, ILogger logger);

    /// <summary>
    /// Used by JIM.Service to retrieve data from the connected system. This will be called multiple times, depending on the user-configured page size, and whether there are more results to retrieve after a page of results.
    /// </summary>
    /// <param name="connectedSystem">Contains informaton on the connected system, i.e. schema, containers, etc.</param>
    /// <param name="runProfile">Contains information on what type of synchronisation run to perform.</param>
    /// <param name="paginationTokens">If you previously supplied pagination tokens as part of returning a page of results to JIM, then they will be played back to you on the next call to ImportAsync().</param>
    /// <param name="persistedConnectorData">If you have previously returned a value to JIM for ConnectedSystemImportResult.PersistedConnectorData, then this is the replayed value. Useful for knowing what the state of a previous synchronisarion run was, i.e. for determining where to query from in a Delta Import run.</param>
    /// <param name="logger">Use this log to record information in the JIM logs, i.e. debug, info, warnings, errort, etc.</param>
    /// <param name="cancellationToken">Connector operations are often long-running. To enable a user to cancel a task, or for the system to shut down gracefully, you should periodically check to see if cancellation has been requested via the token, and stop work if so.</param>
    /// <returns>A composite object that contains details of imported objects, and metadata about the import process.</returns>
    public Task<ConnectedSystemImportResult> ImportAsync(ConnectedSystem connectedSystem, ConnectedSystemRunProfile runProfile, List<ConnectedSystemPaginationToken> paginationTokens, string? persistedConnectorData, ILogger logger, CancellationToken cancellationToken);

    public void CloseImportConnection();
}