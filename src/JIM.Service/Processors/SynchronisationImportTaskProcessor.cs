using JIM.Application;
using JIM.Models.History;
using JIM.Models.Interfaces;
using JIM.Models.Staging;
using Serilog;

namespace JIM.Service.Processors
{
    internal class SynchronisationImportTaskProcessor
    {
        private readonly JimApplication _jim;
        private readonly IConnector _connector;
        private readonly ConnectedSystem _connectedSystem;
        private readonly ConnectedSystemRunProfile _connectedSystemRunProfile;
        private readonly SynchronisationRunHistoryDetail _synchronisationRunHistoryDetail;
        private readonly CancellationTokenSource _cancellationTokenSource;
        
        internal SynchronisationImportTaskProcessor(
            JimApplication jimApplication, 
            IConnector connector, 
            ConnectedSystem connectedSystem, 
            ConnectedSystemRunProfile connectedSystemRunProfile, 
            SynchronisationRunHistoryDetail synchronisationRunHistoryDetail, 
            CancellationTokenSource cancellationTokenSource)
        {
            _jim = jimApplication;
            _connector = connector;
            _connectedSystem = connectedSystem;
            _connectedSystemRunProfile = connectedSystemRunProfile;
            _synchronisationRunHistoryDetail = synchronisationRunHistoryDetail;
            _cancellationTokenSource = cancellationTokenSource;
        }

        internal async Task PerformFullImportAsync()
        {
            if (_connector is IConnectorImportUsingCalls callBasedImportConnector)
            {
                callBasedImportConnector.OpenImportConnection(_connectedSystem.SettingValues, Log.Logger);

                var initialPage = true;
                var paginationTokens = new List<ConnectedSystemPaginationToken>();
                var wereResultsReturned = false;
                while (initialPage || paginationTokens.Count > 0 || wereResultsReturned)
                {
                    // perform the import for this page
                    var result = await callBasedImportConnector.ImportAsync(_connectedSystem, _connectedSystemRunProfile, paginationTokens, null, Log.Logger, _cancellationTokenSource.Token);
                    wereResultsReturned = result.ImportObjects.Count > 0;

                    // make sure we pass the pagination tokens back in on the next page (if there is one)
                    paginationTokens = result.PaginationTokens;

                    if (result.PersistedConnectorData != _connectedSystem.PersistedConnectorData)
                    {
                        // the connector wants to persist some data between sync runs. update the connected system with the new value
                        Log.Debug($"ExecuteAsync: updating persisted connector data. old value: '{_connectedSystem.PersistedConnectorData}', new value: '{result.PersistedConnectorData}'");
                        _connectedSystem.PersistedConnectorData = result.PersistedConnectorData;
                        await _jim.ConnectedSystems.UpdateConnectedSystemAsync(_connectedSystem);
                    }

                    // decision: do we want to load the whole connector space into memory to maximise performance? for now, let's keep it db-centric.

                    // enumerate items
                    // see if we need to create a cso or update a existing one (match on unique id)
                    // make the cso changes
                    // create detail item, inc any errors

                    foreach (var importObject in result.ImportObjects)
                    {
                        // is this a new, or existing object as far as we're concerned?
                        // find the unique id attribute for this connected system, and then pull out the attribute value from the importobject

                        
                    }

                    if (initialPage)
                        initialPage = false;

                    // update the history item with the results from this page
                    await _jim.History.UpdateSynchronisationRunAsync(_synchronisationRunHistoryDetail);
                }

                callBasedImportConnector.CloseImportConnection();
            }
            else if (_connector is IConnectorImportUsingFiles)
            {
                throw new NotImplementedException("Import connector using files it not yet supported.");
            }
            else
            {
                throw new NotSupportedException("Connector inheritance type is not supported (not calls, not files)");
            }
        }
    }
}
