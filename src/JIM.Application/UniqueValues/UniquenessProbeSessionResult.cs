// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Staging;

namespace JIM.Application.UniqueValues;

/// <summary>
/// What a probe session's batch found (Unique Value Generation, #242, release 3): the Connected System's name, for
/// rejection text an administrator can act on, and one outcome per candidate in the batch's order.
/// </summary>
public sealed class UniquenessProbeSessionResult
{
    /// <summary>
    /// A batch the Connected System answered, or could not answer (every outcome
    /// <see cref="UniquenessProbeOutcome.CouldNotDetermine"/>).
    /// </summary>
    public UniquenessProbeSessionResult(string connectedSystemName, IReadOnlyList<UniquenessProbeOutcome> outcomes)
        : this(connectedSystemName, outcomes, isProbed: true)
    {
    }

    private UniquenessProbeSessionResult(string connectedSystemName, IReadOnlyList<UniquenessProbeOutcome> outcomes, bool isProbed)
    {
        ArgumentNullException.ThrowIfNull(connectedSystemName);
        ArgumentNullException.ThrowIfNull(outcomes);

        ConnectedSystemName = connectedSystemName;
        Outcomes = outcomes;
        IsProbed = isProbed;
    }

    /// <summary>
    /// The Connected System's name, as the ProbeGate's rejection text names it.
    /// </summary>
    public string ConnectedSystemName { get; }

    /// <summary>
    /// One outcome per candidate, in the batch's order.
    /// </summary>
    public IReadOnlyList<UniquenessProbeOutcome> Outcomes { get; }

    /// <summary>
    /// False when the system or attribute is not probed at all (see <see cref="NotProbed"/>).
    /// </summary>
    public bool IsProbed { get; }

    /// <summary>
    /// The Connected System, or the attribute, is not probed at all: its Connector declares no uniqueness probe, or
    /// the attribute is one a system-wide search cannot answer for. Every candidate is
    /// <see cref="UniquenessProbeOutcome.NotFound"/>, so the ProbeGate accepts on JIM's own records exactly as it did
    /// before release 3, and the value is not counted as chosen without a probe: there was never one to make.
    /// </summary>
    public static UniquenessProbeSessionResult NotProbed(string connectedSystemName, int candidateCount) =>
        new(connectedSystemName, Enumerable.Repeat(UniquenessProbeOutcome.NotFound, candidateCount).ToList(), isProbed: false);
}
