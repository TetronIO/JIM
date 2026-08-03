// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Staging;

namespace JIM.Models.Interfaces;

/// <summary>
/// Optional capability: a Connector that declares the phases it performs internally, so JIM can
/// show them as steps of the run rather than only narrating the current one (#454).
/// </summary>
/// <remarks>
/// <para>
/// Declaration happens before the run starts, which is what lets an administrator see what the
/// Connector still has to do. A Connector that only narrates (see the progress reporting on the
/// interaction interfaces) still works: its messages appear under the JIM phase that hosts it. This
/// interface adds the steps.
/// </para>
/// <para>
/// Phases are an expectation, not a promise. A phase that a particular run does not need (loading
/// an export file that does not exist yet) is simply never entered, and JIM records it as skipped.
/// </para>
/// </remarks>
public interface IConnectorPhases
{
    /// <summary>
    /// The phases this Connector performs for the given Connected System and Run Profile, in the
    /// order they would occur. Return an empty list when there is nothing worth showing as a step.
    /// </summary>
    /// <remarks>
    /// Called once, before the run begins. Keep it cheap and deterministic: no calls to the
    /// Connected System, and the same answer for the same configuration. The list may legitimately
    /// vary with configuration (a Connector that only merges when told to keep existing content
    /// should only declare a merge phase when that setting is on). If this throws, JIM logs it and
    /// runs with its own phases only; a declaration problem must never fail a run.
    /// </remarks>
    /// <param name="connectedSystem">The Connected System being run against, including its settings.</param>
    /// <param name="runProfile">The Run Profile being executed, including its run type.</param>
    IReadOnlyList<ConnectorPhase> GetPhases(ConnectedSystem connectedSystem, ConnectedSystemRunProfile runProfile);
}
