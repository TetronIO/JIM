// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Interfaces;

/// <summary>
/// How a Connector tells JIM what it is doing while a Run Profile executes. Handed to every
/// interaction interface, and never null, so a Connector can narrate without ceremony.
/// </summary>
/// <remarks>
/// <para>
/// JIM's own object counts cannot move while a Connector call is in flight (nothing has been
/// returned yet), so this is the only thing distinguishing a healthy long phase from a stuck run.
/// </para>
/// <para>
/// Reporting is cosmetic and JIM treats it that way: emits are serialised (safe to call from
/// parallel internal work), a failure to record one is logged and swallowed rather than failing
/// the run, and blank messages are ignored. Cancellation still propagates, so an aborting run
/// keeps unwinding.
/// </para>
/// </remarks>
public interface IConnectorProgress
{
    /// <summary>
    /// Moves to one of the phases this Connector declared through
    /// <see cref="IConnectorPhases"/>, which shows as the step now running inside the JIM phase
    /// that called the Connector.
    /// </summary>
    /// <param name="phaseKey">
    /// The Connector's own phase key, exactly as declared. A key that was never declared still
    /// shows up, appended as an extra step, rather than being dropped.
    /// </param>
    /// <param name="message">
    /// Optional narration to show alongside the step, for detail the step's name cannot carry
    /// ("Fetching User objects from Employees (page 3)..."). Omit it to show the step's name.
    /// </param>
    Task EnterPhaseAsync(string phaseKey, string? message = null);

    /// <summary>
    /// Narrates progress within the phase currently running, without moving to another one. Use it
    /// for a phase that takes a while and has something to count ("Parsed 50,000 rows...").
    /// </summary>
    /// <remarks>
    /// Emit on phase and page boundaries rather than per object: each emit is a small write, and a
    /// message nobody can read at speed is worse than one that changes every few seconds.
    /// </remarks>
    Task ReportAsync(string message);
}
