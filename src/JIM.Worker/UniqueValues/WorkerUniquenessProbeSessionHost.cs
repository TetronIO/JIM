// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Application.Interfaces;
using JIM.Application.Services;
using JIM.Connectors;
using JIM.Models.Interfaces;
using JIM.Models.Staging;

namespace JIM.Worker.UniqueValues;

/// <summary>
/// The worker's <see cref="IUniquenessProbeSessionHost"/> (Unique Value Generation, #242, release 3). Reads through a
/// <see cref="JimApplication"/> of its own, created on first use, so nothing a probe loads (another Connected System's
/// settings, object types and attributes) ever enters the synchronisation run's change tracker, where it could be
/// mistaken for, or collide with, the run's own tracked graph. A run that never probes creates nothing.
/// </summary>
public sealed class WorkerUniquenessProbeSessionHost : IUniquenessProbeSessionHost
{
    private readonly IJimApplicationFactory _jimFactory;
    private readonly IConnectorFactory _connectorFactory;
    private readonly List<JimApplication> _certificateApplications = [];
    private readonly object _lock = new();
    private JimApplication? _jim;
    private bool _disposed;

    public WorkerUniquenessProbeSessionHost(IJimApplicationFactory jimFactory, IConnectorFactory connectorFactory)
    {
        _jimFactory = jimFactory;
        _connectorFactory = connectorFactory;
    }

    private JimApplication Jim
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _jim ??= _jimFactory.Create();
        }
    }

    /// <inheritdoc />
    public Task<ConnectedSystem?> LoadConnectedSystemAsync(int connectedSystemId) =>
        Jim.ConnectedSystems.GetConnectedSystemAsync(connectedSystemId);

    /// <inheritdoc />
    public IConnector CreateConnector(ConnectedSystem connectedSystem)
    {
        ArgumentNullException.ThrowIfNull(connectedSystem);
        ObjectDisposedException.ThrowIf(_disposed, this);

        // The certificate provider reads the JIM certificate store while the Connector opens its connection, which the
        // session runs off the run's own flow and may abandon on a timeout. A JimApplication of its own per Connector
        // means that read can never share a DbContext with this host's reads, which DbContext does not allow.
        var certificateApplication = _jimFactory.Create();
        lock (_lock)
            _certificateApplications.Add(certificateApplication);

        return _connectorFactory.Create(
            connectedSystem.ConnectorDefinition.Name,
            new CredentialProtectionService(DataProtectionHelper.CreateProvider()),
            new CertificateProviderService(certificateApplication));
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<string>> GetControlValuesAsync(int connectedSystemObjectTypeAttributeId, int maximumCount) =>
        Jim.SyncRepository.GetConnectedSystemAttributeSampleValuesAsync(connectedSystemObjectTypeAttributeId, maximumCount);

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        if (_disposed)
            return ValueTask.CompletedTask;

        _disposed = true;
        _jim?.Dispose();
        _jim = null;

        lock (_lock)
        {
            foreach (var application in _certificateApplications)
                application.Dispose();
            _certificateApplications.Clear();
        }

        return ValueTask.CompletedTask;
    }
}
