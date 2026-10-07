// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Globalization;
using JIM.Application.UniqueValues;
using JIM.Models.Interfaces;
using JIM.Models.Staging;
using JIM.Utilities;
using Serilog;

namespace JIM.Worker.UniqueValues;

/// <summary>
/// The worker's run-scoped live probe of a synchronisation's participating Connected Systems (Unique Value
/// Generation, #242, release 3, plan Phase 7 item 2). One instance per synchronisation run that generates a value.
/// <list type="bullet">
/// <item>Opens one connection per probing Connected System, lazily on first use, with that system's own credentials,
/// reuses it for every batch in the run, and closes it in <see cref="DisposeAsync"/>.</item>
/// <item>Chooses each (system, attribute)'s control value from what JIM already holds in that Connector Space, never
/// one of the batch's candidates; with none, the probe runs without one (plan decision 14, revised 2026-10-06).</item>
/// <item>Bounds every connection and search by <see cref="ProbeTimeout"/>. A connection failure, timeout, refusal or
/// error makes the system undetermined for the rest of the run, with the reason recorded, and is never retried per
/// object; a search that completes without returning its control does the same for that attribute only.</item>
/// <item>Collects one warning per Connected System per run (<see cref="GetRunWarnings"/>), never one per object.</item>
/// </list>
/// Nothing here fails the run because a target cannot be probed: the probe gate then accepts on JIM's own records.
/// </summary>
public sealed class UniquenessProbeSession : IUniquenessProbeSession, IAsyncDisposable
{
    /// <summary>
    /// How long one connection or one search may take before the system is treated as unreachable for the run.
    /// </summary>
    public static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How many control value candidates to read per attribute: more than one, so a batch whose candidates include
    /// one of them (the person's own value, typically) still has another to vouch for the search.
    /// </summary>
    private const int ControlValueSampleSize = 3;

    private readonly IUniquenessProbeSessionHost _host;
    private readonly CancellationToken _cancellationToken;
    private readonly TimeSpan _timeout;
    private readonly Serilog.ILogger _logger = Log.ForContext<UniquenessProbeSession>();
    private readonly Dictionary<int, SystemState> _systems = [];
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>
    /// Constructs a session over <paramref name="host"/>, which it owns and disposes.
    /// </summary>
    /// <param name="host">The worker's access to Connected Systems, Connectors and control values.</param>
    /// <param name="cancellationToken">The run's cancellation: a cancelled run stops probing and propagates.</param>
    /// <param name="timeout">Overrides <see cref="ProbeTimeout"/> (tests only).</param>
    public UniquenessProbeSession(IUniquenessProbeSessionHost host, CancellationToken cancellationToken, TimeSpan? timeout = null)
    {
        _host = host;
        _cancellationToken = cancellationToken;
        _timeout = timeout ?? ProbeTimeout;
    }

    /// <inheritdoc />
    public async Task<UniquenessProbeSessionResult> ProbeAsync(UniquenessProbeTarget target, IReadOnlyList<string> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);

        // One batch at a time: the session's state and each Connector's connection are not built for concurrent use,
        // and the worker resolves objects one after another in any case.
        await _gate.WaitAsync(_cancellationToken);
        try
        {
            return await ProbeCoreAsync(target, candidates);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public void RecordValueChosenWithoutProbe(int connectedSystemId)
    {
        if (_systems.TryGetValue(connectedSystemId, out var state))
            state.ValuesChosenWithoutProbe++;
    }

    /// <summary>
    /// One warning per Connected System that could not be probed for a value JIM went on to issue this run, in the
    /// order the systems were first probed. Empty when every probe answered, or when nothing was issued without one.
    /// </summary>
    public IReadOnlyList<string> GetRunWarnings() =>
        _systems.Values
            .Where(s => s.UndeterminedReason != null && s.ValuesChosenWithoutProbe > 0)
            .Select(s => string.Create(CultureInfo.InvariantCulture,
                $"JIM couldn't probe {s.Name} for values already in use. {s.UndeterminedReason}. JIM chose {s.ValuesChosenWithoutProbe} {(s.ValuesChosenWithoutProbe == 1 ? "value" : "values")} using its own records only."))
            .ToList();

    /// <summary>
    /// Closes every connection the run opened and releases the host. Never throws for a Connector fault: the run is
    /// over, and a connection that will not close cleanly is logged, not allowed to change its outcome.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        foreach (var state in _systems.Values.Where(s => s.Connector != null))
        {
            try
            {
                state.Probe?.CloseUniquenessProbeConnection();
                (state.Connector as IDisposable)?.Dispose();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Deliberately broad, with the cancellation exclusion the fallback-dispatcher rule requires: closing
                // a probe connection is housekeeping at the end of a run, and no Connector fault here may fail it.
                _logger.Warning(ex, "UniquenessProbeSession: Closing the probe connection to Connected System {ConnectedSystemId} failed.", state.Id);
            }
        }

        foreach (var state in _systems.Values.Where(s => s.UndeterminedReason != null))
        {
            _logger.Warning("UniquenessProbeSession: Connected System {ConnectedSystemId} could not be probed for values already in use ({Reason}); {Count} value(s) were chosen using JIM's own records only.",
                state.Id, LogSanitiser.Sanitise(state.UndeterminedReason), state.ValuesChosenWithoutProbe);
        }

        _systems.Clear();
        _gate.Dispose();
        await _host.DisposeAsync();
    }

    private async Task<UniquenessProbeSessionResult> ProbeCoreAsync(UniquenessProbeTarget target, IReadOnlyList<string> candidates)
    {
        var system = await GetOrOpenSystemAsync(target.ConnectedSystemId);

        if (!system.Probes)
            return UniquenessProbeSessionResult.NotProbed(system.Name, candidates.Count);

        if (system.LatchedReason != null)
            return Undetermined(system, candidates.Count, system.LatchedReason);

        var attribute = await GetOrPrepareAttributeAsync(system, target.ConnectedSystemObjectTypeAttributeId);

        if (!attribute.Probes)
            return UniquenessProbeSessionResult.NotProbed(system.Name, candidates.Count);

        if (attribute.LatchedReason != null)
            return Undetermined(system, candidates.Count, attribute.LatchedReason);

        // With no usable control (JIM holds no value for the attribute yet, or every one it holds is among this
        // batch's candidates), the probe still runs: a value it returns is in use whatever the bind can see, and a
        // miss is accepted unconfirmed without a warning, since an empty target is normal on a first load (plan
        // decision 14, revised 2026-10-06).
        var controlValue = attribute.ControlValues.FirstOrDefault(v => !candidates.Contains(v, StringComparer.OrdinalIgnoreCase));

        var request = new UniquenessProbeRequest
        {
            ObjectTypeName = attribute.ObjectTypeName,
            AttributeName = attribute.Name,
            Candidates = candidates,
            ControlValue = controlValue,
            Timeout = _timeout
        };

        UniquenessProbeResult result;
        try
        {
            var probe = system.Probe!;
            result = await Task.Run(() => probe.ProbeAsync(request, _logger, _cancellationToken), _cancellationToken).WaitAsync(_timeout, _cancellationToken);
        }
        catch (TimeoutException)
        {
            return LatchSystem(system, candidates.Count, $"It did not answer within {_timeout.TotalSeconds:0} seconds");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Deliberately broad, with the cancellation exclusion: whatever a Connector throws while searching, the
            // run carries on with JIM's own records, and the system is not asked again this run.
            _logger.Warning(ex, "UniquenessProbeSession: Searching Connected System {ConnectedSystemId} for values already in use failed.", system.Id);
            return LatchSystem(system, candidates.Count, $"The probe failed: {ex.Message.TrimEnd('.')}");
        }

        if (result.Outcomes.Count != candidates.Count)
            return LatchSystem(system, candidates.Count, "Its Connector answered for a different number of values than it was asked about");

        if (result.IsFailure)
            return LatchSystem(system, candidates.Count, result.Reason ?? "The probe failed");

        if (result.Reason != null)
        {
            // Completed, but without its control: blind to this attribute (an attribute-level permission, say), which
            // says nothing about the system's other attributes.
            attribute.LatchedReason = result.Reason;
            return Undetermined(system, candidates.Count, result.Reason);
        }

        return new UniquenessProbeSessionResult(system.Name, result.Outcomes);
    }

    private async Task<SystemState> GetOrOpenSystemAsync(int connectedSystemId)
    {
        if (_systems.TryGetValue(connectedSystemId, out var existing))
            return existing;

        var connectedSystem = await _host.LoadConnectedSystemAsync(connectedSystemId);
        var state = new SystemState(connectedSystemId, connectedSystem?.Name ?? $"Connected System {connectedSystemId}");
        _systems[connectedSystemId] = state;

        if (connectedSystem == null)
        {
            // Reported, not silently skipped: a participating system JIM cannot even load is one it could not check.
            state.Probes = true;
            state.LatchedReason = "It could not be found";
            return state;
        }

        // The declared capability decides whether a system is probed at all; a Connector that cannot probe is
        // checked against JIM's own records, as before release 3, and is not reported as undetermined.
        if (!connectedSystem.ConnectorDefinition.SupportsUniquenessProbe)
            return state;

        try
        {
            state.Connector = _host.CreateConnector(connectedSystem);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Deliberately broad: a Connector that cannot even be created must not fail the run.
            _logger.Warning(ex, "UniquenessProbeSession: Could not create the Connector for Connected System {ConnectedSystemId}.", connectedSystemId);
            state.LatchedReason = $"Its Connector could not be created: {ex.Message.TrimEnd('.')}";
            state.Probes = true;
            return state;
        }

        if (state.Connector is not IConnectorUniquenessProbe probe)
        {
            _logger.Warning("UniquenessProbeSession: Connected System {ConnectedSystemId}'s Connector declares the uniqueness probe but does not implement it; checking against JIM's own records only.", connectedSystemId);
            return state;
        }

        state.Probe = probe;
        state.Probes = true;
        state.ConnectedSystem = connectedSystem;

        var open = Task.Run(() => probe.OpenUniquenessProbeConnection(connectedSystem, _logger), _cancellationToken);
        try
        {
            await open.WaitAsync(_timeout, _cancellationToken);
            _logger.Debug("UniquenessProbeSession: Opened the probe connection to Connected System {ConnectedSystemId}.", connectedSystemId);
        }
        catch (TimeoutException)
        {
            state.LatchedReason = $"Connecting to it did not complete within {_timeout.TotalSeconds:0} seconds";

            // The open is still running and may yet succeed. Closing now would race it and leave the late connection
            // open, so the session lets go of the Connector and the open itself closes it once it finishes.
            var connector = state.Connector;
            state.Connector = null;
            state.Probe = null;
            _ = open.ContinueWith(_ => CloseOrphanedConnector(probe, connector, connectedSystemId), CancellationToken.None,
                TaskContinuationOptions.None, TaskScheduler.Default);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Deliberately broad, with the cancellation exclusion: an unreachable or refusing target degrades to
            // JIM's own records for the rest of the run, whatever the Connector throws.
            _logger.Warning(ex, "UniquenessProbeSession: Opening the probe connection to Connected System {ConnectedSystemId} failed.", connectedSystemId);
            state.LatchedReason = $"Connecting to it failed: {ex.Message.TrimEnd('.')}";
        }

        return state;
    }

    private void CloseOrphanedConnector(IConnectorUniquenessProbe probe, IConnector? connector, int connectedSystemId)
    {
        try
        {
            probe.CloseUniquenessProbeConnection();
            (connector as IDisposable)?.Dispose();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Deliberately broad, with the cancellation exclusion: this runs after the session has moved on, possibly
            // after the run has ended, and a Connector fault here must not surface anywhere but the log.
            _logger.Warning(ex, "UniquenessProbeSession: Closing the late probe connection to Connected System {ConnectedSystemId} failed.", connectedSystemId);
        }
    }

    private async Task<AttributeState> GetOrPrepareAttributeAsync(SystemState system, int attributeId)
    {
        if (system.Attributes.TryGetValue(attributeId, out var existing))
            return existing;

        // The object type travels with the attribute: a SCIM provider is searched at that resource type's endpoint and a
        // database in that Object Type's table (#1941).
        var objectType = system.ConnectedSystem!.ObjectTypes?.FirstOrDefault(t => t.Attributes.Any(a => a.Id == attributeId));
        var definition = objectType?.Attributes.First(a => a.Id == attributeId);

        var state = new AttributeState(definition?.Name ?? $"attribute {attributeId}", objectType?.Name ?? string.Empty);
        system.Attributes[attributeId] = state;

        if (definition == null)
        {
            state.LatchedReason = $"JIM could not find {state.Name} in its schema";
            return state;
        }

        if (!system.Probe!.CanProbeAttribute(definition.Name))
        {
            state.Probes = false;
            return state;
        }

        state.ControlValues = await _host.GetControlValuesAsync(attributeId, ControlValueSampleSize);

        return state;
    }

    private static UniquenessProbeSessionResult Undetermined(SystemState system, int candidateCount, string reason)
    {
        system.UndeterminedReason ??= reason;
        return new UniquenessProbeSessionResult(system.Name, Enumerable.Repeat(UniquenessProbeOutcome.CouldNotDetermine, candidateCount).ToList());
    }

    private static UniquenessProbeSessionResult LatchSystem(SystemState system, int candidateCount, string reason)
    {
        system.LatchedReason = reason;
        return Undetermined(system, candidateCount, reason);
    }

    private sealed class SystemState(int id, string name)
    {
        public int Id { get; } = id;
        public string Name { get; } = name;
        public ConnectedSystem? ConnectedSystem { get; set; }
        public IConnector? Connector { get; set; }
        public IConnectorUniquenessProbe? Probe { get; set; }
        public bool Probes { get; set; }
        public string? LatchedReason { get; set; }
        public string? UndeterminedReason { get; set; }
        public int ValuesChosenWithoutProbe { get; set; }
        public Dictionary<int, AttributeState> Attributes { get; } = [];
    }

    private sealed class AttributeState(string name, string objectTypeName)
    {
        public string Name { get; } = name;
        public string ObjectTypeName { get; } = objectTypeName;
        public bool Probes { get; set; } = true;
        public IReadOnlyList<string> ControlValues { get; set; } = [];
        public string? LatchedReason { get; set; }
    }
}
