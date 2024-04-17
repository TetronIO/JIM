using JIM.Models.Interfaces;
using JIM.Models.Staging;
using Serilog;

namespace JIM.Connectors.Mock;

/// <summary>
/// For unit-testing Jim synchronisation functionality. Not for use in a deployed solution.
/// </summary>
public class MockFileConnector : IConnector, IConnectorCapabilities, IConnectorSchema, IConnectorImportUsingFiles
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
        throw new NotImplementedException();
    }

    public Task<ConnectorSchema> GetSchemaAsync(List<ConnectedSystemSettingValue> settings, ILogger logger)
    {
        throw new NotImplementedException();
    }
    
    #region unit-test specific
    /// <summary>
    /// Enables a unit-test to mock the objects that would be returned from the connected system by passing it in to the Connector, for it to return back to Jim.
    /// </summary>
    public List<ConnectedSystemImportObject> TestImportObjects { get; set; }
    #endregion
}