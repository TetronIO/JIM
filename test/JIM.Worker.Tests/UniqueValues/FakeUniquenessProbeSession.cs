// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.UniqueValues;
using JIM.Models.Staging;

namespace JIM.Worker.Tests.UniqueValues;

/// <summary>
/// A scripted <see cref="IUniquenessProbeSession"/> for ProbeGate tests: answers each candidate from a per-system
/// answer function, and records every batch it was asked and every value chosen without a probe.
/// </summary>
internal sealed class FakeUniquenessProbeSession : IUniquenessProbeSession
{
    private readonly Dictionary<int, (string Name, Func<string, UniquenessProbeOutcome> Answer)> _systems = [];
    private readonly HashSet<int> _notProbed = [];

    public List<(UniquenessProbeTarget Target, IReadOnlyList<string> Candidates)> Calls { get; } = [];

    public List<int> ValuesChosenWithoutProbe { get; } = [];

    /// <summary>
    /// Declares a probing system; <paramref name="taken"/> are the values it holds (case-insensitively). Every other
    /// candidate is NotFound.
    /// </summary>
    public FakeUniquenessProbeSession WithSystem(int connectedSystemId, string name, params string[] taken)
    {
        var set = new HashSet<string>(taken, StringComparer.OrdinalIgnoreCase);
        _systems[connectedSystemId] = (name, c => set.Contains(c) ? UniquenessProbeOutcome.Found : UniquenessProbeOutcome.NotFound);
        return this;
    }

    /// <summary>
    /// Declares a system that cannot be probed this run: every candidate is CouldNotDetermine.
    /// </summary>
    public FakeUniquenessProbeSession WithUndeterminedSystem(int connectedSystemId, string name)
    {
        _systems[connectedSystemId] = (name, _ => UniquenessProbeOutcome.CouldNotDetermine);
        return this;
    }

    /// <summary>
    /// Declares a system whose Connector does not probe at all.
    /// </summary>
    public FakeUniquenessProbeSession WithNotProbedSystem(int connectedSystemId, string name)
    {
        _systems[connectedSystemId] = (name, _ => UniquenessProbeOutcome.NotFound);
        _notProbed.Add(connectedSystemId);
        return this;
    }

    public Task<UniquenessProbeSessionResult> ProbeAsync(UniquenessProbeTarget target, IReadOnlyList<string> candidates)
    {
        Calls.Add((target, candidates.ToList()));
        var (name, answer) = _systems[target.ConnectedSystemId];

        return Task.FromResult(_notProbed.Contains(target.ConnectedSystemId)
            ? UniquenessProbeSessionResult.NotProbed(name, candidates.Count)
            : new UniquenessProbeSessionResult(name, candidates.Select(answer).ToList()));
    }

    public void RecordValueChosenWithoutProbe(int connectedSystemId) => ValuesChosenWithoutProbe.Add(connectedSystemId);
}
