using JIM.Models.Interfaces;
using JIM.Models.Staging;
using Serilog;
namespace JIM.Connectors.Mock;

/// <summary>
/// For unit-testing Jim synchronisation functionality. Not for use in a deployed system.
/// </summary>
public class MockFileConnector : IConnector, IConnectorCapabilities, IConnectorImportUsingFiles
{
    public string Name => "Mock Connector";
    public string? Description => "Enables unit testing of synchronisation functionality.";
    public string? Url => "https://github.com/TetronIO/JIM";
    public bool SupportsFullImport => true;
    public bool SupportsDeltaImport => true;
    public bool SupportsExport => true;
    public bool SupportsPartitions => false;
    public bool SupportsPartitionContainers => false;
    public bool SupportsSecondaryExternalId => false;
    public bool SupportsUserSelectedExternalId => true;
    public bool SupportsUserSelectedAttributeTypes => true;

    public Task<ConnectedSystemImportResult> ImportAsync(ConnectedSystem connectedSystem, ConnectedSystemRunProfile runProfile, ILogger logger, CancellationToken cancellationToken)
    {
        // normally this method would go and source data from the connected system, but as we're unit-testing, we want to mock that data
        // so, we just return data that the unit test has passed in by the special test accessor: TestImportObjects.
        var result = new ConnectedSystemImportResult
        {
            ImportObjects = TestImportObjects
        };
        return Task.FromResult(result);
    }
    
    #region unit-test specific
    /// <summary>
    /// Enables a unit-test to mock the objects that would be returned from the connected system by passing it in to the Connector, for it to return back to Jim.
    /// </summary>
    public List<ConnectedSystemImportObject> TestImportObjects { get; set; } = new ();
    #endregion
}
