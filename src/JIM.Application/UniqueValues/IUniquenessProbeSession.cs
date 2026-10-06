// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Application.UniqueValues;

/// <summary>
/// A synchronisation run's live probe of its participating Connected Systems (Unique Value Generation, #242,
/// release 3). <see cref="UniqueValueGenerationServer"/> has no Connector access of its own, so the worker hands one
/// in through <see cref="UniqueValueResolveOptions.ProbeSession"/>; the ProbeGate asks it, last of all the gates,
/// whether each surviving candidate is already in use in a target.
/// </summary>
public interface IUniquenessProbeSession
{
    /// <summary>
    /// Asks <paramref name="target"/>'s Connected System which of <paramref name="candidates"/> (one object's, one to
    /// ten of them) it already holds for the target attribute. Never throws for a fault in the Connected System: one
    /// that cannot be probed answers <see cref="JIM.Models.Staging.UniquenessProbeOutcome.CouldNotDetermine"/>, and
    /// one whose Connector does not probe at all answers <see cref="UniquenessProbeSessionResult.NotProbed"/>.
    /// </summary>
    public Task<UniquenessProbeSessionResult> ProbeAsync(UniquenessProbeTarget target, IReadOnlyList<string> candidates);

    /// <summary>
    /// Records that a value was issued after <paramref name="connectedSystemId"/> answered
    /// <see cref="JIM.Models.Staging.UniquenessProbeOutcome.CouldNotDetermine"/> for it, so the run's warning can say
    /// how many values were chosen using JIM's own records only.
    /// </summary>
    public void RecordValueChosenWithoutProbe(int connectedSystemId);
}
