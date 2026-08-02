// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
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
public class MockCallConnector : IConnector, IConnectorCapabilities, IConnectorImportUsingCalls, IConnectorExportUsingCalls, IConnectorPasswordManagement
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
    public bool SupportsParallelExport => true;
    public bool SupportsPaging => true;
    public bool SupportsFilePaths => false;
    public AttributeStandard SchemaStandard => AttributeStandard.NotSet;

    public bool SupportsPasswordSet => true;

    public bool SupportsPasswordPolicyDiscovery => true;

    private bool _supportsSecondaryExternalId = true;
    private readonly Queue<ConnectedSystemImportResult> _importResultQueue = new();
    private readonly List<PendingExport> _exportedItems = new();
    private readonly Dictionary<Guid, ConnectedSystemExportResult> _exportResultOverrides = new();
    private Func<PendingExport, ConnectedSystemExportResult>? _exportResultFactory;
    private Func<PendingExport, ConnectedSystemImportObject>? _confirmingImportFactory;

    // Issue #230 slice 1 plumbing: configurable Close return values, defaulting to null (the
    // overwhelmingly common case), and the persisted connector data most recently passed to Open.
    private string? _closeImportConnectionReturnValue;
    private string? _closeExportConnectionReturnValue;

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
    /// Sets a factory function to generate export results for each Pending Export.
    /// If not set, all exports succeed by default.
    /// </summary>
    public MockCallConnector WithConnectedSystemExportResultFactory(Func<PendingExport, ConnectedSystemExportResult> factory)
    {
        _exportResultFactory = factory;
        return this;
    }

    /// <summary>
    /// Sets a specific export result for a Pending Export ID.
    /// Takes precedence over the export result factory.
    /// </summary>
    public MockCallConnector WithConnectedSystemExportResult(Guid pendingExportId, ConnectedSystemExportResult result)
    {
        _exportResultOverrides[pendingExportId] = result;
        return this;
    }

    /// <summary>
    /// Sets a factory to generate confirming import objects from exported Pending Exports.
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

    /// <summary>
    /// Configures the value <see cref="CloseImportConnection"/> returns. Defaults to null (the
    /// normal case: leave persisted connector state unchanged). Set to a non-null value to simulate
    /// a connector that needs JIM to persist updated state when the connection closes.
    /// </summary>
    public MockCallConnector WithCloseImportConnectionReturnValue(string? value)
    {
        _closeImportConnectionReturnValue = value;
        return this;
    }

    /// <summary>
    /// Configures the value <see cref="CloseExportConnection"/> returns. Defaults to null (the
    /// normal case: leave persisted connector state unchanged). Set to a non-null value to simulate
    /// a connector that needs JIM to persist updated state when the connection closes.
    /// </summary>
    public MockCallConnector WithCloseExportConnectionReturnValue(string? value)
    {
        _closeExportConnectionReturnValue = value;
        return this;
    }

    #endregion

    #region State Accessors

    /// <summary>
    /// Gets the persisted connector data values passed to each ImportAsync call.
    /// Useful for verifying that the correct watermark is passed during paginated imports.
    /// </summary>
    public List<string?> ImportPersistedDataHistory { get; } = new();

    /// <summary>
    /// Gets the persisted connector data value passed to the most recent OpenImportConnection call.
    /// </summary>
    public string? LastOpenImportPersistedConnectorData { get; private set; }

    /// <summary>
    /// Gets the persisted connector data value passed to the most recent OpenExportConnection call.
    /// </summary>
    public string? LastOpenExportPersistedConnectorData { get; private set; }

    /// <summary>
    /// Gets all Pending Exports that were processed during Export calls.
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
        LastOpenImportPersistedConnectorData = null;
        LastOpenExportPersistedConnectorData = null;
        _closeImportConnectionReturnValue = null;
        _closeExportConnectionReturnValue = null;
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

    public void OpenImportConnection(List<ConnectedSystemSettingValue> settingValues, string? persistedConnectorData, ILogger logger)
    {
        LastOpenImportPersistedConnectorData = persistedConnectorData;
    }

    public Task<ConnectedSystemImportResult> ImportAsync(
        ConnectedSystem connectedSystem,
        ConnectedSystemRunProfile runProfile,
        List<ConnectedSystemPaginationToken> paginationTokens,
        string? persistedConnectorData,
        ILogger logger,
        CancellationToken cancellationToken,
        IConnectorProgress progress)
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

    public string? CloseImportConnection()
    {
        return _closeImportConnectionReturnValue;
    }

    #endregion

    #region IConnectorExportUsingCalls Implementation

    public void OpenExportConnection(IList<ConnectedSystemSettingValue> settings, string? persistedConnectorData)
    {
        LastOpenExportPersistedConnectorData = persistedConnectorData;
    }

    public Task<List<ConnectedSystemExportResult>> ExportAsync(IList<PendingExport> pendingExports, CancellationToken cancellationToken, IConnectorProgress progress)
    {
        if (ExportExceptionToThrow != null)
            throw ExportExceptionToThrow;

        var results = new List<ConnectedSystemExportResult>();

        foreach (var pendingExport in pendingExports)
        {
            cancellationToken.ThrowIfCancellationRequested();

            _exportedItems.Add(pendingExport);

            ConnectedSystemExportResult result;

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
                    ? ConnectedSystemExportResult.Succeeded(Guid.NewGuid().ToString()) // Generate a new external ID for creates
                    : ConnectedSystemExportResult.Succeeded();
            }

            results.Add(result);
        }

        return Task.FromResult(results);
    }

    public string? CloseExportConnection()
    {
        return _closeExportConnectionReturnValue;
    }

    #endregion

    #region IConnectorPasswordManagement Implementation

    /// <summary>
    /// Records one password set attempt. The password value is deliberately NOT captured: nothing in JIM keeps a
    /// password after it has been delivered, and a test double that hoards them would make it easy to write a
    /// test that passes only because the production code leaked one.
    /// </summary>
    public record PasswordSetAttempt(Guid ConnectedSystemObjectId, PasswordSetOptions Options, int PasswordLength);

    private readonly List<PasswordSetAttempt> _passwordSetAttempts = new();
    private Func<ConnectedSystemObject, PasswordSetResult>? _passwordSetResultFactory;

    /// <summary>
    /// Every password set attempted through this connector, in order.
    /// </summary>
    public IReadOnlyList<PasswordSetAttempt> PasswordSetAttempts => _passwordSetAttempts;

    /// <summary>
    /// Whether OpenPasswordConnection has been called and ClosePasswordConnection has not.
    /// Lets tests assert the channel is opened before use and closed afterwards.
    /// </summary>
    public bool PasswordConnectionOpen { get; private set; }

    /// <summary>
    /// The expiry behaviours this mock reports as supported. Settable so tests can simulate a target that cannot
    /// honour every state.
    /// </summary>
    public IReadOnlyCollection<PasswordExpiryBehaviour> SupportedExpiryBehaviours { get; set; } =
    [
        PasswordExpiryBehaviour.RequireChangeAtNextSignIn,
        PasswordExpiryBehaviour.ExpiresAccordingToTargetPolicy,
        PasswordExpiryBehaviour.NeverExpires
    ];

    /// <summary>
    /// Controls what each password set returns, so tests can simulate policy rejections and transient faults.
    /// Defaults to success.
    /// </summary>
    public MockCallConnector WithPasswordSetResult(Func<ConnectedSystemObject, PasswordSetResult> resultFactory)
    {
        _passwordSetResultFactory = resultFactory;
        return this;
    }

    public void OpenPasswordConnection(IList<ConnectedSystemSettingValue> settings)
    {
        PasswordConnectionOpen = true;
    }

    public Task<PasswordSetResult> SetPasswordAsync(ConnectedSystemObject target, string password, PasswordSetOptions options, CancellationToken cancellationToken)
    {
        if (!PasswordConnectionOpen)
            throw new InvalidOperationException("Must call OpenPasswordConnection() before SetPasswordAsync()!");

        cancellationToken.ThrowIfCancellationRequested();
        _passwordSetAttempts.Add(new PasswordSetAttempt(target.Id, options, password.Length));

        var result = _passwordSetResultFactory?.Invoke(target)
            ?? PasswordSetResult.Succeeded(options.ExpiryBehaviour);

        return Task.FromResult(result);
    }

    public void ClosePasswordConnection()
    {
        PasswordConnectionOpen = false;
    }

    /// <summary>
    /// The preflight result this mock returns. Settable so tests can simulate a target that is not ready.
    /// Defaults to a target where everything JIM can check is in order.
    /// </summary>
    public PasswordPreflightResult PreflightResult { get; set; } = new()
    {
        TargetDescription = "a mock system",
        Checks =
        [
            PasswordPreflightCheckResult.Passed(PasswordPreflightCheck.Connection, "Connected."),
            PasswordPreflightCheckResult.Passed(PasswordPreflightCheck.Encryption, "Encrypted."),
            PasswordPreflightCheckResult.Passed(PasswordPreflightCheck.PasswordMechanism, "Supported.")
        ]
    };

    /// <summary>
    /// The container external ids the last preflight was asked about, so tests can assert that rights are checked
    /// where JIM would actually be provisioning.
    /// </summary>
    public IReadOnlyList<string> LastPreflightContainerExternalIds { get; private set; } = [];

    public Task<PasswordPreflightResult> RunPasswordPreflightAsync(List<ConnectedSystemSettingValue> settings, IReadOnlyList<string> containerExternalIds, ILogger logger, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        LastPreflightContainerExternalIds = containerExternalIds;
        return Task.FromResult(PreflightResult);
    }

    #endregion
}
