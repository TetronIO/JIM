// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Interfaces;
using JIM.Models.Staging;
using JIM.Worker.UniqueValues;
using Serilog;

namespace JIM.Worker.Tests.UniqueValues;

/// <summary>
/// A scripted <see cref="IUniquenessProbeSessionHost"/> for probe session and workflow tests: Connected Systems and
/// control values come from what the test registers, and each created Connector is a <see cref="FakeProbingConnector"/>
/// the test configured in advance.
/// </summary>
internal sealed class FakeUniquenessProbeHost : IUniquenessProbeSessionHost
{
    private readonly Dictionary<int, ConnectedSystem> _systems = [];
    private readonly Dictionary<int, Func<ConnectedSystem, IConnector>> _connectors = [];
    private readonly Dictionary<int, IReadOnlyList<string>> _controlValues = [];

    public int LoadCount { get; private set; }

    public int CreateConnectorCount { get; private set; }

    public bool Disposed { get; private set; }

    /// <summary>
    /// Answers control values from this function instead of the registered lists (a workflow test reads the
    /// in-memory repository).
    /// </summary>
    public Func<int, int, Task<IReadOnlyList<string>>>? ControlValueSource { get; set; }

    public FakeUniquenessProbeHost WithSystem(ConnectedSystem connectedSystem, Func<ConnectedSystem, IConnector> connector)
    {
        _systems[connectedSystem.Id] = connectedSystem;
        _connectors[connectedSystem.Id] = connector;
        return this;
    }

    public FakeUniquenessProbeHost WithControlValues(int attributeId, params string[] values)
    {
        _controlValues[attributeId] = values;
        return this;
    }

    public Task<ConnectedSystem?> LoadConnectedSystemAsync(int connectedSystemId)
    {
        LoadCount++;
        return Task.FromResult(_systems.TryGetValue(connectedSystemId, out var system) ? system : null);
    }

    public IConnector CreateConnector(ConnectedSystem connectedSystem)
    {
        CreateConnectorCount++;
        return _connectors[connectedSystem.Id](connectedSystem);
    }

    public Task<IReadOnlyList<string>> GetControlValuesAsync(int connectedSystemObjectTypeAttributeId, int maximumCount)
    {
        if (ControlValueSource != null)
            return ControlValueSource(connectedSystemObjectTypeAttributeId, maximumCount);

        return Task.FromResult(_controlValues.TryGetValue(connectedSystemObjectTypeAttributeId, out var values) ? values : (IReadOnlyList<string>)[]);
    }

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// A probe-capable Connected System with one object type holding the given attributes.
    /// </summary>
    public static ConnectedSystem ProbingSystem(int id, string name, bool supportsProbe = true, params ConnectedSystemObjectTypeAttribute[] attributes)
    {
        var objectType = new ConnectedSystemObjectType { Id = id * 10, Name = "user", Attributes = attributes.ToList() };
        return new ConnectedSystem
        {
            Id = id,
            Name = name,
            ConnectorDefinition = new ConnectorDefinition { Name = "Fake Probing Connector", SupportsUniquenessProbe = supportsProbe },
            ObjectTypes = [objectType]
        };
    }
}

/// <summary>
/// A Connector that probes from an in-memory set of values held, with switches for every fault the session must
/// survive. Tracks whether its connection is open the way a real one would: set when the open completes, cleared by
/// close, so a test can prove nothing is left open.
/// </summary>
internal sealed class FakeProbingConnector : IConnector, IConnectorUniquenessProbe, IDisposable
{
    private readonly HashSet<string> _held = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    public FakeProbingConnector(params string[] held)
    {
        foreach (var value in held)
            _held.Add(value);
    }

    public string Name => "Fake Probing Connector";

    public string? Description => null;

    public string? Url => null;

    /// <summary>Thrown by the open, when set.</summary>
    public Exception? OpenThrows { get; set; }

    /// <summary>When set, the open blocks until this is released.</summary>
    public ManualResetEventSlim? OpenGate { get; set; }

    /// <summary>Thrown by the probe, when set.</summary>
    public Exception? ProbeThrows { get; set; }

    /// <summary>Returned by the probe instead of answering from the held values, when set.</summary>
    public Func<UniquenessProbeRequest, UniquenessProbeResult>? ProbeAnswer { get; set; }

    /// <summary>Attributes the Connector says a system-wide search cannot answer for.</summary>
    public HashSet<string> UnprobeableAttributes { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>When true, the probe ignores the control value and returns only candidates it holds.</summary>
    public bool BlindToControl { get; set; }

    public bool IsOpen { get; private set; }

    public int OpenCount { get; private set; }

    public int CloseCount { get; private set; }

    public int DisposeCount { get; private set; }

    public List<UniquenessProbeRequest> Requests { get; } = [];

    public void OpenUniquenessProbeConnection(ConnectedSystem connectedSystem, ILogger logger)
    {
        lock (_lock)
            OpenCount++;

        OpenGate?.Wait();

        if (OpenThrows != null)
            throw OpenThrows;

        lock (_lock)
            IsOpen = true;
    }

    public bool CanProbeAttribute(string attributeName) => !UnprobeableAttributes.Contains(attributeName);

    public Task<UniquenessProbeResult> ProbeAsync(UniquenessProbeRequest request, ILogger logger, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            if (!IsOpen)
                throw new InvalidOperationException("The probe connection is not open.");

            Requests.Add(request);
        }

        if (ProbeThrows != null)
            throw ProbeThrows;

        if (ProbeAnswer != null)
            return Task.FromResult(ProbeAnswer(request));

        var asked = request.ControlValue == null || BlindToControl ? request.Candidates : request.Candidates.Append(request.ControlValue);
        var found = asked.Where(v => _held.Contains(v) && !(BlindToControl && v == request.ControlValue)).ToList();
        return Task.FromResult(UniquenessProbeResult.FromValuesFound(request, found));
    }

    public void CloseUniquenessProbeConnection()
    {
        lock (_lock)
        {
            CloseCount++;
            IsOpen = false;
        }
    }

    public void Dispose()
    {
        lock (_lock)
            DisposeCount++;
    }
}
