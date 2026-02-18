using JIM.Models.Interfaces;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using Serilog;

namespace JIM.Connectors.Mock;

/// <summary>
/// Mock connector implementing call-based import and export interfaces.
/// For use in workflow tests and integration tests that need full sync cycle simulation.
///
/// Unlike MockFileConnector (which uses IConnectorImportUsingFiles), this connector
/// implements IConnectorImportUsingCalls and IConnectorExportUsingCalls for testing
/// scenarios that require pagination, connection management, and export confirmation.
/// </summary>
public class MockCallConnector : IConnector, IConnectorCapabilities, IConnectorImportUsingCalls, IConnectorExportUsingCalls
{
    public string Name => "Mock Call Connector";
    public string? Description => "Enables workflow and integration testing with call-based import/export.";
    public string? Url => "https://github.com/TetronIO/JIM";
    public bool SupportsFullImport => true;
    public bool SupportsDeltaImport => true;
    public bool SupportsExport => true;
    public bool SupportsPartitions => false;
    public bool SupportsPartitionContainers => false;
    public bool SupportsSecondaryExternalId => _supportsSecondaryExternalId;
    public bool SupportsUserSelectedExternalId => true;
    public bool SupportsUserSelectedAttributeTypes => true;
    public bool SupportsAutoConfirmExport => false;

    private bool _supportsSecondaryExternalId = true;
    private readonly Queue<ConnectedSystemImportResult> _importResultQueue = new();
    private readonly List<PendingExport> _exportedItems = new();
    private readonly Dictionary<Guid, ExportResult> _exportResultOverrides = new();
    private Func<PendingExport, ExportResult>? _exportResultFactory;
    private Func<PendingExport, ConnectedSystemImportObject>? _confirmingImportFactory;

    #region Configuration Methods

    /// <summary>
    /// Configures whether this connector supports secondary external IDs.
    /// </summary>
    public MockCallConnector WithSecondaryExternalIdSupport(bool supported)
    {
        _supportsSecondaryExternalId = supported;
        return this;
    }

    /// <summary>
    /// Queues an import result to be returned on the next ImportAsync call.
    /// Multiple calls queue multiple results (FIFO).
    /// </summary>
    public MockCallConnector QueueImportResult(ConnectedSystemImportResult result)
    {
        _importResultQueue.Enqueue(result);
        return this;
    }

    /// <summary>
    /// Queues import objects to be returned on the next ImportAsync call.
    /// Convenience method that wraps objects in a ConnectedSystemImportResult.
    /// </summary>
    public MockCallConnector QueueImportObjects(params ConnectedSystemImportObject[] objects)
    {
        var result = new ConnectedSystemImportResult
        {
            ImportObjects = objects.ToList()
        };
        _importResultQueue.Enqueue(result);
        return this;
    }

    /// <summary>
    /// Queues import objects to be returned on the next ImportAsync call.
    /// </summary>
    public MockCallConnector QueueImportObjects(IEnumerable<ConnectedSystemImportObject> objects)
    {
        var result = new ConnectedSystemImportResult
        {
            ImportObjects = objects.ToList()
        };
        _importResultQueue.Enqueue(result);
        return this;
    }

    /// <summary>
    /// Sets a factory function to generate export results for each pending export.
    /// If not set, all exports succeed by default.
    /// </summary>
    public MockCallConnector WithExportResultFactory(Func<PendingExport, ExportResult> factory)
    {
        _exportResultFactory = factory;
        return this;
    }

    /// <summary>
    /// Sets a specific export result for a pending export ID.
    /// Takes precedence over the export result factory.
    /// </summary>
    public MockCallConnector WithExportResult(Guid pendingExportId, ExportResult result)
    {
        _exportResultOverrides[pendingExportId] = result;
        return this;
    }

    /// <summary>
    /// Sets a factory to generate confirming import objects from exported pending exports.
    /// Used to simulate the target system returning the objects we just created.
    /// </summary>
    public MockCallConnector WithConfirmingImportFactory(Func<PendingExport, ConnectedSystemImportObject> factory)
    {
        _confirmingImportFactory = factory;
        return this;
    }

    /// <summary>
    /// Configures the connector to throw this exception during ImportAsync.
    /// </summary>
    public Exception? TestExceptionToThrow { get; set; }

    /// <summary>
    /// Configures the connector to throw this exception during Export.
    /// </summary>
    public Exception? ExportExceptionToThrow { get; set; }

    #endregion

    #region State Accessors

    /// <summary>
    /// Gets the persisted connector data values passed to each ImportAsync call.
    /// Useful for verifying that the correct watermark is passed during paginated imports.
    /// </summary>
    public List<string?> ImportPersistedDataHistory { get; } = new();

    /// <summary>
    /// Gets all pending exports that were processed during Export calls.
    /// Useful for verifying what was sent to the "target system".
    /// </summary>
    public IReadOnlyList<PendingExport> ExportedItems => _exportedItems;

    /// <summary>
    /// Gets the number of import results still queued.
    /// </summary>
    public int QueuedImportResultCount => _importResultQueue.Count;

    /// <summary>
    /// Clears all queued import results and exported items.
    /// Call this between test scenarios if reusing the connector.
    /// </summary>
    public void Reset()
    {
        _importResultQueue.Clear();
        _exportedItems.Clear();
        _exportResultOverrides.Clear();
        _exportResultFactory = null;
        _confirmingImportFactory = null;
        TestExceptionToThrow = null;
        ExportExceptionToThrow = null;
        ImportPersistedDataHistory.Clear();
    }

    /// <summary>
    /// Generates confirming import objects for all successfully exported Create operations.
    /// Call this to prepare import results that simulate the target system returning
    /// the objects we just provisioned.
    /// </summary>
    public List<ConnectedSystemImportObject> GenerateConfirmingImportObjects()
    {
        if (_confirmingImportFactory == null)
        {
            throw new InvalidOperationException(
                "No confirming import factory configured. Call WithConfirmingImportFactory first.");
        }

        return _exportedItems
            .Where(pe => pe.ChangeType == PendingExportChangeType.Create)
            .Select(pe => _confirmingImportFactory(pe))
            .ToList();
    }

    /// <summary>
    /// Queues confirming import objects based on previously exported items.
    /// Convenience method that calls GenerateConfirmingImportObjects and queues the result.
    /// </summary>
    public MockCallConnector QueueConfirmingImport()
    {
        var confirmingObjects = GenerateConfirmingImportObjects();
        if (confirmingObjects.Count > 0)
        {
            QueueImportObjects(confirmingObjects);
        }
        return this;
    }

    #endregion

    #region IConnectorImportUsingCalls Implementation

    public void OpenImportConnection(List<ConnectedSystemSettingValue> settingValues, ILogger logger)
    {
        // No-op for mock
    }

    public Task<ConnectedSystemImportResult> ImportAsync(
        ConnectedSystem connectedSystem,
        ConnectedSystemRunProfile runProfile,
        List<ConnectedSystemPaginationToken> paginationTokens,
        string? persistedConnectorData,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        // Record the persisted data passed on each call for test verification
        ImportPersistedDataHistory.Add(persistedConnectorData);

        if (TestExceptionToThrow != null)
            throw TestExceptionToThrow;

        if (_importResultQueue.Count == 0)
        {
            // Return empty result if nothing queued
            return Task.FromResult(new ConnectedSystemImportResult
            {
                ImportObjects = new List<ConnectedSystemImportObject>()
            });
        }

        var result = _importResultQueue.Dequeue();
        return Task.FromResult(result);
    }

    public void CloseImportConnection()
    {
        // No-op for mock
    }

    #endregion

    #region IConnectorExportUsingCalls Implementation

    public void OpenExportConnection(IList<ConnectedSystemSettingValue> settings)
    {
        // No-op for mock
    }

    public Task<List<ExportResult>> ExportAsync(IList<PendingExport> pendingExports, CancellationToken cancellationToken)
    {
        if (ExportExceptionToThrow != null)
            throw ExportExceptionToThrow;

        var results = new List<ExportResult>();

        foreach (var pendingExport in pendingExports)
        {
            cancellationToken.ThrowIfCancellationRequested();

            _exportedItems.Add(pendingExport);

            ExportResult result;

            // Check for specific override first
            if (_exportResultOverrides.TryGetValue(pendingExport.Id, out var overrideResult))
            {
                result = overrideResult;
            }
            // Then try factory
            else if (_exportResultFactory != null)
            {
                result = _exportResultFactory(pendingExport);
            }
            // Default to success
            else
            {
                result = pendingExport.ChangeType == PendingExportChangeType.Create
                    ? ExportResult.Succeeded(Guid.NewGuid().ToString()) // Generate a new external ID for creates
                    : ExportResult.Succeeded();
            }

            results.Add(result);
        }

        return Task.FromResult(results);
    }

    public void CloseExportConnection()
    {
        // No-op for mock
    }

    #endregion
}
